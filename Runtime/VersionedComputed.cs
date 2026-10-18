using System;

namespace SaveStore
{
    /// <summary>
    /// 도메인 버전을 키로 한 파생값 캐시. 의존 도메인 중 하나라도 바뀌었을 때만 다시 계산한다.
    /// 예: 전투력 = f(인벤토리) — 재화만 바뀌면 다시 계산하지 않는다. 단, 무효화는 도메인 단위라서
    /// 같은 도메인 안의 무관한 고빈도 값(시간에 따라 변하는 값 등)은 별도 도메인으로 떼거나 시간 공식으로 저장해 버전을 흔들지 않게 한다.
    /// </summary>
    public sealed class VersionedComputed<TResult>
    {
        private readonly Func<TResult> _compute;
        private readonly SaveDomain[] _dependencies;
        private readonly int[] _seenVersions;
        private bool _hasValue;
        private TResult _value;

        public VersionedComputed(Func<TResult> compute, params SaveDomain[] dependencies)
        {
            _compute = compute ?? throw new ArgumentNullException(nameof(compute));
            _dependencies = dependencies ?? throw new ArgumentNullException(nameof(dependencies));
            _seenVersions = new int[_dependencies.Length];
        }

        public int ComputeCount { get; private set; }

        public TResult Value
        {
            get
            {
                if (_hasValue == true && IsStale() == false)
                {
                    return _value;
                }

                _value = _compute();
                for (int i = 0; i < _dependencies.Length; i++)
                {
                    _seenVersions[i] = _dependencies[i].Version;
                }

                _hasValue = true;
                ComputeCount++;
                return _value;
            }
        }

        private bool IsStale()
        {
            for (int i = 0; i < _dependencies.Length; i++)
            {
                if (_dependencies[i].Version != _seenVersions[i])
                {
                    return true;
                }
            }

            return false;
        }
    }
}
