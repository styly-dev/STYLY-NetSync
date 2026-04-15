// ReplicationClientTests.cs
// Validates the Phase 2 join pipeline: STATE_BATCH pre-snapshot buffering,
// snapshot apply, and buffered replay ordering per spec §10.2.

using System.Collections.Generic;
using NUnit.Framework;
using Styly.NetSync;
using Styly.NetSync.Internal;
using UnityEditor;
using UnityEngine;

namespace Styly.NetSync.Tests.EditorTests
{
    [TestFixture]
    public class ReplicationClientTests
    {
        private sealed class StubTransport : IReplicationTransport
        {
            public int SendCount;
            public string LastRoomId;
            public byte[] LastPayload;

            public bool SendControl(string roomId, byte[] payload)
            {
                SendCount++;
                LastRoomId = roomId;
                LastPayload = payload;
                return true;
            }
        }

        private GameObject _go;
        private NetSyncObject _obj;
        private ulong _entityId;

        [SetUp]
        public void SetUp()
        {
            _go = new GameObject("ReplTestObj");
            _obj = _go.AddComponent<NetSyncObject>();
            // Force a deterministic GUID and register with the registry.
            SerializedObject so = new SerializedObject(_obj);
            SerializedProperty guidProp = so.FindProperty("_guid");
            guidProp.stringValue = "11111111-2222-3333-4444-555566667777";
            so.ApplyModifiedPropertiesWithoutUndo();

            _entityId = _obj.EntityId;
            EntityRegistry.Instance.Register(_obj);
        }

        [TearDown]
        public void TearDown()
        {
            if (_obj != null)
            {
                EntityRegistry.Instance.Unregister(_obj);
            }
            if (_go != null)
            {
                Object.DestroyImmediate(_go);
            }
        }

        private static StateBatchMessage MakeBatch(uint serverTick, ulong entityId, Vector3 pos)
        {
            return new StateBatchMessage
            {
                RoomSeq = serverTick,
                Updates = new List<StateUpdate>
                {
                    new StateUpdate
                    {
                        EntityId = entityId,
                        AuthorityEpoch = 1,
                        PoseSeq = (ushort)serverTick,
                        Flags = StateFlags.None,
                        ChangedMask = ChangedMask.Position,
                        State = new TransformState
                        {
                            Position = pos,
                            Rotation = Quaternion.identity,
                            Scale = Vector3.one,
                        },
                    },
                },
            };
        }

        private static RoomSnapshotMessage MakeSnapshot(uint serverTick, ulong entityId, Vector3 pos)
        {
            return new RoomSnapshotMessage
            {
                RoomId = "room-A",
                BaseRoomSeq = serverTick,
                Entities = new List<EntityRecord>
                {
                    new EntityRecord
                    {
                        EntityId = entityId,
                        AuthorityEpoch = 1,
                        OwnerShortId = 0,
                        PoseSeq = 1,
                        ChangedMask = ChangedMask.Position,
                        State = new TransformState
                        {
                            Position = pos,
                            Rotation = Quaternion.identity,
                            Scale = Vector3.one,
                        },
                    },
                },
            };
        }

        [Test]
        public void JoinFlow_BuffersAndReplaysOnlyBatchesAfterBaseTick()
        {
            // Seq 5, 7, 3 pre-snapshot; snapshot base = 6. Expect only seq 7
            // to be replayed, and final transform reflects seq 7's position.
            StubTransport transport = new StubTransport();
            ReplicationClient client = new ReplicationClient(transport, new TransformCodecV1());

            List<JoinState> observed = new List<JoinState>();
            client.JoinStateChanged += s => observed.Add(s);

            client.BeginJoin("room-A", "device-1", "hash-xyz");
            Assert.AreEqual(JoinState.Joining, client.State);
            Assert.AreEqual(1, transport.SendCount, "JOIN_ROOM should have been sent exactly once.");

            // Pre-snapshot batches in arrival order 5, 7, 3.
            client.TestInjectStateBatch(MakeBatch(5, _entityId, new Vector3(5f, 0f, 0f)));
            client.TestInjectStateBatch(MakeBatch(7, _entityId, new Vector3(7f, 0f, 0f)));
            client.TestInjectStateBatch(MakeBatch(3, _entityId, new Vector3(3f, 0f, 0f)));

            Assert.AreEqual(3, client.TestPreSnapshotBuffer.Count,
                "All pre-snapshot batches should buffer while Joining.");
            // Transform must NOT have moved yet.
            Assert.AreEqual(Vector3.zero, _obj.transform.localPosition,
                "No batch should apply before snapshot arrives.");

            // Snapshot with baseRoomSeq=6, puts entity at (100,0,0).
            client.TestInjectRoomSnapshot(MakeSnapshot(6, _entityId, new Vector3(100f, 0f, 0f)));

            Assert.AreEqual(JoinState.Joined, client.State);
            Assert.AreEqual(0, client.TestPreSnapshotBuffer.Count,
                "Buffer should drain on snapshot apply.");
            Assert.AreEqual(7u, client.HighestAppliedRoomSeq,
                "Highest applied room seq should be the replayed batch's RoomSeq.");
            // Expect seq 7 replayed, positions 5 and 3 dropped.
            Assert.AreEqual(new Vector3(7f, 0f, 0f), _obj.transform.localPosition);

            // State-change events: Joining then Joined.
            Assert.AreEqual(new List<JoinState> { JoinState.Joining, JoinState.Joined }, observed);
        }

        [Test]
        public void Snapshot_RoomIdMismatch_TransitionsToRejected()
        {
            StubTransport transport = new StubTransport();
            ReplicationClient client = new ReplicationClient(transport, new TransformCodecV1());
            client.BeginJoin("room-A", "d", "h");

            RoomSnapshotMessage bad = MakeSnapshot(1, _entityId, Vector3.zero);
            bad.RoomId = "room-B";
            client.TestInjectRoomSnapshot(bad);

            Assert.AreEqual(JoinState.Rejected, client.State);
            Assert.AreEqual(JoinRejectReason.RoomIdMismatch, client.LastRejectReason);
        }

        [Test]
        public void PostJoin_BatchApplied_StaleBatchDropped()
        {
            StubTransport transport = new StubTransport();
            ReplicationClient client = new ReplicationClient(transport, new TransformCodecV1());
            client.BeginJoin("room-A", "d", "h");
            client.TestInjectRoomSnapshot(MakeSnapshot(10, _entityId, Vector3.zero));
            Assert.AreEqual(JoinState.Joined, client.State);

            // Fresh batch applies.
            client.TestInjectStateBatch(MakeBatch(11, _entityId, new Vector3(11f, 0f, 0f)));
            Assert.AreEqual(new Vector3(11f, 0f, 0f), _obj.transform.localPosition);

            // Stale batch at tick 10 is dropped.
            client.TestInjectStateBatch(MakeBatch(10, _entityId, new Vector3(999f, 0f, 0f)));
            Assert.AreEqual(new Vector3(11f, 0f, 0f), _obj.transform.localPosition);
        }
    }
}
