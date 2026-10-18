using SaveStore.Storage;

namespace SaveStore
{
    /// <summary>
    /// SaveSystem·SaveRepository 동작 옵션. 기본값 그대로면 기기 시계·평문·백그라운드 쓰기다.
    /// </summary>
    public sealed class SaveOptions
    {
        /// <summary>
        /// 파일 쓰기를 쓰기 스레드에서 한다. WebGL 은 이 값과 관계없이 같은 스레드에서 쓴다.
        /// </summary>
        public bool IsBackgroundWriteEnabled = true;

        /// <summary>
        /// 커밋 시각을 기록하는 시계. 슬롯 목록의 "마지막 저장 시각"과 클라우드 충돌 판정에 쓰인다.
        /// </summary>
        public IClock Clock = SystemClock.Instance;

        /// <summary>
        /// blob 을 저장 전에 변환한다(압축·암호화·서명). null 이면 평문.
        /// </summary>
        public ISavePayloadTransform PayloadTransform;

        /// <summary>
        /// 변환이 설정돼 있어도 변환 없이 저장된(평문) blob 을 읽을지. 평문으로 저장하던 버전에서 암호화로 넘어갈 때만 켠다 —
        /// 켜 두면 사용자가 서명 없는 평문 파일을 넣어 서명 검사를 우회할 수 있다.
        /// </summary>
        public bool AcceptsUntransformedBlobs;

        internal static readonly SaveOptions Default = new SaveOptions();
    }
}
