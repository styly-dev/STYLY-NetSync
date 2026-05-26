// MovingFloorManager.cs
using System.Collections.Generic;
using UnityEngine;

namespace Styly.NetSync
{
    /// <summary>
    /// Tracks moving floors for protocol v5 moving-floor-local avatar poses.
    /// A moving floor is any scene-stable Transform known by the same string id on each client.
    /// </summary>
    internal class MovingFloorManager
    {
        public const string ClientVariableName = NetSyncManager.PrefixForSystem + "movingFloor";

        private readonly Dictionary<string, Transform> _floors = new Dictionary<string, Transform>();
        private readonly Dictionary<int, string> _clientFloorIds = new Dictionary<int, string>();
        private readonly HashSet<(int clientNo, string floorId)> _missingWarnings = new HashSet<(int clientNo, string floorId)>();

        private string _localFloorId;

        public string LocalFloorId => _localFloorId;
        public bool HasLocalFloor => !string.IsNullOrEmpty(_localFloorId);

        public bool RegisterFloor(string floorId, Transform floor)
        {
            if (string.IsNullOrEmpty(floorId) || floor == null)
            {
                return false;
            }

            // Reject duplicate registration with a different Transform so the
            // second component's OnDisable cannot unregister the still-needed
            // first one. Re-registration with the same Transform is idempotent.
            if (_floors.TryGetValue(floorId, out var existing) && existing != null && existing != floor)
            {
                Debug.LogWarning(
                    $"[NetSync] Moving floor id '{floorId}' is already registered to a different Transform. Ignoring duplicate registration.");
                return false;
            }

            _floors[floorId] = floor;
            _missingWarnings.RemoveWhere(key => key.floorId == floorId);
            return true;
        }

        public void UnregisterFloor(string floorId)
        {
            if (string.IsNullOrEmpty(floorId))
            {
                return;
            }

            _floors.Remove(floorId);
        }

        public bool BoardLocal(string floorId)
        {
            if (string.IsNullOrEmpty(floorId))
            {
                return false;
            }

            if (!_floors.TryGetValue(floorId, out var floor) || floor == null)
            {
                return false;
            }

            _localFloorId = floorId;
            return true;
        }

        public void LeaveLocal()
        {
            _localFloorId = null;
        }

        public bool TryGetLocalFloor(out Transform floor)
        {
            floor = null;
            if (string.IsNullOrEmpty(_localFloorId))
            {
                return false;
            }

            return _floors.TryGetValue(_localFloorId, out floor) && floor != null;
        }

        public void SetClientFloorId(int clientNo, string floorId)
        {
            if (clientNo <= 0)
            {
                return;
            }

            RemoveWarningKeysForClient(clientNo);
            if (string.IsNullOrEmpty(floorId))
            {
                _clientFloorIds.Remove(clientNo);
                return;
            }

            _clientFloorIds[clientNo] = floorId;
        }

        public void RemoveClient(int clientNo)
        {
            _clientFloorIds.Remove(clientNo);
            RemoveWarningKeysForClient(clientNo);
        }

        public bool TryGetFloorForClient(int clientNo, bool warnIfMissingId, out Transform floor)
        {
            floor = null;
            if (!_clientFloorIds.TryGetValue(clientNo, out var floorId) || string.IsNullOrEmpty(floorId))
            {
                if (warnIfMissingId)
                {
                    WarnOnce(
                        clientNo,
                        "<missing>",
                        $"[NetSync] Moving-floor-local pose received for client#{clientNo}, but {ClientVariableName} is missing. Holding the last applied avatar pose until the floor id is received.");
                }
                return false;
            }

            if (_floors.TryGetValue(floorId, out floor) && floor != null)
            {
                return true;
            }

            WarnOnce(
                clientNo,
                floorId,
                $"[NetSync] Moving floor '{floorId}' for client#{clientNo} is not registered. Holding the last applied avatar pose until it is registered.");
            floor = null;
            return false;
        }

        public void ClearClientStates(bool clearLocal)
        {
            _clientFloorIds.Clear();
            _missingWarnings.Clear();
            if (clearLocal)
            {
                _localFloorId = null;
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

        private void WarnOnce(int clientNo, string floorId, string message)
        {
            if (_missingWarnings.Add((clientNo, floorId)))
            {
                Debug.LogWarning(message);
            }
        }

        private void RemoveWarningKeysForClient(int clientNo)
        {
            _missingWarnings.RemoveWhere(key => key.clientNo == clientNo);
        }
    }
}
