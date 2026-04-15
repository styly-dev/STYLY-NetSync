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
            writer.Write((byte)message.Reason);
            return ms.ToArray();
        }

        public static OwnershipEventMessage DecodeOwnershipEvent(byte[] data)
        {
            using var ms = new MemoryStream(data);
            using var reader = new BinaryReader(ms);
            ReadAndVerifyHeader(reader, ReplMessageIds.OwnershipEvent);
            return new OwnershipEventMessage
            {
                EntityId = reader.ReadUInt64(),
                NewOwnerShortId = reader.ReadUInt32(),
                NewAuthorityEpoch = reader.ReadUInt32(),
                Reason = (OwnershipReason)reader.ReadByte(),
            };
        }

        // --- RESYNC_REQUEST ---

        public static byte[] EncodeResyncRequest(in ResyncRequestMessage message)
        {
            using var ms = new MemoryStream();
            using var writer = new BinaryWriter(ms);
            WriteHeader(writer, ReplMessageIds.ResyncRequest);
            var ids = message.EntityIds;
            ushort count = ids == null ? (ushort)0 : checked((ushort)ids.Count);
            writer.Write(count);
            if (ids != null)
            {
                for (int i = 0; i < ids.Count; i++)
                {
                    writer.Write(ids[i]);
                }
            }
            return ms.ToArray();
        }

        public static ResyncRequestMessage DecodeResyncRequest(byte[] data)
        {
            using var ms = new MemoryStream(data);
            using var reader = new BinaryReader(ms);
            ReadAndVerifyHeader(reader, ReplMessageIds.ResyncRequest);
            ushort count = reader.ReadUInt16();
            var ids = new List<ulong>(count);
            for (int i = 0; i < count; i++)
            {
                ids.Add(reader.ReadUInt64());
            }
            return new ResyncRequestMessage { EntityIds = ids };
        }

        // --- RESYNC_REPLY ---

        public static byte[] EncodeResyncReply(
            in ResyncReplyMessage message, ITransformCodec codec)
        {
            using var ms = new MemoryStream();
            using var writer = new BinaryWriter(ms);
            WriteHeader(writer, ReplMessageIds.ResyncReply);
            var entities = message.Entities;
            ushort count = entities == null ? (ushort)0 : checked((ushort)entities.Count);
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
            ushort count = reader.ReadUInt16();
            var entities = new List<EntityRecord>(count);
            for (int i = 0; i < count; i++)
            {
                entities.Add(ReadEntityRecord(reader, codec));
            }
            return new ResyncReplyMessage { Entities = entities };
        }

        // --- STATE_BATCH ---

        public static byte[] EncodeStateBatch(
            in StateBatchMessage message, ITransformCodec codec)
        {
            using var ms = new MemoryStream();
            using var writer = new BinaryWriter(ms);
            WriteHeader(writer, ReplMessageIds.StateBatch);
            writer.Write(message.ServerTick);
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
            var msg = new StateBatchMessage { ServerTick = reader.ReadUInt32() };
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
