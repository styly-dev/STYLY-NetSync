// MessageProcessor.cs - Handles incoming message processing and routing
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;
using UnityEngine;

namespace Styly.NetSync
{
    public class MessageProcessor
    {
        private readonly ConcurrentQueue<NetworkMessage> _messageQueue = new();
        private readonly bool _logNetworkTraffic;
        private int _messagesReceived;
        private readonly Dictionary<int, string> _clientNoToDeviceId = new();
        private readonly Dictionary<string, int> _deviceIdToClientNo = new();
        private readonly Dictionary<int, bool> _clientNoToIsStealthMode = new();
        private readonly Dictionary<int, ClientTransformData> _pendingClients = new(); // Clients waiting for ID mapping
        private string _localDeviceId;
        private int _localClientNo = 0;

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
                    case BinarySerializer.MSG_ROOM_TRANSFORM when data is RoomTransformData:
                        var json = JsonConvert.SerializeObject(data);
                        _messageQueue.Enqueue(new NetworkMessage { type = "room_transform", data = json });
                        _messagesReceived++;
                        break;

                    case BinarySerializer.MSG_RPC_BROADCAST when data is RPCMessage rpc:
                        var args = JsonConvert.DeserializeObject<string[]>(rpc.argumentsJson);
                        _messageQueue.Enqueue(new NetworkMessage
                        {
                            type = "rpc",
                            data = JsonConvert.SerializeObject(new { senderClientNo = rpc.senderClientNo, rpc.functionName, args })
                        });
                        _messagesReceived++;
                        break;

                    case BinarySerializer.MSG_RPC_CLIENT when data is RPCClientMessage clientRpc:
                        var clientArgs = JsonConvert.DeserializeObject<string[]>(clientRpc.argumentsJson);
                        _messageQueue.Enqueue(new NetworkMessage
                        {
                            type = "rpc",
                            data = JsonConvert.SerializeObject(new { senderClientNo = clientRpc.senderClientNo, clientRpc.functionName, args = clientArgs })
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

        public void ProcessMessageQueue(PlayerManager playerManager, RPCManager rpcManager, string localDeviceId, NetSyncManager netSyncManager = null, NetworkVariableManager networkVariableManager = null)
        {
            while (_messageQueue.TryDequeue(out var msg))
            {
                switch (msg.type)
                {
                    case "room_transform":
                        ProcessRoomTransform(msg.data, playerManager, localDeviceId, netSyncManager);
                        break;

                    case "rpc":
                        ProcessRPCMessage(msg.data, rpcManager);
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
                        
                        // Spawn the player
                        if (!playerManager.ConnectedPeers.ContainsKey(clientNo))
                        {
                            // Spawning pending client
                            
                            // Spawn the remote player with the device ID
                            if (netSyncManager != null)
                            {
                                var remotePlayerPrefab = netSyncManager.GetRemotePlayerPrefab();
                                if (remotePlayerPrefab != null)
                                {
                                    playerManager.SpawnRemotePlayer(clientNo, deviceId, remotePlayerPrefab, netSyncManager);
                                    netSyncManager.OnClientConnected?.Invoke(clientNo);
                                }
                            }
                            else
                            {
                                // Fallback to just updating the player if NetSyncManager is not available
                                playerManager.UpdateRemotePlayer(clientNo, pendingClient);
                            }
                        }
                        
                        // Remove from pending
                        _pendingClients.Remove(clientNo);
                    }
                }
            }
        }

        private void ProcessRoomTransform(string json, PlayerManager playerManager, string localDeviceId, NetSyncManager netSyncManager = null)
        {
            if (string.IsNullOrEmpty(json))
            {
                Debug.LogError("RoomTransform: empty JSON");
                return;
            }

            try
            {
                var room = JsonConvert.DeserializeObject<RoomTransformData>(json);
                if (room == null) { return; }

                var alive = new HashSet<int>();
                foreach (var c in room.clients)
                {
                    // Skip local player by client number
                    if (c.clientNo == _localClientNo) { continue; }
                    
                    alive.Add(c.clientNo);
                    
                    // Check if player already exists and just needs update
                    if (playerManager.ConnectedPeers.ContainsKey(c.clientNo))
                    {
                        // Update existing player
                        playerManager.UpdateRemotePlayer(c.clientNo, c);
                    }
                    else
                    {
                        // Store in pending queue for processing in ProcessMessageQueue
                        _pendingClients[c.clientNo] = c;
                        
                        // Client added to pending queue
                    }
                }

                // Check for disconnected clients (including stealth clients)
                var currentClients = playerManager.GetAliveClients(this, includeStealthClients: true);
                foreach (var clientNo in currentClients)
                {
                    if (!alive.Contains(clientNo))
                    {
                        playerManager.RemoveClient(clientNo);
                        playerManager.OnClientDisconnected?.Invoke(clientNo);
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.LogError($"RoomTransform error: {ex.Message}");
            }
        }

        private void ProcessRPCMessage(string json, RPCManager rpcManager)
        {
            try
            {
                var rpcData = JsonConvert.DeserializeObject<RpcMessageData>(json);
                if (rpcData != null)
                {
                    rpcManager.EnqueueRPC(rpcData.senderClientNo, rpcData.functionName, rpcData.args);
                }
            }
            catch (Exception ex)
            {
                Debug.LogError($"RPC parse error: {ex.Message}");
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
                    OnLocalClientNoAssigned?.Invoke(mapping.clientNo);
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
        
        public bool IsClientStealthMode(int clientNo)
        {
            return _clientNoToIsStealthMode.TryGetValue(clientNo, out var isStealthMode) && isStealthMode;
        }

        private void ProcessGlobalVariableSync(Dictionary<string, object> variableData, NetworkVariableManager networkVariableManager)
        {
            if (networkVariableManager == null || variableData == null) return;
            
            try
            {
                networkVariableManager.HandleGlobalVariableSync(variableData);
                
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
                
                networkVariableManager.HandleClientVariableSync(variableData);
                
                // Processed client variable sync
            }
            catch (Exception ex)
            {
                Debug.LogError($"[MessageProcessor] Failed to process client variable sync: {ex.Message}");
            }
        }
    }
}