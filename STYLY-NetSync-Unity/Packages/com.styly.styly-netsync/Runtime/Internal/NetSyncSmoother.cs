// NetSyncSmoother.cs
// Unified MonoBehaviour that handles smoothing for both remote avatars and Human Presence.
// All comments and documentation are in English per project guidelines.

using System.Collections.Generic;
using UnityEngine;

namespace Styly.NetSync
{
    internal static class NetSyncSmoothingUtil
    {
        public static float ComputeFactor(float deltaTime, float sendRate, float defaultInterval)
        {
            float interval = defaultInterval;
            if (sendRate > 0f)
            {
                interval = 1f / sendRate;
            }
            return Mathf.Clamp01(deltaTime / interval);
        }
    }

    /// <summary>
    /// NetSyncSmoother: single component with two modes
    /// - Avatar mode: smooths multiple transforms using last received targets
    /// - Presence mode: follows a target avatar's physical pose in world space
    /// </summary>
    public class NetSyncSmoother : MonoBehaviour
    {
        public enum Mode { Avatar, Presence }

        [Header("Common")]
        [SerializeField] private Mode _mode = Mode.Avatar;
        [SerializeField] private NetSyncManager _manager; // optional; used to read SendRate
        [SerializeField] private float _defaultPacketInterval = 0.1f; // 10 Hz fallback

        [Header("Avatar (multi-transform)")]
        [SerializeField] private Transform _physical; // local space
        [SerializeField] private Transform _head;      // world space
        [SerializeField] private Transform _rightHand; // world space
        [SerializeField] private Transform _leftHand;  // world space
        [SerializeField] private Transform[] _virtuals; // world space

        // Targets
        private TransformData _targetPhysical;
        private TransformData _targetHead;
        private TransformData _targetRightHand;
        private TransformData _targetLeftHand;
        private List<TransformData> _targetVirtuals;
        private bool _initialized; // for first target snap

        [Header("Presence (single target)")]
        [SerializeField] private NetSyncAvatar _presenceTarget;

        // --- Initialization API ---
        public void InitializeForAvatar(Transform physical, Transform head, Transform rightHand, Transform leftHand, Transform[] virtuals, NetSyncManager manager)
        {
            _mode = Mode.Avatar;
            _physical = physical;
            _head = head;
            _rightHand = rightHand;
            _leftHand = leftHand;
            _virtuals = virtuals;
            _manager = manager;

            int count = (virtuals != null) ? virtuals.Length : 0;
            _targetVirtuals = new List<TransformData>(count);
            for (int i = 0; i < count; i++)
            {
                _targetVirtuals.Add(new TransformData());
            }
            _initialized = false;
        }

        public void InitializeForPresence(NetSyncAvatar target, NetSyncManager manager)
        {
            _mode = Mode.Presence;
            _presenceTarget = target;
            _manager = manager;

            if (_presenceTarget != null)
            {
                transform.position = _presenceTarget.PhysicalPosition;
                transform.rotation = Quaternion.Euler(_presenceTarget.PhysicalRotation);
            }
        }

        // --- Avatar mode: receive targets ---
        internal void SetTarget(ClientTransformData data)
        {
            if (_mode != Mode.Avatar) { return; }

            if (!_initialized)
            {
                ApplyImmediate(data);
                _initialized = true;
            }

            if (data.physical != null) { _targetPhysical = data.physical; }
            if (data.head != null) { _targetHead = data.head; }
            if (data.rightHand != null) { _targetRightHand = data.rightHand; }
            if (data.leftHand != null) { _targetLeftHand = data.leftHand; }

            if (data.virtuals != null && _virtuals != null)
            {
                int count = Mathf.Min(data.virtuals.Count, _virtuals.Length);
                EnsureVirtualTargets(count);
                for (int i = 0; i < count; i++)
                {
                    _targetVirtuals[i] = data.virtuals[i];
                }
            }
        }

        private void EnsureVirtualTargets(int count)
        {
            if (_targetVirtuals == null)
            {
                _targetVirtuals = new List<TransformData>(count);
            }
            while (_targetVirtuals.Count < count)
            {
                _targetVirtuals.Add(new TransformData());
            }
        }

        private void ApplyImmediate(ClientTransformData data)
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

        private float GetPacketInterval()
        {
            if (_manager != null)
            {
                var tsm = _manager.TransformSyncManager; // not a UnityEngine.Object
                if (tsm != null && tsm.SendRate > 0f)
                {
                    return 1f / tsm.SendRate;
                }
            }
            return _defaultPacketInterval;
        }

        private void Update()
        {
            float t = NetSyncSmoothingUtil.ComputeFactor(Time.deltaTime, _manager != null && _manager.TransformSyncManager != null ? _manager.TransformSyncManager.SendRate : 0f, _defaultPacketInterval);

            if (_mode == Mode.Avatar)
            {
                UpdateAvatar(t);
            }
            else if (_mode == Mode.Presence)
            {
                UpdatePresence(t);
            }
        }

        private void UpdateAvatar(float t)
        {
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
                    if (vt == null || target == null) { continue; }

                    Vector3 pos = target.GetPosition();
                    Quaternion rot = Quaternion.Euler(target.GetRotation());
                    vt.position = Vector3.Lerp(vt.position, pos, t);
                    vt.rotation = Quaternion.Slerp(vt.rotation, rot, t);
                }
            }
        }

        private void UpdatePresence(float t)
        {
            if (_presenceTarget == null) { return; }

            Vector3 targetPos = _presenceTarget.PhysicalPosition;
            Quaternion targetRot = Quaternion.Euler(_presenceTarget.PhysicalRotation);
            Transform tr = transform;
            tr.position = Vector3.Lerp(tr.position, targetPos, t);
            tr.rotation = Quaternion.Slerp(tr.rotation, targetRot, t);
        }
    }
}
