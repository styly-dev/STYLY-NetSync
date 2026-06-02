// MovingFloorManager.cs
using System;
using System.Collections.Generic;
using System.Globalization;
using UnityEngine;

namespace Styly.NetSync
{
    /// <summary>
    /// Tracks moving floors for moving-floor-local avatar poses.
    /// A moving floor is any scene-stable Transform known by the same 32-bit id on each client.
    /// </summary>
    internal class MovingFloorManager
    {
        public const string ClientVariableName = NetSyncManager.PrefixForSystem + "movingFloor";

        public const uint UnassignedFloorId = 0u;

        private readonly Dictionary<uint, Transform> _floors = new Dictionary<uint, Transform>();
        private readonly Dictionary<int, uint> _clientPoseFloorIds = new Dictionary<int, uint>();
        private readonly Dictionary<int, uint> _clientFloorIds = new Dictionary<int, uint>();
        private readonly HashSet<(int clientNo, uint floorId)> _missingWarnings = new HashSet<(int clientNo, uint floorId)>();

        private uint _localFloorId;

        public uint LocalFloorId => _localFloorId;
        public bool HasLocalFloor => _localFloorId != UnassignedFloorId;

        public bool RegisterFloor(uint floorId, Transform floor)
        {
            if (floorId == UnassignedFloorId || floor == null)
            {
                return false;
            }

            // Reject duplicate registration with a different Transform so the
            // second component's OnDisable cannot unregister the still-needed
            // first one. Re-registration with the same Transform is idempotent.
            if (_floors.TryGetValue(floorId, out var existing) && existing != null && existing != floor)
            {
                Debug.LogWarning(
                    $"[NetSync] Moving floor id '{FormatFloorId(floorId)}' is already registered to a different Transform. Ignoring duplicate registration.");
                return false;
            }

            _floors[floorId] = floor;
            _missingWarnings.RemoveWhere(key => key.floorId == floorId);
            return true;
        }

        public void UnregisterFloor(uint floorId)
        {
            if (floorId == UnassignedFloorId)
            {
                return;
            }

            _floors.Remove(floorId);
        }

        public bool BoardLocal(uint floorId)
        {
            if (floorId == UnassignedFloorId)
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
            _localFloorId = UnassignedFloorId;
        }

        public bool TryGetLocalFloor(out Transform floor)
        {
            floor = null;
            if (_localFloorId == UnassignedFloorId)
            {
                return false;
            }

            return _floors.TryGetValue(_localFloorId, out floor) && floor != null;
        }

        public void SetClientFloorId(int clientNo, string encodedFloorId)
        {
            if (clientNo <= 0)
            {
                return;
            }

            RemoveWarningKeysForClient(clientNo);
            if (string.IsNullOrEmpty(encodedFloorId))
            {
                _clientFloorIds.Remove(clientNo);
                return;
            }

            if (!TryParseFloorId(encodedFloorId, out var floorId))
            {
                _clientFloorIds.Remove(clientNo);
                Debug.LogWarning(
                    $"[NetSync] Ignoring invalid moving floor id '{encodedFloorId}' for client#{clientNo}.");
                return;
            }

            _clientFloorIds[clientNo] = floorId;
        }

        public void SetClientPoseFloorId(int clientNo, uint floorId, bool movingFloorLocal)
        {
            if (clientNo <= 0)
            {
                return;
            }

            bool hadPoseFloorId = _clientPoseFloorIds.TryGetValue(clientNo, out var previousFloorId);
            if (!movingFloorLocal || floorId == UnassignedFloorId)
            {
                if (hadPoseFloorId)
                {
                    _clientPoseFloorIds.Remove(clientNo);
                    RemoveWarningKeysForClient(clientNo);
                }
                return;
            }

            if (hadPoseFloorId && previousFloorId == floorId)
            {
                return;
            }

            _clientPoseFloorIds[clientNo] = floorId;
            RemoveWarningKeysForClient(clientNo);
        }

        public void RemoveClient(int clientNo)
        {
            _clientPoseFloorIds.Remove(clientNo);
            _clientFloorIds.Remove(clientNo);
            RemoveWarningKeysForClient(clientNo);
        }

        public bool TryGetFloorForClient(int clientNo, bool warnIfMissingId, out Transform floor)
        {
            floor = null;
            if (!_clientPoseFloorIds.TryGetValue(clientNo, out var floorId) &&
                !_clientFloorIds.TryGetValue(clientNo, out floorId))
            {
                floorId = UnassignedFloorId;
            }

            if (floorId == UnassignedFloorId)
            {
                if (warnIfMissingId)
                {
                    WarnOnce(
                        clientNo,
                        UnassignedFloorId,
                        $"[NetSync] Moving-floor-local pose received for client#{clientNo}, but its floor id is missing. Holding the last applied avatar pose until the floor id is received.");
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
                $"[NetSync] Moving floor '{FormatFloorId(floorId)}' for client#{clientNo} is not registered. Holding the last applied avatar pose until it is registered.");
            floor = null;
            return false;
        }

        public void ClearClientStates(bool clearLocal)
        {
            _clientPoseFloorIds.Clear();
            _clientFloorIds.Clear();
            _missingWarnings.Clear();
            if (clearLocal)
            {
                _localFloorId = UnassignedFloorId;
            }
        }

        public static string FormatFloorId(uint floorId)
        {
            return floorId == UnassignedFloorId ? "(unassigned)" : $"0x{floorId:X8}";
        }

        public static string EncodeFloorId(uint floorId)
        {
            return floorId == UnassignedFloorId ? string.Empty : $"0x{floorId:X8}";
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

        private static bool TryParseFloorId(string encodedFloorId, out uint floorId)
        {
            floorId = UnassignedFloorId;
            if (string.IsNullOrWhiteSpace(encodedFloorId))
            {
                return false;
            }

            string s = encodedFloorId.Trim();
            if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ||
                s.StartsWith("#", StringComparison.Ordinal))
            {
                s = s.Substring(s[0] == '#' ? 1 : 2);
                return uint.TryParse(s, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out floorId) &&
                    floorId != UnassignedFloorId;
            }

            return uint.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out floorId) &&
                floorId != UnassignedFloorId;
        }

        private void WarnOnce(int clientNo, uint floorId, string message)
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
