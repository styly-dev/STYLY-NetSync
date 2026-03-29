// NetSyncObject.cs - High-frequency transform synchronization component for non-avatar objects
using System;
using UnityEngine;
using UnityEngine.Events;

namespace Styly.NetSync
{
    /// <summary>
    /// Attach to any scene-placed GameObject to synchronize its transform across the network.
    /// Supports configurable ownership (LastTouch / Explicit) and smooth interpolation for remote objects.
    /// </summary>
    public class NetSyncObject : MonoBehaviour
    {
        [Header("Network Settings")]
        [Tooltip("Unique object ID within the scene (1-1023). Must be unique across all NetSyncObjects.")]
        [SerializeField] private ushort _objectId;

        [Tooltip("Ownership mode: LastTouch = grab to own, Explicit = transfer via API only.")]
        [SerializeField] private ObjectOwnershipMode _ownershipMode = ObjectOwnershipMode.LastTouch;

        [Tooltip("Whether to sync in world space or local space.")]
        [SerializeField] private NetSyncTransformApplier.SpaceMode _syncSpace = NetSyncTransformApplier.SpaceMode.World;

        [Header("Events")]
        [Tooltip("Fired when ownership changes. Args: (oldOwnerClientNo, newOwnerClientNo)")]
        public UnityEvent<int, int> OnOwnershipChanged = new UnityEvent<int, int>();

        // Runtime state
        private int _ownerClientNo;
        private ushort _ownershipSeq;
        private NetSyncManager _netSyncManager;
        private NetSyncTransformApplier _applier;
        private bool _applierInitialized;

        /// <summary>
        /// Unique object ID for network identification.
        /// </summary>
        public ushort ObjectId => _objectId;

        /// <summary>
        /// Current owner's client number. 0 = unowned.
        /// </summary>
        public int OwnerClientNo => _ownerClientNo;

        /// <summary>
        /// Monotonically increasing sequence number for ownership conflict resolution.
        /// </summary>
        internal ushort OwnershipSeq => _ownershipSeq;

        /// <summary>
        /// True if the local client is the current owner.
        /// </summary>
        public bool IsLocallyOwned
        {
            get
            {
                var mgr = NetSyncManager.Instance;
                return mgr != null && _ownerClientNo > 0 && _ownerClientNo == mgr.ClientNo;
            }
        }

        /// <summary>
        /// True if no client currently owns this object.
        /// </summary>
        public bool IsUnowned => _ownerClientNo == 0;

        /// <summary>
        /// Configured ownership mode.
        /// </summary>
        public ObjectOwnershipMode OwnershipMode => _ownershipMode;

        /// <summary>
        /// Configured sync space.
        /// </summary>
        internal NetSyncTransformApplier.SpaceMode SyncSpace => _syncSpace;

        /// <summary>
        /// Request ownership of this object.
        /// In LastTouch mode, ownership is claimed immediately (optimistic).
        /// In Explicit mode, the current owner must transfer ownership instead.
        /// </summary>
        /// <returns>True if ownership was claimed or already owned locally.</returns>
        public bool RequestOwnership()
        {
            if (IsLocallyOwned) return true;

            var mgr = NetSyncManager.Instance;
            if (mgr == null || mgr.ClientNo <= 0) return false;

            if (_ownershipMode == ObjectOwnershipMode.Explicit && _ownerClientNo > 0)
            {
                // In Explicit mode, cannot claim if already owned by someone else
                return false;
            }

            var newSeq = (ushort)(_ownershipSeq + 1);
            SetOwnership(mgr.ClientNo, newSeq);
            mgr.SendObjectOwnershipChange(_objectId, (ushort)mgr.ClientNo, newSeq);
            return true;
        }

        /// <summary>
        /// Release ownership, setting the object to unowned.
        /// Only succeeds if the local client is the current owner.
        /// </summary>
        public void ReleaseOwnership()
        {
            if (!IsLocallyOwned) return;

            var mgr = NetSyncManager.Instance;
            if (mgr == null) return;

            var newSeq = (ushort)(_ownershipSeq + 1);
            SetOwnership(0, newSeq);
            mgr.SendObjectOwnershipChange(_objectId, 0, newSeq);
        }

        /// <summary>
        /// Transfer ownership to a specific client.
        /// Only succeeds if the local client is the current owner.
        /// </summary>
        public void TransferOwnership(int targetClientNo)
        {
            if (!IsLocallyOwned) return;
            if (targetClientNo <= 0) return;

            var mgr = NetSyncManager.Instance;
            if (mgr == null) return;

            var newSeq = (ushort)(_ownershipSeq + 1);
            SetOwnership(targetClientNo, newSeq);
            mgr.SendObjectOwnershipChange(_objectId, (ushort)targetClientNo, newSeq);
        }

        /// <summary>
        /// Apply an ownership change from the server broadcast.
        /// Updates ownerClientNo if the incoming data is authoritative.
        /// </summary>
        internal void ApplyOwnershipFromServer(ushort ownerClientNo)
        {
            if (_ownerClientNo != ownerClientNo)
            {
                // Server room broadcast acts as a fallback reconciliation path.
                // Accept if we have no local ownership (unowned or not locally owned).
                // This ensures late-joining clients converge on the correct owner.
                if (!IsLocallyOwned)
                {
                    SetOwnership(ownerClientNo, _ownershipSeq);
                }
            }
        }

        /// <summary>
        /// Apply an ownership change message (MSG_OBJECT_OWNER).
        /// Uses conflict resolution: higher seq wins; on tie, lower clientNo wins.
        /// </summary>
        internal void ApplyOwnershipChange(int newOwnerClientNo, ushort newSeq)
        {
            if (newSeq > _ownershipSeq)
            {
                SetOwnership(newOwnerClientNo, newSeq);
            }
            else if (newSeq == _ownershipSeq && newOwnerClientNo != _ownerClientNo)
            {
                // Conflict: same seq, different owner. Lower clientNo wins.
                if (newOwnerClientNo > 0 && (newOwnerClientNo < _ownerClientNo || _ownerClientNo == 0))
                {
                    SetOwnership(newOwnerClientNo, newSeq);
                }
                else if (IsLocallyOwned)
                {
                    // We win the conflict — re-assert with seq+1
                    var mgr = NetSyncManager.Instance;
                    if (mgr != null)
                    {
                        var reassertSeq = (ushort)(_ownershipSeq + 1);
                        _ownershipSeq = reassertSeq;
                        mgr.SendObjectOwnershipChange(_objectId, (ushort)_ownerClientNo, reassertSeq);
                    }
                }
            }
            // else newSeq < _ownershipSeq: stale update, ignore
        }

        /// <summary>
        /// Apply a remote transform snapshot for interpolation.
        /// Called by ObjectSyncManager when receiving MSG_ROOM_OBJECTS.
        /// </summary>
        internal void ApplyRemoteTransform(double poseTime, Vector3 position, Quaternion rotation)
        {
            EnsureApplierInitialized();
            if (_applier != null)
            {
                _applier.AddSingleSnapshot(poseTime, 0, position, rotation);
            }
        }

        private void SetOwnership(int newOwnerClientNo, ushort newSeq)
        {
            var oldOwner = _ownerClientNo;
            _ownerClientNo = newOwnerClientNo;
            _ownershipSeq = newSeq;

            if (oldOwner != newOwnerClientNo)
            {
                if (OnOwnershipChanged != null)
                {
                    OnOwnershipChanged.Invoke(oldOwner, newOwnerClientNo);
                }
            }
        }

        private void EnsureApplierInitialized()
        {
            if (_applierInitialized) return;
            _applierInitialized = true;

            var mgr = NetSyncManager.Instance;
            if (mgr == null) return;

            _applier = new NetSyncTransformApplier();
            _applier.InitializeForSingle(
                transform,
                _syncSpace,
                mgr.TimeEstimator,
                null, // Use default smoothing settings
                mgr.TransformSendRate);
        }

        private void OnEnable()
        {
            var mgr = NetSyncManager.Instance;
            if (mgr != null)
            {
                mgr.RegisterNetSyncObject(this);
            }
        }

        private void OnDisable()
        {
            var mgr = NetSyncManager.Instance;
            if (mgr != null)
            {
                mgr.UnregisterNetSyncObject(this);
            }
        }

        private void Update()
        {
            // For remote objects: apply interpolated transform
            if (!IsLocallyOwned && _applier != null)
            {
                _applier.Tick(Time.deltaTime, Time.timeAsDouble);
            }
        }

        private void OnValidate()
        {
            if (_objectId == 0)
            {
                Debug.LogWarning($"[NetSyncObject] ObjectId is 0 on '{gameObject.name}'. Assign a unique ID (1-1023).", this);
            }
        }
    }
}
