using System.IO;
using UnityEngine;

namespace Styly.NetSync.Internal
{
    // v1 transform codec. float32 components, no quantization.
    // Only fields selected by `mask` are written/read, in order: Position,
    // Rotation, Scale. Fields not in the mask leave the decoded state at
    // its default (identity).
    public sealed class TransformCodecV1 : ITransformCodec
    {
        public static readonly TransformCodecV1 Instance = new TransformCodecV1();

        public byte Version => 1;

        public void Write(BinaryWriter writer, ChangedMask mask, in TransformState state)
        {
            if ((mask & ChangedMask.Position) != 0)
            {
                writer.Write(state.Position.x);
                writer.Write(state.Position.y);
                writer.Write(state.Position.z);
            }
            if ((mask & ChangedMask.Rotation) != 0)
            {
                writer.Write(state.Rotation.x);
                writer.Write(state.Rotation.y);
                writer.Write(state.Rotation.z);
                writer.Write(state.Rotation.w);
            }
            if ((mask & ChangedMask.Scale) != 0)
            {
                writer.Write(state.Scale.x);
                writer.Write(state.Scale.y);
                writer.Write(state.Scale.z);
            }
        }

        public TransformState Read(BinaryReader reader, ChangedMask mask)
        {
            var result = TransformState.Identity;
            if ((mask & ChangedMask.Position) != 0)
            {
                float px = reader.ReadSingle();
                float py = reader.ReadSingle();
                float pz = reader.ReadSingle();
                result.Position = new Vector3(px, py, pz);
            }
            if ((mask & ChangedMask.Rotation) != 0)
            {
                float rx = reader.ReadSingle();
                float ry = reader.ReadSingle();
                float rz = reader.ReadSingle();
                float rw = reader.ReadSingle();
                result.Rotation = new Quaternion(rx, ry, rz, rw);
            }
            if ((mask & ChangedMask.Scale) != 0)
            {
                float sx = reader.ReadSingle();
                float sy = reader.ReadSingle();
                float sz = reader.ReadSingle();
                result.Scale = new Vector3(sx, sy, sz);
            }
            return result;
        }
    }
}
