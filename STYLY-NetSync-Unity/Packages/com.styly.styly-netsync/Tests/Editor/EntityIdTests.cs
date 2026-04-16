// EntityIdTests.cs
// Round-trip + determinism checks for EntityIdUtils.

using System;
using NUnit.Framework;
using Styly.NetSync.Internal;

namespace Styly.NetSync.Tests.EditorTests
{
    [TestFixture]
    public class EntityIdTests
    {
        [Test]
        public void FromGuidString_IsDeterministic()
        {
            const string guid = "12345678-1234-1234-1234-1234567890ab";
            ulong a = EntityIdUtils.FromGuidString(guid);
            ulong b = EntityIdUtils.FromGuidString(guid);
            Assert.AreEqual(a, b);
            Assert.AreNotEqual(EntityIdUtils.Invalid, a);
        }

        [Test]
        public void FromGuidString_DifferentGuids_ProduceDifferentIds()
        {
            ulong a = EntityIdUtils.FromGuidString(Guid.NewGuid().ToString("D"));
            ulong b = EntityIdUtils.FromGuidString(Guid.NewGuid().ToString("D"));
            Assert.AreNotEqual(a, b);
        }

        [Test]
        public void FromGuidString_Empty_ReturnsInvalid()
        {
            Assert.AreEqual(EntityIdUtils.Invalid, EntityIdUtils.FromGuidString(""));
            Assert.AreEqual(EntityIdUtils.Invalid, EntityIdUtils.FromGuidString(null));
        }

        [Test]
        public void FromGuidString_Malformed_ReturnsInvalid()
        {
            Assert.AreEqual(EntityIdUtils.Invalid, EntityIdUtils.FromGuidString("not-a-guid"));
        }

        [Test]
        public void FromGuid_MatchesFromGuidString()
        {
            Guid g = Guid.NewGuid();
            Assert.AreEqual(EntityIdUtils.FromGuid(g), EntityIdUtils.FromGuidString(g.ToString("D")));
        }

        [Test]
        public void FromGuid_ZeroFoldReturnsOne()
        {
            // Guid whose hi and lo halves are identical will XOR to zero.
            // Build one manually: 16 bytes where first 8 == last 8 (after
            // normalization). Easiest: all-zero ObjectId folds to zero, but
            // Guid.Empty's normalized bytes are all zero too → expect 1.
            ulong id = EntityIdUtils.FromGuid(Guid.Empty);
            Assert.AreEqual(1UL, id);
        }
    }
}
