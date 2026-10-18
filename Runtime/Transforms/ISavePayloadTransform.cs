using System;
using System.Buffers;

namespace SaveStore.Storage
{
    /// <summary>
    /// 저장 직전 blob 변환(압축·암호화·서명). blob 헤더에 <see cref="Id"/> 가 기록돼, 읽을 때 같은 변환으로 되돌린다.
    /// CRC 는 변환된 바이트에 대해 계산되므로 "매체 손상"은 변환 전에 걸러지고, 변환은 "위조·키 불일치"만 판단한다.
    ///
    /// 구현 계약:
    /// <list type="bullet">
    /// <item><see cref="Id"/> 는 1~65535 의 영구 식별자. 0 은 "변환 없음"으로 예약돼 있다. 알고리즘·키 형식을 바꾸면 Id 도 바꾼다.</item>
    /// <item>쓰기 스레드와 메인 스레드에서 번갈아(동시 아님) 불린다 — 호출 간 공유 상태를 두지 않는다.</item>
    /// </list>
    /// XXX: 클라이언트에 든 키는 앱을 풀면 꺼낼 수 있다. 암호화·서명은 "손쉬운 세이브 수정"을 막을 뿐, 결정적인 변조 방지는 서버 검증이 맡는다.
    /// </summary>
    public interface ISavePayloadTransform
    {
        ushort Id { get; }

        void Encode(ReadOnlySpan<byte> plain, IBufferWriter<byte> output);

        /// <summary>
        /// 위조·키 불일치·형식 오류면 false 와 이유.
        /// </summary>
        bool TryDecode(ReadOnlySpan<byte> stored, IBufferWriter<byte> output, out string failureReason);
    }

    internal static class BufferWriterExtensions
    {
        public static void WriteBytes(this IBufferWriter<byte> writer, ReadOnlySpan<byte> bytes)
        {
            Span<byte> destination = writer.GetSpan(bytes.Length);
            bytes.CopyTo(destination);
            writer.Advance(bytes.Length);
        }
    }
}
