using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using MemoryPack;
using NUnit.Framework;
using SaveStore.Storage;
using UnityEngine;
using UnityEngine.TestTools;

namespace SaveStore.Tests
{
    public enum StorageKind
    {
        File,
        Memory,
    }

    /// <summary>
    /// 모든 시나리오를 파일 저장소와 메모리 저장소에서 각각 돌린다 — 안전 규칙은 저장소 구현과 무관해야 한다.
    /// </summary>
    [TestFixture(StorageKind.File)]
    [TestFixture(StorageKind.Memory)]
    public sealed class SaveSystemTests
    {
        private readonly StorageKind _storageKind;
        private string _root;
        private ISaveStorage _storage;

        public SaveSystemTests(StorageKind storageKind)
        {
            _storageKind = storageKind;
        }

        [SetUp]
        public void SetUp()
        {
            _root = Path.Combine(Path.GetTempPath(), "SaveStoreTests", Guid.NewGuid().ToString("N"));
            _storage = _storageKind == StorageKind.File ? new FileSaveStorage(_root) : new MemorySaveStorage();
        }

        [TearDown]
        public void TearDown()
        {
            if (Directory.Exists(_root) == true)
            {
                Directory.Delete(_root, true);
            }
        }

        [Test]
        public void Test_Load_NoFiles_CreatesDefaults()
        {
            var counter = new CounterDomain("counter");
            SaveLoadReport report = CreateSystem(counter).Load();

            Assert.That(report.ManifestStatus, Is.EqualTo(ManifestLoadStatus.New));
            Assert.That(report.StatusOf("counter"), Is.EqualTo(DomainLoadStatus.CreatedDefault));
            Assert.That(counter.Value, Is.EqualTo(0));
            Assert.That(counter.IsDirty, Is.False, "기본값은 아직 저장할 변경이 아니다");
        }

        [Test]
        public void Test_WriteDirty_RoundTrip_RestoresValues()
        {
            var counter = new CounterDomain("counter");
            SaveSystem system = CreateSystem(counter);
            system.Load();
            counter.Increment();
            counter.Increment();
            system.SaveNow();

            var reloaded = new CounterDomain("counter");
            SaveLoadReport report = CreateSystem(reloaded).Load();

            Assert.That(report.StatusOf("counter"), Is.EqualTo(DomainLoadStatus.Loaded));
            Assert.That(reloaded.Value, Is.EqualTo(2));
        }

        [Test]
        public void Test_WriteDirty_BackgroundThread_FlushWaitsForCommit()
        {
            var counter = new CounterDomain("counter");
            var system = new SaveSystem(_storage, new SaveDomain[] { counter }, new SaveOptions { IsBackgroundWriteEnabled = true });
            system.Load();
            counter.Increment();

            Assert.That(system.WriteDirty(), Is.EqualTo(1));
            system.Flush();

            var reloaded = new CounterDomain("counter");
            CreateSystem(reloaded).Load();
            Assert.That(reloaded.Value, Is.EqualTo(1));
        }

        [Test]
        public void Test_WriteDirty_OnlyDirtyDomainsAreWritten()
        {
            var first = new CounterDomain("first");
            var second = new CounterDomain("second");
            SaveSystem system = CreateSystem(first, second);
            system.Load();
            first.Increment();
            second.Increment();
            system.SaveNow();
            string secondBlobBefore = system.Repository.GetCurrentDomainBlobName("second");

            first.Increment();
            int writtenCount = system.WriteDirty();
            system.Flush();

            Assert.That(writtenCount, Is.EqualTo(1));
            Assert.That(system.Repository.GetCurrentDomainBlobName("second"), Is.EqualTo(secondBlobBefore),
                "바뀌지 않은 도메인은 같은 세대 blob 을 계속 가리킨다");
            Assert.That(system.WriteDirty(), Is.EqualTo(0), "dirty 가 없으면 아무것도 쓰지 않는다");
        }

        [Test]
        public void Test_Load_CorruptedCurrentFile_FallsBackToPreviousGeneration()
        {
            var counter = new CounterDomain("counter");
            SaveSystem system = CreateSystem(counter);
            system.Load();
            counter.Increment();
            system.SaveNow();
            counter.Increment();
            system.SaveNow();

            CorruptLastByte(system.Repository.GetCurrentDomainBlobName("counter"));

            var reloaded = new CounterDomain("counter");
            LogAssert.Expect(LogType.Warning, new Regex("직전 세대"));
            SaveLoadReport report = CreateSystem(reloaded).Load();

            Assert.That(report.IsRolledBack, Is.True);
            Assert.That(report.StatusOf("counter"), Is.EqualTo(DomainLoadStatus.LoadedFromBackup));
            Assert.That(reloaded.Value, Is.EqualTo(1), "직전 세대 값으로 되돌아간다");
            Assert.That(reloaded.IsDirty, Is.True, "되돌린 상태를 다음 커밋에 다시 써 손상 세대를 대체한다");
        }

        [Test]
        public void Test_Load_OneDomainCorrupted_RollsBackWholeSnapshot()
        {
            var gold = new CounterDomain("gold");
            var items = new CounterDomain("items");
            SaveSystem system = CreateSystem(gold, items);
            system.Load();
            gold.Increment();
            items.Increment();
            system.SaveNow();

            // 한 거래(재화 지급 + 아이템 지급)가 한 커밋으로 들어갔다.
            gold.Increment();
            items.Increment();
            system.SaveNow();

            CorruptLastByte(system.Repository.GetCurrentDomainBlobName("items"));

            var reloadedGold = new CounterDomain("gold");
            var reloadedItems = new CounterDomain("items");
            LogAssert.Expect(LogType.Warning, new Regex("전체로 되돌렸습니다"));
            CreateSystem(reloadedGold, reloadedItems).Load();

            Assert.That(reloadedItems.Value, Is.EqualTo(1));
            Assert.That(reloadedGold.Value, Is.EqualTo(1), "멀쩡한 gold 도 같은 세대로 함께 되돌아가야 거래가 반만 남지 않는다");
        }

        [Test]
        public void Test_Load_CorruptedWithoutBackup_ResetsAndMarksDirty()
        {
            var counter = new CounterDomain("counter");
            SaveSystem system = CreateSystem(counter);
            system.Load();
            counter.Increment();
            system.SaveNow();

            CorruptLastByte(system.Repository.GetCurrentDomainBlobName("counter"));

            var reloaded = new CounterDomain("counter");
            LogAssert.Expect(LogType.Error, new Regex("기본값으로 초기화"));
            SaveLoadReport report = CreateSystem(reloaded).Load();

            Assert.That(report.StatusOf("counter"), Is.EqualTo(DomainLoadStatus.ResetCorrupted));
            Assert.That(reloaded.Value, Is.EqualTo(0));
            Assert.That(reloaded.IsDirty, Is.True, "초기화한 상태를 다음 체크포인트에 다시 써 손상 blob 을 대체한다");
        }

        [Test]
        public void Test_Load_MainManifestMissing_RecoversFromTemp()
        {
            var counter = new CounterDomain("counter");
            SaveSystem system = CreateSystem(counter);
            system.Load();
            counter.Increment();
            system.SaveNow();

            // 커밋이 "manifest.tmp → manifest.bin 이름 교체" 직전에 끊긴 상황.
            _storage.Rename("manifest.bin", "manifest.tmp");

            var reloaded = new CounterDomain("counter");
            LogAssert.Expect(LogType.Warning, new Regex("끊긴 커밋"));
            SaveLoadReport report = CreateSystem(reloaded).Load();

            Assert.That(report.ManifestStatus, Is.EqualTo(ManifestLoadStatus.RecoveredFromTemp));
            Assert.That(reloaded.Value, Is.EqualTo(1));
        }

        [Test]
        public void Test_Load_TempNewerThanMain_PicksNewestCommit()
        {
            var counter = new CounterDomain("counter");
            SaveSystem system = CreateSystem(counter);
            system.Load();
            counter.Increment();
            system.SaveNow();
            counter.Increment();
            system.SaveNow();

            // 커밋 도중 "manifest.tmp 를 끝까지 썼지만 .bin 을 .bak 으로 옮기기 전"에 끊긴 상황: main=세대1, tmp=세대2.
            _storage.Rename("manifest.bin", "manifest.tmp");
            _storage.Rename("manifest.bak", "manifest.bin");

            var reloaded = new CounterDomain("counter");
            LogAssert.Expect(LogType.Warning, new Regex("끊긴 커밋"));
            SaveLoadReport report = CreateSystem(reloaded).Load();

            Assert.That(report.ManifestStatus, Is.EqualTo(ManifestLoadStatus.RecoveredFromTemp));
            Assert.That(reloaded.Value, Is.EqualTo(2), "완성된 더 새 커밋(tmp)을 버리지 않는다");
        }

        [Test]
        public void Test_Load_OrphanBlob_IsDeleted()
        {
            var counter = new CounterDomain("counter");
            SaveSystem system = CreateSystem(counter);
            system.Load();
            counter.Increment();
            system.SaveNow();

            // manifest 에 실리기 전에 끊긴 커밋이 남긴 blob.
            _storage.Write("counter.g99.bin", new byte[] { 1, 2, 3 });

            CreateSystem(new CounterDomain("counter")).Load();

            Assert.That(_storage.List(), Does.Not.Contain("counter.g99.bin"));
        }

        [Test]
        public void Test_Load_AppendOnlyFieldAdded_ReadsOldSaveWithDefault()
        {
            var v1 = new ProfileDomainV1();
            SaveSystem system = CreateSystem(v1);
            system.Load();
            v1.SetNickname("나비");
            system.SaveNow();

            var v2 = new ProfileDomainV2();
            CreateSystem(v2).Load();

            Assert.That(v2.Nickname, Is.EqualTo("나비"));
            // 함정: 옛 세이브에 없는 새 필드는 필드 초기값(= 7)이 아니라 default(0/null)로 들어온다.
            // 기본값이 필요한 새 필드는 도메인의 Validate() 에서 채운다(README "레코드 규칙").
            Assert.That(v2.FrameId, Is.EqualTo(0));
            Assert.That(v2.TagsWereNullBeforeValidate, Is.True, "새 List 필드도 null 로 들어온다 — Validate 의 ??= 가 필요한 이유");
            Assert.That(v2.Tags, Is.Not.Null);
        }

        [Test]
        public void Test_Load_OlderSchema_RunsMigrate()
        {
            var v1 = new ProfileDomainV1();
            SaveSystem system = CreateSystem(v1);
            system.Load();
            v1.SetNickname("치즈");
            system.SaveNow();

            var v3 = new ProfileDomainV3();
            CreateSystem(v3).Load();

            Assert.That(v3.DisplayName, Is.EqualTo("치즈"));
        }

        [Test]
        public void Test_Load_NewerSchemaThanApp_Throws()
        {
            // 더 새 앱(스키마 2)이 쓴 blob 을 직접 만든다.
            var repository = new SaveRepository(_storage);
            repository.LoadManifests();
            byte[] payload = MemoryPackSerializer.Serialize(new ProfileRecordV3 { DisplayName = "x" });
            repository.Commit(new[] { new DomainPayload("profile", payload, payload.Length, schemaVersion: 2) });

            var olderApp = new ProfileDomainV2();
            Assert.Throws<SaveDataException>(() => CreateSystem(olderApp).Load());
        }

        [Test]
        public void Test_Validate_RunsOnlyOnLoad()
        {
            var counter = new CounterDomain("counter");
            SaveSystem system = CreateSystem(counter);
            system.Load();
            int validateCountAfterLoad = counter.ValidateCount;

            for (int i = 0; i < 10; i++)
            {
                counter.Increment();
                _ = counter.Value;
            }

            system.SaveNow();
            Assert.That(counter.ValidateCount, Is.EqualTo(validateCountAfterLoad), "조회·변경·저장은 self-heal 을 다시 돌리지 않는다");
        }

        [Test]
        public void Test_WriteDirty_BeforeLoad_Throws()
        {
            SaveSystem system = CreateSystem(new CounterDomain("counter"));
            Assert.Throws<InvalidOperationException>(() => system.WriteDirty());
        }

        [Test]
        public void Test_Constructor_InvalidOrDuplicateKey_Throws()
        {
            Assert.Throws<ArgumentException>(() => CreateSystem(new CounterDomain("Bad Key")));
            Assert.Throws<ArgumentException>(() => CreateSystem(new CounterDomain("dup"), new CounterDomain("dup")));
        }

        [Test]
        public void Test_Commit_KeepsOnlyCurrentAndPreviousGenerations()
        {
            var counter = new CounterDomain("counter");
            SaveSystem system = CreateSystem(counter);
            system.Load();
            for (int i = 0; i < 5; i++)
            {
                counter.Increment();
                system.SaveNow();
            }

            Assert.That(_storage.List().Count(name => name.StartsWith("counter.g", StringComparison.Ordinal)), Is.EqualTo(2));
        }

        [Test]
        public void Test_Storage_RenameOntoExisting_Throws()
        {
            _storage.Open();
            _storage.Write("a.bin", new byte[] { 1 });
            _storage.Write("b.bin", new byte[] { 2 });

            Assert.That(() => _storage.Rename("a.bin", "b.bin"), Throws.Exception, "Rename 은 대상을 덮어쓰지 않는다(ISaveStorage 계약)");
            Assert.That(_storage.TryRead("b.bin", out byte[] data), Is.True);
            Assert.That(data[0], Is.EqualTo(2));
        }

        [Test]
        public void Test_Crc32_KnownCheckValue()
        {
            Assert.That(Crc32.Compute(Encoding.ASCII.GetBytes("123456789")), Is.EqualTo(0xCBF43926u));
        }

        [Test]
        public void Test_VersionedComputed_RecomputesOnlyWhenDependencyChanges()
        {
            var counter = new CounterDomain("counter");
            var other = new CounterDomain("other");
            CreateSystem(counter, other).Load();
            var doubled = new VersionedComputed<int>(() => counter.Value * 2, counter);

            Assert.That(doubled.Value, Is.EqualTo(0));
            Assert.That(doubled.Value, Is.EqualTo(0));
            other.Increment();
            Assert.That(doubled.Value, Is.EqualTo(0));
            Assert.That(doubled.ComputeCount, Is.EqualTo(1), "의존하지 않는 도메인 변경은 다시 계산하지 않는다");

            counter.Increment();
            Assert.That(doubled.Value, Is.EqualTo(2));
            Assert.That(doubled.ComputeCount, Is.EqualTo(2));
        }

        private SaveSystem CreateSystem(params SaveDomain[] domains)
        {
            // 재시작을 흉내 내도록 저장소는 같고 리포지토리(메모리 상태)는 매번 새로 만든다.
            return new SaveSystem(new SaveRepository(_storage), domains, new SaveOptions { IsBackgroundWriteEnabled = false });
        }

        private void CorruptLastByte(string blobName)
        {
            Assert.That(_storage.TryRead(blobName, out byte[] bytes), Is.True, blobName);
            bytes[bytes.Length - 1] ^= 0xFF;
            _storage.Write(blobName, bytes);
        }
    }
}
