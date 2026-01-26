// DataStructure.cs
using System;
using System.Collections.Generic;
using UnityEngine;

namespace Styly.NetSync
{
    [Flags]
    internal enum PoseFlags : byte
    {
        None = 0,
        IsStealth = 1 << 0,
        PhysicalValid = 1 << 1,
        HeadValid = 1 << 2,
        RightValid = 1 << 3,
        LeftValid = 1 << 4,
        VirtualsValid = 1 << 5
    }

    // Pose data structure (position + rotation quaternion)
    [Serializable]
    internal class TransformData
    {
        public Vector3 position;
        public Quaternion rotation;

        public TransformData()
        {
            position = Vector3.zero;
            rotation = Quaternion.identity;
        }

        public TransformData(Vector3 pos, Quaternion rot)
        {
            position = pos;
            rotation = rot;
        }

        public Vector3 GetPosition() => position;

        /// <summary>
        /// Returns normalized rotation, guarding against zero quaternion.
        /// </summary>
        public Quaternion GetRotation()
        {
            if (rotation.x == 0f && rotation.y == 0f && rotation.z == 0f && rotation.w == 0f)
            {
                return Quaternion.identity;
            }
            return Quaternion.Normalize(rotation);
        }
    }

    // Client transform data using unified structure
    [Serializable]
    internal class ClientTransformData
    {
        public string deviceId;
        public int clientNo;  // Client number assigned by server (0 if not assigned)
        public double poseTime;
        public ushort poseSeq;
        public PoseFlags flags;
        public TransformData physical;
        public TransformData head;
        public TransformData rightHand;
        public TransformData leftHand;
        public List<TransformData> virtuals;
    }

    // Room data from server
    [Serializable]
    internal class RoomTransformData
    {
        public string roomId;
        public double broadcastTime;
        public List<ClientTransformData> clients;
    }

    // Network message with string-based message types for better readability
    [Serializable]
    internal class NetworkMessage
    {
        public string type; // "client_transform" or "room_transform"
        public string data;
        public object dataObj; // For preserving object types without JSON conversion
    }

    // RPC message structure
    [Serializable]
    internal class RPCMessage
    {
        // Client number of the sender
        public int senderClientNo;
        // Name of function to call
        public string functionName;
        // JSON-serialized function arguments array
        public string argumentsJson;
    }

    [Serializable]
    internal class RpcDeliveryMessage
    {
        public string rpcId;
        public int senderClientNo;
        public string functionName;
        public string argumentsJson;
    }


    // Device ID mapping data
    [Serializable]
    internal class DeviceIdMapping
    {
        public int clientNo;
        public string deviceId;
        public bool isStealthMode;
    }

    // Device ID mapping notification message
    [Serializable]
    internal class DeviceIdMappingData
    {
        public int serverVersionMajor;
        public int serverVersionMinor;
        public int serverVersionPatch;
        public List<DeviceIdMapping> mappings;
    }
}
