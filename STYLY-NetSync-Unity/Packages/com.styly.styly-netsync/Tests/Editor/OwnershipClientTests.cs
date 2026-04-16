// OwnershipClientTests.cs
// Acquire/release/grant/deny/timeout + snapshot hand-off coverage.

using System.Collections.Generic;
using NUnit.Framework;
using Styly.NetSync;
using Styly.NetSync.Internal;
using UnityEditor;
using UnityEngine;

namespace Styly.NetSync.Tests.EditorTests
{
    [TestFixture]
    public class OwnershipClientTests
    {
        private sealed class StubTransport : IReplicationTransport
        {
            public int SendCount;
            public byte[] LastPayload;
            public bool Succeed = true;

            public bool SendControl(string roomId, byte[] payload)
            {
                SendCount++;
                LastPayload = payload;
                return Succeed;
            }
        }

        private sealed class FakeClock : OwnershipClient.IClock
        {
            public float Now { get; set; }
        }

        private const int LocalClientNo = 42;
        private const int OtherClientNo = 99;

        private GameObject _go;
        private NetSyncObject _obj;
        private ulong _entityId;

        [SetUp]
        public void SetUp()
        {
            _go = new GameObject("OwnershipTestObj");
            _obj = _go.AddComponent<NetSyncObject>();

            SerializedObject so = new SerializedObject(_obj);
            SerializedProperty guidProp = so.FindProperty("_guid");
            guidProp.stringValue = "aabbccdd-1111-2222-3333-445566778899";
            so.ApplyModifiedPropertiesWithoutUndo();

            _entityId = _obj.EntityId;
            EntityRegistry.Instance.Register(_obj);
        }

        [TearDown]
        public void TearDown()
        {
            OwnershipClient.Active = null;
            if (_obj != null)
            {
                EntityRegistry.Instance.Unregister(_obj);
            }
            if (_go != null)
            {
                Object.DestroyImmediate(_go);
            }
        }

        private static OwnershipEventMessage Event(ulong id, int owner, uint epoch, OwnershipResult result)
        {
            return new OwnershipEventMessage
            {
                EntityId = id,
                NewOwnerShortId = (uint)owner,
                NewAuthorityEpoch = epoch,
                Result = result,
                ReasonCode = OwnershipEventReasonCode.None,
            };
        }

        [Test]
        public void AcquireGrant_FlipsIsOwnedAndBumpsEpoch()
        {
            StubTransport transport = new StubTransport();
            FakeClock clock = new FakeClock();
            OwnershipClient client = new OwnershipClient(transport, clock)
            {
                LocalClientNo = LocalClientNo,
            };
            OwnershipClient.Active = client;

            List<OwnershipChangedEvent> perObject = new List<OwnershipChangedEvent>();
            _obj.OnOwnershipChanged += perObject.Add;

            Assert.IsFalse(_obj.IsOwnedByMe);

            bool dispatched = _obj.RequestOwnership();
            Assert.IsTrue(dispatched);
            Assert.AreEqual(1, transport.SendCount);
            Assert.IsTrue(client.HasPending(_entityId));
            // Speculative update forbidden.
            Assert.IsFalse(_obj.IsOwnedByMe);

            client.HandleOwnershipEvent(Event(_entityId, LocalClientNo, 7u, OwnershipResult.Granted));

            Assert.IsTrue(_obj.IsOwnedByMe);
            Assert.AreEqual(7u, _obj.AuthorityEpoch);
            Assert.AreEqual(LocalClientNo, _obj.CurrentOwnerClientNo);
            Assert.AreEqual(1, perObject.Count);
            Assert.AreEqual(OwnershipChangeReason.Granted, perObject[0].Reason);
            Assert.IsFalse(client.HasPending(_entityId));
        }

        [Test]
        public void AcquireDeny_DoesNotFlipOwnership()
        {
            StubTransport transport = new StubTransport();
            FakeClock clock = new FakeClock();
            OwnershipClient client = new OwnershipClient(transport, clock)
            {
                LocalClientNo = LocalClientNo,
            };
            OwnershipClient.Active = client;

            List<OwnershipChangedEvent> perObject = new List<OwnershipChangedEvent>();
            _obj.OnOwnershipChanged += perObject.Add;

            _obj.RequestOwnership();
            // Server rejects: owner stays at 0 (server-owned), epoch 1.
            client.HandleOwnershipEvent(Event(_entityId, 0, 1u, OwnershipResult.Denied));

            Assert.IsFalse(_obj.IsOwnedByMe);
            Assert.AreEqual(0, _obj.CurrentOwnerClientNo);
            Assert.AreEqual(1, perObject.Count);
            Assert.AreEqual(OwnershipChangeReason.Rejected, perObject[0].Reason);
            Assert.IsFalse(client.HasPending(_entityId));
        }

        [Test]
        public void ReleaseSuccess_ReturnsToUnownedAndBumpsEpoch()
        {
            StubTransport transport = new StubTransport();
            FakeClock clock = new FakeClock();
            OwnershipClient client = new OwnershipClient(transport, clock)
            {
                LocalClientNo = LocalClientNo,
            };
            OwnershipClient.Active = client;

            // Establish local ownership.
            _obj.RequestOwnership();
            client.HandleOwnershipEvent(Event(_entityId, LocalClientNo, 3u, OwnershipResult.Granted));
            Assert.IsTrue(_obj.IsOwnedByMe);

            bool dispatched = _obj.ReleaseOwnership();
            Assert.IsTrue(dispatched);
            Assert.AreEqual(2, transport.SendCount);

            client.HandleOwnershipEvent(Event(_entityId, 0, 4u, OwnershipResult.Released));

            Assert.IsFalse(_obj.IsOwnedByMe);
            Assert.AreEqual(0, _obj.CurrentOwnerClientNo);
            Assert.AreEqual(4u, _obj.AuthorityEpoch);
        }

        [Test]
        public void SnapshotEntity_AppliesOwnerAndEpoch()
        {
            StubTransport transport = new StubTransport();
            FakeClock clock = new FakeClock();
            OwnershipClient client = new OwnershipClient(transport, clock)
            {
                LocalClientNo = LocalClientNo,
            };
            OwnershipClient.Active = client;

            EntityRecord rec = new EntityRecord
            {
                EntityId = _entityId,
                AuthorityEpoch = 9u,
                OwnerShortId = (uint)OtherClientNo,
                PoseSeq = 1,
                ChangedMask = ChangedMask.None,
                State = TransformState.Identity,
            };
            client.HandleSnapshotEntity(rec);

            Assert.AreEqual(OtherClientNo, _obj.CurrentOwnerClientNo);
            Assert.AreEqual(9u, _obj.AuthorityEpoch);
            Assert.IsFalse(_obj.IsOwnedByMe);
        }

        [Test]
        public void PendingRequest_TimesOut_FiresTimeoutAndClears()
        {
            StubTransport transport = new StubTransport();
            FakeClock clock = new FakeClock { Now = 0f };
            OwnershipClient client = new OwnershipClient(transport, clock)
            {
                LocalClientNo = LocalClientNo,
            };
            OwnershipClient.Active = client;

            List<OwnershipChangedEvent> events = new List<OwnershipChangedEvent>();
            _obj.OnOwnershipChanged += events.Add;

            _obj.RequestOwnership();
            Assert.IsTrue(client.HasPending(_entityId));

            // Advance past the timeout.
            clock.Now = OwnershipClient.PendingRequestTimeoutSeconds + 0.1f;
            client.Pump();

            Assert.IsFalse(client.HasPending(_entityId));
            Assert.AreEqual(1, events.Count);
            Assert.AreEqual(OwnershipChangeReason.Timeout, events[0].Reason);
            Assert.IsFalse(_obj.IsOwnedByMe);
        }

        [Test]
        public void RequestWithoutLocalClientNo_ReturnsFalse()
        {
            StubTransport transport = new StubTransport();
            OwnershipClient client = new OwnershipClient(transport, new FakeClock());
            OwnershipClient.Active = client;

            bool result = _obj.RequestOwnership();
            Assert.IsFalse(result);
            Assert.AreEqual(0, transport.SendCount);
        }
    }
}
