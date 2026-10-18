using System;
using System.Threading;

namespace SaveStore
{
    /// <summary>
    /// 시각 공급자. 커밋 시각 기록과, 시간에 따라 변하는 게임 값(스태미나 등)의 기준 시각이 이것을 쓴다.
    /// 기기 시계는 사용자가 바꿀 수 있으므로 보상·진행이 걸린 판정은 서버 시각을 반영한 구현(<see cref="OffsetClock"/>)을 넘긴다.
    /// 쓰기 스레드에서도 읽히므로 구현은 스레드 안전해야 한다.
    /// </summary>
    public interface IClock
    {
        long UtcNowUnixMs { get; }
    }

    public static class ClockExtensions
    {
        public static long UtcNowUnixSecs(this IClock clock)
        {
            return clock.UtcNowUnixMs / 1000;
        }
    }

    /// <summary>
    /// 기기 시계. 조작될 수 있다 — 보상 판정에는 <see cref="OffsetClock"/> 을 쓴다.
    /// </summary>
    public sealed class SystemClock : IClock
    {
        public static readonly SystemClock Instance = new SystemClock();

        private SystemClock()
        {
        }

        public long UtcNowUnixMs => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
    }

    /// <summary>
    /// 서버 시각에 맞춘 시계. 서버 응답을 받을 때마다 <see cref="SyncTo"/> 로 오프셋을 갱신하고, 그 사이에는 기준 시계 + 오프셋으로 흐른다.
    /// 오프라인이면 마지막 오프셋을 유지한다(서버 확인이 불가능할 때의 불가피한 폴백).
    /// XXX: 기준 시계를 기기 시계로 두면 동기화 사이에 기기 시계를 바꾸는 조작에는 여전히 취약하다 — 엄격하게 막으려면 단조 시계를 기준으로 준다.
    /// </summary>
    public sealed class OffsetClock : IClock
    {
        private readonly IClock _baseClock;
        private long _offsetMs;

        public OffsetClock(IClock baseClock = null)
        {
            _baseClock = baseClock ?? SystemClock.Instance;
        }

        public bool IsSynced { get; private set; }

        public long OffsetMs => Interlocked.Read(ref _offsetMs);

        public long UtcNowUnixMs => _baseClock.UtcNowUnixMs + Interlocked.Read(ref _offsetMs);

        public void SyncTo(long serverUtcNowUnixMs)
        {
            Interlocked.Exchange(ref _offsetMs, serverUtcNowUnixMs - _baseClock.UtcNowUnixMs);
            IsSynced = true;
        }
    }

    /// <summary>
    /// 손으로 움직이는 시계 — 테스트에서 시간 경과를 재현한다.
    /// </summary>
    public sealed class ManualClock : IClock
    {
        private long _utcNowUnixMs;

        public ManualClock(long startUnixMs = 1_700_000_000_000)
        {
            _utcNowUnixMs = startUnixMs;
        }

        public long UtcNowUnixMs => Interlocked.Read(ref _utcNowUnixMs);

        public void Set(long utcNowUnixMs)
        {
            Interlocked.Exchange(ref _utcNowUnixMs, utcNowUnixMs);
        }

        public void Advance(long milliseconds)
        {
            Interlocked.Add(ref _utcNowUnixMs, milliseconds);
        }
    }
}
