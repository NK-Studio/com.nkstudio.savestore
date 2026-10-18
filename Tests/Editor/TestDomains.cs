using System;
using System.Collections.Generic;
using MemoryPack;

namespace SaveStore.Tests
{
    [MemoryPackable(GenerateType.VersionTolerant)]
    public sealed partial class CounterRecord
    {
        [MemoryPackOrder(0)] public int Value;
        [MemoryPackOrder(1)] public List<string> Tags = new();
    }

    public sealed class CounterDomain : SaveDomain<CounterRecord>
    {
        private readonly string _key;

        public CounterDomain(string key)
        {
            _key = key;
        }

        public override string Key => _key;

        public int Value => Record.Value;

        public int ValidateCount { get; private set; }

        public void Increment()
        {
            Record.Value++;
            MarkChanged();
        }

        protected override CounterRecord CreateDefault()
        {
            return new CounterRecord();
        }

        protected override void Validate()
        {
            ValidateCount++;
            if (Record.Value < 0)
            {
                Record.Value = 0;
            }
        }
    }

    // ---- 스키마 진화: V1 으로 쓴 세이브를 필드가 추가된 V2 가 읽는다(같은 키·같은 스키마 버전, "추가만" 변경) ----

    [MemoryPackable(GenerateType.VersionTolerant)]
    public sealed partial class ProfileRecordV1
    {
        [MemoryPackOrder(0)] public string Nickname;
    }

    [MemoryPackable(GenerateType.VersionTolerant)]
    public sealed partial class ProfileRecordV2
    {
        [MemoryPackOrder(0)] public string Nickname;
        [MemoryPackOrder(1)] public int FrameId = 7;
        [MemoryPackOrder(2)] public List<string> Tags = new();
    }

    public sealed class ProfileDomainV1 : SaveDomain<ProfileRecordV1>
    {
        public override string Key => "profile";

        public void SetNickname(string nickname)
        {
            Record.Nickname = nickname;
            MarkChanged();
        }

        protected override ProfileRecordV1 CreateDefault()
        {
            return new ProfileRecordV1();
        }
    }

    public sealed class ProfileDomainV2 : SaveDomain<ProfileRecordV2>
    {
        public override string Key => "profile";

        public string Nickname => Record.Nickname;

        public int FrameId => Record.FrameId;

        public List<string> Tags => Record.Tags;

        public bool TagsWereNullBeforeValidate { get; private set; }

        protected override ProfileRecordV2 CreateDefault()
        {
            return new ProfileRecordV2();
        }

        protected override void Validate()
        {
            TagsWereNullBeforeValidate = Record.Tags == null;
            Record.Tags ??= new List<string>();
        }
    }

    /// <summary>
    /// 스키마 2 로 올라간 도메인 — 깨지는 변경(Nickname → DisplayName)을 Migrate 로 옮긴다.
    /// </summary>
    [MemoryPackable(GenerateType.VersionTolerant)]
    public sealed partial class ProfileRecordV3
    {
        [MemoryPackOrder(0)] public string DisplayName;
    }

    public sealed class ProfileDomainV3 : SaveDomain<ProfileRecordV3>
    {
        public override string Key => "profile";

        public override int SchemaVersion => 2;

        public string DisplayName => Record.DisplayName;

        protected override ProfileRecordV3 CreateDefault()
        {
            return new ProfileRecordV3();
        }

        protected override ProfileRecordV3 Migrate(ReadOnlySpan<byte> payload, int fromSchemaVersion)
        {
            ProfileRecordV1 old = MemoryPackSerializer.Deserialize<ProfileRecordV1>(payload);
            return new ProfileRecordV3 { DisplayName = old?.Nickname };
        }
    }
}
