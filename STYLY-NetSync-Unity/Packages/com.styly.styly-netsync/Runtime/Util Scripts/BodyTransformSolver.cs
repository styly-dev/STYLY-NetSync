using UnityEngine;

namespace Styly.NetSync
{
    /// <summary>
    /// Drives helper Transforms from a NetSyncAvatar reference.
    /// - Estimated Body: head world position with a vertical offset (yaw-only rotation).
    /// - Calculated Ground Center: physical floor point derived from the avatar's
    ///   physical pose, so the marker does not ride with virtual locomotion or
    ///   reference-frame motion.
    /// </summary>
    public class BodyTransformSolver : MonoBehaviour
    {
        public NetSyncAvatar netSyncAvatar;

        [Header("Estimated Body")]
        public Transform body;
        public float offsetY = 0.2f;

        [Header("Calculated Ground Center")]
        public Transform groundCenter;

        // Auto-wire netSyncAvatar from the same GameObject (or any ancestor) when the
        // component is first added or reset in the Inspector.
        private void Reset()
        {
            if (netSyncAvatar == null)
            {
                netSyncAvatar = GetComponentInParent<NetSyncAvatar>();
            }
        }

        void LateUpdate()
        {
            if (netSyncAvatar == null) return;
            Transform head = netSyncAvatar._head;
            if (head == null) return;

            // Yaw-only rotation, shared by virtual body targets.
            bool hasYaw = TryGetYawRotation(head.rotation, out var yawRotation);

            if (body != null)
            {
                Vector3 bodyPosition = head.position;
                bodyPosition.y -= offsetY;
                body.position = bodyPosition;
                if (hasYaw) body.rotation = yawRotation;
            }

            if (groundCenter != null)
            {
                Vector3 groundPosition = netSyncAvatar.PhysicalPosition;
                groundPosition.y = 0f;
                groundCenter.position = groundPosition;
                bool hasGroundYaw = TryGetYawRotation(netSyncAvatar.PhysicalRotation, out var groundRotation);
                if (hasGroundYaw) groundCenter.rotation = groundRotation;
            }
        }

        private static bool TryGetYawRotation(Quaternion rotation, out Quaternion yawRotation)
        {
            Vector3 forward = rotation * Vector3.forward;
            forward.y = 0f;
            if (forward.sqrMagnitude <= 0.001f)
            {
                yawRotation = Quaternion.identity;
                return false;
            }

            yawRotation = Quaternion.LookRotation(forward, Vector3.up);
            return true;
        }
    }
}
