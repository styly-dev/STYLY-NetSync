using System.Collections.Generic;

namespace Styly.NetSync.Internal
{
    // Replication message type identifiers. Reserved range 30-39.
    // See docs/replication-protocol-v1.md.
    internal static class ReplMessageIds
    {
        public const byte JoinRoom = 30;
        public const byte RoomSnapshot = 31;
        public const byte OwnershipRequest = 32;
        public const byte OwnershipEvent = 33;
        public const byte ResyncRequest = 34;
        public const byte ResyncReply = 35;
        public const byte StateBatch = 36;
        public const byte JoinReject = 37;

        public const byte ReplProtocolVersion = 1;
    }

    // Client -> server. Announces room membership. SceneHash lets the
    // server reject clients that were built against a different scene.
    public struct JoinRoomMessage
    {
        public string RoomId;
        public string DeviceId;
        public string SceneHash;
    }

    // Server -> client. Initial snapshot delivered on join.
    // BaseRoomSeq is the anchor sequence (RoomState.NextRoomSeq - 1) against
    // which subsequent STATE_BATCH deltas are ordered. ServerTimeUs is the
    // server wall clock in microseconds since the Unix epoch; clients use
    // it for relative age calculations (cross-process monotonic clocks are
    // unusable for this). YourClientNo is the short client id the server
    // assigned to the receiving client; the replication layer reads its
    // own identity from this rather than requiring external wiring.
    public struct RoomSnapshotMessage
    {
        public string RoomId;
        public uint BaseRoomSeq;
        public ulong ServerTimeUs;
        public uint YourClientNo;
        public List<EntityRecord> Entities;
    }

    // Server -> client. Sent instead of RoomSnapshot when JOIN_ROOM is
    // rejected (e.g. scene hash mismatch). ReasonText is free-form for
    // diagnostics; receivers must not rely on its format.
    public struct JoinRejectMessage
    {
        public string RoomId;
        public JoinRejectReason Reason;
        public string ReasonText;
    }

    // Client -> server. Requests ownership of an entity.
    public struct OwnershipRequestMessage
    {
        public ulong EntityId;
        public uint RequesterShortId;
        public uint ExpectedEpoch;
    }

    // Server -> clients. Broadcast when ownership changes. Result is the
    // outcome of the transition; ReasonCode is auxiliary (None for
    // success and server-initiated Expired, populated for Denied).
    public struct OwnershipEventMessage
    {
        public ulong EntityId;
        public uint NewOwnerShortId;
        public uint NewAuthorityEpoch;
        public OwnershipResult Result;
        public OwnershipEventReasonCode ReasonCode;
    }

    // Client -> server. Requests fresh state for one or more entities.
    public struct ResyncRequestMessage
    {
        public List<ulong> EntityIds;
    }

    // Server -> client. Response to ResyncRequest.
    public struct ResyncReplyMessage
    {
        public List<EntityRecord> Entities;
    }

    // Both directions. Carries per-entity state updates.
    public struct StateBatchMessage
    {
        public uint ServerTick;
        public List<StateUpdate> Updates;
    }
}
