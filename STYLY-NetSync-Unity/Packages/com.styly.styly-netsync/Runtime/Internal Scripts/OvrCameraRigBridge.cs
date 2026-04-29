// OvrCameraRigBridge.cs
//
// When a project uses Meta's OVRCameraRig instead of XR Interaction Toolkit's
// XROrigin, NetSync's avatar root has no shared anchor in the scene to follow.
// In LBE / colocation setups (e.g. with MR Utility Kit's World Lock) this leaves
// the local avatar pinned at world (0,0,0) while the actual TrackingSpace is
// shifted each frame, so the local avatar drifts away from the player's head.
//
// This bridge resolves the OVRCameraRig.trackingSpace transform via reflection
// so NetSync does not take a hard dependency on the Meta XR SDK assembly. The
// reflection lookup is gated by the META_XR_SDK_PRESENT define, which the
// runtime asmdef adds automatically when the com.meta.xr.sdk.core package is
// installed.

using System.Reflection;
using UnityEngine;

namespace Styly.NetSync.Internal
{
    internal static class OvrCameraRigBridge
    {
        private const string OvrCameraRigTypeName = "OVRCameraRig, Oculus.VR";
        private const string TrackingSpacePropertyName = "trackingSpace";
        private const string TrackingSpaceChildName = "TrackingSpace";

        /// <summary>
        /// Returns the <c>OVRCameraRig.trackingSpace</c> transform of the first
        /// active OVRCameraRig found in the scene, or <c>null</c> when no
        /// OVRCameraRig exists. Always returns <c>null</c> on builds where the
        /// Meta XR SDK is not installed.
        /// </summary>
        public static Transform TryGetTrackingSpace()
        {
#if META_XR_SDK_PRESENT
            var ovrType = System.Type.GetType(OvrCameraRigTypeName);
            if (ovrType == null) return null;

            // FindFirstObjectByType(System.Type) is unreliable when the type was
            // resolved via reflection across asmdef boundaries; iterate the
            // MonoBehaviour list and use IsInstanceOfType for a robust match.
            // Note: Do not use null propagation with UnityEngine.Object.
            var monos = Object.FindObjectsByType<MonoBehaviour>(FindObjectsSortMode.None);
            Component rig = null;
            for (int i = 0; i < monos.Length; i++)
            {
                if (ovrType.IsInstanceOfType(monos[i]))
                {
                    rig = monos[i];
                    break;
                }
            }
            if (rig == null) return null;

            var prop = ovrType.GetProperty(TrackingSpacePropertyName,
                BindingFlags.Public | BindingFlags.Instance);
            if (prop != null)
            {
                if (prop.GetValue(rig) is Transform fromProperty && fromProperty != null)
                {
                    return fromProperty;
                }
            }

            // Fallback: locate the conventional child by name. OVRCameraRig wires
            // its trackingSpace property in Awake, so this path only matters when
            // the rig has not been activated yet.
            var fromChild = rig.transform.Find(TrackingSpaceChildName);
            return fromChild != null ? fromChild : null;
#else
            return null;
#endif
        }
    }
}
