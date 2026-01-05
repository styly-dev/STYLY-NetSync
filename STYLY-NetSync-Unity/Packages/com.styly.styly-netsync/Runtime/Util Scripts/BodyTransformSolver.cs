using UnityEngine;

namespace Styly.NetSync
{
    /// <summary>
    /// Solves body transform to follow head position with Y-axis rotation only.
    /// Body position is derived from head (HMD) position only, without hand tracking dependency.
    /// </summary>
    public class BodyTransformSolver : MonoBehaviour
    {
        // ---- Public (as requested) ----
        public Transform body;       // Torso object to drive
        public Transform head;       // HMD
        public float offsetY = 0.2f; // Vertical offset below the head (in meters)

        void LateUpdate()
        {
            if (body == null || head == null) return;

            // Apply head position to body with vertical offset
            Vector3 bodyPosition = head.position;
            bodyPosition.y -= offsetY;
            body.position = bodyPosition;

            // Extract only Y-axis rotation from head and apply to body
            Vector3 headForward = head.forward;
            headForward.y = 0f; // Project to horizontal plane by zeroing Y component

            if (headForward.sqrMagnitude > 0.001f) // Only apply rotation if vector is not near zero
            {
                body.rotation = Quaternion.LookRotation(headForward, Vector3.up);
            }
        }
    }
}
