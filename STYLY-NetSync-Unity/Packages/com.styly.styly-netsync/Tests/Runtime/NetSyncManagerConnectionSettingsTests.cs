using System.Collections;
using System.Text.RegularExpressions;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace Styly.NetSync.Tests
{
    /// <summary>
    /// PlayMode tests for configuring NetSyncManager from code before the
    /// connection is established (issue #537). Offline mode is used so no
    /// server is required; the gating logic is identical for online mode.
    /// </summary>
    public class NetSyncManagerConnectionSettingsTests
    {
        private const int ReadyTimeoutFrames = 300;

        private GameObject _go;
        private NetSyncManager _manager;

        [UnityTearDown]
        public IEnumerator TearDown()
        {
            if (_go != null)
            {
                Object.Destroy(_go);
                _go = null;
                _manager = null;
                // Let OnDisable/OnDestroy run so the singleton slot is released
                // before the next test creates its own manager.
                yield return null;
            }
        }

        /// <summary>
        /// Adds NetSyncManager to an active GameObject. Awake and OnEnable run
        /// synchronously here; the first Update (which creates the managers and
        /// starts networking) does not run until the test yields.
        /// </summary>
        private NetSyncManager CreateManager()
        {
            _go = new GameObject("NetSyncManager (test)");
            _manager = _go.AddComponent<NetSyncManager>();
            return _manager;
        }

        private IEnumerator WaitUntilReady(NetSyncManager manager)
        {
            for (int i = 0; i < ReadyTimeoutFrames && !manager.IsReady; i++)
            {
                yield return null;
            }
            Assert.IsTrue(manager.IsReady, "NetSyncManager did not become ready in time");
        }

        private static void ExpectRejectedSetterError(string propertyName)
        {
            LogAssert.Expect(LogType.Error,
                new Regex($"{propertyName} cannot be changed after networking has started"));
        }

        [UnityTest]
        public IEnumerator Setters_BeforeFirstUpdate_AreAppliedToTheConnection()
        {
            var manager = CreateManager();

            // Awake has run, but managers must not exist yet.
            Assert.IsFalse(manager.HasServerConnection);
            Assert.IsFalse(manager.IsReady);

            manager.ServerAddress = "  10.0.0.5  ";
            manager.RoomId = "room_from_code";
            manager.OfflineMode = true;

            yield return WaitUntilReady(manager);

            Assert.AreEqual("10.0.0.5", manager.ServerAddress, "ServerAddress should be trimmed and applied");
            Assert.AreEqual("room_from_code", manager.RoomId);
            Assert.IsTrue(manager.OfflineMode);
            Assert.IsTrue(manager.IsOfflineMode, "IsOfflineMode must alias OfflineMode");
            Assert.AreEqual(1, manager.ClientNo, "Offline mode assigns client number 1");
        }

        [UnityTest]
        public IEnumerator Setters_FromAnotherComponentsStart_AreApplied()
        {
            var manager = CreateManager();
            var probe = _go.AddComponent<StartProbe>();
            probe.RoomIdToSet = "room_from_start";

            yield return WaitUntilReady(manager);

            Assert.IsTrue(probe.Ran, "probe Start() should have run before the manager's first Update");
            Assert.AreEqual("room_from_start", manager.RoomId);
            Assert.IsTrue(manager.OfflineMode);
        }

        [UnityTest]
        public IEnumerator Setters_AfterNetworkingStarted_AreRejectedAndLeaveValuesUnchanged()
        {
            var manager = CreateManager();
            manager.ServerAddress = "10.0.0.5";
            manager.RoomId = "initial_room";
            manager.OfflineMode = true;
            yield return WaitUntilReady(manager);

            ExpectRejectedSetterError("ServerAddress");
            manager.ServerAddress = "10.0.0.6";

            ExpectRejectedSetterError("RoomId");
            manager.RoomId = "other_room";

            ExpectRejectedSetterError("OfflineMode");
            manager.OfflineMode = false;

            Assert.AreEqual("10.0.0.5", manager.ServerAddress);
            Assert.AreEqual("initial_room", manager.RoomId);
            Assert.IsTrue(manager.OfflineMode);
            Assert.IsTrue(manager.IsReady, "rejected assignments must not disturb the live connection");
        }

        [UnityTest]
        public IEnumerator RoomIdSetter_RejectsNullOrEmpty()
        {
            var manager = CreateManager();
            manager.RoomId = "valid_room";

            LogAssert.Expect(LogType.Error, new Regex("RoomId cannot be null or empty"));
            manager.RoomId = "";
            LogAssert.Expect(LogType.Error, new Regex("RoomId cannot be null or empty"));
            manager.RoomId = null;

            Assert.AreEqual("valid_room", manager.RoomId);
            yield break;
        }

        [UnityTest]
        public IEnumerator ServerAddressSetter_TreatsNullAsEmpty()
        {
            var manager = CreateManager();
            manager.ServerAddress = "10.0.0.5";
            manager.ServerAddress = null;
            Assert.AreEqual("", manager.ServerAddress);
            yield break;
        }

        [UnityTest]
        public IEnumerator SetRoomId_BeforeNetworkingStarts_AssignsRoomWithoutRoomSwitch()
        {
            var manager = CreateManager();
            manager.OfflineMode = true;

            // Before the fix this entered the room-switch path ("Switching from
            // room ...") and, in offline mode, left _roomSwitching stuck so
            // NV/RPC sending never resumed. The pre-connection path logs instead.
            LogAssert.Expect(LogType.Log, new Regex("Room ID set to 'room_via_setroomid' before networking started"));
            manager.SetRoomId("room_via_setroomid");
            Assert.AreEqual("room_via_setroomid", manager.RoomId);

            yield return WaitUntilReady(manager);

            string changedName = null, changedValue = null;
            manager.OnGlobalVariableChanged.AddListener((name, oldValue, newValue) =>
            {
                changedName = name;
                changedValue = newValue;
            });

            Assert.IsTrue(manager.SetGlobalVariable("probe", "alive"), "NV path must be alive after startup");
            Assert.AreEqual("alive", manager.GetGlobalVariable("probe"));
            Assert.AreEqual("probe", changedName);
            Assert.AreEqual("alive", changedValue);
        }

        [UnityTest]
        public IEnumerator SetRoomId_BeforeAwake_DoesNotThrowAndIsUsedForFirstConnection()
        {
            _go = new GameObject("NetSyncManager (test, inactive)");
            _go.SetActive(false);
            _manager = _go.AddComponent<NetSyncManager>(); // Awake has not run yet

            // Before the fix this reached StartNetworking() with a null
            // connection manager and threw NullReferenceException.
            _manager.ServerAddress = "10.0.0.5";
            _manager.OfflineMode = true;
            Assert.DoesNotThrow(() => _manager.SetRoomId("room_before_awake"));
            Assert.AreEqual("room_before_awake", _manager.RoomId);

            _go.SetActive(true);
            yield return WaitUntilReady(_manager);
            Assert.AreEqual("room_before_awake", _manager.RoomId);
        }

        [UnityTest]
        public IEnumerator DisabledComponent_CanBeConfiguredLater_AndStartsWhenEnabled()
        {
            _go = new GameObject("NetSyncManager (test, disabled)");
            _go.SetActive(false);
            _manager = _go.AddComponent<NetSyncManager>();
            _manager.enabled = false;
            _go.SetActive(true); // Awake runs (GameObject active), OnEnable/Update do not.

            Assert.AreSame(_manager, NetSyncManager.Instance, "Instance must be available while the component is disabled");

            yield return null;
            yield return null;
            Assert.IsFalse(_manager.IsReady, "networking must not start while the component is disabled");

            _manager.RoomId = "late_room";
            _manager.OfflineMode = true;
            _manager.enabled = true;

            yield return WaitUntilReady(_manager);
            Assert.AreEqual("late_room", _manager.RoomId);
            Assert.IsTrue(_manager.OfflineMode);
        }

        /// <summary>
        /// Stands in for an application bootstrap component: its Start() runs
        /// after NetSyncManager.Awake (execution order -1000) and before
        /// NetSyncManager's first Update.
        /// </summary>
        private class StartProbe : MonoBehaviour
        {
            public string RoomIdToSet;
            public bool Ran;

            private void Start()
            {
                var manager = NetSyncManager.Instance;
                manager.RoomId = RoomIdToSet;
                manager.OfflineMode = true;
                Ran = true;
            }
        }
    }
}
