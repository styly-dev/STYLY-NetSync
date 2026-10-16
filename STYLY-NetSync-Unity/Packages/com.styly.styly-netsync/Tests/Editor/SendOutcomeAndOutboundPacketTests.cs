// SendOutcomeAndOutboundPacketTests.cs - EditMode tests for send-result and outbound-lane types.
using NUnit.Framework;

namespace Styly.NetSync.Tests
{
    public class SendOutcomeAndOutboundPacketTests
    {
        [Test]
        public void Sent_HasSentStatusOnly()
        {
            var outcome = SendOutcome.Sent();
            Assert.AreEqual(SendStatus.Sent, outcome.Status);
            Assert.IsTrue(outcome.IsSent);
            Assert.IsFalse(outcome.IsBackpressure);
            Assert.IsFalse(outcome.IsFatal);
            Assert.IsNull(outcome.Error);
        }

        [Test]
        public void Backpressure_IsTemporaryNotFatal()
        {
            var outcome = SendOutcome.Backpressure();
            Assert.AreEqual(SendStatus.Backpressure, outcome.Status);
            Assert.IsFalse(outcome.IsSent);
            Assert.IsTrue(outcome.IsBackpressure);
            Assert.IsFalse(outcome.IsFatal);
            Assert.IsNull(outcome.Error);
        }

        [Test]
        public void Fatal_CarriesErrorMessage()
        {
            var outcome = SendOutcome.Fatal("socket closed");
            Assert.IsTrue(outcome.IsFatal);
            Assert.AreEqual("socket closed", outcome.Error);
        }

        [Test]
        public void Fatal_NullError_FallsBackToUnknown()
        {
            Assert.AreEqual("unknown", SendOutcome.Fatal(null).Error);
        }

        [Test]
        public void OutboundLane_ValuesArePinned()
        {
            // The lane order is part of the send-priority contract:
            // control drains before transform lanes.
            Assert.AreEqual(0, (int)OutboundLane.Control);
            Assert.AreEqual(1, (int)OutboundLane.Transform);
            Assert.AreEqual(2, (int)OutboundLane.ObjectTransform);
        }
    }
}
