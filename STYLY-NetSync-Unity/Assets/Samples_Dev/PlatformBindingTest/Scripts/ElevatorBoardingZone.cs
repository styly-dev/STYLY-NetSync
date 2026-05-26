// ElevatorBoardingZone.cs
using Unity.XR.CoreUtils;
using UnityEngine;

namespace Styly.NetSync.Samples.PlatformBindingTest
{
    /// <summary>
    /// Detects when the local player enters or leaves the trigger volume above
    /// an elevator. Detection keys off colliders tagged <c>Player</c> on the
    /// local avatar prefab. Each zone optionally calls
    /// <see cref="NetSyncMovingFloor.BoardLocalAvatar"/> so receivers reconstruct
    /// that avatar in the elevator's moving floor (v5).
    ///
    /// While boarded the rig is moved by the elevator's per-frame delta in
    /// <see cref="LateUpdate"/> rather than parented under the elevator. The
    /// Transform.SetParent path causes the rig's world position to be
    /// recomputed via inverse-matrix multiplication, which introduces ~1e-6 m
    /// of drift each parent change and produces an OnTriggerExit/Enter
    /// ping-pong right at the trigger boundary.
    ///
    /// The reference count handles the common case of multiple Player-tagged
    /// colliders on the avatar (e.g. head + hands). Boarding fires on the first
    /// enter; unboarding fires only when the last collider has exited.
    /// </summary>
    public class ElevatorBoardingZone : MonoBehaviour
    {
        [Header("Platform")]
        [SerializeField, Tooltip("If true, NetSyncMovingFloor.BoardLocalAvatar is called on board so receivers apply the issue #425 fix. Leave false on the 'raw' elevator to keep the legacy laggy reconstruction visible.")]
        private bool _applyPlatformBinding;

        [Header("References")]
        [SerializeField, Tooltip("The elevator GameObject the rig should be parented under while inside the volume. Usually this zone's parent.")]
        private GameObject _elevatorRoot;
        [SerializeField, Tooltip("Moving floor registered on the elevator root. Fixed elevators board this floor; raw elevators keep it register-only.")]
        private NetSyncMovingFloor _movingFloor;
        [SerializeField, Tooltip("Trigger volume that determines whether the rig is on the elevator. World AABB is polled each frame against the rig position.")]
        private BoxCollider _triggerVolume;

        [Header("Boarding Visual")]
        [SerializeField, Tooltip("Renderer whose base color flips to BoundColor while the local rig is bound to this platform. Defaults to a MeshRenderer on the elevator root.")]
        private Renderer _glowRenderer;
        [SerializeField, Tooltip("Color swapped onto the elevator's material while moving floor binding is in effect.")]
        private Color _boundColor = new Color(0.2f, 0.5f, 1f, 1f);

        private const string PlayerTag = "Player";

        private Transform _localRig;
        private int _playerColliderCount;
        private bool _boarded;
        // Elevator pose at the previous LateUpdate. While boarded we apply the
        // per-frame delta to the rig's world pose so it rides along WITHOUT
        // making the rig a child of the elevator. SetParent introduces a
        // ~1e-6 m world-position drift from the worldPositionStays inverse-
        // matrix recompute, which fires spurious OnTriggerExit/Enter pairs
        // right at the trigger boundary.
        private Vector3 _prevElevatorPos;
        private Quaternion _prevElevatorRot;
        private bool _hasPrevElevatorPose;
        private Material _matInstance;
        private Color _originalColor;
        private static readonly int _baseColorIdURP = Shader.PropertyToID("_BaseColor");
        private static readonly int _baseColorIdLegacy = Shader.PropertyToID("_Color");

        public bool IsBoarded => _boarded;
        public uint PlatformId => _movingFloor != null ? _movingFloor.FloorId : 0u;
        public string PlatformIdHex => _movingFloor != null ? _movingFloor.FloorIdHex : "";
        public bool AppliesPlatformBinding => _applyPlatformBinding;
        public GameObject ElevatorRoot => _elevatorRoot;

        private void Start()
        {
            if (_elevatorRoot == null) { _elevatorRoot = transform.parent != null ? transform.parent.gameObject : gameObject; }
            if (_triggerVolume == null) { _triggerVolume = GetComponent<BoxCollider>(); }

            var xrOrigin = FindFirstObjectByType<XROrigin>();
            if (xrOrigin == null)
            {
                UnityEngine.Debug.LogError("[PlatformBindingTest] ElevatorBoardingZone could not find an XROrigin in the scene.");
                enabled = false;
                return;
            }
            _localRig = xrOrigin.transform;

            EnsureMovingFloor();

            if (_glowRenderer == null && _elevatorRoot != null)
            {
                _glowRenderer = _elevatorRoot.GetComponent<Renderer>();
            }
            if (_glowRenderer != null)
            {
                // Instance the material so color swaps don't bleed back to the
                // asset or other elevators sharing it.
                _matInstance = _glowRenderer.material;
                _originalColor = _matInstance.color;
            }
        }

        private void OnDisable()
        {
            // Releasing the binding on disable keeps the receiver in a clean
            // state if the scene is unloaded mid-ride.
            if (_boarded) { Unboard(); }
        }

        private void OnDestroy()
        {
            if (_matInstance != null) { Destroy(_matInstance); }
        }

        private void OnTriggerEnter(Collider other)
        {
            if (!other.CompareTag(PlayerTag)) { return; }
            _playerColliderCount++;
            if (_playerColliderCount == 1 && !_boarded) { Board(); }
        }

        private void OnTriggerExit(Collider other)
        {
            if (!other.CompareTag(PlayerTag)) { return; }
            _playerColliderCount = Mathf.Max(0, _playerColliderCount - 1);
            if (_playerColliderCount == 0 && _boarded) { Unboard(); }
        }

        private void Board()
        {
            _boarded = true;
            _hasPrevElevatorPose = false; // snapshot on next LateUpdate

            if (_applyPlatformBinding && EnsureMovingFloor())
            {
                _movingFloor.BoardLocalAvatar();
            }
            SetGlow(true);
            UnityEngine.Debug.Log($"[PlatformBindingTest] Boarded {_elevatorRoot.name} (binding={_applyPlatformBinding}).");
        }

        private void Unboard()
        {
            _boarded = false;
            _hasPrevElevatorPose = false;

            if (_applyPlatformBinding && _movingFloor != null)
            {
                _movingFloor.LeaveLocalAvatar();
            }
            SetGlow(false);
            UnityEngine.Debug.Log($"[PlatformBindingTest] Unboarded {_elevatorRoot.name}.");
        }

        private void LateUpdate()
        {
            if (!_boarded || _localRig == null || _elevatorRoot == null) { return; }
            var pos = _elevatorRoot.transform.position;
            var rot = _elevatorRoot.transform.rotation;
            if (_hasPrevElevatorPose)
            {
                // Apply elevator's per-frame motion to the rig so it rides along.
                // Translation: add positional delta. Rotation: rotate the rig
                // around the elevator's pivot by the rotational delta.
                var rotDelta = rot * Quaternion.Inverse(_prevElevatorRot);
                var rigOffsetFromPrev = _localRig.position - _prevElevatorPos;
                _localRig.position = pos + rotDelta * rigOffsetFromPrev;
                _localRig.rotation = rotDelta * _localRig.rotation;
            }
            _prevElevatorPos = pos;
            _prevElevatorRot = rot;
            _hasPrevElevatorPose = true;
        }

        private void SetGlow(bool on)
        {
            if (_matInstance == null) { return; }
            // Only swap while the platform binding is actually active. Riding the
            // Raw elevator boards the rig but does not call BoardLocalAvatar,
            // so there is no binding to indicate.
            var swap = on && _applyPlatformBinding;
            var color = swap ? _boundColor : _originalColor;
            // URP/Lit uses _BaseColor; the Standard / Built-in fallback is _Color.
            // Material.color setter handles the right one for the bound shader,
            // but we set both to be safe across SRP variants.
            _matInstance.color = color;
            if (_matInstance.HasProperty(_baseColorIdURP)) { _matInstance.SetColor(_baseColorIdURP, color); }
            if (_matInstance.HasProperty(_baseColorIdLegacy)) { _matInstance.SetColor(_baseColorIdLegacy, color); }
        }

        private bool EnsureMovingFloor()
        {
            if (_movingFloor != null)
            {
                return true;
            }

            if (_elevatorRoot != null)
            {
                _movingFloor = _elevatorRoot.GetComponent<NetSyncMovingFloor>();
            }

            if (_movingFloor != null)
            {
                return true;
            }

            UnityEngine.Debug.LogWarning("[PlatformBindingTest] ElevatorBoardingZone requires a NetSyncMovingFloor on the elevator root.", this);
            return false;
        }
    }
}
