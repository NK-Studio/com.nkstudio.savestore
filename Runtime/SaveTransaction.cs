using System;

namespace SaveStore
{
    /// <summary>
    /// 메모리 트랜잭션. 시작할 때 대상 도메인의 상태를 떠 두고, <see cref="Complete"/> 없이 <see cref="Dispose"/> 되면 그 상태로 되돌린다.
    /// 디스크 커밋은 원래 원자적이지만, 여러 단계로 된 행동이 중간에 실패하면 메모리에 반쯤 바뀐 상태가 남는다 — 그것을 막는다.
    /// 트랜잭션이 열려 있는 동안 <see cref="SaveSystem.WriteDirty"/> 는 아무것도 쓰지 않는다.
    /// </summary>
    public sealed class SaveTransaction : IDisposable
    {
        private readonly SaveSystem _owner;
        private readonly SaveDomain[] _domains;
        private readonly byte[][] _states;
        private readonly bool[] _wasDirty;
        private bool _isCompleted;
        private bool _isDisposed;

        internal SaveTransaction(SaveSystem owner, SaveDomain[] domains)
        {
            _owner = owner;
            _domains = domains;
            _states = new byte[domains.Length][];
            _wasDirty = new bool[domains.Length];
            for (int i = 0; i < domains.Length; i++)
            {
                _states[i] = domains[i].CaptureState();
                _wasDirty[i] = domains[i].IsDirty;
            }
        }

        public bool IsCompleted => _isCompleted;

        public void Complete()
        {
            if (_isDisposed == true)
            {
                throw new ObjectDisposedException(nameof(SaveTransaction));
            }

            _isCompleted = true;
        }

        public void Dispose()
        {
            if (_isDisposed == true)
            {
                return;
            }

            _isDisposed = true;
            try
            {
                if (_isCompleted == false)
                {
                    for (int i = 0; i < _domains.Length; i++)
                    {
                        _domains[i].RestoreState(_states[i], _wasDirty[i]);
                    }
                }
            }
            finally
            {
                _owner.EndTransaction(this);
            }
        }
    }
}
