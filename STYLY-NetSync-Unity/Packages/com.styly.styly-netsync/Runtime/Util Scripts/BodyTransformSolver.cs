using UnityEngine;

namespace Styly.NetSync
{
    /// <summary>
    /// Drives helper Transforms from a NetSyncAvatar reference.
    /// - Estimated Body: head world position with a vertical offset (yaw-only rotation).
    /// - Calculated Ground Center: physical floor point derived from the avatar's
    ///   physical pose, so the marker does not ride with virtual locomotion or
    ///   moving-floor motion.
    /// </summary>
    // Runs after MovingFloorLateApplier so visible body meshes read the
    // final head pose for the rendered frame.
    [DefaultExecutionOrder(10010)]
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
                // Drop the marker straight below the head at the rig's floor
                // height: head world Y minus the head's height in physical
                // (rig-local) space. Tracks vertical rig motion such as
                // elevators, lifts, and moving-floor carry without snapping to
                // world Y = 0.
                Vector3 groundPosition = head.position;
                groundPosition.y -= netSyncAvatar.PhysicalPosition.y;
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
