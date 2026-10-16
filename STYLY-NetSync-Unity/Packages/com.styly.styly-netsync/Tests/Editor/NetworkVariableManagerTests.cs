// NetworkVariableManagerTests.cs - EditMode tests for NV send debounce, dedupe,
// offline local apply, validation, and server-sync handling.
using System;
using System.Collections.Generic;
using NUnit.Framework;

namespace Styly.NetSync.Tests
{
    public class NetworkVariableManagerTests
    {
        private FakeConnectionManager _connection;
        private FakeNetSyncContext _context;
        private NetworkVariableManager _manager;

        private static double UnixNow => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0;

        [SetUp]
        public void SetUp()
        {
            _connection = new FakeConnectionManager();
            _context = new FakeNetSyncContext { ClientNo = 3, RoomId = "room-1", IsOfflineMode = false };
            _manager = new NetworkVariableManager(_connection, "device-1", _context);
        }

        [TearDown]
        public void TearDown()
        {
            _manager.Dispose();
        }

        #region === Online send path ===

        [Test]
        public void SetGlobalVariable_LeadingEdge_SendsImmediately()
        {
            Assert.IsTrue(_manager.SetGlobalVariable("score", "10", "room-1"));

            Assert.AreEqual(1, _connection.ControlSends.Count);
            Assert.AreEqual(BinarySerializer.MSG_GLOBAL_VAR_SET, _connection.ControlSends[0].payload[0]);
        }

        [Test]
        public void SetGlobalVariable_SameValueTwice_IsDeduped()
        {
            _manager.SetGlobalVariable("score", "10", "room-1");
            _manager.SetGlobalVariable("score", "10", "room-1");

            Assert.AreEqual(1, _connection.ControlSends.Count, "identical value must not be re-sent");
        }

        [Test]
        public void SetGlobalVariable_TrailingValue_FlushedOnTick()
        {
            _manager.SetGlobalVariable("score", "10", "room-1"); // leading-edge send
            _manager.SetGlobalVariable("score", "20", "room-1"); // within cooldown -> pending

            Assert.AreEqual(1, _connection.ControlSends.Count, "second value is debounced, not sent yet");

            // Advance well past the debounce deadline
            _manager.Tick(UnixNow + 10.0, "room-1");

            Assert.AreEqual(2, _connection.ControlSends.Count, "trailing value flushed after debounce");
        }

        [Test]
        public void SetClientVariable_LeadingEdge_SendsImmediately()
        {
            Assert.IsTrue(_manager.SetClientVariable("hp", "50", targetClientNo: 7, "room-1"));

            Assert.AreEqual(1, _connection.ControlSends.Count);
            Assert.AreEqual(BinarySerializer.MSG_CLIENT_VAR_SET, _connection.ControlSends[0].payload[0]);
        }

        #endregion

        #region === Offline path ===

        [Test]
        public void SetGlobalVariable_Offline_AppliesLocallyAndFiresEvent()
        {
            _context.IsOfflineMode = true;
            _manager.MarkInitialSyncComplete();

            string evName = null, evOld = "unset", evNew = null;
            _manager.OnGlobalVariableChanged += (n, o, v) => { evName = n; evOld = o; evNew = v; };

            Assert.IsTrue(_manager.SetGlobalVariable("score", "42", "room-1"));

            Assert.AreEqual(0, _connection.ControlSends.Count, "offline NV never touches the wire");
            Assert.AreEqual("score", evName);
            Assert.IsNull(evOld);
            Assert.AreEqual("42", evNew);
            Assert.AreEqual("42", _manager.GetGlobalVariable("score"));
        }

        [Test]
        public void SetClientVariable_Offline_AppliesLocallyAndFiresEvent()
        {
            _context.IsOfflineMode = true;
            _manager.MarkInitialSyncComplete();

            int evClient = -1; string evName = null, evNew = null;
            _manager.OnClientVariableChanged += (c, n, o, v) => { evClient = c; evName = n; evNew = v; };

            Assert.IsTrue(_manager.SetClientVariable("hp", "80", targetClientNo: 3, "room-1"));

            Assert.AreEqual(3, evClient);
            Assert.AreEqual("hp", evName);
            Assert.AreEqual("80", evNew);
            Assert.AreEqual("80", _manager.GetClientVariable("hp", 3));
        }

        [Test]
        public void SetGlobalVariable_OfflineSameValue_DoesNotRefireEvent()
        {
            _context.IsOfflineMode = true;
            _manager.MarkInitialSyncComplete();

            int count = 0;
            _manager.OnGlobalVariableChanged += (_, _, _) => count++;

            _manager.SetGlobalVariable("score", "42", "room-1");
            _manager.SetGlobalVariable("score", "42", "room-1");

            Assert.AreEqual(1, count, "unchanged offline value must not refire the event");
        }

        #endregion

        #region === Validation & lifecycle ===

        [Test]
        public void SetGlobalVariable_InvalidName_ReturnsFalseAndDoesNotSend()
        {
            Assert.IsFalse(_manager.SetGlobalVariable("", "v", "room-1"));
            Assert.IsFalse(_manager.SetGlobalVariable(new string('n', 65), "v", "room-1"));
            Assert.AreEqual(0, _connection.ControlSends.Count);
        }

        [Test]
        public void SetGlobalVariable_ValueTooLong_ReturnsFalse()
        {
            Assert.IsFalse(_manager.SetGlobalVariable("k", new string('v', 1025), "room-1"));
        }

        [Test]
        public void GetGlobalVariable_BeforeInitialSync_Throws()
        {
            Assert.Throws<InvalidOperationException>(() => _manager.GetGlobalVariable("score"));
        }

        [Test]
        public void GetClientVariable_BeforeInitialSync_Throws()
        {
            Assert.Throws<InvalidOperationException>(() => _manager.GetClientVariable("hp", 3));
        }

        [Test]
        public void MarkInitialSyncComplete_SetsFlag()
        {
            Assert.IsFalse(_manager.HasReceivedInitialSync);
            _manager.MarkInitialSyncComplete();
            Assert.IsTrue(_manager.HasReceivedInitialSync);
        }

        [Test]
        public void ResetInitialSyncFlag_ClearsFlag()
        {
            _manager.MarkInitialSyncComplete();
            _manager.ResetInitialSyncFlag();
            Assert.IsFalse(_manager.HasReceivedInitialSync);
        }

        #endregion

        #region === Server sync handling ===

        [Test]
        public void HandleGlobalVariableSync_AppliesValuesAndMarksSynced()
        {
            string changedName = null, changedValue = null;
            _manager.OnGlobalVariableChanged += (n, o, v) => { changedName = n; changedValue = v; };

            _manager.HandleGlobalVariableSync(new Dictionary<string, object>
            {
                ["variables"] = new object[]
                {
                    new Dictionary<string, object> { ["name"] = "score", ["value"] = "99" },
                },
            });

            Assert.IsTrue(_manager.HasReceivedInitialSync);
            Assert.AreEqual("score", changedName);
            Assert.AreEqual("99", changedValue);
            Assert.AreEqual("99", _manager.GetGlobalVariable("score"));
        }

        [Test]
        public void HandleClientVariableSync_RemovesMissingKeysAsSnapshot()
        {
            // First snapshot: client 7 has {a=1, b=2}
            _manager.HandleClientVariableSync(new Dictionary<string, object>
            {
                ["clientVariables"] = new Dictionary<string, object>
                {
                    ["7"] = new object[]
                    {
                        new Dictionary<string, object> { ["name"] = "a", ["value"] = "1" },
                        new Dictionary<string, object> { ["name"] = "b", ["value"] = "2" },
                    },
                },
            });

            var removed = new List<string>();
            _manager.OnClientVariableChanged += (c, n, o, v) => { if (v == null) removed.Add(n); };

            // Second snapshot drops "b"
            _manager.HandleClientVariableSync(new Dictionary<string, object>
            {
                ["clientVariables"] = new Dictionary<string, object>
                {
                    ["7"] = new object[]
                    {
                        new Dictionary<string, object> { ["name"] = "a", ["value"] = "1" },
                    },
                },
            });

            Assert.Contains("b", removed, "keys absent from the new snapshot must be removed");
            Assert.AreEqual("1", _manager.GetClientVariable("a", 7));
            Assert.IsNull(_manager.GetClientVariable("b", 7));
        }

        #endregion
    }
}
