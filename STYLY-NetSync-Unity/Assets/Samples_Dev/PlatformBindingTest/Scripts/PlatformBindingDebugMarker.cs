// PlatformBindingDebugMarker.cs
using UnityEngine;

namespace Styly.NetSync.Samples.PlatformBindingTest
{
    /// <summary>
    /// Diagnostic marker that follows a platform Transform directly in LateUpdate,
    /// bypassing all network and binding logic. Used to isolate whether observed
    /// jitter on remote avatars during platform motion comes from the network
    /// override path or from the HMD render/compositor path (late-latching, ASW).
    ///
    /// If this marker jitters in the same way as the remote avatar on HMD, the
    /// cause is in the render layer. If the marker is rock-solid while the avatar
    /// jitters, the cause is in the override path.
    /// </summary>
    public class PlatformBindingDebugMarker : MonoBehaviour
    {
        [SerializeField, Tooltip("Platform Transform to follow. Assign the elevator root.")]
        private Transform _platformToFollow;

        [SerializeField, Tooltip("Offset from the platform origin, in the platform's local frame.")]
        private Vector3 _localOffset = new Vector3(0.5f, 1.6f, 0f);

        private void LateUpdate()
        {
            if (_platformToFollow == null) { return; }
            transform.position = _platformToFollow.TransformPoint(_localOffset);
            transform.rotation = _platformToFollow.rotation;
        }
    }
}
