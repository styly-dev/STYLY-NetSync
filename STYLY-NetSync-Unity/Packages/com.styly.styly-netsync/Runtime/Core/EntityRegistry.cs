// EntityRegistry.cs
// Runtime registry mapping EntityId -> NetSyncObject binding.
// Populated by NetSyncObject.OnEnable / OnDisable and by a SceneManager
// load hook. All access is main-thread only.

using System.Collections.Generic;
using Styly.NetSync;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Styly.NetSync.Internal
{
    /// <summary>
    /// Binding kept for a single live NetSyncObject. For Phase 1 this is
    /// just a pointer to the component; later phases will attach snapshot,
    /// authority, and replication-state fields.
    /// </summary>
    public sealed class EntityBinding
    {
        public ulong EntityId;
        public NetSyncObject Component;
    }

    /// <summary>
    /// Process-wide singleton that tracks every active NetSyncObject by
    /// EntityId. Register/Unregister are called from the component's
    /// OnEnable/OnDisable. Fatal-logs on duplicate registration: duplicates
    /// should have been caught at author-time by NetSyncObjectIdValidator.
    /// </summary>
    public sealed class EntityRegistry
    {
        private static EntityRegistry _instance;
        private readonly Dictionary<ulong, EntityBinding> _byId = new Dictionary<ulong, EntityBinding>();
        private bool _sceneHookInstalled;

        public static EntityRegistry Instance
        {
            get
            {
                if (_instance == null)
                {
                    _instance = new EntityRegistry();
                    _instance.InstallSceneHook();
                }
                return _instance;
            }
        }

        private void InstallSceneHook()
        {
            if (_sceneHookInstalled)
            {
                return;
            }
            SceneManager.sceneLoaded += OnSceneLoaded;
            _sceneHookInstalled = true;
        }

        private void OnSceneLoaded(Scene scene, LoadSceneMode mode)
        {
            // Sweep the newly loaded scene to ensure any NetSyncObject whose
            // OnEnable was skipped (edge cases with DontDestroyOnLoad / pools)
            // gets registered here. NetSyncObject.Register is idempotent for
            // identical references and fatal-logs for conflicting EntityIds.
            GameObject[] roots = scene.GetRootGameObjects();
            for (int i = 0; i < roots.Length; i++)
            {
                GameObject root = roots[i];
                if (root == null)
                {
                    continue;
                }
                NetSyncObject[] objs = root.GetComponentsInChildren<NetSyncObject>(includeInactive: true);
                for (int j = 0; j < objs.Length; j++)
                {
                    NetSyncObject o = objs[j];
                    if (o != null && o.isActiveAndEnabled)
                    {
                        Register(o);
                    }
                }
            }
        }

        /// <summary>
        /// Register a NetSyncObject. Fatal-logs on a collision with a
        /// different live component.
        /// </summary>
        public void Register(NetSyncObject obj)
        {
            if (obj == null)
            {
                return;
            }
            ulong id = obj.EntityId;
            if (id == EntityIdUtils.Invalid)
            {
                Debug.LogError($"[NetSync] Refusing to register NetSyncObject '{obj.name}' with invalid EntityId (missing or malformed GUID).", obj);
                return;
            }

            if (_byId.TryGetValue(id, out EntityBinding existing))
            {
                if (existing.Component == obj)
                {
                    return;
                }
                // Duplicate: same EntityId on two different live components.
                Debug.LogError(
                    $"[NetSync] FATAL: duplicate EntityId 0x{id:X16} — '{obj.name}' conflicts with '{(existing.Component != null ? existing.Component.name : "<destroyed>")}'. Author-time validator should have caught this.",
                    obj);
                return;
            }

            _byId.Add(id, new EntityBinding { EntityId = id, Component = obj });
        }

        /// <summary>
        /// Unregister a NetSyncObject. Silently ignores objects that were
        /// never registered (e.g. because registration failed).
        /// </summary>
        public void Unregister(NetSyncObject obj)
        {
            if (obj == null)
            {
                return;
            }
            ulong id = obj.EntityId;
            if (id == EntityIdUtils.Invalid)
            {
                return;
            }
            if (_byId.TryGetValue(id, out EntityBinding existing) && existing.Component == obj)
            {
                _byId.Remove(id);
            }
        }

        /// <summary>
        /// Look up the binding for an EntityId. Returns false when no live
        /// NetSyncObject is registered under that id.
        /// </summary>
        public bool TryGet(ulong entityId, out EntityBinding binding)
        {
            return _byId.TryGetValue(entityId, out binding);
        }

        /// <summary>
        /// Current number of live registrations. Useful for diagnostics.
        /// </summary>
        public int Count => _byId.Count;
    }
}
