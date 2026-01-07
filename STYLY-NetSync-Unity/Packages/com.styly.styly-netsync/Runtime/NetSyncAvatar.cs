// NetSyncAvatar.cs
using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Events;
using Unity.XR.CoreUtils;

namespace Styly.NetSync
{
    public class NetSyncAvatar : MonoBehaviour
    {
        [Header("Network Settings")]
        [SerializeField, ReadOnly] private string _deviceId;
        [SerializeField, ReadOnly] private int _clientNo;

        [Header("Physical Transform Data")]
        [ReadOnly] public Vector3 PhysicalPosition;
        [ReadOnly] public Vector3 PhysicalRotation;

        [Header("Body Parts")]
        public Transform _head;
        public Transform _rightHand;
        public Transform _leftHand;
        public Transform[] _virtualTransforms; // Object array to sync Virtual position (world coordinate system)

        // Properties
        public string DeviceId => _deviceId;
        public int ClientNo => _clientNo;
        public bool IsLocalAvatar { get; private set; }

        // Reference to NetSyncManager
        private NetSyncManager _netSyncManager;

        // Smoothing helper for remote avatars (shared with Human Presence)
        private readonly NetSyncTransformSmoother _smoother = new NetSyncTransformSmoother(0.1f);

        // Events
        [Header("Network Variable Events")]
        public UnityEvent<string, string, string> OnClientVariableChanged;

        [Header("Hand Tracking Events")]
        /// <summary>
        /// Invoked when hand tracking is lost (true hand tracking loss, not controller switch).
        /// Parameter: Hand (Left or Right)
        /// </summary>
        public UnityEvent<Hand> OnHandTrackingLost;

        /// <summary>
        /// Invoked when hand tracking is restored.
        /// Parameter: Hand (Left or Right)
        /// </summary>
        public UnityEvent<Hand> OnHandTrackingRestored;

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
        private static void Fill(TransformData td, Vector3 pos, Vector3 rot)
        {
            td.posX = pos.x; td.posY = pos.y; td.posZ = pos.z;
            td.rotX = rot.x; td.rotY = rot.y; td.rotZ = rot.z;
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
            _smoother.InitializeForAvatar(null, _head, _rightHand, _leftHand, _virtualTransforms);

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
            _smoother.InitializeForAvatar(null, _head, _rightHand, _leftHand, _virtualTransforms);

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
                _smoother.Update(Time.deltaTime);
            }

            // Reflect physical transform (local pose) only for the local avatar.
            // Remote avatars keep the last received physical values from SetTransformData.
            if (IsLocalAvatar && _head != null)
            {
                PhysicalPosition = _head.localPosition + _netSyncManager._physicalOffsetPosition;
                PhysicalRotation = _head.localEulerAngles + _netSyncManager._physicalOffsetRotation;
            }
        }

        // Get current transform data for sending
        internal ClientTransformData GetTransformData()
        {
            // Ensure buffers exist and sized (handles rare runtime changes to _virtualTransforms).
            EnsureTxBuffersAllocated();

            _tx.deviceId = _deviceId;
            _tx.clientNo = _clientNo;

            Fill(
                    _txPhysical,
                    PhysicalPosition,
                    PhysicalRotation);
            _tx.physical = _txPhysical;

            // World space transforms.
            Fill(_txHead,
                _head != null ? _head.position : Vector3.zero,
                _head != null ? _head.eulerAngles : Vector3.zero);
            _tx.head = _txHead;

            Fill(_txRight,
                _rightHand != null ? _rightHand.position : Vector3.zero,
                _rightHand != null ? _rightHand.eulerAngles : Vector3.zero);
            _tx.rightHand = _txRight;

            Fill(_txLeft,
                _leftHand != null ? _leftHand.position : Vector3.zero,
                _leftHand != null ? _leftHand.eulerAngles : Vector3.zero);
            _tx.leftHand = _txLeft;

            // Virtuals: reuse pre-allocated TransformData instances.
            if (_virtualTransforms != null && _txVirtuals.Count == _virtualTransforms.Length)
            {
                for (int i = 0; i < _virtualTransforms.Length; i++)
                {
                    var t = _virtualTransforms[i];
                    var td = _txVirtuals[i];
                    Fill(
                        td,
                        t != null ? t.position : Vector3.zero,
                        t != null ? t.eulerAngles : Vector3.zero);
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

            _smoother.SetTargets(data);

            PhysicalPosition = data.physical != null ? data.physical.GetPosition() : Vector3.zero;
            PhysicalRotation = data.physical != null ? data.physical.GetRotation() : Vector3.zero;

            // Update client number for remote avatars
            _clientNo = data.clientNo;
        }

        // Get world transform data (world space, full 6DOF)
        internal TransformData GetWorldTransform(Transform transform)
        {
            if (transform == null) return new TransformData();
            return new TransformData(
                transform.position,
                transform.eulerAngles
            );
        }

        // Get local transform data (local space relative to parent, full 6DOF)
        // Note: Do not use null propagation on UnityEngine.Object.
        internal TransformData GetLocalTransform(Transform transform)
        {
            if (transform == null) return new TransformData();
            return new TransformData(
                transform.localPosition,
                transform.localEulerAngles
            );
        }

        // Convert transform array to TransformData list (world space)
        internal List<TransformData> GetWorldTransformList(Transform[] transforms)
        {
            var result = new List<TransformData>(transforms.Length);
            foreach (var t in transforms) result.Add(GetWorldTransform(t));
            return result;
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
