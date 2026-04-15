// ReplicationResyncTests.cs
// Gap / flood / reconnect resync triggers plus RESYNC_REPLY apply.

using System.Collections.Generic;
using NUnit.Framework;
using Styly.NetSync;
using Styly.NetSync.Internal;
using UnityEditor;
using UnityEngine;

namespace Styly.NetSync.Tests.EditorTests
{
    [TestFixture]
    public class ReplicationResyncTests
    {
        private sealed class StubTransport : IReplicationTransport
        {
            public List<(string RoomId, byte[] Payload)> Sent = new List<(string, byte[])>();
            public bool SendControl(string roomId, byte[] payload)
            {
                Sent.Add((roomId, payload));
                return true;
            }
            public int CountByType(byte msgType)
            {
                int n = 0;
                for (int i = 0; i < Sent.Count; i++)
                {
                    if (Sent[i].Payload != null && Sent[i].Payload.Length > 0 && Sent[i].Payload[0] == msgType)
                    {
                        n++;
                    }
                }
                return n;
            }
        }

        private GameObject _go;
        private NetSyncObject _obj;
        private ulong _entityId;

        [SetUp]
        public void SetUp()
        {
            _go = new GameObject("ResyncTestObj");
            _obj = _go.AddComponent<NetSyncObject>();
            SerializedObject so = new SerializedObject(_obj);
            so.FindProperty("_guid").stringValue = "f0f0f0f0-0000-0000-0000-000000000001";
            so.ApplyModifiedPropertiesWithoutUndo();
            _entityId = _obj.EntityId;
            EntityRegistry.Instance.Register(_obj);
        }

        [TearDown]
        public void TearDown()
        {
            if (_obj != null) EntityRegistry.Instance.Unregister(_obj);
            if (_go != null) Object.DestroyImmediate(_go);
        }

        private ReplicationClient MakeJoinedClient(StubTransport tx, uint baseRoomSeq = 100)
        {
            ReplicationClient client = new ReplicationClient(tx, TransformCodecV1.Instance);
            client.BeginJoin("room-A", "dev", "scene");
            client.TestInjectRoomSnapshot(new RoomSnapshotMessage
            {
                RoomId = "room-A",
                BaseRoomSeq = baseRoomSeq,
                ServerTimeUs = 0,
                YourClientNo = 7,
                Entities = new List<EntityRecord>(),
            });
            Assert.AreEqual(JoinState.Joined, client.State);
            return client;
        }

        private static StateBatchMessage Batch(uint roomSeq, ulong entityId, Vector3 pos)
        {
            return new StateBatchMessage
            {
                RoomSeq = roomSeq,
                ServerTimeUs = 0,
                Updates = new List<StateUpdate>
                {
                    new StateUpdate
                    {
                        EntityId = entityId,
                        AuthorityEpoch = 0,
                        PoseSeq = (ushort)roomSeq,
                        Flags = StateFlags.None,
                        ChangedMask = ChangedMask.Position,
                        State = new TransformState { Position = pos, Rotation = Quaternion.identity, Scale = Vector3.one },
                    },
                },
            };
        }

        [Test]
        public void GapTrigger_DispatchesResyncRequest()
        {
            StubTransport tx = new StubTransport();
            ReplicationClient client = MakeJoinedClient(tx, baseRoomSeq: 100);
            client.GapTolerance = 16;

            int sentBefore = tx.CountByType(ReplMessageIds.ResyncRequest);
            int resyncStartedCount = 0;
            client.OnResyncStarted += _ => resyncStartedCount++;

            // Apply a normal batch first to establish a baseline.
            client.TestInjectStateBatch(Batch(101, _entityId, Vector3.zero));
            Assert.AreEqual(101u, client.HighestAppliedRoomSeq);

            // Large gap: jumps to 200, which is > 101 + 16.
            client.TestInjectStateBatch(Batch(200, _entityId, Vector3.one));

            Assert.AreEqual(1, resyncStartedCount, "Gap must fire OnResyncStarted exactly once.");
            Assert.AreEqual(sentBefore + 1, tx.CountByType(ReplMessageIds.ResyncRequest),
                "Exactly one RESYNC_REQUEST frame must be on the wire.");
            Assert.IsTrue(client.TestResyncPending);
        }

        [Test]
        public void UnknownEntityFlood_DispatchesResyncRequest()
        {
            StubTransport tx = new StubTransport();
            ReplicationClient client = MakeJoinedClient(tx);
            client.UnknownEntityFloodThreshold = 3;

            int resyncStartedCount = 0;
            client.OnResyncStarted += _ => resyncStartedCount++;

            // Three batches referencing an unknown entity in a row.
            ulong ghostId = 0xDEADBEEFCAFEBABEUL;
            Assert.AreNotEqual(_entityId, ghostId, "Pick a different id for this test.");

            client.TestInjectStateBatch(Batch(101, ghostId, Vector3.zero));
            client.TestInjectStateBatch(Batch(102, ghostId, Vector3.one));
            Assert.AreEqual(0, resyncStartedCount, "Threshold not yet reached.");
            client.TestInjectStateBatch(Batch(103, ghostId, Vector3.up));

            Assert.AreEqual(1, resyncStartedCount);
            Assert.IsTrue(client.TestResyncPending);
        }

        [Test]
        public void ResyncReply_AppliesAndFiresCompleted()
        {
            StubTransport tx = new StubTransport();
            ReplicationClient client = MakeJoinedClient(tx, baseRoomSeq: 100);
            client.RequestResync();
            Assert.IsTrue(client.TestResyncPending);

            int completedCount = 0;
            uint completedArg = 0;
            client.OnResyncCompleted += seq => { completedCount++; completedArg = seq; };

            const uint resyncBaseRoomSeq = 150;
            client.TestInjectResyncReply(new ResyncReplyMessage
            {
                RoomId = "room-A",
                BaseRoomSeq = resyncBaseRoomSeq,
                ServerTimeUs = 0,
                Entities = new List<EntityRecord>
                {
                    new EntityRecord
                    {
                        EntityId = _entityId,
                        AuthorityEpoch = 5,
                        OwnerShortId = 0,
                        PoseSeq = 1,
                        ChangedMask = ChangedMask.Position,
                        State = new TransformState
                        {
                            Position = new Vector3(42f, 0f, 0f),
                            Rotation = Quaternion.identity,
                            Scale = Vector3.one,
                        },
                    },
                },
            });

            Assert.IsFalse(client.TestResyncPending);
            Assert.AreEqual(1, completedCount);
            Assert.AreEqual(resyncBaseRoomSeq, completedArg,
                "Resync advances HighestAppliedRoomSeq to the reply's BaseRoomSeq.");
            Assert.AreEqual(resyncBaseRoomSeq, client.HighestAppliedRoomSeq);
            // Transform applied from the reply's entity record.
            Assert.AreEqual(new Vector3(42f, 0f, 0f), _obj.transform.localPosition);
        }

        [Test]
        public void Reset_ClearsBufferAndRoomSeq()
        {
            StubTransport tx = new StubTransport();
            ReplicationClient client = MakeJoinedClient(tx, baseRoomSeq: 100);
            PoseBuffer buf = new PoseBuffer();
            client.Buffer = buf;

            // Seed the buffer via a state batch.
            client.TestInjectStateBatch(Batch(101, _entityId, Vector3.one));
            Assert.AreEqual(101u, client.HighestAppliedRoomSeq);

            client.Reset();
            Assert.AreEqual(JoinState.Disconnected, client.State);
            Assert.AreEqual(0u, client.HighestAppliedRoomSeq);
            Assert.AreEqual(0, buf.Count(_entityId));
            Assert.IsFalse(client.TestResyncPending);
        }

        [Test]
        public void NoDoubleDispatchWhileResyncPending()
        {
            StubTransport tx = new StubTransport();
            ReplicationClient client = MakeJoinedClient(tx, baseRoomSeq: 100);
            client.GapTolerance = 4;

            int starts = 0;
            client.OnResyncStarted += _ => starts++;

            client.TestInjectStateBatch(Batch(101, _entityId, Vector3.zero));
            client.TestInjectStateBatch(Batch(200, _entityId, Vector3.one)); // trigger 1
            client.TestInjectStateBatch(Batch(300, _entityId, Vector3.up));  // still pending
            client.TestInjectStateBatch(Batch(400, _entityId, Vector3.forward));

            Assert.AreEqual(1, starts,
                "While a resync is in flight, additional gaps must not re-trigger requests.");
        }
    }
}
