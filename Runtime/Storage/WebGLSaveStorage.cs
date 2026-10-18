using System;
using System.Collections.Generic;
#if UNITY_WEBGL && !UNITY_EDITOR
using System.Runtime.InteropServices;
#endif

namespace SaveStore.Storage
{
    /// <summary>
    /// WebGL 저장소. WebGL 의 <c>Application.persistentDataPath</c> 는 브라우저 IndexedDB 위의 가상 파일 시스템이라,
    /// 파일을 쓴 뒤 JS 의 <c>FS.syncfs</c> 로 IndexedDB 에 내려야 탭을 닫아도 남는다. 이 구현은 파일 동작은 <see cref="FileSaveStorage"/> 에 맡기고
    /// 커밋 지점(manifest 이름 교체)과 삭제 뒤에 syncfs 를 요청한다(Plugins/WebGL/SaveStoreWebGL.jslib).
    ///
    /// XXX: syncfs 는 비동기라 <see cref="ISaveStorage.Write"/> 의 "반환 시 확정" 계약을 완전히 지키지 못한다 — syncfs 가 끝나기 전에 탭이 닫히면
    ///      마지막 커밋을 잃을 수 있다(그래도 manifest 커밋 순서 덕에 깨진 상태로 남지는 않는다). 에디터·다른 플랫폼에서는 FileSaveStorage 와 같다.
    /// </summary>
    public sealed class WebGLSaveStorage : ISaveStorage
    {
        private readonly FileSaveStorage _files;

        public WebGLSaveStorage(string rootDirectory)
        {
            _files = new FileSaveStorage(rootDirectory);
        }

        public string Location => _files.Location;

        public void Open()
        {
            _files.Open();
        }

        public bool TryRead(string name, out byte[] data)
        {
            return _files.TryRead(name, out data);
        }

        public void Write(string name, ReadOnlySpan<byte> data)
        {
            // 도메인 blob 쓰기 뒤에는 동기화하지 않는다 — 바로 이어지는 manifest 이름 교체(Rename)에서 한 번에 내린다.
            _files.Write(name, data);
        }

        public void Rename(string sourceName, string destinationName)
        {
            _files.Rename(sourceName, destinationName);
            RequestSync();
        }

        public void Delete(string name)
        {
            _files.Delete(name);
            RequestSync();
        }

        public IReadOnlyList<string> List()
        {
            return _files.List();
        }

#if UNITY_WEBGL && !UNITY_EDITOR
        [DllImport("__Internal")]
        private static extern void SaveStore_SyncFs();

        private static void RequestSync()
        {
            SaveStore_SyncFs();
        }
#else
        private static void RequestSync()
        {
        }
#endif
    }
}
