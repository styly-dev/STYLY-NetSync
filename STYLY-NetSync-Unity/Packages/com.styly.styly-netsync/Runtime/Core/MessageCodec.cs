using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace Styly.NetSync.Internal
{
    // Encode/decode for replication-protocol-v1 messages. Matches the byte
    // layout in docs/replication-protocol-v1.md. Little-endian via
    // BinaryWriter/BinaryReader.
    //
    // All messages share the two-byte prefix: msgType (u8) + replVersion (u8).
    // The transform payload inside EntityRecord / StateUpdate delegates to an
    // ITransformCodec implementation (v1: TransformCodecV1).
    public static class MessageCodec
    {
        private const byte ReplVersion = ReplMessageIds.ReplProtocolVersion;

        // --- Primitive helpers ---

        private static void WriteShortString(BinaryWriter writer, string value)
        {
            var bytes = string.IsNullOrEmpty(value)
                ? Array.Empty<byte>()
                : Encoding.UTF8.GetBytes(value);
            if (bytes.Length > byte.MaxValue)
            {
                throw new ArgumentException(
                    $"Short string length {bytes.Length} exceeds 255 bytes");
            }
            writer.Write((byte)bytes.Length);
            if (bytes.Length > 0)
            {
                writer.Write(bytes);
            }
        }

        private static string ReadShortString(BinaryReader reader)
        {
            int length = reader.ReadByte();
            if (length == 0)
            {
                return string.Empty;
            }
            var bytes = reader.ReadBytes(length);
            return Encoding.UTF8.GetString(bytes);
        }

        private static void WriteHeader(BinaryWriter writer, byte messageType)
        {
            writer.Write(messageType);
            writer.Write(ReplVersion);
        }

        private static void ReadAndVerifyHeader(BinaryReader reader, byte expectedType)
        {
            byte type = reader.ReadByte();
            if (type != expectedType)
            {
                throw new InvalidDataException(
                    $"Unexpected message type {type} (expected {expectedType})");
            }
            byte version = reader.ReadByte();
            if (version != ReplVersion)
            {
                throw new InvalidDataException(
                    $"Unsupported replication protocol version {version}");
            }
        }

        private static void WriteEntityRecord(
            BinaryWriter writer, in EntityRecord record, ITransformCodec codec)
        {
            writer.Write(record.EntityId);
            writer.Write(record.AuthorityEpoch);
            writer.Write(record.OwnerShortId);
            writer.Write(record.PoseSeq);
            writer.Write((byte)record.ChangedMask);
            codec.Write(writer, record.ChangedMask, record.State);
        }

        private static EntityRecord ReadEntityRecord(
            BinaryReader reader, ITransformCodec codec)
        {
            var record = new EntityRecord
            {
                EntityId = reader.ReadUInt64(),
                AuthorityEpoch = reader.ReadUInt32(),
                OwnerShortId = reader.ReadUInt32(),
                PoseSeq = reader.ReadUInt16(),
                ChangedMask = (ChangedMask)reader.ReadByte(),
            };
            record.State = codec.Read(reader, record.ChangedMask);
            return record;
        }

        private static void WriteStateUpdate(
            BinaryWriter writer, in StateUpdate update, ITransformCodec codec)
        {
            writer.Write(update.EntityId);
            writer.Write(update.AuthorityEpoch);
            writer.Write(update.PoseSeq);
            writer.Write((byte)update.Flags);
            writer.Write((byte)update.ChangedMask);
            codec.Write(writer, update.ChangedMask, update.State);
        }

        private static StateUpdate ReadStateUpdate(
            BinaryReader reader, ITransformCodec codec)
        {
            var update = new StateUpdate
            {
                EntityId = reader.ReadUInt64(),
                AuthorityEpoch = reader.ReadUInt32(),
                PoseSeq = reader.ReadUInt16(),
                Flags = (StateFlags)reader.ReadByte(),
                ChangedMask = (ChangedMask)reader.ReadByte(),
            };
            update.State = codec.Read(reader, update.ChangedMask);
            return update;
        }

        // --- JOIN_ROOM ---

        public static byte[] EncodeJoinRoom(in JoinRoomMessage message)
        {
            using var ms = new MemoryStream();
            using var writer = new BinaryWriter(ms);
            WriteHeader(writer, ReplMessageIds.JoinRoom);
            WriteShortString(writer, message.RoomId);
            WriteShortString(writer, message.DeviceId);
            WriteShortString(writer, message.SceneHash);
            return ms.ToArray();
        }

        public static JoinRoomMessage DecodeJoinRoom(byte[] data)
        {
            using var ms = new MemoryStream(data);
            using var reader = new BinaryReader(ms);
            ReadAndVerifyHeader(reader, ReplMessageIds.JoinRoom);
            return new JoinRoomMessage
            {
                RoomId = ReadShortString(reader),
                DeviceId = ReadShortString(reader),
                SceneHash = ReadShortString(reader),
            };
        }

        // --- ROOM_SNAPSHOT ---

        public static byte[] EncodeRoomSnapshot(
            in RoomSnapshotMessage message, ITransformCodec codec)
        {
            using var ms = new MemoryStream();
            using var writer = new BinaryWriter(ms);
            WriteHeader(writer, ReplMessageIds.RoomSnapshot);
            WriteShortString(writer, message.RoomId);
            writer.Write(message.BaseRoomSeq);
            writer.Write(message.ServerTimeUs);
            writer.Write(message.YourClientNo);
            var entities = message.Entities;
            uint count = entities == null ? 0u : (uint)entities.Count;
            writer.Write(count);
            if (entities != null)
            {
                for (int i = 0; i < entities.Count; i++)
                {
                    WriteEntityRecord(writer, entities[i], codec);
                }
            }
            return ms.ToArray();
        }

        public static RoomSnapshotMessage DecodeRoomSnapshot(
            byte[] data, ITransformCodec codec)
        {
            using var ms = new MemoryStream(data);
            using var reader = new BinaryReader(ms);
            ReadAndVerifyHeader(reader, ReplMessageIds.RoomSnapshot);
            var msg = new RoomSnapshotMessage
            {
                RoomId = ReadShortString(reader),
                BaseRoomSeq = reader.ReadUInt32(),
                ServerTimeUs = reader.ReadUInt64(),
                YourClientNo = reader.ReadUInt32(),
            };
            uint count = reader.ReadUInt32();
            var entities = new List<EntityRecord>((int)count);
            for (uint i = 0; i < count; i++)
            {
                entities.Add(ReadEntityRecord(reader, codec));
            }
            msg.Entities = entities;
            return msg;
        }

        // --- JOIN_REJECT ---

        public static byte[] EncodeJoinReject(in JoinRejectMessage message)
        {
            using var ms = new MemoryStream();
            using var writer = new BinaryWriter(ms);
            WriteHeader(writer, ReplMessageIds.JoinReject);
            WriteShortString(writer, message.RoomId);
            writer.Write((byte)message.Reason);
            WriteShortString(writer, message.ReasonText);
            return ms.ToArray();
        }

        public static JoinRejectMessage DecodeJoinReject(byte[] data)
        {
            using var ms = new MemoryStream(data);
            using var reader = new BinaryReader(ms);
            ReadAndVerifyHeader(reader, ReplMessageIds.JoinReject);
            var roomId = ReadShortString(reader);
            byte rawReason = reader.ReadByte();
            var reason = IsKnownJoinRejectReason(rawReason)
                ? (JoinRejectReason)rawReason
                : JoinRejectReason.Unspecified;
            if (!IsKnownJoinRejectReason(rawReason))
            {
                UnityEngine.Debug.LogWarning(
                    $"[MessageCodec] Unknown JoinRejectReason {rawReason}; coercing to Unspecified");
            }
            return new JoinRejectMessage
            {
                RoomId = roomId,
                Reason = reason,
                ReasonText = ReadShortString(reader),
            };
        }

        private static bool IsKnownJoinRejectReason(byte raw)
        {
            switch (raw)
            {
                case (byte)JoinRejectReason.SceneHashMismatch:
                case (byte)JoinRejectReason.RoomFull:
                case (byte)JoinRejectReason.ProtocolVersionMismatch:
                case (byte)JoinRejectReason.RoomIdMismatch:
                case (byte)JoinRejectReason.None:
                case (byte)JoinRejectReason.Unspecified:
                    return true;
                default:
                    return false;
            }
        }

        // --- OWNERSHIP_REQUEST ---

        public static byte[] EncodeOwnershipRequest(in OwnershipRequestMessage message)
        {
            using var ms = new MemoryStream();
            using var writer = new BinaryWriter(ms);
            WriteHeader(writer, ReplMessageIds.OwnershipRequest);
            writer.Write(message.EntityId);
            writer.Write(message.RequesterShortId);
            writer.Write(message.ExpectedEpoch);
            return ms.ToArray();
        }

        public static OwnershipRequestMessage DecodeOwnershipRequest(byte[] data)
        {
            using var ms = new MemoryStream(data);
            using var reader = new BinaryReader(ms);
            ReadAndVerifyHeader(reader, ReplMessageIds.OwnershipRequest);
            return new OwnershipRequestMessage
            {
                EntityId = reader.ReadUInt64(),
                RequesterShortId = reader.ReadUInt32(),
                ExpectedEpoch = reader.ReadUInt32(),
            };
        }

        // --- OWNERSHIP_EVENT ---

        public static byte[] EncodeOwnershipEvent(in OwnershipEventMessage message)
        {
            using var ms = new MemoryStream();
            using var writer = new BinaryWriter(ms);
            WriteHeader(writer, ReplMessageIds.OwnershipEvent);
            writer.Write(message.EntityId);
            writer.Write(message.NewOwnerShortId);
            writer.Write(message.NewAuthorityEpoch);
            writer.Write((byte)message.Result);
            writer.Write((byte)message.ReasonCode);
            return ms.ToArray();
        }

        public static OwnershipEventMessage DecodeOwnershipEvent(byte[] data)
        {
            using var ms = new MemoryStream(data);
            using var reader = new BinaryReader(ms);
            ReadAndVerifyHeader(reader, ReplMessageIds.OwnershipEvent);
            var entityId = reader.ReadUInt64();
            var newOwner = reader.ReadUInt32();
            var newEpoch = reader.ReadUInt32();
            byte rawResult = reader.ReadByte();
            byte rawReasonCode = reader.ReadByte();
            if (!IsKnownOwnershipResult(rawResult))
            {
                throw new InvalidDataException(
                    $"Unknown OwnershipResult {rawResult}");
            }
            var reasonCode = IsKnownOwnershipEventReasonCode(rawReasonCode)
                ? (OwnershipEventReasonCode)rawReasonCode
                : OwnershipEventReasonCode.None;
            if (!IsKnownOwnershipEventReasonCode(rawReasonCode))
            {
                UnityEngine.Debug.LogWarning(
                    $"[MessageCodec] Unknown OwnershipEventReasonCode {rawReasonCode}; coercing to None");
            }
            return new OwnershipEventMessage
            {
                EntityId = entityId,
                NewOwnerShortId = newOwner,
                NewAuthorityEpoch = newEpoch,
                Result = (OwnershipResult)rawResult,
                ReasonCode = reasonCode,
            };
        }

        private static bool IsKnownOwnershipResult(byte raw)
        {
            switch (raw)
            {
                case (byte)OwnershipResult.Granted:
                case (byte)OwnershipResult.Denied:
                case (byte)OwnershipResult.Released:
                case (byte)OwnershipResult.Expired:
                    return true;
                default:
                    return false;
            }
        }

        private static bool IsKnownOwnershipEventReasonCode(byte raw)
        {
            switch (raw)
            {
                case (byte)OwnershipEventReasonCode.None:
                case (byte)OwnershipEventReasonCode.AlreadyOwned:
                case (byte)OwnershipEventReasonCode.NotOwner:
                case (byte)OwnershipEventReasonCode.EpochMismatch:
                case (byte)OwnershipEventReasonCode.LeaseExpired:
                case (byte)OwnershipEventReasonCode.Timeout:
                    return true;
                default:
                    return false;
            }
        }

        // --- RESYNC_REQUEST ---

        public static byte[] EncodeResyncRequest(in ResyncRequestMessage message)
        {
            using var ms = new MemoryStream();
            using var writer = new BinaryWriter(ms);
            WriteHeader(writer, ReplMessageIds.ResyncRequest);
            writer.Write(message.LastAppliedRoomSeq);
            return ms.ToArray();
        }

        public static ResyncRequestMessage DecodeResyncRequest(byte[] data)
        {
            using var ms = new MemoryStream(data);
            using var reader = new BinaryReader(ms);
            ReadAndVerifyHeader(reader, ReplMessageIds.ResyncRequest);
            return new ResyncRequestMessage
            {
                LastAppliedRoomSeq = reader.ReadUInt32(),
            };
        }

        // --- RESYNC_REPLY ---

        public static byte[] EncodeResyncReply(
            in ResyncReplyMessage message, ITransformCodec codec)
        {
            using var ms = new MemoryStream();
            using var writer = new BinaryWriter(ms);
            WriteHeader(writer, ReplMessageIds.ResyncReply);
            WriteShortString(writer, message.RoomId);
            writer.Write(message.BaseRoomSeq);
            writer.Write(message.ServerTimeUs);
            var entities = message.Entities;
            uint count = entities == null ? 0u : (uint)entities.Count;
            writer.Write(count);
            if (entities != null)
            {
                for (int i = 0; i < entities.Count; i++)
                {
                    WriteEntityRecord(writer, entities[i], codec);
                }
            }
            return ms.ToArray();
        }

        public static ResyncReplyMessage DecodeResyncReply(
            byte[] data, ITransformCodec codec)
        {
            using var ms = new MemoryStream(data);
            using var reader = new BinaryReader(ms);
            ReadAndVerifyHeader(reader, ReplMessageIds.ResyncReply);
            var msg = new ResyncReplyMessage
            {
                RoomId = ReadShortString(reader),
                BaseRoomSeq = reader.ReadUInt32(),
                ServerTimeUs = reader.ReadUInt64(),
            };
            uint count = reader.ReadUInt32();
            var entities = new List<EntityRecord>((int)count);
            for (uint i = 0; i < count; i++)
            {
                entities.Add(ReadEntityRecord(reader, codec));
            }
            msg.Entities = entities;
            return msg;
        }

        // --- STATE_BATCH ---

        public static byte[] EncodeStateBatch(
            in StateBatchMessage message, ITransformCodec codec)
        {
            using var ms = new MemoryStream();
            using var writer = new BinaryWriter(ms);
            WriteHeader(writer, ReplMessageIds.StateBatch);
            writer.Write(message.RoomSeq);
            writer.Write(message.ServerTimeUs);
            var updates = message.Updates;
            uint count = updates == null ? 0u : (uint)updates.Count;
            writer.Write(count);
            if (updates != null)
            {
                for (int i = 0; i < updates.Count; i++)
                {
                    WriteStateUpdate(writer, updates[i], codec);
                }
            }
            return ms.ToArray();
        }

        public static StateBatchMessage DecodeStateBatch(
            byte[] data, ITransformCodec codec)
        {
            using var ms = new MemoryStream(data);
            using var reader = new BinaryReader(ms);
            ReadAndVerifyHeader(reader, ReplMessageIds.StateBatch);
            var msg = new StateBatchMessage
            {
                RoomSeq = reader.ReadUInt32(),
                ServerTimeUs = reader.ReadUInt64(),
            };
            uint count = reader.ReadUInt32();
            var updates = new List<StateUpdate>((int)count);
            for (uint i = 0; i < count; i++)
            {
                updates.Add(ReadStateUpdate(reader, codec));
            }
            msg.Updates = updates;
            return msg;
        }

        // --- Peek helper ---

        // Returns the message type byte without consuming the buffer. Useful
        // for dispatch when the caller does not yet know which Decode* to
        // call. Returns 0 if the buffer is empty.
        public static byte PeekMessageType(byte[] data)
        {
            if (data == null || data.Length == 0)
            {
                return 0;
            }
            return data[0];
        }
    }
}
