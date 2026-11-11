// ConnectionIntegrationTests.cs - Verify a Unity client connects to the live server,
// completes the handshake + NV initial sync, and fires OnReady exactly once.
using System.Collections;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace Styly.NetSync.Tests
{
    public class ConnectionIntegrationTests : ServerIntegrationTestBase
    {
        [UnityTest]
        [Timeout(60000)]
        public IEnumerator ConnectAndReady_AssignsClientNoAndFiresOnReadyOnce()
        {
            var room = NewRoom();
            Manager = NetSyncManagerTestFactory.CreateConnected(
                Server.ServerAddress, Server.ControlPort, Server.TransformPort, Server.PubPort, room);

            int readyCount = 0;
            Manager.OnReady.AddListener(() => readyCount++);

            yield return PollWait.Until(() => Manager.IsReady, 20f, "IsReady against live server");

            Assert.Greater(Manager.ClientNo, 0, "server assigns a positive client number");
            Assert.IsFalse(Manager.IsOfflineMode);

            // Give a few frames to ensure OnReady is not fired more than once.
            for (int i = 0; i < 10; i++) { yield return null; }
            Assert.AreEqual(1, readyCount, "OnReady must fire exactly once");
        }
    }
}
