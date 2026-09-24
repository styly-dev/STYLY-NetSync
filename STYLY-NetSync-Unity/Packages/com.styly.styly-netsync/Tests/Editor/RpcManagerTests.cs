// RpcManagerTests.cs - EditMode tests for RPC send/queue/rate-limit and offline loopback.
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace Styly.NetSync.Tests
{
    public class RpcManagerTests
    {
        private FakeConnectionManager _connection;
        private FakeNetSyncContext _context;
        private RPCManager _manager;

        [SetUp]
        public void SetUp()
        {
            _connection = new FakeConnectionManager();
            _context = new FakeNetSyncContext { ClientNo = 5, IsReady = true, IsOfflineMode = false };
            _manager = new RPCManager(_connection, "device-1", _context);
        }

        [TearDown]
        public void TearDown()
        {
            _manager.Dispose();
        }

        [Test]
        public void Send_WhenReady_EnqueuesControlPayload()
        {
            _manager.Send("room-1", "Hello", new[] { "a", "b" });

            Assert.AreEqual(1, _connection.ControlSends.Count);
            Assert.AreEqual("room-1", _connection.ControlSends[0].roomId);
            Assert.AreEqual(BinarySerializer.MSG_RPC, _connection.ControlSends[0].payload[0]);
        }

        [Test]
        public void Send_WhenNotReady_BuffersUntilFlush()
        {
            _context.IsReady = false;
            _manager.Send("room-1", "Buffered", new[] { "x" });
            Assert.AreEqual(0, _connection.ControlSends.Count, "must not send before ready");

            _context.IsReady = true;
            var fullyFlushed = _manager.FlushPendingIfReady("room-1");

            Assert.IsTrue(fullyFlushed);
            Assert.AreEqual(1, _connection.ControlSends.Count);
        }

        [Test]
        public void FlushPendingIfReady_WhenNotReady_DoesNothing()
        {
            _context.IsReady = false;
            _manager.Send("room-1", "Buffered", new[] { "x" });

            Assert.IsTrue(_manager.FlushPendingIfReady("room-1"), "no backpressure while not ready");
            Assert.AreEqual(0, _connection.ControlSends.Count);
        }

        [Test]
        public void Send_RateLimited_DropsBeyondLimitAndWarns()
        {
            _manager.ConfigureRpcLimit(rpcLimit: 2, windowSeconds: 100.0, warnCooldown: 0.0);

            _manager.Send("room-1", "A", new[] { "1" });
            _manager.Send("room-1", "B", new[] { "2" });
            LogAssert.Expect(LogType.Warning, new System.Text.RegularExpressions.Regex(@"\[NetSync/RPC\] Rate limited"));
            _manager.Send("room-1", "C", new[] { "3" }); // exceeds limit

            Assert.AreEqual(2, _connection.ControlSends.Count, "only 2 of 3 RPCs should be sent");
        }

        [Test]
        public void Send_OfflineBroadcast_LoopsBackThroughReceiveQueue()
        {
            _context.IsOfflineMode = true;
            int received = 0;
            int receivedSender = -1;
            string receivedFn = null;
            _manager.OnRPCReceived.AddListener((sender, fn, args) =>
            {
                received++;
                receivedSender = sender;
                receivedFn = fn;
            });

            _manager.Send("room-1", "PingSelf", new[] { "v" });
            _manager.ProcessRPCQueue();

            Assert.AreEqual(1, received);
            Assert.AreEqual(5, receivedSender, "loopback carries our own clientNo");
            Assert.AreEqual("PingSelf", receivedFn);
            Assert.AreEqual(0, _connection.ControlSends.Count, "offline RPC never touches the wire");
        }

        [Test]
        public void SendTo_OfflineTargetingAnotherClient_DoesNotLoopBack()
        {
            _context.IsOfflineMode = true;
            int received = 0;
            _manager.OnRPCReceived.AddListener((_, _, _) => received++);

            _manager.SendTo("room-1", new[] { 99 }, "ForOther", new[] { "v" });
            _manager.ProcessRPCQueue();

            Assert.AreEqual(0, received, "targeted RPC to a different client must not loop back to self");
        }

        [Test]
        public void SendTo_OfflineTargetingSelf_LoopsBack()
        {
            _context.IsOfflineMode = true;
            int received = 0;
            _manager.OnRPCReceived.AddListener((_, _, _) => received++);

            _manager.SendTo("room-1", new[] { 5 }, "ForSelf", new[] { "v" });
            _manager.ProcessRPCQueue();

            Assert.AreEqual(1, received);
        }
    }
}
