using System;
using System.Collections.Generic;
using MemoryPack;

namespace SaveStore
{
    /// <summary>
    /// 커밋 한 번의 결과. 클라우드 차등 업로드에 쓴다(<see cref="SaveSystem.OnCommitted"/>).
    /// </summary>
    public sealed class SaveCommitInfo
    {
        public SaveCommitInfo(long generation, long committedAtUnixMs, IReadOnlyList<CommittedDomain> domains, IReadOnlyList<string> removedKeys)
        {
            Generation = generation;
            CommittedAtUnixMs = committedAtUnixMs;
            Domains = domains ?? Array.Empty<CommittedDomain>();
            RemovedKeys = removedKeys ?? Array.Empty<string>();
        }

        public long Generation { get; }

        public long CommittedAtUnixMs { get; }

        /// <summary>
        /// 이번 세대에 바뀐 도메인. payload 는 평문 MemoryPack(변환 전)이다.
        /// </summary>
        public IReadOnlyList<CommittedDomain> Domains { get; }

        /// <summary>
        /// 이번 세대에 지워진 도메인 키(<see cref="SaveSystem.Forget"/>).
        /// </summary>
        public IReadOnlyList<string> RemovedKeys { get; }
    }

    public readonly struct CommittedDomain
    {
        public readonly string Key;
        public readonly int SchemaVersion;
        public readonly byte[] Payload;

        public CommittedDomain(string key, int schemaVersion, byte[] payload)
        {
            Key = key;
            SchemaVersion = schemaVersion;
            Payload = payload;
        }
    }

    /// <summary>
    /// 세이브 전체 스냅샷 — 클라우드 전체 업로드·복원, 계정 이전, 디버그 공유용. <see cref="ToBytes"/> 로 그대로 전송할 수 있다.
    /// </summary>
    [MemoryPackable(GenerateType.VersionTolerant)]
    public sealed partial class SaveSnapshot
    {
        /// <summary>
        /// 스냅샷을 뜰 때 로컬에 커밋돼 있던 세대(참고값 — 기기마다 세대 번호는 따로 증가한다).
        /// </summary>
        [MemoryPackOrder(0)] public long BaseGeneration;

        /// <summary>
        /// 스냅샷을 뜬 시각(UTC unix ms). 로컬·클라우드 중 어느 쪽이 최신인지 판단할 때 쓴다.
        /// </summary>
        [MemoryPackOrder(1)] public long CapturedAtUnixMs;

        [MemoryPackOrder(2)] public List<SaveSnapshotEntry> Entries = new();

        public byte[] ToBytes()
        {
            return MemoryPackSerializer.Serialize(this);
        }

        public static SaveSnapshot FromBytes(ReadOnlySpan<byte> bytes)
        {
            SaveSnapshot snapshot = MemoryPackSerializer.Deserialize<SaveSnapshot>(bytes)
                                    ?? throw new SaveDataException("스냅샷이 null 로 역직렬화됐습니다.");
            snapshot.Entries ??= new List<SaveSnapshotEntry>();
            return snapshot;
        }
    }

    [MemoryPackable(GenerateType.VersionTolerant)]
    public sealed partial class SaveSnapshotEntry
    {
        [MemoryPackOrder(0)] public string Key;
        [MemoryPackOrder(1)] public int SchemaVersion;
        [MemoryPackOrder(2)] public byte[] Payload;
    }
}
