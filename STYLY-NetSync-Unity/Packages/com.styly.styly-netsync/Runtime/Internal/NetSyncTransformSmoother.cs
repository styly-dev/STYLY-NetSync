// NetSyncTransformSmoother.cs
using System.Collections.Generic;
using UnityEngine;

namespace Styly.NetSync
{
    /// <summary>
    /// General-purpose transform smoother used for both Remote Avatar and Human Presence.
    /// - Supports multiple tracked transforms with individual coordinate spaces (World/Local).
    /// - Targets are updated when network packets arrive; interpolation happens in Update.
    /// - For the first packet, values are applied immediately to avoid snapping from the origin.
    ///
    /// NOTE: Do not use null propagation with UnityEngine.Object. All Unity API calls are on main thread.
    /// </summary>
    internal class NetSyncTransformSmoother
    {
        private const float DefaultPacketInterval = 0.1f;
        public enum SpaceMode { World, Local }

        private class Entry
        {
            public Transform Transform;
            public SpaceMode Space;
            public Vector3 TargetPosition;
            public Quaternion TargetRotation;
            public bool HasTarget;

            public Entry(Transform t, SpaceMode space)
            {
                Transform = t;
                Space = space;
                TargetPosition = Vector3.zero;
                TargetRotation = Quaternion.identity;
                HasTarget = false;
            }
        }

        // Interpolation time constant derived from expected packet interval
        private readonly float _packetInterval;

        // Tracked entries in a fixed order for avatar usage
        private Entry _physical;     // Local space
        private Entry _head;         // World space
        private Entry _rightHand;    // World space
        private Entry _leftHand;     // World space
        private readonly List<Entry> _virtuals = new List<Entry>(8);

        // Single-target mode (Human Presence)
        private Entry _single;

        private bool _initialized;

        public NetSyncTransformSmoother(float packetIntervalSeconds = DefaultPacketInterval)
        {
            _packetInterval = packetIntervalSeconds <= 0f ? DefaultPacketInterval : packetIntervalSeconds;
            _initialized = false;
        }

        /// <summary>
        /// Configure entries for Remote Avatar usage.
        /// </summary>
        public void InitializeForAvatar(
            Transform physical,
            Transform head,
            Transform rightHand,
            Transform leftHand,
            Transform[] virtuals)
        {
            _single = null;

            _physical = physical != null ? new Entry(physical, SpaceMode.Local) : null;
            _head = head != null ? new Entry(head, SpaceMode.World) : null;
            _rightHand = rightHand != null ? new Entry(rightHand, SpaceMode.World) : null;
            _leftHand = leftHand != null ? new Entry(leftHand, SpaceMode.World) : null;

            _virtuals.Clear();
            if (virtuals != null)
            {
                for (int i = 0; i < virtuals.Length; i++)
                {
                    Transform t = virtuals[i];
                    if (t != null)
                    {
                        _virtuals.Add(new Entry(t, SpaceMode.World));
                    }
                    else
                    {
                        _virtuals.Add(null);
                    }
                }
            }

            _initialized = false;
        }

        /// <summary>
        /// Configure a single transform for Human Presence usage.
        /// </summary>
        public void InitializeForSingle(Transform transform, SpaceMode space)
        {
            _physical = null;
            _head = null;
            _rightHand = null;
            _leftHand = null;
            _virtuals.Clear();

            _single = transform != null ? new Entry(transform, space) : null;
            _initialized = false;
        }

        /// <summary>
        /// Update avatar targets from a network packet.
        /// </summary>
        public void SetTargets(ClientTransformData data)
        {
            if (data == null) { return; }

            // First update: apply immediately to avoid jump from origin
            if (!_initialized)
            {
                ApplyImmediate(data);
                _initialized = true;
            }

            if (data.physical != null && _physical != null && _physical.Transform != null)
            {
                _physical.TargetPosition = data.physical.GetPosition();
                _physical.TargetRotation = Quaternion.Euler(data.physical.GetRotation());
                _physical.HasTarget = true;
            }

            if (data.head != null && _head != null && _head.Transform != null)
            {
                _head.TargetPosition = data.head.GetPosition();
                _head.TargetRotation = Quaternion.Euler(data.head.GetRotation());
                _head.HasTarget = true;
            }

            if (data.rightHand != null && _rightHand != null && _rightHand.Transform != null)
            {
                _rightHand.TargetPosition = data.rightHand.GetPosition();
                _rightHand.TargetRotation = Quaternion.Euler(data.rightHand.GetRotation());
                _rightHand.HasTarget = true;
            }

            if (data.leftHand != null && _leftHand != null && _leftHand.Transform != null)
            {
                _leftHand.TargetPosition = data.leftHand.GetPosition();
                _leftHand.TargetRotation = Quaternion.Euler(data.leftHand.GetRotation());
                _leftHand.HasTarget = true;
            }

            if (data.virtuals != null && _virtuals != null)
            {
                int count = data.virtuals.Count < _virtuals.Count ? data.virtuals.Count : _virtuals.Count;
                for (int i = 0; i < count; i++)
                {
                    Entry entry = _virtuals[i];
                    TransformData td = data.virtuals[i];
                    if (entry == null || entry.Transform == null || td == null) { continue; }
                    entry.TargetPosition = td.GetPosition();
                    entry.TargetRotation = Quaternion.Euler(td.GetRotation());
                    entry.HasTarget = true;
                }
            }
        }

        /// <summary>
        /// Update the single-target smoother from a world/local pose.
        /// </summary>
        public void SetSingleTarget(Vector3 position, Vector3 eulerRotation)
        {
            if (_single == null || _single.Transform == null)
            {
                return;
            }

            if (!_initialized)
            {
                if (_single.Space == SpaceMode.World)
                {
                    _single.Transform.position = position;
                    _single.Transform.rotation = Quaternion.Euler(eulerRotation);
                }
                else
                {
                    _single.Transform.localPosition = position;
                    _single.Transform.localRotation = Quaternion.Euler(eulerRotation);
                }
                _initialized = true;
            }

            _single.TargetPosition = position;
            _single.TargetRotation = Quaternion.Euler(eulerRotation);
            _single.HasTarget = true;
        }

        /// <summary>
        /// Apply transforms immediately on first update (avatar mode).
        /// </summary>
        private void ApplyImmediate(ClientTransformData data)
        {
            if (data == null) { return; }

            if (_physical != null && _physical.Transform != null && data.physical != null)
            {
                _physical.Transform.localPosition = data.physical.GetPosition();
                _physical.Transform.localRotation = Quaternion.Euler(data.physical.GetRotation());
            }

            if (_head != null && _head.Transform != null && data.head != null)
            {
                _head.Transform.position = data.head.GetPosition();
                _head.Transform.rotation = Quaternion.Euler(data.head.GetRotation());
            }

            if (_rightHand != null && _rightHand.Transform != null && data.rightHand != null)
            {
                _rightHand.Transform.position = data.rightHand.GetPosition();
                _rightHand.Transform.rotation = Quaternion.Euler(data.rightHand.GetRotation());
            }

            if (_leftHand != null && _leftHand.Transform != null && data.leftHand != null)
            {
                _leftHand.Transform.position = data.leftHand.GetPosition();
                _leftHand.Transform.rotation = Quaternion.Euler(data.leftHand.GetRotation());
            }

            if (data.virtuals != null && _virtuals != null)
            {
                int count = data.virtuals.Count < _virtuals.Count ? data.virtuals.Count : _virtuals.Count;
                for (int i = 0; i < count; i++)
                {
                    Entry entry = _virtuals[i];
                    TransformData td = data.virtuals[i];
                    if (entry == null || entry.Transform == null || td == null) { continue; }
                    entry.Transform.position = td.GetPosition();
                    entry.Transform.rotation = Quaternion.Euler(td.GetRotation());
                }
            }
        }

        /// <summary>
        /// Interpolates all configured transforms towards their targets.
        /// </summary>
        public void Update(float deltaTime)
        {
            float t = GetInterpolationFactor(deltaTime);

            // Avatar mode entries
            if (_physical != null && _physical.Transform != null && _physical.HasTarget)
            {
                _physical.Transform.localPosition = Vector3.Lerp(_physical.Transform.localPosition, _physical.TargetPosition, t);
                _physical.Transform.localRotation = Quaternion.Slerp(_physical.Transform.localRotation, _physical.TargetRotation, t);
            }

            if (_head != null && _head.Transform != null && _head.HasTarget)
            {
                _head.Transform.position = Vector3.Lerp(_head.Transform.position, _head.TargetPosition, t);
                _head.Transform.rotation = Quaternion.Slerp(_head.Transform.rotation, _head.TargetRotation, t);
            }

            if (_rightHand != null && _rightHand.Transform != null && _rightHand.HasTarget)
            {
                _rightHand.Transform.position = Vector3.Lerp(_rightHand.Transform.position, _rightHand.TargetPosition, t);
                _rightHand.Transform.rotation = Quaternion.Slerp(_rightHand.Transform.rotation, _rightHand.TargetRotation, t);
            }

            if (_leftHand != null && _leftHand.Transform != null && _leftHand.HasTarget)
            {
                _leftHand.Transform.position = Vector3.Lerp(_leftHand.Transform.position, _leftHand.TargetPosition, t);
                _leftHand.Transform.rotation = Quaternion.Slerp(_leftHand.Transform.rotation, _leftHand.TargetRotation, t);
            }

            if (_virtuals != null)
            {
                int count = _virtuals.Count;
                for (int i = 0; i < count; i++)
                {
                    Entry entry = _virtuals[i];
                    if (entry == null || entry.Transform == null || !entry.HasTarget) { continue; }
                    entry.Transform.position = Vector3.Lerp(entry.Transform.position, entry.TargetPosition, t);
                    entry.Transform.rotation = Quaternion.Slerp(entry.Transform.rotation, entry.TargetRotation, t);
                }
            }

            // Single-target mode
            if (_single != null && _single.Transform != null && _single.HasTarget)
            {
                if (_single.Space == SpaceMode.World)
                {
                    _single.Transform.position = Vector3.Lerp(_single.Transform.position, _single.TargetPosition, t);
                    _single.Transform.rotation = Quaternion.Slerp(_single.Transform.rotation, _single.TargetRotation, t);
                }
                else
                {
                    _single.Transform.localPosition = Vector3.Lerp(_single.Transform.localPosition, _single.TargetPosition, t);
                    _single.Transform.localRotation = Quaternion.Slerp(_single.Transform.localRotation, _single.TargetRotation, t);
                }
            }
        }

        /// <summary>
        /// Computes interpolation factor from delta time and configured packet interval.
        /// Returns 0 when delta time is non-positive.
        /// </summary>
        private float GetInterpolationFactor(float deltaTime)
        {
            if (deltaTime <= 0f) { return 0f; }
            return Mathf.Clamp01(deltaTime / _packetInterval);
        }
    }
}
