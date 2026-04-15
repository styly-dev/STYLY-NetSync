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

        public const byte ReplProtocolVersion = 1;
    }

    // Client -> server. Announces room membership.
    public struct JoinRoomMessage
    {
        public string RoomId;
        public string DeviceId;
    }

    // Server -> client. Initial snapshot delivered on join.
    public struct RoomSnapshotMessage
    {
        public string RoomId;
        public uint ServerTick;
        public List<EntityRecord> Entities;
    }

    // Client -> server. Requests ownership of an entity.
    public struct OwnershipRequestMessage
    {
        public ulong EntityId;
        public uint RequesterShortId;
        public uint ExpectedEpoch;
    }

    // Server -> clients. Broadcast when ownership changes.
    public struct OwnershipEventMessage
    {
        public ulong EntityId;
        public uint NewOwnerShortId;
        public uint NewAuthorityEpoch;
        public OwnershipReason Reason;
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
