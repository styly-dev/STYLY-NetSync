using System.IO;

namespace Styly.NetSync.Internal
{
    // Swappable transform encoder. v1 uses float32 for all components.
    // A future implementation may introduce quantization (e.g. int24 positions,
    // smallest-three quaternions) without changing the surrounding framing.
    // Implementations write/read only the fields selected by `mask`.
    public interface ITransformCodec
    {
        byte Version { get; }

        void Write(BinaryWriter writer, ChangedMask mask, in TransformState state);

        TransformState Read(BinaryReader reader, ChangedMask mask);
    }
}
