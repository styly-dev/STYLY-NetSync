// AvatarManager.cs - Handles avatar spawning and lifecycle management
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.XR;
using UnityEngine.XR.Hands;
using UnityEngine.XR.Interaction.Toolkit.Inputs.Composites;
using Styly.NetSync.Internal;

namespace Styly.NetSync
{
    internal class AvatarManager
    {
        private readonly Dictionary<int, GameObject> _connectedPeers = new();
        // Cache of NetSyncAvatar components per remote client for hot paths.
        // Note: Do not use null propagation with UnityEngine.Object. Use explicit checks.
        private readonly Dictionary<int, NetSyncAvatar> _peerAvatars = new();
        private NetSyncAvatar _localAvatar;
        private bool _enableDebugLogs;

        // Hand pose normalizer references for tracking state events
        private HandPoseNormalizer _leftHandNormalizer;
        private HandPoseNormalizer _rightHandNormalizer;

        public UnityEvent<int> OnAvatarConnected { get; } = new();
        public UnityEvent<int> OnAvatarDisconnected { get; } = new();

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
                localGO = Object.Instantiate(localAvatarPrefab);

                // If XR Origin exists, use it as the parent using a ParentConstraint
                var xrOrigin = Object.FindFirstObjectByType<Unity.XR.CoreUtils.XROrigin>();
                if (xrOrigin != null)
                {
                    var parentConstraint = localGO.AddComponent<UnityEngine.Animations.ParentConstraint>();
                    var source = new UnityEngine.Animations.ConstraintSource { sourceTransform = xrOrigin.transform, weight = 1 };
                    parentConstraint.AddSource(source);
                    parentConstraint.constraintActive = true;
                }
            }

            _localAvatar = localGO.GetComponent<NetSyncAvatar>();
            if (_localAvatar == null)
            {
                Debug.LogError("LocalAvatar Prefab requires NetSyncAvatar component");
                return;
            }

            // Add TrackedPoseDriver component to the local avatar
            AddTrackedPoseDriverToLocalAvatar();

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
            _peerAvatars[clientNo] = net; // Cache component for hot path lookups

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
            // Ensure component cache is also cleared
            _peerAvatars.Remove(clientNo);
        }

        public void CleanupRemoteAvatars()
        {
            foreach (var go in _connectedPeers.Values) { if (go) { Object.Destroy(go); } }

            _connectedPeers.Clear();
            _peerAvatars.Clear();
            DebugLog("All remote avatars cleaned up");
        }

        public void UpdateRemoteAvatar(int clientNo, ClientTransformData transformData)
        {
            if (_peerAvatars.TryGetValue(clientNo, out var net) && net != null)
            {
                net.SetTransformData(transformData);
            }
        }

        /// <summary>
        /// Try to get a cached NetSyncAvatar component for a remote client.
        /// </summary>
        internal bool TryGetNetSyncAvatar(int clientNo, out NetSyncAvatar avatar)
        {
            var found = _peerAvatars.TryGetValue(clientNo, out avatar);
            if (!found) { return false; }
            // Explicit null check (UnityEngine.Object override)
            return avatar != null;
        }

        public HashSet<int> GetAliveClients(MessageProcessor messageProcessor = null, bool includeStealthClients = false, int localClientNo = 0)
        {
            var result = new HashSet<int>();

            if (includeStealthClients)
            {
                // When including stealth clients, prefer the authoritative mapping from MessageProcessor
                if (messageProcessor != null)
                {
                    var known = messageProcessor.GetKnownClientNosSnapshot();
                    if (known != null)
                    {
                        for (int i = 0; i < known.Length; i++)
                        {
                            var clientNo = known[i];
                            if (clientNo > 0)
                            {
                                result.Add(clientNo);
                            }
                        }
                    }
                }

                // Also include any peers we have spawned avatars for (defensive union)
                foreach (var clientNo in _connectedPeers.Keys)
                {
                    result.Add(clientNo);
                }
            }
            else
            {
                // Default behavior: exclude stealth clients
                if (messageProcessor == null)
                {
                    // Without a processor we cannot distinguish stealth; return visible avatars only
                    foreach (var clientNo in _connectedPeers.Keys)
                    {
                        result.Add(clientNo);
                    }
                }
                else
                {
                    foreach (var clientNo in _connectedPeers.Keys)
                    {
                        if (!messageProcessor.IsClientStealthMode(clientNo))
                        {
                            result.Add(clientNo);
                        }
                    }
                }
            }

            // Add local client if valid and meets stealth mode criteria
            if (localClientNo > 0)
            {
                if (includeStealthClients || messageProcessor == null || !messageProcessor.IsClientStealthMode(localClientNo))
                {
                    result.Add(localClientNo);
                }
            }

            return result;
        }

        private void AddTrackedPoseDriverToLocalAvatar()
        {
            if (!_localAvatar.gameObject.TryGetComponent<NetSyncAvatar>(out var netSyncAvatar)) return;

            // Ensure XRI fallback composites are registered (safe if called multiple times)
            InputSystem.RegisterBindingComposite<Vector3FallbackComposite>();
            InputSystem.RegisterBindingComposite<QuaternionFallbackComposite>();

            var head = netSyncAvatar._head;
            var rightHand = netSyncAvatar._rightHand;
            var leftHand = netSyncAvatar._leftHand;

            if (head != null)
            {
                var _tpd = head.gameObject.AddComponent<TrackedPoseDriver>();

                // --- Position (Vector3 Fallback) ---
                InputAction _positionAction = new(name: "Head Position", type: InputActionType.Value, expectedControlType: "Vector3");
                var pos = _positionAction.AddCompositeBinding("Vector3Fallback");
                pos.With("First", "<XRHMD>/centerEyePosition");
                pos.With("Second", "<XRHMD>/devicePosition");
                pos.With("Third", "<HandheldARInputDevice>/devicePosition");

                // --- Rotation (Quaternion Fallback) ---
                InputAction _rotationAction = new(name: "Head Rotation", type: InputActionType.Value, expectedControlType: "Quaternion");
                var rot = _rotationAction.AddCompositeBinding("QuaternionFallback");
                rot.With("First", "<XRHMD>/centerEyeRotation");
                rot.With("Second", "<XRHMD>/deviceRotation");
                rot.With("Third", "<HandheldARInputDevice>/deviceRotation");

                // Assign to Tracked Pose Driver
                _tpd.positionAction = _positionAction;
                _tpd.rotationAction = _rotationAction;
                _positionAction.Enable();
                _rotationAction.Enable();

            }

            if (rightHand != null)
            {
                var _tpd = rightHand.gameObject.AddComponent<TrackedPoseDriver>();

                // --- Position (Vector3 Fallback) ---
                InputAction _positionAction = new(name: "Right Position", type: InputActionType.Value, expectedControlType: "Vector3");
                var pos = _positionAction.AddCompositeBinding("Vector3Fallback");
                pos.With("First", "<XRController>{RightHand}/pointerPosition");
                pos.With("Second", "<XRController>{RightHand}/devicePosition");
                pos.With("Third", "<XRHandDevice>{RightHand}/devicePosition");

                // --- Rotation (Quaternion Fallback) ---
                InputAction _rotationAction = new(name: "Right Rotation", type: InputActionType.Value, expectedControlType: "Quaternion");
                var rot = _rotationAction.AddCompositeBinding("QuaternionFallback");
                rot.With("First", "<XRController>{RightHand}/pointerRotation");
                rot.With("Second", "<XRController>{RightHand}/deviceRotation");
                rot.With("Third", "<XRHandDevice>{RightHand}/deviceRotation");

                // Assign to Tracked Pose Driver
                _tpd.positionAction = _positionAction;
                _tpd.rotationAction = _rotationAction;
                _positionAction.Enable();
                _rotationAction.Enable();

                // Add hand pose normalizer for cross-platform compatibility
                var rightNormalizer = rightHand.gameObject.AddComponent<HandPoseNormalizer>();
                rightNormalizer.Handedness = Handedness.Right;
                rightNormalizer.EnableDebugLog = _enableDebugLogs;
                rightNormalizer.HeadTransform = head;

                // Subscribe to hand tracking state changes
                rightNormalizer.OnTrackingStateChanged += HandleHandTrackingStateChanged;
                _rightHandNormalizer = rightNormalizer;
            }

            if (leftHand != null)
            {
                var _tpd = leftHand.gameObject.AddComponent<TrackedPoseDriver>();

                // --- Position (Vector3 Fallback) ---
                InputAction _positionAction = new(name: "Left Position", type: InputActionType.Value, expectedControlType: "Vector3");
                var pos = _positionAction.AddCompositeBinding("Vector3Fallback");
                pos.With("First", "<XRController>{LeftHand}/pointerPosition");
                pos.With("Second", "<XRController>{LeftHand}/devicePosition");
                pos.With("Third", "<XRHandDevice>{LeftHand}/devicePosition");

                // --- Rotation (Quaternion Fallback) ---
                InputAction _rotationAction = new(name: "Left Rotation", type: InputActionType.Value, expectedControlType: "Quaternion");
                var rot = _rotationAction.AddCompositeBinding("QuaternionFallback");
                rot.With("First", "<XRController>{LeftHand}/pointerRotation");
                rot.With("Second", "<XRController>{LeftHand}/deviceRotation");
                rot.With("Third", "<XRHandDevice>{LeftHand}/deviceRotation");

                // Assign to Tracked Pose Driver
                _tpd.positionAction = _positionAction;
                _tpd.rotationAction = _rotationAction;
                _positionAction.Enable();
                _rotationAction.Enable();

                // Add hand pose normalizer for cross-platform compatibility
                var leftNormalizer = leftHand.gameObject.AddComponent<HandPoseNormalizer>();
                leftNormalizer.Handedness = Handedness.Left;
                leftNormalizer.EnableDebugLog = _enableDebugLogs;
                leftNormalizer.HeadTransform = head;

                // Subscribe to hand tracking state changes
                leftNormalizer.OnTrackingStateChanged += HandleHandTrackingStateChanged;
                _leftHandNormalizer = leftNormalizer;
            }
        }

        /// <summary>
        /// Handles hand tracking state changes from HandPoseNormalizer and forwards to NetSyncAvatar.
        /// </summary>
        private void HandleHandTrackingStateChanged(Handedness handedness, bool isTracking)
        {
            if (_localAvatar != null)
            {
                var hand = handedness == Handedness.Left ? Hand.Left : Hand.Right;
                _localAvatar.NotifyHandTrackingStateChanged(hand, isTracking);
            }
        }

        private void DebugLog(string msg)
        {
            if (_enableDebugLogs) { Debug.Log($"[AvatarManager] {msg}"); }
        }
    }
}
