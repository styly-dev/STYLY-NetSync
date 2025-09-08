using UnityEngine;

namespace Styly.NetSync
{
    internal class Util
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
    }
}