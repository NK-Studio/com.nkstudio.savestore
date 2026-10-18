using System;
using System.Buffers;

namespace SaveStore.Storage
{
    /// <summary>
    /// 재사용 가능한 <see cref="IBufferWriter{T}"/>. .NET Standard 2.1 에는 ArrayBufferWriter 가 없어 직접 둔다.
    /// <see cref="Clear"/> 는 내부 배열을 유지한다 — 체크포인트마다 직렬화해도 버퍼가 커진 뒤로는 할당이 없다.
    /// 메인 스레드 전용.
    /// </summary>
    public sealed class PooledBufferWriter : IBufferWriter<byte>, IDisposable
    {
        private const int MinimumGrowBytes = 256;

        private byte[] _buffer;
        private int _writtenCount;

        public PooledBufferWriter(int initialCapacityBytes = 64 * 1024)
        {
            _buffer = ArrayPool<byte>.Shared.Rent(Math.Max(MinimumGrowBytes, initialCapacityBytes));
        }

        public int WrittenCount => _writtenCount;

        public ReadOnlySpan<byte> WrittenSpan => new ReadOnlySpan<byte>(_buffer, 0, _writtenCount);

        public void Clear()
        {
            _writtenCount = 0;
        }

        public void Advance(int count)
        {
            if (count < 0 || _writtenCount + count > _buffer.Length)
            {
                throw new ArgumentOutOfRangeException(nameof(count));
            }

            _writtenCount += count;
        }

        public Memory<byte> GetMemory(int sizeHint = 0)
        {
            EnsureCapacity(sizeHint);
            return new Memory<byte>(_buffer, _writtenCount, _buffer.Length - _writtenCount);
        }

        public Span<byte> GetSpan(int sizeHint = 0)
        {
            EnsureCapacity(sizeHint);
            return new Span<byte>(_buffer, _writtenCount, _buffer.Length - _writtenCount);
        }

        public void Dispose()
        {
            if (_buffer == null)
            {
                return;
            }

            ArrayPool<byte>.Shared.Return(_buffer);
            _buffer = null;
        }

        private void EnsureCapacity(int sizeHint)
        {
            int required = Math.Max(1, sizeHint);
            if (_buffer.Length - _writtenCount >= required)
            {
                return;
            }

            int newSize = Math.Max(_buffer.Length * 2, _writtenCount + Math.Max(required, MinimumGrowBytes));
            byte[] grown = ArrayPool<byte>.Shared.Rent(newSize);
            Buffer.BlockCopy(_buffer, 0, grown, 0, _writtenCount);
            ArrayPool<byte>.Shared.Return(_buffer);
            _buffer = grown;
        }
    }
}
