using UnityEngine;

namespace Styly.NetSync
{
    /// <summary>
    /// Estimate a simple torso transform (position & rotation) from 3 tracked points:
    /// head, right hand, left hand. Public fields are only the four Transforms.
    /// Attach anywhere and assign references in the Inspector.
    /// </summary>
    public class BodyTransformSolver : MonoBehaviour
    {
        // ---- Public (as requested) ----
        public Transform body;       // Torso object to drive
        public Transform head;       // HMD
        public Transform rightHand;  // Right controller/hand
        public Transform leftHand;   // Left controller/hand

        // ---- Private internals (no extra public options) ----
        Vector3 _posVel;         // for SmoothDamp
        float _scale = 1f;     // inferred user scale from hand span

        void LateUpdate()
        {
            if (!body || !head || !rightHand || !leftHand) return;

            Vector3 up = Vector3.up;

            // --- Gather inputs ---
            Vector3 rh = rightHand.position;
            Vector3 lh = leftHand.position;
            Vector3 hd = head.position;

            // Hand span (used as shoulder proxy & scale hint)
            Vector3 across = rh - lh;
            float acrossLen = across.magnitude;
            Vector3 acrossDir = (acrossLen > 1e-4f) ? (across / acrossLen) : head.right;

            // --- Facing (Yaw) ---
            // 1) From head yaw
            Vector3 fwdHead = Vector3.ProjectOnPlane(head.forward, up);
            if (fwdHead.sqrMagnitude < 1e-6f) fwdHead = Vector3.forward;
            fwdHead.Normalize();

            // 2) From hands (perpendicular to shoulder axis, horizontal)
            Vector3 fwdHands = Vector3.Cross(up, acrossDir);   // left→right, so cross gives forward-ish
            if (Vector3.Dot(fwdHands, fwdHead) < 0f) fwdHands = -fwdHands; // keep same hemisphere
            fwdHands.Normalize();

            // Blend amount rises a bit when hands are spread (more reliable shoulder cue)
            float handSpread01 = Mathf.Clamp01(Mathf.InverseLerp(0.15f, 0.60f, acrossLen)); // meters
            float handsWeight = Mathf.Lerp(0.10f, 0.35f, handSpread01); // modest influence
            Vector3 fwd = Vector3.Slerp(fwdHead, fwdHands, handsWeight).normalized;
            Quaternion targetRot = Quaternion.LookRotation(fwd, up);

            // --- Scale estimation from hand span (shoulder width ~0.38m baseline) ---
            float shoulderRef = 0.38f;
            float scaleNow = (acrossLen > 1e-3f) ? Mathf.Clamp(acrossLen / shoulderRef, 0.6f, 1.6f) : 1f;
            _scale = Mathf.Lerp(_scale, scaleNow, 1f - Mathf.Exp(-6f * Time.deltaTime)); // smooth, snappy

            // --- Position ---
            // Base offsets relative to head (meters), NOT scaled (fixed offset)
            float down = 0.28f; // head → chest/torso center drop (fixed distance)
            float back = 0.08f; // a touch behind head forward to land in torso center (fixed distance)

            // Anchor horizontally toward hands' midpoint (XZ only), but keep head Y
            Vector3 handsMid = (rh + lh) * 0.5f;
            Vector3 handsMidFlat = new(handsMid.x, hd.y, handsMid.z); // Use head's Y for hands midpoint
            Vector3 anchor = Vector3.Lerp(hd, handsMidFlat, 0.25f); // Horizontal shift only, Y stays at head level

            Vector3 targetPos = anchor - (fwd * back) - (up * down);

            // --- Apply with light smoothing ---
            float posSmooth = 0.04f; // seconds
            body.position = Vector3.SmoothDamp(body.position, targetPos, ref _posVel, posSmooth);

            float rotLerp = 1f - Mathf.Exp(-10f * Time.deltaTime);
            body.rotation = Quaternion.Slerp(body.rotation, targetRot, rotLerp);
        }
    }
}
