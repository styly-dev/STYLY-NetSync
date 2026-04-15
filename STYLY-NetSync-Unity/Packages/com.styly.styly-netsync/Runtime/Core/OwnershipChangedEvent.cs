// OwnershipChangedEvent.cs
// Public payload for NetSyncObject.OnOwnershipChanged.

using Styly.NetSync.Internal;

namespace Styly.NetSync
{
    /// <summary>
    /// Reason an ownership change was observed. Mirrors the server-side
    /// OwnershipResult on the wire plus a local Timeout emitted when a
    /// pending OWNERSHIP_REQUEST expires without a server reply, and a
    /// Revoked value for non-local changes observed through ROOM_SNAPSHOT.
    /// </summary>
    public enum OwnershipChangeReason : byte
    {
        Granted = 0,
        Rejected = 1,
        Revoked = 2,
        Released = 3,
        Timeout = 4,
        Expired = 5,
    }

    /// <summary>
    /// Delivered to <see cref="NetSyncObject.OnOwnershipChanged"/>.
    /// </summary>
    public struct OwnershipChangedEvent
    {
        public int PreviousOwnerClientNo;
        public int NewOwnerClientNo;
        public uint AuthorityEpoch;
        public OwnershipChangeReason Reason;

        internal static OwnershipChangedEvent FromInternal(in OwnershipChange src)
        {
            return new OwnershipChangedEvent
            {
                PreviousOwnerClientNo = src.PreviousOwnerClientNo,
                NewOwnerClientNo = src.NewOwnerClientNo,
                AuthorityEpoch = src.AuthorityEpoch,
                Reason = (OwnershipChangeReason)(byte)src.Reason,
            };
        }
    }
}
