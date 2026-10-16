// AvatarLifecycleOfflineTests.cs - PlayMode tests for local-avatar / stealth-mode
// detection in offline mode. The positive local-avatar-spawn path needs a prefab
// asset and a rig, and is exercised by the sample scenes and server integration
// tests; here we lock the deterministic stealth-mode behavior.
using System.Collections;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace Styly.NetSync.Tests
{
    public class AvatarLifecycleOfflineTests
    {
        private NetSyncManager _manager;

        [UnityTearDown]
        public IEnumerator TearDown()
        {
            if (_manager != null)
            {
                Object.Destroy(_manager.gameObject);
                _manager = null;
            }
            yield return null;
        }

        [UnityTest]
        public IEnumerator NoLocalAvatarPrefab_EntersStealthModeWithoutLocalAvatar()
        {
            _manager = NetSyncManagerTestFactory.CreateOffline("offline_room");
            yield return PollWait.Until(() => _manager.IsReady, 5f, "IsReady");

            Assert.IsTrue(_manager.IsStealthMode, "no local avatar prefab => stealth mode");
            Assert.IsTrue(_manager.AvatarManager.LocalAvatar == null,
                "stealth mode must not spawn a local avatar");
        }

        [UnityTest]
        public IEnumerator OfflineMode_HasNoRemotePeers()
        {
            _manager = NetSyncManagerTestFactory.CreateOffline("offline_room");
            yield return PollWait.Until(() => _manager.IsReady, 5f, "IsReady");

            // Give a few frames for any (unexpected) spawns to occur.
            for (int i = 0; i < 5; i++) { yield return null; }

            Assert.AreEqual(0, _manager.AvatarManager.ConnectedPeers.Count,
                "offline mode has no remote peers");
        }
    }
}
