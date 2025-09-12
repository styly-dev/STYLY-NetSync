using UnityEngine;

/// <summary>
/// Simple solver that estimates and updates a body Transform from three tracked points (head, left/right hands).
/// The target body Transform can be assigned via `body`. If not set, this component's own `transform` is used.
/// </summary>
public class BodyTransformSolver : MonoBehaviour
{
    [Header("Target Body")]
    [Tooltip("Body Transform to control. If not set, falls back to this component's transform.")]
    public Transform body;

    [Header("Tracked Inputs")]
    public Transform head;
    public Transform rightHand;
    public Transform leftHand;


    [Header("Offsets (meters)")]
    [Tooltip("Approximate distance from eyes/head center to body center")]
    public float headToChest = 0.24f;  // adjust within 0.22–0.28
    [Tooltip("Offset to pull the body slightly backward from directly under the head")]
    public float chestBack = 0.06f;    // adjust within 0.04–0.08
    [Tooltip("Maximum forward push of the body when both hands move ahead of the head")]
    public float handPushForwardMax = 0.05f;

    [Header("Orientation Weights")]
    [Range(0f, 1f)] public float yawFollow = 0.9f;         // how much body Yaw follows the head
    [Range(0f, 1f)] public float pitchRollFollow = 0.25f;  // how much body follows head tilt (P/R)
    [Range(0f, 1f)] public float handYawInfluence = 0.25f; // how much hand direction blends into Yaw

    [Header("Crouch / Lean")]
    [Tooltip("Standing head height (calibration value)")]
    public float standingHeadHeight = 1.65f;
    [Tooltip("Head drop distance that maps to maximum forward lean (m)")]
    public float crouchRange = 0.40f;
    [Tooltip("Maximum forward pitch applied to the body when crouched (degrees)")]
    public float crouchMaxPitch = 15f;

    [Header("Smoothing")]
    [Tooltip("Time constant (seconds) for position/rotation smoothing. Smaller is snappier.")]
    public float smoothingTime = 0.08f;

    Vector3 _posVel; // reserved for optional smoothing extensions
    Quaternion _prevRot;
    Transform _targetBody;

    void Awake()
    {
        ResolveBodyTarget();
    }

    void OnValidate()
    {
        // Keep target up to date in editor.
        if (!Application.isPlaying)
        {
            ResolveBodyTarget();
        }
    }

    void Start()
    {
        if (head != null) standingHeadHeight = head.position.y; // simple calibration
        if (_targetBody != null)
        {
            _prevRot = _targetBody.rotation;
        }
    }

    void LateUpdate()
    {
        if (_targetBody == null) return;
        if (head == null || leftHand == null || rightHand == null) return;

        Vector3 up = Vector3.up;

        // --- 1) Base Yaw (project head forward vector onto horizontal plane)
        Quaternion headRot = head.rotation;
        Quaternion headYawOnly = YawOnly(headRot, up);

        // --- 2) Extract head tilt (Pitch/Roll) and attenuate for body
        Quaternion tiltOnly = Quaternion.Inverse(headYawOnly) * headRot;
        Vector3 tiltEuler = tiltOnly.eulerAngles;
        float pitch = ClampSym(SignedAngle(tiltEuler.x), -20f, 20f) * pitchRollFollow;
        float roll  = ClampSym(SignedAngle(tiltEuler.z), -15f, 15f) * pitchRollFollow * 0.5f;

        Quaternion chestRotTarget = headYawOnly * Quaternion.Euler(pitch, 0f, roll);

        // --- 3) Blend midpoint of hands into Yaw
        Vector3 handMid = (leftHand.position + rightHand.position) * 0.5f;
        Vector3 toHandsPlanar = Vector3.ProjectOnPlane(handMid - head.position, up);
        if (toHandsPlanar.sqrMagnitude > 1e-6f)
        {
            Quaternion handYaw = Quaternion.LookRotation(toHandsPlanar.normalized, up);
            chestRotTarget = Quaternion.Slerp(chestRotTarget, handYaw, handYawInfluence);
        }

        // --- 4) Crouch estimation (forward lean based on head lowering)
        float headH = head.position.y;
        float crouchT = Mathf.Clamp01((standingHeadHeight - headH) / Mathf.Max(0.0001f, crouchRange));
        if (crouchT > 0f)
        {
            chestRotTarget *= Quaternion.Euler(-crouchMaxPitch * crouchT, 0f, 0f);
        }

        // --- 5) Body target position: below head + slight backward offset
        Vector3 chestPosTarget =
            head.position
            + (-up * headToChest)
            - (chestRotTarget * Vector3.forward * chestBack);

        // When hands are in front, push body forward slightly
        if (toHandsPlanar.sqrMagnitude > 1e-6f)
        {
            float push = Mathf.Min(handPushForwardMax, toHandsPlanar.magnitude * 0.15f);
            chestPosTarget += toHandsPlanar.normalized * push;
        }

        // --- 6) Smoothing (exponential smoothing)
        float a = 1f - Mathf.Exp(-Time.deltaTime / Mathf.Max(0.0001f, smoothingTime));
        _targetBody.position = Vector3.Lerp(_targetBody.position, chestPosTarget, a);
        _targetBody.rotation = Quaternion.Slerp(_targetBody.rotation, chestRotTarget, a);

        _prevRot = _targetBody.rotation;
    }

    public void CalibrateStandingNow()
    {
        if (head != null) standingHeadHeight = head.position.y;
    }

    static Quaternion YawOnly(Quaternion rot, Vector3 up)
    {
        Vector3 fwd = Vector3.ProjectOnPlane(rot * Vector3.forward, up);
        if (fwd.sqrMagnitude < 1e-6f) return Quaternion.identity;
        return Quaternion.LookRotation(fwd.normalized, up);
    }

    static float SignedAngle(float eulerDeg)
    {
        // 0..360 -> -180..180
        float a = Mathf.Repeat(eulerDeg + 180f, 360f) - 180f;
        return a;
    }

    static float ClampSym(float v, float min, float max)
    {
        return Mathf.Clamp(v, min, max);
    }

    void ResolveBodyTarget()
    {
        if (body != null)
        {
            _targetBody = body;
        }
        else
        {
            _targetBody = transform;
        }
    }
}
