// ReplicationClient.cs
// Client-side replication pump layered on top of ConnectionManager. Drives
// the join -> snapshot -> state-batch handshake described in
// docs/replication-protocol-v1.md §10.2.
//
// Threading model:
//   - Incoming payloads arrive from the ConnectionManager network thread via
//     OnNetworkThreadPayload. We only enqueue there; no Unity APIs.
//   - Pump() must be called on the Unity main thread (NetSyncManager will do
//     this each frame in Phase 3; unit tests call it directly). All state
//     mutation and transform application happens inside Pump().
//
// Phase 2 scope:
//   - Join sequence + buffered STATE_BATCH replay.
//   - Remote-apply = direct Transform write (no interpolation, no smoothing).
//   - No ownership publishing, no outbound STATE_BATCH.

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using Styly.NetSync;
using UnityEngine;

namespace Styly.NetSync.Internal
{
    /// <summary>
    /// Transport abstraction used by ReplicationClient to send control
    /// messages (currently JOIN_ROOM). Implemented by a small shim over
    /// ConnectionManager in Phase 3; tests use a stub.
    /// </summary>
    public interface IReplicationTransport
    {
        bool SendControl(string roomId, byte[] payload);
    }

    /// <summary>
    /// Public join lifecycle state.
    /// </summary>
    public enum JoinState
    {
        Disconnected = 0,
        Joining = 1,
        Joined = 2,
        Rejected = 3,
    }

    public sealed class ReplicationClient
    {
        private readonly IReplicationTransport _transport;
        private readonly ITransformCodec _codec;

        // Thread-safe inbox for payloads handed off by the network thread.
        // Entries are (firstByte, payload). Drained on Pump().
        private readonly ConcurrentQueue<(byte msgType, byte[] payload)> _inbox =
            new ConcurrentQueue<(byte, byte[])>();

        // Buffer for STATE_BATCH messages that arrive before ROOM_SNAPSHOT.
        // On snapshot apply, we replay entries with RoomSeq > baseRoomSeq.
        private readonly List<StateBatchMessage> _preSnapshotBuffer = new List<StateBatchMessage>();

        // Highest RoomSeq we have already applied. Updated by snapshot apply
        // and by each STATE_BATCH applied post-join. Out-of-order batches
        // with a stale RoomSeq are dropped.
        private uint _highestAppliedRoomSeq;

        private JoinState _state = JoinState.Disconnected;
        private string _pendingRoomId;
        private string _pendingDeviceId;
        private string _pendingSceneHash;
        private JoinRejectReason _lastRejectReason = JoinRejectReason.None;

        /// <summary>
        /// Raised whenever <see cref="State"/> changes. Always raised on the
        /// main thread (inside Pump).
        /// </summary>
        public event Action<JoinState> JoinStateChanged;

        /// <summary>
        /// Raised when an unknown EntityId is encountered in a snapshot or
        /// state batch. Hook for diagnostics. Always raised on the main thread.
        /// </summary>
        public event Action<ulong> UnknownEntityEncountered;

        /// <summary>
        /// Raised on the main thread when the client dispatches a
        /// RESYNC_REQUEST. The argument is the last-applied RoomSeq.
        /// </summary>
        public event Action<uint> OnResyncStarted;

        /// <summary>
        /// Raised on the main thread after a RESYNC_REPLY has been applied.
        /// The argument is the new HighestAppliedRoomSeq.
        /// </summary>
        public event Action<uint> OnResyncCompleted;

        /// <summary>
        /// Resync trigger thresholds. Kept as properties so tests and
        /// tuning can override them without recompiling the codebase.
        /// </summary>
        public uint GapTolerance { get; set; } = 16;
        public int UnknownEntityFloodThreshold { get; set; } = 3;

        /// <summary>
        /// When true, the client has a resync in flight and is waiting for
        /// RESYNC_REPLY. Suppresses additional RESYNC_REQUEST dispatches.
        /// </summary>
        private bool _resyncPending;
        private int _consecutiveUnknownEntities;

        public JoinState State => _state;
        public string RoomId => _pendingRoomId;
        public JoinRejectReason LastRejectReason => _lastRejectReason;
        public uint HighestAppliedRoomSeq => _highestAppliedRoomSeq;

        // LocalClientNo is learned from ROOM_SNAPSHOT.YourClientNo during the
        // join handshake. Tests and specialized integrations may override it
        // via the public setter, but the default path reads it off the wire.
        private int _localClientNo;

        /// <summary>
        /// Optional ownership subsystem. ReplicationClient routes
        /// OWNERSHIP_EVENT and snapshot ownership columns here when set.
        /// </summary>
        public OwnershipClient Ownership { get; set; }

        /// <summary>
        /// Optional remote-pose buffer. When set, STATE_BATCH updates for
        /// non-owned entities are enqueued here instead of being applied
        /// directly. The paired <see cref="Interpolator"/> consumes the
        /// buffer in its Pump step.
        /// </summary>
        public PoseBuffer Buffer { get; set; }

        /// <summary>
        /// Optional interpolator driven by <see cref="Pump"/>.
        /// </summary>
        public PoseInterpolator Interpolator { get; set; }

        /// <summary>
        /// Optional publisher driven by <see cref="Pump"/>.
        /// </summary>
        public PosePublisher Publisher { get; set; }

        /// <summary>
        /// Clock used to stamp incoming STATE_BATCH samples into PoseBuffer.
        /// Until the wire carries per-batch ServerTimeUs, we use the local
        /// server-clock estimate at arrival time.
        /// </summary>
        public IServerClock ServerClock { get; set; }

        private ulong SynthesizeServerTimeUs()
        {
            IServerClock clock = ServerClock;
            return clock != null ? clock.NowUs : 0UL;
        }

        /// <summary>
        /// Network-assigned short id for the local client. Set by
        /// NetSyncManager when the device-id mapping resolves.
        /// </summary>
        public int LocalClientNo
        {
            get => _localClientNo;
            set
            {
                _localClientNo = value;
                OwnershipClient ownership = Ownership;
                if (ownership != null)
                {
                    ownership.LocalClientNo = value;
                }
            }
        }

        public ReplicationClient(IReplicationTransport transport, ITransformCodec codec)
        {
            _transport = transport ?? throw new ArgumentNullException(nameof(transport));
            _codec = codec ?? throw new ArgumentNullException(nameof(codec));
        }

        // -----------------------------------------------------------------
        // Public driver API
        // -----------------------------------------------------------------

        /// <summary>
        /// Begin the join sequence. Safe to call only from the main thread.
        /// Spec §10.2 step (a) is implicit: OnNetworkThreadPayload is already
        /// buffering STATE_BATCH because this class installs itself as the
        /// payload sink before BeginJoin is called.
        /// </summary>
        public void BeginJoin(string roomId, string deviceId, string sceneHash)
        {
            if (string.IsNullOrEmpty(roomId))
            {
                throw new ArgumentException("roomId is required", nameof(roomId));
            }
            _pendingRoomId = roomId;
            _pendingDeviceId = deviceId ?? string.Empty;
            _pendingSceneHash = sceneHash ?? string.Empty;
            _preSnapshotBuffer.Clear();
            _highestAppliedRoomSeq = 0;
            _lastRejectReason = JoinRejectReason.None;
            TransitionTo(JoinState.Joining);

            byte[] payload = MessageCodec.EncodeJoinRoom(new JoinRoomMessage
            {
                RoomId = _pendingRoomId,
                DeviceId = _pendingDeviceId,
                SceneHash = _pendingSceneHash,
            });
            _transport.SendControl(_pendingRoomId, payload);
        }

        /// <summary>
        /// Reset to Disconnected. Clears internal buffers plus the remote
        /// pose buffer (if attached) so reconnects start from scratch.
        /// Per spec §10.7, a transport reconnect forces a fresh join.
        /// </summary>
        public void Reset()
        {
            _inbox.Clear();
            _preSnapshotBuffer.Clear();
            _highestAppliedRoomSeq = 0;
            _lastRejectReason = JoinRejectReason.None;
            _pendingRoomId = null;
            _pendingDeviceId = null;
            _pendingSceneHash = null;
            _resyncPending = false;
            _consecutiveUnknownEntities = 0;
            PoseBuffer buffer = Buffer;
            if (buffer != null)
            {
                buffer.Clear();
            }
            TransitionTo(JoinState.Disconnected);
        }

        /// <summary>
        /// Public hook to force a RESYNC_REQUEST. Used by debug UI and by
        /// the automatic gap / flood triggers. Returns true when dispatched.
        /// </summary>
        public bool RequestResync()
        {
            if (_state != JoinState.Joined || _resyncPending)
            {
                return false;
            }
            DispatchResyncRequest();
            return true;
        }

        /// <summary>
        /// Hand-off point for the ConnectionManager network thread. Must be
        /// thread-safe; does not touch Unity APIs.
        /// </summary>
        public void OnNetworkThreadPayload(byte firstByte, byte[] payload)
        {
            if (payload == null || payload.Length == 0)
            {
                return;
            }
            _inbox.Enqueue((firstByte, payload));
        }

        /// <summary>
        /// Drain the inbox on the main thread. Decodes messages and applies
        /// them to the EntityRegistry. Call once per frame.
        /// </summary>
        public void Pump()
        {
            while (_inbox.TryDequeue(out var item))
            {
                DispatchDecoded(item.msgType, item.payload);
            }

            OwnershipClient ownership = Ownership;
            if (ownership != null)
            {
                ownership.Pump();
            }

            if (_state == JoinState.Joined)
            {
                PosePublisher publisher = Publisher;
                if (publisher != null)
                {
                    publisher.Tick();
                }
            }

            PoseInterpolator interp = Interpolator;
            if (interp != null)
            {
                interp.Tick();
            }
        }

        // -----------------------------------------------------------------
        // Test-visible entry points (bypass codec for deterministic tests)
        // -----------------------------------------------------------------

        internal void TestInjectStateBatch(StateBatchMessage msg)
        {
            HandleStateBatch(msg);
        }

        internal void TestInjectRoomSnapshot(RoomSnapshotMessage msg)
        {
            HandleRoomSnapshot(msg);
        }

        internal IReadOnlyList<StateBatchMessage> TestPreSnapshotBuffer => _preSnapshotBuffer;

        internal void TestInjectResyncReply(ResyncReplyMessage msg)
        {
            HandleResyncReply(msg);
        }

        internal bool TestResyncPending => _resyncPending;

        // -----------------------------------------------------------------
        // Internal dispatch
        // -----------------------------------------------------------------

        private void DispatchDecoded(byte msgType, byte[] payload)
        {
            try
            {
                switch (msgType)
                {
                    case ReplMessageIds.RoomSnapshot:
                        HandleRoomSnapshot(MessageCodec.DecodeRoomSnapshot(payload, _codec));
                        break;
                    case ReplMessageIds.StateBatch:
                        HandleStateBatch(MessageCodec.DecodeStateBatch(payload, _codec));
                        break;
                    case ReplMessageIds.OwnershipEvent:
                        HandleOwnershipEvent(MessageCodec.DecodeOwnershipEvent(payload));
                        break;
                    case ReplMessageIds.ResyncReply:
                        HandleResyncReply(MessageCodec.DecodeResyncReply(payload, _codec));
                        break;
                    default:
                        // Unknown / client-only outbound IDs appearing in inbound
                        // traffic: ignore.
                        break;
                }
            }
            catch (IOException ex)
            {
                Debug.LogError($"[NetSync] Malformed replication payload (type={msgType}): {ex.Message}");
            }
            catch (InvalidDataException ex)
            {
                Debug.LogError($"[NetSync] Invalid replication payload (type={msgType}): {ex.Message}");
            }
        }

        private void HandleRoomSnapshot(RoomSnapshotMessage msg)
        {
            // Spec §10.2 step (c): reject gracefully on room mismatch. v1
            // does not carry a scene-hash reject-code frame, so we can only
            // validate what the snapshot exposes.
            if (_state != JoinState.Joining)
            {
                // Late or duplicate snapshot — ignore.
                return;
            }

            if (!string.IsNullOrEmpty(_pendingRoomId) &&
                !string.IsNullOrEmpty(msg.RoomId) &&
                !string.Equals(_pendingRoomId, msg.RoomId, StringComparison.Ordinal))
            {
                _lastRejectReason = JoinRejectReason.RoomIdMismatch;
                Debug.LogError(
                    $"[NetSync] ROOM_SNAPSHOT rejected: roomId mismatch (expected '{_pendingRoomId}', got '{msg.RoomId}').");
                TransitionTo(JoinState.Rejected);
                return;
            }

            // Step (d): learn our own identity from the snapshot.
            if (msg.YourClientNo != 0)
            {
                LocalClientNo = (int)msg.YourClientNo;
            }

            // Step (e): apply snapshot to live bindings.
            ApplySnapshot(msg);

            // Step (f): replay buffered STATE_BATCH where RoomSeq > base.
            ReplayBufferedBatches(msg.BaseRoomSeq);

            // Step (g): transition to Joined.
            TransitionTo(JoinState.Joined);
        }

        private void HandleStateBatch(StateBatchMessage msg)
        {
            switch (_state)
            {
                case JoinState.Joining:
                    // Buffer for replay.
                    _preSnapshotBuffer.Add(msg);
                    break;
                case JoinState.Joined:
                    ApplyStateBatch(msg);
                    break;
                default:
                    // Disconnected/Rejected: drop.
                    break;
            }
        }

        private void ApplySnapshot(RoomSnapshotMessage msg)
        {
            ApplySnapshotBody(msg.Entities, msg.BaseRoomSeq);
        }

        /// <summary>
        /// Shared snapshot-apply used by both ROOM_SNAPSHOT (initial join)
        /// and RESYNC_REPLY (mid-session recovery). Updates ownership state,
        /// applies transforms, and advances the highest-applied RoomSeq.
        /// </summary>
        private void ApplySnapshotBody(IReadOnlyList<EntityRecord> entities, uint newHighRoomSeq)
        {
            EntityRegistry registry = EntityRegistry.Instance;
            OwnershipClient ownership = Ownership;
            if (entities != null)
            {
                for (int i = 0; i < entities.Count; i++)
                {
                    EntityRecord rec = entities[i];
                    if (ownership != null)
                    {
                        ownership.HandleSnapshotEntity(rec);
                    }
                    if (!registry.TryGet(rec.EntityId, out EntityBinding binding))
                    {
                        RaiseUnknownEntity(rec.EntityId);
                        continue;
                    }
                    NetSyncObject obj = binding.Component;
                    if (obj == null)
                    {
                        continue;
                    }
                    ApplyTransformState(obj, rec.State, rec.ChangedMask);
                }
            }
            _highestAppliedRoomSeq = newHighRoomSeq;

            // Snapshots authoritatively re-seed state; flush any PoseBuffer
            // samples that would otherwise re-apply stale data.
            PoseBuffer buffer = Buffer;
            if (buffer != null)
            {
                buffer.Clear();
            }

            // Snapshot success clears the flood counter — stale-id bursts
            // prior to the resync were expected.
            _consecutiveUnknownEntities = 0;
        }

        private void HandleResyncReply(ResyncReplyMessage msg)
        {
            // RESYNC_REPLY carries its own BaseRoomSeq (same semantics as
            // ROOM_SNAPSHOT.BaseRoomSeq) so we can advance the drop-stale
            // cursor to match the server's authoritative state at the
            // moment the resync was produced.
            ApplySnapshotBody(msg.Entities, msg.BaseRoomSeq);
            _highestAppliedRoomSeq = msg.BaseRoomSeq;
            _resyncPending = false;
            OnResyncCompleted?.Invoke(_highestAppliedRoomSeq);
        }

        private void DispatchResyncRequest()
        {
            if (_resyncPending)
            {
                return;
            }
            _resyncPending = true;
            _consecutiveUnknownEntities = 0;

            byte[] payload = MessageCodec.EncodeResyncRequest(new ResyncRequestMessage
            {
                LastAppliedRoomSeq = _highestAppliedRoomSeq,
            });
            _transport.SendControl(_pendingRoomId ?? string.Empty, payload);
            OnResyncStarted?.Invoke(_highestAppliedRoomSeq);
        }

        private void HandleOwnershipEvent(OwnershipEventMessage evt)
        {
            OwnershipClient ownership = Ownership;
            if (ownership != null)
            {
                ownership.HandleOwnershipEvent(evt);
            }
        }

        private void ReplayBufferedBatches(uint baseRoomSeq)
        {
            if (_preSnapshotBuffer.Count == 0)
            {
                return;
            }
            // Sort by RoomSeq ascending so we apply in order even if the
            // network delivered them out of sequence.
            _preSnapshotBuffer.Sort((a, b) => a.RoomSeq.CompareTo(b.RoomSeq));
            for (int i = 0; i < _preSnapshotBuffer.Count; i++)
            {
                StateBatchMessage batch = _preSnapshotBuffer[i];
                if (batch.RoomSeq > baseRoomSeq)
                {
                    ApplyStateBatch(batch);
                }
            }
            _preSnapshotBuffer.Clear();
        }

        private void ApplyStateBatch(StateBatchMessage batch)
        {
            // Drop strictly-stale batches (out-of-order delivery after join).
            if (batch.RoomSeq != 0 && batch.RoomSeq <= _highestAppliedRoomSeq)
            {
                return;
            }

            // Gap detection (spec §10.7): a RoomSeq that jumps forward past
            // GapTolerance implies we've missed batches; trigger a resync.
            if (batch.RoomSeq > 0 && _highestAppliedRoomSeq > 0 &&
                batch.RoomSeq > _highestAppliedRoomSeq + GapTolerance &&
                !_resyncPending)
            {
                DispatchResyncRequest();
                // Still apply the batch — the resync reply will reconcile any
                // remaining discrepancies.
            }

            EntityRegistry registry = EntityRegistry.Instance;
            List<StateUpdate> updates = batch.Updates;
            PoseBuffer buffer = Buffer;
            OwnershipClient ownership = Ownership;
            int unknownThisBatch = 0;
            // Source-of-truth for remote-sample timing: server-stamped time.
            // Falls back to the local IServerClock estimate when the server
            // did not fill ServerTimeUs (client-originated echo paths).
            ulong sampleTimeUs = batch.ServerTimeUs != 0UL ? batch.ServerTimeUs : SynthesizeServerTimeUs();
            if (updates != null)
            {
                for (int i = 0; i < updates.Count; i++)
                {
                    StateUpdate u = updates[i];
                    if ((u.Flags & StateFlags.Heartbeat) != 0)
                    {
                        continue;
                    }
                    if (!registry.TryGet(u.EntityId, out EntityBinding binding))
                    {
                        RaiseUnknownEntity(u.EntityId);
                        unknownThisBatch++;
                        continue;
                    }
                    NetSyncObject obj = binding.Component;
                    if (obj == null)
                    {
                        continue;
                    }

                    // Authority / epoch gate: drop if the sender's epoch is
                    // stale relative to what OwnershipClient currently tracks.
                    if (ownership != null)
                    {
                        uint currentEpoch = ownership.GetAuthorityEpoch(u.EntityId);
                        if (currentEpoch != 0 && u.AuthorityEpoch < currentEpoch)
                        {
                            continue;
                        }
                    }

                    // Local-owner entities are driven by PosePublisher, not
                    // by inbound updates. Skip to avoid echo.
                    if (ownership != null && ownership.IsOwnedByLocalClient(u.EntityId))
                    {
                        continue;
                    }

                    if (buffer != null)
                    {
                        buffer.Add(u.EntityId, new PoseSample
                        {
                            ServerTimeUs = sampleTimeUs,
                            PoseSeq = u.PoseSeq,
                            Flags = u.Flags,
                            Mask = u.ChangedMask,
                            State = u.State,
                        });
                    }
                    else
                    {
                        // Legacy direct-apply path (used when no interpolator
                        // is attached — e.g. Phase 2 unit tests).
                        ApplyTransformState(obj, u.State, u.ChangedMask);
                    }
                }
            }

            // Unknown-entity flood detection. If we see N unknowns in a row
            // across batches, assume the local registry is behind the server
            // and pull a fresh snapshot.
            if (unknownThisBatch > 0)
            {
                _consecutiveUnknownEntities += unknownThisBatch;
                if (_consecutiveUnknownEntities >= UnknownEntityFloodThreshold && !_resyncPending)
                {
                    DispatchResyncRequest();
                }
            }
            else
            {
                _consecutiveUnknownEntities = 0;
            }

            if (batch.RoomSeq > _highestAppliedRoomSeq)
            {
                _highestAppliedRoomSeq = batch.RoomSeq;
            }
        }

        private static void ApplyTransformState(NetSyncObject obj, TransformState state, ChangedMask mask)
        {
            // Phase 2 stub: direct write. Phase 4 introduces interpolation.
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

        private void TransitionTo(JoinState next)
        {
            if (_state == next)
            {
                return;
            }
            _state = next;
            JoinStateChanged?.Invoke(next);
        }

        private void RaiseUnknownEntity(ulong entityId)
        {
            Debug.LogWarning($"[NetSync] Replication update for unknown EntityId 0x{entityId:X16}; skipping.");
            UnknownEntityEncountered?.Invoke(entityId);
        }
    }
}
