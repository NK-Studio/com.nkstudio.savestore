using System;
using System.Collections.Generic;
using System.Threading;
using MemoryPack;
using UnityEngine;

namespace SaveStore.Storage
{
    /// <summary>
    /// 세대 blob + manifest 커밋으로 원자적 저장을 한다. 매체 입출력은 <see cref="ISaveStorage"/> 에 맡긴다.
    /// <code>
    /// manifest.bin   ← 커밋된 manifest (도메인별 유효 세대·길이·CRC·스키마)
    /// manifest.bak   ← 직전 manifest
    /// wallet.g42.bin ← 도메인 blob. 세대마다 새 이름이라 쓰는 도중 끊겨도 유효한 blob 을 덮지 않는다
    /// </code>
    /// 커밋 순서: 도메인 blob 쓰기(확정) → manifest.tmp 쓰기(확정) → manifest.bin 을 .bak 으로 → .tmp 를 .bin 으로.
    /// 로드는 .bin/.tmp/.bak 중 검증을 통과한 것 가운데 <b>세대가 가장 높은 것</b>을 현재로, 그다음을 직전으로 삼는다
    /// — 어느 단계에서 끊겼든 마지막으로 완성된 커밋을 찾는다.
    ///
    /// 스레드: <see cref="Commit"/> 은 SaveSystem 의 쓰기 스레드 하나에서만, 나머지는 쓰기가 없을 때 메인 스레드에서 부른다.
    /// </summary>
    public sealed class SaveRepository
    {
        private const string ManifestName = "manifest.bin";
        private const string ManifestBackupName = "manifest.bak";
        private const string ManifestTempName = "manifest.tmp";
        private const string DomainBlobSuffix = ".bin";
        private const string GenerationMarker = ".g";
        private const int ManifestSchemaVersion = 1;

        private readonly ISaveStorage _storage;
        private readonly IClock _clock;
        private readonly ISavePayloadTransform _transform;
        private readonly bool _acceptsUntransformedBlobs;
        // blob(헤더 + payload)을 조립하는 버퍼와 변환 결과 버퍼. 커밋 스레드 전용 — 커밋은 한 번에 하나라 공유해도 된다.
        private readonly PooledBufferWriter _blobBuffer = new PooledBufferWriter();
        private readonly PooledBufferWriter _transformBuffer = new PooledBufferWriter();
        private SaveManifest _current = new SaveManifest();
        private SaveManifest _previous;

        public SaveRepository(ISaveStorage storage, SaveOptions options = null)
        {
            _storage = storage ?? throw new ArgumentNullException(nameof(storage));
            options ??= SaveOptions.Default;
            _clock = options.Clock ?? SystemClock.Instance;
            _transform = options.PayloadTransform;
            _acceptsUntransformedBlobs = options.AcceptsUntransformedBlobs;
        }

        public ISaveStorage Storage => _storage;

        public long CommittedGeneration => Volatile.Read(ref _current).Generation;

        /// <summary>
        /// 마지막 커밋 시각(UTC unix ms). 커밋한 적이 없으면 0.
        /// </summary>
        public long CommittedAtUnixMs => Volatile.Read(ref _current).CommittedAtUnixMs;

        public bool HasPreviousManifest => _previous != null;

        public long PreviousGeneration => _previous != null ? _previous.Generation : -1;

        public ManifestLoadStatus LoadManifests()
        {
            _storage.Open();

            bool hasMain = TryReadManifest(ManifestName, out SaveManifest main, out string mainFailure);
            bool hasTemp = TryReadManifest(ManifestTempName, out SaveManifest temp, out _);
            bool hasBackup = TryReadManifest(ManifestBackupName, out SaveManifest backup, out string backupFailure);

            SaveManifest newest = null;
            ManifestLoadStatus status = ManifestLoadStatus.New;
            ConsiderNewest(hasMain, main, ManifestLoadStatus.Loaded, ref newest, ref status);
            ConsiderNewest(hasTemp, temp, ManifestLoadStatus.RecoveredFromTemp, ref newest, ref status);
            ConsiderNewest(hasBackup, backup, ManifestLoadStatus.LoadedFromBackup, ref newest, ref status);

            if (newest == null)
            {
                _current = new SaveManifest();
                _previous = null;
                if (_storage.TryRead(ManifestName, out _) == true || _storage.TryRead(ManifestBackupName, out _) == true)
                {
                    Debug.LogError($"[SaveStore] manifest 본 파일·백업이 모두 손상돼 새 세이브로 시작합니다. main={mainFailure} backup={backupFailure}");
                    return ManifestLoadStatus.CorruptedReset;
                }

                return ManifestLoadStatus.New;
            }

            _current = newest;
            _previous = PickPrevious(newest, hasMain ? main : null, hasTemp ? temp : null, hasBackup ? backup : null);

            if (status == ManifestLoadStatus.RecoveredFromTemp)
            {
                Debug.LogWarning($"[SaveStore] manifest 이름 교체가 끊긴 커밋을 복구했습니다(세대 {newest.Generation}).");
            }
            else if (status == ManifestLoadStatus.LoadedFromBackup)
            {
                Debug.LogWarning($"[SaveStore] manifest 가 손상돼 직전 세대로 되돌렸습니다: {mainFailure}");
            }

            DeleteOrphanBlobs();
            return status;
        }

        /// <summary>
        /// 지정한 manifest(현재/직전)가 가리키는 도메인 blob 을 읽어 검증한다. 대체(fallback) 판단은 호출자가 한다 —
        /// 도메인 하나만 직전 세대로 돌리면 여러 도메인에 걸친 거래가 반만 되돌아가므로, SaveSystem 이 스냅샷 단위로 고른다.
        /// </summary>
        public DomainFile ReadDomain(string key, ManifestSlot slot)
        {
            SaveManifest manifest = slot == ManifestSlot.Current ? _current : _previous;
            SaveManifestEntry entry = manifest?.Find(key);
            if (entry == null)
            {
                return DomainFile.NotFound();
            }

            string name = DomainBlobName(entry.Key, entry.FileGeneration);
            if (_storage.TryRead(name, out byte[] bytes) == false)
            {
                return DomainFile.Corrupted($"blob 없음({name})");
            }

            if (TryDecodeBlob(bytes, out int schemaVersion, out byte[] payload, out int offset, out int length, out string failure) == false)
            {
                return DomainFile.Corrupted(failure);
            }

            DomainFile file = DomainFile.Loaded(payload, offset, length, schemaVersion);
            return slot == ManifestSlot.Current ? file : file.AsBackup();
        }

        /// <summary>
        /// 바뀐 도메인 blob 과 지울 도메인 키를 한 세대로 커밋한다. <paramref name="removedKeys"/> 는 manifest 에서 빠지고,
        /// 그 blob 은 세대가 밀려날 때 정리된다(직전 세대 롤백에는 남아 있다).
        /// </summary>
        public void Commit(IReadOnlyList<DomainPayload> payloads, IReadOnlyList<string> removedKeys = null)
        {
            int payloadCount = payloads != null ? payloads.Count : 0;
            int removedCount = removedKeys != null ? removedKeys.Count : 0;
            if (payloadCount == 0 && removedCount == 0)
            {
                return;
            }

            SaveManifest current = Volatile.Read(ref _current);
            SaveManifest previous = _previous;
            SaveManifest next = current.CloneShallow();
            next.Generation = Math.Max(current.Generation, previous != null ? previous.Generation : 0) + 1;
            next.CommittedAtUnixMs = _clock.UtcNowUnixMs;

            for (int i = 0; i < removedCount; i++)
            {
                string removedKey = removedKeys[i];
                next.Entries.RemoveAll(entry => entry.Key == removedKey);
            }

            for (int i = 0; i < payloadCount; i++)
            {
                DomainPayload payload = payloads[i];
                ReadOnlySpan<byte> span = new ReadOnlySpan<byte>(payload.Buffer, 0, payload.Length);
                WriteBlob(DomainBlobName(payload.Key, next.Generation), span, payload.SchemaVersion);

                SaveManifestEntry entry = next.Find(payload.Key);
                if (entry == null)
                {
                    entry = new SaveManifestEntry { Key = payload.Key };
                    next.Entries.Add(entry);
                }

                entry.FileGeneration = next.Generation;
                entry.SchemaVersion = payload.SchemaVersion;
                entry.PayloadLength = payload.Length;
                entry.Checksum = Crc32.Compute(span);
            }

            WriteBlob(ManifestTempName, MemoryPackSerializer.Serialize(next), ManifestSchemaVersion);
            _storage.Delete(ManifestBackupName);
            if (_storage.TryRead(ManifestName, out _) == true)
            {
                _storage.Rename(ManifestName, ManifestBackupName);
            }

            _storage.Rename(ManifestTempName, ManifestName);

            _previous = current;
            Volatile.Write(ref _current, next);

            // 세대 하나가 밀려나면서 아무도 가리키지 않게 된 blob 만 지운다 — 매 커밋마다 저장소 전체를 나열하지 않는다.
            DeleteDroppedBlobs(previous, next, current);
        }

        /// <summary>
        /// 저장소를 고치지 않고 마지막으로 완성된 커밋의 세대·시각만 읽는다 — 슬롯 목록 화면용.
        /// 커밋이 없거나 manifest 를 읽을 수 없으면 false.
        /// </summary>
        public bool TryPeekLatestCommit(out long generation, out long committedAtUnixMs)
        {
            SaveManifest newest = PeekLatestManifest();
            generation = newest != null ? newest.Generation : 0;
            committedAtUnixMs = newest != null ? newest.CommittedAtUnixMs : 0;
            return newest != null;
        }

        /// <summary>
        /// 저장소를 고치지 않고(고아 정리·로그 없음) 마지막으로 완성된 manifest 를 읽는다. 세이브 뷰어·도구용. 없으면 null.
        /// </summary>
        public SaveManifest PeekLatestManifest()
        {
            SaveManifest newest = null;
            string[] names = { ManifestName, ManifestTempName, ManifestBackupName };
            for (int i = 0; i < names.Length; i++)
            {
                if (TryReadManifest(names[i], out SaveManifest manifest, out _) == true && (newest == null || manifest.Generation > newest.Generation))
                {
                    newest = manifest;
                }
            }

            return newest;
        }

        /// <summary>
        /// manifest 항목이 가리키는 도메인 blob 을 읽어 검증·복원한다(상태 없음). 세이브 뷰어·골든 파일 도구용.
        /// </summary>
        public DomainFile ReadDomain(SaveManifestEntry entry)
        {
            if (entry == null)
            {
                return DomainFile.NotFound();
            }

            string name = DomainBlobName(entry.Key, entry.FileGeneration);
            if (_storage.TryRead(name, out byte[] bytes) == false)
            {
                return DomainFile.Corrupted($"blob 없음({name})");
            }

            return TryDecodeBlob(bytes, out int schemaVersion, out byte[] payload, out int offset, out int length, out string failure) == true
                ? DomainFile.Loaded(payload, offset, length, schemaVersion)
                : DomainFile.Corrupted(failure);
        }

        public IReadOnlyList<string> ListBlobs()
        {
            return _storage.List();
        }

        /// <summary>
        /// 현재 manifest 가 가리키는 도메인 blob 이름. 테스트·디버그 도구(손상 재현 등)용.
        /// </summary>
        public string GetCurrentDomainBlobName(string key)
        {
            SaveManifestEntry entry = _current.Find(key);
            return entry == null ? null : DomainBlobName(entry.Key, entry.FileGeneration);
        }

        private static void ConsiderNewest(bool isValid, SaveManifest candidate, ManifestLoadStatus candidateStatus, ref SaveManifest newest, ref ManifestLoadStatus status)
        {
            if (isValid == false || (newest != null && candidate.Generation <= newest.Generation))
            {
                return;
            }

            newest = candidate;
            status = candidateStatus;
        }

        private static SaveManifest PickPrevious(SaveManifest current, params SaveManifest[] candidates)
        {
            SaveManifest previous = null;
            for (int i = 0; i < candidates.Length; i++)
            {
                SaveManifest candidate = candidates[i];
                if (candidate == null || candidate.Generation >= current.Generation)
                {
                    continue;
                }

                if (previous == null || candidate.Generation > previous.Generation)
                {
                    previous = candidate;
                }
            }

            return previous;
        }

        private bool TryReadManifest(string name, out SaveManifest manifest, out string failureReason)
        {
            manifest = null;
            if (_storage.TryRead(name, out byte[] bytes) == false)
            {
                failureReason = "없음";
                return false;
            }

            if (TryDecodeBlob(bytes, out _, out byte[] payload, out int offset, out int length, out failureReason) == false)
            {
                return false;
            }

            try
            {
                manifest = MemoryPackSerializer.Deserialize<SaveManifest>(new ReadOnlySpan<byte>(payload, offset, length));
            }
            catch (MemoryPackSerializationException exception)
            {
                failureReason = exception.Message;
                return false;
            }

            if (manifest == null)
            {
                failureReason = "null manifest";
                return false;
            }

            return true;
        }

        private void WriteBlob(string name, ReadOnlySpan<byte> payload, int schemaVersion)
        {
            _blobBuffer.Clear();
            if (_transform == null)
            {
                SaveFileFormat.Write(_blobBuffer, payload, schemaVersion);
            }
            else
            {
                _transformBuffer.Clear();
                _transform.Encode(payload, _transformBuffer);
                SaveFileFormat.Write(_blobBuffer, _transformBuffer.WrittenSpan, schemaVersion, _transform.Id);
            }

            _storage.Write(name, _blobBuffer.WrittenSpan);
        }

        /// <summary>
        /// 헤더·CRC 검증 후 변환을 되돌린다. payload 는 <paramref name="payloadBytes"/>[offset..offset+length].
        /// </summary>
        private bool TryDecodeBlob(byte[] blob, out int schemaVersion, out byte[] payloadBytes, out int offset, out int length, out string failureReason)
        {
            payloadBytes = blob;
            if (SaveFileFormat.TryRead(blob, out schemaVersion, out ushort transformId, out offset, out length, out failureReason) == false)
            {
                return false;
            }

            if (transformId == 0)
            {
                if (_transform != null && _acceptsUntransformedBlobs == false)
                {
                    failureReason = "변환(암호화·서명) 없이 저장된 blob 입니다 — AcceptsUntransformedBlobs 가 꺼져 있어 거부합니다.";
                    return false;
                }

                return true;
            }

            if (_transform == null || _transform.Id != transformId)
            {
                failureReason = $"알 수 없는 변환 Id({transformId})입니다 — 이 앱에 설정된 변환과 다릅니다.";
                return false;
            }

            var decoded = new PooledBufferWriter(Math.Max(256, length));
            try
            {
                if (_transform.TryDecode(new ReadOnlySpan<byte>(blob, offset, length), decoded, out failureReason) == false)
                {
                    return false;
                }

                payloadBytes = decoded.WrittenSpan.ToArray();
                offset = 0;
                length = payloadBytes.Length;
                return true;
            }
            finally
            {
                decoded.Dispose();
            }
        }

        private void DeleteDroppedBlobs(SaveManifest dropped, SaveManifest current, SaveManifest previous)
        {
            if (dropped == null)
            {
                return;
            }

            for (int i = 0; i < dropped.Entries.Count; i++)
            {
                SaveManifestEntry entry = dropped.Entries[i];
                if (IsReferenced(entry, current) == true || IsReferenced(entry, previous) == true)
                {
                    continue;
                }

                _storage.Delete(DomainBlobName(entry.Key, entry.FileGeneration));
            }
        }

        /// <summary>
        /// 로드 때 한 번 — 커밋 도중 끊겨 manifest 에 실리지 못한 blob 을 치운다.
        /// </summary>
        private void DeleteOrphanBlobs()
        {
            IReadOnlyList<string> names = _storage.List();
            for (int i = 0; i < names.Count; i++)
            {
                string name = names[i];
                if (IsDomainBlobName(name) == false || IsReferencedName(name, _current) == true || IsReferencedName(name, _previous) == true)
                {
                    continue;
                }

                _storage.Delete(name);
            }
        }

        private static bool IsReferenced(SaveManifestEntry entry, SaveManifest manifest)
        {
            SaveManifestEntry other = manifest?.Find(entry.Key);
            return other != null && other.FileGeneration == entry.FileGeneration;
        }

        private static bool IsReferencedName(string name, SaveManifest manifest)
        {
            if (manifest == null)
            {
                return false;
            }

            for (int i = 0; i < manifest.Entries.Count; i++)
            {
                SaveManifestEntry entry = manifest.Entries[i];
                if (string.Equals(name, DomainBlobName(entry.Key, entry.FileGeneration), StringComparison.Ordinal) == true)
                {
                    return true;
                }
            }

            return false;
        }

        private static bool IsDomainBlobName(string name)
        {
            return name.EndsWith(DomainBlobSuffix, StringComparison.Ordinal) == true &&
                   name.IndexOf(GenerationMarker, StringComparison.Ordinal) > 0 &&
                   string.Equals(name, ManifestName, StringComparison.Ordinal) == false;
        }

        private static string DomainBlobName(string key, long generation)
        {
            return $"{key}{GenerationMarker}{generation}{DomainBlobSuffix}";
        }
    }

    public enum ManifestSlot
    {
        Current,
        Previous,
    }

    public enum ManifestLoadStatus
    {
        New,
        Loaded,
        RecoveredFromTemp,
        LoadedFromBackup,
        CorruptedReset,
    }

    public enum DomainFileStatus
    {
        NotFound,
        Loaded,
        LoadedFromBackup,
        Corrupted,
    }

    /// <summary>
    /// 검증을 통과한 도메인 blob. payload 는 <see cref="Bytes"/>[Offset..Offset+Length].
    /// </summary>
    public readonly struct DomainFile
    {
        public readonly DomainFileStatus Status;
        public readonly byte[] Bytes;
        public readonly int Offset;
        public readonly int Length;
        public readonly int SchemaVersion;
        public readonly string FailureReason;

        private DomainFile(DomainFileStatus status, byte[] bytes, int offset, int length, int schemaVersion, string failureReason)
        {
            Status = status;
            Bytes = bytes;
            Offset = offset;
            Length = length;
            SchemaVersion = schemaVersion;
            FailureReason = failureReason;
        }

        public ReadOnlySpan<byte> Payload => new ReadOnlySpan<byte>(Bytes, Offset, Length);

        public static DomainFile NotFound() => new DomainFile(DomainFileStatus.NotFound, null, 0, 0, 0, null);

        public static DomainFile Corrupted(string reason) => new DomainFile(DomainFileStatus.Corrupted, null, 0, 0, 0, reason);

        public static DomainFile Loaded(byte[] bytes, int offset, int length, int schemaVersion)
            => new DomainFile(DomainFileStatus.Loaded, bytes, offset, length, schemaVersion, null);

        public DomainFile AsBackup() => new DomainFile(DomainFileStatus.LoadedFromBackup, Bytes, Offset, Length, SchemaVersion, null);
    }

    /// <summary>
    /// 쓰기 스레드로 넘기는 직렬화 결과. <see cref="Buffer"/> 는 ArrayPool 에서 빌린 배열이며 커밋 후 반납된다.
    /// </summary>
    public readonly struct DomainPayload
    {
        public readonly string Key;
        public readonly byte[] Buffer;
        public readonly int Length;
        public readonly int SchemaVersion;

        public DomainPayload(string key, byte[] buffer, int length, int schemaVersion)
        {
            Key = key;
            Buffer = buffer;
            Length = length;
            SchemaVersion = schemaVersion;
        }
    }
}
