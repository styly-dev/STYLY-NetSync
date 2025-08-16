// DataStructure.cs
using System;
using System.Collections.Generic;
using UnityEngine;

namespace Styly.NetSync
{
    // Simple transform data structure (position + rotation euler angles)
    [Serializable]
    public class TransformData
    {
        public float posX;
        public float posY;
        public float posZ;
        public float rotX;
        public float rotY;
        public float rotZ;

        public TransformData()
        {
            posX = posY = posZ = 0;
            rotX = rotY = rotZ = 0;
        }

        public TransformData(Vector3 pos, Vector3 rot)
        {
            posX = pos.x;
            posY = pos.y;
            posZ = pos.z;
            rotX = rot.x;
            rotY = rot.y;
            rotZ = rot.z;
        }

        public Vector3 GetPosition() => new Vector3(posX, posY, posZ);
        public Vector3 GetRotation() => new Vector3(rotX, rotY, rotZ);
    }

    // Client transform data using unified structure
    [Serializable]
    public class ClientTransformData
    {
        public string deviceId;
        public int clientNo;  // Client number assigned by server (0 if not assigned)
        public TransformData physical;
        public TransformData head;
        public TransformData rightHand;
        public TransformData leftHand;
        public List<TransformData> virtuals;
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

    // RPC message structure
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