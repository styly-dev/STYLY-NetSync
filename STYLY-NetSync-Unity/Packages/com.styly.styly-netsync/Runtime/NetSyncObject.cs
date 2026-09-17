// NetSyncObject.cs - Synchronizes a GameObject's Transform across the network
using System;
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

        // When true, _objectId is user-specified and the auto-assign pipeline
        // must leave it alone. Enables matching the same logical entity across
        // separate scenes (e.g., a player scene and an admin console scene).
        [SerializeField, HideInInspector]
        private bool _manualObjectId;

        private int _ownerClientNo;
        private NetSyncTransformApplier _transformApplier;

        public uint ObjectId => _objectId;
        [Obsolete("ClientNo changes across reconnects; use OwnerDeviceId instead. See issue #393.")]
        public int OwnerClientNo => _ownerClientNo;

        /// <summary>
        /// The stable device ID of the current owner, or null when unowned or when the
        /// mapping is not currently known.
        /// </summary>
        public string OwnerDeviceId => ResolveDeviceId(_ownerClientNo);

        public bool IsOwnedByMe
        {
            get
            {
                var manager = NetSyncManager.Instance;
                if (manager == null) return false;
                return _ownerClientNo != 0 && _ownerClientNo == manager.ClientNo;
            }
        }

        // Tooltip and "Events" header are rendered by NetSyncObjectEditor so
        // that UnityEventDrawer doesn't swallow them.
        [Obsolete("ClientNo changes across reconnects; use OnOwnershipChangedByDeviceId instead. See issue #393.")]
        public UnityEvent<int, int> OnOwnershipChanged = new UnityEvent<int, int>();
        public UnityEvent<string, string> OnOwnershipChangedByDeviceId = new UnityEvent<string, string>();

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

        // Non-obsolete internal accessor for package-internal code (e.g. ObjectSyncManager)
        // that still needs to read the wire-level ClientNo without triggering CS0618.
        internal int OwnerClientNoInternal => _ownerClientNo;

        internal void SetOwnerClientNoInternal(int ownerClientNo)
        {
            _ownerClientNo = ownerClientNo;
        }

        internal void InvokeOwnershipChanged(int newOwner, int previousOwner)
        {
#pragma warning disable CS0618 // bridging to the obsolete ClientNo-based event
            OnOwnershipChanged.Invoke(newOwner, previousOwner);
#pragma warning restore CS0618

            OnOwnershipChangedByDeviceId.Invoke(ResolveDeviceId(newOwner), ResolveDeviceId(previousOwner));
        }

        // Returns null for 0 (no owner) or when the mapping is unknown.
        // NetSyncManager is a MonoBehaviour, so use Unity's null check instead of ?.
        private static string ResolveDeviceId(int clientNo)
        {
            var manager = NetSyncManager.Instance;
            return manager != null ? manager.GetDeviceIdByClientNo(clientNo) : null;
        }

#if UNITY_EDITOR
        // Editor-only accessor used by the auto-assign pipeline to read the
        // current persisted value. Writes go through SerializedProperty so Unity
        // records them correctly on prefab instances.
        internal uint ObjectIdEditorOnly => _objectId;
        internal bool IsManualObjectIdEditorOnly => _manualObjectId;

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
