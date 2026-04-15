// ReplicationBridge.cs
// Glue between the existing NetSyncManager / ConnectionManager / MessageProcessor
// and the Phase 2-5 replication subsystem (ReplicationClient + OwnershipClient
// + PosePublisher + PoseInterpolator + PoseBuffer). Owned by NetSyncManager;
// lifetime matches the NetSyncManager instance.
//
// The bridge is intentionally independent of MonoBehaviour plumbing — it is
// driven by NetSyncManager.Update() via <see cref="Pump"/> so the existing
// update loop retains a single ordering authority.

using System;
using System.Collections.Generic;
using Styly.NetSync.Internal;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Styly.NetSync.Internal
{
    /// <summary>
    /// Wires ConnectionManager outbound control as an <see cref="IReplicationTransport"/>.
    /// </summary>
    internal sealed class ConnectionManagerReplicationTransport : IReplicationTransport
    {
        private readonly IConnectionManager _connection;

        public ConnectionManagerReplicationTransport(IConnectionManager connection)
        {
            _connection = connection ?? throw new ArgumentNullException(nameof(connection));
        }

        public bool SendControl(string roomId, byte[] payload)
        {
            if (_connection == null)
            {
                return false;
            }
            return _connection.TryEnqueueControl(roomId ?? string.Empty, payload);
        }
    }

    /// <summary>
    /// Monotonic microsecond clock sourced from the Unity engine. Safe to
    /// call from the main thread only.
    /// </summary>
    internal sealed class UnityServerClock : IServerClock
    {
        public ulong NowUs => (ulong)(Time.unscaledTimeAsDouble * 1_000_000.0);
    }

    /// <summary>
    /// View over OwnershipClient for the PosePublisher. Tracks owned
    /// entities by listening to OwnershipChanged events.
    /// </summary>
    internal sealed class OwnershipAuthorityView : IAuthorityView
    {
        private readonly OwnershipClient _ownership;
        private readonly HashSet<ulong> _owned = new HashSet<ulong>();
        private readonly List<ulong> _ownedList = new List<ulong>();
        private bool _dirty = true;

        public OwnershipAuthorityView(OwnershipClient ownership)
        {
            _ownership = ownership ?? throw new ArgumentNullException(nameof(ownership));
            _ownership.OwnershipChanged += OnOwnershipChanged;
        }

        private void OnOwnershipChanged(ulong entityId, OwnershipChange change)
        {
            if (_ownership.IsOwnedByLocalClient(entityId))
            {
                if (_owned.Add(entityId))
                {
                    _dirty = true;
                }
            }
            else if (_owned.Remove(entityId))
            {
                _dirty = true;
            }
        }

        public IReadOnlyList<ulong> OwnedEntityIds
        {
            get
            {
                if (_dirty)
                {
                    _ownedList.Clear();
                    foreach (ulong id in _owned)
                    {
                        _ownedList.Add(id);
                    }
                    _dirty = false;
                }
                return _ownedList;
            }
        }

        public bool TryGetAuthority(ulong entityId, out int ownerClientNo, out uint authorityEpoch)
        {
            ownerClientNo = _ownership.GetOwnerClientNo(entityId);
            authorityEpoch = _ownership.GetAuthorityEpoch(entityId);
            return ownerClientNo != 0;
        }
    }

    /// <summary>
    /// Owns the replication-subsystem object graph for one NetSyncManager.
    /// </summary>
    internal sealed class ReplicationBridge
    {
        private readonly IConnectionManager _connection;
        private readonly MessageProcessor _messageProcessor;
        private readonly IReplicationTransport _transport;

        public ReplicationClient Client { get; }
        public OwnershipClient Ownership { get; }
        public PoseBuffer Buffer { get; }
        public PoseInterpolator Interpolator { get; }
        public PosePublisher Publisher { get; }

        private readonly OwnershipAuthorityView _authorityView;
        private readonly UnityServerClock _serverClock = new UnityServerClock();
        private bool _joinedConnectionSeen;

        public ReplicationBridge(IConnectionManager connection, MessageProcessor messageProcessor)
        {
            _connection = connection ?? throw new ArgumentNullException(nameof(connection));
            _messageProcessor = messageProcessor ?? throw new ArgumentNullException(nameof(messageProcessor));

            _transport = new ConnectionManagerReplicationTransport(connection);

            Client = new ReplicationClient(_transport, TransformCodecV1.Instance)
            {
                ServerClock = _serverClock,
            };
            Ownership = new OwnershipClient(_transport);
            Buffer = new PoseBuffer();
            _authorityView = new OwnershipAuthorityView(Ownership);
            Publisher = new PosePublisher(_transport, _authorityView, _serverClock);
            Interpolator = new PoseInterpolator(Buffer, _serverClock)
            {
                IsOwnedLocally = id => Ownership.IsOwnedByLocalClient(id),
                IsJoined = () => Client.State == JoinState.Joined,
            };

            Client.Ownership = Ownership;
            Client.Buffer = Buffer;
            Client.Publisher = Publisher;
            Client.Interpolator = Interpolator;

            // Install the process-wide Active pointer used by NetSyncObject's
            // public ownership accessors.
            OwnershipClient.Active = Ownership;

            // Route network-thread replication payloads into ReplicationClient.
            _messageProcessor.OnReplicationPayload += OnReplicationPayload;

            // Watch the connection state so a reconnect resets internal
            // buffers per spec §10.7.
            _connection.OnConnectionError += OnConnectionError;
            _connection.OnConnectionEstablished += OnConnectionEstablished;
        }

        private void OnReplicationPayload(byte msgType, byte[] payload)
        {
            Client.OnNetworkThreadPayload(msgType, payload);
        }

        private void OnConnectionError(string _)
        {
            // Network failure: drop all in-flight state so the next
            // connect triggers a fresh join.
            Client.Reset();
            Publisher.Reset();
            Ownership.Reset();
            _joinedConnectionSeen = false;
        }

        private void OnConnectionEstablished()
        {
            // Flag only; actual BeginJoin is kicked off by the caller
            // (NetSyncManager.JoinReplicationRoom).
            _joinedConnectionSeen = true;
        }

        /// <summary>
        /// Main-thread tick. Drains the network inbox, runs ownership
        /// timeouts, and advances publish + interpolate cycles.
        /// </summary>
        public void Pump()
        {
            Client.Pump();
        }

        /// <summary>
        /// Kick off a JOIN_ROOM using the supplied identifiers. Callers
        /// are expected to derive sceneHash via <see cref="SceneHashBuilder"/>.
        /// </summary>
        public void BeginJoin(string roomId, string deviceId, string sceneHash)
        {
            Ownership.RoomId = roomId;
            Publisher.RoomId = roomId;
            Client.BeginJoin(roomId, deviceId, sceneHash);
        }

        public void Dispose()
        {
            _messageProcessor.OnReplicationPayload -= OnReplicationPayload;
            _connection.OnConnectionError -= OnConnectionError;
            _connection.OnConnectionEstablished -= OnConnectionEstablished;
            if (ReferenceEquals(OwnershipClient.Active, Ownership))
            {
                OwnershipClient.Active = null;
            }
        }

        public bool HasEverConnected => _joinedConnectionSeen;
    }
}
