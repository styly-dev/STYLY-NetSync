// EntityId.cs
// Deterministic GUID -> ulong derivation used to address replicated entities
// on the wire. Same input GUID always produces the same EntityId across
// processes and machines.

using System;

namespace Styly.NetSync.Internal
{
    /// <summary>
    /// Helpers to derive a 64-bit network EntityId from an authored GUID.
    ///
    /// Derivation:
    ///   1. Parse the GUID string into its 16-byte big-endian RFC form
    ///      (via <see cref="Guid.ToByteArray"/> then normalized to
    ///      big-endian so the layout is independent of host endianness).
    ///   2. Fold the 16 bytes into two little-endian uint64 halves
    ///      (hi = bytes[0..7], lo = bytes[8..15]).
    ///   3. EntityId = hi XOR lo.
    ///
    /// Collision risk: with an XOR fold of a random 128-bit value we get
    /// uniform 64-bit output. For scene populations &lt;&lt; 2^32 the birthday
    /// bound is comfortably safe; duplicates are further guarded by the
    /// authoring-time validator.
    ///
    /// EntityId 0 is reserved as "invalid / unassigned". In the unlikely
    /// event the fold produces 0, we return 1.
    /// </summary>
    public static class EntityIdUtils
    {
        public const ulong Invalid = 0UL;

        /// <summary>
        /// Derive an EntityId from an authored GUID string. Returns
        /// <see cref="Invalid"/> when the string cannot be parsed.
        /// </summary>
        public static ulong FromGuidString(string guidString)
        {
            if (string.IsNullOrEmpty(guidString))
            {
                return Invalid;
            }

            if (!Guid.TryParse(guidString, out Guid g))
            {
                return Invalid;
            }

            return FromGuid(g);
        }

        /// <summary>
        /// Derive an EntityId from a Guid. The Guid's byte layout is
        /// normalized to big-endian RFC 4122 form before folding.
        /// </summary>
        public static ulong FromGuid(Guid g)
        {
            byte[] bytes = g.ToByteArray();
            // Guid.ToByteArray returns mixed-endian (first three groups are
            // little-endian on all platforms). Normalize to big-endian so
            // the fold is deterministic across platforms.
            NormalizeToBigEndian(bytes);

            ulong hi = ReadUInt64LittleEndian(bytes, 0);
            ulong lo = ReadUInt64LittleEndian(bytes, 8);
            ulong id = hi ^ lo;
            return id == Invalid ? 1UL : id;
        }

        private static void NormalizeToBigEndian(byte[] b)
        {
            // Swap data1 (4 bytes), data2 (2 bytes), data3 (2 bytes).
            (b[0], b[3]) = (b[3], b[0]);
            (b[1], b[2]) = (b[2], b[1]);
            (b[4], b[5]) = (b[5], b[4]);
            (b[6], b[7]) = (b[7], b[6]);
            // data4 (8 bytes) is already big-endian in Guid.ToByteArray.
        }

        private static ulong ReadUInt64LittleEndian(byte[] b, int offset)
        {
            return (ulong)b[offset]
                | ((ulong)b[offset + 1] << 8)
                | ((ulong)b[offset + 2] << 16)
                | ((ulong)b[offset + 3] << 24)
                | ((ulong)b[offset + 4] << 32)
                | ((ulong)b[offset + 5] << 40)
                | ((ulong)b[offset + 6] << 48)
                | ((ulong)b[offset + 7] << 56);
        }
    }
}
