using System;
using System.Buffers;
using MemoryPack;

namespace SaveStore
{
    /// <summary>
    /// 세이브 도메인 — 파일 하나에 대응하는 저장 단위. 게임 코드는 파생 클래스의 변경 메서드만 부르고 저장은 모른다.
    ///
    /// 직렬화·로드·self-heal 진입점은 <c>internal</c> 이다. 조회 코드가 "혹시 몰라" 복구를 다시 부를 수 없게
    /// 컴파일러가 막는다 — 불변식은 변경 메서드가 쓰는 순간 지키고, 복구는 로드에서 한 번만 돈다.
    /// </summary>
    public abstract class SaveDomain
    {
        /// <summary>
        /// 파일 이름 줄기. 출시 후 바꾸면 기존 세이브를 찾지 못한다 — 영구 식별자로 다룬다.
        /// </summary>
        public abstract string Key { get; }

        /// <summary>
        /// 저장 형식 버전. 추가만 하는 변경(VersionTolerant)은 올리지 않아도 되고, 깨지는 변경 때만 올리고 Migrate 를 구현한다.
        /// </summary>
        public virtual int SchemaVersion => 1;

        /// <summary>
        /// 바뀔 때마다 증가한다. 파생값 캐시(<see cref="VersionedComputed{TResult}"/>)의 키다. 저장되지 않는다.
        /// </summary>
        public int Version { get; private set; }

        public bool IsDirty { get; private set; }

        /// <summary>
        /// 변경 메서드 끝에서 부른다 — 다음 체크포인트에 이 도메인만 다시 쓴다.
        /// </summary>
        protected void MarkChanged()
        {
            Version++;
            IsDirty = true;
        }

        internal void ClearDirty()
        {
            IsDirty = false;
        }

        internal void MarkDirtyForRetry()
        {
            IsDirty = true;
        }

        internal void BumpVersionAfterLoad()
        {
            Version++;
            IsDirty = false;
        }

        internal void RestoreDirty(bool isDirty)
        {
            // 내용이 바뀌었으니 버전은 올려 파생값 캐시를 무효화하고, dirty 는 트랜잭션 시작 전 값으로 돌린다.
            Version++;
            IsDirty = isDirty;
        }

        internal abstract void WriteTo(IBufferWriter<byte> writer);

        /// <summary>
        /// 트랜잭션 되돌리기용 — 현재 레코드를 통째로 직렬화한다.
        /// </summary>
        internal abstract byte[] CaptureState();

        internal abstract void RestoreState(byte[] state, bool wasDirty);

        internal abstract void ReadFrom(ReadOnlySpan<byte> payload, int storedSchemaVersion);

        internal abstract void ResetToDefault();
    }

    /// <summary>
    /// MemoryPack 레코드 하나를 소유하는 도메인. <typeparamref name="TRecord"/> 는
    /// <c>[MemoryPackable(GenerateType.VersionTolerant)]</c> 여야 한다 — 기본 모드는 멤버가 바뀌면 이전 데이터를 읽지 못한다.
    /// </summary>
    public abstract class SaveDomain<TRecord> : SaveDomain where TRecord : class
    {
        private TRecord _record;

        /// <summary>
        /// 현재 레코드. <see cref="SaveSystem.Load"/> 전에는 접근할 수 없다(로드 순서 위반을 숨기지 않는다).
        /// </summary>
        protected TRecord Record
        {
            get
            {
                if (_record == null)
                {
                    throw new InvalidOperationException($"[SaveStore] '{Key}' 도메인을 SaveSystem.Load() 전에 사용했습니다.");
                }

                return _record;
            }
        }

        public bool IsLoaded => _record != null;

        /// <summary>
        /// 새 세이브(파일 없음)이거나 복구 불가로 초기화할 때의 기본 레코드.
        /// </summary>
        protected abstract TRecord CreateDefault();

        /// <summary>
        /// 로드 직후 한 번 — 클램프·정리 같은 self-heal. 런타임 조회 경로에서 부르지 않는다.
        /// </summary>
        protected virtual void Validate()
        {
        }

        /// <summary>
        /// 저장된 스키마가 현재보다 낮을 때. 기본 구현은 그대로 역직렬화한다(VersionTolerant 의 "추가만" 변경이면 충분).
        /// 깨지는 변경이라면 이전 레코드 타입(예: CatsRecordV1)으로 읽어 새 타입으로 옮겨 돌려준다.
        /// </summary>
        protected virtual TRecord Migrate(ReadOnlySpan<byte> payload, int fromSchemaVersion)
        {
            return MemoryPackSerializer.Deserialize<TRecord>(payload);
        }

        internal override void WriteTo(IBufferWriter<byte> writer)
        {
            MemoryPackSerializer.Serialize(writer, Record);
        }

        internal override void ReadFrom(ReadOnlySpan<byte> payload, int storedSchemaVersion)
        {
            // 더 새 앱이 쓴 세이브를 옛 앱이 읽으면 모르는 필드를 버린 채 다시 저장해 데이터를 잃는다 — 읽지 않는다.
            if (storedSchemaVersion > SchemaVersion)
            {
                throw new SaveDataException(
                    $"'{Key}' 세이브 스키마({storedSchemaVersion})가 앱 스키마({SchemaVersion})보다 새롭습니다.");
            }

            TRecord record = storedSchemaVersion == SchemaVersion
                ? MemoryPackSerializer.Deserialize<TRecord>(payload)
                : Migrate(payload, storedSchemaVersion);

            _record = record ?? throw new SaveDataException($"'{Key}' 세이브가 null 로 역직렬화됐습니다.");
            Validate();
            BumpVersionAfterLoad();
        }

        internal override void ResetToDefault()
        {
            _record = CreateDefault();
            Validate();
            BumpVersionAfterLoad();
        }

        internal override byte[] CaptureState()
        {
            return MemoryPackSerializer.Serialize(Record);
        }

        internal override void RestoreState(byte[] state, bool wasDirty)
        {
            _record = MemoryPackSerializer.Deserialize<TRecord>(state)
                      ?? throw new SaveDataException($"'{Key}' 트랜잭션 되돌리기 상태가 null 로 역직렬화됐습니다.");
            RestoreDirty(wasDirty);
        }
    }
}
