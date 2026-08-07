// ReusableBufferWriterTests.cs - EditMode tests for the pooled buffer/stream/writer helper.
using NUnit.Framework;

namespace Styly.NetSync.Tests
{
    public class ReusableBufferWriterTests
    {
        [Test]
        public void Constructor_RentsAtLeastRequestedCapacity()
        {
            using var buffer = new ReusableBufferWriter(64);
            Assert.GreaterOrEqual(buffer.Capacity, 64);
            Assert.IsNotNull(buffer.Stream);
            Assert.IsNotNull(buffer.Writer);
        }

        [Test]
        public void Constructor_NonPositiveCapacity_IsClampedToOne()
        {
            using var buffer = new ReusableBufferWriter(0);
            Assert.GreaterOrEqual(buffer.Capacity, 1);
        }

        [Test]
        public void WriteAndReuse_ViaPositionReset_ProducesExpectedBytes()
        {
            using var buffer = new ReusableBufferWriter(16);

            buffer.Stream.Position = 0;
            buffer.Writer.Write((uint)0x11223344);
            Assert.AreEqual(4, buffer.Stream.Position);
            Assert.AreEqual(0x44, buffer.GetBufferUnsafe()[0]);

            // Reuse: rewind and overwrite
            buffer.Stream.Position = 0;
            buffer.Writer.Write((uint)0xAABBCCDD);
            Assert.AreEqual(0xDD, buffer.GetBufferUnsafe()[0]);
        }

        [Test]
        public void EnsureCapacity_SmallerThanCurrent_KeepsStreamInstance()
        {
            using var buffer = new ReusableBufferWriter(64);
            var stream = buffer.Stream;
            var writer = buffer.Writer;

            buffer.EnsureCapacity(8);

            Assert.AreSame(stream, buffer.Stream);
            Assert.AreSame(writer, buffer.Writer);
        }

        [Test]
        public void EnsureCapacity_Growth_AtLeastDoubles()
        {
            using var buffer = new ReusableBufferWriter(64);
            var initialCapacity = buffer.Capacity;
            var oldStream = buffer.Stream;

            buffer.EnsureCapacity(initialCapacity + 1);

            Assert.GreaterOrEqual(buffer.Capacity, initialCapacity * 2);
            Assert.AreNotSame(oldStream, buffer.Stream, "Growth must recreate the stream");
            // The fresh stream is writable from position 0
            buffer.Stream.Position = 0;
            buffer.Writer.Write((byte)1);
        }

        [Test]
        public void EnsureCapacity_LargeRequest_SatisfiesExactRequirement()
        {
            using var buffer = new ReusableBufferWriter(4);
            buffer.EnsureCapacity(100_000);
            Assert.GreaterOrEqual(buffer.Capacity, 100_000);
            Assert.GreaterOrEqual(buffer.GetBufferUnsafe().Length, 100_000);
        }

        [Test]
        public void Dispose_IsIdempotent()
        {
            var buffer = new ReusableBufferWriter(16);
            buffer.Dispose();
            Assert.DoesNotThrow(() => buffer.Dispose());
        }
    }
}
