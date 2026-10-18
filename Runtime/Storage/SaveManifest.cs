using System.Collections.Generic;
using MemoryPack;

namespace SaveStore.Storage
{
    /// <summary>
    /// 커밋 지점. 도메인별로 "지금 유효한 파일 세대"를 가리킨다.
    /// 도메인 파일을 다 쓴 뒤 마지막에 이 파일을 교체하므로, 쓰다가 앱이 죽어도 이전 manifest 가 가리키는 세대가 그대로 유효하다
    /// — 여러 도메인을 한 번에 바꾸는 거래(재화 차감 + 지급)도 함께 반영되거나 함께 빠진다.
    /// </summary>
    [MemoryPackable(GenerateType.VersionTolerant)]
    public sealed partial class SaveManifest
    {
        [MemoryPackOrder(0)] public long Generation;
        [MemoryPackOrder(1)] public List<SaveManifestEntry> Entries = new();

        /// <summary>
        /// 커밋 시각(UTC unix ms). 슬롯 목록의 마지막 저장 시각·클라우드 충돌 판정용. 이 필드가 없던 세이브는 0.
        /// </summary>
        [MemoryPackOrder(2)] public long CommittedAtUnixMs;

        public SaveManifestEntry Find(string key)
        {
            for (int i = 0; i < Entries.Count; i++)
            {
                if (Entries[i].Key == key)
                {
                    return Entries[i];
                }
            }

            return null;
        }

        public SaveManifest CloneShallow()
        {
            var clone = new SaveManifest { Generation = Generation, CommittedAtUnixMs = CommittedAtUnixMs };
            for (int i = 0; i < Entries.Count; i++)
            {
                SaveManifestEntry entry = Entries[i];
                clone.Entries.Add(new SaveManifestEntry
                {
                    Key = entry.Key,
                    FileGeneration = entry.FileGeneration,
                    SchemaVersion = entry.SchemaVersion,
                    PayloadLength = entry.PayloadLength,
                    Checksum = entry.Checksum,
                });
            }

            return clone;
        }
    }

    [MemoryPackable(GenerateType.VersionTolerant)]
    public sealed partial class SaveManifestEntry
    {
        [MemoryPackOrder(0)] public string Key;
        [MemoryPackOrder(1)] public long FileGeneration;
        [MemoryPackOrder(2)] public int SchemaVersion;
        [MemoryPackOrder(3)] public int PayloadLength;
        [MemoryPackOrder(4)] public uint Checksum;
    }
}
