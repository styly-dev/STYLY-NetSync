using System;
using UnityEngine;

namespace Styly.NetSync.Internal
{
    // StateFlags bitfield. Matches docs/replication-protocol-v1.md.
    [Flags]
    public enum StateFlags : byte
    {
        None = 0,
        Keyframe = 1 << 0,
        Teleport = 1 << 1,
        Heartbeat = 1 << 2,
    }

    // ChangedMask bitfield. Identifies which TransformState fields are present.
    [Flags]
    public enum ChangedMask : byte
    {
        None = 0,
        Position = 1 << 0,
        Rotation = 1 << 1,
        Scale = 1 << 2,
        All = Position | Rotation | Scale,
    }

    // Outcome of an ownership transition. Carried in OwnershipEvent.
    // Expired is server-initiated (lease sweep); LeaseExpired as a reason
    // code paired with Denied covers the client-initiated "stale request"
    // path (see OwnershipEventReasonCode).
    public enum OwnershipResult : byte
    {
        Granted = 0,
        Denied = 1,
        Released = 2,
        Expired = 3,
    }

    // Auxiliary reason code accompanying OwnershipResult. None is used on
    // success (Granted / Released) and for server-initiated Expired
    // sweeps. Timeout is reserved for Unity-side use (a client-local
    // "request never got an answer") and is never produced by the server.
    public enum OwnershipEventReasonCode : byte
    {
        None = 0,
        AlreadyOwned = 1,
        NotOwner = 2,
        EpochMismatch = 3,
        LeaseExpired = 4,
        Timeout = 5,
    }

    // Reason codes for JoinRejectMessage. 255 is the forward-compatible
    // catch-all so clients can always surface the accompanying reason text
    // even when a new server reports an unknown code.
    public enum JoinRejectReason : byte
    {
        SceneHashMismatch = 0,
        RoomFull = 1,
        ProtocolVersionMismatch = 2,
        Unspecified = 255,
    }

    // Replicated transform. v1 stores float32 components; quantization is a
    // future codec drop-in (see ITransformCodec).
    public struct TransformState
    {
        public Vector3 Position;
        public Quaternion Rotation;
        public Vector3 Scale;

        public static TransformState Identity => new TransformState
        {
            Position = Vector3.zero,
            Rotation = Quaternion.identity,
            Scale = Vector3.one,
        };
    }

    // Per-entity state update carried inside a StateBatch.
    public struct StateUpdate
    {
        public ulong EntityId;
        public uint AuthorityEpoch;
        public ushort PoseSeq;
        public StateFlags Flags;
        public ChangedMask ChangedMask;
        public TransformState State;
    }

    // Authoritative record of an entity as tracked by a peer (client or server).
    // Used by snapshots and resync replies.
    public struct EntityRecord
    {
        public ulong EntityId;
        public uint AuthorityEpoch;
        public uint OwnerShortId;
        public ushort PoseSeq;
        public ChangedMask ChangedMask;
        public TransformState State;
    }
}
