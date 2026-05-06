// NetSyncAvatar.cs
using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Events;
using Unity.XR.CoreUtils;
#if UNITY_EDITOR
using UnityEngine.XR;
#endif

namespace Styly.NetSync
{
    public class NetSyncAvatar : MonoBehaviour
    {
        [Header("Network Settings")]
        [SerializeField, ReadOnly, Tooltip("Runtime-only device identifier assigned by the server. Empty in Edit mode.")]
        private string _deviceId;
        [SerializeField, ReadOnly, Tooltip("Runtime-only client number assigned by the server. 0 until the client is assigned.")]
        private int _clientNo;

        [Header("Physical Transform Data")]
        [ReadOnly, Tooltip("Physical (real-world) position of this avatar relative to the XR rig. Updated every sync tick at runtime.")]
        public Vector3 PhysicalPosition;
        [ReadOnly, Tooltip("Physical (real-world) rotation of this avatar relative to the XR rig. Updated every sync tick at runtime.")]
        public Quaternion PhysicalRotation;

        [Header("Body Parts")]
        [Tooltip("Head transform (typically the XR camera). Synced as an absolute pose.")]
        public Transform _head;
        [Tooltip("Right hand transform (controller or tracked hand root). Synced relative to the head.")]
        public Transform _rightHand;
        [Tooltip("Left hand transform (controller or tracked hand root). Synced relative to the head.")]
        public Transform _leftHand;
        [Tooltip("Additional transforms synchronized in world space (e.g. held objects). Order must match across peers.")]
        public Transform[] _virtualTransforms;

        // Properties
        public string DeviceId => _deviceId;
        public int ClientNo => _clientNo;
        public bool IsLocalAvatar { get; private set; }

        // Reference to NetSyncManager
        private NetSyncManager _netSyncManager;

        // Transform applier for remote avatars (applies targets directly)
        private readonly NetSyncTransformApplier _transformApplier = new NetSyncTransformApplier();
        private readonly NetSyncSmoothingSettings _smoothingSettings = new NetSyncSmoothingSettings();

        // Event-group headers and per-event tooltips are rendered by
        // NetSyncAvatarEditor so UnityEventDrawer doesn't swallow them.
        // Initialize UnityEvents at declaration to ensure they are always non-null.
        [Tooltip("Fired when a client variable changes for this avatar's owner. Parameters: name (string), oldValue (string), newValue (string).")]
        public UnityEvent<string, string, string> OnClientVariableChanged = new UnityEvent<string, string, string>();

        /// <summary>
        /// Invoked when hand tracking is lost (true hand tracking loss, not controller switch).
        /// Parameter: Hand (Left or Right)
        /// </summary>
        [Tooltip("Fired when hand tracking is lost (true hand tracking loss, not controller switch). Parameter: hand (Hand) — Left or Right.")]
        public UnityEvent<Hand> OnHandTrackingLost = new UnityEvent<Hand>();

        /// <summary>
        /// Invoked when hand tracking is restored.
        /// Parameter: Hand (Left or Right)
        /// </summary>
        [Tooltip("Fired when hand tracking is restored. Parameter: hand (Hand) — Left or Right.")]
        public UnityEvent<Hand> OnHandTrackingRestored = new UnityEvent<Hand>();

        // --- Cached objects for zero-allocation transform packaging on send ---
        // These are reused every frame to avoid GC pressure from frequent network sends.
        private ClientTransformData _tx;
        private TransformData _txPhysical;
        private TransformData _txHead;
        private TransformData _txRight;
        private TransformData _txLeft;
        private readonly List<TransformData> _txVirtuals = new List<TransformData>(8);

        // Helper to ensure cached containers exist and are sized for current virtual transforms.
        private void EnsureTxBuffersAllocated()
        {
            if (_tx == null) _tx = new ClientTransformData();
            if (_txPhysical == null) _txPhysical = new TransformData();
            if (_txHead == null) _txHead = new TransformData();
            if (_txRight == null) _txRight = new TransformData();
            if (_txLeft == null) _txLeft = new TransformData();

            var vtLen = _virtualTransforms != null ? _virtualTransforms.Length : 0;

            // Grow list to match current number of virtual transforms.
            while (_txVirtuals.Count < vtLen)
            {
                _txVirtuals.Add(new TransformData());
            }
            // Shrink only if it became smaller; this is rare and prevents stale items from being serialized.
            if (_txVirtuals.Count > vtLen)
            {
                _txVirtuals.RemoveRange(vtLen, _txVirtuals.Count - vtLen);
            }
        }

        // Copy values into a cached TransformData without allocating.
        private static void Fill(TransformData td, Vector3 pos, Quaternion rot)
        {
            td.position = pos;
            td.rotation = rot;
        }

        void Start()
        {

        }

        void OnEnable()
        {
            // Subscribe to NetSyncManager's client variable change event
            if (NetSyncManager.Instance != null)
            {
                NetSyncManager.Instance.OnClientVariableChanged.AddListener(HandleClientVariableChanged);
            }
        }

        void OnDisable()
        {
            // Unsubscribe from NetSyncManager's client variable change event
            if (NetSyncManager.Instance != null)
            {
                NetSyncManager.Instance.OnClientVariableChanged.RemoveListener(HandleClientVariableChanged);
            }
        }

        // Initialization method called from NetSyncManager
        internal void Initialize(string deviceId, bool isLocalAvatar, NetSyncManager manager)
        {
            _deviceId = deviceId;
            IsLocalAvatar = isLocalAvatar;
            _netSyncManager = manager;

            if (isLocalAvatar)
            {
                // For local avatar, client number will be updated via NetSyncManager
                _clientNo = 0;
            }

            // Do not drive the same Transform in both local (physical) and world (head) spaces.
            // Passing null for physical avoids conflicting updates on _head.
            _transformApplier.InitializeForAvatar(
                null,
                _head,
                _rightHand,
                _leftHand,
                _virtualTransforms,
                _netSyncManager != null ? _netSyncManager.TimeEstimator : null,
                _smoothingSettings,
                _netSyncManager != null ? _netSyncManager.TransformSendRate : 10f);

            // Prepare reusable send buffers (after transforms are known).
            EnsureTxBuffersAllocated();
        }

        // Initialization method for remote avatars with known client number
        internal void InitializeRemote(int clientNo, NetSyncManager manager)
        {
            _clientNo = clientNo;
            _deviceId = null; // Will be set when ID mapping is received
            IsLocalAvatar = false;
            _netSyncManager = manager;

            // For remote avatars, avoid double-driving _head (physical/local vs head/world).
            _transformApplier.InitializeForAvatar(
                null,
                _head,
                _rightHand,
                _leftHand,
                _virtualTransforms,
                _netSyncManager != null ? _netSyncManager.TimeEstimator : null,
                _smoothingSettings,
                _netSyncManager != null ? _netSyncManager.TransformSendRate : 10f);

            // Prepare reusable send buffers (after transforms are known).
            EnsureTxBuffersAllocated();
        }

        void Update()
        {
            if (IsLocalAvatar && _netSyncManager != null)
            {
                // For local avatar, update client number display
                _clientNo = _netSyncManager.ClientNo;
            }

            if (!IsLocalAvatar)
            {
                // Use high-resolution clock for consistent time estimation
                Transform referenceFrame = null;
                if (_netSyncManager != null && _transformApplier.IsReferenceFrameLocal)
                {
                    _netSyncManager.TryGetReferenceFrameForClient(_clientNo, true, out referenceFrame);
                }
                _transformApplier.Tick(Time.deltaTime, NetSyncClock.NowSeconds(), referenceFrame);
            }

            if (IsLocalAvatar && _head != null)
            {
                // Local avatar: physical pose follows the head's parent-local pose plus
                // the cached startup XR-rig offset. No smoothing needed (driven by tracker).
                PhysicalPosition = _head.localPosition + _netSyncManager._physicalOffsetPosition;
                PhysicalRotation = _head.localRotation * Quaternion.Euler(_netSyncManager._physicalOffsetRotation);
            }
            else if (!IsLocalAvatar && _transformApplier.HasPhysicalSample)
            {
                // Remote avatar: read the smoothed physical sample produced by Tick so
                // PhysicalPosition is time-aligned with the head channel. Reading the raw
                // value set in SetTransformData would lag head's smoothing and cause the
                // `head.y - PhysicalPosition.y` ground-center derivation to wobble.
                var sample = _transformApplier.LastPhysicalSample;
                PhysicalPosition = sample.Position;
                PhysicalRotation = sample.Rotation;
                if (_transformApplier.IsReferenceFrameLocal && _transformApplier.LastTickApplied && _netSyncManager != null)
                {
                    _netSyncManager.UpdateBoundHumanPresenceFromAvatar(_clientNo, this, sample);
                }
            }
        }

#if UNITY_EDITOR
        private Camera _cachedEditorMainCamera;
        private Transform _cachedEditorMainCameraTransform;

        // Editor-only fallback: when no XR device is active, TrackedPoseDriver leaves
        // _head at (0,0,0). Return Camera.main.transform so the local avatar reflects
        // CameraYOffset. Returns null when an XR device (Meta Link, XR Device Simulator,
        // etc.) is active, so real tracked poses are never overwritten.
        private Transform GetCachedEditorMainCameraTransform()
        {
            if (_cachedEditorMainCamera == null)
            {
                _cachedEditorMainCamera = Camera.main;
                _cachedEditorMainCameraTransform = _cachedEditorMainCamera != null ? _cachedEditorMainCamera.transform : null;
            }

            return _cachedEditorMainCameraTransform;
        }

        private Transform TryGetEditorHeadOverride()
        {
            if (XRSettings.isDeviceActive) { return null; }
            return GetCachedEditorMainCameraTransform();
        }

        void LateUpdate()
        {
            if (!IsLocalAvatar || _head == null) { return; }
            var overrideTransform = TryGetEditorHeadOverride();
            if (overrideTransform != null)
            {
                _head.position = overrideTransform.position;
                _head.rotation = overrideTransform.rotation;
            }
        }
#endif

        // Get current transform data for sending
        internal ClientTransformData GetTransformData()
        {
            // Ensure buffers exist and sized (handles rare runtime changes to _virtualTransforms).
            EnsureTxBuffersAllocated();

            _tx.deviceId = _deviceId;
            _tx.clientNo = _clientNo;
            Transform referenceFrame = null;
            bool referenceFrameLocal = _netSyncManager != null && _netSyncManager.TryGetLocalReferenceFrame(out referenceFrame);
            _tx.flags = BuildPoseFlags(referenceFrameLocal);
            if (_netSyncManager != null)
            {
                _netSyncManager.ComputeXrOriginDelta(out var deltaPos, out var deltaYaw);
                _tx.xrOriginDeltaPosition = deltaPos;
                _tx.xrOriginDeltaYaw = deltaYaw;
            }
            else
            {
                _tx.xrOriginDeltaPosition = Vector3.zero;
                _tx.xrOriginDeltaYaw = 0f;
            }

            Fill(
                    _txPhysical,
                    PhysicalPosition,
                    PhysicalRotation);
            _tx.physical = _txPhysical;

            // World space transforms.
            // In the Editor without an active XR device, TrackedPoseDriver returns (0,0,0).
            // TryGetEditorHeadOverride() substitutes Camera.main.transform so the transmitted
            // head pose reflects CameraYOffset. When an XR device is active (Meta Link, XR
            // Device Simulator, on-device builds), _head is used directly.
            Transform headForSync = _head;
#if UNITY_EDITOR
            var overrideTransform = TryGetEditorHeadOverride();
            if (overrideTransform != null) { headForSync = overrideTransform; }
#endif
            FillFromTransform(_txHead, headForSync, referenceFrame, referenceFrameLocal);
            _tx.head = _txHead;

            FillFromTransform(_txRight, _rightHand, referenceFrame, referenceFrameLocal);
            _tx.rightHand = _txRight;

            FillFromTransform(_txLeft, _leftHand, referenceFrame, referenceFrameLocal);
            _tx.leftHand = _txLeft;

            // Virtuals: reuse pre-allocated TransformData instances.
            if (_virtualTransforms != null && _txVirtuals.Count == _virtualTransforms.Length)
            {
                for (int i = 0; i < _virtualTransforms.Length; i++)
                {
                    var t = _virtualTransforms[i];
                    var td = _txVirtuals[i];
                    FillFromTransform(td, t, referenceFrame, referenceFrameLocal);
                }
            }
            // If _virtualTransforms is null, make sure list is empty to avoid serializing stale entries.
            // EnsureTxBuffersAllocated already handled resizing, so just assign.
            _tx.virtuals = _txVirtuals;

            return _tx;
        }

        // Update device ID when mapping is received
        internal void UpdateDeviceId(string deviceId)
        {
            if (!IsLocalAvatar && !string.IsNullOrEmpty(deviceId))
            {
                _deviceId = deviceId;
            }
        }

        // Receive and apply transform data (for remote avatars)
        internal void SetTransformData(ClientTransformData data)
        {
            if (IsLocalAvatar) { return; }

            _transformApplier.AddSnapshot(data);

            PhysicalPosition = data.physical != null ? data.physical.GetPosition() : Vector3.zero;
            PhysicalRotation = data.physical != null ? data.physical.GetRotation() : Quaternion.identity;

            // Update client number for remote avatars
            _clientNo = data.clientNo;
        }

        // Get world transform data (world space, full 6DOF)
        internal TransformData GetWorldTransform(Transform transform)
        {
            if (transform == null) return new TransformData();
            return new TransformData(
                transform.position,
                transform.rotation
            );
        }

        // Get local transform data (local space relative to parent, full 6DOF)
        // Note: Do not use null propagation on UnityEngine.Object.
        internal TransformData GetLocalTransform(Transform transform)
        {
            if (transform == null) return new TransformData();
            return new TransformData(
                transform.localPosition,
                transform.localRotation
            );
        }

        // Convert transform array to TransformData list (world space)
        internal List<TransformData> GetWorldTransformList(Transform[] transforms)
        {
            var result = new List<TransformData>(transforms.Length);
            foreach (var t in transforms) result.Add(GetWorldTransform(t));
            return result;
        }

        private static void FillFromTransform(TransformData td, Transform t, Transform referenceFrame, bool referenceFrameLocal)
        {
            if (t == null)
            {
                Fill(td, Vector3.zero, Quaternion.identity);
                return;
            }

            if (referenceFrameLocal && referenceFrame != null)
            {
                var frameRotation = referenceFrame.rotation;
                Fill(
                    td,
                    referenceFrame.InverseTransformPoint(t.position),
                    Quaternion.Inverse(frameRotation) * t.rotation);
                return;
            }

            Fill(td, t.position, t.rotation);
        }

        private PoseFlags BuildPoseFlags(bool referenceFrameLocal)
        {
            var flags = PoseFlags.None;
            if (_netSyncManager != null && _netSyncManager.IsStealthMode)
            {
                flags |= PoseFlags.IsStealth;
                return flags;
            }

            bool hasHead = _head != null;
            if (hasHead) { flags |= PoseFlags.HeadValid; }
            if (hasHead && _rightHand != null) { flags |= PoseFlags.RightValid; }
            if (hasHead && _leftHand != null) { flags |= PoseFlags.LeftValid; }
            if (true) { flags |= PoseFlags.PhysicalValid; }
            if (hasHead && _virtualTransforms != null && _virtualTransforms.Length > 0)
            {
                flags |= PoseFlags.VirtualsValid;
            }
            if (referenceFrameLocal)
            {
                flags |= PoseFlags.ReferenceFrameLocal;
            }
            return flags;
        }

        // Handle client variable changes from NetSyncManager
        internal void HandleClientVariableChanged(int clientNo, string name, string oldValue, string newValue)
        {
            // Only invoke the event if the change is for this client
            if (clientNo == _clientNo)
            {
                if (OnClientVariableChanged != null)
                {
                    OnClientVariableChanged.Invoke(name, oldValue, newValue);
                }
            }
        }

        /// <summary>
        /// Called by AvatarManager when hand tracking state changes.
        /// This is only called for local avatars.
        /// Note: Head-relative position maintenance is handled by HandPoseNormalizer.
        /// </summary>
        internal void NotifyHandTrackingStateChanged(Hand hand, bool isTracking)
        {
            if (isTracking)
            {
                Debug.Log($"[NetSyncAvatar] {hand} hand tracking restored");
                OnHandTrackingRestored?.Invoke(hand);
            }
            else
            {
                Debug.Log($"[NetSyncAvatar] {hand} hand tracking lost");
                OnHandTrackingLost?.Invoke(hand);
            }
        }

        #region === Network Variables Convenience Methods ===
        /// <summary>
        /// Set a client variable for this NetSyncAvatar's owner
        /// </summary>
        public bool SetClientVariable(string name, string value)
        {
            return NetSyncManager.Instance != null ? NetSyncManager.Instance.SetClientVariable(name, value, _clientNo) : false;
        }

        /// <summary>
        /// Get a client variable for this NetSyncAvatar's owner
        /// </summary>
        public string GetClientVariable(string name, string defaultValue = null)
        {
            return NetSyncManager.Instance != null ? NetSyncManager.Instance.GetClientVariable(name, _clientNo, defaultValue) : defaultValue;
        }

        /// <summary>
        /// Set a client variable for a specific client
        /// </summary>
        public bool SetClientVariable(string name, string value, int targetClientNo)
        {
            return NetSyncManager.Instance != null ? NetSyncManager.Instance.SetClientVariable(name, value, targetClientNo) : false;
        }

        /// <summary>
        /// Get a client variable for a specific client
        /// </summary>
        public string GetClientVariable(string name, int clientNo, string defaultValue = null)
        {
            return NetSyncManager.Instance != null ? NetSyncManager.Instance.GetClientVariable(name, clientNo, defaultValue) : defaultValue;
        }
        #endregion
    }
}
