// NetSyncTransformSmoother.cs
using System.Collections.Generic;
using UnityEngine;

namespace Styly.NetSync
{
    /// <summary>
    /// Transform applier used for both Remote Avatar and Human Presence.
    /// - Supports multiple tracked transforms with individual coordinate spaces (World/Local).
    /// - Targets are updated when network packets arrive and applied directly in Update (no interpolation).
    /// - For the first packet, values are applied immediately to avoid snapping from the origin.
    ///
    /// NOTE: Do not use null propagation with UnityEngine.Object. All Unity API calls are on main thread.
    /// </summary>
    internal class NetSyncTransformSmoother
    {
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

        // Tracked entries in a fixed order for avatar usage
        private Entry _physical;     // Local space
        private Entry _head;         // World space
        private Entry _rightHand;    // World space
        private Entry _leftHand;     // World space
        private readonly List<Entry> _virtuals = new List<Entry>(8);

        // Single-target mode (Human Presence)
        private Entry _single;

        private bool _initialized;

        public NetSyncTransformSmoother()
        {
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
        /// Update the single-target transform from a world/local pose.
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
        /// Applies all configured transforms directly to their targets (no interpolation).
        /// </summary>
        public void Update()
        {
            // Avatar mode entries - apply directly without interpolation
            if (_physical != null && _physical.Transform != null && _physical.HasTarget)
            {
                _physical.Transform.localPosition = _physical.TargetPosition;
                _physical.Transform.localRotation = _physical.TargetRotation;
            }

            if (_head != null && _head.Transform != null && _head.HasTarget)
            {
                _head.Transform.position = _head.TargetPosition;
                _head.Transform.rotation = _head.TargetRotation;
            }

            if (_rightHand != null && _rightHand.Transform != null && _rightHand.HasTarget)
            {
                _rightHand.Transform.position = _rightHand.TargetPosition;
                _rightHand.Transform.rotation = _rightHand.TargetRotation;
            }

            if (_leftHand != null && _leftHand.Transform != null && _leftHand.HasTarget)
            {
                _leftHand.Transform.position = _leftHand.TargetPosition;
                _leftHand.Transform.rotation = _leftHand.TargetRotation;
            }

            if (_virtuals != null)
            {
                int count = _virtuals.Count;
                for (int i = 0; i < count; i++)
                {
                    Entry entry = _virtuals[i];
                    if (entry == null || entry.Transform == null || !entry.HasTarget) { continue; }
                    entry.Transform.position = entry.TargetPosition;
                    entry.Transform.rotation = entry.TargetRotation;
                }
            }

            // Single-target mode
            if (_single != null && _single.Transform != null && _single.HasTarget)
            {
                if (_single.Space == SpaceMode.World)
                {
                    _single.Transform.position = _single.TargetPosition;
                    _single.Transform.rotation = _single.TargetRotation;
                }
                else
                {
                    _single.Transform.localPosition = _single.TargetPosition;
                    _single.Transform.localRotation = _single.TargetRotation;
                }
            }
        }
    }
}
