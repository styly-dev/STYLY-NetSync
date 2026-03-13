// NetSyncObject.cs - Synchronizes a GameObject's Transform across the network
using UnityEngine;
using UnityEngine.Events;

namespace Styly.NetSync
{
    public class NetSyncObject : MonoBehaviour
    {
        [SerializeField, Tooltip("Unique ID for this object within the room. Must be the same on all clients.")]
        private string _objectId;

        [SerializeField, Range(0.5f, 60f), Tooltip("Maximum send rate in Hz")]
        private float _sendRate = 10f;

        private int _ownerClientNo;
        private NetSyncTransformApplier _transformApplier;

        public string ObjectId => _objectId;
        public int OwnerClientNo => _ownerClientNo;
        public float SendRate => _sendRate;

        public bool IsOwnedByMe
        {
            get
            {
                var manager = NetSyncManager.Instance;
                if (manager == null) return false;
                return _ownerClientNo != 0 && _ownerClientNo == manager.ClientNo;
            }
        }

        public bool HasOwner => _ownerClientNo != 0;

        [Header("Events")]
        public UnityEvent<int, int> OnOwnershipChanged = new UnityEvent<int, int>();

        internal NetSyncTransformApplier TransformApplier => _transformApplier;

        public void Claim()
        {
            var manager = NetSyncManager.Instance;
            if (manager == null) return;
            manager.RequestObjectOwnership(0, _objectId);
        }

        public void Release()
        {
            var manager = NetSyncManager.Instance;
            if (manager == null) return;
            manager.RequestObjectOwnership(1, _objectId);
        }

        public void ForceClaim()
        {
            var manager = NetSyncManager.Instance;
            if (manager == null) return;
            manager.RequestObjectOwnership(2, _objectId);
        }

        internal void SetOwnerClientNoInternal(int ownerClientNo)
        {
            _ownerClientNo = ownerClientNo;
        }

        internal void InvokeOwnershipChanged(int newOwner, int previousOwner)
        {
            if (OnOwnershipChanged != null)
            {
                OnOwnershipChanged.Invoke(newOwner, previousOwner);
            }
        }

        private void OnEnable()
        {
            _transformApplier = new NetSyncTransformApplier();
            var manager = NetSyncManager.Instance;
            if (manager != null)
            {
                _transformApplier.InitializeForSingle(
                    transform,
                    NetSyncTransformApplier.SpaceMode.World,
                    manager.TimeEstimator,
                    null,
                    _sendRate);
                manager.RegisterNetSyncObject(this);
            }
        }

        private void OnDisable()
        {
            var manager = NetSyncManager.Instance;
            if (manager != null)
            {
                manager.UnregisterNetSyncObject(this);
            }
            _transformApplier = null;
        }
    }
}
