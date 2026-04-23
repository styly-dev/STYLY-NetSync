using UnityEngine;

namespace Styly.NetSync
{
    /// <summary>
    /// Solves body and foot transforms to follow the NetSyncAvatar's head (HMD) with Y-axis rotation only.
    /// Body is driven to the head position with a vertical offset. Foot is snapped to the rig floor Y
    /// directly below the head: for the local avatar the head's parent Y is used (the XR rig);
    /// for remote avatars the network protocol does not carry vertical rig movement, so the foot
    /// effectively tracks the world-origin Y (see BinarySerializer decoder; xrOriginDelta.y is
    /// hardcoded to 0). Remote foot placement is correct only while the sender's rig floor stays
    /// at its startup Y.
    /// </summary>
    public class BodyTransformSolver : MonoBehaviour
    {
        // ---- Public (as requested) ----
        public NetSyncAvatar netSyncAvatar; // Source of head transform
        public Transform body;              // Torso object to drive (optional)
        public float bodyOffsetY = 0.2f;        // Vertical offset of body below the head (in meters)
        public Transform foot;              // Foot object to drive (optional)

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
                bodyPosition.y -= bodyOffsetY;
                body.position = bodyPosition;

                if (hasYaw)
                {
                    body.rotation = yawRotation;
                }
            }

            // Drive foot: X/Z of the head at the rig floor Y, with yaw-only rotation
            if (foot != null)
            {
                Vector3 footPosition = head.position;
                if (netSyncAvatar.IsLocalAvatar && head.parent != null)
                {
                    // Local: head is parented under the XR rig, so the rig floor Y is head.parent.y
                    footPosition.y = head.parent.position.y;
                }
                else
                {
                    // Remote: the wire protocol does not carry rig Y movement (xrOriginDelta.y
                    // is always 0 — see BinarySerializer.cs:713/743), so PhysicalPosition.y ends
                    // up equal to head.position.y and the subtraction pins the foot to world Y=0.
                    // This is correct only while the sender's rig stays at its startup Y.
                    footPosition.y -= netSyncAvatar.PhysicalPosition.y;
                }
                foot.position = footPosition;

                if (hasYaw)
                {
                    foot.rotation = yawRotation;
                }
            }
        }
    }
}
