using System;
using System.Collections.Generic;

namespace SaveStore.Storage
{
    /// <summary>
    /// 메모리 저장소. 앱을 끄면 사라진다 — 테스트, 저장하지 않는 체험판·게스트 모드, 새 플랫폼 구현 전 임시용.
    /// </summary>
    public sealed class MemorySaveStorage : ISaveStorage
    {
        private readonly Dictionary<string, byte[]> _blobDict = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        private readonly object _gate = new object();

        public string Location => "(memory)";

        public void Open()
        {
        }

        public bool TryRead(string name, out byte[] data)
        {
            lock (_gate)
            {
                if (_blobDict.TryGetValue(name, out byte[] stored) == false)
                {
                    data = null;
                    return false;
                }

                // 호출자가 받은 배열을 고쳐도 저장된 값이 바뀌지 않게 사본을 준다(파일 저장소와 같은 의미).
                data = (byte[])stored.Clone();
                return true;
            }
        }

        public void Write(string name, ReadOnlySpan<byte> data)
        {
            byte[] copy = data.ToArray();
            lock (_gate)
            {
                _blobDict[name] = copy;
            }
        }

        public void Rename(string sourceName, string destinationName)
        {
            lock (_gate)
            {
                if (_blobDict.ContainsKey(destinationName) == true)
                {
                    throw new InvalidOperationException($"'{destinationName}' 가 이미 있습니다.");
                }

                if (_blobDict.Remove(sourceName, out byte[] data) == false)
                {
                    throw new InvalidOperationException($"'{sourceName}' 가 없습니다.");
                }

                _blobDict[destinationName] = data;
            }
        }

        public void Delete(string name)
        {
            lock (_gate)
            {
                _blobDict.Remove(name);
            }
        }

        public IReadOnlyList<string> List()
        {
            lock (_gate)
            {
                var names = new List<string>(_blobDict.Keys);
                names.Sort(StringComparer.Ordinal);
                return names;
            }
        }
    }
}
