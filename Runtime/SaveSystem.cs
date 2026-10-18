using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using SaveStore.Storage;
using UnityEngine;

namespace SaveStore
{
    /// <summary>
    /// 세이브 진입점. 도메인 등록 → <see cref="Load"/> → 게임 진행 중 체크포인트마다 <see cref="WriteDirty"/>.
    ///
    /// 직렬화는 호출한 스레드(메인)에서 재사용 버퍼에 한다 — 그래야 한 시점의 일관된 스냅샷이 된다.
    /// 파일 쓰기는 쓰기 스레드 하나에 순서대로 넘긴다(옵션). 거대한 string 이 생기는 단계가 없다.
    /// 언제 쓸지는 이 클래스가 정하지 않는다 — <see cref="SaveScheduler"/> 같은 체크포인트 정책이 부른다.
    ///
    /// 스레드: 모든 공개 API 는 메인 스레드 전용이다. 이벤트(<see cref="OnCommitted"/>, <see cref="OnWriteFailed"/>)도
    /// 메인 스레드에서 <see cref="WriteDirty"/>·<see cref="Flush"/>·<see cref="PumpEvents"/> 때 전달된다.
    /// </summary>
    public sealed class SaveSystem : IDisposable
    {
        // 파일 이름에 그대로 쓰이므로 플랫폼마다 안전한 문자만 허용한다.
        private static readonly Regex KeyPattern = new Regex("^[a-z0-9_-]{1,32}$", RegexOptions.Compiled);

        private readonly SaveRepository _repository;
        private readonly List<SaveDomain> _domains;
        private readonly bool _isBackgroundWriteEnabled;
        private readonly PooledBufferWriter _serializeBuffer = new PooledBufferWriter();
        private readonly ConcurrentQueue<WriteFailure> _failures = new ConcurrentQueue<WriteFailure>();
        private readonly ConcurrentQueue<SaveCommitInfo> _commits = new ConcurrentQueue<SaveCommitInfo>();
        private readonly HashSet<string> _pendingRemovals = new HashSet<string>(StringComparer.Ordinal);
        private readonly List<DomainPayload> _pendingRawPayloads = new List<DomainPayload>();
        private readonly object _queueGate = new object();
        private Task _writeTail = Task.CompletedTask;
        private SaveTransaction _activeTransaction;
        private bool _isLoaded;

        /// <summary>
        /// 쓰기 실패(디스크 가득 참 등). 실패한 도메인은 다시 dirty 가 돼 다음 체크포인트에 재시도된다.
        /// </summary>
        public event Action<Exception> OnWriteFailed;

        /// <summary>
        /// 커밋 완료. 클라우드 차등 업로드 훅이다 — <see cref="SaveCommitInfo.Domains"/> 에 이번 세대에 바뀐 도메인의 payload(평문 MemoryPack)가 들어 있다.
        /// 구독자가 있을 때만 payload 사본을 만든다(구독하지 않으면 추가 할당 없음).
        /// </summary>
        public event Action<SaveCommitInfo> OnCommitted;

        /// <summary>
        /// <paramref name="storage"/> 는 세이브가 놓일 매체다 — 로컬 파일은 <see cref="FileSaveStorage"/>,
        /// 테스트·게스트 모드는 <see cref="MemorySaveStorage"/>, 그 밖의 플랫폼은 <see cref="ISaveStorage"/> 를 구현해 넘긴다.
        /// </summary>
        public SaveSystem(ISaveStorage storage, IEnumerable<SaveDomain> domains, SaveOptions options = null)
            : this(new SaveRepository(storage, options), domains, options)
        {
        }

        /// <summary>
        /// 리포지토리를 직접 넘길 때. 시계·변환은 리포지토리 쪽 옵션이 쓰이고, 여기 옵션은 쓰기 스레드 여부에만 쓰인다.
        /// </summary>
        public SaveSystem(SaveRepository repository, IEnumerable<SaveDomain> domains, SaveOptions options = null)
        {
            _repository = repository ?? throw new ArgumentNullException(nameof(repository));
            _domains = new List<SaveDomain>(domains ?? throw new ArgumentNullException(nameof(domains)));
            options ??= SaveOptions.Default;
            // WebGL 은 스레드가 없다 — 그 경우 같은 스레드에서 바로 쓴다.
            _isBackgroundWriteEnabled = options.IsBackgroundWriteEnabled == true && Application.platform != RuntimePlatform.WebGLPlayer;

            var seenKeys = new HashSet<string>(StringComparer.Ordinal);
            for (int i = 0; i < _domains.Count; i++)
            {
                ValidateKey(_domains[i].Key);
                if (seenKeys.Add(_domains[i].Key) == false)
                {
                    throw new ArgumentException($"[SaveStore] 도메인 키 '{_domains[i].Key}' 가 중복됐습니다.");
                }
            }
        }

        public SaveRepository Repository => _repository;

        public IReadOnlyList<SaveDomain> Domains => _domains;

        public bool IsLoaded => _isLoaded;

        public bool IsInTransaction => _activeTransaction != null;

        public bool HasDirtyDomains
        {
            get
            {
                if (_pendingRemovals.Count > 0 || _pendingRawPayloads.Count > 0)
                {
                    return true;
                }

                for (int i = 0; i < _domains.Count; i++)
                {
                    if (_domains[i].IsDirty == true)
                    {
                        return true;
                    }
                }

                return false;
            }
        }

        // ---- 로드 ----

        /// <summary>
        /// manifest 와 등록된 모든 도메인을 읽는다. 파일이 없는 도메인은 기본값, 복구 불가로 손상된 도메인은 기본값 + 오류 로그.
        /// </summary>
        public SaveLoadReport Load()
        {
            ThrowIfInTransaction();
            Flush();
            _pendingRemovals.Clear();
            ReleasePendingRawPayloads();

            var report = new SaveLoadReport { ManifestStatus = _repository.LoadManifests() };
            DomainFile[] files = ReadSnapshot(ManifestSlot.Current);
            if (ContainsCorrupted(files) == true)
            {
                files = ChooseFallbackSnapshot(files, report);
            }

            for (int i = 0; i < _domains.Count; i++)
            {
                SaveDomain domain = _domains[i];
                DomainLoadStatus status = LoadDomain(domain, files[i]);
                report.Domains.Add(new DomainLoadResult(domain.Key, status));
            }

            // 직전 세대로 되돌렸다면 현재 manifest 는 여전히 손상 세대를 가리킨다 — 다음 커밋에서 전 도메인을 메모리 상태로 다시 써 정합을 맞춘다.
            if (report.IsRolledBack == true)
            {
                for (int i = 0; i < _domains.Count; i++)
                {
                    _domains[i].MarkDirtyForRetry();
                }
            }

            _isLoaded = true;
            return report;
        }

        // ---- 동적 도메인 ----

        /// <summary>
        /// 도메인을 나중에 추가한다(맵 청크·이벤트 도메인 등). <see cref="Load"/> 뒤라면 그 자리에서 현재 세대의 데이터를 읽는다.
        /// 나중에 등록한 도메인은 단독으로 읽히므로 손상 시 그 도메인만 직전 세대로 대체된다(스냅샷 롤백 대상 아님).
        /// </summary>
        public DomainLoadStatus Register(SaveDomain domain)
        {
            if (domain == null)
            {
                throw new ArgumentNullException(nameof(domain));
            }

            ValidateKey(domain.Key);
            if (FindDomain(domain.Key) != null)
            {
                throw new ArgumentException($"[SaveStore] 도메인 키 '{domain.Key}' 가 이미 등록돼 있습니다.");
            }

            if (_pendingRemovals.Contains(domain.Key) == true)
            {
                throw new InvalidOperationException($"[SaveStore] '{domain.Key}' 는 지우기(Forget)가 예약돼 있습니다 — 커밋 후 다시 등록하세요.");
            }

            _domains.Add(domain);
            if (_isLoaded == false)
            {
                return DomainLoadStatus.CreatedDefault;
            }

            DomainFile file = _repository.ReadDomain(domain.Key, ManifestSlot.Current);
            if (file.Status == DomainFileStatus.Corrupted && _repository.HasPreviousManifest == true)
            {
                DomainFile previous = _repository.ReadDomain(domain.Key, ManifestSlot.Previous);
                if (previous.Status != DomainFileStatus.Corrupted)
                {
                    Debug.LogWarning($"[SaveStore] '{domain.Key}' 현재 세대가 손상돼 직전 세대로 읽었습니다: {file.FailureReason}");
                    DomainLoadStatus backupStatus = LoadDomain(domain, previous);
                    domain.MarkDirtyForRetry();
                    return backupStatus;
                }
            }

            return LoadDomain(domain, file);
        }

        /// <summary>
        /// 도메인을 뺀다. 저장된 데이터는 남는다(다시 Register 하면 읽힌다). 지우려면 이어서 <see cref="Forget"/>.
        /// </summary>
        public void Unregister(SaveDomain domain, bool isSavingIfDirty = true)
        {
            ThrowIfInTransaction();
            if (domain == null || _domains.Contains(domain) == false)
            {
                return;
            }

            if (_isLoaded == true && isSavingIfDirty == true && domain.IsDirty == true)
            {
                WriteDomains(candidate => ReferenceEquals(candidate, domain));
            }

            _domains.Remove(domain);
        }

        /// <summary>
        /// 등록되지 않은 도메인의 저장 데이터를 다음 커밋에서 지운다(파괴된 청크·끝난 이벤트 등).
        /// </summary>
        public void Forget(string key)
        {
            ValidateKey(key);
            if (FindDomain(key) != null)
            {
                throw new InvalidOperationException($"[SaveStore] '{key}' 는 등록돼 있습니다 — Unregister 후 Forget 하세요.");
            }

            _pendingRemovals.Add(key);
        }

        // ---- 저장 ----

        /// <summary>
        /// dirty 도메인만 직렬화해 쓰기를 예약한다. 반환값은 쓴 도메인 수.
        /// 트랜잭션 중에는 아무것도 쓰지 않는다(0) — 반쯤 진행된 상태가 디스크에 남지 않게, dirty 는 남겨 두고 트랜잭션이 끝난 뒤 체크포인트에 쓴다.
        /// </summary>
        public int WriteDirty()
        {
            ThrowIfNotLoaded();
            DeliverEvents();
            if (_activeTransaction != null)
            {
                return 0;
            }

            return WriteDomains(domain => domain.IsDirty);
        }

        /// <summary>
        /// 예약된 쓰기가 끝날 때까지 기다린다. 앱이 백그라운드로 갈 때·중요 거래 직후에 부른다.
        /// </summary>
        public void Flush()
        {
            Task tail;
            lock (_queueGate)
            {
                tail = _writeTail;
            }

            tail.Wait();
            DeliverEvents();
        }

        /// <summary>
        /// 중요 거래(결제·보상·재화 차감) 직후 — 지금 쓰고 끝날 때까지 기다린다.
        /// </summary>
        public void SaveNow()
        {
            WriteDirty();
            Flush();
        }

        /// <summary>
        /// 쓰기 스레드에서 끝난 커밋·실패 이벤트를 메인 스레드로 전달한다. <see cref="SaveScheduler"/> 가 매 프레임 부른다.
        /// </summary>
        public void PumpEvents()
        {
            DeliverEvents();
        }

        // ---- 트랜잭션 ----

        /// <summary>
        /// 여러 도메인을 고치는 행동을 묶는다. <see cref="SaveTransaction.Complete"/> 없이 Dispose 되면(예외·중도 실패) 메모리 상태를 시작 시점으로 되돌린다.
        /// 도메인을 지정하지 않으면 등록된 전부가 대상이다. 중첩은 지원하지 않는다.
        /// <code>
        /// using (var tx = saveSystem.BeginTransaction(wallet, inventory))
        /// {
        ///     if (wallet.TrySpendGems(2) == false) return;   // Complete 없이 끝나면 되돌린다
        ///     inventory.AddPotions(3);
        ///     tx.Complete();
        /// }
        /// scheduler.RequestCritical();
        /// </code>
        /// </summary>
        public SaveTransaction BeginTransaction(params SaveDomain[] domains)
        {
            ThrowIfNotLoaded();
            if (_activeTransaction != null)
            {
                throw new InvalidOperationException("[SaveStore] 트랜잭션은 중첩할 수 없습니다.");
            }

            SaveDomain[] targets = domains == null || domains.Length == 0 ? _domains.ToArray() : domains;
            for (int i = 0; i < targets.Length; i++)
            {
                if (_domains.Contains(targets[i]) == false)
                {
                    throw new ArgumentException($"[SaveStore] '{targets[i]?.Key}' 는 이 SaveSystem 에 등록되지 않은 도메인입니다.");
                }
            }

            _activeTransaction = new SaveTransaction(this, targets);
            return _activeTransaction;
        }

        internal void EndTransaction(SaveTransaction transaction)
        {
            if (ReferenceEquals(_activeTransaction, transaction) == true)
            {
                _activeTransaction = null;
            }
        }

        // ---- 스냅샷(클라우드 백업·복원) ----

        /// <summary>
        /// 등록된 모든 도메인의 현재 메모리 상태(저장 전 변경 포함)를 스냅샷으로 만든다. 클라우드 전체 업로드용.
        /// </summary>
        public SaveSnapshot CaptureSnapshot(IClock clock = null)
        {
            ThrowIfNotLoaded();
            var snapshot = new SaveSnapshot
            {
                BaseGeneration = _repository.CommittedGeneration,
                CapturedAtUnixMs = (clock ?? SystemClock.Instance).UtcNowUnixMs,
            };

            for (int i = 0; i < _domains.Count; i++)
            {
                SaveDomain domain = _domains[i];
                _serializeBuffer.Clear();
                domain.WriteTo(_serializeBuffer);
                snapshot.Entries.Add(new SaveSnapshotEntry
                {
                    Key = domain.Key,
                    SchemaVersion = domain.SchemaVersion,
                    Payload = _serializeBuffer.WrittenSpan.ToArray(),
                });
            }

            return snapshot;
        }

        /// <summary>
        /// 스냅샷(클라우드에서 받은 세이브 등)으로 메모리 상태를 바꾼다. 스냅샷에 없는 등록 도메인은 기본값이 되고,
        /// 등록되지 않은 도메인 항목은 그대로 다음 커밋에 기록된다. 디스크에는 다음 커밋에 반영된다 — 보통 이어서 <see cref="SaveNow"/>.
        /// 어느 쪽을 남길지(로컬 vs 클라우드) 판단은 호출자 몫이다 — <see cref="SaveSnapshot.CapturedAtUnixMs"/>·<see cref="SaveRepository.CommittedAtUnixMs"/> 로 비교한다.
        /// </summary>
        public SaveLoadReport ApplySnapshot(SaveSnapshot snapshot)
        {
            if (snapshot == null)
            {
                throw new ArgumentNullException(nameof(snapshot));
            }

            ThrowIfNotLoaded();
            ThrowIfInTransaction();
            Flush();

            var report = new SaveLoadReport { ManifestStatus = ManifestLoadStatus.Loaded };
            var remaining = new Dictionary<string, SaveSnapshotEntry>(StringComparer.Ordinal);
            for (int i = 0; i < snapshot.Entries.Count; i++)
            {
                ValidateKey(snapshot.Entries[i].Key);
                remaining[snapshot.Entries[i].Key] = snapshot.Entries[i];
            }

            for (int i = 0; i < _domains.Count; i++)
            {
                SaveDomain domain = _domains[i];
                if (remaining.TryGetValue(domain.Key, out SaveSnapshotEntry entry) == true)
                {
                    domain.ReadFrom(entry.Payload, entry.SchemaVersion);
                    remaining.Remove(domain.Key);
                    report.Domains.Add(new DomainLoadResult(domain.Key, DomainLoadStatus.Loaded));
                }
                else
                {
                    domain.ResetToDefault();
                    report.Domains.Add(new DomainLoadResult(domain.Key, DomainLoadStatus.CreatedDefault));
                }

                domain.MarkDirtyForRetry();
            }

            foreach (SaveSnapshotEntry entry in remaining.Values)
            {
                byte[] copy = ArrayPool<byte>.Shared.Rent(entry.Payload.Length);
                Buffer.BlockCopy(entry.Payload, 0, copy, 0, entry.Payload.Length);
                _pendingRawPayloads.Add(new DomainPayload(entry.Key, copy, entry.Payload.Length, entry.SchemaVersion));
                _pendingRemovals.Remove(entry.Key);
            }

            return report;
        }

        public void Dispose()
        {
            if (_isLoaded == true && _activeTransaction == null)
            {
                SaveNow();
            }

            ReleasePendingRawPayloads();
            _serializeBuffer.Dispose();
        }

        // ---- 내부 ----

        private int WriteDomains(Predicate<SaveDomain> shouldWrite)
        {
            List<DomainPayload> payloads = null;
            bool isCapturingCommits = OnCommitted != null;
            List<CommittedDomain> committedDomains = isCapturingCommits == true ? new List<CommittedDomain>() : null;

            for (int i = 0; i < _domains.Count; i++)
            {
                SaveDomain domain = _domains[i];
                if (shouldWrite(domain) == false)
                {
                    continue;
                }

                _serializeBuffer.Clear();
                domain.WriteTo(_serializeBuffer);

                // 버퍼는 다음 도메인 직렬화에 재사용하므로 쓰기 스레드에는 풀에서 빌린 사본을 넘긴다.
                byte[] copy = ArrayPool<byte>.Shared.Rent(_serializeBuffer.WrittenCount);
                _serializeBuffer.WrittenSpan.CopyTo(copy);
                payloads ??= new List<DomainPayload>(_domains.Count);
                payloads.Add(new DomainPayload(domain.Key, copy, _serializeBuffer.WrittenCount, domain.SchemaVersion));
                committedDomains?.Add(new CommittedDomain(domain.Key, domain.SchemaVersion, _serializeBuffer.WrittenSpan.ToArray()));
                domain.ClearDirty();
            }

            for (int i = 0; i < _pendingRawPayloads.Count; i++)
            {
                DomainPayload raw = _pendingRawPayloads[i];
                payloads ??= new List<DomainPayload>();
                payloads.Add(raw);
                committedDomains?.Add(new CommittedDomain(raw.Key, raw.SchemaVersion, new ReadOnlySpan<byte>(raw.Buffer, 0, raw.Length).ToArray()));
            }

            _pendingRawPayloads.Clear();
            List<string> removals = _pendingRemovals.Count > 0 ? new List<string>(_pendingRemovals) : null;
            _pendingRemovals.Clear();

            if (payloads == null && removals == null)
            {
                return 0;
            }

            var job = new CommitJob(payloads ?? new List<DomainPayload>(), removals, committedDomains);
            if (_isBackgroundWriteEnabled == true)
            {
                lock (_queueGate)
                {
                    _writeTail = _writeTail.ContinueWith(_ => CommitAndRelease(job), TaskScheduler.Default);
                }
            }
            else
            {
                CommitAndRelease(job);
            }

            return job.Payloads.Count;
        }

        private void CommitAndRelease(CommitJob job)
        {
            try
            {
                _repository.Commit(job.Payloads, job.Removals);
                if (job.CommittedDomains != null)
                {
                    _commits.Enqueue(new SaveCommitInfo(_repository.CommittedGeneration, _repository.CommittedAtUnixMs, job.CommittedDomains, job.Removals));
                }
            }
            catch (Exception exception)
            {
                _failures.Enqueue(new WriteFailure(job, exception));
            }
            finally
            {
                for (int i = 0; i < job.Payloads.Count; i++)
                {
                    ArrayPool<byte>.Shared.Return(job.Payloads[i].Buffer);
                }
            }
        }

        private void DeliverEvents()
        {
            while (_failures.TryDequeue(out WriteFailure failure) == true)
            {
                for (int i = 0; i < failure.Job.Payloads.Count; i++)
                {
                    FindDomain(failure.Job.Payloads[i].Key)?.MarkDirtyForRetry();
                }

                if (failure.Job.Removals != null)
                {
                    for (int i = 0; i < failure.Job.Removals.Count; i++)
                    {
                        _pendingRemovals.Add(failure.Job.Removals[i]);
                    }
                }

                Debug.LogError($"[SaveStore] 세이브 쓰기 실패 — 다음 체크포인트에 재시도합니다: {failure.Exception}");
                OnWriteFailed?.Invoke(failure.Exception);
            }

            while (_commits.TryDequeue(out SaveCommitInfo info) == true)
            {
                OnCommitted?.Invoke(info);
            }
        }

        private DomainFile[] ReadSnapshot(ManifestSlot slot)
        {
            var files = new DomainFile[_domains.Count];
            for (int i = 0; i < _domains.Count; i++)
            {
                files[i] = _repository.ReadDomain(_domains[i].Key, slot);
            }

            return files;
        }

        /// <summary>
        /// 현재 세대에 손상이 있을 때. 직전 세대 전체가 온전하면 스냅샷째로 되돌린다 — 도메인마다 따로 되돌리면
        /// "재화 지급 + 아이템 지급" 같은 거래가 반만 남는다. 직전 세대도 깨졌으면 그때만 도메인 단위로 최선을 다한다.
        /// </summary>
        private DomainFile[] ChooseFallbackSnapshot(DomainFile[] currentFiles, SaveLoadReport report)
        {
            if (_repository.HasPreviousManifest == false)
            {
                return currentFiles;
            }

            DomainFile[] previousFiles = ReadSnapshot(ManifestSlot.Previous);
            if (ContainsCorrupted(previousFiles) == false)
            {
                Debug.LogWarning($"[SaveStore] 현재 세대에 손상된 파일이 있어 직전 세대({_repository.PreviousGeneration}) 전체로 되돌렸습니다: {DescribeCorrupted(currentFiles)}");
                report.IsRolledBack = true;
                return previousFiles;
            }

            var mixed = new DomainFile[currentFiles.Length];
            for (int i = 0; i < currentFiles.Length; i++)
            {
                mixed[i] = currentFiles[i].Status == DomainFileStatus.Corrupted && previousFiles[i].Status != DomainFileStatus.Corrupted
                    ? previousFiles[i]
                    : currentFiles[i];
            }

            Debug.LogError($"[SaveStore] 현재·직전 세대 모두 손상 — 도메인 단위로 복구합니다(도메인 간 정합이 깨질 수 있음): {DescribeCorrupted(currentFiles)}");
            return mixed;
        }

        private static bool ContainsCorrupted(DomainFile[] files)
        {
            for (int i = 0; i < files.Length; i++)
            {
                if (files[i].Status == DomainFileStatus.Corrupted)
                {
                    return true;
                }
            }

            return false;
        }

        private string DescribeCorrupted(DomainFile[] files)
        {
            var parts = new List<string>();
            for (int i = 0; i < files.Length; i++)
            {
                if (files[i].Status == DomainFileStatus.Corrupted)
                {
                    parts.Add($"{_domains[i].Key}({files[i].FailureReason})");
                }
            }

            return string.Join(", ", parts);
        }

        private static DomainLoadStatus LoadDomain(SaveDomain domain, DomainFile file)
        {
            if (file.Status == DomainFileStatus.NotFound)
            {
                domain.ResetToDefault();
                return DomainLoadStatus.CreatedDefault;
            }

            if (file.Status == DomainFileStatus.Corrupted)
            {
                Debug.LogError($"[SaveStore] '{domain.Key}' 세이브가 손상됐고 백업도 없어 기본값으로 초기화합니다: {file.FailureReason}");
                domain.ResetToDefault();
                domain.MarkDirtyForRetry();
                return DomainLoadStatus.ResetCorrupted;
            }

            domain.ReadFrom(file.Payload, file.SchemaVersion);
            return file.Status == DomainFileStatus.LoadedFromBackup ? DomainLoadStatus.LoadedFromBackup : DomainLoadStatus.Loaded;
        }

        private void ReleasePendingRawPayloads()
        {
            for (int i = 0; i < _pendingRawPayloads.Count; i++)
            {
                ArrayPool<byte>.Shared.Return(_pendingRawPayloads[i].Buffer);
            }

            _pendingRawPayloads.Clear();
        }

        private SaveDomain FindDomain(string key)
        {
            for (int i = 0; i < _domains.Count; i++)
            {
                if (_domains[i].Key == key)
                {
                    return _domains[i];
                }
            }

            return null;
        }

        private void ThrowIfNotLoaded()
        {
            if (_isLoaded == false)
            {
                throw new InvalidOperationException("[SaveStore] Load() 전에 저장할 수 없습니다 — 빈 기본값으로 기존 세이브를 덮어쓰게 됩니다.");
            }
        }

        private void ThrowIfInTransaction()
        {
            if (_activeTransaction != null)
            {
                throw new InvalidOperationException("[SaveStore] 트랜잭션 중에는 할 수 없습니다 — 먼저 Complete 하거나 Dispose 하세요.");
            }
        }

        private static void ValidateKey(string key)
        {
            if (key == null || KeyPattern.IsMatch(key) == false)
            {
                throw new ArgumentException($"[SaveStore] 도메인 키 '{key}' 는 소문자·숫자·_·- 32자 이내여야 합니다(파일 이름으로 쓰인다).");
            }
        }

        private sealed class CommitJob
        {
            public readonly List<DomainPayload> Payloads;
            public readonly List<string> Removals;
            public readonly List<CommittedDomain> CommittedDomains;

            public CommitJob(List<DomainPayload> payloads, List<string> removals, List<CommittedDomain> committedDomains)
            {
                Payloads = payloads;
                Removals = removals;
                CommittedDomains = committedDomains;
            }
        }

        private readonly struct WriteFailure
        {
            public readonly CommitJob Job;
            public readonly Exception Exception;

            public WriteFailure(CommitJob job, Exception exception)
            {
                Job = job;
                Exception = exception;
            }
        }
    }

    public enum DomainLoadStatus
    {
        CreatedDefault,
        Loaded,
        LoadedFromBackup,
        ResetCorrupted,
    }

    public readonly struct DomainLoadResult
    {
        public readonly string Key;
        public readonly DomainLoadStatus Status;

        public DomainLoadResult(string key, DomainLoadStatus status)
        {
            Key = key;
            Status = status;
        }
    }

    public sealed class SaveLoadReport
    {
        public ManifestLoadStatus ManifestStatus;

        /// <summary>
        /// 현재 세대 손상으로 직전 세대 전체(스냅샷)로 되돌렸다. 마지막 커밋 하나만큼의 진행을 잃는다.
        /// </summary>
        public bool IsRolledBack;

        public readonly List<DomainLoadResult> Domains = new List<DomainLoadResult>();

        public DomainLoadStatus StatusOf(string key)
        {
            for (int i = 0; i < Domains.Count; i++)
            {
                if (Domains[i].Key == key)
                {
                    return Domains[i].Status;
                }
            }

            throw new KeyNotFoundException(key);
        }
    }
}
