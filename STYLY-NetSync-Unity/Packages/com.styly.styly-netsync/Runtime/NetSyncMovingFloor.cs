using Styly.NetSync.Internal;
using UnityEngine;

namespace Styly.NetSync
{
    /// <summary>
    /// User-facing moving floor for LBE platforms and vehicles.
    /// Adding this component registers the GameObject as a moving floor.
    /// Calling <see cref="BoardLocalAvatar"/> makes the local avatar send poses
    /// in this floor's local coordinates and optionally carries the local XR rig.
    /// </summary>
    [DisallowMultipleComponent]
    [DefaultExecutionOrder(5000)]
    public sealed class NetSyncMovingFloor : MonoBehaviour
    {
        [Header("Moving Floor")]
        [SerializeField, Tooltip("Stable floor id. The same id must refer to the corresponding moving floor on every client.")]
        private string _floorId = "";

        [Header("Local XR Rig")]
        [SerializeField, InspectorName("Local XR Rig Root"), Tooltip("Root transform carried by this moving floor while the local avatar is on it. Leave empty to auto-resolve the active XR rig.")]
        private Transform _localXRRigRoot;
        [SerializeField, InspectorName("Carry Local XR Rig While On Floor"), Tooltip("Move Local XR Rig Root with this moving floor while the local avatar is on it.")]
        private bool _carryLocalXRRigWhileOnFloor = true;

        // Previous floor pose tracked so the rig is carried by per-frame delta
        // rather than snapped to a cached absolute pose. This preserves any
        // user-driven XR rig motion (snap turn, locomotion, recenter) that
        // happens between platform updates.
        private Vector3 _previousFloorPosition;
        private Quaternion _previousFloorRotation = Quaternion.identity;
        private bool _hasPreviousFloorPose;
        private bool _isRegistered;
        private bool _isLocalAvatarOnFloor;

        public string FloorId => _floorId;

        public bool IsLocalAvatarOnFloor
        {
            get
            {
                var manager = NetSyncManager.Instance;
                if (manager != null)
                {
                    return manager.IsLocalAvatarOnMovingFloorId(_floorId);
                }

                return _isLocalAvatarOnFloor;
            }
        }

        public bool BoardLocalAvatar()
        {
            var manager = NetSyncManager.Instance;
            if (manager == null)
            {
                Debug.LogWarning("[NetSyncMovingFloor] NetSyncManager is not available.", this);
                _isLocalAvatarOnFloor = false;
                return false;
            }

            if (!TryRegister(true))
            {
                _isLocalAvatarOnFloor = false;
                return false;
            }

            if (!PrepareLocalXRRigCarry())
            {
                _isLocalAvatarOnFloor = false;
                return false;
            }

            _isLocalAvatarOnFloor = manager.BoardLocalAvatarOnMovingFloor(_floorId);
            if (!_isLocalAvatarOnFloor)
            {
                _hasPreviousFloorPose = false;
            }

            return _isLocalAvatarOnFloor;
        }

        public void LeaveLocalAvatar()
        {
            var manager = NetSyncManager.Instance;
            if (manager != null && manager.IsLocalAvatarOnMovingFloorId(_floorId))
            {
                manager.LeaveLocalAvatarFromMovingFloor();
            }

            _isLocalAvatarOnFloor = false;
            _hasPreviousFloorPose = false;
        }

        private void OnEnable()
        {
            TryRegister(false);
        }

        private void Start()
        {
            TryRegister(true);
        }

        private void Update()
        {
            // Keep retrying registration until NetSyncManager.Instance is online.
            // Components instantiated mid-game (or before the manager finishes
            // Awake/Start) would otherwise stay unregistered forever.
            if (!_isRegistered)
            {
                TryRegister(false);
            }
        }

        private void OnDisable()
        {
            var manager = NetSyncManager.Instance;
            if (manager != null && _isRegistered)
            {
                manager.UnregisterMovingFloor(_floorId);
            }

            _isRegistered = false;
            _isLocalAvatarOnFloor = false;
            _hasPreviousFloorPose = false;
        }

        private void LateUpdate()
        {
            if (!_carryLocalXRRigWhileOnFloor || !IsLocalAvatarOnFloor)
            {
                _hasPreviousFloorPose = false;
                return;
            }

            UpdateLocalXRRigRootPose();
        }

        private bool TryRegister(bool logWarning)
        {
            if (_isRegistered)
            {
                return true;
            }

            if (string.IsNullOrEmpty(_floorId))
            {
                if (logWarning)
                {
                    Debug.LogWarning("[NetSyncMovingFloor] Floor Id is required.", this);
                }
                return false;
            }

            var manager = NetSyncManager.Instance;
            if (manager == null)
            {
                if (logWarning)
                {
                    Debug.LogWarning("[NetSyncMovingFloor] NetSyncManager is not available.", this);
                }
                return false;
            }

            _isRegistered = manager.RegisterMovingFloor(_floorId, transform);
            if (!_isRegistered && logWarning)
            {
                Debug.LogWarning("[NetSyncMovingFloor] Failed to register moving floor '" + _floorId + "'.", this);
            }

            return _isRegistered;
        }

        private bool PrepareLocalXRRigCarry()
        {
            _hasPreviousFloorPose = false;
            if (!_carryLocalXRRigWhileOnFloor)
            {
                return true;
            }

            if (!EnsureLocalXRRigRoot())
            {
                return false;
            }

            // Seed the delta tracker with the current floor pose so the first
            // LateUpdate after boarding does not snap the rig by the floor's
            // accumulated motion.
            _previousFloorPosition = transform.position;
            _previousFloorRotation = transform.rotation;
            _hasPreviousFloorPose = true;
            return true;
        }

        private bool EnsureLocalXRRigRoot()
        {
            if (_localXRRigRoot != null)
            {
                return true;
            }

            var resolvedRoot = RigTransformResolver.TryResolve();
            if (resolvedRoot != null)
            {
                _localXRRigRoot = resolvedRoot;
            }

            if (_localXRRigRoot != null)
            {
                return true;
            }

            Debug.LogWarning(
                "[NetSyncMovingFloor] Local XR Rig Root is required when Carry Local XR Rig While On Floor is enabled, but it could not be resolved automatically.",
                this);
            return false;
        }

        private void UpdateLocalXRRigRootPose()
        {
            if (_localXRRigRoot == null)
            {
                return;
            }

            var currentFloorPosition = transform.position;
            var currentFloorRotation = transform.rotation;

            // Apply only the floor's per-frame delta on top of the rig's current
            // pose. Any user-driven rig motion this frame (snap turn, smooth
            // locomotion, recenter) is preserved instead of being overwritten
            // by a cached absolute snapshot from boarding time.
            if (_hasPreviousFloorPose)
            {
                var deltaRotation = currentFloorRotation * Quaternion.Inverse(_previousFloorRotation);
                var rigOffset = _localXRRigRoot.position - _previousFloorPosition;
                _localXRRigRoot.position = currentFloorPosition + (deltaRotation * rigOffset);
                _localXRRigRoot.rotation = deltaRotation * _localXRRigRoot.rotation;
            }

            _previousFloorPosition = currentFloorPosition;
            _previousFloorRotation = currentFloorRotation;
            _hasPreviousFloorPose = true;
        }
    }
}
