// Copyright (c) STYLY
// Editor utility to inject NSLocalNetworkUsageDescription into iOS Info.plist after build.
// Note: All comments and documentation are written in English by project policy.

#if UNITY_EDITOR && UNITY_IOS
using System.IO;
using UnityEditor;
using UnityEditor.Callbacks;
using UnityEditor.iOS.Xcode;
using UnityEngine;

namespace STYLY.NetSync.Editor
{
    /// <summary>
    /// Post-processes the generated Xcode project to ensure Info.plist contains
    /// NSLocalNetworkUsageDescription required by iOS 14+ Local Network privacy.
    /// </summary>
    public static class iOSInfoPlistLocalNetwork
    {
        // Key name required by iOS.
        private const string LocalNetworkUsageKey = "NSLocalNetworkUsageDescription";

        // Default message shown in the system dialog. You can change this text to better fit your app.
        // Keep the message succinct and user-friendly, explaining why local network access is needed.
        private const string DefaultLocalNetworkUsageDescription =
            "This app uses the local network to discover and communicate with devices for synchronization.";

        /// <summary>
        /// Adds or updates NSLocalNetworkUsageDescription in Info.plist after building for iOS.
        /// </summary>
        /// <param name="buildTarget">The build target.</param>
        /// <param name="pathToBuiltProject">The path to the generated Xcode project.</param>
        [PostProcessBuild]
        public static void OnPostProcessBuild(BuildTarget buildTarget, string pathToBuiltProject)
        {
            // Only process for iOS builds.
            if (buildTarget != BuildTarget.iOS)
            {
                return;
            }

            if (string.IsNullOrEmpty(pathToBuiltProject))
            {
                Debug.LogWarning("[NetSync] iOS post-process: pathToBuiltProject is empty; skipping Info.plist modification.");
                return;
            }

            string plistPath = Path.Combine(pathToBuiltProject, "Info.plist");
            if (!File.Exists(plistPath))
            {
                Debug.LogWarning("[NetSync] iOS post-process: Info.plist not found; skipping.");
                return;
            }

            try
            {
                var plist = new PlistDocument();
                plist.ReadFromFile(plistPath);

                PlistElementDict rootDict = plist.root;
                if (rootDict != null)
                {
                    // Set or update the NSLocalNetworkUsageDescription value.
                    rootDict.SetString(LocalNetworkUsageKey, DefaultLocalNetworkUsageDescription);
                }

                plist.WriteToFile(plistPath);
                Debug.Log("[NetSync] iOS post-process: Added NSLocalNetworkUsageDescription to Info.plist.");
            }
            catch (System.Exception ex)
            {
                Debug.LogError("[NetSync] iOS post-process: Failed to update Info.plist. " + ex.Message);
            }
        }
    }
}
#endif

