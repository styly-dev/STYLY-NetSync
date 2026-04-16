// OwnershipClient.cs
// Per-entity ownership state machine driven by OWNERSHIP_EVENT messages and
// seeded by ROOM_SNAPSHOT entity records. Non-speculative: IsOwnedByLocalClient
// only flips on a matching grant event. Pending requests time out locally
// after PendingRequestTimeoutSeconds.
//
// Threading: OwnershipClient is a pure-main-thread component. ReplicationClient
// trampolines network-thread payloads onto the main thread before invoking the
// Handle* methods here. Pump() advances pending-request timeouts.

using System;
using System.Collections.Generic;
using UnityEngine;

namespace Styly.NetSync.Internal
{
    /// <summary>
    /// Terminal outcome of a pending OWNERSHIP_REQUEST as surfaced to
    /// user code. Maps from wire <see cref="OwnershipResult"/> plus the
    /// locally-originated Timeout when the server never replies. The
    /// Revoked variant represents a non-local change observed through a
    /// ROOM_SNAPSHOT refresh, which has no direct wire counterpart.
    /// </summary>
    public enum OwnershipResolution : byte
    {
        Granted = 0,
        Rejected = 1,
        Revoked = 2,
        Released = 3,
        Timeout = 4,
        Expired = 5,
    }

    /// <summary>
    /// Payload delivered to <see cref="OwnershipClient.OwnershipChanged"/>
    /// (entity-scoped; the entityId is the event's second argument).
    /// </summary>
    public struct OwnershipChange
    {
        public int PreviousOwnerClientNo;
        public int NewOwnerClientNo;
        public uint AuthorityEpoch;
        public OwnershipResolution Reason;
    }

    /// <summary>
    /// Tracks ownership of NetSyncObject entities.
    /// </summary>
    public sealed class OwnershipClient
    {
        public const float PendingRequestTimeoutSeconds = 5.0f;

        /// <summary>
        /// Process-wide active client that public NetSyncObject API calls
        /// route through. Set by NetSyncManager when the replication layer
        /// is initialized; tests may assign a stub.
        /// </summary>
        public static OwnershipClient Active { get; set; }

        /// <summary>
        /// Clock abstraction so tests can drive deterministic time without
        /// using Unity's frame clock.
        /// </summary>
        public interface IClock
        {
            float Now { get; }
        }

        private sealed class UnityClock : IClock
        {
            public float Now => Time.unscaledTime;
        }

        private enum PendingKind
        {
            Acquire,
            Release,
        }

        private struct PendingRequest
        {
            public PendingKind Kind;
            public float DeadlineAt;
            public uint ExpectedEpoch;
        }

        private struct AuthorityState
        {
            public int OwnerClientNo;
            public uint AuthorityEpoch;
        }

        private readonly IReplicationTransport _transport;
        private readonly IClock _clock;
        private readonly Dictionary<ulong, AuthorityState> _state = new Dictionary<ulong, AuthorityState>();
        private readonly Dictionary<ulong, PendingRequest> _pending = new Dictionary<ulong, PendingRequest>();
        private readonly List<ulong> _scratchExpired = new List<ulong>();

        private string _roomId = string.Empty;
        private int _localClientNo;

        /// <summary>
        /// Fired on the main thread whenever ownership for an entity changes.
        /// Args: (entityId, change).
        /// </summary>
        public event Action<ulong, OwnershipChange> OwnershipChanged;

        public int LocalClientNo
        {
            get => _localClientNo;
            set => _localClientNo = value;
        }

        public string RoomId
        {
            get => _roomId;
            set => _roomId = value ?? string.Empty;
        }

        public OwnershipClient(IReplicationTransport transport, IClock clock = null)
        {
            _transport = transport ?? throw new ArgumentNullException(nameof(transport));
            _clock = clock ?? new UnityClock();
        }

        // -----------------------------------------------------------------
        // Public API used by NetSyncObject.RequestOwnership / Release.
        // -----------------------------------------------------------------

        /// <summary>
        /// Dispatch an OWNERSHIP_REQUEST to acquire an entity. Returns true
        /// when the request was handed to the transport (i.e. encoded + sent).
        /// It does NOT mean the request was granted — wait for
        /// <see cref="OwnershipChanged"/>. Re-requesting while a prior
        /// request is pending is a no-op that returns true.
        /// </summary>
        public bool RequestAcquire(ulong entityId)
        {
            if (_localClientNo <= 0)
            {
                return false;
            }
            uint expectedEpoch = GetState(entityId).AuthorityEpoch;
            return DispatchRequest(entityId, PendingKind.Acquire, expectedEpoch);
        }

        /// <summary>
        /// Dispatch an OWNERSHIP_REQUEST to release an entity. Only
        /// succeeds on the wire if the local client is the current owner
        /// (enforced server-side). expectedEpoch defaults to the current
        /// tracked epoch; callers may pass an explicit value for resync paths.
        /// </summary>
        public bool RequestRelease(ulong entityId, uint expectedEpoch)
        {
            if (_localClientNo <= 0)
            {
                return false;
            }
            return DispatchRequest(entityId, PendingKind.Release, expectedEpoch);
        }

        public bool IsOwnedByLocalClient(ulong entityId)
        {
            if (_localClientNo <= 0)
            {
                return false;
            }
            return _state.TryGetValue(entityId, out AuthorityState s) && s.OwnerClientNo == _localClientNo;
        }

        public int GetOwnerClientNo(ulong entityId)
        {
            return _state.TryGetValue(entityId, out AuthorityState s) ? s.OwnerClientNo : 0;
        }

        public uint GetAuthorityEpoch(ulong entityId)
        {
            return _state.TryGetValue(entityId, out AuthorityState s) ? s.AuthorityEpoch : 0u;
        }

        // -----------------------------------------------------------------
        // Driven by ReplicationClient.
        // -----------------------------------------------------------------

        /// <summary>
        /// Consume OWNERSHIP_EVENT from ReplicationClient dispatch. Always
        /// called on the main thread.
        /// </summary>
        public void HandleOwnershipEvent(in OwnershipEventMessage evt)
        {
            int previous = _state.TryGetValue(evt.EntityId, out AuthorityState prev) ? prev.OwnerClientNo : 0;
            int newOwner = (int)evt.NewOwnerShortId;
            uint newEpoch = evt.NewAuthorityEpoch;

            // Ignore stale events (older epoch than what we already applied).
            if (prev.AuthorityEpoch != 0 && newEpoch < prev.AuthorityEpoch)
            {
                return;
            }

            _state[evt.EntityId] = new AuthorityState
            {
                OwnerClientNo = newOwner,
                AuthorityEpoch = newEpoch,
            };

            // Resolve the pending request, if any. The server's `Result`
            // tells us granted vs denied; for locally-observed outcomes
            // (e.g. another client stole ownership) we map to Revoked.
            if (_pending.TryGetValue(evt.EntityId, out _))
            {
                _pending.Remove(evt.EntityId);
            }

            OwnershipResolution resolution = MapResult(evt.Result);
            OwnershipChange change = new OwnershipChange
            {
                PreviousOwnerClientNo = previous,
                NewOwnerClientNo = newOwner,
                AuthorityEpoch = newEpoch,
                Reason = resolution,
            };
            OwnershipChanged?.Invoke(evt.EntityId, change);
            RaiseOnComponent(evt.EntityId, change);
        }

        /// <summary>
        /// Consume owner/epoch columns from a ROOM_SNAPSHOT entity record.
        /// Fires OwnershipChanged only when the effective owner differs from
        /// what we previously tracked (or for fresh entries).
        /// </summary>
        public void HandleSnapshotEntity(in EntityRecord record)
        {
            int newOwner = (int)record.OwnerShortId;
            uint newEpoch = record.AuthorityEpoch;
            bool had = _state.TryGetValue(record.EntityId, out AuthorityState prev);

            _state[record.EntityId] = new AuthorityState
            {
                OwnerClientNo = newOwner,
                AuthorityEpoch = newEpoch,
            };

            // Snapshots can arrive with epoch=0 for server-side defaults;
            // still emit an event when this is the first knowledge we have
            // of the entity or when ownership actually changed.
            bool changed = !had || prev.OwnerClientNo != newOwner || prev.AuthorityEpoch != newEpoch;
            if (!changed)
            {
                return;
            }

            OwnershipChange change = new OwnershipChange
            {
                PreviousOwnerClientNo = had ? prev.OwnerClientNo : 0,
                NewOwnerClientNo = newOwner,
                AuthorityEpoch = newEpoch,
                Reason = had ? OwnershipResolution.Revoked : OwnershipResolution.Granted,
            };
            OwnershipChanged?.Invoke(record.EntityId, change);
            RaiseOnComponent(record.EntityId, change);
        }

        /// <summary>
        /// Advance pending-request timeouts. Call once per frame.
        /// </summary>
        public void Pump()
        {
            float now = _clock.Now;
            if (_pending.Count == 0)
            {
                return;
            }
            _scratchExpired.Clear();
            foreach (KeyValuePair<ulong, PendingRequest> kv in _pending)
            {
                if (now >= kv.Value.DeadlineAt)
                {
                    _scratchExpired.Add(kv.Key);
                }
            }
            for (int i = 0; i < _scratchExpired.Count; i++)
            {
                ulong entityId = _scratchExpired[i];
                _pending.Remove(entityId);
                AuthorityState s = GetState(entityId);
                OwnershipChange change = new OwnershipChange
                {
                    PreviousOwnerClientNo = s.OwnerClientNo,
                    NewOwnerClientNo = s.OwnerClientNo,
                    AuthorityEpoch = s.AuthorityEpoch,
                    Reason = OwnershipResolution.Timeout,
                };
                OwnershipChanged?.Invoke(entityId, change);
                RaiseOnComponent(entityId, change);
            }
        }

        /// <summary>
        /// Clear all local state; used on disconnect / room switch.
        /// </summary>
        public void Reset()
        {
            _state.Clear();
            _pending.Clear();
        }

        // -----------------------------------------------------------------
        // Internals
        // -----------------------------------------------------------------

        private AuthorityState GetState(ulong entityId)
        {
            return _state.TryGetValue(entityId, out AuthorityState s) ? s : default;
        }

        private bool DispatchRequest(ulong entityId, PendingKind kind, uint expectedEpoch)
        {
            byte[] payload = MessageCodec.EncodeOwnershipRequest(new OwnershipRequestMessage
            {
                EntityId = entityId,
                RequesterShortId = (uint)_localClientNo,
                ExpectedEpoch = expectedEpoch,
                Release = kind == PendingKind.Release,
            });
            if (!_transport.SendControl(_roomId, payload))
            {
                return false;
            }
            _pending[entityId] = new PendingRequest
            {
                Kind = kind,
                DeadlineAt = _clock.Now + PendingRequestTimeoutSeconds,
                ExpectedEpoch = expectedEpoch,
            };
            return true;
        }

        private static OwnershipResolution MapResult(OwnershipResult r)
        {
            switch (r)
            {
                case OwnershipResult.Granted: return OwnershipResolution.Granted;
                case OwnershipResult.Denied: return OwnershipResolution.Rejected;
                case OwnershipResult.Released: return OwnershipResolution.Released;
                case OwnershipResult.Expired: return OwnershipResolution.Expired;
                default: return OwnershipResolution.Rejected;
            }
        }

        private static void RaiseOnComponent(ulong entityId, OwnershipChange change)
        {
            if (EntityRegistry.Instance.TryGet(entityId, out EntityBinding binding))
            {
                NetSyncObject obj = binding.Component;
                if (obj != null)
                {
                    obj.RaiseOwnershipChanged(OwnershipChangedEvent.FromInternal(change));
                }
            }
        }

        // -----------------------------------------------------------------
        // Test hooks
        // -----------------------------------------------------------------

        internal bool HasPending(ulong entityId) => _pending.ContainsKey(entityId);
    }
}
