using System;
using System.IO;
using NUnit.Framework;
using SaveStore.Editor;
using SaveStore.Storage;

namespace SaveStore.Tests
{
    /// <summary>
    /// 골든 파일: v1 스키마(ProfileRecordV1)로 실제 저장해 둔 세이브(Fixtures/profile-v1)를 최신 코드가 계속 읽는지 확인한다.
    /// 레코드 규칙(Order 재사용·삭제 등)을 어기면 이 테스트가 깨진다 — 세이브 호환을 사람 기억이 아니라 빌드가 지킨다.
    /// </summary>
    public sealed class GoldenFileTests
    {
        private const string FixturesRoot = "Packages/com.nkstudio.savestore/Tests/Editor/Fixtures";

        [Test]
        public void Test_Golden_ProfileV1_LoadsWithAppendOnlyRecord()
        {
            SaveDomain[] domains = SaveGoldenFiles.AssertLoads(Path.Combine(FixturesRoot, "profile-v1"), () => new SaveDomain[] { new ProfileDomainV2() });

            var profile = (ProfileDomainV2)domains[0];
            Assert.That(profile.Nickname, Is.EqualTo("골든"));
        }

        [Test]
        public void Test_Golden_ProfileV1_LoadsThroughMigration()
        {
            SaveDomain[] domains = SaveGoldenFiles.AssertLoads(Path.Combine(FixturesRoot, "profile-v1"), () => new SaveDomain[] { new ProfileDomainV3() });

            Assert.That(((ProfileDomainV3)domains[0]).DisplayName, Is.EqualTo("골든"));
        }

        [Test]
        public void Test_Golden_FixtureIsNotModifiedByLoading()
        {
            string fixture = Path.Combine(FixturesRoot, "profile-v1");
            DateTime before = File.GetLastWriteTimeUtc(Path.Combine(fixture, "manifest.bin"));

            SaveGoldenFiles.AssertLoads(fixture, () => new SaveDomain[] { new ProfileDomainV2() });

            Assert.That(File.GetLastWriteTimeUtc(Path.Combine(fixture, "manifest.bin")), Is.EqualTo(before), "픽스처는 메모리 저장소로 올려 읽는다");
        }

        [Test]
        public void Test_Golden_DamagedFixture_IsReported()
        {
            string copy = Path.Combine(Path.GetTempPath(), "SaveStoreGolden", Guid.NewGuid().ToString("N"));
            try
            {
                SaveGoldenFiles.Capture(SaveGoldenFiles.LoadFixture(Path.Combine(FixturesRoot, "profile-v1")), copy);
                string blob = Path.Combine(copy, "profile.g1.bin");
                byte[] bytes = File.ReadAllBytes(blob);
                bytes[bytes.Length - 1] ^= 0xFF;
                File.WriteAllBytes(blob, bytes);

                UnityEngine.TestTools.LogAssert.Expect(UnityEngine.LogType.Error, new System.Text.RegularExpressions.Regex("기본값으로 초기화"));
                Assert.Throws<InvalidOperationException>(() => SaveGoldenFiles.AssertLoads(copy, () => new SaveDomain[] { new ProfileDomainV2() }));
            }
            finally
            {
                if (Directory.Exists(copy) == true)
                {
                    Directory.Delete(copy, true);
                }
            }
        }

        [Test]
        public void Test_RecordDumper_WritesReadableJson()
        {
            string json = SaveRecordDumper.ToJson(new ProfileRecordV2 { Nickname = "나비", FrameId = 3 });

            Assert.That(json, Does.Contain("\"Nickname\": \"나비\""));
            Assert.That(json, Does.Contain("\"FrameId\": 3"));
        }
    }
}
