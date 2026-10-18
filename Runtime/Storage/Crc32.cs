using System;

namespace SaveStore.Storage
{
    /// <summary>
    /// CRC-32(IEEE 802.3, 다항식 0xEDB88320). 세이브 파일 손상 감지용이다 — 변조 방지는 아니다(필요하면 HMAC 을 더한다).
    /// 예: "123456789" → 0xCBF43926.
    /// </summary>
    public static class Crc32
    {
        private static readonly uint[] Table = CreateTable();

        public static uint Compute(ReadOnlySpan<byte> data)
        {
            uint crc = 0xFFFFFFFFu;
            for (int i = 0; i < data.Length; i++)
            {
                crc = Table[(crc ^ data[i]) & 0xFF] ^ (crc >> 8);
            }

            return crc ^ 0xFFFFFFFFu;
        }

        private static uint[] CreateTable()
        {
            var table = new uint[256];
            for (uint i = 0; i < table.Length; i++)
            {
                uint value = i;
                for (int bit = 0; bit < 8; bit++)
                {
                    value = (value & 1) == 1 ? 0xEDB88320u ^ (value >> 1) : value >> 1;
                }

                table[i] = value;
            }

            return table;
        }
    }
}
