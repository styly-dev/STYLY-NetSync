// NetSyncManagerTestFactory.cs - Builds NetSyncManager instances for PlayMode tests.
// Uses the inactive-GameObject pattern so ConfigureForTests runs before Awake.
using UnityEngine;

namespace Styly.NetSync.Tests
{
    internal static class NetSyncManagerTestFactory
    {
        /// <summary>
        /// Create an offline-mode NetSyncManager. Caller must Destroy the returned
        /// GameObject and yield a frame in teardown so OnDisable clears the singleton.
        /// </summary>
        public static NetSyncManager CreateOffline(string roomId, GameObject localAvatarPrefab = null)
        {
            var go = new GameObject("NetSyncManager (offline test)");
            go.SetActive(false);
            var manager = go.AddComponent<NetSyncManager>();
            manager.ConfigureForTests(
                serverAddress: "127.0.0.1", controlPort: 0, transformPort: 0, subPort: 0,
                roomId: roomId, offlineMode: true);
            if (localAvatarPrefab != null)
            {
                manager.SetLocalAvatarPrefabForTests(localAvatarPrefab);
            }
            go.SetActive(true); // Awake -> OnEnable -> StartNetworking (offline)
            return manager;
        }

        /// <summary>
        /// Create a NetSyncManager pointed at a live server on the given ports.
        /// </summary>
        public static NetSyncManager CreateConnected(
            string serverAddress, int controlPort, int transformPort, int subPort,
            string roomId, GameObject localAvatarPrefab = null)
        {
            var go = new GameObject("NetSyncManager (integration test)");
            go.SetActive(false);
            var manager = go.AddComponent<NetSyncManager>();
            manager.ConfigureForTests(
                serverAddress, controlPort, transformPort, subPort, roomId, offlineMode: false);
            if (localAvatarPrefab != null)
            {
                manager.SetLocalAvatarPrefabForTests(localAvatarPrefab);
            }
            go.SetActive(true);
            return manager;
        }
    }
}
