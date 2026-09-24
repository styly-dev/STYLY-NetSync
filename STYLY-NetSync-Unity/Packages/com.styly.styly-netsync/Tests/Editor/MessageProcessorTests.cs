// MessageProcessorTests.cs - EditMode tests for inbound queueing, device-id mapping,
// stealth tracking, version-mismatch detection, and malformed-payload safety.
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace Styly.NetSync.Tests
{
    public class MessageProcessorTests
    {
        private MessageProcessor _processor;

        [SetUp]
        public void SetUp()
        {
            _processor = new MessageProcessor(logNetworkTraffic: false);
        }

        #region === Helpers ===

        private static byte[] BuildDeviceIdMapping(
            (int clientNo, string deviceId, bool stealth)[] mappings,
            int major = 0, int minor = 17, int patch = 1)
        {
            using var ms = new MemoryStream();
            using var writer = new BinaryWriter(ms);
            writer.Write(BinarySerializer.MSG_DEVICE_ID_MAPPING);
            writer.Write((byte)major);
            writer.Write((byte)minor);
            writer.Write((byte)patch);
            writer.Write((ushort)mappings.Length);
            foreach (var (clientNo, deviceId, stealth) in mappings)
            {
                writer.Write((ushort)clientNo);
                writer.Write(stealth ? (byte)0x01 : (byte)0x00);
                var bytes = System.Text.Encoding.UTF8.GetBytes(deviceId);
                writer.Write((byte)bytes.Length);
                writer.Write(bytes);
            }
            return ms.ToArray();
        }

        private static byte[] BuildRpc(int senderClientNo, string fn)
        {
            return BinarySerializer.SerializeRPCMessage(new RPCMessage
            {
                senderClientNo = senderClientNo,
                deviceId = "d",
                targetClientNos = System.Array.Empty<int>(),
                functionName = fn,
                argumentsJson = "[]",
            });
        }

        // Drives the id_mapping / rpc queue without a NetSyncManager or prefabs.
        private void DrainQueue(AvatarManager avatarManager, RPCManager rpcManager)
        {
            _processor.ProcessMessageQueue(avatarManager, rpcManager, "local-device");
        }

        #endregion

        #region === Device-id mapping ===

        [Test]
        public void DeviceIdMapping_ResolvesClientNoAndDeviceIdBothWays()
        {
            _processor.ProcessIncomingMessage(BuildDeviceIdMapping(new[]
            {
                (1, "device-a", false),
                (2, "device-b", true),
            }));
            DrainQueue(new AvatarManager(false), rpcManager: null);

            Assert.AreEqual(1, _processor.GetClientNo("device-a"));
            Assert.AreEqual(2, _processor.GetClientNo("device-b"));
            Assert.AreEqual("device-a", _processor.GetDeviceIdFromClientNo(1));
            Assert.AreEqual("device-b", _processor.GetDeviceIdFromClientNo(2));
        }

        [Test]
        public void UnknownLookups_ReturnZeroOrNull()
        {
            Assert.AreEqual(0, _processor.GetClientNo("nobody"));
            Assert.IsNull(_processor.GetDeviceIdFromClientNo(999));
            Assert.IsFalse(_processor.IsClientStealthMode(999));
        }

        [Test]
        public void StealthFlag_IsTrackedPerClient()
        {
            _processor.ProcessIncomingMessage(BuildDeviceIdMapping(new[]
            {
                (1, "visible", false),
                (2, "stealthy", true),
            }));
            DrainQueue(new AvatarManager(false), rpcManager: null);

            Assert.IsFalse(_processor.IsClientStealthMode(1));
            Assert.IsTrue(_processor.IsClientStealthMode(2));
        }

        [Test]
        public void ClearRoomScopedState_ForgetsMappings()
        {
            _processor.ProcessIncomingMessage(BuildDeviceIdMapping(new[] { (1, "device-a", false) }));
            DrainQueue(new AvatarManager(false), rpcManager: null);
            Assert.AreEqual(1, _processor.GetClientNo("device-a"));

            _processor.ClearRoomScopedState();
            Assert.AreEqual(0, _processor.GetClientNo("device-a"));
        }

        #endregion

        #region === Version mismatch ===

        [Test]
        public void VersionMismatch_FiresEventOnIncompatibleMinor()
        {
            // Client resource version is 0.17.x; advertise a different minor.
            _processor.ProcessIncomingMessage(BuildDeviceIdMapping(
                new[] { (1, "device-a", false) }, major: 0, minor: 16, patch: 0));

            int mismatchCount = 0;
            int gotServerMinor = -1;
            _processor.OnVersionMismatch += (sMaj, sMin, sPat, cMaj, cMin, cPat) =>
            {
                mismatchCount++;
                gotServerMinor = sMin;
            };

            LogAssert.Expect(LogType.Error, new System.Text.RegularExpressions.Regex(@"Version mismatch"));
            DrainQueue(new AvatarManager(false), rpcManager: null);

            Assert.AreEqual(1, mismatchCount);
            Assert.AreEqual(16, gotServerMinor);
        }

        [Test]
        public void VersionCheck_MatchingVersion_DoesNotFire()
        {
            var (major, minor, patch) = Information.ParseVersion(Information.GetVersion());
            _processor.ProcessIncomingMessage(BuildDeviceIdMapping(
                new[] { (1, "device-a", false) }, major, minor, patch));

            int mismatchCount = 0;
            _processor.OnVersionMismatch += (_, _, _, _, _, _) => mismatchCount++;

            DrainQueue(new AvatarManager(false), rpcManager: null);

            Assert.AreEqual(0, mismatchCount);
        }

        #endregion

        #region === Malformed payloads ===

        [Test]
        public void ProcessIncomingMessage_MalformedPayloads_DoNotThrow()
        {
            Assert.DoesNotThrow(() => _processor.ProcessIncomingMessage(new byte[] { 12 })); // room pose type, no body
            Assert.DoesNotThrow(() => _processor.ProcessIncomingMessage(new byte[] { 200, 1, 2 })); // unknown type
            Assert.AreEqual(0, _processor.MessagesReceived, "malformed input is not counted as received");
        }

        [Test]
        public void ProcessIncomingMessage_UnknownType_IsIgnored()
        {
            _processor.ProcessIncomingMessage(new byte[] { 250, 0, 0, 0 });
            Assert.AreEqual(0, _processor.MessagesReceived);
        }

        #endregion

        #region === Threaded enqueue / drain ===

        [Test]
        public void ConcurrentRpcEnqueue_DeliversEveryMessage()
        {
            const int threads = 4;
            const int perThread = 50;

            Parallel.For(0, threads, t =>
            {
                for (int i = 0; i < perThread; i++)
                {
                    _processor.ProcessIncomingMessage(BuildRpc(t * 1000 + i, "Fn"));
                }
            });

            var connection = new FakeConnectionManager();
            var context = new FakeNetSyncContext();
            var rpcManager = new RPCManager(connection, "d", context);
            try
            {
                int received = 0;
                rpcManager.OnRPCReceived.AddListener((_, _, _) => received++);

                DrainQueue(new AvatarManager(false), rpcManager);
                rpcManager.ProcessRPCQueue();

                Assert.AreEqual(threads * perThread, received, "no RPC may be lost across threads");
            }
            finally
            {
                rpcManager.Dispose();
            }
        }

        #endregion
    }
}
