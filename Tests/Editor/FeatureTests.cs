using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using NUnit.Framework;
using SaveStore.Storage;
using UnityEngine;
using UnityEngine.TestTools;

namespace SaveStore.Tests
{
    public sealed class FeatureTests
    {
        private static readonly byte[] TestKey = Encoding.UTF8.GetBytes("unit-test-key-0123456789abcdef!!");

        private static SaveSystem Create(ISaveStorage storage, SaveOptions options, params SaveDomain[] domains)
        {
            options ??= new SaveOptions();
            options.IsBackgroundWriteEnabled = false;
            return new SaveSystem(storage, domains, options);
        }

        // ---- 슬롯 ----

        [Test]
        public void Test_Slots_InspectReportsLatestCommitWithoutOpening()
        {
            var provider = new MemorySaveSlotProvider();
            var clock = new ManualClock(1_000_000);
            var counter = new CounterDomain("counter");
            SaveSystem slotA = Create(provider.OpenSlot("slot-a"), new SaveOptions { Clock = clock }, counter);
            slotA.Load();
            counter.Increment();
            slotA.SaveNow();
            clock.Advance(5_000);
            counter.Increment();
            slotA.SaveNow();

            SaveSlotInfo info = SaveSlots.Inspect(provider, "slot-a");
            SaveSlotInfo empty = SaveSlots.Inspect(provider, "slot-b");

            Assert.That(info.HasData, Is.True);
            Assert.That(info.Generation, Is.EqualTo(2));
            Assert.That(info.CommittedAtUnixMs, Is.EqualTo(1_005_000), "커밋 시각은 주입한 시계에서 온다");
            Assert.That(empty.HasData, Is.False);
            Assert.That(provider.ListSlotIds(), Is.EqualTo(new[] { "slot-a" }));
        }

        [Test]
        public void Test_Slots_FileProvider_IsolatesSlotsAndDeletes()
        {
            string root = Path.Combine(Path.GetTempPath(), "SaveStoreSlots", Guid.NewGuid().ToString("N"));
            try
            {
                var provider = new FileSaveSlotProvider(root);
                var first = new CounterDomain("counter");
                SaveSystem slot1 = Create(provider.OpenSlot("slot-1"), null, first);
                slot1.Load();
                first.Increment();
                slot1.SaveNow();

                var second = new CounterDomain("counter");
                Create(provider.OpenSlot("slot-2"), null, second).Load();
                Assert.That(second.Value, Is.EqualTo(0), "다른 슬롯은 독립이다");

                provider.DeleteSlot("slot-1");
                Assert.That(provider.ListSlotIds(), Does.Not.Contain("slot-1"));
                Assert.Throws<ArgumentException>(() => provider.OpenSlot("../escape"));
            }
            finally
            {
                if (Directory.Exists(root) == true)
                {
                    Directory.Delete(root, true);
                }
            }
        }

        // ---- 변환 ----

        private static IEnumerable<ISavePayloadTransform> AllTransforms()
        {
            yield return new DeflateCompressionTransform();
            yield return new HmacSignatureTransform(TestKey);
            yield return new AesHmacEncryptionTransform(TestKey);
            yield return new TransformChain(100, new DeflateCompressionTransform(), new AesHmacEncryptionTransform(TestKey));
        }

        [Test]
        public void Test_Transforms_RoundTripThroughSaveSystem([ValueSource(nameof(AllTransforms))] ISavePayloadTransform transform)
        {
            var storage = new MemorySaveStorage();
            var counter = new CounterDomain("counter");
            SaveSystem system = Create(storage, new SaveOptions { PayloadTransform = transform }, counter);
            system.Load();
            for (int i = 0; i < 5; i++)
            {
                counter.Increment();
            }

            system.SaveNow();

            var reloaded = new CounterDomain("counter");
            SaveLoadReport report = Create(storage, new SaveOptions { PayloadTransform = transform }, reloaded).Load();

            Assert.That(report.StatusOf("counter"), Is.EqualTo(DomainLoadStatus.Loaded));
            Assert.That(reloaded.Value, Is.EqualTo(5));
        }

        [Test]
        public void Test_Transforms_TamperedPayload_IsRejectedBySignature()
        {
            foreach (ISavePayloadTransform transform in new ISavePayloadTransform[] { new HmacSignatureTransform(TestKey), new AesHmacEncryptionTransform(TestKey) })
            {
                var plain = Encoding.UTF8.GetBytes("gold=100");
                var encoded = new PooledBufferWriter();
                transform.Encode(plain, encoded);
                byte[] tampered = encoded.WrittenSpan.ToArray();
                tampered[tampered.Length / 2] ^= 0x01;

                var decoded = new PooledBufferWriter();
                Assert.That(transform.TryDecode(tampered, decoded, out string reason), Is.False, transform.GetType().Name);
                Assert.That(reason, Does.Contain("서명"));
            }
        }

        [Test]
        public void Test_Transforms_WrongKey_IsRejected()
        {
            var encoded = new PooledBufferWriter();
            new AesHmacEncryptionTransform(TestKey).Encode(Encoding.UTF8.GetBytes("secret"), encoded);
            var otherKey = new AesHmacEncryptionTransform(Encoding.UTF8.GetBytes("another-key-0123456789abcdef!!!!"));

            Assert.That(otherKey.TryDecode(encoded.WrittenSpan, new PooledBufferWriter(), out _), Is.False);
        }

        [Test]
        public void Test_Transforms_PlainBlobRejectedUnlessMigrationAllowed()
        {
            var storage = new MemorySaveStorage();
            var counter = new CounterDomain("counter");
            SaveSystem plainSystem = Create(storage, null, counter);
            plainSystem.Load();
            counter.Increment();
            plainSystem.SaveNow();

            var signed = new HmacSignatureTransform(TestKey);
            var strict = new CounterDomain("counter");
            LogAssert.Expect(LogType.Error, new Regex("manifest 본 파일·백업이 모두 손상"));
            Create(storage, new SaveOptions { PayloadTransform = signed }, strict).Load();
            Assert.That(strict.Value, Is.EqualTo(0), "서명 없는 평문 세이브는 거부한다(서명 우회 방지)");

            var migrating = new CounterDomain("counter");
            Create(storage, new SaveOptions { PayloadTransform = signed, AcceptsUntransformedBlobs = true }, migrating).Load();
            Assert.That(migrating.Value, Is.EqualTo(1), "평문 → 서명 이전 기간에는 읽는다");
        }

        [Test]
        public void Test_Transforms_DeflateShrinksRepetitivePayload()
        {
            byte[] repetitive = Enumerable.Repeat((byte)'a', 10_000).ToArray();
            var encoded = new PooledBufferWriter();
            new DeflateCompressionTransform().Encode(repetitive, encoded);
            Assert.That(encoded.WrittenCount, Is.LessThan(repetitive.Length / 10));
        }

        // ---- 트랜잭션 ----

        [Test]
        public void Test_Transaction_DisposeWithoutComplete_RestoresMemoryState()
        {
            var gold = new CounterDomain("gold");
            var items = new CounterDomain("items");
            SaveSystem system = Create(new MemorySaveStorage(), null, gold, items);
            system.Load();
            gold.Increment();
            int versionBefore = items.Version;

            using (system.BeginTransaction(gold, items))
            {
                gold.Increment();
                items.Increment();
                // 중간 실패 — Complete 하지 않고 빠져나간다.
            }

            Assert.That(gold.Value, Is.EqualTo(1));
            Assert.That(items.Value, Is.EqualTo(0));
            Assert.That(gold.IsDirty, Is.True, "시작 전의 dirty 상태로 돌아간다");
            Assert.That(items.IsDirty, Is.False);
            Assert.That(items.Version, Is.GreaterThan(versionBefore), "되돌린 뒤에도 파생값 캐시가 무효화되도록 버전은 오른다");
            Assert.That(system.IsInTransaction, Is.False);
        }

        [Test]
        public void Test_Transaction_Complete_KeepsChanges()
        {
            var gold = new CounterDomain("gold");
            SaveSystem system = Create(new MemorySaveStorage(), null, gold);
            system.Load();

            using (SaveTransaction transaction = system.BeginTransaction())
            {
                gold.Increment();
                transaction.Complete();
            }

            Assert.That(gold.Value, Is.EqualTo(1));
        }

        [Test]
        public void Test_Transaction_WritesAreDeferredUntilItEnds()
        {
            var storage = new MemorySaveStorage();
            var gold = new CounterDomain("gold");
            SaveSystem system = Create(storage, null, gold);
            system.Load();

            using (SaveTransaction transaction = system.BeginTransaction())
            {
                gold.Increment();
                Assert.That(system.WriteDirty(), Is.EqualTo(0), "반쯤 진행된 상태를 디스크에 쓰지 않는다");
                transaction.Complete();
            }

            Assert.That(system.WriteDirty(), Is.EqualTo(1));
            Assert.Throws<InvalidOperationException>(() =>
            {
                using (system.BeginTransaction())
                {
                    system.BeginTransaction();
                }
            });
        }

        // ---- 동기화 훅 ----

        [Test]
        public void Test_OnCommitted_ReportsOnlyChangedDomainsWithPayload()
        {
            var gold = new CounterDomain("gold");
            var items = new CounterDomain("items");
            SaveSystem system = Create(new MemorySaveStorage(), null, gold, items);
            system.Load();
            var commits = new List<SaveCommitInfo>();
            system.OnCommitted += commits.Add;

            gold.Increment();
            system.SaveNow();

            Assert.That(commits.Count, Is.EqualTo(1));
            Assert.That(commits[0].Generation, Is.EqualTo(1));
            Assert.That(commits[0].Domains.Select(domain => domain.Key), Is.EqualTo(new[] { "gold" }));
            Assert.That(commits[0].Domains[0].Payload, Is.Not.Empty);
        }

        [Test]
        public void Test_Snapshot_CaptureAndApply_MovesSaveBetweenDevices()
        {
            var deviceAGold = new CounterDomain("gold");
            SaveSystem deviceA = Create(new MemorySaveStorage(), null, deviceAGold);
            deviceA.Load();
            deviceAGold.Increment();
            deviceAGold.Increment();
            byte[] uploaded = deviceA.CaptureSnapshot().ToBytes();

            var deviceBStorage = new MemorySaveStorage();
            var deviceBGold = new CounterDomain("gold");
            SaveSystem deviceB = Create(deviceBStorage, null, deviceBGold);
            deviceB.Load();
            deviceB.ApplySnapshot(SaveSnapshot.FromBytes(uploaded));
            deviceB.SaveNow();

            var reloaded = new CounterDomain("gold");
            Create(deviceBStorage, null, reloaded).Load();
            Assert.That(deviceBGold.Value, Is.EqualTo(2));
            Assert.That(reloaded.Value, Is.EqualTo(2), "적용한 스냅샷은 다음 커밋으로 디스크에 남는다");
        }

        [Test]
        public void Test_Snapshot_UnregisteredEntriesAreCarriedToDisk()
        {
            var snapshot = new SaveSnapshot();
            var source = new CounterDomain("chunk-1");
            SaveSystem sourceSystem = Create(new MemorySaveStorage(), null, source);
            sourceSystem.Load();
            source.Increment();
            snapshot.Entries.AddRange(sourceSystem.CaptureSnapshot().Entries);

            var storage = new MemorySaveStorage();
            SaveSystem target = Create(storage, null);
            target.Load();
            target.ApplySnapshot(snapshot);
            target.SaveNow();

            var lateChunk = new CounterDomain("chunk-1");
            target.Register(lateChunk);
            Assert.That(lateChunk.Value, Is.EqualTo(1), "등록되지 않았던 도메인 항목도 저장돼 나중에 Register 하면 읽힌다");
        }

        // ---- 동적 도메인 ----

        [Test]
        public void Test_Dynamic_RegisterAfterLoad_ReadsStoredData()
        {
            var storage = new MemorySaveStorage();
            var chunk = new CounterDomain("chunk-3-4");
            SaveSystem first = Create(storage, null, chunk);
            first.Load();
            chunk.Increment();
            first.SaveNow();

            SaveSystem second = Create(storage, null);
            second.Load();
            var lateChunk = new CounterDomain("chunk-3-4");
            DomainLoadStatus status = second.Register(lateChunk);

            Assert.That(status, Is.EqualTo(DomainLoadStatus.Loaded));
            Assert.That(lateChunk.Value, Is.EqualTo(1));
            Assert.Throws<ArgumentException>(() => second.Register(new CounterDomain("chunk-3-4")));
        }

        [Test]
        public void Test_Dynamic_UnregisterSavesDirtyAndKeepsData()
        {
            var storage = new MemorySaveStorage();
            SaveSystem system = Create(storage, null);
            system.Load();
            var chunk = new CounterDomain("chunk-1");
            system.Register(chunk);
            chunk.Increment();

            system.Unregister(chunk);

            Assert.That(system.Domains.Contains(chunk), Is.False);
            var again = new CounterDomain("chunk-1");
            system.Register(again);
            Assert.That(again.Value, Is.EqualTo(1), "Unregister 는 dirty 를 먼저 저장하고, 데이터는 남긴다");
        }

        [Test]
        public void Test_Dynamic_ForgetRemovesDataAtNextCommit()
        {
            var storage = new MemorySaveStorage();
            var chunk = new CounterDomain("chunk-9");
            SaveSystem system = Create(storage, null, chunk);
            system.Load();
            chunk.Increment();
            system.SaveNow();

            system.Unregister(chunk);
            system.Forget("chunk-9");
            Assert.That(system.HasDirtyDomains, Is.True, "지우기 예약도 커밋할 변경이다");
            system.SaveNow();

            var reloaded = new CounterDomain("chunk-9");
            Assert.That(Create(storage, null, reloaded).Load().StatusOf("chunk-9"), Is.EqualTo(DomainLoadStatus.CreatedDefault));
            var live = new CounterDomain("live");
            system.Register(live);
            Assert.Throws<InvalidOperationException>(() => system.Forget("live"), "등록된 도메인은 Forget 할 수 없다");
        }
    }
}
