// NetSyncAvatarSmoother.cs
using System.Collections.Generic;
using UnityEngine;

namespace Styly.NetSync
{
    /// <summary>
    /// Helper component that smooths remote avatar transforms between network updates.
    /// Physical transform is handled in local space, others in world space.
    /// </summary>
    internal class NetSyncAvatarSmoother
    {
        private Transform _physical;
        private Transform _head;
        private Transform _rightHand;
        private Transform _leftHand;
        private Transform[] _virtuals;

        private TransformData _targetPhysical;
        private TransformData _targetHead;
        private TransformData _targetRightHand;
        private TransformData _targetLeftHand;
        private List<TransformData> _targetVirtuals;

        // Single flag to track if first update has been received
        private bool _initialized = false;

        private const float PacketInterval = 0.1f; // 10 Hz

        /// <summary>
        /// Initialize with transform references.
        /// </summary>
        public void Initialize(
            Transform physical,
            Transform head,
            Transform rightHand,
            Transform leftHand,
            Transform[] virtuals)
        {
            _physical = physical;
            _head = head;
            _rightHand = rightHand;
            _leftHand = leftHand;
            _virtuals = virtuals;

            int count = virtuals != null ? virtuals.Length : 0;
            _targetVirtuals = new List<TransformData>(count);
            for (int i = 0; i < count; i++)
            {
                _targetVirtuals.Add(new TransformData());
            }
        }

        /// <summary>
        /// Store new target data when network packet arrives.
        /// </summary>
        public void SetTarget(ClientTransformData data)
        {
            // On first update, set positions immediately to avoid interpolation from 0,0,0
            if (!_initialized)
            {
                ApplyTransformsImmediate(data);
                _initialized = true;
            }

            // Update targets for interpolation

            if (data.physical != null)
            {
                _targetPhysical = data.physical;
            }

            if (data.head != null)
            {
                _targetHead = data.head;
            }

            if (data.rightHand != null)
            {
                _targetRightHand = data.rightHand;
            }

            if (data.leftHand != null)
            {
                _targetLeftHand = data.leftHand;
            }

            if (data.virtuals != null && _virtuals != null)
            {
                int count = Mathf.Min(data.virtuals.Count, _virtuals.Length);
                for (int i = 0; i < count; i++)
                {
                    _targetVirtuals[i] = data.virtuals[i];
                }
            }
        }

        /// <summary>
        /// Apply transforms immediately without interpolation (for first update).
        /// </summary>
        private void ApplyTransformsImmediate(ClientTransformData data)
        {
            if (data.physical != null && _physical != null)
            {
                _physical.localPosition = data.physical.GetPosition();
                _physical.localRotation = Quaternion.Euler(data.physical.GetRotation());
            }

            if (data.head != null && _head != null)
            {
                _head.position = data.head.GetPosition();
                _head.rotation = Quaternion.Euler(data.head.GetRotation());
            }

            if (data.rightHand != null && _rightHand != null)
            {
                _rightHand.position = data.rightHand.GetPosition();
                _rightHand.rotation = Quaternion.Euler(data.rightHand.GetRotation());
            }

            if (data.leftHand != null && _leftHand != null)
            {
                _leftHand.position = data.leftHand.GetPosition();
                _leftHand.rotation = Quaternion.Euler(data.leftHand.GetRotation());
            }

            if (data.virtuals != null && _virtuals != null)
            {
                int count = Mathf.Min(data.virtuals.Count, _virtuals.Length);
                for (int i = 0; i < count; i++)
                {
                    if (_virtuals[i] != null)
                    {
                        _virtuals[i].position = data.virtuals[i].GetPosition();
                        _virtuals[i].rotation = Quaternion.Euler(data.virtuals[i].GetRotation());
                    }
                }
            }
        }

        /// <summary>
        /// Interpolate transforms towards their targets.
        /// </summary>
        public void Update(float deltaTime)
        {
            float t = Mathf.Clamp01(deltaTime / PacketInterval);

            if (_physical != null && _targetPhysical != null)
            {
                Vector3 pos = _targetPhysical.GetPosition();
                Quaternion rot = Quaternion.Euler(_targetPhysical.GetRotation());
                _physical.localPosition = Vector3.Lerp(_physical.localPosition, pos, t);
                _physical.localRotation = Quaternion.Slerp(_physical.localRotation, rot, t);
            }

            if (_head != null && _targetHead != null)
            {
                Vector3 pos = _targetHead.GetPosition();
                Quaternion rot = Quaternion.Euler(_targetHead.GetRotation());
                _head.position = Vector3.Lerp(_head.position, pos, t);
                _head.rotation = Quaternion.Slerp(_head.rotation, rot, t);
            }

            if (_rightHand != null && _targetRightHand != null)
            {
                Vector3 pos = _targetRightHand.GetPosition();
                Quaternion rot = Quaternion.Euler(_targetRightHand.GetRotation());
                _rightHand.position = Vector3.Lerp(_rightHand.position, pos, t);
                _rightHand.rotation = Quaternion.Slerp(_rightHand.rotation, rot, t);
            }

            if (_leftHand != null && _targetLeftHand != null)
            {
                Vector3 pos = _targetLeftHand.GetPosition();
                Quaternion rot = Quaternion.Euler(_targetLeftHand.GetRotation());
                _leftHand.position = Vector3.Lerp(_leftHand.position, pos, t);
                _leftHand.rotation = Quaternion.Slerp(_leftHand.rotation, rot, t);
            }

            if (_virtuals != null && _targetVirtuals != null)
            {
                int count = Mathf.Min(_virtuals.Length, _targetVirtuals.Count);
                for (int i = 0; i < count; i++)
                {
                    Transform vt = _virtuals[i];
                    TransformData target = _targetVirtuals[i];
                    if (vt == null || target == null) continue;

                    Vector3 pos = target.GetPosition();
                    Quaternion rot = Quaternion.Euler(target.GetRotation());
                    vt.position = Vector3.Lerp(vt.position, pos, t);
                    vt.rotation = Quaternion.Slerp(vt.rotation, rot, t);
                }
            }
        }
    }
}