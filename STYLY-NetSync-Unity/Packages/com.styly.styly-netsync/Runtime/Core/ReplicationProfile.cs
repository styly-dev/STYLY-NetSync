// ReplicationProfile.cs
// Authoring-time per-object replication configuration.
// Serialized on NetSyncObject; controls sync cadence, interpolation, and channel priority.

using System;
using UnityEngine;

namespace Styly.NetSync
{
    /// <summary>
    /// Transport priority class for a replicated entity.
    /// Higher priority items are sent first under bandwidth contention.
    /// </summary>
    public enum ReplicationPriority
    {
        Low = 0,
        Normal = 1,
        High = 2,
        Critical = 3
    }

    /// <summary>
    /// Interpolation strategy applied by the receiver when reconstructing
    /// entity state between keyframes.
    /// </summary>
    public enum InterpolationMode
    {
        None = 0,
        Linear = 1,
        Hermite = 2
    }

    /// <summary>
    /// Per-NetSyncObject replication profile. Serialized as a struct so it
    /// inlines into the host MonoBehaviour and is editable from the inspector.
    ///
    /// Spec §5.4 "v1 recommended default":
    ///   SendRateHz          = 20
    ///   KeyframeIntervalHz  = 1
    ///   PositionDeadband    = 0.005 m
    ///   RotationDeadbandDeg = 0.5
    ///   ScaleDeadband       = 0.005
    ///   Priority            = Normal
    ///   Interpolation       = Linear
    ///   ReplicateScale      = false
    ///   ProfileVersion      = 1
    /// </summary>
    [Serializable]
    public struct ReplicationProfile : IEquatable<ReplicationProfile>
    {
        [Tooltip("Upper bound for state updates per second. Motion-adaptive filtering applies below this rate.")]
        [Range(1, 60)]
        [SerializeField] private int _sendRateHz;

        [Tooltip("Minimum keyframe broadcast rate in Hz (also acts as heartbeat while idle).")]
        [Range(1, 10)]
        [SerializeField] private int _keyframeIntervalHz;

        [Tooltip("Minimum position delta (meters) before a delta update is sent.")]
        [Min(0f)]
        [SerializeField] private float _positionDeadband;

        [Tooltip("Minimum rotation delta (degrees) before a delta update is sent.")]
        [Min(0f)]
        [SerializeField] private float _rotationDeadbandDeg;

        [Tooltip("Minimum scale delta before a delta update is sent.")]
        [Min(0f)]
        [SerializeField] private float _scaleDeadband;

        [SerializeField] private ReplicationPriority _priority;
        [SerializeField] private InterpolationMode _interpolation;
        [SerializeField] private bool _replicateScale;

        /// <summary>
        /// Version number for the profile schema. Feed into SceneHash so
        /// scene digest flips whenever profile semantics change.
        /// </summary>
        [SerializeField] private int _profileVersion;

        public int SendRateHz => _sendRateHz;
        public int KeyframeIntervalHz => _keyframeIntervalHz;
        public float PositionDeadband => _positionDeadband;
        public float RotationDeadbandDeg => _rotationDeadbandDeg;
        public float ScaleDeadband => _scaleDeadband;
        public ReplicationPriority Priority => _priority;
        public InterpolationMode Interpolation => _interpolation;
        public bool ReplicateScale => _replicateScale;
        public int ProfileVersion => _profileVersion;

        /// <summary>
        /// Returns the v1 recommended default profile. See spec §5.4.
        /// </summary>
        public static ReplicationProfile Default => new ReplicationProfile
        {
            _sendRateHz = 20,
            _keyframeIntervalHz = 1,
            _positionDeadband = 0.005f,
            _rotationDeadbandDeg = 0.5f,
            _scaleDeadband = 0.005f,
            _priority = ReplicationPriority.Normal,
            _interpolation = InterpolationMode.Linear,
            _replicateScale = false,
            _profileVersion = 1
        };

        public bool Equals(ReplicationProfile other)
        {
            return _sendRateHz == other._sendRateHz
                && _keyframeIntervalHz == other._keyframeIntervalHz
                && _positionDeadband.Equals(other._positionDeadband)
                && _rotationDeadbandDeg.Equals(other._rotationDeadbandDeg)
                && _scaleDeadband.Equals(other._scaleDeadband)
                && _priority == other._priority
                && _interpolation == other._interpolation
                && _replicateScale == other._replicateScale
                && _profileVersion == other._profileVersion;
        }

        public override bool Equals(object obj) => obj is ReplicationProfile p && Equals(p);

        public override int GetHashCode()
        {
            unchecked
            {
                int h = 17;
                h = h * 31 + _sendRateHz;
                h = h * 31 + _keyframeIntervalHz;
                h = h * 31 + _positionDeadband.GetHashCode();
                h = h * 31 + _rotationDeadbandDeg.GetHashCode();
                h = h * 31 + _scaleDeadband.GetHashCode();
                h = h * 31 + (int)_priority;
                h = h * 31 + (int)_interpolation;
                h = h * 31 + (_replicateScale ? 1 : 0);
                h = h * 31 + _profileVersion;
                return h;
            }
        }
    }
}
