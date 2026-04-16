// NetSyncObjectIdValidator.cs
// Automatically detects and fixes duplicate / missing NetSyncObject GUIDs
// when scenes are opened or Play Mode is entered.

using System.Collections.Generic;
using Styly.NetSync;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Styly.NetSync.Internal.EditorTools
{
    [InitializeOnLoad]
    internal static class NetSyncObjectIdValidator
    {
        static NetSyncObjectIdValidator()
        {
            EditorSceneManager.sceneOpened += OnSceneOpened;
            EditorApplication.playModeStateChanged += OnPlayModeStateChanged;
        }

        private static void OnSceneOpened(Scene scene, OpenSceneMode mode)
        {
            ValidateAndFix();
        }

        private static void OnPlayModeStateChanged(PlayModeStateChange state)
        {
            if (state == PlayModeStateChange.ExitingEditMode)
            {
                ValidateAndFix();
            }
        }

        private static void ValidateAndFix()
        {
            int fixedCount = FixAll();
            if (fixedCount > 0)
            {
                Debug.Log($"[NetSync] Auto-fixed {fixedCount} NetSyncObject ObjectId(s) (duplicate or missing).");
                EditorSceneManager.MarkAllScenesDirty();
            }
        }

        /// <summary>
        /// Collect all NetSyncObject instances across every loaded scene,
        /// including inactive ones.
        /// </summary>
        private static List<NetSyncObject> CollectAllInLoadedScenes()
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
        /// Regenerate missing GUIDs, and for any duplicate group keep the
        /// first occurrence and regenerate the rest. Returns the number of
        /// components whose GUID was changed.
        /// </summary>
        private static int FixAll()
        {
            List<NetSyncObject> all = CollectAllInLoadedScenes();
            Dictionary<string, List<NetSyncObject>> byGuid = new Dictionary<string, List<NetSyncObject>>();
            List<NetSyncObject> missing = new List<NetSyncObject>();

            for (int i = 0; i < all.Count; i++)
            {
                NetSyncObject obj = all[i];
                if (obj == null)
                {
                    continue;
                }
                string guid = obj.ObjectIdForEditor;
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

            int fixedCount = 0;

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
    /// Asset postprocessor: when a prefab is (re)imported, sweep any
    /// NetSyncObject with a missing GUID and assign one. This covers
    /// duplicated prefabs that land in the project without passing
    /// through OnValidate.
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
                string guid = o.ObjectIdForEditor;
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
