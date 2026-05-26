// NetSyncMovingFloorIdAssigner.cs - Editor-only pipeline that auto-assigns
// uint FloorIds to NetSyncMovingFloor instances, derived from Unity's
// GlobalObjectId.
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
    internal static class NetSyncMovingFloorIdAssigner
    {
        internal const uint UnassignedId = MovingFloorManager.UnassignedFloorId;

        private static bool _passScheduled;

        static NetSyncMovingFloorIdAssigner()
        {
            EditorApplication.hierarchyChanged += OnHierarchyChanged;
        }

        internal static void RequestAssignForFloor(NetSyncMovingFloor target)
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

        private static bool IsEditorReadyForPass()
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode) return false;
            if (EditorApplication.isCompiling) return false;
            if (EditorApplication.isUpdating) return false;
            return true;
        }

        private static void AssignIdsAcrossLoadedScenes()
        {
            var targets = CollectLoadedMovingFloors();
            if (targets.Count == 0) return;

            ReconcileIds(targets);
        }

        internal static void AssignIdsForScene(Scene scene)
        {
            var targets = new List<NetSyncMovingFloor>();
            CollectFromScene(scene, targets);
            if (targets.Count == 0) return;
            ReconcileIds(targets);
        }

        private static List<NetSyncMovingFloor> CollectLoadedMovingFloors()
        {
            var list = new List<NetSyncMovingFloor>();
            int sceneCount = SceneManager.sceneCount;
            for (int i = 0; i < sceneCount; i++)
            {
                var scene = SceneManager.GetSceneAt(i);
                if (!scene.IsValid() || !scene.isLoaded) continue;
                CollectFromScene(scene, list);
            }

            var prefabStage = PrefabStageUtility.GetCurrentPrefabStage();
            if (prefabStage != null)
            {
                CollectFromScene(prefabStage.scene, list);
            }

            return list;
        }

        private static void CollectFromScene(Scene scene, List<NetSyncMovingFloor> into)
        {
            if (!scene.IsValid()) return;
            var roots = scene.GetRootGameObjects();
            for (int i = 0; i < roots.Length; i++)
            {
                var root = roots[i];
                if (root == null) continue;
                var found = root.GetComponentsInChildren<NetSyncMovingFloor>(includeInactive: true);
                for (int j = 0; j < found.Length; j++)
                {
                    if (found[j] != null) into.Add(found[j]);
                }
            }
        }

        private static void ReconcileIds(List<NetSyncMovingFloor> targets)
        {
            var entries = new List<(NetSyncMovingFloor floor, string globalId, uint baseHash)>(targets.Count);
            for (int i = 0; i < targets.Count; i++)
            {
                var t = targets[i];
                if (t == null) continue;
                var idStr = GlobalObjectId.GetGlobalObjectIdSlow(t).ToString();
                entries.Add((t, idStr, HashToNonZeroUint32(idStr)));
            }
            entries.Sort((a, b) => string.CompareOrdinal(a.globalId, b.globalId));

            var taken = new HashSet<uint>();
            var pendingChanges = new List<(NetSyncMovingFloor floor, uint newId)>();

            foreach (var entry in entries)
            {
                if (!entry.floor.IsManualFloorIdEditorOnly) continue;
                var current = entry.floor.FloorIdEditorOnly;
                if (current == UnassignedId)
                {
                    Debug.LogWarning(
                        $"[NetSyncMovingFloor] '{entry.floor.name}' has manual Floor ID enabled but ID is 0; falling back to auto until a non-zero ID is entered.",
                        entry.floor);
                    continue;
                }
                if (!taken.Add(current))
                {
                    Debug.LogWarning(
                        $"[NetSyncMovingFloor] Manual Floor ID {MovingFloorManager.FormatFloorId(current)} on '{entry.floor.name}' collides with another manual ID in a loaded scene.",
                        entry.floor);
                }
            }

            foreach (var entry in entries)
            {
                if (entry.floor.IsManualFloorIdEditorOnly &&
                    entry.floor.FloorIdEditorOnly != UnassignedId) continue;

                var current = entry.floor.FloorIdEditorOnly;
                uint resolved;

                if (current == UnassignedId)
                {
                    resolved = ProbeForFreeValue(entry.baseHash, taken);
                }
                else if (taken.Contains(current))
                {
                    resolved = ProbeForFreeValue(entry.baseHash, taken);
                }
                else
                {
                    resolved = current;
                }

                taken.Add(resolved);
                if (resolved != current)
                {
                    pendingChanges.Add((entry.floor, resolved));
                }
            }

            if (pendingChanges.Count == 0) return;

            var dirtyScenes = new HashSet<Scene>();
            foreach (var (floor, newId) in pendingChanges)
            {
                if (floor == null) continue;
                var so = new SerializedObject(floor);
                var prop = so.FindProperty("_floorId");
                if (prop == null) continue;
                prop.longValue = newId;
                so.ApplyModifiedPropertiesWithoutUndo();
                dirtyScenes.Add(floor.gameObject.scene);
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
            for (int i = 0; i < int.MaxValue; i++)
            {
                if (!taken.Contains(v)) return v;
                unchecked { v++; }
                if (v == UnassignedId) v = 1u;
            }
            return v;
        }

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

        private sealed class ScenePostProcessor : IProcessSceneWithReport
        {
            public int callbackOrder => 0;

            public void OnProcessScene(Scene scene, BuildReport report)
            {
                if (!BuildPipeline.isBuildingPlayer) return;
                AssignIdsForScene(scene);
            }
        }
    }
}
#endif
