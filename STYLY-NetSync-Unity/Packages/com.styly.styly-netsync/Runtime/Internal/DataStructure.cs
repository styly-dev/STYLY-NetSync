// DataStructure.cs
using System;
using System.Collections.Generic;
using UnityEngine;

namespace Styly.NetSync
{
    // Unified transform data structure (supports both local and world coordinates)
    [Serializable]
    public class Transform3D
    {
        public float posX;
        public float posY;
        public float posZ;
        public float rotX;
        public float rotY;
        public float rotZ;
        public bool isLocalSpace; // true for local/physical, false for world/virtual

        public Transform3D() { }

        // Constructor for virtual/world transforms (full 6DOF)
        public Transform3D(float x, float y, float z, float rotX, float rotY, float rotZ, bool local = false)
        {
            posX = x;
            posY = y;
            posZ = z;
            this.rotX = rotX;
            this.rotY = rotY;
            this.rotZ = rotZ;
            isLocalSpace = local;
        }
    }

    // Client transform data using unified structure
    [Serializable]
    public class ClientTransformData
    {
        public string deviceId;
        public int clientNo;  // Client number assigned by server (0 if not assigned)
        public Transform3D physical;
        public Transform3D head;
        public Transform3D rightHand;
        public Transform3D leftHand;
        public List<Transform3D> virtuals;
    }

    // Room data from server
    [Serializable]
    public class RoomTransformData
    {
        public string roomId;
        public List<ClientTransformData> clients;
    }

    // Network message with string-based message types for better readability
    [Serializable]
    public class NetworkMessage
    {
        public string type; // "client_transform" or "room_transform"
        public string data;
        public object dataObj; // For preserving object types without JSON conversion
    }

    // RPC message structure for broadcast calls
    [Serializable]
    public class RPCMessage
    {
        // Client number of the sender
        public int senderClientNo;
        // Name of function to call
        public string functionName;
        // JSON-serialized function arguments array
        public string argumentsJson;
    }

    // RPC message structure for client-targeted calls
    [Serializable]
    public class RPCClientMessage
    {
        // Client number of the sender
        public int senderClientNo;
        // Client number of the target
        public int targetClientNo;
        // Name of function to call
        public string functionName;
        // JSON-serialized function arguments array
        public string argumentsJson;
    }

    // Device ID mapping data
    [Serializable]
    public class DeviceIdMapping
    {
        public int clientNo;
        public string deviceId;
        public bool isStealthMode;
    }

    // Device ID mapping notification message
    [Serializable]
    public class DeviceIdMappingData
    {
        public List<DeviceIdMapping> mappings;
    }
}