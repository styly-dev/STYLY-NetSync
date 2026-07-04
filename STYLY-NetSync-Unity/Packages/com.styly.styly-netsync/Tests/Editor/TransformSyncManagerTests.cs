// TransformSyncManagerTests.cs - EditMode tests for send-rate gating and stealth handshake.
// SendLocalTransform (only-on-change / heartbeat) needs a NetSyncAvatar and is covered
// by PlayMode tests; here we test the parts reachable with a fake IConnectionManager.
using NUnit.Framework;

namespace Styly.NetSync.Tests
{
    public class TransformSyncManagerTests
    {
        private FakeConnectionManager _connection;
        private TransformSyncManager _manager;

        [SetUp]
        public void SetUp()
        {
            _connection = new FakeConnectionManager();
            _manager = new TransformSyncManager(_connection, "device-1", sendRate: 10f);
        }

        [TearDown]
        public void TearDown()
        {
            _manager.Dispose();
        }

        [Test]
        public void ShouldSendTransform_RespectsSendRateInterval()
        {
            // 10 Hz => 0.1 s minimum interval
            _manager.UpdateLastSendTime(1.0f);
            Assert.IsFalse(_manager.ShouldSendTransform(1.05f), "0.05 s < 0.1 s interval");
            Assert.IsTrue(_manager.ShouldSendTransform(1.10f), "0.10 s reaches the interval");
            Assert.IsTrue(_manager.ShouldSendTransform(2.0f));
        }

        [Test]
        public void SendRate_IsConfigurable()
        {
            _manager.SendRate = 2f; // 0.5 s interval
            _manager.UpdateLastSendTime(0f);
            Assert.IsFalse(_manager.ShouldSendTransform(0.4f));
            Assert.IsTrue(_manager.ShouldSendTransform(0.5f));
        }

        [Test]
        public void SendStealthHandshake_EnqueuesOneControlPayload()
        {
            var outcome = _manager.SendStealthHandshake("room-1");

            Assert.IsTrue(outcome.IsSent);
            Assert.AreEqual(1, _connection.ControlSends.Count);
            Assert.AreEqual("room-1", _connection.ControlSends[0].roomId);
            Assert.AreEqual(BinarySerializer.MSG_CLIENT_HELLO, _connection.ControlSends[0].payload[0]);
            Assert.AreEqual(1, _manager.MessagesSent);
        }

        [Test]
        public void SendStealthHandshake_WhenQueueFull_ReturnsBackpressure()
        {
            _connection.ControlEnqueueResult = false;

            var outcome = _manager.SendStealthHandshake("room-1");

            Assert.IsTrue(outcome.IsBackpressure);
            Assert.AreEqual(0, _connection.ControlSends.Count);
            Assert.AreEqual(0, _manager.MessagesSent);
        }
    }
}
