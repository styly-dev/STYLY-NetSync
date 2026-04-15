using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;
using Styly.NetSync.Internal;

namespace Styly.NetSync.Tests.Editor
{
    // Round-trip tests for the replication protocol v1 MessageCodec.
    // Byte layout must match docs/replication-protocol-v1.md and
    // STYLY-NetSync-Server/src/styly_netsync/replication/message_codec.py.
    public class MessageCodecTests
    {
        private static readonly ITransformCodec Codec = TransformCodecV1.Instance;

        private static TransformState SampleState(float seed)
        {
            return new TransformState
            {
                Position = new Vector3(seed, seed + 1f, seed + 2f),
                Rotation = new Quaternion(0f, 0f, 0f, 1f),
                Scale = new Vector3(seed + 0.1f, seed + 0.2f, seed + 0.3f),
            };
        }

        [Test]
        public void JoinRoomRoundTrip()
        {
            var msg = new JoinRoomMessage
            {
                RoomId = "lobby",
                DeviceId = "device-abc",
                SceneHash = "abc123",
            };
            var bytes = MessageCodec.EncodeJoinRoom(msg);
            Assert.AreEqual(ReplMessageIds.JoinRoom, bytes[0]);
            Assert.AreEqual(ReplMessageIds.ReplProtocolVersion, bytes[1]);
            var decoded = MessageCodec.DecodeJoinRoom(bytes);
            Assert.AreEqual(msg.RoomId, decoded.RoomId);
            Assert.AreEqual(msg.DeviceId, decoded.DeviceId);
            Assert.AreEqual(msg.SceneHash, decoded.SceneHash);
        }

        [Test]
        public void JoinRoomEmptyStrings()
        {
            var msg = new JoinRoomMessage { RoomId = "", DeviceId = "", SceneHash = "" };
            var decoded = MessageCodec.DecodeJoinRoom(MessageCodec.EncodeJoinRoom(msg));
            Assert.AreEqual(string.Empty, decoded.RoomId);
            Assert.AreEqual(string.Empty, decoded.DeviceId);
            Assert.AreEqual(string.Empty, decoded.SceneHash);
        }

        [Test]
        public void JoinRejectRoundTrip()
        {
            foreach (JoinRejectReason reason in System.Enum.GetValues(typeof(JoinRejectReason)))
            {
                var msg = new JoinRejectMessage
                {
                    RoomId = "room-x",
                    Reason = reason,
                    ReasonText = $"reason={reason}",
                };
                var bytes = MessageCodec.EncodeJoinReject(msg);
                Assert.AreEqual(ReplMessageIds.JoinReject, bytes[0]);
                var decoded = MessageCodec.DecodeJoinReject(bytes);
                Assert.AreEqual(msg.RoomId, decoded.RoomId);
                Assert.AreEqual(msg.Reason, decoded.Reason);
                Assert.AreEqual(msg.ReasonText, decoded.ReasonText);
            }
        }

        [Test]
        public void JoinRejectUnknownReasonCoerced()
        {
            // Synthesize a payload with an unknown reason code; decoder must
            // coerce to Unspecified rather than throw, so forward-compatible
            // clients still surface the reason text.
            var bytes = MessageCodec.EncodeJoinReject(new JoinRejectMessage
            {
                RoomId = "room-x",
                Reason = JoinRejectReason.Unspecified,
                ReasonText = "future reason",
            });
            // Layout: header(2) + roomId_len(1) + roomId(6) + reason(1) + ...
            int reasonOffset = 2 + 1 + "room-x".Length;
            bytes[reasonOffset] = 200; // not defined in the enum
            UnityEngine.TestTools.LogAssert.Expect(
                UnityEngine.LogType.Warning,
                new System.Text.RegularExpressions.Regex("Unknown JoinRejectReason"));
            var decoded = MessageCodec.DecodeJoinReject(bytes);
            Assert.AreEqual(JoinRejectReason.Unspecified, decoded.Reason);
            Assert.AreEqual("future reason", decoded.ReasonText);
        }

        [Test]
        public void RoomSnapshotRoundTrip()
        {
            var msg = new RoomSnapshotMessage
            {
                RoomId = "room-1",
                BaseRoomSeq = 123456,
                ServerTimeUs = 1_700_000_000_000_000UL,
                YourClientNo = 42u,
                Entities = new List<EntityRecord>
                {
                    new EntityRecord
                    {
                        EntityId = 0xDEADBEEFCAFEBABEUL,
                        AuthorityEpoch = 7,
                        OwnerShortId = 42,
                        PoseSeq = 9,
                        ChangedMask = ChangedMask.All,
                        State = SampleState(1f),
                    },
                    new EntityRecord
                    {
                        EntityId = 1,
                        AuthorityEpoch = 0,
                        OwnerShortId = 0,
                        PoseSeq = 0,
                        ChangedMask = ChangedMask.Position,
                        State = SampleState(2f),
                    },
                },
            };
            var bytes = MessageCodec.EncodeRoomSnapshot(msg, Codec);
            Assert.AreEqual(ReplMessageIds.RoomSnapshot, bytes[0]);
            var decoded = MessageCodec.DecodeRoomSnapshot(bytes, Codec);
            Assert.AreEqual(msg.RoomId, decoded.RoomId);
            Assert.AreEqual(msg.BaseRoomSeq, decoded.BaseRoomSeq);
            Assert.AreEqual(msg.ServerTimeUs, decoded.ServerTimeUs);
            Assert.AreEqual(msg.YourClientNo, decoded.YourClientNo);
            Assert.AreEqual(2, decoded.Entities.Count);
            Assert.AreEqual(msg.Entities[0].State.Position, decoded.Entities[0].State.Position);
            Assert.AreEqual(msg.Entities[0].State.Scale, decoded.Entities[0].State.Scale);
            // Position-only second entity should decode scale as default (1,1,1).
            Assert.AreEqual(msg.Entities[1].State.Position, decoded.Entities[1].State.Position);
            Assert.AreEqual(Vector3.one, decoded.Entities[1].State.Scale);
        }

        [Test]
        public void OwnershipRequestRoundTrip()
        {
            var msg = new OwnershipRequestMessage
            {
                EntityId = 0x1122334455667788UL,
                RequesterShortId = 12,
                ExpectedEpoch = 3,
            };
            var bytes = MessageCodec.EncodeOwnershipRequest(msg);
            Assert.AreEqual(ReplMessageIds.OwnershipRequest, bytes[0]);
            var decoded = MessageCodec.DecodeOwnershipRequest(bytes);
            Assert.AreEqual(msg.EntityId, decoded.EntityId);
            Assert.AreEqual(msg.RequesterShortId, decoded.RequesterShortId);
            Assert.AreEqual(msg.ExpectedEpoch, decoded.ExpectedEpoch);
        }

        [Test]
        public void OwnershipEventRoundTrip()
        {
            var cases = new (OwnershipResult result, OwnershipEventReasonCode reason, uint owner)[]
            {
                (OwnershipResult.Granted, OwnershipEventReasonCode.None, 7u),
                (OwnershipResult.Released, OwnershipEventReasonCode.None, 0u),
                (OwnershipResult.Expired, OwnershipEventReasonCode.None, 0u),
                (OwnershipResult.Denied, OwnershipEventReasonCode.AlreadyOwned, 0u),
                (OwnershipResult.Denied, OwnershipEventReasonCode.NotOwner, 0u),
                (OwnershipResult.Denied, OwnershipEventReasonCode.EpochMismatch, 0u),
                (OwnershipResult.Denied, OwnershipEventReasonCode.LeaseExpired, 0u),
            };
            foreach (var c in cases)
            {
                var msg = new OwnershipEventMessage
                {
                    EntityId = 99,
                    NewOwnerShortId = c.owner,
                    NewAuthorityEpoch = 4,
                    Result = c.result,
                    ReasonCode = c.reason,
                };
                var bytes = MessageCodec.EncodeOwnershipEvent(msg);
                Assert.AreEqual(ReplMessageIds.OwnershipEvent, bytes[0]);
                var decoded = MessageCodec.DecodeOwnershipEvent(bytes);
                Assert.AreEqual(msg.EntityId, decoded.EntityId);
                Assert.AreEqual(msg.NewOwnerShortId, decoded.NewOwnerShortId);
                Assert.AreEqual(msg.NewAuthorityEpoch, decoded.NewAuthorityEpoch);
                Assert.AreEqual(msg.Result, decoded.Result);
                Assert.AreEqual(msg.ReasonCode, decoded.ReasonCode);
            }
        }

        [Test]
        public void OwnershipEventUnknownReasonCodeCoerced()
        {
            var bytes = MessageCodec.EncodeOwnershipEvent(new OwnershipEventMessage
            {
                EntityId = 1,
                NewOwnerShortId = 0,
                NewAuthorityEpoch = 0,
                Result = OwnershipResult.Denied,
                ReasonCode = OwnershipEventReasonCode.None,
            });
            // header(2) + entityId(8) + owner(4) + epoch(4) + result(1) + reasonCode(1)
            int reasonCodeOffset = 2 + 8 + 4 + 4 + 1;
            bytes[reasonCodeOffset] = 200;
            UnityEngine.TestTools.LogAssert.Expect(
                UnityEngine.LogType.Warning,
                new System.Text.RegularExpressions.Regex("Unknown OwnershipEventReasonCode"));
            var decoded = MessageCodec.DecodeOwnershipEvent(bytes);
            Assert.AreEqual(OwnershipEventReasonCode.None, decoded.ReasonCode);
            Assert.AreEqual(OwnershipResult.Denied, decoded.Result);
        }

        [Test]
        public void OwnershipEventUnknownResultThrows()
        {
            var bytes = MessageCodec.EncodeOwnershipEvent(new OwnershipEventMessage
            {
                EntityId = 1,
                NewOwnerShortId = 0,
                NewAuthorityEpoch = 0,
                Result = OwnershipResult.Granted,
                ReasonCode = OwnershipEventReasonCode.None,
            });
            int resultOffset = 2 + 8 + 4 + 4;
            bytes[resultOffset] = 200;
            Assert.Throws<System.IO.InvalidDataException>(
                () => MessageCodec.DecodeOwnershipEvent(bytes));
        }

        [Test]
        public void ResyncRequestRoundTrip()
        {
            var msg = new ResyncRequestMessage { LastAppliedRoomSeq = 0xDEADBEEFu };
            var bytes = MessageCodec.EncodeResyncRequest(msg);
            Assert.AreEqual(ReplMessageIds.ResyncRequest, bytes[0]);
            var decoded = MessageCodec.DecodeResyncRequest(bytes);
            Assert.AreEqual(msg.LastAppliedRoomSeq, decoded.LastAppliedRoomSeq);
        }

        [Test]
        public void ResyncRequestZero()
        {
            // A fresh client with no applied batches uses 0 to ask for everything.
            var msg = new ResyncRequestMessage { LastAppliedRoomSeq = 0u };
            var decoded = MessageCodec.DecodeResyncRequest(
                MessageCodec.EncodeResyncRequest(msg));
            Assert.AreEqual(0u, decoded.LastAppliedRoomSeq);
        }

        [Test]
        public void ResyncReplyRoundTrip()
        {
            var msg = new ResyncReplyMessage
            {
                RoomId = "room-resync",
                BaseRoomSeq = 98765u,
                ServerTimeUs = 1_700_000_000_000_500UL,
                Entities = new List<EntityRecord>
                {
                    new EntityRecord
                    {
                        EntityId = 10,
                        AuthorityEpoch = 1,
                        OwnerShortId = 2,
                        PoseSeq = 3,
                        ChangedMask = ChangedMask.Rotation,
                        State = SampleState(3f),
                    },
                },
            };
            var bytes = MessageCodec.EncodeResyncReply(msg, Codec);
            Assert.AreEqual(ReplMessageIds.ResyncReply, bytes[0]);
            var decoded = MessageCodec.DecodeResyncReply(bytes, Codec);
            Assert.AreEqual(msg.RoomId, decoded.RoomId);
            Assert.AreEqual(msg.BaseRoomSeq, decoded.BaseRoomSeq);
            Assert.AreEqual(msg.ServerTimeUs, decoded.ServerTimeUs);
            Assert.AreEqual(1, decoded.Entities.Count);
            Assert.AreEqual(ChangedMask.Rotation, decoded.Entities[0].ChangedMask);
            Assert.AreEqual(Vector3.zero, decoded.Entities[0].State.Position);
            Assert.AreEqual(Vector3.one, decoded.Entities[0].State.Scale);
            Assert.AreEqual(msg.Entities[0].State.Rotation, decoded.Entities[0].State.Rotation);
        }

        [Test]
        public void StateBatchRoundTrip()
        {
            var msg = new StateBatchMessage
            {
                RoomSeq = 42,
                ServerTimeUs = 1_700_000_000_000_000UL,
                Updates = new List<StateUpdate>
                {
                    new StateUpdate
                    {
                        EntityId = 1,
                        AuthorityEpoch = 5,
                        PoseSeq = 10,
                        Flags = StateFlags.Keyframe | StateFlags.Teleport,
                        ChangedMask = ChangedMask.All,
                        State = SampleState(4f),
                    },
                    new StateUpdate
                    {
                        EntityId = 2,
                        AuthorityEpoch = 5,
                        PoseSeq = 11,
                        Flags = StateFlags.Heartbeat,
                        ChangedMask = ChangedMask.None,
                        State = TransformState.Identity,
                    },
                },
            };
            var bytes = MessageCodec.EncodeStateBatch(msg, Codec);
            Assert.AreEqual(ReplMessageIds.StateBatch, bytes[0]);
            var decoded = MessageCodec.DecodeStateBatch(bytes, Codec);
            Assert.AreEqual(42u, decoded.RoomSeq);
            Assert.AreEqual(1_700_000_000_000_000UL, decoded.ServerTimeUs);
            Assert.AreEqual(2, decoded.Updates.Count);
            Assert.AreEqual(
                StateFlags.Keyframe | StateFlags.Teleport, decoded.Updates[0].Flags);
            Assert.AreEqual(msg.Updates[0].State.Position, decoded.Updates[0].State.Position);
            Assert.AreEqual(StateFlags.Heartbeat, decoded.Updates[1].Flags);
            Assert.AreEqual(ChangedMask.None, decoded.Updates[1].ChangedMask);
        }

        [Test]
        public void StateBatchEmpty()
        {
            var msg = new StateBatchMessage
            {
                RoomSeq = 0,
                ServerTimeUs = 0UL,
                Updates = new List<StateUpdate>(),
            };
            var decoded = MessageCodec.DecodeStateBatch(
                MessageCodec.EncodeStateBatch(msg, Codec), Codec);
            Assert.AreEqual(0u, decoded.RoomSeq);
            Assert.AreEqual(0UL, decoded.ServerTimeUs);
            Assert.AreEqual(0, decoded.Updates.Count);
        }

        [Test]
        public void RejectUnknownVersion()
        {
            var bytes = MessageCodec.EncodeJoinRoom(
                new JoinRoomMessage { RoomId = "a", DeviceId = "b", SceneHash = "c" });
            bytes[1] = 99;
            Assert.Throws<System.IO.InvalidDataException>(
                () => MessageCodec.DecodeJoinRoom(bytes));
        }

        [Test]
        public void RejectWrongMessageType()
        {
            var bytes = MessageCodec.EncodeJoinRoom(
                new JoinRoomMessage { RoomId = "a", DeviceId = "b", SceneHash = "c" });
            Assert.Throws<System.IO.InvalidDataException>(
                () => MessageCodec.DecodeOwnershipRequest(bytes));
        }

        [Test]
        public void PeekMessageTypeEmpty()
        {
            Assert.AreEqual(0, MessageCodec.PeekMessageType(new byte[0]));
            Assert.AreEqual(0, MessageCodec.PeekMessageType(null));
        }
    }
}
