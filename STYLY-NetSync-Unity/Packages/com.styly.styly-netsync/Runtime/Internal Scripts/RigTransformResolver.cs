// RigTransformResolver.cs
//
// Resolves the transform that the local avatar should follow / use as the
// locomotion-delta reference. The same priority order is required by both
// NetSyncManager (locomotion delta) and AvatarManager (ParentConstraint
// source); centralizing it here keeps the two call sites from drifting.

using UnityEngine;
using Unity.XR.CoreUtils;

namespace Styly.NetSync.Internal
{
    internal static class RigTransformResolver
    {
        /// <summary>
        /// Returns the rig transform that the local avatar should follow.
        ///
        /// Priority:
        ///   1. XR Interaction Toolkit's <c>XROrigin</c> — the cross-platform
        ///      default used by most projects.
        ///   2. <c>OVRCameraRig.trackingSpace</c> — fallback for pure Meta
        ///      XR SDK projects that do not include an XROrigin. Resolved via
        ///      reflection (gated by <c>META_XR_SDK_PRESENT</c>) so non-Meta
        ///      builds pay no cost. In MRUK World Lock / colocation setups
        ///      this transform is shifted every frame, so following it keeps
        ///      the avatar pinned to the tracked space.
        ///
        /// Returns <c>null</c> when neither rig is present; callers should
        /// treat that as "no follow source" and skip parenting.
        ///
        /// Note: Do not use null propagation with UnityEngine.Object.
        /// </summary>
        public static Transform TryResolve()
        {
            var xrOrigin = Object.FindFirstObjectByType<XROrigin>();
            if (xrOrigin != null)
            {
                return xrOrigin.transform;
            }

            return OvrCameraRigBridge.TryGetTrackingSpace();
        }
    }
}
