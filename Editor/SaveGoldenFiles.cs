using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using SaveStore.Storage;
using UnityEditor;

namespace SaveStore.Editor
{
    /// <summary>
    /// 골든 파일(출시 버전 세이브 샘플) 도구. 버전을 낼 때 실제 세이브를 <c>Fixtures/{버전}/</c> 에 떠 두고,
    /// EditMode 테스트에서 <see cref="AssertLoads"/> 로 "최신 코드가 모든 과거 세이브를 읽는가"를 검사한다 — 호환이 깨지면 빌드 전에 실패한다.
    /// <code>
    /// [Test]
    /// public void Test_AllReleasedSavesStillLoad()
    /// {
    ///     foreach (string fixture in SaveGoldenFiles.ListFixtures("Assets/Tests/SaveFixtures"))
    ///         SaveGoldenFiles.AssertLoads(fixture, () =&gt; MySave.CreateDomains());
    /// }
    /// </code>
    /// </summary>
    public static class SaveGoldenFiles
    {
        /// <summary>
        /// 저장소의 모든 blob 을 폴더로 복사한다(기존 내용은 지운다).
        /// </summary>
        public static void Capture(ISaveStorage source, string fixtureDirectory)
        {
            if (Directory.Exists(fixtureDirectory) == true)
            {
                foreach (string file in Directory.GetFiles(fixtureDirectory))
                {
                    if (file.EndsWith(".meta", StringComparison.Ordinal) == false)
                    {
                        File.Delete(file);
                    }
                }
            }

            Directory.CreateDirectory(fixtureDirectory);
            foreach (string name in source.List())
            {
                if (source.TryRead(name, out byte[] data) == true)
                {
                    File.WriteAllBytes(Path.Combine(fixtureDirectory, name), data);
                }
            }
        }

        /// <summary>
        /// 픽스처 폴더를 메모리 저장소로 올린다 — 원본 파일을 건드리지 않고 로드해 보기 위해서다.
        /// </summary>
        public static MemorySaveStorage LoadFixture(string fixtureDirectory)
        {
            if (Directory.Exists(fixtureDirectory) == false)
            {
                throw new DirectoryNotFoundException(fixtureDirectory);
            }

            var storage = new MemorySaveStorage();
            foreach (string file in Directory.GetFiles(fixtureDirectory))
            {
                if (file.EndsWith(".meta", StringComparison.Ordinal) == true)
                {
                    continue;
                }

                storage.Write(Path.GetFileName(file), File.ReadAllBytes(file));
            }

            return storage;
        }

        public static IReadOnlyList<string> ListFixtures(string fixturesRoot)
        {
            if (Directory.Exists(fixturesRoot) == false)
            {
                return Array.Empty<string>();
            }

            string[] directories = Directory.GetDirectories(fixturesRoot);
            Array.Sort(directories, StringComparer.Ordinal);
            return directories;
        }

        /// <summary>
        /// 픽스처를 지금 코드로 로드해 본다. 손상 판정·롤백·초기화·예외가 하나라도 있으면 <see cref="InvalidOperationException"/>.
        /// 로드에 성공한 도메인을 돌려주므로 값 검증을 이어서 할 수 있다.
        /// </summary>
        public static SaveDomain[] AssertLoads(string fixtureDirectory, Func<SaveDomain[]> createDomains, SaveOptions options = null)
        {
            SaveDomain[] domains = createDomains();
            var problems = new StringBuilder();
            try
            {
                var effectiveOptions = new SaveOptions
                {
                    IsBackgroundWriteEnabled = false,
                    Clock = options?.Clock ?? SystemClock.Instance,
                    PayloadTransform = options?.PayloadTransform,
                    AcceptsUntransformedBlobs = options != null && options.AcceptsUntransformedBlobs,
                };
                var system = new SaveSystem(LoadFixture(fixtureDirectory), domains, effectiveOptions);
                SaveLoadReport report = system.Load();
                if (report.ManifestStatus == ManifestLoadStatus.CorruptedReset || report.ManifestStatus == ManifestLoadStatus.New)
                {
                    problems.Append($"manifest: {report.ManifestStatus}; ");
                }

                if (report.IsRolledBack == true)
                {
                    problems.Append("직전 세대로 롤백됨; ");
                }

                foreach (DomainLoadResult result in report.Domains)
                {
                    if (result.Status == DomainLoadStatus.ResetCorrupted || result.Status == DomainLoadStatus.LoadedFromBackup)
                    {
                        problems.Append($"{result.Key}: {result.Status}; ");
                    }
                }
            }
            catch (Exception exception)
            {
                problems.Append($"예외: {exception.GetType().Name} {exception.Message}");
            }

            if (problems.Length > 0)
            {
                throw new InvalidOperationException($"[SaveStore] 골든 파일 '{fixtureDirectory}' 를 읽지 못했습니다 — 세이브 호환이 깨졌습니다: {problems}");
            }

            return domains;
        }

        [MenuItem("Tools/Save Store/Capture Save Folder as Golden Fixture...")]
        private static void CaptureFromFolderMenu()
        {
            string source = EditorUtility.OpenFolderPanel("원본 세이브 폴더 (manifest.bin 이 있는 폴더)", UnityEngine.Application.persistentDataPath, string.Empty);
            if (string.IsNullOrEmpty(source) == true)
            {
                return;
            }

            string destination = EditorUtility.SaveFolderPanel("픽스처 폴더 (예: Assets/Tests/SaveFixtures/1.2.0)", "Assets", "fixture");
            if (string.IsNullOrEmpty(destination) == true)
            {
                return;
            }

            Capture(new FileSaveStorage(source), destination);
            AssetDatabase.Refresh();
            EditorUtility.DisplayDialog("Save Store", $"픽스처를 만들었습니다:\n{destination}", "확인");
        }
    }
}
