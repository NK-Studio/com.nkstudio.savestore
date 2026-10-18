using System;
using System.Buffers.Binary;
using System.Buffers;

namespace SaveStore.Storage
{
    /// <summary>
    /// 세이브 파일 한 개의 바이너리 배치(리틀 엔디언, 헤더 20바이트 + payload).
    /// <code>
    /// [매직 "NKSS" 4][포맷 버전 2][변환 Id 2][스키마 버전 4][payload 길이 4][CRC32 4][payload ...]
    /// </code>
    /// 포맷 버전은 이 헤더 배치 자체의 버전이고, 스키마 버전은 payload(도메인 레코드)의 버전이다 — 둘을 섞지 않는다.
    /// 변환 Id 는 payload 에 적용된 <see cref="ISavePayloadTransform"/> 이며 0 이면 평문이다. CRC 는 저장된(변환된) 바이트 기준이다.
    /// </summary>
    public static class SaveFileFormat
    {
        public const int HeaderSize = 20;
        public const ushort CurrentFormatVersion = 1;

        // "NKSS" — 다른 파일을 세이브로 오인하지 않기 위한 표식. 값 자체에 의미는 없다.
        private const uint Magic = 0x53534B4Eu;

        public static void Write(IBufferWriter<byte> writer, ReadOnlySpan<byte> payload, int schemaVersion, ushort transformId = 0)
        {
            Span<byte> header = writer.GetSpan(HeaderSize);
            BinaryPrimitives.WriteUInt32LittleEndian(header.Slice(0, 4), Magic);
            BinaryPrimitives.WriteUInt16LittleEndian(header.Slice(4, 2), CurrentFormatVersion);
            BinaryPrimitives.WriteUInt16LittleEndian(header.Slice(6, 2), transformId);
            BinaryPrimitives.WriteInt32LittleEndian(header.Slice(8, 4), schemaVersion);
            BinaryPrimitives.WriteInt32LittleEndian(header.Slice(12, 4), payload.Length);
            BinaryPrimitives.WriteUInt32LittleEndian(header.Slice(16, 4), Crc32.Compute(payload));
            writer.Advance(HeaderSize);

            Span<byte> body = writer.GetSpan(payload.Length);
            payload.CopyTo(body);
            writer.Advance(payload.Length);
        }

        /// <summary>
        /// 파일 전체 바이트를 검증하고 payload 위치를 돌려준다. 손상이면 false 와 이유.
        /// </summary>
        public static bool TryRead(ReadOnlySpan<byte> file, out int schemaVersion, out ushort transformId, out int payloadOffset, out int payloadLength, out string failureReason)
        {
            schemaVersion = 0;
            transformId = 0;
            payloadOffset = HeaderSize;
            payloadLength = 0;

            if (file.Length < HeaderSize)
            {
                failureReason = $"헤더보다 짧습니다({file.Length}B).";
                return false;
            }

            if (BinaryPrimitives.ReadUInt32LittleEndian(file.Slice(0, 4)) != Magic)
            {
                failureReason = "매직이 다릅니다.";
                return false;
            }

            ushort formatVersion = BinaryPrimitives.ReadUInt16LittleEndian(file.Slice(4, 2));
            if (formatVersion > CurrentFormatVersion)
            {
                failureReason = $"알 수 없는 파일 포맷 버전({formatVersion})입니다.";
                return false;
            }

            transformId = BinaryPrimitives.ReadUInt16LittleEndian(file.Slice(6, 2));
            schemaVersion = BinaryPrimitives.ReadInt32LittleEndian(file.Slice(8, 4));
            payloadLength = BinaryPrimitives.ReadInt32LittleEndian(file.Slice(12, 4));
            uint expectedCrc = BinaryPrimitives.ReadUInt32LittleEndian(file.Slice(16, 4));

            if (payloadLength < 0 || HeaderSize + payloadLength != file.Length)
            {
                failureReason = $"길이가 맞지 않습니다(헤더 {payloadLength}B, 실제 {file.Length - HeaderSize}B) — 쓰다 끊긴 파일일 수 있습니다.";
                return false;
            }

            if (Crc32.Compute(file.Slice(HeaderSize, payloadLength)) != expectedCrc)
            {
                failureReason = "체크섬이 맞지 않습니다.";
                return false;
            }

            failureReason = null;
            return true;
        }
    }
}
