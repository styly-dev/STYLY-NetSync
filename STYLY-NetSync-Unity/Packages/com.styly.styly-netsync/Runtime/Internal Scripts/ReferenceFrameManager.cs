// ReferenceFrameManager.cs
using System.Collections.Generic;
using UnityEngine;

namespace Styly.NetSync
{
    /// <summary>
    /// Tracks optional reference frames for protocol v5 reference-frame-local avatar poses.
    /// A reference frame is any scene-stable Transform known by the same string id on each client.
    /// </summary>
    internal class ReferenceFrameManager
    {
        public const string ClientVariableName = NetSyncManager.PrefixForSystem + "referenceFrame";

        private readonly Dictionary<string, Transform> _frames = new Dictionary<string, Transform>();
        private readonly Dictionary<int, string> _clientFrameIds = new Dictionary<int, string>();
        private readonly HashSet<string> _missingWarnings = new HashSet<string>();

        private string _localFrameId;

        public string LocalFrameId => _localFrameId;
        public bool HasLocalFrame => !string.IsNullOrEmpty(_localFrameId);

        public bool RegisterFrame(string frameId, Transform frame)
        {
            if (string.IsNullOrEmpty(frameId) || frame == null)
            {
                return false;
            }

            _frames[frameId] = frame;
            _missingWarnings.RemoveWhere(key => key.EndsWith(":" + frameId));
            return true;
        }

        public void UnregisterFrame(string frameId)
        {
            if (string.IsNullOrEmpty(frameId))
            {
                return;
            }

            _frames.Remove(frameId);
        }

        public bool AttachLocal(string frameId)
        {
            if (string.IsNullOrEmpty(frameId))
            {
                return false;
            }

            if (!_frames.TryGetValue(frameId, out var frame) || frame == null)
            {
                return false;
            }

            _localFrameId = frameId;
            return true;
        }

        public void DetachLocal()
        {
            _localFrameId = null;
        }

        public bool TryGetLocalFrame(out Transform frame)
        {
            frame = null;
            if (string.IsNullOrEmpty(_localFrameId))
            {
                return false;
            }

            return _frames.TryGetValue(_localFrameId, out frame) && frame != null;
        }

        public void SetClientFrameId(int clientNo, string frameId)
        {
            if (clientNo <= 0)
            {
                return;
            }

            RemoveWarningKeysForClient(clientNo);
            if (string.IsNullOrEmpty(frameId))
            {
                _clientFrameIds.Remove(clientNo);
                return;
            }

            _clientFrameIds[clientNo] = frameId;
        }

        public void RemoveClient(int clientNo)
        {
            _clientFrameIds.Remove(clientNo);
            RemoveWarningKeysForClient(clientNo);
        }

        public bool TryGetFrameForClient(int clientNo, bool warnIfMissingId, out Transform frame)
        {
            frame = null;
            if (!_clientFrameIds.TryGetValue(clientNo, out var frameId) || string.IsNullOrEmpty(frameId))
            {
                if (warnIfMissingId)
                {
                    WarnOnce(
                        clientNo,
                        "<missing>",
                        $"[NetSync] Reference-frame-local pose received for client#{clientNo}, but {ClientVariableName} is missing. Holding the last applied avatar pose until the frame id is received.");
                }
                return false;
            }

            if (_frames.TryGetValue(frameId, out frame) && frame != null)
            {
                return true;
            }

            WarnOnce(
                clientNo,
                frameId,
                $"[NetSync] Reference frame '{frameId}' for client#{clientNo} is not registered. Holding the last applied avatar pose until it is registered.");
            frame = null;
            return false;
        }

        public void ClearClientStates(bool clearLocal)
        {
            _clientFrameIds.Clear();
            _missingWarnings.Clear();
            if (clearLocal)
            {
                _localFrameId = null;
            }
        }

        /// <summary>
        /// Yaw of a rotation around world up, computed from the projected forward vector.
        /// </summary>
        public static float ExtractYawDegrees(Quaternion rotation)
        {
            var fwd = rotation * Vector3.forward;
            fwd.y = 0f;
            if (fwd.sqrMagnitude < 1e-8f)
            {
                return 0f;
            }

            fwd.Normalize();
            return Mathf.Atan2(fwd.x, fwd.z) * Mathf.Rad2Deg;
        }

        private void WarnOnce(int clientNo, string frameId, string message)
        {
            var warningKey = clientNo.ToString() + ":" + frameId;
            if (_missingWarnings.Add(warningKey))
            {
                Debug.LogWarning(message);
            }
        }

        private void RemoveWarningKeysForClient(int clientNo)
        {
            var prefix = clientNo.ToString() + ":";
            _missingWarnings.RemoveWhere(key => key.StartsWith(prefix));
        }
    }
}
