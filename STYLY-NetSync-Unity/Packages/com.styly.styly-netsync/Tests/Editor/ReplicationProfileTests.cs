// ReplicationProfileTests.cs
// Verifies that the v1 default profile matches the spec §5.4 recommended
// values and that equality/hash behave sensibly.

using NUnit.Framework;
using Styly.NetSync;

namespace Styly.NetSync.Tests.EditorTests
{
    [TestFixture]
    public class ReplicationProfileTests
    {
        [Test]
        public void Default_MatchesSpecV1()
        {
            ReplicationProfile p = ReplicationProfile.Default;
            Assert.AreEqual(20, p.SendRateHz);
            Assert.AreEqual(1, p.KeyframeIntervalHz);
            Assert.AreEqual(0.005f, p.PositionDeadband);
            Assert.AreEqual(0.5f, p.RotationDeadbandDeg);
            Assert.AreEqual(0.005f, p.ScaleDeadband);
            Assert.AreEqual(ReplicationPriority.Normal, p.Priority);
            Assert.AreEqual(InterpolationMode.Linear, p.Interpolation);
            Assert.IsFalse(p.ReplicateScale);
            Assert.AreEqual(1, p.ProfileVersion);
        }

        [Test]
        public void DefaultEqualsItself()
        {
            ReplicationProfile a = ReplicationProfile.Default;
            ReplicationProfile b = ReplicationProfile.Default;
            Assert.IsTrue(a.Equals(b));
            Assert.AreEqual(a.GetHashCode(), b.GetHashCode());
        }
    }
}
