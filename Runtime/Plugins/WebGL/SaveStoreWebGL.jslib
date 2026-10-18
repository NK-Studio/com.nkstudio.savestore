mergeInto(LibraryManager.library, {
    // persistentDataPath(IDBFS)의 변경을 IndexedDB 로 내린다. 비동기 — 실패는 콘솔에만 남긴다.
    SaveStore_SyncFs: function () {
        FS.syncfs(false, function (error) {
            if (error) {
                console.error('[SaveStore] FS.syncfs failed', error);
            }
        });
    }
});
