using UnityEngine;

namespace SaveStore
{
    /// <summary>
    /// 체크포인트 정책 — "언제 쓰나"만 담당한다. 값이 바뀔 때마다 쓰지 않는다.
    /// <list type="bullet">
    /// <item>주기: <see cref="_periodicIntervalSecs"/>마다, dirty 도메인이 있을 때만(포그라운드 크래시 시 손실 상한).</item>
    /// <item>라이프사이클: 백그라운드 전환·포커스 상실·종료 시 즉시 쓰고 기다린다(모바일은 백그라운드에서만 죽는다).</item>
    /// <item>중요 거래: 호출자가 <see cref="RequestCritical"/>(결제·보상·재화 차감 직후).</item>
    /// </list>
    /// </summary>
    public sealed class SaveScheduler : MonoBehaviour
    {
        // 60초 = 포그라운드에서 크래시가 났을 때 잃을 수 있는 최대 진행. 짧을수록 안전하지만 직렬화 횟수가 는다.
        [SerializeField, Min(5f)] private float _periodicIntervalSecs = 60f;

        private SaveSystem _saveSystem;
        private float _lastCheckpointTime;

        public float PeriodicIntervalSecs
        {
            get => _periodicIntervalSecs;
            set => _periodicIntervalSecs = Mathf.Max(5f, value);
        }

        public int CheckpointCount { get; private set; }

        public float SecondsUntilNextCheckpoint => Mathf.Max(0f, _periodicIntervalSecs - (Time.unscaledTime - _lastCheckpointTime));

        public void Bind(SaveSystem saveSystem)
        {
            _saveSystem = saveSystem;
            _lastCheckpointTime = Time.unscaledTime;
        }

        public void RequestCritical()
        {
            if (_saveSystem == null)
            {
                return;
            }

            _saveSystem.SaveNow();
            MarkCheckpoint();
        }

        private void Update()
        {
            if (_saveSystem == null)
            {
                return;
            }

            // 쓰기 스레드에서 끝난 커밋·실패를 메인 스레드 이벤트로 넘긴다(OnCommitted 로 클라우드 업로드 등).
            _saveSystem.PumpEvents();
            if (Time.unscaledTime - _lastCheckpointTime < _periodicIntervalSecs)
            {
                return;
            }

            if (_saveSystem.HasDirtyDomains == true)
            {
                _saveSystem.WriteDirty();
                CheckpointCount++;
            }

            _lastCheckpointTime = Time.unscaledTime;
        }

        private void OnApplicationPause(bool isPaused)
        {
            if (isPaused == true)
            {
                RequestCritical();
            }
        }

        private void OnApplicationFocus(bool hasFocus)
        {
            if (hasFocus == false)
            {
                RequestCritical();
            }
        }

        private void OnApplicationQuit()
        {
            RequestCritical();
        }

        private void MarkCheckpoint()
        {
            CheckpointCount++;
            _lastCheckpointTime = Time.unscaledTime;
        }
    }
}
