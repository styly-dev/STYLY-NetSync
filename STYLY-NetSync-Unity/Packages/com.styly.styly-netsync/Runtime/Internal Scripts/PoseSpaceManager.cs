using System;
using System.Collections.Generic;
using UnityEngine;

namespace Styly.NetSync
{
    internal sealed class PoseSpaceManager
    {
        private readonly Action _clearPoseSpaceDrivenSnapshots;
        private readonly Action<int> _clearClientDrivenSnapshots;
        private readonly Action<uint> _clearObjectSnapshots;

        private readonly Dictionary<string, INetSyncPoseSpace> _poseSpaces = new Dictionary<string, INetSyncPoseSpace>();
        private readonly Dictionary<int, string> _clientPoseSpaceIds = new Dictionary<int, string>();
        private readonly Dictionary<uint, string> _objectPoseSpaceIds = new Dictionary<uint, string>();
        private string _defaultPoseSpaceId;
        private string _localAvatarPoseSpaceId;

        public PoseSpaceManager(
            Action clearPoseSpaceDrivenSnapshots,
            Action<int> clearClientDrivenSnapshots,
            Action<uint> clearObjectSnapshots)
        {
            _clearPoseSpaceDrivenSnapshots = clearPoseSpaceDrivenSnapshots;
            _clearClientDrivenSnapshots = clearClientDrivenSnapshots;
            _clearObjectSnapshots = clearObjectSnapshots;
        }

        public static bool IsWorldPoseSpaceId(string spaceId)
        {
            return string.Equals(spaceId, NetSyncWorldPoseSpace.Instance.SpaceId, StringComparison.Ordinal);
        }

        public static bool IsWorldPoseSpace(INetSyncPoseSpace poseSpace)
        {
            return poseSpace == null || poseSpace.IsIdentity || ReferenceEquals(poseSpace, NetSyncWorldPoseSpace.Instance) || IsWorldPoseSpaceId(poseSpace.SpaceId);
        }

        public static string NormalizePoseSpaceId(string spaceId)
        {
            if (string.IsNullOrEmpty(spaceId) || IsWorldPoseSpaceId(spaceId))
            {
                return null;
            }

            return spaceId;
        }

        public void RegisterPoseSpace(string spaceId, Transform origin)
        {
            if (string.IsNullOrEmpty(spaceId))
            {
                return;
            }

            RegisterPoseSpace(new TransformNetSyncPoseSpace(spaceId, origin));
        }

        public void RegisterPoseSpace(INetSyncPoseSpace poseSpace)
        {
            if (poseSpace == null || string.IsNullOrEmpty(poseSpace.SpaceId))
            {
                return;
            }

            _poseSpaces[poseSpace.SpaceId] = poseSpace;
            _clearPoseSpaceDrivenSnapshots?.Invoke();
        }

        public void UnregisterPoseSpace(string spaceId)
        {
            if (string.IsNullOrEmpty(spaceId) || IsWorldPoseSpaceId(spaceId))
            {
                return;
            }

            _poseSpaces.Remove(spaceId);
            _clearPoseSpaceDrivenSnapshots?.Invoke();
        }

        public void SetDefaultPoseSpace(string spaceId)
        {
            _defaultPoseSpaceId = NormalizePoseSpaceId(spaceId);
            _clearPoseSpaceDrivenSnapshots?.Invoke();
        }

        public void ClearDefaultPoseSpace()
        {
            _defaultPoseSpaceId = null;
            _clearPoseSpaceDrivenSnapshots?.Invoke();
        }

        public void SetLocalAvatarPoseSpace(string spaceId)
        {
            _localAvatarPoseSpaceId = NormalizePoseSpaceId(spaceId);
        }

        public void ClearLocalAvatarPoseSpace()
        {
            _localAvatarPoseSpaceId = null;
        }

        public void SetClientPoseSpace(int clientNo, string spaceId)
        {
            if (clientNo <= 0)
            {
                return;
            }

            var normalized = NormalizePoseSpaceId(spaceId);
            if (string.IsNullOrEmpty(normalized))
            {
                _clientPoseSpaceIds.Remove(clientNo);
                _clearClientDrivenSnapshots?.Invoke(clientNo);
                return;
            }

            _clientPoseSpaceIds[clientNo] = normalized;
            _clearClientDrivenSnapshots?.Invoke(clientNo);
        }

        public void ClearClientPoseSpace(int clientNo)
        {
            _clientPoseSpaceIds.Remove(clientNo);
            _clearClientDrivenSnapshots?.Invoke(clientNo);
        }

        public void SetObjectPoseSpace(uint objectId, string spaceId)
        {
            if (objectId == 0u)
            {
                return;
            }

            var normalized = NormalizePoseSpaceId(spaceId);
            if (string.IsNullOrEmpty(normalized))
            {
                _objectPoseSpaceIds.Remove(objectId);
                _clearObjectSnapshots?.Invoke(objectId);
                return;
            }

            _objectPoseSpaceIds[objectId] = normalized;
            _clearObjectSnapshots?.Invoke(objectId);
        }

        public void ClearObjectPoseSpace(uint objectId)
        {
            _objectPoseSpaceIds.Remove(objectId);
            _clearObjectSnapshots?.Invoke(objectId);
        }

        public void NotifyObjectPoseSpaceChanged(NetSyncObject obj)
        {
            if (obj == null || obj.ObjectId == 0u)
            {
                return;
            }

            _clearObjectSnapshots?.Invoke(obj.ObjectId);
        }

        public INetSyncPoseSpace ResolveLocalAvatarPoseSpace()
        {
            if (!string.IsNullOrEmpty(_localAvatarPoseSpaceId))
            {
                return ResolvePoseSpaceById(_localAvatarPoseSpaceId);
            }

            return ResolveDefaultPoseSpace();
        }

        public INetSyncPoseSpace ResolveClientPoseSpace(int clientNo)
        {
            if (clientNo > 0 && _clientPoseSpaceIds.TryGetValue(clientNo, out var spaceId))
            {
                return ResolvePoseSpaceById(spaceId);
            }

            return ResolveDefaultPoseSpace();
        }

        public INetSyncPoseSpace ResolveObjectPoseSpace(NetSyncObject obj)
        {
            if (obj != null)
            {
                var objectId = obj.ObjectId;
                if (objectId != 0u && _objectPoseSpaceIds.TryGetValue(objectId, out var spaceId))
                {
                    return ResolvePoseSpaceById(spaceId);
                }

                if (!string.IsNullOrEmpty(obj.PoseSpaceId))
                {
                    return ResolvePoseSpaceById(obj.PoseSpaceId);
                }
            }

            return ResolveDefaultPoseSpace();
        }

        public bool HasPoseSpace(string spaceId)
        {
            return !string.IsNullOrEmpty(spaceId) && _poseSpaces.ContainsKey(spaceId);
        }

        public void ClearRoomScopedSelections()
        {
            _localAvatarPoseSpaceId = null;
            _clientPoseSpaceIds.Clear();
            _objectPoseSpaceIds.Clear();
        }

        private INetSyncPoseSpace ResolveDefaultPoseSpace()
        {
            return ResolvePoseSpaceById(_defaultPoseSpaceId);
        }

        private INetSyncPoseSpace ResolvePoseSpaceById(string spaceId)
        {
            if (string.IsNullOrEmpty(spaceId) || IsWorldPoseSpaceId(spaceId))
            {
                return NetSyncWorldPoseSpace.Instance;
            }

            if (_poseSpaces.TryGetValue(spaceId, out var poseSpace) && poseSpace != null)
            {
                return poseSpace;
            }

            return NetSyncWorldPoseSpace.Instance;
        }
    }
}
