using UnityEngine;

namespace Styly.NetSync
{
    /// <summary>
    /// Solves body and foot transforms to follow the NetSyncAvatar's head (HMD) with Y-axis rotation only.
    /// Body is driven to the head position with a vertical offset; foot is driven to the ground directly
    /// below the head, using PhysicalPosition.y as the HMD local height so it snaps to the rig floor.
    /// </summary>
    public class BodyTransformSolver : MonoBehaviour
    {
        // ---- Public (as requested) ----
        public NetSyncAvatar netSyncAvatar; // Source of head transform and PhysicalPosition.y
        public Transform body;              // Torso object to drive (optional)
        public Transform foot;              // Foot object to drive (optional)
        public float offsetY = 0.2f;        // Vertical offset of body below the head (in meters)

        void LateUpdate()
        {
            if (netSyncAvatar == null) return;
            Transform head = netSyncAvatar._head;
            if (head == null) return;

            // Extract only Y-axis rotation from head (shared by body and foot)
            Vector3 headForward = head.forward;
            headForward.y = 0f; // Project to horizontal plane by zeroing Y component
            bool hasYaw = headForward.sqrMagnitude > 0.001f;
            Quaternion yawRotation = hasYaw ? Quaternion.LookRotation(headForward, Vector3.up) : Quaternion.identity;

            // Drive body: head position with vertical offset and yaw-only rotation
            if (body != null)
            {
                Vector3 bodyPosition = head.position;
                bodyPosition.y -= offsetY;
                body.position = bodyPosition;

                if (hasYaw)
                {
                    body.rotation = yawRotation;
                }
            }

            // Drive foot: ground position below head (using HMD local height) with yaw-only rotation
            if (foot != null)
            {
                Vector3 footPosition = head.position;
                footPosition.y -= netSyncAvatar.PhysicalPosition.y;
                foot.position = footPosition;

                if (hasYaw)
                {
                    foot.rotation = yawRotation;
                }
            }
        }
    }
}
