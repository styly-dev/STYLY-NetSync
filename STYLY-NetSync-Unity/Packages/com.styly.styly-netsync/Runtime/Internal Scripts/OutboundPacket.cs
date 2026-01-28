// OutboundPacket.cs - Data structure for outbound send queue with priority lanes
namespace Styly.NetSync
{
    /// <summary>
    /// Defines the priority lane for outbound packets.
    /// Control messages (RPC, Network Variables) have higher priority than Transform updates.
    /// </summary>
    internal enum OutboundLane
    {
        /// <summary>
        /// Control messages (RPC, Network Variables). Higher priority - drained first.
        /// </summary>
        Control,

        /// <summary>
        /// Transform updates. Lower priority - only sent after control queue is drained.
        /// Uses latest-wins semantics (only the most recent transform is sent).
        /// </summary>
        Transform
    }

    /// <summary>
    /// Represents an outbound packet waiting to be sent.
    /// Used for the application-level send queue with TTL support.
    /// </summary>
    internal sealed class OutboundPacket
    {
        /// <summary>
        /// The priority lane for this packet.
        /// </summary>
        public OutboundLane Lane;

        /// <summary>
        /// The room ID this packet should be sent to.
        /// </summary>
        public string RoomId;

        /// <summary>
        /// The serialized payload bytes to send.
        /// </summary>
        public byte[] Payload;

        /// <summary>
        /// Unix timestamp (seconds) when this packet was enqueued.
        /// Used for TTL expiration checks.
        /// </summary>
        public double EnqueuedAt;

        /// <summary>
        /// Number of send attempts made for this packet.
        /// Incremented on each backpressure retry.
        /// </summary>
        public int Attempts;
    }
}
