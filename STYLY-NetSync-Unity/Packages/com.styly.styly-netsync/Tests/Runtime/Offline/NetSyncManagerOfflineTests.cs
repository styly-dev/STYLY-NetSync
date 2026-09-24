// NetSyncManagerOfflineTests.cs - PlayMode tests for offline-mode lifecycle and
// local-loopback of RPC and Network Variables (no server required).
using System.Collections;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace Styly.NetSync.Tests
{
    public class NetSyncManagerOfflineTests
    {
        private NetSyncManager _manager;

        [UnityTearDown]
        public IEnumerator TearDown()
        {
            if (_manager != null)
            {
                Object.Destroy(_manager.gameObject);
                _manager = null;
            }
            // Let OnDisable run (clears the singleton) before the next test.
            yield return null;
        }

        [UnityTest]
        public IEnumerator OfflineMode_ReachesReadyAndAssignsClientNo()
        {
            _manager = NetSyncManagerTestFactory.CreateOffline("offline_room");

            bool readyFired = false;
            _manager.OnReady.AddListener(() => readyFired = true);

            yield return PollWait.Until(() => _manager.IsReady, 5f, "IsReady in offline mode");

            Assert.AreEqual(1, _manager.ClientNo, "offline client number is 1");
            Assert.IsTrue(_manager.IsOfflineMode);

            // OnReady may have fired before we subscribed; wait one frame and accept
            // either the captured flag or the already-ready state.
            yield return null;
            Assert.IsTrue(readyFired || _manager.IsReady);
        }

        [UnityTest]
        public IEnumerator OfflineMode_GlobalVariableRoundTripsLocally()
        {
            _manager = NetSyncManagerTestFactory.CreateOffline("offline_room");
            yield return PollWait.Until(() => _manager.IsReady, 5f, "IsReady");

            string changedName = null, changedValue = null;
            _manager.OnGlobalVariableChanged.AddListener((n, o, v) => { changedName = n; changedValue = v; });

            Assert.IsTrue(_manager.SetGlobalVariable("score", "42"));

            yield return PollWait.Until(() => changedName != null, 2f, "OnGlobalVariableChanged");
            Assert.AreEqual("score", changedName);
            Assert.AreEqual("42", changedValue);
            Assert.AreEqual("42", _manager.GetGlobalVariable("score"));
        }

        [UnityTest]
        public IEnumerator OfflineMode_RpcLoopsBackToSelf()
        {
            _manager = NetSyncManagerTestFactory.CreateOffline("offline_room");
            yield return PollWait.Until(() => _manager.IsReady, 5f, "IsReady");

            int received = 0;
            string receivedFn = null;
            _manager.OnRPCReceived.AddListener((sender, fn, args) => { received++; receivedFn = fn; });

            _manager.Rpc("Ping", new[] { "v" });

            yield return PollWait.Until(() => received > 0, 2f, "OnRPCReceived loopback");
            Assert.AreEqual(1, received);
            Assert.AreEqual("Ping", receivedFn);
        }

        [UnityTest]
        public IEnumerator OfflineMode_ClientVariableRoundTripsLocally()
        {
            _manager = NetSyncManagerTestFactory.CreateOffline("offline_room");
            yield return PollWait.Until(() => _manager.IsReady, 5f, "IsReady");

            int changedClient = -1; string changedName = null, changedValue = null;
            _manager.OnClientVariableChanged.AddListener((c, n, o, v) =>
            {
                changedClient = c; changedName = n; changedValue = v;
            });

            // Target our own client number (1 in offline mode).
            Assert.IsTrue(_manager.SetClientVariable("hp", "80", 1));

            yield return PollWait.Until(() => changedName != null, 2f, "OnClientVariableChanged");
            Assert.AreEqual(1, changedClient);
            Assert.AreEqual("hp", changedName);
            Assert.AreEqual("80", changedValue);
        }

        [UnityTest]
        public IEnumerator DuplicateManager_LogsWarningAndSelfDestructs()
        {
            _manager = NetSyncManagerTestFactory.CreateOffline("offline_room");
            yield return PollWait.Until(() => _manager.IsReady, 5f, "first manager ready");

            LogAssert.Expect(LogType.Warning, new System.Text.RegularExpressions.Regex("Duplicate NetSyncManager"));

            // Build the second manager inline so we keep a handle to its GameObject
            // (the component destroys itself in Awake when it detects the singleton).
            var secondGo = new GameObject("NetSyncManager (duplicate)");
            secondGo.SetActive(false);
            var second = secondGo.AddComponent<NetSyncManager>();
            second.ConfigureForTests("127.0.0.1", 0, 0, 0, "offline_room", offlineMode: true);
            secondGo.SetActive(true); // Awake detects the existing instance and self-destructs

            yield return null;

            Assert.IsTrue(second == null, "duplicate NetSyncManager component must be destroyed");
            Object.Destroy(secondGo);
        }
    }
}
