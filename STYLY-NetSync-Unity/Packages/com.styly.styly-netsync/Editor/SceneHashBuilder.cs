// SceneHashBuilder.cs (Editor wrapper)
// Thin editor-only facade that forwards to the runtime
// Styly.NetSync.Internal.SceneHashBuilder. Kept so existing editor code
// (and any future build-time tooling) can compute the hash without pulling
// in runtime-only types through their own paths.

using UnityEngine.SceneManagement;

namespace Styly.NetSync.Internal.EditorTools
{
    public static class SceneHashBuilder
    {
        public static string BuildHash(Scene scene)
        {
            return Styly.NetSync.Internal.SceneHashBuilder.BuildHash(scene);
        }
    }
}
