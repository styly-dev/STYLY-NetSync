// AvatarManager.cs - Handles avatar spawning and lifecycle management
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Events;

namespace Styly.NetSync
{
    public class AvatarManager
    {
        private readonly Dictionary<int, GameObject> _connectedPeers = new();
        private NetSyncAvatar _localAvatar;
        private bool _enableDebugLogs;

        public UnityEvent<int> OnAvatarConnected { get; } = new();
        public UnityEvent<int> OnClientDisconnected { get; } = new();

        public NetSyncAvatar LocalAvatar => _localAvatar;
        public IReadOnlyDictionary<int, GameObject> ConnectedPeers => _connectedPeers;

        public AvatarManager(bool enableDebugLogs)
        {
            _enableDebugLogs = enableDebugLogs;
        }

        public void InitializeLocalAvatar(GameObject localAvatarPrefab, string deviceId, NetSyncManager netSyncManager)
        {
            if (localAvatarPrefab == null)
            {
                Debug.Log("[NetSync] ***** Stealth mode enabled ***** - No local avatar will be spawned (LocalAvatarPrefab not set)");
                return;
            }

            // Instantiate a local avatar prefab asset
            GameObject localGO = null;
            if (localAvatarPrefab.scene.IsValid())
            {
                Debug.LogError("LocalAvatar Prefab should not be a scene object. Please use a prefab asset instead.");
            }
            else
            {
                // If XR Origin exists, instantiate the local avatar prefab as a child of the XR Origin
                var xrOrigin = Object.FindFirstObjectByType<Unity.XR.CoreUtils.XROrigin>();
                if (xrOrigin != null)
                {
                    localGO = Object.Instantiate(localAvatarPrefab, xrOrigin.transform);
                }
                else
                {
                    localGO = Object.Instantiate(localAvatarPrefab);
                }
            }

            _localAvatar = localGO.GetComponent<NetSyncAvatar>();
            if (_localAvatar == null)
            {
                Debug.LogError("LocalAvatar Prefab requires NetSyncAvatar component");
                return;
            }

            _localAvatar.Initialize(deviceId, true, netSyncManager);
            DebugLog($"Local avatar initialized with ID: {deviceId}");
        }

        public void SpawnRemoteAvatar(int clientNo, string deviceId, GameObject remoteAvatarPrefab, NetSyncManager netSyncManager)
        {
            if (remoteAvatarPrefab == null || _connectedPeers.ContainsKey(clientNo)) { return; }

            if (string.IsNullOrEmpty(deviceId))
            {
                Debug.LogError($"Cannot spawn remote avatar {clientNo} without device ID");
                return;
            }

            var go = Object.Instantiate(remoteAvatarPrefab);
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
            string prefabName = remoteAvatarPrefab.name;
            go.name = $"{prefabName} ({clientNo})";

            DebugLog($"Remote avatar spawned: clientNo={clientNo}, deviceId={deviceId}");
        }

        public void RemoveClient(int clientNo)
        {
            if (_connectedPeers.Remove(clientNo, out var go) && go)
            {
                Object.Destroy(go);
                DebugLog($"Remote avatar removed: clientNo={clientNo}");
            }
        }

        public void CleanupRemoteAvatars()
        {
            foreach (var go in _connectedPeers.Values) { if (go) { Object.Destroy(go); } }

            _connectedPeers.Clear();
            DebugLog("All remote avatars cleaned up");
        }

        public void UpdateRemoteAvatar(int clientNo, ClientTransformData transformData)
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
            if (_enableDebugLogs) { Debug.Log($"[AvatarManager] {msg}"); }
        }
    }
}