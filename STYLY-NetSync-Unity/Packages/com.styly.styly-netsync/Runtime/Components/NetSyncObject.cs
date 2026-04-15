// NetSyncObject.cs
// Authoring-time marker component for a replicated entity.
// Phase 1 scope: identity only — no networking logic.

using System;
using Styly.NetSync.Internal;
using UnityEngine;

namespace Styly.NetSync
{
    /// <summary>
    /// Attach to any GameObject that should participate in network
    /// replication. Holds a stable authored GUID and a replication profile.
    ///
    /// Network behavior (ownership, snapshot application, state emission)
    /// is added in later phases.
    /// </summary>
    [DisallowMultipleComponent]
    [AddComponentMenu("STYLY NetSync/NetSync Object")]
    public sealed class NetSyncObject : MonoBehaviour
    {
        // Stored as a string form so Unity's serializer handles it uniformly
        // across prefab overrides and scene diffs. Hidden from the default
        // inspector; a custom editor may expose it later.
        [SerializeField, HideInInspector]
        private string _guid;

        [SerializeField]
        private ReplicationProfile _profile = ReplicationProfile.Default;

        private ulong _cachedEntityId;
        private string _cachedGuidForEntityId;

        /// <summary>
        /// Raw GUID string as authored. Empty when not yet assigned. Do NOT
        /// mutate at runtime; EntityId is derived from this value.
        /// </summary>
        public string Guid => _guid;

        /// <summary>
        /// 64-bit network-facing identifier derived from <see cref="Guid"/>.
        /// Stable across processes and builds as long as the authored GUID
        /// does not change. See <see cref="EntityIdUtils.FromGuidString"/>
        /// for the derivation.
        /// </summary>
        public ulong EntityId
        {
            get
            {
                // Lazy-cache to avoid recomputing the hash every access.
                if (_cachedGuidForEntityId != _guid)
                {
                    _cachedEntityId = EntityIdUtils.FromGuidString(_guid);
                    _cachedGuidForEntityId = _guid;
                }
                return _cachedEntityId;
            }
        }

        /// <summary>
        /// Serialized replication profile. Read-only at runtime for Phase 1.
        /// </summary>
        public ReplicationProfile Profile => _profile;

        private void OnEnable()
        {
            EntityRegistry.Instance.Register(this);
        }

        private void OnDisable()
        {
            EntityRegistry.Instance.Unregister(this);
        }

#if UNITY_EDITOR
        private void OnValidate()
        {
            // Auto-assign a GUID at authoring time so designers don't have
            // to think about it. Runs in the editor only.
            if (string.IsNullOrEmpty(_guid))
            {
                _guid = System.Guid.NewGuid().ToString("D");
                UnityEditor.EditorUtility.SetDirty(this);
            }
            else if (!System.Guid.TryParse(_guid, out _))
            {
                // Malformed GUID — regenerate and warn.
                Debug.LogWarning($"[NetSync] NetSyncObject '{name}' had a malformed GUID; regenerating.", this);
                _guid = System.Guid.NewGuid().ToString("D");
                UnityEditor.EditorUtility.SetDirty(this);
            }
            // Invalidate the EntityId cache so a freshly-assigned GUID is
            // reflected on the next access.
            _cachedGuidForEntityId = null;
        }

        /// <summary>
        /// Editor-only: force-regenerate the GUID. Used by validators when
        /// resolving duplicates. Marks the object dirty.
        /// </summary>
        internal void RegenerateGuid_EditorOnly()
        {
            _guid = System.Guid.NewGuid().ToString("D");
            _cachedGuidForEntityId = null;
            UnityEditor.EditorUtility.SetDirty(this);
        }

        /// <summary>
        /// Editor-only accessor for the stored GUID field. Used by build
        /// validators and scene-hash tooling.
        /// </summary>
        internal string GuidForEditor => _guid;
#endif
    }
}
