using Styly.NetSync.Internal;
using UnityEngine;

namespace Styly.NetSync
{
    /// <summary>
    /// User-facing ride frame for moving platforms and vehicles.
    /// Adding this component registers the GameObject as a reference frame.
    /// Calling <see cref="AttachLocalAvatar"/> makes the local avatar send poses
    /// in this frame's local coordinates and optionally carries the local rig.
    /// </summary>
    [DisallowMultipleComponent]
    [DefaultExecutionOrder(5000)]
    public sealed class NetSyncRideFrame : MonoBehaviour
    {
        [Header("Ride Frame")]
        [SerializeField, Tooltip("Stable frame id. The same id must refer to the corresponding moving platform on every client.")]
        private string _frameId = "";

        [Header("Local XR Rig")]
        [SerializeField, InspectorName("Local XR Rig Root"), Tooltip("Root transform carried by this ride frame while the local avatar is attached. Leave empty to auto-resolve the active XR rig.")]
        private Transform _localXRRigRoot;
        [SerializeField, InspectorName("Carry Local XR Rig While Attached"), Tooltip("Move Local XR Rig Root with this ride frame while the local avatar is attached.")]
        private bool _carryLocalXRRigWhileAttached = true;

        private Vector3 _localXRRigFrameLocalPosition;
        private Quaternion _localXRRigFrameLocalRotation = Quaternion.identity;
        private bool _hasLocalXRRigFramePose;
        private bool _isRegistered;
        private bool _isLocalAvatarAttached;

        public string FrameId => _frameId;

        public bool IsLocalAvatarAttached
        {
            get
            {
                var manager = NetSyncManager.Instance;
                if (manager != null)
                {
                    return manager.IsLocalAvatarAttachedToReferenceFrameId(_frameId);
                }

                return _isLocalAvatarAttached;
            }
        }

        public bool AttachLocalAvatar()
        {
            var manager = NetSyncManager.Instance;
            if (manager == null)
            {
                Debug.LogWarning("[NetSyncRideFrame] NetSyncManager is not available.", this);
                _isLocalAvatarAttached = false;
                return false;
            }

            if (!TryRegister(true))
            {
                _isLocalAvatarAttached = false;
                return false;
            }

            if (!CacheLocalXRRigFramePose())
            {
                _isLocalAvatarAttached = false;
                return false;
            }

            _isLocalAvatarAttached = manager.AttachLocalAvatarToReferenceFrame(_frameId);
            if (!_isLocalAvatarAttached)
            {
                _hasLocalXRRigFramePose = false;
            }

            return _isLocalAvatarAttached;
        }

        public void DetachLocalAvatar()
        {
            var manager = NetSyncManager.Instance;
            if (manager != null && manager.IsLocalAvatarAttachedToReferenceFrameId(_frameId))
            {
                manager.DetachLocalAvatarFromReferenceFrame();
            }

            _isLocalAvatarAttached = false;
            _hasLocalXRRigFramePose = false;
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
                manager.UnregisterReferenceFrame(_frameId);
            }

            _isRegistered = false;
            _isLocalAvatarAttached = false;
            _hasLocalXRRigFramePose = false;
        }

        private void LateUpdate()
        {
            if (!_carryLocalXRRigWhileAttached || !IsLocalAvatarAttached)
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

            if (string.IsNullOrEmpty(_frameId))
            {
                if (logWarning)
                {
                    Debug.LogWarning("[NetSyncRideFrame] Frame Id is required.", this);
                }
                return false;
            }

            var manager = NetSyncManager.Instance;
            if (manager == null)
            {
                if (logWarning)
                {
                    Debug.LogWarning("[NetSyncRideFrame] NetSyncManager is not available.", this);
                }
                return false;
            }

            _isRegistered = manager.RegisterReferenceFrame(_frameId, transform);
            if (!_isRegistered && logWarning)
            {
                Debug.LogWarning("[NetSyncRideFrame] Failed to register ride frame '" + _frameId + "'.", this);
            }

            return _isRegistered;
        }

        private bool CacheLocalXRRigFramePose()
        {
            _hasLocalXRRigFramePose = false;
            if (!_carryLocalXRRigWhileAttached)
            {
                return true;
            }

            if (!EnsureLocalXRRigRoot())
            {
                return false;
            }

            _localXRRigFrameLocalPosition = transform.InverseTransformPoint(_localXRRigRoot.position);
            _localXRRigFrameLocalRotation = Quaternion.Inverse(transform.rotation) * _localXRRigRoot.rotation;
            _hasLocalXRRigFramePose = true;
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
                "[NetSyncRideFrame] Local XR Rig Root is required when Carry Local XR Rig While Attached is enabled, but it could not be resolved automatically.",
                this);
            return false;
        }

        private void UpdateLocalXRRigRootPose()
        {
            if (!_hasLocalXRRigFramePose || _localXRRigRoot == null)
            {
                return;
            }

            _localXRRigRoot.position = transform.TransformPoint(_localXRRigFrameLocalPosition);
            _localXRRigRoot.rotation = transform.rotation * _localXRRigFrameLocalRotation;
        }
    }
}
