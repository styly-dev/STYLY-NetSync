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

    /// <summary>
    /// Reason a ROOM_SNAPSHOT handshake was rejected. Phase 2 only
    /// surfaces the two we can detect from a v1-only wire.
    /// </summary>
    public enum JoinRejectReason
    {
        None = 0,
        SceneHashMismatch = 1,
        RoomIdMismatch = 2,
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
        // On snapshot apply, we replay entries with ServerTick > baseServerTick.
        private readonly List<StateBatchMessage> _preSnapshotBuffer = new List<StateBatchMessage>();

        // Highest ServerTick we have already applied. Updated by snapshot
        // apply and by each STATE_BATCH applied post-join. Out-of-order
        // batches with a stale tick are dropped.
        private uint _highestAppliedServerTick;

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

        public JoinState State => _state;
        public string RoomId => _pendingRoomId;
        public JoinRejectReason LastRejectReason => _lastRejectReason;
        public uint HighestAppliedServerTick => _highestAppliedServerTick;

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
            _highestAppliedServerTick = 0;
            _lastRejectReason = JoinRejectReason.None;
            TransitionTo(JoinState.Joining);

            // Encode and send JOIN_ROOM. v1 JoinRoomMessage only carries
            // roomId + deviceId on the wire; sceneHash travels once Phase 3
            // extends the message. We stash _pendingSceneHash for the
            // eventual comparison against the server's roomId (best we can
            // do with the current v1 schema).
            byte[] payload = MessageCodec.EncodeJoinRoom(new JoinRoomMessage
            {
                RoomId = _pendingRoomId,
                DeviceId = _pendingDeviceId,
            });
            _transport.SendControl(_pendingRoomId, payload);
        }

        /// <summary>
        /// Reset to Disconnected. Clears internal buffers.
        /// </summary>
        public void Reset()
        {
            _inbox.Clear();
            _preSnapshotBuffer.Clear();
            _highestAppliedServerTick = 0;
            _lastRejectReason = JoinRejectReason.None;
            _pendingRoomId = null;
            _pendingDeviceId = null;
            _pendingSceneHash = null;
            TransitionTo(JoinState.Disconnected);
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
                    case ReplMessageIds.ResyncReply:
                        // Phase 2: decoded but not yet applied. Drop silently.
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

            // Step (d): apply snapshot to live bindings.
            ApplySnapshot(msg);

            // Step (e): replay buffered STATE_BATCH where ServerTick > base.
            ReplayBufferedBatches(msg.ServerTick);

            // Step (f): transition to Joined.
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
            EntityRegistry registry = EntityRegistry.Instance;
            List<EntityRecord> entities = msg.Entities;
            if (entities == null)
            {
                _highestAppliedServerTick = msg.ServerTick;
                return;
            }
            for (int i = 0; i < entities.Count; i++)
            {
                EntityRecord rec = entities[i];
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
            _highestAppliedServerTick = msg.ServerTick;
        }

        private void ReplayBufferedBatches(uint baseServerTick)
        {
            if (_preSnapshotBuffer.Count == 0)
            {
                return;
            }
            // Sort by ServerTick ascending so we apply in order even if the
            // network delivered them out of sequence.
            _preSnapshotBuffer.Sort((a, b) => a.ServerTick.CompareTo(b.ServerTick));
            for (int i = 0; i < _preSnapshotBuffer.Count; i++)
            {
                StateBatchMessage batch = _preSnapshotBuffer[i];
                if (batch.ServerTick > baseServerTick)
                {
                    ApplyStateBatch(batch);
                }
            }
            _preSnapshotBuffer.Clear();
        }

        private void ApplyStateBatch(StateBatchMessage batch)
        {
            // Drop strictly-stale batches (out-of-order delivery after join).
            if (batch.ServerTick != 0 && batch.ServerTick <= _highestAppliedServerTick)
            {
                return;
            }

            EntityRegistry registry = EntityRegistry.Instance;
            List<StateUpdate> updates = batch.Updates;
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
                        continue;
                    }
                    NetSyncObject obj = binding.Component;
                    if (obj == null)
                    {
                        continue;
                    }
                    ApplyTransformState(obj, u.State, u.ChangedMask);
                }
            }

            if (batch.ServerTick > _highestAppliedServerTick)
            {
                _highestAppliedServerTick = batch.ServerTick;
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
