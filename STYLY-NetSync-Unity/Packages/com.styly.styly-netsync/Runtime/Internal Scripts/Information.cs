using System;
using UnityEngine;

namespace Styly.NetSync
{
    internal class Information
    {
        /// <summary>
        /// The package name of STYLY NetSync.
        /// </summary>
        private const string PackageName = "com.styly.styly-netsync";

        /// <summary>
        /// Get the version of the package which is written in the Resources folder by CI.
        /// </summary>
        /// <returns></returns>
        public static string GetVersion()
        {
            var ResourcePath = PackageName + ".version";
            var ta = Resources.Load<TextAsset>(ResourcePath);
            if (ta != null) return ta.text.Trim();
            return "unknown";
        }

        /// <summary>
        /// Parse semantic version string into (major, minor, patch) tuple.
        /// </summary>
        /// <param name="versionStr">Version string like "0.7.5" or "1.2.3-beta"</param>
        /// <returns>Tuple of (major, minor, patch) integers. Returns (0, 0, 0) for invalid input.</returns>
        public static (int major, int minor, int patch) ParseVersion(string versionStr)
        {
            if (string.IsNullOrEmpty(versionStr) || versionStr == "unknown")
            {
                return (0, 0, 0);
            }

            try
            {
                // Remove any suffix like "-beta", "-rc1", etc.
                var baseVersion = versionStr.Split('-')[0].Split('+')[0];
                var parts = baseVersion.Split('.');
                int major = parts.Length > 0 ? int.Parse(parts[0]) : 0;
                int minor = parts.Length > 1 ? int.Parse(parts[1]) : 0;
                int patch = parts.Length > 2 ? int.Parse(parts[2]) : 0;
                // Clamp to 0-255 range for single byte storage
                return (Math.Min(major, 255), Math.Min(minor, 255), Math.Min(patch, 255));
            }
            catch (Exception)
            {
                return (0, 0, 0);
            }
        }

        /// <summary>
        /// Check if the client version is compatible with the server version.
        /// Major and minor versions must match. Patch version mismatches are OK.
        /// </summary>
        /// <param name="serverMajor">Server major version</param>
        /// <param name="serverMinor">Server minor version</param>
        /// <param name="serverPatch">Server patch version (unused, for reference only)</param>
        /// <returns>True if versions are compatible, false otherwise</returns>
        public static bool IsVersionCompatible(int serverMajor, int serverMinor, int serverPatch)
        {
            var (clientMajor, clientMinor, _) = ParseVersion(GetVersion());

            // If either version is unknown (0.0.x), skip version check
            if (clientMajor == 0 && clientMinor == 0)
            {
                return true;
            }
            if (serverMajor == 0 && serverMinor == 0)
            {
                return true;
            }

            // Major and minor must match
            return clientMajor == serverMajor && clientMinor == serverMinor;
        }
    }
}