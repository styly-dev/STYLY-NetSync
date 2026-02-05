using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace Styly.NetSync
{
    internal static class BinarySerializer
    {
        public const byte PROTOCOL_VERSION = 3;

        // Message type identifiers
        public const byte MSG_CLIENT_TRANSFORM = 1;
        public const byte MSG_ROOM_TRANSFORM = 2;  // Room transform with short IDs only
        public const byte MSG_RPC = 3;             // Remote procedure call
        public const byte MSG_RPC_SERVER = 4;   // Reserved for future use
        public const byte MSG_RPC_CLIENT = 5;   // Reserved for future use
        public const byte MSG_DEVICE_ID_MAPPING = 6;  // Device ID mapping notification
        public const byte MSG_GLOBAL_VAR_SET = 7;  // Set global variable
        public const byte MSG_GLOBAL_VAR_SYNC = 8;  // Sync global variables
        public const byte MSG_CLIENT_VAR_SET = 9;  // Set client variable
        public const byte MSG_CLIENT_VAR_SYNC = 10;  // Sync client variables
        public const byte MSG_CLIENT_POSE_V2 = 11;  // Client pose (quaternion + timestamps)
        public const byte MSG_ROOM_POSE_V2 = 12;  // Room pose snapshot (quaternion + timestamps)

        // Transform data type identifiers (deprecated - kept for reference)
        // Protocol v3 pose encoding constants
        private const float ABS_POS_SCALE = 0.01f;
        private const float REL_POS_SCALE = 0.005f;
        private const float PHYSICAL_YAW_SCALE = 0.1f;

        private const byte ENCODING_PHYSICAL_YAW_ONLY = 1 << 0;
        private const byte ENCODING_RIGHT_REL_HEAD = 1 << 1;
        private const byte ENCODING_LEFT_REL_HEAD = 1 << 2;
        private const byte ENCODING_VIRTUAL_REL_HEAD = 1 << 3;
        private const byte ENCODING_FLAGS_DEFAULT = ENCODING_PHYSICAL_YAW_ONLY | ENCODING_RIGHT_REL_HEAD | ENCODING_LEFT_REL_HEAD | ENCODING_VIRTUAL_REL_HEAD;

        #region === Serialization ===

        // Maximum allowed virtual transforms to prevent memory issues
        private const int MAX_VIRTUAL_TRANSFORMS = 50;

        private static TransformData EnsureTransform(TransformData data)
        {
            return data ?? new TransformData();
        }

        private static PoseFlags SanitizePoseFlags(PoseFlags flags)
        {
            if ((flags & PoseFlags.HeadValid) == 0)
            {
                flags &= ~(PoseFlags.RightValid | PoseFlags.LeftValid | PoseFlags.VirtualsValid);
            }
            return flags;
        }

        private static short QuantizeSigned(float value, float scale)
        {
            if (scale <= 0f)
            {
                return 0;
            }

            var scaled = value / scale;
            var rounded = Mathf.RoundToInt(scaled);
            if (rounded > short.MaxValue)
            {
                return short.MaxValue;
            }
            if (rounded < short.MinValue)
            {
                return short.MinValue;
            }
            return (short)rounded;
        }

        private static Vector3 QuantizedToVector3(short x, short y, short z, float scale)
        {
            return new Vector3(x * scale, y * scale, z * scale);
        }

        private static float NormalizeYawDegrees(float yaw)
        {
            return Mathf.DeltaAngle(0f, yaw);
        }

        private static float QuaternionToYawDegrees(Quaternion rotation)
        {
            var q = NormalizeQuaternionSafe(rotation);
            float sinyCosp = 2f * (q.w * q.y + q.z * q.x);
            float cosyCosp = 1f - 2f * (q.y * q.y + q.z * q.z);
            return NormalizeYawDegrees(Mathf.Atan2(sinyCosp, cosyCosp) * Mathf.Rad2Deg);
        }

        private static Quaternion NormalizeQuaternionSafe(Quaternion rotation)
        {
            if (rotation.x == 0f && rotation.y == 0f && rotation.z == 0f && rotation.w == 0f)
            {
                return Quaternion.identity;
            }
            return Quaternion.Normalize(rotation);
        }

        private static uint CompressQuaternionSmallestThree(Quaternion rotation)
        {
            var q = NormalizeQuaternionSafe(rotation);

            float ax = Mathf.Abs(q.x);
            float ay = Mathf.Abs(q.y);
            float az = Mathf.Abs(q.z);
            float aw = Mathf.Abs(q.w);

            int largestIndex = 0;
            float largest = ax;
            if (ay > largest)
            {
                largestIndex = 1;
                largest = ay;
            }
            if (az > largest)
            {
                largestIndex = 2;
                largest = az;
            }
            if (aw > largest)
            {
                largestIndex = 3;
            }

            var values = new float[] { q.x, q.y, q.z, q.w };
            if (values[largestIndex] < 0f)
            {
                values[0] = -values[0];
                values[1] = -values[1];
                values[2] = -values[2];
                values[3] = -values[3];
            }

            const float min = -0.70710677f;
            const float max = 0.70710677f;
            const int max10 = 1023;

            uint packed = (uint)largestIndex << 30;
            int writeIndex = 0;
            for (int i = 0; i < 4; i++)
            {
                if (i == largestIndex)
                {
                    continue;
                }

                float clamped = Mathf.Clamp(values[i], min, max);
                float normalized = (clamped - min) / (max - min);
                uint scaled = (uint)Mathf.RoundToInt(normalized * max10);
                if (scaled > max10)
                {
                    scaled = max10;
                }

                int shift = 20 - (writeIndex * 10);
                packed |= scaled << shift;
                writeIndex++;
            }

            return packed;
        }

        private static Quaternion DecompressQuaternionSmallestThree(uint packed)
        {
            int largestIndex = (int)((packed >> 30) & 0x3);
            uint a = (packed >> 20) & 0x3FF;
            uint b = (packed >> 10) & 0x3FF;
            uint c = packed & 0x3FF;

            const float min = -0.70710677f;
            const float max = 0.70710677f;
            const float inv = 1f / 1023f;

            float Decode(uint v)
            {
                return min + ((max - min) * (v * inv));
            }

            float[] values = new float[4];
            int readIndex = 0;
            for (int i = 0; i < 4; i++)
            {
                if (i == largestIndex)
                {
                    continue;
                }

                uint quantized = readIndex == 0 ? a : readIndex == 1 ? b : c;
                values[i] = Decode(quantized);
                readIndex++;
            }

            float sumSquares = 0f;
            for (int i = 0; i < 4; i++)
            {
                if (i == largestIndex)
                {
                    continue;
                }
                sumSquares += values[i] * values[i];
            }

            values[largestIndex] = Mathf.Sqrt(Mathf.Max(0f, 1f - sumSquares));

            var result = new Quaternion(values[0], values[1], values[2], values[3]);
            return NormalizeQuaternionSafe(result);
        }

        private static void HashShort(ref ulong hash, short value)
        {
            unchecked
            {
                hash ^= (byte)(value & 0xFF);
                hash *= 1099511628211UL;
                hash ^= (byte)((value >> 8) & 0xFF);
                hash *= 1099511628211UL;
            }
        }

        private static void HashUInt(ref ulong hash, uint value)
        {
            unchecked
            {
                hash ^= (byte)(value & 0xFF);
                hash *= 1099511628211UL;
                hash ^= (byte)((value >> 8) & 0xFF);
                hash *= 1099511628211UL;
                hash ^= (byte)((value >> 16) & 0xFF);
                hash *= 1099511628211UL;
                hash ^= (byte)((value >> 24) & 0xFF);
                hash *= 1099511628211UL;
            }
        }

        /// <summary>
        /// Computes a stable hash for the quantized pose payload content.
        /// Pose sequence and device ID are intentionally excluded.
        /// </summary>
        internal static ulong ComputePoseSignature(ClientTransformData data)
        {
            var flags = SanitizePoseFlags(data != null ? data.flags : PoseFlags.None);
            bool physicalValid = (flags & PoseFlags.PhysicalValid) != 0;
            bool headValid = (flags & PoseFlags.HeadValid) != 0;
            bool rightValid = headValid && ((flags & PoseFlags.RightValid) != 0);
            bool leftValid = headValid && ((flags & PoseFlags.LeftValid) != 0);
            bool virtualValid = headValid && ((flags & PoseFlags.VirtualsValid) != 0);

            var physical = EnsureTransform(data != null ? data.physical : null);
            var head = EnsureTransform(data != null ? data.head : null);
            var right = EnsureTransform(data != null ? data.rightHand : null);
            var left = EnsureTransform(data != null ? data.leftHand : null);
            var headRot = NormalizeQuaternionSafe(head.rotation);

            ulong hash = 1469598103934665603UL; // FNV-1a 64 offset basis
            unchecked
            {
                hash ^= (byte)flags;
                hash *= 1099511628211UL;
                hash ^= ENCODING_FLAGS_DEFAULT;
                hash *= 1099511628211UL;
            }

            if (physicalValid)
            {
                HashShort(ref hash, QuantizeSigned(physical.position.x, ABS_POS_SCALE));
                HashShort(ref hash, QuantizeSigned(physical.position.y, ABS_POS_SCALE));
                HashShort(ref hash, QuantizeSigned(physical.position.z, ABS_POS_SCALE));
                HashShort(ref hash, QuantizeSigned(QuaternionToYawDegrees(physical.rotation), PHYSICAL_YAW_SCALE));
            }

            if (headValid)
            {
                HashShort(ref hash, QuantizeSigned(head.position.x, ABS_POS_SCALE));
                HashShort(ref hash, QuantizeSigned(head.position.y, ABS_POS_SCALE));
                HashShort(ref hash, QuantizeSigned(head.position.z, ABS_POS_SCALE));
                HashUInt(ref hash, CompressQuaternionSmallestThree(headRot));
            }

            if (rightValid)
            {
                var relPos = right.position - head.position;
                var relRot = Quaternion.Inverse(headRot) * NormalizeQuaternionSafe(right.rotation);
                HashShort(ref hash, QuantizeSigned(relPos.x, REL_POS_SCALE));
                HashShort(ref hash, QuantizeSigned(relPos.y, REL_POS_SCALE));
                HashShort(ref hash, QuantizeSigned(relPos.z, REL_POS_SCALE));
                HashUInt(ref hash, CompressQuaternionSmallestThree(relRot));
            }

            if (leftValid)
            {
                var relPos = left.position - head.position;
                var relRot = Quaternion.Inverse(headRot) * NormalizeQuaternionSafe(left.rotation);
                HashShort(ref hash, QuantizeSigned(relPos.x, REL_POS_SCALE));
                HashShort(ref hash, QuantizeSigned(relPos.y, REL_POS_SCALE));
                HashShort(ref hash, QuantizeSigned(relPos.z, REL_POS_SCALE));
                HashUInt(ref hash, CompressQuaternionSmallestThree(relRot));
            }

            int virtualCount = 0;
            if (virtualValid && data != null && data.virtuals != null)
            {
                virtualCount = Mathf.Min(data.virtuals.Count, MAX_VIRTUAL_TRANSFORMS);
            }
            unchecked
            {
                hash ^= (byte)virtualCount;
                hash *= 1099511628211UL;
            }

            for (int i = 0; i < virtualCount; i++)
            {
                var vt = EnsureTransform(data.virtuals[i]);
                var relPos = vt.position - head.position;
                var relRot = Quaternion.Inverse(headRot) * NormalizeQuaternionSafe(vt.rotation);
                HashShort(ref hash, QuantizeSigned(relPos.x, REL_POS_SCALE));
                HashShort(ref hash, QuantizeSigned(relPos.y, REL_POS_SCALE));
                HashShort(ref hash, QuantizeSigned(relPos.z, REL_POS_SCALE));
                HashUInt(ref hash, CompressQuaternionSmallestThree(relRot));
            }

            return hash;
        }

        /// <summary>
        /// Serialize client transform into a new byte array (legacy API).
        /// Prefer SerializeClientTransformInto to reuse an existing stream/writer.
        /// </summary>
        public static byte[] SerializeClientTransform(ClientTransformData data)
        {
            using (var ms = new MemoryStream())
            using (var writer = new BinaryWriter(ms))
            {
                SerializeClientTransformInto(writer, data);
                return ms.ToArray();
            }
        }

        /// <summary>
        /// Serialize client transform into an existing BinaryWriter.
        /// The writer's underlying stream position advances to the end of the payload.
        /// </summary>
        public static void SerializeClientTransformInto(BinaryWriter writer, ClientTransformData data)
        {
            // Message type
            writer.Write(MSG_CLIENT_POSE_V2);

            // Protocol version
            writer.Write(PROTOCOL_VERSION);

            // Device ID (as UTF8 bytes with length prefix)
            var deviceIdBytes = System.Text.Encoding.UTF8.GetBytes(data != null ? data.deviceId ?? string.Empty : string.Empty);
            var deviceIdLength = Mathf.Min(deviceIdBytes.Length, byte.MaxValue);
            writer.Write((byte)deviceIdLength);
            writer.Write(deviceIdBytes, 0, deviceIdLength);

            // Pose sequence and flags
            var flags = SanitizePoseFlags(data != null ? data.flags : PoseFlags.None);
            writer.Write(data != null ? data.poseSeq : (ushort)0);
            writer.Write((byte)flags);
            writer.Write(ENCODING_FLAGS_DEFAULT);

            var physicalValid = (flags & PoseFlags.PhysicalValid) != 0;
            var headValid = (flags & PoseFlags.HeadValid) != 0;
            var rightValid = headValid && ((flags & PoseFlags.RightValid) != 0);
            var leftValid = headValid && ((flags & PoseFlags.LeftValid) != 0);
            var virtualValid = headValid && ((flags & PoseFlags.VirtualsValid) != 0);

            var physical = EnsureTransform(data != null ? data.physical : null);
            var head = EnsureTransform(data != null ? data.head : null);
            var right = EnsureTransform(data != null ? data.rightHand : null);
            var left = EnsureTransform(data != null ? data.leftHand : null);
            var headRot = NormalizeQuaternionSafe(head.rotation);

            if (physicalValid)
            {
                writer.Write(QuantizeSigned(physical.position.x, ABS_POS_SCALE));
                writer.Write(QuantizeSigned(physical.position.y, ABS_POS_SCALE));
                writer.Write(QuantizeSigned(physical.position.z, ABS_POS_SCALE));

                var yawDegrees = QuaternionToYawDegrees(physical.rotation);
                writer.Write(QuantizeSigned(yawDegrees, PHYSICAL_YAW_SCALE));
            }

            if (headValid)
            {
                writer.Write(QuantizeSigned(head.position.x, ABS_POS_SCALE));
                writer.Write(QuantizeSigned(head.position.y, ABS_POS_SCALE));
                writer.Write(QuantizeSigned(head.position.z, ABS_POS_SCALE));
                writer.Write(CompressQuaternionSmallestThree(headRot));
            }

            if (rightValid)
            {
                var relPos = right.position - head.position;
                var rightRot = NormalizeQuaternionSafe(right.rotation);
                var relRot = Quaternion.Inverse(headRot) * rightRot;

                writer.Write(QuantizeSigned(relPos.x, REL_POS_SCALE));
                writer.Write(QuantizeSigned(relPos.y, REL_POS_SCALE));
                writer.Write(QuantizeSigned(relPos.z, REL_POS_SCALE));
                writer.Write(CompressQuaternionSmallestThree(relRot));
            }

            if (leftValid)
            {
                var relPos = left.position - head.position;
                var leftRot = NormalizeQuaternionSafe(left.rotation);
                var relRot = Quaternion.Inverse(headRot) * leftRot;

                writer.Write(QuantizeSigned(relPos.x, REL_POS_SCALE));
                writer.Write(QuantizeSigned(relPos.y, REL_POS_SCALE));
                writer.Write(QuantizeSigned(relPos.z, REL_POS_SCALE));
                writer.Write(CompressQuaternionSmallestThree(relRot));
            }

            var virtualCount = 0;
            if (virtualValid && data != null && data.virtuals != null)
            {
                virtualCount = Mathf.Min(data.virtuals.Count, MAX_VIRTUAL_TRANSFORMS);
            }
            writer.Write((byte)virtualCount);

            for (int i = 0; i < virtualCount; i++)
            {
                var vt = EnsureTransform(data.virtuals[i]);
                var relPos = vt.position - head.position;
                var relRot = Quaternion.Inverse(headRot) * NormalizeQuaternionSafe(vt.rotation);

                writer.Write(QuantizeSigned(relPos.x, REL_POS_SCALE));
                writer.Write(QuantizeSigned(relPos.y, REL_POS_SCALE));
                writer.Write(QuantizeSigned(relPos.z, REL_POS_SCALE));
                writer.Write(CompressQuaternionSmallestThree(relRot));
            }
        }

        /// <summary>
        /// Serialize a stealth handshake into a new byte array (legacy API).
        /// Prefer SerializeStealthHandshakeInto to reuse an existing stream/writer.
        /// </summary>
        public static byte[] SerializeStealthHandshake(string deviceId)
        {
            using (var ms = new MemoryStream())
            using (var writer = new BinaryWriter(ms))
            {
                SerializeStealthHandshakeInto(writer, deviceId);
                return ms.ToArray();
            }
        }

        /// <summary>
        /// Serialize a stealth handshake into an existing BinaryWriter.
        /// The writer's underlying stream position advances to the end of the payload.
        /// </summary>
        public static void SerializeStealthHandshakeInto(BinaryWriter writer, string deviceId)
        {
            // Message type
            writer.Write(MSG_CLIENT_POSE_V2);

            // Protocol version
            writer.Write(PROTOCOL_VERSION);

            // Device ID (as UTF8 bytes with length prefix)
            var deviceIdBytes = System.Text.Encoding.UTF8.GetBytes(deviceId ?? "");
            var deviceIdLength = Mathf.Min(deviceIdBytes.Length, byte.MaxValue);
            writer.Write((byte)deviceIdLength);
            writer.Write(deviceIdBytes, 0, deviceIdLength);

            // Pose sequence and flags (stealth, invalid poses)
            writer.Write((ushort)0);
            writer.Write((byte)PoseFlags.IsStealth);
            writer.Write(ENCODING_FLAGS_DEFAULT);

            // No virtual transforms for stealth handshake
            writer.Write((byte)0);
        }

        #region === Deserialization ===

        public static (byte messageType, object data) Deserialize(byte[] bytes)
        {
            if (bytes == null || bytes.Length == 0)
            {
                throw new ArgumentException("Cannot deserialize null or empty byte array");
            }

            using (var ms = new MemoryStream(bytes))
            using (var reader = new BinaryReader(ms))
            {
                var messageType = reader.ReadByte();

                // Validate message type is within valid range
                if (messageType < MSG_CLIENT_TRANSFORM || messageType > MSG_ROOM_POSE_V2)
                {
                    // Don't throw exception, just return invalid type with null data
                    // This allows the caller to handle it gracefully
                    return (messageType, null);
                }

                switch (messageType)
                {
                    // case MSG_CLIENT_TRANSFORM:
                    //     return (messageType, DeserializeClientTransform(reader));
                    case MSG_ROOM_POSE_V2:
                        return (messageType, DeserializeRoomTransform(reader));
                    case MSG_RPC:
                        // RPC message
                        return (messageType, DeserializeRPCMessage(reader));
                    // MSG_RPC_SERVER and MSG_RPC_CLIENT are reserved for future use
                    case MSG_DEVICE_ID_MAPPING:
                        // Device ID mapping notification
                        return (messageType, DeserializeDeviceIdMapping(reader));
                    case MSG_GLOBAL_VAR_SYNC:
                        // Global variables sync from server
                        return (messageType, DeserializeGlobalVarSync(reader));
                    case MSG_CLIENT_VAR_SYNC:
                        // Client variables sync from server
                        return (messageType, DeserializeClientVarSync(reader));
                    default:
                        // This should not happen due to validation above, but keep as safety
                        return (messageType, null);
                }
            }
        }


        private static RoomTransformData DeserializeRoomTransform(BinaryReader reader)
        {
            var data = new RoomTransformData();

            // Protocol version
            var protocolVersion = reader.ReadByte();
            if (protocolVersion != PROTOCOL_VERSION)
            {
                throw new InvalidDataException($"Unsupported room pose protocol version: {protocolVersion}");
            }

            // Room ID
            var roomIdLength = reader.ReadByte();
            data.roomId = System.Text.Encoding.UTF8.GetString(reader.ReadBytes(roomIdLength));

            // Broadcast time (server monotonic seconds)
            data.broadcastTime = reader.ReadDouble();

            // Number of clients
            var clientCount = reader.ReadUInt16();
            data.clients = new List<ClientTransformData>(clientCount);

            // Each client with short ID
            for (int i = 0; i < clientCount; i++)
            {
                var client = new ClientTransformData();

                // Client number (2 bytes)
                client.clientNo = reader.ReadUInt16();

                // Pose time (server monotonic seconds)
                client.poseTime = reader.ReadDouble();

                // Pose sequence and flags
                client.poseSeq = reader.ReadUInt16();
                client.flags = (PoseFlags)reader.ReadByte();
                var encodingFlags = reader.ReadByte();
                _ = encodingFlags;

                // Note: Device ID is NOT sent in MSG_ROOM_POSE_V2
                // Device ID will be resolved from client number using mapping table

                client.physical = new TransformData();
                client.head = new TransformData();
                client.rightHand = new TransformData();
                client.leftHand = new TransformData();

                bool physicalValid = (client.flags & PoseFlags.PhysicalValid) != 0;
                bool headValid = (client.flags & PoseFlags.HeadValid) != 0;
                bool rightValid = headValid && ((client.flags & PoseFlags.RightValid) != 0);
                bool leftValid = headValid && ((client.flags & PoseFlags.LeftValid) != 0);
                bool virtualValid = headValid && ((client.flags & PoseFlags.VirtualsValid) != 0);

                if (physicalValid)
                {
                    short px = reader.ReadInt16();
                    short py = reader.ReadInt16();
                    short pz = reader.ReadInt16();
                    short yawQ = reader.ReadInt16();
                    client.physical.position = QuantizedToVector3(px, py, pz, ABS_POS_SCALE);
                    client.physical.rotation = Quaternion.Euler(0f, yawQ * PHYSICAL_YAW_SCALE, 0f);
                }
                else
                {
                    client.physical.position = Vector3.zero;
                    client.physical.rotation = Quaternion.identity;
                }

                var headPos = Vector3.zero;
                var headRot = Quaternion.identity;
                if (headValid)
                {
                    short hx = reader.ReadInt16();
                    short hy = reader.ReadInt16();
                    short hz = reader.ReadInt16();
                    uint packedHeadRot = reader.ReadUInt32();
                    headPos = QuantizedToVector3(hx, hy, hz, ABS_POS_SCALE);
                    headRot = DecompressQuaternionSmallestThree(packedHeadRot);
                    client.head.position = headPos;
                    client.head.rotation = headRot;
                }
                else
                {
                    client.head.position = Vector3.zero;
                    client.head.rotation = Quaternion.identity;
                }

                if (rightValid)
                {
                    short rx = reader.ReadInt16();
                    short ry = reader.ReadInt16();
                    short rz = reader.ReadInt16();
                    uint packedRightRelRot = reader.ReadUInt32();
                    var relPos = QuantizedToVector3(rx, ry, rz, REL_POS_SCALE);
                    var relRot = DecompressQuaternionSmallestThree(packedRightRelRot);
                    client.rightHand.position = headPos + relPos;
                    client.rightHand.rotation = NormalizeQuaternionSafe(headRot * relRot);
                }
                else
                {
                    client.rightHand.position = Vector3.zero;
                    client.rightHand.rotation = Quaternion.identity;
                }

                if (leftValid)
                {
                    short lx = reader.ReadInt16();
                    short ly = reader.ReadInt16();
                    short lz = reader.ReadInt16();
                    uint packedLeftRelRot = reader.ReadUInt32();
                    var relPos = QuantizedToVector3(lx, ly, lz, REL_POS_SCALE);
                    var relRot = DecompressQuaternionSmallestThree(packedLeftRelRot);
                    client.leftHand.position = headPos + relPos;
                    client.leftHand.rotation = NormalizeQuaternionSafe(headRot * relRot);
                }
                else
                {
                    client.leftHand.position = Vector3.zero;
                    client.leftHand.rotation = Quaternion.identity;
                }

                // Virtual transforms
                var virtualCount = reader.ReadByte();

                // Validate virtual count to prevent memory issues
                if (virtualCount > MAX_VIRTUAL_TRANSFORMS)
                {
                    virtualCount = MAX_VIRTUAL_TRANSFORMS;
                }

                if (virtualCount > 0 && virtualValid)
                {
                    client.virtuals = new List<TransformData>(virtualCount);
                    for (int j = 0; j < virtualCount; j++)
                    {
                        short vx = reader.ReadInt16();
                        short vy = reader.ReadInt16();
                        short vz = reader.ReadInt16();
                        uint packedRelRot = reader.ReadUInt32();
                        var relPos = QuantizedToVector3(vx, vy, vz, REL_POS_SCALE);
                        var relRot = DecompressQuaternionSmallestThree(packedRelRot);
                        client.virtuals.Add(new TransformData
                        {
                            position = headPos + relPos,
                            rotation = NormalizeQuaternionSafe(headRot * relRot)
                        });
                    }
                }
                else if (virtualCount > 0)
                {
                    // If flag is unset but payload still has entries, consume bytes to keep stream aligned.
                    for (int j = 0; j < virtualCount; j++)
                    {
                        reader.ReadInt16();
                        reader.ReadInt16();
                        reader.ReadInt16();
                        reader.ReadUInt32();
                    }
                }

                data.clients.Add(client);
            }
            return data;
        }


        #endregion

        /// <summary>
        /// Serialize an RPC message into a new array (legacy API). Prefer the Into version.
        /// </summary>
        public static byte[] SerializeRPCMessage(RPCMessage msg)
        {
            using var ms = new MemoryStream();
            using var writer = new BinaryWriter(ms);
            SerializeRPCMessageInto(writer, msg);
            return ms.ToArray();
        }

        /// <summary>
        /// Serialize an RPC message into an existing BinaryWriter.
        /// </summary>
        public static void SerializeRPCMessageInto(BinaryWriter writer, RPCMessage msg)
        {
            // Message type
            writer.Write(MSG_RPC);
            // Sender client number (2 bytes)
            writer.Write((ushort)msg.senderClientNo);
            // Function name (length-prefixed byte)
            var nameBytes = System.Text.Encoding.UTF8.GetBytes(msg.functionName);
            if (nameBytes.Length > 255)
            {
                throw new ArgumentException("Function name is too long. Maximum length is 255 bytes.");
            }
            writer.Write((byte)nameBytes.Length);
            writer.Write(nameBytes);
            // Arguments JSON
            var argsBytes = System.Text.Encoding.UTF8.GetBytes(msg.argumentsJson ?? string.Empty);
            writer.Write((ushort)argsBytes.Length);
            writer.Write(argsBytes);
        }


        /// <summary>
        /// Serialize global variable set message
        /// </summary>
        public static byte[] SerializeGlobalVarSet(Dictionary<string, object> data)
        {
            using var ms = new MemoryStream();
            using var writer = new BinaryWriter(ms);
            SerializeGlobalVarSetInto(writer, data);
            return ms.ToArray();
        }

        /// <summary>
        /// Serialize global variable set into an existing BinaryWriter.
        /// </summary>
        public static void SerializeGlobalVarSetInto(BinaryWriter writer, Dictionary<string, object> data)
        {
            // Message type
            writer.Write(MSG_GLOBAL_VAR_SET);

            // Sender client number (2 bytes)
            var senderClientNo = data.TryGetValue("senderClientNo", out var senderObj) ? Convert.ToUInt16(senderObj) : (ushort)0;
            writer.Write(senderClientNo);

            // Variable name (max 64 bytes)
            var varName = data.TryGetValue("variableName", out var nameObj) ? (nameObj != null ? nameObj.ToString() : "") : "";
            if (varName.Length > 64) varName = varName.Substring(0, 64);
            var nameBytes = System.Text.Encoding.UTF8.GetBytes(varName);
            writer.Write((byte)nameBytes.Length);
            writer.Write(nameBytes);

            // Variable value (max 1024 bytes)
            var varValue = data.TryGetValue("variableValue", out var valueObj) ? (valueObj != null ? valueObj.ToString() : "") : "";
            if (varValue.Length > 1024) varValue = varValue.Substring(0, 1024);
            var valueBytes = System.Text.Encoding.UTF8.GetBytes(varValue);
            writer.Write((ushort)valueBytes.Length);
            writer.Write(valueBytes);

            // Timestamp (8 bytes double)
            var timestamp = data.TryGetValue("timestamp", out var timestampObj) ? Convert.ToDouble(timestampObj) : 0.0;
            writer.Write(timestamp);
        }

        /// <summary>
        /// Serialize client variable set message
        /// </summary>
        public static byte[] SerializeClientVarSet(Dictionary<string, object> data)
        {
            using var ms = new MemoryStream();
            using var writer = new BinaryWriter(ms);
            SerializeClientVarSetInto(writer, data);
            return ms.ToArray();
        }

        /// <summary>
        /// Serialize client variable set into an existing BinaryWriter.
        /// </summary>
        public static void SerializeClientVarSetInto(BinaryWriter writer, Dictionary<string, object> data)
        {
            // Message type
            writer.Write(MSG_CLIENT_VAR_SET);

            // Sender client number (2 bytes)
            var senderClientNo = data.TryGetValue("senderClientNo", out var senderObj) ? Convert.ToUInt16(senderObj) : (ushort)0;
            writer.Write(senderClientNo);

            // Target client number (2 bytes)
            var targetClientNo = data.TryGetValue("targetClientNo", out var targetObj) ? Convert.ToUInt16(targetObj) : (ushort)0;
            writer.Write(targetClientNo);

            // Variable name (max 64 bytes)
            var varName = data.TryGetValue("variableName", out var nameObj) ? (nameObj != null ? nameObj.ToString() : "") : "";
            if (varName.Length > 64) varName = varName.Substring(0, 64);
            var nameBytes = System.Text.Encoding.UTF8.GetBytes(varName);
            writer.Write((byte)nameBytes.Length);
            writer.Write(nameBytes);

            // Variable value (max 1024 bytes)
            var varValue = data.TryGetValue("variableValue", out var valueObj) ? (valueObj != null ? valueObj.ToString() : "") : "";
            if (varValue.Length > 1024) varValue = varValue.Substring(0, 1024);
            var valueBytes = System.Text.Encoding.UTF8.GetBytes(varValue);
            writer.Write((ushort)valueBytes.Length);
            writer.Write(valueBytes);

            // Timestamp (8 bytes double)
            var timestamp = data.TryGetValue("timestamp", out var timestampObj) ? Convert.ToDouble(timestampObj) : 0.0;
            writer.Write(timestamp);
        }

        /// <summary>
        /// Deserialize an RPC message
        /// </summary>
        private static RPCMessage DeserializeRPCMessage(BinaryReader reader)
        {
            // Sender client number (2 bytes)
            var senderClientNo = reader.ReadUInt16();
            // Function name
            var nameLen = reader.ReadByte();
            var name = System.Text.Encoding.UTF8.GetString(reader.ReadBytes(nameLen));
            // Arguments JSON
            var argsLen = reader.ReadUInt16();
            var argsJson = System.Text.Encoding.UTF8.GetString(reader.ReadBytes(argsLen));
            return new RPCMessage
            {
                senderClientNo = senderClientNo,
                functionName = name,
                argumentsJson = argsJson
            };
        }


        /// <summary>
        /// Deserialize device ID mapping notification
        /// </summary>
        private static DeviceIdMappingData DeserializeDeviceIdMapping(BinaryReader reader)
        {
            var data = new DeviceIdMappingData();
            data.mappings = new List<DeviceIdMapping>();

            // Server version (3 bytes: major, minor, patch)
            data.serverVersionMajor = reader.ReadByte();
            data.serverVersionMinor = reader.ReadByte();
            data.serverVersionPatch = reader.ReadByte();

            // Number of mappings
            var count = reader.ReadUInt16();

            // Each mapping
            for (int i = 0; i < count; i++)
            {
                var mapping = new DeviceIdMapping();
                mapping.clientNo = reader.ReadUInt16();

                // Read stealth flag (1 byte)
                mapping.isStealthMode = reader.ReadByte() == 0x01;

                var deviceIdLength = reader.ReadByte();
                mapping.deviceId = System.Text.Encoding.UTF8.GetString(reader.ReadBytes(deviceIdLength));

                data.mappings.Add(mapping);
            }

            return data;
        }

        /// <summary>
        /// Deserialize global variables sync message
        /// </summary>
        private static Dictionary<string, object> DeserializeGlobalVarSync(BinaryReader reader)
        {
            var data = new Dictionary<string, object>();
            var variables = new List<object>();

            // Number of variables
            var count = reader.ReadUInt16();

            // Each variable
            for (int i = 0; i < count; i++)
            {
                var variable = new Dictionary<string, object>();

                // Variable name
                var nameLength = reader.ReadByte();
                variable["name"] = System.Text.Encoding.UTF8.GetString(reader.ReadBytes(nameLength));

                // Variable value
                var valueLength = reader.ReadUInt16();
                variable["value"] = System.Text.Encoding.UTF8.GetString(reader.ReadBytes(valueLength));

                // Timestamp
                variable["timestamp"] = reader.ReadDouble();

                // Last writer client number
                variable["lastWriterClientNo"] = reader.ReadUInt16();

                variables.Add(variable);
            }

            data["variables"] = variables.ToArray();
            return data;
        }

        /// <summary>
        /// Deserialize client variables sync message
        /// </summary>
        private static Dictionary<string, object> DeserializeClientVarSync(BinaryReader reader)
        {
            var data = new Dictionary<string, object>();
            var clientVariables = new Dictionary<string, object>();

            // Number of clients
            var clientCount = reader.ReadUInt16();

            // Each client's variables
            for (int i = 0; i < clientCount; i++)
            {
                var clientNo = reader.ReadUInt16();
                var varCount = reader.ReadUInt16();

                var variables = new List<object>();
                for (int j = 0; j < varCount; j++)
                {
                    var variable = new Dictionary<string, object>();

                    // Variable name
                    var nameLength = reader.ReadByte();
                    variable["name"] = System.Text.Encoding.UTF8.GetString(reader.ReadBytes(nameLength));

                    // Variable value
                    var valueLength = reader.ReadUInt16();
                    variable["value"] = System.Text.Encoding.UTF8.GetString(reader.ReadBytes(valueLength));

                    // Timestamp
                    variable["timestamp"] = reader.ReadDouble();

                    // Last writer client number
                    variable["lastWriterClientNo"] = reader.ReadUInt16();

                    variables.Add(variable);
                }

                clientVariables[clientNo.ToString()] = variables.ToArray();
            }

            data["clientVariables"] = clientVariables;
            return data;
        }

        #endregion
    }
}
