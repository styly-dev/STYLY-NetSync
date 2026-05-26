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

        private Vector3 _localXRRigFloorLocalPosition;
        private Quaternion _localXRRigFloorLocalRotation = Quaternion.identity;
        private bool _hasLocalXRRigFloorPose;
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

            if (!CacheLocalXRRigFloorPose())
            {
                _isLocalAvatarOnFloor = false;
                return false;
            }

            _isLocalAvatarOnFloor = manager.BoardLocalAvatarOnMovingFloor(_floorId);
            if (!_isLocalAvatarOnFloor)
            {
                _hasLocalXRRigFloorPose = false;
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
            _hasLocalXRRigFloorPose = false;
        }

        private void OnEnable()
        {
            TryRegister(false);
        }

        private void Start()
        {
            TryRegister(true);
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
            _hasLocalXRRigFloorPose = false;
        }

        private void LateUpdate()
        {
            if (!_carryLocalXRRigWhileOnFloor || !IsLocalAvatarOnFloor)
            {
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

        private bool CacheLocalXRRigFloorPose()
        {
            _hasLocalXRRigFloorPose = false;
            if (!_carryLocalXRRigWhileOnFloor)
            {
                return true;
            }

            if (!EnsureLocalXRRigRoot())
            {
                return false;
            }

            _localXRRigFloorLocalPosition = transform.InverseTransformPoint(_localXRRigRoot.position);
            _localXRRigFloorLocalRotation = Quaternion.Inverse(transform.rotation) * _localXRRigRoot.rotation;
            _hasLocalXRRigFloorPose = true;
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
            if (!_hasLocalXRRigFloorPose || _localXRRigRoot == null)
            {
                return;
            }

            _localXRRigRoot.position = transform.TransformPoint(_localXRRigFloorLocalPosition);
            _localXRRigRoot.rotation = transform.rotation * _localXRRigFloorLocalRotation;
        }
    }
}
