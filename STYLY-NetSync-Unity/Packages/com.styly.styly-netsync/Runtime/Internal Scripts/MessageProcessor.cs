// MessageProcessor.cs - Handles incoming message processing and routing
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;
using UnityEngine;

namespace Styly.NetSync
{
    internal class MessageProcessor
    {
        // General-purpose message queue (RPC, variable sync, etc.)
        private readonly ConcurrentQueue<NetworkMessage> _messageQueue = new();

        // Ring buffer for room transform updates: keep only the latest few frames.
        // Rationale: room state is overwrite-only; older frames are not useful.
        private readonly ConcurrentQueue<RoomTransformData> _roomTransformQueue = new();
        private const int MaxRoomTransformUpdatesQueueSize = 2; // Keep only the most recent N updates
        private readonly bool _logNetworkTraffic;
        private int _messagesReceived;
        private readonly Dictionary<int, string> _clientNoToDeviceId = new();
        private readonly Dictionary<string, int> _deviceIdToClientNo = new();
        private readonly Dictionary<int, bool> _clientNoToIsStealthMode = new();
        private readonly Dictionary<int, ClientTransformData> _pendingClients = new(); // Clients waiting for ID mapping
        private readonly HashSet<int> _knownConnectedClients = new HashSet<int>(); // Clients we announced via OnAvatarConnected
        private string _localDeviceId;
        private int _localClientNo = 0;
        private NetSyncManager _netSyncManager; // Reference to NetSyncManager for triggering ready checks

        // Scratch collections reused per-room update to avoid frequent allocations (GC pressure)
        // NOTE: These are used only on the main thread inside ProcessRoomTransform.
        private readonly HashSet<int> _scratchAlive = new HashSet<int>();
        private readonly List<int> _scratchToDisconnect = new List<int>(32);

        public int MessagesReceived => _messagesReceived;
        public event System.Action<int> OnLocalClientNoAssigned;

        public MessageProcessor(bool logNetworkTraffic)
        {
            _logNetworkTraffic = logNetworkTraffic;
        }

        public void SetLocalDeviceId(string deviceId)
        {
            _localDeviceId = deviceId;
        }

        public void SetLocalClientNo(int clientNo)
        {
            _localClientNo = clientNo;
            // Local client number set
        }
        
        public void SetNetSyncManager(NetSyncManager netSyncManager)
        {
            _netSyncManager = netSyncManager;
        }

        public void ProcessIncomingMessage(byte[] payload)
        {
            try
            {
                var (msgType, data) = BinarySerializer.Deserialize(payload);
                if (data == null)
                {
                    if (_logNetworkTraffic) { Debug.LogWarning("Deserialize => null"); }
                    return;
                }

                switch (msgType)
                {
                    case BinarySerializer.MSG_ROOM_TRANSFORM when data is RoomTransformData room:
                        // Drop-old strategy: keep only the latest few room updates.
                        // NOTE: We use a dedicated queue so we never discard non-room messages.
                        while (_roomTransformQueue.Count >= MaxRoomTransformUpdatesQueueSize)
                        {
                            _roomTransformQueue.TryDequeue(out _); // Drop the oldest room update
                        }
                        _roomTransformQueue.Enqueue(room);
                        _messagesReceived++;
                        break;

                    case BinarySerializer.MSG_RPC when data is RPCMessage rpc:
                        // Avoid JSON round-trip: parse args once and pass object via dataObj
                        var args = JsonConvert.DeserializeObject<string[]>(rpc.argumentsJson);
                        _messageQueue.Enqueue(new NetworkMessage
                        {
                            type = "rpc",
                            dataObj = new RpcMessageData { senderClientNo = rpc.senderClientNo, functionName = rpc.functionName, args = args }
                        });
                        _messagesReceived++;
                        break;


                    case BinarySerializer.MSG_DEVICE_ID_MAPPING when data is DeviceIdMappingData mappingData:
                        // Process ID mappings immediately (don't queue)
                        // ID mapping data received
                        ProcessIdMappings(mappingData);
                        _messagesReceived++;
                        break;

                    case BinarySerializer.MSG_GLOBAL_VAR_SYNC when data is Dictionary<string, object> globalVarData:
                        _messageQueue.Enqueue(new NetworkMessage { type = "global_var_sync", dataObj = globalVarData });
                        _messagesReceived++;
                        // Global variable sync data received
                        break;

                    case BinarySerializer.MSG_CLIENT_VAR_SYNC when data is Dictionary<string, object> clientVarData:
                        _messageQueue.Enqueue(new NetworkMessage { type = "client_var_sync", dataObj = clientVarData });
                        _messagesReceived++;
                        // Client variable sync data received
                        break;

                    default:
                        if (_logNetworkTraffic) { Debug.LogWarning($"Unhandled type: {msgType}"); }
                        break;
                }
            }
            catch (Exception ex)
            {
                // Log error with more context for debugging
                Debug.LogError($"Binary parse error: {ex.Message}");

                // Log first few bytes of payload for debugging
                if (payload != null && payload.Length > 0)
                {
                    var hexDump = BitConverter.ToString(payload.Take(Math.Min(32, payload.Length)).ToArray());
                    Debug.LogError($"First bytes of problematic payload: {hexDump} (length: {payload.Length})");

                    // Log the message type byte specifically
                    if (payload.Length >= 1)
                    {
                        Debug.LogError($"Message type byte: {payload[0]} (0x{payload[0]:X2})");
                    }
                }
            }
        }

        public void ProcessMessageQueue(AvatarManager avatarManager, RPCManager rpcManager, string localDeviceId, NetSyncManager netSyncManager = null, NetworkVariableManager networkVariableManager = null)
        {
            // First, flush room transforms (bounded by MaxRoomTransformUpdatesQueueSize)
            // Purpose: ensure latest room state is applied promptly without starving other messages.
            while (_roomTransformQueue.TryDequeue(out var room))
            {
                ProcessRoomTransform(room, avatarManager, localDeviceId, netSyncManager);
            }

            while (_messageQueue.TryDequeue(out var msg))
            {
                switch (msg.type)
                {
                    case "room_transform":
                        // Object-only path (protocol unchanged); no JSON fallback.
                        if (msg.dataObj is RoomTransformData roomObj)
                        {
                            ProcessRoomTransform(roomObj, avatarManager, localDeviceId, netSyncManager);
                        }
                        else
                        {
                            Debug.LogError("[MessageProcessor] room_transform without dataObj (unexpected)");
                        }
                        break;

                    case "rpc":
                        if (msg.dataObj is RpcMessageData rpcObj)
                        {
                            rpcManager.EnqueueRPC(rpcObj.senderClientNo, rpcObj.functionName, rpcObj.args);
                        }
                        else
                        {
                            Debug.LogError("[MessageProcessor] rpc without dataObj (unexpected)");
                        }
                        break;

                    case "global_var_sync":
                        ProcessGlobalVariableSync(msg.dataObj as Dictionary<string, object>, networkVariableManager);
                        break;

                    case "client_var_sync":
                        ProcessClientVariableSync(msg.dataObj as Dictionary<string, object>, networkVariableManager);
                        break;
                }
            }

            // Check for pending clients that now have ID mappings
            if (_pendingClients.Count > 0)
            {
                var pendingToProcess = new List<int>();
                foreach (var kvp in _pendingClients)
                {
                    if (_clientNoToDeviceId.TryGetValue(kvp.Key, out var deviceId))
                    {
                        pendingToProcess.Add(kvp.Key);
                        // Found ID mapping for pending client
                    }
                }

                foreach (var clientNo in pendingToProcess)
                {
                    if (_pendingClients.TryGetValue(clientNo, out var pendingClient) &&
                        _clientNoToDeviceId.TryGetValue(clientNo, out var deviceId))
                    {
                        // Update device ID
                        pendingClient.deviceId = deviceId;

                        // Spawn the avatar and announce connection
                        if (!avatarManager.ConnectedPeers.ContainsKey(clientNo))
                        {
                            // Spawning pending client

                            // Spawn the remote avatar with the device ID
                            if (netSyncManager != null)
                            {
                                var remoteAvatarPrefab = netSyncManager.GetRemoteAvatarPrefab();
                                if (remoteAvatarPrefab != null)
                                {
                                    avatarManager.SpawnRemoteAvatar(clientNo, deviceId, remoteAvatarPrefab, netSyncManager);
                                }

                                // Announce connection regardless of avatar prefab
                                if (!_knownConnectedClients.Contains(clientNo))
                                {
                                    if (netSyncManager.OnAvatarConnected != null)
                                    {
                                        netSyncManager.OnAvatarConnected.Invoke(clientNo);
                                    }
                                    _knownConnectedClients.Add(clientNo);
                                }
                            }
                            else
                            {
                                // Fallback to just updating the avatar if NetSyncManager is not available
                                avatarManager.UpdateRemoteAvatar(clientNo, pendingClient);
                            }
                        }

                        // Remove from pending
                        _pendingClients.Remove(clientNo);
                    }
                }
            }
        }

        /// <summary>
        /// Preferred entrypoint: process already-deserialized room data without JSON round-trip.
        /// </summary>
        private void ProcessRoomTransform(RoomTransformData room, AvatarManager avatarManager, string localDeviceId, NetSyncManager netSyncManager = null)
        {
            if (room == null)
            {
                Debug.LogError("RoomTransform: null object");
                return;
            }

            try
            {
                // Reuse scratch set for alive client tracking
                var alive = _scratchAlive;
                alive.Clear();
                foreach (var c in room.clients)
                {
                    // Skip local avatar by client number
                    if (c.clientNo == _localClientNo) { continue; }

                    alive.Add(c.clientNo);

                    // Check if avatar already exists and just needs update
                    if (avatarManager.ConnectedPeers.ContainsKey(c.clientNo))
                    {
                        // Update existing avatar
                        avatarManager.UpdateRemoteAvatar(c.clientNo, c);
                    }
                    else
                    {
                        // Store in pending queue for processing in ProcessMessageQueue
                        _pendingClients[c.clientNo] = c;
                        // Client added to pending queue
                    }

                    // Update Human Presence from the "physical" (local) pose relative to the Head's parent.
                    // We pass local coordinates here; NetSyncManager converts them to world space using the
                    // remote avatar's hierarchy and applies yaw-only for the visual indicator.
                    var physical = c.physical;
                    if (physical != null && netSyncManager != null)
                    {
                        var localPos = physical.GetPosition();
                        var localRot = physical.GetRotation();
                        netSyncManager.UpdateHumanPresenceTransform(c.clientNo, localPos, localRot);
                    }
                }

                // Check for disconnected clients (including ones without avatars)
                var toDisconnect = _scratchToDisconnect;
                toDisconnect.Clear();
                foreach (var known in _knownConnectedClients)
                {
                    if (!alive.Contains(known)) { toDisconnect.Add(known); }
                }
                foreach (var clientNo in toDisconnect)
                {
                    bool hadAvatar = avatarManager.ConnectedPeers.ContainsKey(clientNo);
                    if (hadAvatar)
                    {
                        avatarManager.RemoveClient(clientNo);
                        if (avatarManager.OnAvatarDisconnected != null)
                        {
                            avatarManager.OnAvatarDisconnected.Invoke(clientNo);
                        }
                    }
                    else
                    {
                        if (netSyncManager != null && netSyncManager.OnAvatarDisconnected != null)
                        {
                            netSyncManager.OnAvatarDisconnected.Invoke(clientNo);
                        }
                    }
                    _knownConnectedClients.Remove(clientNo);
                }
            }
            catch (Exception ex)
            {
                Debug.LogError($"RoomTransform error: {ex.Message}");
            }
        }

        private class RpcMessageData
        {
            public int senderClientNo { get; set; }
            public string functionName { get; set; }
            public string[] args { get; set; }
        }

        private void ProcessIdMappings(DeviceIdMappingData mappingData)
        {
            // Clear existing mappings
            _clientNoToDeviceId.Clear();
            _deviceIdToClientNo.Clear();
            _clientNoToIsStealthMode.Clear();

            // Add new mappings
            foreach (var mapping in mappingData.mappings)
            {
                _clientNoToDeviceId[mapping.clientNo] = mapping.deviceId;
                _deviceIdToClientNo[mapping.deviceId] = mapping.clientNo;
                _clientNoToIsStealthMode[mapping.clientNo] = mapping.isStealthMode;

                // ID Mapping: ClientNo => Device ID

                // Check if this is the local client's mapping
                if (!string.IsNullOrEmpty(_localDeviceId) && mapping.deviceId == _localDeviceId)
                {
                    SetLocalClientNo(mapping.clientNo);
                    if (OnLocalClientNoAssigned != null)
                    {
                        OnLocalClientNoAssigned.Invoke(mapping.clientNo);
                    }
                }

                // Check if we have pending clients waiting for this mapping
                if (_pendingClients.TryGetValue(mapping.clientNo, out var pendingClient))
                {
                    // Update device ID
                    pendingClient.deviceId = mapping.deviceId;

                    // Remove from pending
                    _pendingClients.Remove(mapping.clientNo);

                    // Client now has device ID mapping
                }
            }

            // ID mappings updated

            // Pending clients will be processed in ProcessMessageQueue on the main thread
        }

        public int GetClientNo(string deviceId)
        {
            if (_deviceIdToClientNo.TryGetValue(deviceId, out var clientNo))
            {
                return clientNo;
            }
            else
            {
                if (_logNetworkTraffic)
                {
                    Debug.LogWarning($"[MessageProcessor] No client number mapping for device ID: {deviceId}");
                }
                return 0;
            }
        }

        public string GetDeviceIdFromClientNo(int clientNo)
        {
            return _clientNoToDeviceId.TryGetValue(clientNo, out var deviceId) ? deviceId : null;
        }

        /// <summary>
        /// Returns a snapshot of all known client numbers based on the latest device ID mapping.
        /// This includes both visible and stealth clients. The returned array is a copy to avoid
        /// enumerating the live dictionary while it may be updated by the networking thread.
        /// </summary>
        internal int[] GetKnownClientNosSnapshot()
        {
            // Using Linq ToArray() produces a stable snapshot.
            // Note: Do not expose the live Keys collection to callers.
            return _clientNoToDeviceId.Keys.ToArray();
        }

        public bool IsClientStealthMode(int clientNo)
        {
            return _clientNoToIsStealthMode.TryGetValue(clientNo, out var isStealthMode) && isStealthMode;
        }

        private void ProcessGlobalVariableSync(Dictionary<string, object> variableData, NetworkVariableManager networkVariableManager)
        {
            if (networkVariableManager == null || variableData == null) return;

            try
            {
                bool wasFirstSync = !networkVariableManager.HasReceivedInitialSync;
                networkVariableManager.HandleGlobalVariableSync(variableData);

                // If this was the first sync, notify NetSyncManager to check ready state
                if (wasFirstSync && networkVariableManager.HasReceivedInitialSync)
                {
                    // Avoid null-propagation on UnityEngine.Object (NetSyncManager is a MonoBehaviour)
                    if (_netSyncManager != null)
                    {
                        _netSyncManager.TriggerReadyCheck();
                    }
                }

                // Processed global variable sync
            }
            catch (Exception ex)
            {
                Debug.LogError($"[MessageProcessor] Failed to process global variable sync: {ex.Message}");
            }
        }

        private void ProcessClientVariableSync(Dictionary<string, object> variableData, NetworkVariableManager networkVariableManager)
        {
            if (networkVariableManager == null)
            {
                Debug.LogWarning("[MessageProcessor] NetworkVariableManager is null, cannot process client variable sync");
                return;
            }

            if (variableData == null)
            {
                Debug.LogWarning("[MessageProcessor] Variable data is null");
                return;
            }

            try
            {
                // Processing client variable sync data

                bool wasFirstSync = !networkVariableManager.HasReceivedInitialSync;
                networkVariableManager.HandleClientVariableSync(variableData);

                // If this was the first sync, notify NetSyncManager to check ready state
                if (wasFirstSync && networkVariableManager.HasReceivedInitialSync)
                {
                    // Avoid null-propagation on UnityEngine.Object (NetSyncManager is a MonoBehaviour)
                    if (_netSyncManager != null)
                    {
                        _netSyncManager.TriggerReadyCheck();
                    }
                }

                // Processed client variable sync
            }
            catch (Exception ex)
            {
                Debug.LogError($"[MessageProcessor] Failed to process client variable sync: {ex.Message}");
            }
        }

        /// <summary>
        /// Clears all room-scoped state for room switching.
        /// Called before reconnecting to a new room to prevent state leaks.
        /// </summary>
        internal void ClearRoomScopedState()
        {
            _clientNoToDeviceId.Clear();
            _deviceIdToClientNo.Clear();
            _clientNoToIsStealthMode.Clear();
            _pendingClients.Clear();
            _knownConnectedClients.Clear();
            SetLocalClientNo(0);   // reset local mapping

            // Also clear message queues to avoid cross-room leakage.
            while (_roomTransformQueue.TryDequeue(out _)) { }
            while (_messageQueue.TryDequeue(out _)) { }
        }
    }
}
