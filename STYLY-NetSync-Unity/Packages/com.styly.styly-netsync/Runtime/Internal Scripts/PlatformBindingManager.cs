// PlatformBindingManager.cs
using System.Collections.Generic;
using System.Globalization;
using UnityEngine;

namespace Styly.NetSync
{
    /// <summary>
    /// Tracks which remote clients are riding a moving platform so receivers can
    /// reconstruct their head pose from a local real-time platform transform
    /// instead of the lagged xrOriginDelta channel. See GitHub issue #425.
    ///
    /// State is propagated via the per-client network variable
    /// <see cref="ClientVariableName"/>; binding poses are captured at boarding
    /// and remain constant while bound (drift handling is a follow-up).
    /// </summary>
    internal class PlatformBindingManager
    {
        // Reserved client variable name used to broadcast the binding state.
        public const string ClientVariableName = NetSyncManager.PrefixForSystem + "platformBinding";

        // Approximate duration for the head pose to fade from platform-derived
        // back to network-smoothed when a binding is released.
        public const float UnbindBlendSeconds = 0.3f;

        public class BindingState
        {
            // Local Transform to read the current platform pose from. Null when the
            // receiver has not registered the matching platformId yet — the caller
            // should fall back to the unbound path until it becomes available.
            public Transform Platform;

            public string PlatformId;

            // XR Origin pose expressed in the platform's local frame, captured at
            // boarding. Persists for the lifetime of the binding.
            public Vector3 RigOffsetPos;
            public float RigOffsetYaw;

            // Sender's startup XR Origin pose, needed to rebase the smoothed
            // physical pose into xr-origin-local space:
            //   head_local = R(-Yaw0) * (physical - P0)
            public Vector3 P0;
            public float Yaw0;

            // Active blend (set when the binding is released). Captures the last
            // platform-derived head pose so the network-smoothed head can ease in
            // over UnbindBlendSeconds.
            public bool IsUnbinding;
            public float UnbindElapsedSeconds;
            public Vector3 UnbindHeadPos;
            public Quaternion UnbindHeadRot;
        }

        private readonly Dictionary<string, Transform> _platforms = new Dictionary<string, Transform>();
        private readonly Dictionary<int, BindingState> _bindings = new Dictionary<int, BindingState>();

        public void RegisterPlatform(string platformId, Transform platform)
        {
            if (string.IsNullOrEmpty(platformId) || platform == null) { return; }
            _platforms[platformId] = platform;
            // Re-attach any existing bindings that were waiting for this id.
            foreach (var kv in _bindings)
            {
                if (kv.Value.Platform == null && kv.Value.PlatformId == platformId)
                {
                    kv.Value.Platform = platform;
                }
            }
        }

        public void UnregisterPlatform(string platformId)
        {
            if (string.IsNullOrEmpty(platformId)) { return; }
            _platforms.Remove(platformId);
            foreach (var kv in _bindings)
            {
                if (kv.Value.PlatformId == platformId)
                {
                    kv.Value.Platform = null;
                }
            }
        }

        public bool TryResolvePlatform(string platformId, out Transform platform)
        {
            return _platforms.TryGetValue(platformId, out platform);
        }

        public bool TryGetBinding(int clientNo, out BindingState state)
        {
            return _bindings.TryGetValue(clientNo, out state);
        }

        public void SetBinding(int clientNo, BindingState state)
        {
            _bindings[clientNo] = state;
        }

        /// <summary>
        /// Mark the binding as unbinding so NetSyncAvatar can blend back to the
        /// network-smoothed head pose. The actual removal happens when the blend
        /// completes (see <see cref="AdvanceUnbind"/>).
        /// </summary>
        public void StartUnbind(int clientNo, Vector3 lastHeadPos, Quaternion lastHeadRot)
        {
            if (!_bindings.TryGetValue(clientNo, out var state)) { return; }
            state.IsUnbinding = true;
            state.UnbindElapsedSeconds = 0f;
            state.UnbindHeadPos = lastHeadPos;
            state.UnbindHeadRot = lastHeadRot;
        }

        /// <summary>
        /// Advance an active unbind blend. Returns true if the binding has been
        /// fully released and the caller should resume the unbound code path.
        /// </summary>
        public bool AdvanceUnbind(int clientNo, float deltaTime)
        {
            if (!_bindings.TryGetValue(clientNo, out var state) || !state.IsUnbinding) { return false; }
            state.UnbindElapsedSeconds += deltaTime;
            if (state.UnbindElapsedSeconds >= UnbindBlendSeconds)
            {
                _bindings.Remove(clientNo);
                return true;
            }
            return false;
        }

        public void RemoveBinding(int clientNo)
        {
            _bindings.Remove(clientNo);
        }

        public void Clear()
        {
            _bindings.Clear();
        }

        // --- Serialization helpers (compact CSV; client variables are strings) ---

        // Format: platformId|posX,posY,posZ|yaw|p0X,p0Y,p0Z|yaw0
        public static string Encode(string platformId, Vector3 rigOffsetPos, float rigOffsetYaw, Vector3 p0, float yaw0)
        {
            var inv = CultureInfo.InvariantCulture;
            return string.Join("|",
                platformId,
                string.Concat(rigOffsetPos.x.ToString("R", inv), ",", rigOffsetPos.y.ToString("R", inv), ",", rigOffsetPos.z.ToString("R", inv)),
                rigOffsetYaw.ToString("R", inv),
                string.Concat(p0.x.ToString("R", inv), ",", p0.y.ToString("R", inv), ",", p0.z.ToString("R", inv)),
                yaw0.ToString("R", inv));
        }

        public static bool TryDecode(string encoded, out string platformId, out Vector3 rigOffsetPos, out float rigOffsetYaw, out Vector3 p0, out float yaw0)
        {
            platformId = null;
            rigOffsetPos = Vector3.zero;
            rigOffsetYaw = 0f;
            p0 = Vector3.zero;
            yaw0 = 0f;
            if (string.IsNullOrEmpty(encoded)) { return false; }

            var parts = encoded.Split('|');
            if (parts.Length != 5) { return false; }

            var inv = CultureInfo.InvariantCulture;
            platformId = parts[0];
            if (!TryParseVec3(parts[1], inv, out rigOffsetPos)) { return false; }
            if (!float.TryParse(parts[2], NumberStyles.Float, inv, out rigOffsetYaw)) { return false; }
            if (!TryParseVec3(parts[3], inv, out p0)) { return false; }
            if (!float.TryParse(parts[4], NumberStyles.Float, inv, out yaw0)) { return false; }
            return true;
        }

        private static bool TryParseVec3(string s, CultureInfo inv, out Vector3 v)
        {
            v = Vector3.zero;
            var p = s.Split(',');
            if (p.Length != 3) { return false; }
            if (!float.TryParse(p[0], NumberStyles.Float, inv, out var x)) { return false; }
            if (!float.TryParse(p[1], NumberStyles.Float, inv, out var y)) { return false; }
            if (!float.TryParse(p[2], NumberStyles.Float, inv, out var z)) { return false; }
            v = new Vector3(x, y, z);
            return true;
        }

        /// <summary>
        /// Yaw of a Transform's rotation around world up, computed from the forward
        /// vector projected onto the XZ plane. Robust to tilt/roll, unlike
        /// <see cref="Transform.eulerAngles"/>.y which decomposes ambiguously.
        /// </summary>
        public static float ExtractYawDegrees(Quaternion rotation)
        {
            var fwd = rotation * Vector3.forward;
            fwd.y = 0f;
            if (fwd.sqrMagnitude < 1e-8f) { return 0f; }
            fwd.Normalize();
            return Mathf.Atan2(fwd.x, fwd.z) * Mathf.Rad2Deg;
        }
    }
}
