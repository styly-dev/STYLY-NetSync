using System;
using UnityEngine;

namespace Styly.NetSync
{
    public interface INetSyncPoseSpace
    {
        string SpaceId { get; }
        bool TryWorldToSpace(Vector3 worldPosition, Quaternion worldRotation, out Vector3 spacePosition, out Quaternion spaceRotation);
        bool TrySpaceToWorld(Vector3 spacePosition, Quaternion spaceRotation, out Vector3 worldPosition, out Quaternion worldRotation);
    }

    public sealed class NetSyncWorldPoseSpace : INetSyncPoseSpace
    {
        public static readonly NetSyncWorldPoseSpace Instance = new NetSyncWorldPoseSpace();

        public string SpaceId => "world";

        private NetSyncWorldPoseSpace()
        {
        }

        public bool TryWorldToSpace(Vector3 worldPosition, Quaternion worldRotation, out Vector3 spacePosition, out Quaternion spaceRotation)
        {
            spacePosition = worldPosition;
            spaceRotation = worldRotation;
            return true;
        }

        public bool TrySpaceToWorld(Vector3 spacePosition, Quaternion spaceRotation, out Vector3 worldPosition, out Quaternion worldRotation)
        {
            worldPosition = spacePosition;
            worldRotation = spaceRotation;
            return true;
        }
    }

    public sealed class TransformNetSyncPoseSpace : INetSyncPoseSpace
    {
        private readonly string _spaceId;
        private readonly Transform _origin;

        public string SpaceId => _spaceId;
        public Transform Origin => _origin;

        public TransformNetSyncPoseSpace(string spaceId, Transform origin)
        {
            if (string.IsNullOrEmpty(spaceId))
            {
                throw new ArgumentException("Pose space ID cannot be null or empty.", nameof(spaceId));
            }

            _spaceId = spaceId;
            _origin = origin;
        }

        public bool TryWorldToSpace(Vector3 worldPosition, Quaternion worldRotation, out Vector3 spacePosition, out Quaternion spaceRotation)
        {
            if (_origin == null)
            {
                spacePosition = worldPosition;
                spaceRotation = worldRotation;
                return false;
            }

            spacePosition = _origin.InverseTransformPoint(worldPosition);
            spaceRotation = Quaternion.Inverse(_origin.rotation) * worldRotation;
            return true;
        }

        public bool TrySpaceToWorld(Vector3 spacePosition, Quaternion spaceRotation, out Vector3 worldPosition, out Quaternion worldRotation)
        {
            if (_origin == null)
            {
                worldPosition = spacePosition;
                worldRotation = spaceRotation;
                return false;
            }

            worldPosition = _origin.TransformPoint(spacePosition);
            worldRotation = _origin.rotation * spaceRotation;
            return true;
        }
    }
}
