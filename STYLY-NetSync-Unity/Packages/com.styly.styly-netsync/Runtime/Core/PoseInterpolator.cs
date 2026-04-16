// PoseInterpolator.cs
// Drives remote (non-owner) NetSyncObject transforms from a PoseBuffer.
// Renders at currentServerTime - interpolationBackTimeSec and linearly
// interpolates between flanking samples. Handles teleports and missing
// samples by snapping.

using System;
using System.Collections.Generic;
using UnityEngine;

namespace Styly.NetSync.Internal
{
    /// <summary>
    /// Monotonic server-time source. Unit tests inject a fake.
    /// </summary>
    public interface IServerClock
    {
        ulong NowUs { get; }
    }

    /// <summary>
    /// Runtime defaults; may move into ReplicationProfile once protocol
    /// stabilizes. Keeping them here avoids bumping ProfileVersion
    /// (which would invalidate every SceneHash in the wild).
    /// </summary>
    public static class PoseTuning
    {
        public const float InterpolationBackTimeSec = 0.1f;
        public const float TeleportDistanceMeters = 5.0f;
        public const float IdleHeartbeatSec = 1.0f;
    }

    public sealed class PoseInterpolator
    {
        private readonly PoseBuffer _buffer;
        private readonly IServerClock _clock;

        /// <summary>
        /// Predicate that indicates whether the local client owns a given
        /// entity (and should therefore skip interpolation). Provided by
        /// the caller so tests can stub without pulling in OwnershipClient.
        /// </summary>
        public Func<ulong, bool> IsOwnedLocally { get; set; } = _ => false;

        /// <summary>
        /// Returns true once the client is fully joined and interpolation
        /// may mutate transforms.
        /// </summary>
        public Func<bool> IsJoined { get; set; } = () => true;

        public float InterpolationBackTimeSec { get; set; } = PoseTuning.InterpolationBackTimeSec;
        public float TeleportDistanceMeters { get; set; } = PoseTuning.TeleportDistanceMeters;

        public PoseInterpolator(PoseBuffer buffer, IServerClock clock)
        {
            _buffer = buffer ?? throw new ArgumentNullException(nameof(buffer));
            _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        }

        /// <summary>
        /// Advance interpolation for every entity with buffered samples.
        /// Call once per main-thread frame.
        /// </summary>
        public void Tick()
        {
            if (IsJoined != null && !IsJoined())
            {
                return;
            }

            ulong nowUs = _clock.NowUs;
            ulong backUs = (ulong)Mathf.Max(0, (int)(InterpolationBackTimeSec * 1_000_000f));
            ulong renderTimeUs = nowUs > backUs ? nowUs - backUs : 0UL;

            // Snapshot the key set to avoid "collection modified" if
            // ApplyToEntity triggers something that mutates the buffer.
            List<ulong> entityIds = new List<ulong>();
            foreach (ulong id in _buffer.Entities)
            {
                entityIds.Add(id);
            }

            EntityRegistry registry = EntityRegistry.Instance;
            for (int i = 0; i < entityIds.Count; i++)
            {
                ulong entityId = entityIds[i];
                if (IsOwnedLocally != null && IsOwnedLocally(entityId))
                {
                    continue;
                }
                if (!registry.TryGet(entityId, out EntityBinding binding))
                {
                    continue;
                }
                NetSyncObject obj = binding.Component;
                if (obj == null)
                {
                    continue;
                }
                ApplyToEntity(obj, entityId, renderTimeUs);
            }
        }

        private void ApplyToEntity(NetSyncObject obj, ulong entityId, ulong renderTimeUs)
        {
            IReadOnlyList<PoseSample> samples = _buffer.GetSamples(entityId);
            if (samples.Count == 0)
            {
                return;
            }

            // Find the pair of samples that bracket renderTimeUs.
            int idxBefore = -1;
            int idxAfter = -1;
            for (int i = 0; i < samples.Count; i++)
            {
                if (samples[i].ServerTimeUs < renderTimeUs)
                {
                    idxBefore = i;
                }
                else
                {
                    idxAfter = i;
                    break;
                }
            }

            PoseSample chosen;
            if (idxBefore >= 0 && idxAfter >= 0)
            {
                PoseSample a = samples[idxBefore];
                PoseSample b = samples[idxAfter];

                // Teleport: snap to newer sample, do not lerp.
                if ((b.Flags & StateFlags.Teleport) != 0 ||
                    Vector3.Distance(a.State.Position, b.State.Position) > TeleportDistanceMeters)
                {
                    ApplyState(obj, b.State, b.Mask);
                    _buffer.MarkApplied(entityId, b.PoseSeq);
                    return;
                }
                else
                {
                    double span = b.ServerTimeUs - (double)a.ServerTimeUs;
                    float t = span > 0.0 ? (float)((renderTimeUs - (double)a.ServerTimeUs) / span) : 1f;
                    if (t < 0f) t = 0f; else if (t > 1f) t = 1f;

                    TransformState mixed = new TransformState
                    {
                        Position = Vector3.Lerp(a.State.Position, b.State.Position, t),
                        Rotation = Quaternion.Slerp(a.State.Rotation, b.State.Rotation, t),
                        Scale = Vector3.Lerp(a.State.Scale, b.State.Scale, t),
                    };
                    ApplyState(obj, mixed, b.Mask | a.Mask);
                    _buffer.MarkApplied(entityId, b.PoseSeq);
                    return;
                }
            }
            else if (idxBefore >= 0)
            {
                // Render time is past the newest sample — snap to newest.
                chosen = samples[idxBefore];
            }
            else
            {
                // Render time is before the oldest sample — snap to oldest.
                chosen = samples[idxAfter >= 0 ? idxAfter : 0];
            }

            ApplyState(obj, chosen.State, chosen.Mask);
            _buffer.MarkApplied(entityId, chosen.PoseSeq);
        }

        private static void ApplyState(NetSyncObject obj, TransformState state, ChangedMask mask)
        {
            Transform t = obj.transform;
            if ((mask & ChangedMask.Position) != 0)
            {
                t.localPosition = state.Position;
            }
            if ((mask & ChangedMask.Rotation) != 0)
            {
                t.localRotation = state.Rotation;
            }
            if ((mask & ChangedMask.Scale) != 0 && obj.Profile.ReplicateScale)
            {
                t.localScale = state.Scale;
            }
        }
    }
}
