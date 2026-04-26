using UnityEngine;

namespace Styly.NetSync
{
    /// <summary>
    /// Drives helper Transforms from a NetSyncAvatar reference.
    /// - Estimated Body: head world position with a vertical offset (yaw-only rotation).
    /// - Calculated Ground Center: world point directly below the head at the avatar's
    ///   physical rig floor (head.y - PhysicalPosition.y); works for both local and
    ///   remote avatars and tracks vertical rig motion (elevators, lifts) under v4.
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

            // Yaw-only rotation, shared by both targets.
            Vector3 headForward = head.forward;
            headForward.y = 0f;
            bool hasYaw = headForward.sqrMagnitude > 0.001f;
            Quaternion yawRotation = hasYaw
                ? Quaternion.LookRotation(headForward, Vector3.up)
                : Quaternion.identity;

            if (body != null)
            {
                Vector3 bodyPosition = head.position;
                bodyPosition.y -= offsetY;
                body.position = bodyPosition;
                if (hasYaw) body.rotation = yawRotation;
            }

            if (groundCenter != null)
            {
                Vector3 groundPosition = head.position;
                groundPosition.y -= netSyncAvatar.PhysicalPosition.y;
                groundCenter.position = groundPosition;
                if (hasYaw) groundCenter.rotation = yawRotation;
            }
        }
    }
}
