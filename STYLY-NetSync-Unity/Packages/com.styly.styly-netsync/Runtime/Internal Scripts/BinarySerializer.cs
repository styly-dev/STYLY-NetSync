using System;
using System.IO;
using System.Collections.Generic;
using UnityEngine;

namespace Styly.NetSync
{
    internal static class BinarySerializer
    {
        public const byte PROTOCOL_VERSION = 2;

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
        public const byte MSG_HEARTBEAT = 13;  // Heartbeat message
        public const byte MSG_RPC_DELIVERY = 14;  // Reliable RPC delivery
        public const byte MSG_RPC_ACK = 15;  // Reliable RPC ACK

        // Transform data type identifiers (deprecated - kept for reference)
        // All transforms now use 6 floats for consistency
        #region === Serialization ===

        // Helper to write TransformData as 7 floats (pos + quaternion)
        private static void WriteTransformData(BinaryWriter writer, TransformData data)
        {
            if (data == null)
            {
                for (int i = 0; i < 7; i++) writer.Write(0f);
                return;
            }
            writer.Write(data.position.x);
            writer.Write(data.position.y);
            writer.Write(data.position.z);
            writer.Write(data.rotation.x);
            writer.Write(data.rotation.y);
            writer.Write(data.rotation.z);
            writer.Write(data.rotation.w);
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
            var deviceIdBytes = System.Text.Encoding.UTF8.GetBytes(data.deviceId ?? "");
            writer.Write((byte)deviceIdBytes.Length);
            writer.Write(deviceIdBytes);

            // Pose sequence and flags
            writer.Write(data.poseSeq);
            writer.Write((byte)data.flags);

            // Physical transform (pos + quaternion)
            WriteTransformData(writer, data.physical);

            // Head transform
            WriteTransformData(writer, data.head);

            // Right hand transform
            WriteTransformData(writer, data.rightHand);

            // Left hand transform
            WriteTransformData(writer, data.leftHand);

            // Virtual transforms count
            var virtualCount = data.virtuals != null ? data.virtuals.Count : 0;
            if (virtualCount > MAX_VIRTUAL_TRANSFORMS)
            {
                virtualCount = MAX_VIRTUAL_TRANSFORMS;
            }
            writer.Write((byte)virtualCount);

            // Virtual transforms (always full 6DOF)
            if (data.virtuals != null && virtualCount > 0)
            {
                for (int i = 0; i < virtualCount; i++)
                {
                    WriteTransformData(writer, data.virtuals[i]);
                }
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
            writer.Write((byte)deviceIdBytes.Length);
            writer.Write(deviceIdBytes);

            // Pose sequence and flags (stealth, invalid poses)
            writer.Write((ushort)0);
            writer.Write((byte)PoseFlags.IsStealth);

            // Physical, Head, Right, Left — write 4 * 7 zeroed floats
            for (int i = 0; i < 28; i++) { writer.Write(0f); }

            // No virtual transforms for stealth handshake
            writer.Write((byte)0);
        }

        #region === Deserialization ===

        // Maximum allowed virtual transforms to prevent memory issues
        private const int MAX_VIRTUAL_TRANSFORMS = 50;

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
                if (messageType < MSG_CLIENT_TRANSFORM || messageType > MSG_RPC_ACK)
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
                    case MSG_RPC_DELIVERY:
                        return (messageType, DeserializeRpcDelivery(reader));
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


        // Helper to read TransformData as 7 floats (pos + quaternion)
        private static TransformData ReadTransformData(BinaryReader reader)
        {
            var data = new TransformData();
            data.position.x = reader.ReadSingle();
            data.position.y = reader.ReadSingle();
            data.position.z = reader.ReadSingle();
            data.rotation.x = reader.ReadSingle();
            data.rotation.y = reader.ReadSingle();
            data.rotation.z = reader.ReadSingle();
            data.rotation.w = reader.ReadSingle();
            return data;
        }

        private static RoomTransformData DeserializeRoomTransform(BinaryReader reader)
        {
            var data = new RoomTransformData();

            // Protocol version
            _ = reader.ReadByte();

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

                // Note: Device ID is NOT sent in MSG_ROOM_POSE_V2
                // Device ID will be resolved from client number using mapping table

                // Physical transform (pos + quaternion)
                client.physical = ReadTransformData(reader);

                // Head transform
                client.head = ReadTransformData(reader);

                // Right hand transform
                client.rightHand = ReadTransformData(reader);

                // Left hand transform
                client.leftHand = ReadTransformData(reader);

                // Virtual transforms
                var virtualCount = reader.ReadByte();

                // Validate virtual count to prevent memory issues
                if (virtualCount > MAX_VIRTUAL_TRANSFORMS)
                {
                    virtualCount = MAX_VIRTUAL_TRANSFORMS;
                }

                if (virtualCount > 0)
                {
                    client.virtuals = new List<TransformData>(virtualCount);
                    for (int j = 0; j < virtualCount; j++)
                    {
                        client.virtuals.Add(ReadTransformData(reader));
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

        public static byte[] SerializeHeartbeat(string deviceId, int clientNo, double timestamp)
        {
            using var ms = new MemoryStream();
            using var writer = new BinaryWriter(ms);
            writer.Write(MSG_HEARTBEAT);
            var deviceIdBytes = System.Text.Encoding.UTF8.GetBytes(deviceId ?? string.Empty);
            writer.Write((byte)deviceIdBytes.Length);
            writer.Write(deviceIdBytes);
            writer.Write((ushort)clientNo);
            writer.Write(timestamp);
            return ms.ToArray();
        }

        public static byte[] SerializeRpcAck(RpcAckMessage msg)
        {
            using var ms = new MemoryStream();
            using var writer = new BinaryWriter(ms);
            writer.Write(MSG_RPC_ACK);
            var rpcIdBytes = System.Text.Encoding.UTF8.GetBytes(msg.rpcId ?? string.Empty);
            writer.Write((ushort)rpcIdBytes.Length);
            writer.Write(rpcIdBytes);
            writer.Write((ushort)msg.receiverClientNo);
            writer.Write(msg.timestamp);
            return ms.ToArray();
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

        private static RpcDeliveryMessage DeserializeRpcDelivery(BinaryReader reader)
        {
            var rpcIdLen = reader.ReadUInt16();
            var rpcId = System.Text.Encoding.UTF8.GetString(reader.ReadBytes(rpcIdLen));
            var senderClientNo = reader.ReadUInt16();
            var nameLen = reader.ReadByte();
            var name = System.Text.Encoding.UTF8.GetString(reader.ReadBytes(nameLen));
            var argsLen = reader.ReadUInt16();
            var argsJson = System.Text.Encoding.UTF8.GetString(reader.ReadBytes(argsLen));
            return new RpcDeliveryMessage
            {
                rpcId = rpcId,
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
