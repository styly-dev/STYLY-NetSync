// SceneHashBuilder.cs (Runtime)
// Computes a deterministic SHA-256 digest over the authored NetSync content
// of a scene. Used at join-time by ReplicationClient to verify that the
// server and the local scene agree on the replicated layout.
//
// The Editor/NetSyncBuildValidator.cs build-time validator wraps this
// runtime helper; there is exactly one hashing algorithm.

using System.Collections.Generic;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Styly.NetSync;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Styly.NetSync.Internal
{
    /// <summary>
    /// Deterministic scene-hash helper.
    ///
    /// Contributing inputs, in order:
    ///   1. Scene name
    ///   2. For each NetSyncObject (sorted by GUID ascending):
    ///        - GUID
    ///        - authored default local position (x,y,z)
    ///        - authored default local rotation (x,y,z,w)
    ///        - authored default local scale (x,y,z)
    ///        - profile version
    ///
    /// Floats are formatted with "R" so round-trip precision is preserved
    /// and the hash is stable across platforms that agree on IEEE-754.
    /// </summary>
    public static class SceneHashBuilder
    {
        /// <summary>
        /// Compute the hex SHA-256 hash for a scene by inspecting the live
        /// hierarchy. Intended for runtime use (join handshake).
        /// </summary>
        public static string BuildHash(Scene scene)
        {
            List<NetSyncObject> all = new List<NetSyncObject>();
            GameObject[] roots = scene.GetRootGameObjects();
            for (int i = 0; i < roots.Length; i++)
            {
                GameObject root = roots[i];
                if (root == null)
                {
                    continue;
                }
                all.AddRange(root.GetComponentsInChildren<NetSyncObject>(includeInactive: true));
            }
            return BuildHash(scene.name, all);
        }

        /// <summary>
        /// Compute the hex SHA-256 hash from an explicit list of
        /// NetSyncObject instances. Exposed so editor build-time tooling
        /// and unit tests can share the same algorithm.
        /// </summary>
        public static string BuildHash(string sceneName, IReadOnlyList<NetSyncObject> objects)
        {
            List<NetSyncObject> all = new List<NetSyncObject>(objects != null ? objects.Count : 0);
            if (objects != null)
            {
                for (int i = 0; i < objects.Count; i++)
                {
                    NetSyncObject o = objects[i];
                    if (o != null)
                    {
                        all.Add(o);
                    }
                }
            }

            // Sort by GUID for determinism.
            all.Sort((a, b) =>
            {
                string ga = a != null ? a.ObjectId : string.Empty;
                string gb = b != null ? b.ObjectId : string.Empty;
                return string.CompareOrdinal(ga, gb);
            });

            StringBuilder sb = new StringBuilder();
            sb.Append("scene:").Append(sceneName ?? string.Empty).Append('\n');
            for (int i = 0; i < all.Count; i++)
            {
                NetSyncObject o = all[i];
                if (o == null)
                {
                    continue;
                }
                Transform t = o.transform;
                Vector3 p = t.localPosition;
                Quaternion r = t.localRotation;
                Vector3 s = t.localScale;

                sb.Append(o.ObjectId).Append('|');
                AppendFloat(sb, p.x); sb.Append(',');
                AppendFloat(sb, p.y); sb.Append(',');
                AppendFloat(sb, p.z); sb.Append('|');
                AppendFloat(sb, r.x); sb.Append(',');
                AppendFloat(sb, r.y); sb.Append(',');
                AppendFloat(sb, r.z); sb.Append(',');
                AppendFloat(sb, r.w); sb.Append('|');
                AppendFloat(sb, s.x); sb.Append(',');
                AppendFloat(sb, s.y); sb.Append(',');
                AppendFloat(sb, s.z); sb.Append('|');
                sb.Append(o.Profile.ProfileVersion);
                sb.Append('\n');
            }

            using SHA256 sha = SHA256.Create();
            byte[] digest = sha.ComputeHash(Encoding.UTF8.GetBytes(sb.ToString()));
            StringBuilder hex = new StringBuilder(digest.Length * 2);
            for (int i = 0; i < digest.Length; i++)
            {
                hex.Append(digest[i].ToString("x2", CultureInfo.InvariantCulture));
            }
            return hex.ToString();
        }

        private static void AppendFloat(StringBuilder sb, float f)
        {
            sb.Append(f.ToString("R", CultureInfo.InvariantCulture));
        }
    }
}
