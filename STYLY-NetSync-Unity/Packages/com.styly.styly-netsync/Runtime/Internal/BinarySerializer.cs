using System;
using System.IO;
using System.Collections.Generic;
using UnityEngine;

namespace Styly.NetSync
{
    public static class BinarySerializer
    {
        // Message type identifiers
        public const byte MSG_CLIENT_TRANSFORM = 1;
        public const byte MSG_ROOM_TRANSFORM = 2;  // Room transform with short IDs only
        public const byte MSG_RPC_BROADCAST = 3;   // Broadcast function call
        public const byte MSG_RPC_SERVER = 4;   // Client-to-server RPC call
        public const byte MSG_RPC_CLIENT = 5;   // Client-to-client RPC call
        public const byte MSG_DEVICE_ID_MAPPING = 6;  // Device ID mapping notification
        public const byte MSG_GLOBAL_VAR_SET = 7;  // Set global variable
        public const byte MSG_GLOBAL_VAR_SYNC = 8;  // Sync global variables
        public const byte MSG_CLIENT_VAR_SET = 9;  // Set client variable
        public const byte MSG_CLIENT_VAR_SYNC = 10;  // Sync client variables

        // Transform data type identifiers (deprecated - kept for reference)
        // All transforms now use 6 floats for consistency
        #region === Serialization ===

        // Helper to write TransformData as 6 floats
        private static void WriteTransformData(BinaryWriter writer, TransformData data)
        {
            if (data == null)
            {
                for (int i = 0; i < 6; i++) writer.Write(0f);
                return;
            }
            writer.Write(data.posX);
            writer.Write(data.posY);
            writer.Write(data.posZ);
            writer.Write(data.rotX);
            writer.Write(data.rotY);
            writer.Write(data.rotZ);
        }

        public static byte[] SerializeClientTransform(ClientTransformData data)
        {
            using (var ms = new MemoryStream())
            using (var writer = new BinaryWriter(ms))
            {
                // Message type
                writer.Write(MSG_CLIENT_TRANSFORM);

                // Device ID (as UTF8 bytes with length prefix)
                var deviceIdBytes = System.Text.Encoding.UTF8.GetBytes(data.deviceId ?? "");
                writer.Write((byte)deviceIdBytes.Length);
                writer.Write(deviceIdBytes);

                // Note: Client number is not sent by client, only assigned by server

                // Physical transform (now full 6 floats)
                WriteTransformData(writer, data.physical);

                // Head transform
                WriteTransformData(writer, data.head);

                // Right hand transform
                WriteTransformData(writer, data.rightHand);

                // Left hand transform
                WriteTransformData(writer, data.leftHand);

                // Virtual transforms count
                var virtualCount = data.virtuals?.Count ?? 0;
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

                return ms.ToArray();
            }
        }

        public static byte[] SerializeStealthHandshake(string deviceId)
        {
            using (var ms = new MemoryStream())
            using (var writer = new BinaryWriter(ms))
            {
                // Message type
                writer.Write(MSG_CLIENT_TRANSFORM);

                // Device ID (as UTF8 bytes with length prefix)
                var deviceIdBytes = System.Text.Encoding.UTF8.GetBytes(deviceId ?? "");
                writer.Write((byte)deviceIdBytes.Length);
                writer.Write(deviceIdBytes);

                // Physical transform with NaN values (now 6 floats for consistency)
                for (int i = 0; i < 6; i++)
                {
                    writer.Write(float.NaN);
                }

                // Head transform (NaN values)
                for (int i = 0; i < 6; i++)
                {
                    writer.Write(float.NaN);
                }

                // Right hand transform (NaN values)
                for (int i = 0; i < 6; i++)
                {
                    writer.Write(float.NaN);
                }

                // Left hand transform (NaN values)
                for (int i = 0; i < 6; i++)
                {
                    writer.Write(float.NaN);
                }

                // No virtual transforms for stealth handshake
                writer.Write((byte)0);

                return ms.ToArray();
            }
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
                if (messageType < MSG_CLIENT_TRANSFORM || messageType > MSG_CLIENT_VAR_SYNC)
                {
                    // Don't throw exception, just return invalid type with null data
                    // This allows the caller to handle it gracefully
                    return (messageType, null);
                }

                switch (messageType)
                {
                    // case MSG_CLIENT_TRANSFORM:
                    //     return (messageType, DeserializeClientTransform(reader));
                    case MSG_ROOM_TRANSFORM:
                        return (messageType, DeserializeRoomTransform(reader));
                    case MSG_RPC_BROADCAST:
                        // RPC message
                        return (messageType, DeserializeRPCMessage(reader));
                    case MSG_RPC_SERVER:
                        // Server RPC request from client
                        return (messageType, DeserializeRPCMessage(reader));
                    case MSG_RPC_CLIENT:
                        // Client-to-client RPC message
                        return (messageType, DeserializeRPCClientMessage(reader));
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


        // Helper to read TransformData as 6 floats
        private static TransformData ReadTransformData(BinaryReader reader)
        {
            var data = new TransformData();
            data.posX = reader.ReadSingle();
            data.posY = reader.ReadSingle();
            data.posZ = reader.ReadSingle();
            data.rotX = reader.ReadSingle();
            data.rotY = reader.ReadSingle();
            data.rotZ = reader.ReadSingle();
            return data;
        }

        private static RoomTransformData DeserializeRoomTransform(BinaryReader reader)
        {
            var data = new RoomTransformData();

            // Room ID
            var roomIdLength = reader.ReadByte();
            data.roomId = System.Text.Encoding.UTF8.GetString(reader.ReadBytes(roomIdLength));

            // Number of clients
            var clientCount = reader.ReadUInt16();
            data.clients = new List<ClientTransformData>(clientCount);

            // Each client with short ID
            for (int i = 0; i < clientCount; i++)
            {
                var client = new ClientTransformData();

                // Client number (2 bytes)
                client.clientNo = reader.ReadUInt16();

                // Note: Device ID is NOT sent in MSG_ROOM_TRANSFORM
                // Device ID will be resolved from client number using mapping table

                // Physical transform (now full 6 floats)
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
        /// Serialize an RPC broadcast message
        /// </summary>
        public static byte[] SerializeRPCMessage(RPCMessage msg)
        {
            using var ms = new MemoryStream();
            using var writer = new BinaryWriter(ms);
            // Message type
            writer.Write(MSG_RPC_BROADCAST);
            // Sender client number (2 bytes)
            writer.Write((ushort)msg.senderClientNo);
            // Function name (length-prefixed byte)
            var nameBytes = System.Text.Encoding.UTF8.GetBytes(msg.functionName);
            if (nameBytes.Length > 255) { throw new ArgumentException("Function name is too long. Maximum length is 255 bytes."); }
            writer.Write((byte)nameBytes.Length);
            writer.Write(nameBytes);
            // Arguments JSON
            var argsBytes = System.Text.Encoding.UTF8.GetBytes(msg.argumentsJson);
            writer.Write((ushort)argsBytes.Length);
            writer.Write(argsBytes);
            return ms.ToArray();
        }

        /// <summary>
        /// Serialize a client-to-server RPC request
        /// </summary>
        public static byte[] SerializeRPCRequest(RPCMessage msg)
        {
            using var ms = new MemoryStream();
            using var writer = new BinaryWriter(ms);
            // Message type
            writer.Write(MSG_RPC_SERVER);
            // Sender client number (2 bytes)
            writer.Write((ushort)msg.senderClientNo);
            // Function name (length-prefixed byte)
            var nameBytes = System.Text.Encoding.UTF8.GetBytes(msg.functionName);
            if (nameBytes.Length > 255) { throw new ArgumentException("Function name is too long. Maximum length is 255 bytes."); }
            writer.Write((byte)nameBytes.Length);
            writer.Write(nameBytes);
            // Arguments JSON (length-prefixed ushort)
            var argsBytes = System.Text.Encoding.UTF8.GetBytes(msg.argumentsJson);
            writer.Write((ushort)argsBytes.Length);
            writer.Write(argsBytes);
            return ms.ToArray();
        }

        /// <summary>
        /// Serialize a client-to-client RPC message
        /// </summary>
        public static byte[] SerializeRPCClientMessage(RPCClientMessage msg)
        {
            using var ms = new MemoryStream();
            using var writer = new BinaryWriter(ms);
            // Message type
            writer.Write(MSG_RPC_CLIENT);
            // Sender client number (2 bytes)
            writer.Write((ushort)msg.senderClientNo);
            // Target client number (2 bytes)
            writer.Write((ushort)msg.targetClientNo);
            // Function name (length-prefixed byte)
            var nameBytes = System.Text.Encoding.UTF8.GetBytes(msg.functionName);
            if (nameBytes.Length > 255) { throw new ArgumentException("Function name is too long. Maximum length is 255 bytes."); }
            writer.Write((byte)nameBytes.Length);
            writer.Write(nameBytes);
            // Arguments JSON (length-prefixed ushort)
            var argsBytes = System.Text.Encoding.UTF8.GetBytes(msg.argumentsJson);
            writer.Write((ushort)argsBytes.Length);
            writer.Write(argsBytes);
            return ms.ToArray();
        }

        /// <summary>
        /// Serialize global variable set message
        /// </summary>
        public static byte[] SerializeGlobalVarSet(Dictionary<string, object> data)
        {
            using var ms = new MemoryStream();
            using var writer = new BinaryWriter(ms);

            // Message type
            writer.Write(MSG_GLOBAL_VAR_SET);

            // Sender client number (2 bytes)
            var senderClientNo = data.TryGetValue("senderClientNo", out var senderObj) ? Convert.ToUInt16(senderObj) : (ushort)0;
            writer.Write(senderClientNo);

            // Variable name (max 64 bytes)
            var varName = data.TryGetValue("variableName", out var nameObj) ? nameObj?.ToString() ?? "" : "";
            if (varName.Length > 64) varName = varName.Substring(0, 64);
            var nameBytes = System.Text.Encoding.UTF8.GetBytes(varName);
            writer.Write((byte)nameBytes.Length);
            writer.Write(nameBytes);

            // Variable value (max 1024 bytes)
            var varValue = data.TryGetValue("variableValue", out var valueObj) ? valueObj?.ToString() ?? "" : "";
            if (varValue.Length > 1024) varValue = varValue.Substring(0, 1024);
            var valueBytes = System.Text.Encoding.UTF8.GetBytes(varValue);
            writer.Write((ushort)valueBytes.Length);
            writer.Write(valueBytes);

            // Timestamp (8 bytes double)
            var timestamp = data.TryGetValue("timestamp", out var timestampObj) ? Convert.ToDouble(timestampObj) : 0.0;
            writer.Write(timestamp);

            return ms.ToArray();
        }

        /// <summary>
        /// Serialize client variable set message
        /// </summary>
        public static byte[] SerializeClientVarSet(Dictionary<string, object> data)
        {
            using var ms = new MemoryStream();
            using var writer = new BinaryWriter(ms);

            // Message type
            writer.Write(MSG_CLIENT_VAR_SET);

            // Sender client number (2 bytes)
            var senderClientNo = data.TryGetValue("senderClientNo", out var senderObj) ? Convert.ToUInt16(senderObj) : (ushort)0;
            writer.Write(senderClientNo);

            // Target client number (2 bytes)
            var targetClientNo = data.TryGetValue("targetClientNo", out var targetObj) ? Convert.ToUInt16(targetObj) : (ushort)0;
            writer.Write(targetClientNo);

            // Variable name (max 64 bytes)
            var varName = data.TryGetValue("variableName", out var nameObj) ? nameObj?.ToString() ?? "" : "";
            if (varName.Length > 64) varName = varName.Substring(0, 64);
            var nameBytes = System.Text.Encoding.UTF8.GetBytes(varName);
            writer.Write((byte)nameBytes.Length);
            writer.Write(nameBytes);

            // Variable value (max 1024 bytes)
            var varValue = data.TryGetValue("variableValue", out var valueObj) ? valueObj?.ToString() ?? "" : "";
            if (varValue.Length > 1024) varValue = varValue.Substring(0, 1024);
            var valueBytes = System.Text.Encoding.UTF8.GetBytes(varValue);
            writer.Write((ushort)valueBytes.Length);
            writer.Write(valueBytes);

            // Timestamp (8 bytes double)
            var timestamp = data.TryGetValue("timestamp", out var timestampObj) ? Convert.ToDouble(timestampObj) : 0.0;
            writer.Write(timestamp);

            return ms.ToArray();
        }

        /// <summary>
        /// Deserialize an RPC broadcast message
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
        /// Deserialize a client-to-client RPC message
        /// </summary>
        private static RPCClientMessage DeserializeRPCClientMessage(BinaryReader reader)
        {
            // Sender client number (2 bytes)
            var senderClientNo = reader.ReadUInt16();
            // Target client number (2 bytes)
            var targetClientNo = reader.ReadUInt16();
            // Function name
            var nameLen = reader.ReadByte();
            var name = System.Text.Encoding.UTF8.GetString(reader.ReadBytes(nameLen));
            // Arguments JSON
            var argsLen = reader.ReadUInt16();
            var argsJson = System.Text.Encoding.UTF8.GetString(reader.ReadBytes(argsLen));
            return new RPCClientMessage
            {
                senderClientNo = senderClientNo,
                targetClientNo = targetClientNo,
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