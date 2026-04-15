// PoseBufferTests.cs
// Ingest / stale-drop / overflow semantics for the per-entity pose buffer.

using NUnit.Framework;
using Styly.NetSync.Internal;
using UnityEngine;

namespace Styly.NetSync.Tests.EditorTests
{
    [TestFixture]
    public class PoseBufferTests
    {
        private const ulong EntityA = 0x1111111111111111UL;

        private static PoseSample Sample(ushort seq, ulong timeUs, Vector3 pos)
        {
            return new PoseSample
            {
                ServerTimeUs = timeUs,
                PoseSeq = seq,
                Flags = StateFlags.None,
                Mask = ChangedMask.Position,
                State = new TransformState
                {
                    Position = pos,
                    Rotation = Quaternion.identity,
                    Scale = Vector3.one,
                },
            };
        }

        [Test]
        public void AddsSamplesInOrder()
        {
            PoseBuffer buf = new PoseBuffer();
            Assert.IsTrue(buf.Add(EntityA, Sample(1, 1000, Vector3.zero)));
            Assert.IsTrue(buf.Add(EntityA, Sample(2, 2000, Vector3.right)));
            Assert.AreEqual(2, buf.Count(EntityA));
        }

        [Test]
        public void DropsOutOfOrderSeq()
        {
            PoseBuffer buf = new PoseBuffer();
            buf.Add(EntityA, Sample(5, 5000, Vector3.zero));
            bool accepted = buf.Add(EntityA, Sample(3, 3000, Vector3.up));
            Assert.IsFalse(accepted);
            Assert.AreEqual(1, buf.Count(EntityA));
        }

        [Test]
        public void DropsStaleSeqAfterMarkApplied()
        {
            PoseBuffer buf = new PoseBuffer();
            buf.Add(EntityA, Sample(10, 10_000, Vector3.zero));
            buf.MarkApplied(EntityA, 10);
            bool accepted = buf.Add(EntityA, Sample(10, 10_500, Vector3.up));
            Assert.IsFalse(accepted);
        }

        [Test]
        public void EvictsOldestOnOverflow()
        {
            PoseBuffer buf = new PoseBuffer(capacity: 3);
            buf.Add(EntityA, Sample(1, 1_000, Vector3.zero));
            buf.Add(EntityA, Sample(2, 2_000, Vector3.zero));
            buf.Add(EntityA, Sample(3, 3_000, Vector3.zero));
            buf.Add(EntityA, Sample(4, 4_000, Vector3.zero));
            Assert.AreEqual(3, buf.Count(EntityA));
            var samples = buf.GetSamples(EntityA);
            Assert.AreEqual((ushort)2, samples[0].PoseSeq);
            Assert.AreEqual((ushort)4, samples[2].PoseSeq);
        }

        [Test]
        public void WrapAwareSeqComparison()
        {
            PoseBuffer buf = new PoseBuffer();
            buf.Add(EntityA, Sample(65530, 1_000, Vector3.zero));
            buf.MarkApplied(EntityA, 65530);
            // Wrap around.
            Assert.IsTrue(buf.Add(EntityA, Sample(5, 2_000, Vector3.zero)));
        }
    }
}
