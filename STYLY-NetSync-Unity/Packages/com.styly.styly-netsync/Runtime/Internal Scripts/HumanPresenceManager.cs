// HumanPresenceManager.cs - Manages Human Presence Prefab lifecycle and transforms
using System.Collections.Generic;
using UnityEngine;

namespace Styly.NetSync
{
    /// <summary>
    /// Spawns and updates a simple visual presence per remote client at their physical pose.
    /// - Spawns under the corresponding Remote Avatar when available; otherwise at the hierarchy root
    /// - Hidden for local client
    /// - Updates directly from received data (no interpolation)
    /// - Ignores stealth clients (non-supported / not displayed)
    /// </summary>
    internal class HumanPresenceManager
    {
        private readonly Dictionary<int, GameObject> _presenceByClient = new Dictionary<int, GameObject>();
        private readonly Dictionary<int, NetSyncTransformApplier> _applierByClient = new Dictionary<int, NetSyncTransformApplier>();
        private readonly NetSyncManager _netSyncManager;
        private readonly bool _enableDebugLogs;
        private readonly NetSyncSmoothingSettings _smoothingSettings = new NetSyncSmoothingSettings();

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

            // Instantiate presence
            GameObject go = Object.Instantiate(prefab);
            if (go != null)
            {
                go.name = $"HumanPresence ({clientNo})";
                // Parent under remote avatar if available (preserve world space)
                var avatarManager = _netSyncManager.AvatarManager;
                if (avatarManager?.ConnectedPeers != null
                    && avatarManager.ConnectedPeers.TryGetValue(clientNo, out var avatarGo)
                    && avatarGo != null) { go.transform.SetParent(avatarGo.transform, true); }
                _presenceByClient[clientNo] = go;
                // Create and configure transform applier for this instance (world space)
                var applier = new NetSyncTransformApplier();
                applier.InitializeForSingle(
                    go.transform,
                    NetSyncTransformApplier.SpaceMode.World,
                    _netSyncManager != null ? _netSyncManager.TimeEstimator : null,
                    _smoothingSettings,
                    _netSyncManager != null ? _netSyncManager.TransformSendRate : 10f);
                _applierByClient[clientNo] = applier;
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

            if (_applierByClient.ContainsKey(clientNo))
            {
                _applierByClient.Remove(clientNo);
            }
        }

        /// <summary>
        /// Update transform for a remote client's presence.
        /// </summary>
        public void UpdateTransform(int clientNo, Vector3 position, Quaternion rotation, double poseTime, ushort poseSeq)
        {
            // Update transform target; applied directly in Tick()
            if (_applierByClient.TryGetValue(clientNo, out var applier))
            {
                if (applier != null)
                {
                    applier.AddSingleSnapshot(poseTime, poseSeq, position, rotation);
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
            _applierByClient.Clear();
            DebugLog("Cleared all Human Presence instances");
        }

        /// <summary>
        /// Per-frame update called from NetSyncManager.Update() to apply transforms.
        /// </summary>
        public void Tick()
        {
            if (_applierByClient.Count == 0) { return; }
            foreach (var kv in _applierByClient)
            {
                var applier = kv.Value;
                if (applier != null)
                {
                    applier.Tick(Time.deltaTime, Time.realtimeSinceStartupAsDouble);
                }
            }
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
