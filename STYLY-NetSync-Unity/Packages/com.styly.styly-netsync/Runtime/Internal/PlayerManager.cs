// PlayerManager.cs - Handles player spawning and lifecycle management
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Events;

namespace Styly.NetSync
{
    public class PlayerManager
    {
        private readonly Dictionary<int, GameObject> _connectedPeers = new();
        private NetSyncAvatar _localPlayerAvatar;
        private bool _enableDebugLogs;

        public UnityEvent<int> OnClientConnected { get; } = new();
        public UnityEvent<int> OnClientDisconnected { get; } = new();

        public NetSyncAvatar LocalPlayerAvatar => _localPlayerAvatar;
        public IReadOnlyDictionary<int, GameObject> ConnectedPeers => _connectedPeers;

        public PlayerManager(bool enableDebugLogs)
        {
            _enableDebugLogs = enableDebugLogs;
        }

        public void InitializeLocalPlayer(GameObject localPlayerPrefab, string deviceId, NetSyncManager netSyncManager)
        {
            if (localPlayerPrefab == null)
            {
                Debug.Log("[NetSync] ***** Stealth mode enabled ***** - No local player avatar will be spawned (LocalPlayerPrefab not set)");
                return;
            }

            // Instantiate a local avatar prefab asset
            GameObject localGO= null;
            if (localPlayerPrefab.scene.IsValid())
            {
                Debug.LogError("LocalPlayer Prefab should not be a scene object. Please use a prefab asset instead.");
            }
            else
            {
                // If XR Origin exists, instantiate the local player prefab as a child of the XR Origin
                var xrOrigin = Object.FindFirstObjectByType<Unity.XR.CoreUtils.XROrigin>();
                if (xrOrigin != null)
                {
                    localGO = Object.Instantiate(localPlayerPrefab, xrOrigin.transform);
                }
                else
                {
                    localGO = Object.Instantiate(localPlayerPrefab);
                }
            }

            _localPlayerAvatar = localGO.GetComponent<NetSyncAvatar>();
            if (_localPlayerAvatar == null)
            {
                Debug.LogError("LocalPlayer Prefab requires NetSyncAvatar component");
                return;
            }

            _localPlayerAvatar.Initialize(deviceId, true, netSyncManager);
            DebugLog($"Local player initialized with ID: {deviceId}");
        }

        public void SpawnRemotePlayer(int clientNo, string deviceId, GameObject remotePlayerPrefab, NetSyncManager netSyncManager)
        {
            if (remotePlayerPrefab == null || _connectedPeers.ContainsKey(clientNo)) { return; }
            
            if (string.IsNullOrEmpty(deviceId))
            {
                Debug.LogError($"Cannot spawn remote player {clientNo} without device ID");
                return;
            }

            var go = Object.Instantiate(remotePlayerPrefab);
            var net = go.GetComponent<NetSyncAvatar>();
            if (!net)
            {
                Object.Destroy(go);
                return;
            }

            net.InitializeRemote(clientNo, netSyncManager);
            net.UpdateDeviceId(deviceId);
            _connectedPeers[clientNo] = go;
            
            // Set the GameObject name to include the client ID instead of "(Clone)"
            // Get the original prefab name without "(Clone)" suffix
            string prefabName = remotePlayerPrefab.name;
            go.name = $"{prefabName} ({clientNo})";
            
            DebugLog($"Remote player spawned: clientNo={clientNo}, deviceId={deviceId}");
        }

        public void RemoveClient(int clientNo)
        {
            if (_connectedPeers.Remove(clientNo, out var go) && go)
            {
                Object.Destroy(go);
                DebugLog($"Remote player removed: clientNo={clientNo}");
            }
        }

        public void CleanupRemotePlayers()
        {
            foreach (var go in _connectedPeers.Values) { if (go) { Object.Destroy(go); } }

            _connectedPeers.Clear();
            DebugLog("All remote players cleaned up");
        }

        public void UpdateRemotePlayer(int clientNo, ClientTransformData transformData)
        {
            if (_connectedPeers.TryGetValue(clientNo, out var go))
            {
                go.GetComponent<NetSyncAvatar>().SetTransformData(transformData);
            }
        }

        public HashSet<int> GetAliveClients(MessageProcessor messageProcessor = null, bool includeStealthClients = false)
        {
            // If explicitly including stealth clients or no processor available to check
            if (includeStealthClients || messageProcessor == null)
            {
                // Return all clients
                return new HashSet<int>(_connectedPeers.Keys);
            }
            
            // Filter out stealth mode clients (default behavior)
            var result = new HashSet<int>();
            foreach (var clientNo in _connectedPeers.Keys)
            {
                if (!messageProcessor.IsClientStealthMode(clientNo))
                {
                    result.Add(clientNo);
                }
            }
            return result;
        }

        private void DebugLog(string msg)
        {
            if (_enableDebugLogs) { Debug.Log($"[PlayerManager] {msg}"); }
        }
    }
}