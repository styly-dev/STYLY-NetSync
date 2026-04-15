// PosePublisher.cs
// Publishes owned-entity transforms as STATE_BATCH frames. Applies
// deadband + send-rate cap per ReplicationProfile. Coalescing is latest-
// wins per entity: one send per Tick() at most, built from the entity's
// current Transform.
//
// Triggers for a send (evaluated each Tick):
//   * First send after grant                             -> Keyframe
//   * KeyframeInterval elapsed                           -> Keyframe
//   * Position / Rotation / Scale deltas beyond deadband -> Delta
//   * IdleHeartbeat elapsed with no delta                -> Heartbeat (no
//                                                           transform write)
//   * Distance beyond PoseTuning.TeleportDistanceMeters  -> Teleport

using System;
using System.Collections.Generic;
using UnityEngine;

namespace Styly.NetSync.Internal
{
    /// <summary>
    /// Abstraction over the authority layer for PosePublisher. Tests use
    /// a stub so they don't need a full OwnershipClient wiring.
    /// </summary>
    public interface IAuthorityView
    {
        /// <summary>
        /// Enumerate entity ids the local client currently owns.
        /// </summary>
        IReadOnlyList<ulong> OwnedEntityIds { get; }

        bool TryGetAuthority(ulong entityId, out int ownerClientNo, out uint authorityEpoch);
    }

    public sealed class PosePublisher
    {
        private struct EntityState
        {
            public TransformState LastSent;
            public float LastSentAt;
            public float LastKeyframeAt;
            public ushort PoseSeq;
            public bool HasSent;
        }

        private readonly IReplicationTransport _transport;
        private readonly IAuthorityView _authority;
        private readonly IServerClock _clock;

        private readonly Dictionary<ulong, EntityState> _perEntity = new Dictionary<ulong, EntityState>();
        private readonly List<StateUpdate> _batchScratch = new List<StateUpdate>();
        private string _roomId = string.Empty;

        /// <summary>
        /// Wall-clock seconds supplier. Tests inject a fake clock.
        /// </summary>
        public Func<float> NowSec { get; set; } = () => Time.unscaledTime;

        public string RoomId
        {
            get => _roomId;
            set => _roomId = value ?? string.Empty;
        }

        public PosePublisher(IReplicationTransport transport, IAuthorityView authority, IServerClock clock)
        {
            _transport = transport ?? throw new ArgumentNullException(nameof(transport));
            _authority = authority ?? throw new ArgumentNullException(nameof(authority));
            _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        }

        /// <summary>
        /// Clear internal per-entity history. Use on room change.
        /// </summary>
        public void Reset()
        {
            _perEntity.Clear();
        }

        /// <summary>
        /// Drop history for a single entity (e.g. on ownership loss).
        /// </summary>
        public void ClearEntity(ulong entityId)
        {
            _perEntity.Remove(entityId);
        }

        /// <summary>
        /// Build and send a single STATE_BATCH containing one update per
        /// owned entity that satisfies a send condition. No queuing.
        /// </summary>
        public int Tick()
        {
            IReadOnlyList<ulong> owned = _authority.OwnedEntityIds;
            if (owned == null || owned.Count == 0)
            {
                return 0;
            }

            EntityRegistry registry = EntityRegistry.Instance;
            float now = NowSec != null ? NowSec() : 0f;

            _batchScratch.Clear();
            for (int i = 0; i < owned.Count; i++)
            {
                ulong id = owned[i];
                if (!registry.TryGet(id, out EntityBinding binding))
                {
                    continue;
                }
                NetSyncObject obj = binding.Component;
                if (obj == null)
                {
                    continue;
                }

                if (TryBuildUpdate(obj, id, now, out StateUpdate update))
                {
                    _batchScratch.Add(update);
                }
            }

            if (_batchScratch.Count == 0)
            {
                return 0;
            }

            byte[] payload = MessageCodec.EncodeStateBatch(new StateBatchMessage
            {
                RoomSeq = 0, // client-originated batch; server stamps its own RoomSeq on relay.
                ServerTimeUs = 0, // server stamps on relay; client does not fill this.
                Updates = new List<StateUpdate>(_batchScratch),
            }, TransformCodecV1.Instance);

            _transport.SendControl(_roomId, payload);
            return _batchScratch.Count;
        }

        private bool TryBuildUpdate(NetSyncObject obj, ulong entityId, float now, out StateUpdate update)
        {
            update = default;

            ReplicationProfile profile = obj.Profile;
            float sendInterval = profile.SendRateHz > 0 ? 1f / profile.SendRateHz : 1f / 20f;
            float keyframeInterval = profile.KeyframeIntervalHz > 0 ? 1f / profile.KeyframeIntervalHz : 1f;

            Transform t = obj.transform;
            Vector3 pos = t.localPosition;
            Quaternion rot = t.localRotation;
            Vector3 scale = t.localScale;

            if (!_perEntity.TryGetValue(entityId, out EntityState st))
            {
                st = new EntityState();
            }

            bool firstSend = !st.HasSent;
            float sinceLastSend = now - st.LastSentAt;
            float sinceLastKey = now - st.LastKeyframeAt;

            // Compute deltas vs last sent state.
            float posDelta = firstSend ? float.MaxValue : Vector3.Distance(pos, st.LastSent.Position);
            float rotDelta = firstSend ? 180f : Quaternion.Angle(rot, st.LastSent.Rotation);
            float scaleDelta = firstSend ? float.MaxValue : Vector3.Distance(scale, st.LastSent.Scale);

            bool posMoved = posDelta > profile.PositionDeadband;
            bool rotMoved = rotDelta > profile.RotationDeadbandDeg;
            bool scaleMoved = profile.ReplicateScale && scaleDelta > profile.ScaleDeadband;
            bool anyMoved = posMoved || rotMoved || scaleMoved;

            bool keyframeDue = firstSend || sinceLastKey >= keyframeInterval;
            bool teleport = !firstSend && posDelta > PoseTuning.TeleportDistanceMeters;
            bool heartbeatDue = !anyMoved && !keyframeDue &&
                                !firstSend && sinceLastSend >= PoseTuning.IdleHeartbeatSec;

            // Send-rate cap: skip non-keyframe, non-heartbeat motion updates
            // until the rate window elapses.
            if (!keyframeDue && !heartbeatDue && anyMoved && sinceLastSend < sendInterval)
            {
                _perEntity[entityId] = st;
                return false;
            }

            if (!keyframeDue && !heartbeatDue && !anyMoved)
            {
                _perEntity[entityId] = st;
                return false;
            }

            StateFlags flags = StateFlags.None;
            ChangedMask mask = ChangedMask.None;
            TransformState wireState = st.LastSent;

            if (keyframeDue)
            {
                flags |= StateFlags.Keyframe;
                mask |= ChangedMask.Position | ChangedMask.Rotation;
                if (profile.ReplicateScale)
                {
                    mask |= ChangedMask.Scale;
                }
                wireState.Position = pos;
                wireState.Rotation = rot;
                wireState.Scale = scale;
                st.LastKeyframeAt = now;
            }
            else if (heartbeatDue)
            {
                flags |= StateFlags.Heartbeat;
                // Heartbeat carries no transform payload.
            }
            else
            {
                // Delta: include only moved fields.
                if (posMoved) { mask |= ChangedMask.Position; wireState.Position = pos; }
                if (rotMoved) { mask |= ChangedMask.Rotation; wireState.Rotation = rot; }
                if (scaleMoved) { mask |= ChangedMask.Scale; wireState.Scale = scale; }
            }

            if (teleport)
            {
                flags |= StateFlags.Teleport;
            }

            _authority.TryGetAuthority(entityId, out _, out uint epoch);
            st.PoseSeq = unchecked((ushort)(st.PoseSeq + 1));

            // Update last-sent cache only for fields we actually put on the wire.
            // Heartbeats do not change LastSent.
            if ((flags & StateFlags.Heartbeat) == 0)
            {
                if ((mask & ChangedMask.Position) != 0) st.LastSent.Position = pos;
                if ((mask & ChangedMask.Rotation) != 0) st.LastSent.Rotation = rot;
                if ((mask & ChangedMask.Scale) != 0) st.LastSent.Scale = scale;
            }
            st.LastSentAt = now;
            st.HasSent = true;
            _perEntity[entityId] = st;

            update = new StateUpdate
            {
                EntityId = entityId,
                AuthorityEpoch = epoch,
                PoseSeq = st.PoseSeq,
                Flags = flags,
                ChangedMask = mask,
                State = wireState,
            };
            return true;
        }

        // Test helpers.
        internal bool HasEntityState(ulong id) => _perEntity.ContainsKey(id);
        internal ushort LastPoseSeq(ulong id) => _perEntity.TryGetValue(id, out var s) ? s.PoseSeq : (ushort)0;
    }
}
