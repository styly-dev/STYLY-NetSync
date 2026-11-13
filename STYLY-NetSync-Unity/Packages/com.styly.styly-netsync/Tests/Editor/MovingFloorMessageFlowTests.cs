// MovingFloorMessageFlowTests.cs
using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.TestTools;

namespace Styly.NetSync.Tests
{
    /// <summary>
    /// Drives the real MessageProcessor / AvatarManager / NetworkVariableManager / NetSyncManager
    /// with wire-format payloads, without a network. NetSyncManager's Unity callbacks do not run
    /// in EditMode, so its managers are never created; the test wires the three private members
    /// it needs through reflection (see <see cref="WireManager"/>).
    /// </summary>
    public class MovingFloorMessageFlowTests
    {
        private const int RemoteClient = 7;
        private const string RemoteDeviceId = "remote-device";
        private const string LocalDeviceId = "local-device";
        private const string RoomId = "test-room";
        private const uint FloorA = 0x0000A001u;

        private readonly List<GameObject> _gameObjects = new List<GameObject>();
        private NetSyncManager _netSyncManager;
        private MessageProcessor _messageProcessor;
        private AvatarManager _avatarManager;
        private NetworkVariableManager _variables;
        private Transform _floor;
        private int _clientVariableChangeEvents;

        [SetUp]
        public void SetUp()
        {
            _netSyncManager = NewGameObject("TestNetSyncManager").AddComponent<NetSyncManager>();
            _messageProcessor = new MessageProcessor(false);
            _avatarManager = new AvatarManager(false);
            _variables = new NetworkVariableManager(null, LocalDeviceId, _netSyncManager);
            WireManager();

            _clientVariableChangeEvents = 0;
            _netSyncManager.OnClientVariableChanged.AddListener(
                (clientNo, name, oldValue, newValue) => _clientVariableChangeEvents++);

            _floor = NewGameObject("TestMovingFloor").transform;
            Assert.IsTrue(_netSyncManager.RegisterMovingFloor(FloorA, _floor));
        }

        [TearDown]
        public void TearDown()
        {
            _variables.Dispose();
            foreach (var peer in _avatarManager.ConnectedPeers.Values)
            {
                _gameObjects.Add(peer);
            }

            foreach (var go in _gameObjects)
            {
                if (go != null)
                {
                    UnityEngine.Object.DestroyImmediate(go);
                }
            }
            _gameObjects.Clear();
        }

        [Test]
        public void RespawnedAvatar_ResolvesFloor_WithoutVariableChange()
        {
            UseRemoteAvatarPrefab();
            var mapping = IdMapping(RemoteClient, RemoteDeviceId);
            var floorVariable = ClientVariableSync(RemoteClient, MovingFloorManager.ClientVariableName, MovingFloorManager.EncodeFloorId(FloorA));

            // Connect: the floor id arrives through the variable-changed event.
            Pump(mapping, floorVariable, RoomPose(RemoteClient));
            Pump(RoomPose(RemoteClient));
            Assert.IsTrue(_avatarManager.ConnectedPeers.ContainsKey(RemoteClient), "Remote avatar was not spawned.");
            Assert.AreEqual(1, _clientVariableChangeEvents);
            AssertFloorResolved();

            // Disconnect (e.g. the sender app was paused): the avatar and the floor id are dropped.
            var firstAvatar = _avatarManager.ConnectedPeers[RemoteClient];
            _gameObjects.Add(firstAvatar);
            // AvatarManager uses Object.Destroy, which only logs an error in EditMode.
            LogAssert.Expect(LogType.Error, new Regex("Destroy may not be called from edit mode"));
            Pump(RoomPose());
            Assert.IsFalse(_avatarManager.ConnectedPeers.ContainsKey(RemoteClient));
            Assert.IsFalse(_netSyncManager.TryGetMovingFloorForClient(RemoteClient, false, out _));

            // Reconnect: the server repeats the unchanged variable, which fires no change event.
            Pump(floorVariable, RoomPose(RemoteClient));
            Pump(RoomPose(RemoteClient));

            Assert.IsTrue(_avatarManager.ConnectedPeers.ContainsKey(RemoteClient), "Remote avatar was not respawned.");
            Assert.AreNotSame(firstAvatar, _avatarManager.ConnectedPeers[RemoteClient]);
            Assert.AreEqual(1, _clientVariableChangeEvents, "The unchanged variable must not fire a change event.");
            // Fails without the SeedMovingFloorIdForRemoteClient call in MessageProcessor.
            AssertFloorResolved();
        }

        [Test]
        public void ClientWithoutAvatar_DropsFloorIdOnDisconnect()
        {
            // No remote avatar prefab: clients are announced but no avatar is spawned.
            var mapping = IdMapping(RemoteClient, RemoteDeviceId);
            var floorVariable = ClientVariableSync(RemoteClient, MovingFloorManager.ClientVariableName, MovingFloorManager.EncodeFloorId(FloorA));
            int connected = 0;
            int disconnected = 0;
            _netSyncManager.OnAvatarConnected.AddListener(_ => connected++);
            _netSyncManager.OnAvatarDisconnected.AddListener(_ => disconnected++);

            Pump(mapping, floorVariable, RoomPose(RemoteClient));
            Pump(RoomPose(RemoteClient));
            Assert.AreEqual(1, connected);
            Assert.IsEmpty(_avatarManager.ConnectedPeers);
            AssertFloorResolved();

            Pump(RoomPose());

            Assert.AreEqual(1, disconnected);
            Assert.IsFalse(_netSyncManager.TryGetMovingFloorForClient(RemoteClient, false, out _));
        }

        [Test]
        public void SetRoomId_ClearsCachedClientVariables_WithoutChangeEvents()
        {
            // Keep SetRoomId from starting real networking.
            SetPrivateField("_startupPending", true);
            var floorVariable = ClientVariableSync(RemoteClient, MovingFloorManager.ClientVariableName, MovingFloorManager.EncodeFloorId(FloorA));
            Pump(IdMapping(RemoteClient, RemoteDeviceId), floorVariable);
            Assert.AreEqual(1, _clientVariableChangeEvents);
            AssertFloorResolved();

            _netSyncManager.SetRoomId("another-room");

            Assert.AreEqual(1, _clientVariableChangeEvents, "A room switch must not fire change events.");
            Assert.IsFalse(_variables.TryGetCachedClientVariable(MovingFloorManager.ClientVariableName, RemoteClient, out _));

            // The same client number in the new room must not inherit the old room's floor.
            _netSyncManager.SeedMovingFloorIdForRemoteClient(RemoteClient);
            Assert.IsFalse(_netSyncManager.TryGetMovingFloorForClient(RemoteClient, false, out _));

            // The new room's initial sync is delivered as a change, even for an identical value.
            Pump(floorVariable);
            Assert.AreEqual(2, _clientVariableChangeEvents);
            AssertFloorResolved();
        }

        private void AssertFloorResolved()
        {
            Assert.IsTrue(_netSyncManager.TryGetMovingFloorForClient(RemoteClient, false, out var resolved));
            Assert.AreSame(_floor, resolved);
        }

        /// <summary>Receive payloads as the network thread would, then process them on the main thread.</summary>
        private void Pump(params byte[][] payloads)
        {
            foreach (var payload in payloads)
            {
                _messageProcessor.ProcessIncomingMessage(payload);
            }

            _messageProcessor.ProcessMessageQueue(_avatarManager, null, LocalDeviceId, _netSyncManager, _variables);
        }

        private GameObject NewGameObject(string name)
        {
            var go = new GameObject(name);
            _gameObjects.Add(go);
            return go;
        }

        private void UseRemoteAvatarPrefab()
        {
            var prefab = NewGameObject("TestRemoteAvatar");
            prefab.AddComponent<NetSyncAvatar>();
            var serialized = new SerializedObject(_netSyncManager);
            var property = serialized.FindProperty("_remoteAvatarPrefab");
            Assert.IsNotNull(property, "NetSyncManager._remoteAvatarPrefab not found; update the test wiring.");
            property.objectReferenceValue = prefab;
            serialized.ApplyModifiedPropertiesWithoutUndo();
        }

        /// <summary>
        /// Reproduce the part of NetSyncManager's manager initialization this flow depends on:
        /// the variable manager used for seeding, the variable-changed handler and the
        /// avatar-disconnected handler.
        /// </summary>
        private void WireManager()
        {
            SetPrivateField("_networkVariableManager", _variables);
            _variables.OnClientVariableChanged +=
                (Action<int, string, string, string>)PrivateMethod("OnClientVariableChangedHandler")
                    .CreateDelegate(typeof(Action<int, string, string, string>), _netSyncManager);
            _avatarManager.OnAvatarDisconnected.AddListener(
                (UnityAction<int>)PrivateMethod("OnRemoteAvatarDisconnected")
                    .CreateDelegate(typeof(UnityAction<int>), _netSyncManager));
        }

        private void SetPrivateField(string name, object value)
        {
            var field = typeof(NetSyncManager).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.IsNotNull(field, $"NetSyncManager.{name} not found; update the test wiring.");
            field.SetValue(_netSyncManager, value);
        }

        private static MethodInfo PrivateMethod(string name)
        {
            var method = typeof(NetSyncManager).GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.IsNotNull(method, $"NetSyncManager.{name} not found; update the test wiring.");
            return method;
        }

        // --- Server-to-client wire payloads (see BinarySerializer.Deserialize) -------------

        private static byte[] IdMapping(int clientNo, string deviceId)
        {
            var (major, minor, patch) = Information.ParseVersion(Information.GetVersion());
            using (var stream = new MemoryStream())
            using (var writer = new BinaryWriter(stream))
            {
                writer.Write(BinarySerializer.MSG_DEVICE_ID_MAPPING);
                writer.Write((byte)major);
                writer.Write((byte)minor);
                writer.Write((byte)patch);
                writer.Write((ushort)1);
                writer.Write((ushort)clientNo);
                writer.Write((byte)0); // not stealth
                WriteShortString(writer, deviceId);
                writer.Flush();
                return stream.ToArray();
            }
        }

        private static byte[] ClientVariableSync(int clientNo, string name, string value)
        {
            using (var stream = new MemoryStream())
            using (var writer = new BinaryWriter(stream))
            {
                writer.Write(BinarySerializer.MSG_CLIENT_VAR_SYNC);
                writer.Write((ushort)1);
                writer.Write((ushort)clientNo);
                writer.Write((ushort)1);
                WriteShortString(writer, name);
                var valueBytes = Encoding.UTF8.GetBytes(value);
                writer.Write((ushort)valueBytes.Length);
                writer.Write(valueBytes);
                writer.Write((ushort)clientNo); // last writer
                writer.Flush();
                return stream.ToArray();
            }
        }

        /// <summary>A room pose snapshot listing the given clients, each without any valid pose part.</summary>
        private static byte[] RoomPose(params int[] clientNos)
        {
            using (var stream = new MemoryStream())
            using (var writer = new BinaryWriter(stream))
            {
                writer.Write(BinarySerializer.MSG_ROOM_POSE);
                writer.Write(BinarySerializer.PROTOCOL_VERSION);
                WriteShortString(writer, RoomId);
                writer.Write(1.0d); // broadcast time
                writer.Write((ushort)clientNos.Length);
                foreach (var clientNo in clientNos)
                {
                    writer.Write((ushort)clientNo);
                    writer.Write(1.0d); // pose time
                    writer.Write((ushort)1); // pose sequence
                    writer.Write((byte)0); // pose flags
                    writer.Write((byte)0); // encoding flags
                    writer.Write((byte)0); // virtual transform count
                }
                writer.Flush();
                return stream.ToArray();
            }
        }

        private static void WriteShortString(BinaryWriter writer, string value)
        {
            var bytes = Encoding.UTF8.GetBytes(value);
            writer.Write((byte)bytes.Length);
            writer.Write(bytes);
        }
    }
}
