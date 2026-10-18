using System;

namespace SaveStore
{
    /// <summary>
    /// 세이브 파일·형식 계약 위반(손상, 미래 스키마, 잘못된 헤더). 조용히 넘기지 않고 호출자가 판단하게 던진다.
    /// </summary>
    public sealed class SaveDataException : Exception
    {
        public SaveDataException(string message) : base("[SaveStore] " + message)
        {
        }

        public SaveDataException(string message, Exception innerException) : base("[SaveStore] " + message, innerException)
        {
        }
    }
}
