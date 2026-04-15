// NetSyncBuildValidator.cs
// IPreprocessBuildWithReport hook that fails the build if any scene in the
// build list contains duplicate or missing NetSyncObject GUIDs.

using System.Collections.Generic;
using Styly.NetSync;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Styly.NetSync.Internal.EditorTools
{
    public sealed class NetSyncBuildValidator : IPreprocessBuildWithReport
    {
        public int callbackOrder => 0;

        public void OnPreprocessBuild(BuildReport report)
        {
            EditorBuildSettingsScene[] buildScenes = EditorBuildSettings.scenes;
            int totalDuplicates = 0;
            int totalMissing = 0;
            List<string> failures = new List<string>();

            for (int i = 0; i < buildScenes.Length; i++)
            {
                EditorBuildSettingsScene entry = buildScenes[i];
                if (!entry.enabled)
                {
                    continue;
                }
                string path = entry.path;
                if (string.IsNullOrEmpty(path))
                {
                    continue;
                }

                Scene scene = EditorSceneManager.OpenScene(path, OpenSceneMode.Additive);
                try
                {
                    int dup;
                    int miss;
                    ValidateScene(scene, out dup, out miss, failures);
                    totalDuplicates += dup;
                    totalMissing += miss;
                }
                finally
                {
                    EditorSceneManager.CloseScene(scene, removeScene: true);
                }
            }

            if (totalDuplicates > 0 || totalMissing > 0)
            {
                string joined = string.Join("\n  - ", failures);
                throw new BuildFailedException(
                    $"[NetSync] Build aborted: {totalDuplicates} duplicate GUID group(s), {totalMissing} missing GUID(s).\n  - {joined}");
            }
        }

        private static void ValidateScene(Scene scene, out int duplicates, out int missing, List<string> failures)
        {
            duplicates = 0;
            missing = 0;
            Dictionary<string, List<string>> byGuid = new Dictionary<string, List<string>>();
            List<string> missingNames = new List<string>();

            GameObject[] roots = scene.GetRootGameObjects();
            for (int r = 0; r < roots.Length; r++)
            {
                GameObject root = roots[r];
                if (root == null)
                {
                    continue;
                }
                NetSyncObject[] objs = root.GetComponentsInChildren<NetSyncObject>(includeInactive: true);
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
                        missingNames.Add(o.name);
                        continue;
                    }
                    if (!byGuid.TryGetValue(guid, out List<string> names))
                    {
                        names = new List<string>();
                        byGuid[guid] = names;
                    }
                    names.Add(o.name);
                }
            }

            foreach (KeyValuePair<string, List<string>> kv in byGuid)
            {
                if (kv.Value.Count > 1)
                {
                    duplicates++;
                    failures.Add($"{scene.path}: duplicate GUID {kv.Key} on [{string.Join(", ", kv.Value)}]");
                }
            }
            for (int i = 0; i < missingNames.Count; i++)
            {
                missing++;
                failures.Add($"{scene.path}: missing GUID on '{missingNames[i]}'");
            }
        }
    }
}
