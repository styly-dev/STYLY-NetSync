// PoseBuffer.cs
// Per-entity bounded snapshot buffer used by PoseInterpolator.
// Drops stale/out-of-order entries by poseSeq; oldest entry is evicted
// when the buffer is full. Main-thread only.

using System.Collections.Generic;

namespace Styly.NetSync.Internal
{
    /// <summary>
    /// Single snapshot retained in a <see cref="PoseBuffer"/>.
    /// </summary>
    public struct PoseSample
    {
        public ulong ServerTimeUs;
        public ushort PoseSeq;
        public StateFlags Flags;
        public ChangedMask Mask;
        public TransformState State;
    }

    /// <summary>
    /// Bounded per-entity snapshot store. Default capacity 32; drop-oldest
    /// on overflow.
    /// </summary>
    public sealed class PoseBuffer
    {
        public const int DefaultCapacity = 32;

        private sealed class EntityRing
        {
            public readonly List<PoseSample> Samples = new List<PoseSample>(DefaultCapacity);
            public ushort LastPoseSeq;
            public bool HasAnyApplied;
        }

        private readonly Dictionary<ulong, EntityRing> _byEntity = new Dictionary<ulong, EntityRing>();
        private readonly int _capacity;

        public PoseBuffer(int capacity = DefaultCapacity)
        {
            _capacity = capacity < 2 ? 2 : capacity;
        }

        /// <summary>
        /// Try to enqueue a sample for an entity. Returns false when the
        /// sample is stale (poseSeq &lt;= last applied) or out-of-order
        /// relative to the tail.
        /// </summary>
        public bool Add(ulong entityId, in PoseSample sample)
        {
            EntityRing ring = GetOrCreate(entityId);

            // Stale vs last-applied: drop.
            if (ring.HasAnyApplied && !SeqGreater(sample.PoseSeq, ring.LastPoseSeq))
            {
                return false;
            }

            // Drop if older than the tail of the ring (out-of-order burst).
            if (ring.Samples.Count > 0)
            {
                ushort tailSeq = ring.Samples[ring.Samples.Count - 1].PoseSeq;
                if (!SeqGreater(sample.PoseSeq, tailSeq))
                {
                    return false;
                }
            }

            if (ring.Samples.Count >= _capacity)
            {
                ring.Samples.RemoveAt(0);
            }
            ring.Samples.Add(sample);
            return true;
        }

        /// <summary>
        /// Mark the head sample as applied and record its poseSeq so future
        /// stale drops work across ring compaction.
        /// </summary>
        public void MarkApplied(ulong entityId, ushort poseSeq)
        {
            EntityRing ring = GetOrCreate(entityId);
            ring.LastPoseSeq = poseSeq;
            ring.HasAnyApplied = true;
        }

        /// <summary>
        /// Direct access to the per-entity ring. Returns empty list if the
        /// entity has no samples yet.
        /// </summary>
        public IReadOnlyList<PoseSample> GetSamples(ulong entityId)
        {
            return _byEntity.TryGetValue(entityId, out EntityRing ring) ? ring.Samples : System.Array.Empty<PoseSample>();
        }

        /// <summary>
        /// Enumerate entity ids with one or more samples. Caller iterates
        /// synchronously; do not add/remove during iteration.
        /// </summary>
        public IEnumerable<ulong> Entities => _byEntity.Keys;

        public int Count(ulong entityId)
        {
            return _byEntity.TryGetValue(entityId, out EntityRing ring) ? ring.Samples.Count : 0;
        }

        /// <summary>
        /// Drop samples older than <paramref name="serverTimeUs"/>, keeping
        /// the most recent "before" sample for interpolation.
        /// </summary>
        public void Prune(ulong entityId, ulong serverTimeUs)
        {
            if (!_byEntity.TryGetValue(entityId, out EntityRing ring))
            {
                return;
            }
            List<PoseSample> s = ring.Samples;
            // Keep the most-recent-before-time plus everything after.
            int keepFrom = 0;
            for (int i = 0; i < s.Count - 1; i++)
            {
                if (s[i + 1].ServerTimeUs <= serverTimeUs)
                {
                    keepFrom = i + 1;
                }
                else
                {
                    break;
                }
            }
            if (keepFrom > 0)
            {
                s.RemoveRange(0, keepFrom);
            }
        }

        public void Clear()
        {
            _byEntity.Clear();
        }

        public void ClearEntity(ulong entityId)
        {
            _byEntity.Remove(entityId);
        }

        private EntityRing GetOrCreate(ulong entityId)
        {
            if (!_byEntity.TryGetValue(entityId, out EntityRing ring))
            {
                ring = new EntityRing();
                _byEntity[entityId] = ring;
            }
            return ring;
        }

        // Sequence numbers are u16 and may wrap. Compare with wrap-aware semantics:
        // a > b iff ((a - b) & 0xFFFF) is in the first half of the range.
        private static bool SeqGreater(ushort a, ushort b)
        {
            unchecked
            {
                ushort diff = (ushort)(a - b);
                return diff != 0 && diff < 0x8000;
            }
        }
    }
}
