using System;
using System.Collections.Generic;
using System.IO;

namespace SaveStore.Storage
{
    /// <summary>
    /// 로컬 파일 시스템 저장소(Android·iOS·PC·에디터). 보통 <c>Application.persistentDataPath</c> 아래 폴더 하나를 쓴다.
    /// XXX: WebGL 의 persistentDataPath 는 IndexedDB 위의 가상 파일 시스템이라 쓰기 뒤 JS 의 FS.syncfs 가 필요하다 — 이 구현은 그걸 하지 않는다.
    /// </summary>
    public sealed class FileSaveStorage : ISaveStorage
    {
        private readonly string _rootDirectory;

        public FileSaveStorage(string rootDirectory)
        {
            if (string.IsNullOrEmpty(rootDirectory) == true)
            {
                throw new ArgumentException("세이브 폴더 경로가 비었습니다.", nameof(rootDirectory));
            }

            _rootDirectory = rootDirectory;
        }

        public string Location => _rootDirectory;

        public void Open()
        {
            Directory.CreateDirectory(_rootDirectory);
        }

        public bool TryRead(string name, out byte[] data)
        {
            string path = PathOf(name);
            if (File.Exists(path) == false)
            {
                data = null;
                return false;
            }

            data = File.ReadAllBytes(path);
            return true;
        }

        public void Write(string name, ReadOnlySpan<byte> data)
        {
            // bufferSize 1 = FileStream 내부 버퍼(4KB)를 만들지 않는다 — 한 번에 통째로 쓰므로 버퍼가 필요 없고, 커밋마다 생기던 할당이 사라진다.
            using (var stream = new FileStream(PathOf(name), FileMode.Create, FileAccess.Write, FileShare.None, 1))
            {
                stream.Write(data);
                // OS 캐시가 아니라 디스크까지 내린다 — 커밋 순서 보장이 이것에 기댄다.
                stream.Flush(true);
            }
        }

        public void Rename(string sourceName, string destinationName)
        {
            File.Move(PathOf(sourceName), PathOf(destinationName));
        }

        public void Delete(string name)
        {
            string path = PathOf(name);
            if (File.Exists(path) == true)
            {
                File.Delete(path);
            }
        }

        public IReadOnlyList<string> List()
        {
            if (Directory.Exists(_rootDirectory) == false)
            {
                return Array.Empty<string>();
            }

            string[] paths = Directory.GetFiles(_rootDirectory);
            for (int i = 0; i < paths.Length; i++)
            {
                paths[i] = Path.GetFileName(paths[i]);
            }

            Array.Sort(paths, StringComparer.Ordinal);
            return paths;
        }

        private string PathOf(string name)
        {
            return Path.Combine(_rootDirectory, name);
        }
    }
}
