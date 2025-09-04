// HumanPresenceManager.cs - Manages Human Presence Prefab lifecycle and transforms
using System.Collections.Generic;
using UnityEngine;

namespace Styly.NetSync
{
    /// <summary>
    /// Spawns and updates a simple visual presence per remote client at their physical pose.
    /// - Spawns at hierarchy root
    /// - Hidden for local client
    /// - No smoothing; updates directly from received data
    /// - Ignores stealth clients (non-supported / not displayed)
    /// </summary>
    internal class HumanPresenceManager
    {
        private readonly Dictionary<int, GameObject> _presenceByClient = new Dictionary<int, GameObject>();
        private readonly NetSyncManager _netSyncManager;
        private readonly bool _enableDebugLogs;

        public HumanPresenceManager(NetSyncManager netSyncManager, bool enableDebugLogs)
        {
            _netSyncManager = netSyncManager;
            _enableDebugLogs = enableDebugLogs;
        }

        /// <summary>
        /// Called when a remote avatar connects. Spawns a presence if possible.
        /// </summary>
        public void HandleAvatarConnected(int clientNo)
        {
            if (_netSyncManager == null) { return; }

            // Do not show local user
            if (clientNo == _netSyncManager.ClientNo) { return; }

            // Ignore stealth clients (not displayed)
            if (_netSyncManager.IsClientStealthMode(clientNo)) { return; }

            if (_presenceByClient.ContainsKey(clientNo)) { return; }

            var prefab = _netSyncManager.GetHumanPresencePrefab();
            if (prefab == null)
            {
                // No prefab set → feature disabled
                return;
            }

            // Instantiate at hierarchy root
            GameObject go = Object.Instantiate(prefab);
            if (go != null)
            {
                go.name = $"HumanPresence ({clientNo})";
                _presenceByClient[clientNo] = go;
                DebugLog($"Spawned Human Presence for client {clientNo}");
            }
        }

        /// <summary>
        /// Called when a remote avatar disconnects. Destroys the presence if it exists.
        /// </summary>
        public void HandleAvatarDisconnected(int clientNo)
        {
            if (_presenceByClient.TryGetValue(clientNo, out var go))
            {
                if (go != null)
                {
                    Object.Destroy(go);
                }
                _presenceByClient.Remove(clientNo);
                DebugLog($"Destroyed Human Presence for client {clientNo}");
            }
        }

        /// <summary>
        /// Update transform for a remote client's presence.
        /// </summary>
        public void UpdateTransform(int clientNo, Vector3 position, Vector3 eulerRotation)
        {
            if (_presenceByClient.TryGetValue(clientNo, out var go))
            {
                if (go != null)
                {
                    go.transform.position = position;
                    go.transform.eulerAngles = eulerRotation;
                }
            }
        }

        /// <summary>
        /// Destroy all presence instances (used on room switch/disconnect).
        /// </summary>
        public void CleanupAll()
        {
            if (_presenceByClient.Count == 0) { return; }
            foreach (var kv in _presenceByClient)
            {
                var go = kv.Value;
                if (go != null)
                {
                    Object.Destroy(go);
                }
            }
            _presenceByClient.Clear();
            DebugLog("Cleared all Human Presence instances");
        }

        private void DebugLog(string msg)
        {
            if (_enableDebugLogs)
            {
                Debug.Log($"[HumanPresence] {msg}");
            }
        }
    }
}

