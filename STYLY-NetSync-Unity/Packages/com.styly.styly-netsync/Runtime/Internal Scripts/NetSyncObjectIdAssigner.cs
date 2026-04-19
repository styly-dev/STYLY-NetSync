// NetSyncObjectIdAssigner.cs - Editor-only pipeline that auto-assigns uint ObjectIds
// to NetSyncObject instances, derived from Unity's GlobalObjectId.
//
// Inspired by Unity Netcode for GameObjects' GlobalObjectIdHash approach. NGO
// itself uses XXHash32; we use FNV-1a because the hash only runs in the editor
// and the result is persisted, so cross-language agreement is not required.
//   - Hash the object's GlobalObjectId string to 32 bits (FNV-1a).
//   - Reserve 0 as "unassigned".
//   - Resolve collisions by linear probing (deterministic, since results are persisted).
//   - Trigger on OnValidate, EditorApplication.hierarchyChanged, and scene post-process.
#if UNITY_EDITOR
using System.Collections.Generic;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Styly.NetSync
{
    [InitializeOnLoad]
    internal static class NetSyncObjectIdAssigner
    {
        // Reserved id meaning "unassigned". Never used for a real object.
        internal const uint UnassignedId = 0u;

        // `delayCall` coalescing flag. OnValidate fires during scene loads and
        // deserialization, so we defer the actual reconciliation pass to keep
        // SerializedObject mutations out of those unsafe contexts.
        private static bool _passScheduled;

        static NetSyncObjectIdAssigner()
        {
            EditorApplication.hierarchyChanged += OnHierarchyChanged;
        }

        // Called from NetSyncObject.OnValidate. We defer the actual work because
        // OnValidate can fire during asset import / scene load, where mutating a
        // SerializedObject is unsafe.
        internal static void RequestAssignForObject(NetSyncObject target)
        {
            if (target == null) return;
            if (!IsEditorReadyForPass()) return;
            ScheduleDeferredPass();
        }

        private static void OnHierarchyChanged()
        {
            if (!IsEditorReadyForPass()) return;
            ScheduleDeferredPass();
        }

        private static void ScheduleDeferredPass()
        {
            if (_passScheduled) return;
            _passScheduled = true;
            EditorApplication.delayCall += RunDeferredPass;
        }

        private static void RunDeferredPass()
        {
            _passScheduled = false;
            if (!IsEditorReadyForPass()) return;
            AssignIdsAcrossLoadedScenes();
        }

        // Guard against running while Unity is in a state that cannot accept
        // SerializedObject writes: entering/exiting play mode, compiling scripts,
        // or updating the asset database (e.g., during asset import).
        private static bool IsEditorReadyForPass()
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode) return false;
            if (EditorApplication.isCompiling) return false;
            if (EditorApplication.isUpdating) return false;
            return true;
        }

        // Scan every loaded scene, then reconcile ObjectIds in a single pass so
        // cross-scene collisions are resolved.
        private static void AssignIdsAcrossLoadedScenes()
        {
            var targets = CollectLoadedNetSyncObjects();
            if (targets.Count == 0) return;

            ReconcileIds(targets);
        }

        // Invoked by IProcessSceneWithReport at build time to make sure the
        // scene being serialized into the player has fresh, conflict-free ids.
        internal static void AssignIdsForScene(Scene scene)
        {
            var targets = new List<NetSyncObject>();
            CollectFromScene(scene, targets);
            if (targets.Count == 0) return;
            ReconcileIds(targets);
        }

        private static List<NetSyncObject> CollectLoadedNetSyncObjects()
        {
            var list = new List<NetSyncObject>();
            int sceneCount = SceneManager.sceneCount;
            for (int i = 0; i < sceneCount; i++)
            {
                var scene = SceneManager.GetSceneAt(i);
                if (!scene.IsValid() || !scene.isLoaded) continue;
                CollectFromScene(scene, list);
            }
            // Prefab Mode lives in its own stage scene that is not returned by
            // SceneManager. Include it so prefab assets opened for edit also get
            // auto-assigned ids.
            var prefabStage = UnityEditor.SceneManagement.PrefabStageUtility.GetCurrentPrefabStage();
            if (prefabStage != null)
            {
                CollectFromScene(prefabStage.scene, list);
            }
            return list;
        }

        private static void CollectFromScene(Scene scene, List<NetSyncObject> into)
        {
            if (!scene.IsValid()) return;
            var roots = scene.GetRootGameObjects();
            for (int i = 0; i < roots.Length; i++)
            {
                var root = roots[i];
                if (root == null) continue;
                var found = root.GetComponentsInChildren<NetSyncObject>(includeInactive: true);
                for (int j = 0; j < found.Length; j++)
                {
                    if (found[j] != null) into.Add(found[j]);
                }
            }
        }

        private static void ReconcileIds(List<NetSyncObject> targets)
        {
            // Resolve GlobalObjectId strings once and sort deterministically so
            // collision resolution is stable across sessions.
            var entries = new List<(NetSyncObject obj, string globalId, uint baseHash)>(targets.Count);
            for (int i = 0; i < targets.Count; i++)
            {
                var t = targets[i];
                if (t == null) continue;
                var idStr = GlobalObjectId.GetGlobalObjectIdSlow(t).ToString();
                entries.Add((t, idStr, HashToNonZeroUint32(idStr)));
            }
            entries.Sort((a, b) => string.CompareOrdinal(a.globalId, b.globalId));

            var taken = new HashSet<uint>();
            var pendingChanges = new List<(NetSyncObject obj, uint newId)>();

            // Phase 1 (read-only): reserve ids from objects flagged as manual so
            // the auto pass below cannot clobber user-specified ids. A manual id
            // of 0 is invalid (reserved as "unassigned") and falls through to
            // Phase 2, which will probe a fresh value — the user can re-enter.
            foreach (var entry in entries)
            {
                if (!entry.obj.IsManualObjectIdEditorOnly) continue;
                var current = entry.obj.ObjectIdEditorOnly;
                if (current == UnassignedId)
                {
                    Debug.LogWarning(
                        $"[NetSyncObject] '{entry.obj.name}' has manual ObjectID enabled but ID is 0; falling back to auto until a non-zero ID is entered.",
                        entry.obj);
                    continue;
                }
                if (!taken.Add(current))
                {
                    // Another manual object in a loaded scene already claimed this id.
                    // Intentional cross-scene matching is the feature's purpose, so we
                    // do not rewrite — just surface the collision for the author.
                    Debug.LogWarning(
                        $"[NetSyncObject] Manual ObjectID 0x{current:X8} on '{entry.obj.name}' collides with another manual ID in a loaded scene.",
                        entry.obj);
                }
            }

            // Phase 2 (write): run the existing reconcile logic on non-manual
            // entries. Any auto object whose persisted id clashes with a manual
            // reservation gets re-probed to a free slot.
            foreach (var entry in entries)
            {
                if (entry.obj.IsManualObjectIdEditorOnly &&
                    entry.obj.ObjectIdEditorOnly != UnassignedId) continue;

                var current = entry.obj.ObjectIdEditorOnly;
                uint resolved;

                if (current == UnassignedId)
                {
                    resolved = ProbeForFreeValue(entry.baseHash, taken);
                }
                else if (taken.Contains(current))
                {
                    // Collision with an object that was already claimed; re-probe
                    // from this object's base hash so the result is as close to
                    // the natural hash as possible.
                    resolved = ProbeForFreeValue(entry.baseHash, taken);
                }
                else
                {
                    // Keep the existing value — stable, avoids churning scene files
                    // when the natural hash disagrees because of an earlier probe.
                    resolved = current;
                }

                taken.Add(resolved);
                if (resolved != current)
                {
                    pendingChanges.Add((entry.obj, resolved));
                }
            }

            if (pendingChanges.Count == 0) return;

            // Group writes per-scene so we mark exactly the scenes that changed dirty.
            var dirtyScenes = new HashSet<Scene>();
            foreach (var (obj, newId) in pendingChanges)
            {
                if (obj == null) continue;
                var so = new SerializedObject(obj);
                var prop = so.FindProperty("_objectId");
                if (prop == null) continue;
                // SerializedProperty exposes uint via longValue (signed-long storage).
                prop.longValue = newId;
                so.ApplyModifiedPropertiesWithoutUndo();
                dirtyScenes.Add(obj.gameObject.scene);
            }

            foreach (var scene in dirtyScenes)
            {
                if (!scene.IsValid()) continue;
                EditorSceneManager.MarkSceneDirty(scene);
            }
        }

        private static uint ProbeForFreeValue(uint seed, HashSet<uint> taken)
        {
            uint v = seed == UnassignedId ? 1u : seed;
            // Bounded loop as a safety net; a 32-bit space is effectively unbounded
            // for realistic scene sizes, but this guards against accidental infinite
            // loops if `taken` grows unexpectedly.
            for (int i = 0; i < int.MaxValue; i++)
            {
                if (!taken.Contains(v)) return v;
                unchecked { v++; }
                if (v == UnassignedId) v = 1u;
            }
            return v;
        }

        // FNV-1a 32-bit over UTF-16 code units. Deterministic and dependency-free.
        // Runs only in the editor, so cross-language agreement is not required.
        private static uint HashToNonZeroUint32(string s)
        {
            const uint offset = 2166136261u;
            const uint prime = 16777619u;
            uint h = offset;
            if (!string.IsNullOrEmpty(s))
            {
                for (int i = 0; i < s.Length; i++)
                {
                    uint c = s[i];
                    h ^= c & 0xFFu;
                    h *= prime;
                    h ^= (c >> 8) & 0xFFu;
                    h *= prime;
                }
            }
            return h == UnassignedId ? 1u : h;
        }

        // Scene post-process hook: Unity invokes this during both "Play" and
        // "Build" for every scene being baked, so we get a last-chance pass that
        // cannot be skipped even if the user saved the scene without triggering
        // hierarchyChanged.
        private sealed class ScenePostProcessor : IProcessSceneWithReport
        {
            public int callbackOrder => 0;

            public void OnProcessScene(Scene scene, BuildReport report)
            {
                // BuildPipeline.isBuildingPlayer is true only during a real build;
                // during enter-play-mode scene processing we skip because the
                // editor pipeline already handled it.
                if (!BuildPipeline.isBuildingPlayer) return;
                AssignIdsForScene(scene);
            }
        }
    }
}
#endif
