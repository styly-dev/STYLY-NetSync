// NetSyncObject.cs - Synchronizes a GameObject's Transform across the network
using UnityEngine;
using UnityEngine.Events;

namespace Styly.NetSync
{
    public class NetSyncObject : MonoBehaviour
    {
        // Auto-assigned 32-bit ID derived from Unity's GlobalObjectId in the editor.
        // 0 means "unassigned" — the editor pipeline will fill it in on validate,
        // hierarchy change, or scene post-process.
        [SerializeField, HideInInspector]
        private uint _objectId;

        private int _ownerClientNo;
        private NetSyncTransformApplier _transformApplier;

        public uint ObjectId => _objectId;
        public int OwnerClientNo => _ownerClientNo;

        public bool IsOwnedByMe
        {
            get
            {
                var manager = NetSyncManager.Instance;
                if (manager == null) return false;
                return _ownerClientNo != 0 && _ownerClientNo == manager.ClientNo;
            }
        }

        [Header("Events")]
        public UnityEvent<int, int> OnOwnershipChanged = new UnityEvent<int, int>();

        internal NetSyncTransformApplier TransformApplier => _transformApplier;

        public void RequestOwnership()
        {
            if (!EnsureObjectIdAssigned()) return;
            var manager = NetSyncManager.Instance;
            if (manager == null) return;
            manager.RequestObjectOwnership(2, _objectId);
        }

        public void ReleaseOwnership()
        {
            if (!EnsureObjectIdAssigned()) return;
            var manager = NetSyncManager.Instance;
            if (manager == null) return;
            manager.RequestObjectOwnership(1, _objectId);
        }

        private bool EnsureObjectIdAssigned()
        {
            if (_objectId != 0u) return true;
            Debug.LogWarning(
                $"[NetSyncObject] '{name}' has no ObjectId assigned. Open the scene in the editor so the auto-assign pipeline can populate it.",
                this);
            return false;
        }

        internal void SetOwnerClientNoInternal(int ownerClientNo)
        {
            _ownerClientNo = ownerClientNo;
        }

        internal void InvokeOwnershipChanged(int newOwner, int previousOwner)
        {
            OnOwnershipChanged.Invoke(newOwner, previousOwner);
        }

#if UNITY_EDITOR
        // Editor-only accessor used by the auto-assign pipeline to read the
        // current persisted value. Writes go through SerializedProperty so Unity
        // records them correctly on prefab instances.
        internal uint ObjectIdEditorOnly => _objectId;

        private void OnValidate()
        {
            NetSyncObjectIdAssigner.RequestAssignForObject(this);
        }
#endif

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
                    manager.TransformSendRate);
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
