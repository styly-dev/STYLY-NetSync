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
        public const byte MSG_GROUP_TRANSFORM = 2;  // Group transform with short IDs only
        public const byte MSG_RPC_BROADCAST = 3;   // Broadcast function call
        public const byte MSG_RPC_SERVER = 4;   // Client-to-server RPC call
        public const byte MSG_RPC_CLIENT = 5;   // Client-to-client RPC call
        public const byte MSG_DEVICE_ID_MAPPING = 6;  // Device ID mapping notification
        public const byte MSG_GLOBAL_VAR_SET = 7;  // Set global variable
        public const byte MSG_GLOBAL_VAR_SYNC = 8;  // Sync global variables
        public const byte MSG_CLIENT_VAR_SET = 9;  // Set client variable
        public const byte MSG_CLIENT_VAR_SYNC = 10;  // Sync client variables

        // Transform data type identifiers
        private const byte TRANSFORM_PHYSICAL = 1;  // 3 floats: posX, posZ, rotY
        private const byte TRANSFORM_VIRTUAL = 2;   // 6 floats: full transform

        #region === Serialization ===

        public static byte[] SerializeClientTransform(ClientTransformData data)
        {
            using (var ms = new MemoryStream())
            using (var writer = new BinaryWriter(ms))
            {
                // Message type
                writer.Write(MSG_CLIENT_TRANSFORM);

                // Device ID (as UTF8 bytes with length prefix)
                var deviceIdBytes = System.Text.Encoding.UTF8.GetBytes(data.deviceId);
                writer.Write((byte)deviceIdBytes.Length);
                writer.Write(deviceIdBytes);
                
                // Note: Client number is not sent by client, only assigned by server

                // Physical transform (optimized: 3 floats only)
                {
                    writer.Write(data.physical.posX);
                    writer.Write(data.physical.posZ);
                    writer.Write(data.physical.rotY);
                }

                // Head transform
                {
                    writer.Write(data.head.posX);
                    writer.Write(data.head.posY);
                    writer.Write(data.head.posZ);
                    writer.Write(data.head.rotX);
                    writer.Write(data.head.rotY);
                    writer.Write(data.head.rotZ);
                }

                // Right hand transform
                {
                    writer.Write(data.rightHand.posX);
                    writer.Write(data.rightHand.posY);
                    writer.Write(data.rightHand.posZ);
                    writer.Write(data.rightHand.rotX);
                    writer.Write(data.rightHand.rotY);
                    writer.Write(data.rightHand.rotZ);
                }

                // Left hand transform
                {
                    writer.Write(data.leftHand.posX);
                    writer.Write(data.leftHand.posY);
                    writer.Write(data.leftHand.posZ);
                    writer.Write(data.leftHand.rotX);
                    writer.Write(data.leftHand.rotY);
                    writer.Write(data.leftHand.rotZ);
                }

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
                        var vt = data.virtuals[i];
                        writer.Write(vt.posX);
                        writer.Write(vt.posY);
                        writer.Write(vt.posZ);
                        writer.Write(vt.rotX);
                        writer.Write(vt.rotY);
                        writer.Write(vt.rotZ);
                    }
                }

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
                    case MSG_GROUP_TRANSFORM:
                        return (messageType, DeserializeGroupTransform(reader));
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


        private static GroupTransformData DeserializeGroupTransform(BinaryReader reader)
        {
            var data = new GroupTransformData();

            // Group ID
            var groupIdLength = reader.ReadByte();
            data.groupId = System.Text.Encoding.UTF8.GetString(reader.ReadBytes(groupIdLength));

            // Number of clients
            var clientCount = reader.ReadUInt16();
            data.clients = new List<ClientTransformData>(clientCount);

            // Each client with short ID
            for (int i = 0; i < clientCount; i++)
            {
                var client = new ClientTransformData();

                // Client number (2 bytes)
                client.clientNo = reader.ReadUInt16();
                
                // Note: Device ID is no longer sent in MSG_GROUP_TRANSFORM
                // Device ID will be resolved from client number using mapping table

                // Physical transform
                {
                    var posX = reader.ReadSingle();
                    var posZ = reader.ReadSingle();
                    var rotY = reader.ReadSingle();
                    client.physical = new Transform3D(posX, posZ, rotY, true);
                }

                // Head transform
                {
                    var posX = reader.ReadSingle();
                    var posY = reader.ReadSingle();
                    var posZ = reader.ReadSingle();
                    var rotX = reader.ReadSingle();
                    var rotY = reader.ReadSingle();
                    var rotZ = reader.ReadSingle();
                    client.head = new Transform3D(posX, posY, posZ, rotX, rotY, rotZ, false);
                }

                // Right hand transform
                {
                    var posX = reader.ReadSingle();
                    var posY = reader.ReadSingle();
                    var posZ = reader.ReadSingle();
                    var rotX = reader.ReadSingle();
                    var rotY = reader.ReadSingle();
                    var rotZ = reader.ReadSingle();
                    client.rightHand = new Transform3D(posX, posY, posZ, rotX, rotY, rotZ, false);
                }

                // Left hand transform
                {
                    var posX = reader.ReadSingle();
                    var posY = reader.ReadSingle();
                    var posZ = reader.ReadSingle();
                    var rotX = reader.ReadSingle();
                    var rotY = reader.ReadSingle();
                    var rotZ = reader.ReadSingle();
                    client.leftHand = new Transform3D(posX, posY, posZ, rotX, rotY, rotZ, false);
                }

                // Virtual transforms
                var virtualCount = reader.ReadByte();
                
                // Validate virtual count to prevent memory issues
                if (virtualCount > MAX_VIRTUAL_TRANSFORMS)
                {
                    virtualCount = MAX_VIRTUAL_TRANSFORMS;
                }
                
                if (virtualCount > 0)
                {
                    client.virtuals = new List<Transform3D>(virtualCount);
                    for (int j = 0; j < virtualCount; j++)
                    {
                        var posX = reader.ReadSingle();
                        var posY = reader.ReadSingle();
                        var posZ = reader.ReadSingle();
                        var rotX = reader.ReadSingle();
                        var rotY = reader.ReadSingle();
                        var rotZ = reader.ReadSingle();
                        client.virtuals.Add(new Transform3D(posX, posY, posZ, rotX, rotY, rotZ, false));
                    }
                }

                data.clients.Add(client);
            }
            return data;
        }


        #endregion

        #region === Size Calculation ===

        public static int CalculateClientTransformSize(ClientTransformData data)
        {
            int size = 1; // Message type
            size += 1 + System.Text.Encoding.UTF8.GetByteCount(data.deviceId); // Device ID

            // Physical transform
            if (data.physical != null && data.physical.isLocalSpace)
            {
                size += 1 + 12; // Type + 3 floats
            }
            else if (data.physical != null)
            {
                size += 1 + 24; // Type + 6 floats
            }
            else
            {
                size += 1; // Just type (0)
            }

            // Virtual transforms
            size += 1; // Count
            if (data.virtuals != null)
            {
                size += data.virtuals.Count * 24; // 6 floats each
            }

            return size;
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