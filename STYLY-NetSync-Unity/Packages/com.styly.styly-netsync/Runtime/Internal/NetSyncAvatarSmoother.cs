// NetSyncAvatarSmoother.cs
using System.Collections.Generic;
using UnityEngine;

namespace Styly.NetSync
{
    /// <summary>
    /// Helper component that smooths remote avatar transforms between network updates.
    /// Physical transform is handled in local space, others in world space.
    /// </summary>
    public class NetSyncAvatarSmoother
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

        private float _physicalTimestamp;
        private float _headTimestamp;
        private float _rightHandTimestamp;
        private float _leftHandTimestamp;
        private List<float> _virtualTimestamps;

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
            _virtualTimestamps = new List<float>(count);
            for (int i = 0; i < count; i++)
            {
                _targetVirtuals.Add(new TransformData());
                _virtualTimestamps.Add(0f);
            }
        }

        /// <summary>
        /// Store new target data when network packet arrives.
        /// </summary>
        public void SetTarget(ClientTransformData data)
        {
            float now = Time.time;

            if (data.physical != null)
            {
                _targetPhysical = data.physical;
                _physicalTimestamp = now;
            }

            if (data.head != null)
            {
                _targetHead = data.head;
                _headTimestamp = now;
            }

            if (data.rightHand != null)
            {
                _targetRightHand = data.rightHand;
                _rightHandTimestamp = now;
            }

            if (data.leftHand != null)
            {
                _targetLeftHand = data.leftHand;
                _leftHandTimestamp = now;
            }

            if (data.virtuals != null && _virtuals != null)
            {
                int count = Mathf.Min(data.virtuals.Count, _virtuals.Length);
                for (int i = 0; i < count; i++)
                {
                    _targetVirtuals[i] = data.virtuals[i];
                    _virtualTimestamps[i] = now;
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
