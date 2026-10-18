using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using SaveStore.Storage;

namespace SaveStore
{
    /// <summary>
    /// 세이브 슬롯(프로필·계정별 세이브) 목록. 슬롯 하나 = 독립된 <see cref="ISaveStorage"/> 하나다.
    /// 슬롯을 바꿀 때는 지금 SaveSystem 을 Dispose(남은 변경 저장)하고 새 슬롯 저장소로 SaveSystem 을 만들어 Load 한다.
    /// </summary>
    public interface ISaveSlotProvider
    {
        IReadOnlyList<string> ListSlotIds();

        ISaveStorage OpenSlot(string slotId);

        void DeleteSlot(string slotId);
    }

    /// <summary>
    /// 슬롯 목록 화면용 요약. 슬롯을 열지 않고(저장소를 고치지 않고) 마지막 커밋만 읽는다.
    /// </summary>
    public readonly struct SaveSlotInfo
    {
        public readonly string SlotId;
        public readonly bool HasData;
        public readonly long Generation;
        public readonly long CommittedAtUnixMs;

        public SaveSlotInfo(string slotId, bool hasData, long generation, long committedAtUnixMs)
        {
            SlotId = slotId;
            HasData = hasData;
            Generation = generation;
            CommittedAtUnixMs = committedAtUnixMs;
        }

        /// <summary>
        /// 마지막 저장 시각(UTC). 커밋 시각이 기록되기 전 형식이면 null.
        /// </summary>
        public DateTime? CommittedAtUtc => CommittedAtUnixMs > 0 ? DateTimeOffset.FromUnixTimeMilliseconds(CommittedAtUnixMs).UtcDateTime : (DateTime?)null;
    }

    public static class SaveSlots
    {
        // 슬롯 Id 는 폴더·키 이름으로 쓰이므로 도메인 키와 같은 규칙을 따른다.
        private static readonly Regex SlotIdPattern = new Regex("^[a-z0-9_-]{1,32}$", RegexOptions.Compiled);

        public static void ValidateSlotId(string slotId)
        {
            if (slotId == null || SlotIdPattern.IsMatch(slotId) == false)
            {
                throw new ArgumentException($"[SaveStore] 슬롯 Id '{slotId}' 는 소문자·숫자·_·- 32자 이내여야 합니다.");
            }
        }

        /// <summary>
        /// 슬롯의 마지막 커밋을 읽는다. 변환(암호화)을 쓰는 세이브면 같은 옵션을 넘겨야 manifest 를 읽을 수 있다.
        /// </summary>
        public static SaveSlotInfo Inspect(ISaveSlotProvider provider, string slotId, SaveOptions options = null)
        {
            var repository = new SaveRepository(provider.OpenSlot(slotId), options);
            bool hasData = repository.TryPeekLatestCommit(out long generation, out long committedAtUnixMs);
            return new SaveSlotInfo(slotId, hasData, generation, committedAtUnixMs);
        }

        public static List<SaveSlotInfo> InspectAll(ISaveSlotProvider provider, SaveOptions options = null)
        {
            var infos = new List<SaveSlotInfo>();
            IReadOnlyList<string> slotIds = provider.ListSlotIds();
            for (int i = 0; i < slotIds.Count; i++)
            {
                infos.Add(Inspect(provider, slotIds[i], options));
            }

            return infos;
        }
    }

    /// <summary>
    /// 슬롯마다 하위 폴더 하나: <c>{root}/{slotId}/manifest.bin …</c>
    /// </summary>
    public sealed class FileSaveSlotProvider : ISaveSlotProvider
    {
        private readonly string _rootDirectory;

        public FileSaveSlotProvider(string rootDirectory)
        {
            if (string.IsNullOrEmpty(rootDirectory) == true)
            {
                throw new ArgumentException("슬롯 루트 폴더 경로가 비었습니다.", nameof(rootDirectory));
            }

            _rootDirectory = rootDirectory;
        }

        public string RootDirectory => _rootDirectory;

        public IReadOnlyList<string> ListSlotIds()
        {
            if (Directory.Exists(_rootDirectory) == false)
            {
                return Array.Empty<string>();
            }

            var slotIds = new List<string>();
            foreach (string directory in Directory.GetDirectories(_rootDirectory))
            {
                string slotId = Path.GetFileName(directory);
                if (slotId != null && Regex.IsMatch(slotId, "^[a-z0-9_-]{1,32}$") == true)
                {
                    slotIds.Add(slotId);
                }
            }

            slotIds.Sort(StringComparer.Ordinal);
            return slotIds;
        }

        public ISaveStorage OpenSlot(string slotId)
        {
            SaveSlots.ValidateSlotId(slotId);
            return new FileSaveStorage(Path.Combine(_rootDirectory, slotId));
        }

        public void DeleteSlot(string slotId)
        {
            SaveSlots.ValidateSlotId(slotId);
            string path = Path.Combine(_rootDirectory, slotId);
            if (Directory.Exists(path) == true)
            {
                Directory.Delete(path, true);
            }
        }
    }

    /// <summary>
    /// 메모리 슬롯 — 테스트용. 같은 Id 로 다시 열면 같은 저장소를 돌려준다.
    /// </summary>
    public sealed class MemorySaveSlotProvider : ISaveSlotProvider
    {
        private readonly Dictionary<string, MemorySaveStorage> _slotDict = new Dictionary<string, MemorySaveStorage>(StringComparer.Ordinal);

        public IReadOnlyList<string> ListSlotIds()
        {
            var slotIds = new List<string>();
            foreach (KeyValuePair<string, MemorySaveStorage> pair in _slotDict)
            {
                if (pair.Value.List().Count > 0)
                {
                    slotIds.Add(pair.Key);
                }
            }

            slotIds.Sort(StringComparer.Ordinal);
            return slotIds;
        }

        public ISaveStorage OpenSlot(string slotId)
        {
            SaveSlots.ValidateSlotId(slotId);
            if (_slotDict.TryGetValue(slotId, out MemorySaveStorage storage) == false)
            {
                storage = new MemorySaveStorage();
                _slotDict.Add(slotId, storage);
            }

            return storage;
        }

        public void DeleteSlot(string slotId)
        {
            _slotDict.Remove(slotId);
        }
    }
}
