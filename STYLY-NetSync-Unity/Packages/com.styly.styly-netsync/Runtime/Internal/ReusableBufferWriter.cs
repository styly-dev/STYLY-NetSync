// ReusableBufferWriter.cs
// Utility to manage a pooled byte buffer with a MemoryStream and BinaryWriter.
// This consolidates the common buffer management logic across managers to reduce duplication.
using System;
using System.Buffers;
using System.IO;

namespace Styly.NetSync
{
    /// <summary>
    /// Reusable buffer + stream + writer backed by ArrayPool<byte>.
    /// Call EnsureCapacity before writes and set Stream.Position = 0 to reuse.
    /// Dispose when no longer needed to return the rented buffer to the pool.
    /// </summary>
    internal sealed class ReusableBufferWriter : IDisposable
    {
        private readonly ArrayPool<byte> _pool = ArrayPool<byte>.Shared;
        private byte[] _buffer;
        private int _capacity;

        public MemoryStream Stream { get; private set; }
        public BinaryWriter Writer { get; private set; }

        public int Capacity => _capacity;

        public ReusableBufferWriter(int initialCapacity)
        {
            _capacity = Math.Max(1, initialCapacity);
            _buffer = _pool.Rent(_capacity);
            // Use publiclyVisible=true so GetBuffer() can be used to access the underlying array.
            Stream = new MemoryStream(_buffer, 0, _buffer.Length, true, true);
            Writer = new BinaryWriter(Stream);
        }

        /// <summary>
        /// Ensure the underlying buffer can hold at least <paramref name="required"/> bytes.
        /// Recreates the MemoryStream and BinaryWriter when resized.
        /// </summary>
        public void EnsureCapacity(int required)
        {
            if (required <= _capacity) return;

            var newCap = Math.Max(_capacity * 2, required);
            var newBuf = _pool.Rent(newCap);

            // Dispose current writer/stream and return previous buffer to the pool.
            Writer?.Dispose();
            Stream?.Dispose();
            if (_buffer != null)
            {
                _pool.Return(_buffer);
            }

            _buffer = newBuf;
            _capacity = newCap;
            Stream = new MemoryStream(_buffer, 0, _buffer.Length, true, true);
            Writer = new BinaryWriter(Stream);
        }

        /// <summary>
        /// Access the current underlying pooled buffer. Length is managed via Stream.Position.
        /// </summary>
        public byte[] GetBufferUnsafe() => Stream.GetBuffer();

        public void Dispose()
        {
            try { Writer?.Dispose(); } catch { }
            try { Stream?.Dispose(); } catch { }
            if (_buffer != null)
            {
                _pool.Return(_buffer);
                _buffer = null;
            }
        }
    }
}

