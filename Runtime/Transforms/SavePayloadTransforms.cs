using System;
using System.Buffers;
using System.Buffers.Binary;
using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;

namespace SaveStore.Storage
{
    /// <summary>
    /// Deflate 압축. 형식: [원본 길이 4][deflate 스트림]. 반복 구조가 많은 세이브에서 크기를 크게 줄인다.
    /// </summary>
    public sealed class DeflateCompressionTransform : ISavePayloadTransform
    {
        public const ushort DefaultId = 1;

        // 원본 크기 상한 — 손상·악의적 파일이 거대한 버퍼 할당을 일으키지 않게 막는다. 일반적인 세이브보다 충분히 크게 잡았다.
        private const int MaxPlainBytes = 256 * 1024 * 1024;

        public DeflateCompressionTransform(ushort id = DefaultId)
        {
            Id = id;
        }

        public ushort Id { get; }

        public void Encode(ReadOnlySpan<byte> plain, IBufferWriter<byte> output)
        {
            Span<byte> lengthPrefix = stackalloc byte[4];
            BinaryPrimitives.WriteInt32LittleEndian(lengthPrefix, plain.Length);
            output.WriteBytes(lengthPrefix);

            using (var compressed = new MemoryStream())
            {
                using (var deflate = new DeflateStream(compressed, CompressionLevel.Fastest, true))
                {
                    deflate.Write(plain);
                }

                output.WriteBytes(new ReadOnlySpan<byte>(compressed.GetBuffer(), 0, (int)compressed.Length));
            }
        }

        public bool TryDecode(ReadOnlySpan<byte> stored, IBufferWriter<byte> output, out string failureReason)
        {
            if (stored.Length < 4)
            {
                failureReason = "압축 헤더가 없습니다.";
                return false;
            }

            int plainLength = BinaryPrimitives.ReadInt32LittleEndian(stored.Slice(0, 4));
            if (plainLength < 0 || plainLength > MaxPlainBytes)
            {
                failureReason = $"원본 길이가 비정상입니다({plainLength}).";
                return false;
            }

            byte[] compressed = stored.Slice(4).ToArray();
            using (var source = new MemoryStream(compressed, false))
            using (var deflate = new DeflateStream(source, CompressionMode.Decompress))
            {
                Span<byte> destination = output.GetSpan(plainLength);
                int total = 0;
                while (total < plainLength)
                {
                    int read = deflate.Read(destination.Slice(total, plainLength - total));
                    if (read == 0)
                    {
                        failureReason = "압축 데이터가 중간에 끝났습니다.";
                        return false;
                    }

                    total += read;
                }

                output.Advance(plainLength);
            }

            failureReason = null;
            return true;
        }
    }

    /// <summary>
    /// HMAC-SHA256 서명만 붙인다(내용은 평문). 형식: [payload][HMAC 32]. 세이브를 손으로 고치면 로드가 거부된다.
    /// </summary>
    public sealed class HmacSignatureTransform : ISavePayloadTransform
    {
        public const ushort DefaultId = 2;
        private const int MacSize = 32;

        private readonly byte[] _key;

        public HmacSignatureTransform(byte[] key, ushort id = DefaultId)
        {
            if (key == null || key.Length < 16)
            {
                throw new ArgumentException("HMAC 키는 16바이트 이상이어야 합니다.", nameof(key));
            }

            _key = (byte[])key.Clone();
            Id = id;
        }

        public ushort Id { get; }

        public void Encode(ReadOnlySpan<byte> plain, IBufferWriter<byte> output)
        {
            output.WriteBytes(plain);
            output.WriteBytes(ComputeMac(_key, plain));
        }

        public bool TryDecode(ReadOnlySpan<byte> stored, IBufferWriter<byte> output, out string failureReason)
        {
            if (stored.Length < MacSize)
            {
                failureReason = "서명이 없습니다.";
                return false;
            }

            ReadOnlySpan<byte> payload = stored.Slice(0, stored.Length - MacSize);
            if (CryptoUtil.FixedTimeEquals(ComputeMac(_key, payload), stored.Slice(stored.Length - MacSize)) == false)
            {
                failureReason = "서명이 맞지 않습니다(수정됐거나 키가 다릅니다).";
                return false;
            }

            output.WriteBytes(payload);
            failureReason = null;
            return true;
        }

        private static byte[] ComputeMac(byte[] key, ReadOnlySpan<byte> data)
        {
            using (var hmac = new HMACSHA256(key))
            {
                return hmac.ComputeHash(data.ToArray());
            }
        }
    }

    /// <summary>
    /// AES-256-CBC 암호화 + HMAC-SHA256 (encrypt-then-MAC). 형식: [IV 16][암호문][HMAC 32 (IV+암호문 대상)].
    /// 마스터 키 하나에서 암호화 키와 MAC 키를 따로 유도한다 — 같은 키를 두 용도로 쓰지 않기 위해서다.
    /// </summary>
    public sealed class AesHmacEncryptionTransform : ISavePayloadTransform
    {
        public const ushort DefaultId = 3;
        private const int IvSize = 16;
        private const int MacSize = 32;

        private readonly byte[] _encryptionKey;
        private readonly byte[] _macKey;

        public AesHmacEncryptionTransform(byte[] masterKey, ushort id = DefaultId)
        {
            if (masterKey == null || masterKey.Length < 16)
            {
                throw new ArgumentException("마스터 키는 16바이트 이상이어야 합니다.", nameof(masterKey));
            }

            _encryptionKey = Derive(masterKey, "savestore-enc");
            _macKey = Derive(masterKey, "savestore-mac");
            Id = id;
        }

        public ushort Id { get; }

        public void Encode(ReadOnlySpan<byte> plain, IBufferWriter<byte> output)
        {
            using (Aes aes = Aes.Create())
            {
                aes.Key = _encryptionKey;
                aes.GenerateIV();
                aes.Mode = CipherMode.CBC;
                aes.Padding = PaddingMode.PKCS7;
                byte[] cipher;
                using (ICryptoTransform encryptor = aes.CreateEncryptor())
                {
                    byte[] plainArray = plain.ToArray();
                    cipher = encryptor.TransformFinalBlock(plainArray, 0, plainArray.Length);
                }

                byte[] ivAndCipher = new byte[IvSize + cipher.Length];
                Buffer.BlockCopy(aes.IV, 0, ivAndCipher, 0, IvSize);
                Buffer.BlockCopy(cipher, 0, ivAndCipher, IvSize, cipher.Length);
                output.WriteBytes(ivAndCipher);
                output.WriteBytes(ComputeMac(ivAndCipher));
            }
        }

        public bool TryDecode(ReadOnlySpan<byte> stored, IBufferWriter<byte> output, out string failureReason)
        {
            if (stored.Length < IvSize + MacSize + 16)
            {
                failureReason = "암호문이 너무 짧습니다.";
                return false;
            }

            byte[] ivAndCipher = stored.Slice(0, stored.Length - MacSize).ToArray();
            if (CryptoUtil.FixedTimeEquals(ComputeMac(ivAndCipher), stored.Slice(stored.Length - MacSize)) == false)
            {
                failureReason = "서명이 맞지 않습니다(수정됐거나 키가 다릅니다).";
                return false;
            }

            using (Aes aes = Aes.Create())
            {
                aes.Key = _encryptionKey;
                byte[] iv = new byte[IvSize];
                Buffer.BlockCopy(ivAndCipher, 0, iv, 0, IvSize);
                aes.IV = iv;
                aes.Mode = CipherMode.CBC;
                aes.Padding = PaddingMode.PKCS7;
                try
                {
                    using (ICryptoTransform decryptor = aes.CreateDecryptor())
                    {
                        byte[] plain = decryptor.TransformFinalBlock(ivAndCipher, IvSize, ivAndCipher.Length - IvSize);
                        output.WriteBytes(plain);
                    }
                }
                catch (CryptographicException exception)
                {
                    failureReason = "복호화 실패: " + exception.Message;
                    return false;
                }
            }

            failureReason = null;
            return true;
        }

        private byte[] ComputeMac(byte[] data)
        {
            using (var hmac = new HMACSHA256(_macKey))
            {
                return hmac.ComputeHash(data);
            }
        }

        private static byte[] Derive(byte[] masterKey, string purpose)
        {
            using (var hmac = new HMACSHA256(masterKey))
            {
                return hmac.ComputeHash(Encoding.UTF8.GetBytes(purpose));
            }
        }
    }

    /// <summary>
    /// 여러 변환을 순서대로 적용한다(복원은 역순). 예: 압축 → 암호화. 체인 전체가 하나의 <see cref="Id"/> 를 가진다.
    /// </summary>
    public sealed class TransformChain : ISavePayloadTransform
    {
        private readonly ISavePayloadTransform[] _steps;

        public TransformChain(ushort id, params ISavePayloadTransform[] steps)
        {
            if (id == 0)
            {
                throw new ArgumentException("Id 0 은 '변환 없음'으로 예약돼 있습니다.", nameof(id));
            }

            if (steps == null || steps.Length == 0)
            {
                throw new ArgumentException("변환이 하나 이상 필요합니다.", nameof(steps));
            }

            Id = id;
            _steps = (ISavePayloadTransform[])steps.Clone();
        }

        public ushort Id { get; }

        public void Encode(ReadOnlySpan<byte> plain, IBufferWriter<byte> output)
        {
            byte[] current = plain.ToArray();
            for (int i = 0; i < _steps.Length; i++)
            {
                var stepOutput = new PooledBufferWriter(Math.Max(256, current.Length));
                _steps[i].Encode(current, stepOutput);
                current = stepOutput.WrittenSpan.ToArray();
                stepOutput.Dispose();
            }

            output.WriteBytes(current);
        }

        public bool TryDecode(ReadOnlySpan<byte> stored, IBufferWriter<byte> output, out string failureReason)
        {
            byte[] current = stored.ToArray();
            for (int i = _steps.Length - 1; i >= 0; i--)
            {
                var stepOutput = new PooledBufferWriter(Math.Max(256, current.Length));
                bool isDecoded = _steps[i].TryDecode(current, stepOutput, out failureReason);
                current = stepOutput.WrittenSpan.ToArray();
                stepOutput.Dispose();
                if (isDecoded == false)
                {
                    return false;
                }
            }

            output.WriteBytes(current);
            failureReason = null;
            return true;
        }
    }

    internal static class CryptoUtil
    {
        /// <summary>
        /// 길이가 같으면 내용과 무관하게 같은 시간 — 서명 비교에서 타이밍으로 정답을 추측하지 못하게 한다.
        /// </summary>
        public static bool FixedTimeEquals(ReadOnlySpan<byte> left, ReadOnlySpan<byte> right)
        {
            if (left.Length != right.Length)
            {
                return false;
            }

            int difference = 0;
            for (int i = 0; i < left.Length; i++)
            {
                difference |= left[i] ^ right[i];
            }

            return difference == 0;
        }
    }
}
