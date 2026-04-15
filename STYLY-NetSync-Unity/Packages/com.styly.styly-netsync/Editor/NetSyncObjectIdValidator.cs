// NetSyncObjectIdValidator.cs
// Editor utilities that detect duplicate / missing NetSyncObject GUIDs
// across loaded scenes and offer fixes.

using System.Collections.Generic;
using Styly.NetSync;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Styly.NetSync.Internal.EditorTools
{
    public static class NetSyncObjectIdValidator
    {
        private const string MenuValidate = "Tools/STYLY NetSync/Validate Scene GUIDs";
        private const string MenuFix = "Tools/STYLY NetSync/Fix Duplicate Scene GUIDs";

        /// <summary>
        /// Collect all NetSyncObject instances across every loaded scene,
        /// including inactive ones.
        /// </summary>
        public static List<NetSyncObject> CollectAllInLoadedScenes()
        {
            List<NetSyncObject> all = new List<NetSyncObject>();
            int sceneCount = SceneManager.sceneCount;
            for (int i = 0; i < sceneCount; i++)
            {
                Scene scene = SceneManager.GetSceneAt(i);
                if (!scene.isLoaded)
                {
                    continue;
                }
                GameObject[] roots = scene.GetRootGameObjects();
                for (int r = 0; r < roots.Length; r++)
                {
                    GameObject root = roots[r];
                    if (root == null)
                    {
                        continue;
                    }
                    all.AddRange(root.GetComponentsInChildren<NetSyncObject>(includeInactive: true));
                }
            }
            return all;
        }

        /// <summary>
        /// Build a map from GUID -> components with that GUID across all
        /// loaded scenes. Components with empty/malformed GUIDs are returned
        /// separately in <paramref name="missing"/>.
        /// </summary>
        public static Dictionary<string, List<NetSyncObject>> GroupByGuid(
            List<NetSyncObject> all, out List<NetSyncObject> missing)
        {
            Dictionary<string, List<NetSyncObject>> byGuid = new Dictionary<string, List<NetSyncObject>>();
            missing = new List<NetSyncObject>();
            for (int i = 0; i < all.Count; i++)
            {
                NetSyncObject obj = all[i];
                if (obj == null)
                {
                    continue;
                }
                string guid = obj.GuidForEditor;
                if (string.IsNullOrEmpty(guid) || !System.Guid.TryParse(guid, out _))
                {
                    missing.Add(obj);
                    continue;
                }
                if (!byGuid.TryGetValue(guid, out List<NetSyncObject> list))
                {
                    list = new List<NetSyncObject>();
                    byGuid[guid] = list;
                }
                list.Add(obj);
            }
            return byGuid;
        }

        [MenuItem(MenuValidate)]
        public static void ValidateMenu()
        {
            int duplicates;
            int missingCount;
            bool ok = Validate(logMissing: true, logDuplicates: true, out duplicates, out missingCount);
            if (ok)
            {
                Debug.Log("[NetSync] Scene GUID validation passed.");
            }
            else
            {
                Debug.LogError($"[NetSync] Validation failed: {duplicates} duplicate group(s), {missingCount} missing GUID(s). Run '{MenuFix}' to auto-resolve.");
            }
        }

        [MenuItem(MenuFix)]
        public static void FixMenu()
        {
            int fixedCount = FixAll();
            Debug.Log($"[NetSync] Fixed {fixedCount} NetSyncObject GUID assignment(s).");
            if (fixedCount > 0)
            {
                EditorSceneManager.MarkAllScenesDirty();
            }
        }

        /// <summary>
        /// Returns true when all loaded scenes are clean.
        /// </summary>
        public static bool Validate(bool logMissing, bool logDuplicates, out int duplicates, out int missingCount)
        {
            List<NetSyncObject> all = CollectAllInLoadedScenes();
            Dictionary<string, List<NetSyncObject>> byGuid = GroupByGuid(all, out List<NetSyncObject> missing);

            duplicates = 0;
            foreach (KeyValuePair<string, List<NetSyncObject>> kv in byGuid)
            {
                if (kv.Value.Count > 1)
                {
                    duplicates++;
                    if (logDuplicates)
                    {
                        string names = string.Join(", ", kv.Value.ConvertAll(o => o != null ? o.name : "<null>"));
                        Debug.LogError($"[NetSync] Duplicate GUID {kv.Key} on objects: {names}");
                    }
                }
            }

            missingCount = missing.Count;
            if (logMissing)
            {
                for (int i = 0; i < missing.Count; i++)
                {
                    NetSyncObject o = missing[i];
                    if (o != null)
                    {
                        Debug.LogError($"[NetSync] NetSyncObject '{o.name}' has missing/malformed GUID.", o);
                    }
                }
            }

            return duplicates == 0 && missingCount == 0;
        }

        /// <summary>
        /// Regenerate missing GUIDs, and for any duplicate group keep the
        /// first occurrence and regenerate the rest. Returns the number of
        /// components whose GUID was changed.
        /// </summary>
        public static int FixAll()
        {
            int fixedCount = 0;
            List<NetSyncObject> all = CollectAllInLoadedScenes();
            Dictionary<string, List<NetSyncObject>> byGuid = GroupByGuid(all, out List<NetSyncObject> missing);

            for (int i = 0; i < missing.Count; i++)
            {
                NetSyncObject o = missing[i];
                if (o != null)
                {
                    o.RegenerateGuid_EditorOnly();
                    fixedCount++;
                }
            }

            foreach (KeyValuePair<string, List<NetSyncObject>> kv in byGuid)
            {
                List<NetSyncObject> list = kv.Value;
                if (list.Count <= 1)
                {
                    continue;
                }
                // Skip index 0 — keep the first occurrence as the canonical
                // owner of the authored GUID.
                for (int i = 1; i < list.Count; i++)
                {
                    NetSyncObject o = list[i];
                    if (o != null)
                    {
                        o.RegenerateGuid_EditorOnly();
                        fixedCount++;
                    }
                }
            }

            return fixedCount;
        }
    }

    /// <summary>
    /// Asset postprocessor: when a scene or prefab is (re)imported, sweep
    /// any NetSyncObject with a missing GUID and assign one. This covers
    /// duplicated scenes / copied prefabs that land in the project without
    /// passing through OnValidate.
    /// </summary>
    internal sealed class NetSyncObjectAssetPostprocessor : AssetPostprocessor
    {
        private static void OnPostprocessAllAssets(
            string[] importedAssets,
            string[] deletedAssets,
            string[] movedAssets,
            string[] movedFromAssetPaths)
        {
            bool anyChange = false;
            for (int i = 0; i < importedAssets.Length; i++)
            {
                string path = importedAssets[i];
                if (path.EndsWith(".prefab", System.StringComparison.OrdinalIgnoreCase))
                {
                    anyChange |= FixPrefab(path);
                }
            }
            if (anyChange)
            {
                AssetDatabase.SaveAssets();
            }
        }

        private static bool FixPrefab(string path)
        {
            GameObject root = AssetDatabase.LoadAssetAtPath<GameObject>(path);
            if (root == null)
            {
                return false;
            }
            NetSyncObject[] objs = root.GetComponentsInChildren<NetSyncObject>(includeInactive: true);
            bool changed = false;
            for (int i = 0; i < objs.Length; i++)
            {
                NetSyncObject o = objs[i];
                if (o == null)
                {
                    continue;
                }
                string guid = o.GuidForEditor;
                if (string.IsNullOrEmpty(guid) || !System.Guid.TryParse(guid, out _))
                {
                    o.RegenerateGuid_EditorOnly();
                    changed = true;
                }
            }
            if (changed)
            {
                EditorUtility.SetDirty(root);
            }
            return changed;
        }
    }
}
