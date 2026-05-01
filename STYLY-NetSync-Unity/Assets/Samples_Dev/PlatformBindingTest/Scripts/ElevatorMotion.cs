// ElevatorMotion.cs
using UnityEngine;

namespace Styly.NetSync.Samples.PlatformBindingTest
{
    /// <summary>
    /// Deterministic 3-phase platform motion used by the issue #425 sample.
    ///   Phase 1: x+ <see cref="Distance"/> over <see cref="PhaseSeconds"/>.
    ///   Phase 2: y+ <see cref="Distance"/> over <see cref="PhaseSeconds"/>.
    ///   Phase 3: linear return to origin over <see cref="PhaseSeconds"/>.
    /// All clients run the same calculation against a shared start time so the
    /// motion is locally real-time on every receiver — that is the precondition
    /// the platform-binding fix relies on.
    /// </summary>
    public class ElevatorMotion : MonoBehaviour
    {
        public const float Distance = 100f;
        public const float PhaseSeconds = 3f;
        public const float TotalSeconds = PhaseSeconds * 3f;

        private Vector3 _origin;
        private bool _moving;
        private float _startTime;

        public bool IsMoving => _moving;
        public Vector3 Origin => _origin;

        private void Awake()
        {
            _origin = transform.position;
        }

        public void StartMotion(float startTime)
        {
            _origin = transform.position;
            _startTime = startTime;
            _moving = true;
        }

        private void Update()
        {
            if (!_moving) { return; }

            var elapsed = Time.time - _startTime;
            if (elapsed < 0f) { return; }

            Vector3 offset;
            if (elapsed <= PhaseSeconds)
            {
                var a = elapsed / PhaseSeconds;
                offset = new Vector3(Distance * a, 0f, 0f);
            }
            else if (elapsed <= 2f * PhaseSeconds)
            {
                var a = (elapsed - PhaseSeconds) / PhaseSeconds;
                offset = new Vector3(Distance, Distance * a, 0f);
            }
            else if (elapsed <= 3f * PhaseSeconds)
            {
                var a = (elapsed - 2f * PhaseSeconds) / PhaseSeconds;
                offset = Vector3.Lerp(new Vector3(Distance, Distance, 0f), Vector3.zero, a);
            }
            else
            {
                offset = Vector3.zero;
                _moving = false;
            }

            transform.position = _origin + offset;
        }
    }
}
