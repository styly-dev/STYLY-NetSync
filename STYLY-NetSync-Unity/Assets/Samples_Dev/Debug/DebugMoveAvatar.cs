using UnityEngine;

namespace Styly.NetSync.Debug
{
    public enum MovementPattern
    {
        Circle,
        Figure8,
        RandomWalk,
        LinearPingPong,
        Spiral
    }

    public class DebugMoveAvatar : MonoBehaviour
    {
        [Header("Avatar Parts")]
        [SerializeField] private Transform _head;
        [SerializeField] private Transform _rightHand;
        [SerializeField] private Transform _leftHand;
        [SerializeField] private Transform[] _virtualTransforms;

        [Header("Movement Settings")]
        [SerializeField] private float _moveSpeed = 2f;
        [SerializeField] private float _movementRadius = 3f;
        [SerializeField] private float _handSwingAmplitude = 0.5f;
        [SerializeField] private float _handSwingSpeed = 3f;

        [Header("Virtual Items Settings")]
        [SerializeField] private float _virtualItemOrbitSpeed = 1f;
        [SerializeField] private float _virtualItemOrbitRadius = 1f;
        [SerializeField] private float _virtualItemVerticalRange = 0.5f;

        [Header("Random Walk Settings")]
        [SerializeField] private float _randomWalkChangeInterval = 2f;
        [SerializeField] private float _randomWalkRange = 5f;

        private MovementPattern _currentPattern;
        private Vector3 _startLocalPosition;
        private Vector3 _currentTarget;
        private float _time;
        private float _randomWalkTimer;
        private Vector3[] _virtualItemInitialOffsets;
        private float[] _virtualItemOrbitPhases;
        private float[] _virtualItemOrbitSpeeds;
        private float[] _virtualItemOrbitRadii;

        void Start()
        {
            // Randomly select a movement pattern
            _currentPattern = (MovementPattern)Random.Range(0, System.Enum.GetValues(typeof(MovementPattern)).Length);
            UnityEngine.Debug.Log($"[DebugMoveAvatar] Selected movement pattern: {_currentPattern}");

            // Record the initial local position
            _startLocalPosition = transform.localPosition;
            _currentTarget = _startLocalPosition;

            // Initialize virtual items
            InitializeVirtualItems();
        }

        void InitializeVirtualItems()
        {
            if (_virtualTransforms == null || _virtualTransforms.Length == 0) { return; }

            int count = _virtualTransforms.Length;
            _virtualItemInitialOffsets = new Vector3[count];
            _virtualItemOrbitPhases = new float[count];
            _virtualItemOrbitSpeeds = new float[count];
            _virtualItemOrbitRadii = new float[count];

            for (int i = 0; i < count; i++)
            {
                if (_virtualTransforms[i] != null)
                {
                    // Set different orbit parameters for each item
                    _virtualItemOrbitPhases[i] = (float)i / count * 2f * Mathf.PI;
                    _virtualItemOrbitSpeeds[i] = _virtualItemOrbitSpeed * Random.Range(0.5f, 1.5f);
                    _virtualItemOrbitRadii[i] = _virtualItemOrbitRadius * Random.Range(0.8f, 1.2f);

                    // Record the initial offset in local space
                    _virtualItemInitialOffsets[i] = transform.InverseTransformPoint(_virtualTransforms[i].position);
                }
            }
        }

        void Update()
        {
            _time += Time.deltaTime;

            // Avatar movement
            UpdateAvatarMovement();

            // Hand movement
            UpdateHandMovement();

            // Virtual item orbital movement
            UpdateVirtualItems();
        }

        void UpdateAvatarMovement()
        {
            Vector3 newLocalPosition = _startLocalPosition;

            switch (_currentPattern)
            {
                case MovementPattern.Circle:
                    newLocalPosition = CalculateCircleMovement();
                    break;
                case MovementPattern.Figure8:
                    newLocalPosition = CalculateFigure8Movement();
                    break;
                case MovementPattern.RandomWalk:
                    newLocalPosition = CalculateRandomWalkMovement();
                    break;
                case MovementPattern.LinearPingPong:
                    newLocalPosition = CalculateLinearPingPongMovement();
                    break;
                case MovementPattern.Spiral:
                    newLocalPosition = CalculateSpiralMovement();
                    break;
            }

            // Update the local position of the entire avatar
            transform.localPosition = newLocalPosition;

            // Update head local position (slightly above the avatar's center)
            if (_head != null)
            {
                _head.localPosition = Vector3.up * 1.7f;
            }
        }

        Vector3 CalculateCircleMovement()
        {
            float angle = _time * _moveSpeed;
            return _startLocalPosition + new Vector3(
                Mathf.Cos(angle) * _movementRadius,
                0,
                Mathf.Sin(angle) * _movementRadius
            );
        }

        Vector3 CalculateFigure8Movement()
        {
            float t = _time * _moveSpeed * 0.5f;
            return _startLocalPosition + new Vector3(
                Mathf.Sin(t) * _movementRadius,
                0,
                Mathf.Sin(2 * t) * _movementRadius * 0.5f
            );
        }

        Vector3 CalculateRandomWalkMovement()
        {
            _randomWalkTimer += Time.deltaTime;

            if (_randomWalkTimer >= _randomWalkChangeInterval)
            {
                _randomWalkTimer = 0f;
                Vector3 randomDirection = new Vector3(
                    Random.Range(-1f, 1f),
                    0,
                    Random.Range(-1f, 1f)
                ).normalized;

                _currentTarget = _startLocalPosition + randomDirection * Random.Range(1f, _randomWalkRange);
            }

            return Vector3.MoveTowards(transform.localPosition, _currentTarget, _moveSpeed * Time.deltaTime);
        }

        Vector3 CalculateLinearPingPongMovement()
        {
            float pingPong = Mathf.PingPong(_time * _moveSpeed, 2f) - 1f; // -1 to 1
            return _startLocalPosition + Vector3.forward * pingPong * _movementRadius;
        }

        Vector3 CalculateSpiralMovement()
        {
            float angle = _time * _moveSpeed;
            float radius = (_movementRadius * 0.5f) + Mathf.Sin(_time * 0.5f) * (_movementRadius * 0.5f);
            return _startLocalPosition + new Vector3(
                Mathf.Cos(angle) * radius,
                Mathf.Sin(_time * _moveSpeed * 0.3f) * 0.5f,
                Mathf.Sin(angle) * radius
            );
        }

        void UpdateHandMovement()
        {
            if (_rightHand != null)
            {
                Vector3 rightHandBase = Vector3.right * 0.3f + Vector3.up * 1.2f;
                Vector3 rightHandSwing = new Vector3(
                    Mathf.Sin(_time * _handSwingSpeed) * _handSwingAmplitude,
                    Mathf.Cos(_time * _handSwingSpeed * 0.7f) * _handSwingAmplitude * 0.5f,
                    Mathf.Sin(_time * _handSwingSpeed * 1.3f) * _handSwingAmplitude * 0.3f
                );
                _rightHand.localPosition = rightHandBase + rightHandSwing;
            }

            if (_leftHand != null)
            {
                Vector3 leftHandBase = Vector3.left * 0.3f + Vector3.up * 1.2f;
                Vector3 leftHandSwing = new Vector3(
                    Mathf.Sin(_time * _handSwingSpeed + Mathf.PI) * _handSwingAmplitude,
                    Mathf.Cos(_time * _handSwingSpeed * 0.7f + Mathf.PI) * _handSwingAmplitude * 0.5f,
                    Mathf.Sin(_time * _handSwingSpeed * 1.3f + Mathf.PI) * _handSwingAmplitude * 0.3f
                );
                _leftHand.localPosition = leftHandBase + leftHandSwing;
            }
        }

        void UpdateVirtualItems()
        {
            if (_virtualTransforms == null || _virtualTransforms.Length == 0) { return; }

            Vector3 avatarCenterLocal = Vector3.up * 1f; // Avatar center height in local space

            for (int i = 0; i < _virtualTransforms.Length; i++)
            {
                if (_virtualTransforms[i] != null)
                {
                    // Calculate orbit for each item
                    float currentPhase = _virtualItemOrbitPhases[i] + _time * _virtualItemOrbitSpeeds[i];
                    float radius = _virtualItemOrbitRadii[i];

                    // 3D orbital motion (elliptical orbit + vertical movement)
                    Vector3 orbitPosition = new Vector3(
                        Mathf.Cos(currentPhase) * radius,
                        Mathf.Sin(currentPhase * 2f) * _virtualItemVerticalRange,
                        Mathf.Sin(currentPhase) * radius * 0.7f // to make it elliptical
                    );

                    // Set local position relative to avatar
                    _virtualTransforms[i].localPosition = _virtualItemInitialOffsets[i] + avatarCenterLocal + orbitPosition;

                    // Point the item towards the avatar center
                    Vector3 lookDirection = (avatarCenterLocal - _virtualTransforms[i].localPosition).normalized;
                    if (lookDirection != Vector3.zero)
                    {
                        _virtualTransforms[i].localRotation = Quaternion.LookRotation(lookDirection);
                    }
                }
            }
        }

        // For debugging: manually change movement pattern
        [ContextMenu("Change Movement Pattern")]
        public void ChangeMovementPattern()
        {
            int currentIndex = (int)_currentPattern;
            int nextIndex = (currentIndex + 1) % System.Enum.GetValues(typeof(MovementPattern)).Length;
            _currentPattern = (MovementPattern)nextIndex;

            // Reset local position for the new pattern
            _startLocalPosition = transform.localPosition;
            _currentTarget = _startLocalPosition;
            _time = 0f;

            UnityEngine.Debug.Log($"[DebugMoveAvatar] Changed movement pattern to: {_currentPattern}");
        }

        void OnDrawGizmosSelected()
        {
            // Visualize movement range
            Gizmos.color = Color.yellow;
            Gizmos.DrawWireSphere(transform.position, _movementRadius);

            // Visualize virtual item orbits
            if (_virtualTransforms != null)
            {
                Gizmos.color = Color.cyan;
                Vector3 center = transform.position + Vector3.up * 1f;
                Gizmos.DrawWireSphere(center, _virtualItemOrbitRadius);
            }
        }
    }
}
