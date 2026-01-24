// NetSyncTransformApplier.cs
using System;
using System.Collections.Generic;
using UnityEngine;

namespace Styly.NetSync
{
    /// <summary>
    /// Transform applier with snapshot interpolation, bounded extrapolation, and adaptive smoothing.
    /// </summary>
    internal class NetSyncTransformApplier
    {
        public enum SpaceMode { World, Local }

        private struct TransformBinding
        {
            public Transform Transform;
            public SpaceMode Space;

            public TransformBinding(Transform transform, SpaceMode space)
            {
                Transform = transform;
                Space = space;
            }
        }

        private NetSyncTimeEstimator _timeEstimator;
        private NetSyncSmoothingSettings _settings;
        private readonly SendIntervalEstimator _intervalEstimator = new SendIntervalEstimator();
        private double _configuredSendIntervalSeconds = 0.1;

        private PoseChannel _physical;
        private PoseChannel _head;
        private PoseChannel _rightHand;
        private PoseChannel _leftHand;
        private readonly List<PoseChannel> _virtuals = new List<PoseChannel>(8);

        private TransformBinding _physicalBinding;
        private TransformBinding _headBinding;
        private TransformBinding _rightBinding;
        private TransformBinding _leftBinding;
        private readonly List<TransformBinding> _virtualBindings = new List<TransformBinding>(8);

        private PoseChannel _singleChannel;
        private TransformBinding _singleBinding;

        public void InitializeForAvatar(
            Transform physical,
            Transform head,
            Transform rightHand,
            Transform leftHand,
            Transform[] virtuals,
            NetSyncTimeEstimator timeEstimator,
            NetSyncSmoothingSettings settings,
            float sendRateHz)
        {
            _timeEstimator = timeEstimator;
            _settings = settings ?? new NetSyncSmoothingSettings();
            _configuredSendIntervalSeconds = 1.0 / Math.Max(1e-6, sendRateHz);

            _physical = new PoseChannel(_settings.Physical);
            _head = new PoseChannel(_settings.Head);
            _rightHand = new PoseChannel(_settings.Right);
            _leftHand = new PoseChannel(_settings.Left);

            _physicalBinding = new TransformBinding(physical, SpaceMode.Local);
            _headBinding = new TransformBinding(head, SpaceMode.World);
            _rightBinding = new TransformBinding(rightHand, SpaceMode.World);
            _leftBinding = new TransformBinding(leftHand, SpaceMode.World);

            _virtuals.Clear();
            _virtualBindings.Clear();
            if (virtuals != null)
            {
                for (int i = 0; i < virtuals.Length; i++)
                {
                    _virtuals.Add(new PoseChannel(_settings.Virtual));
                    _virtualBindings.Add(new TransformBinding(virtuals[i], SpaceMode.World));
                }
            }

            _singleChannel = null;
            _intervalEstimator.Reset();
        }

        public void InitializeForSingle(
            Transform transform,
            SpaceMode space,
            NetSyncTimeEstimator timeEstimator,
            NetSyncSmoothingSettings settings,
            float sendRateHz)
        {
            _timeEstimator = timeEstimator;
            _settings = settings ?? new NetSyncSmoothingSettings();
            _configuredSendIntervalSeconds = 1.0 / Math.Max(1e-6, sendRateHz);

            _singleChannel = new PoseChannel(_settings.Physical);
            _singleBinding = new TransformBinding(transform, space);

            _physical = null;
            _head = null;
            _rightHand = null;
            _leftHand = null;
            _virtuals.Clear();
            _virtualBindings.Clear();

            _intervalEstimator.Reset();
        }

        public void AddSnapshot(ClientTransformData data)
        {
            if (data == null || _timeEstimator == null)
            {
                return;
            }

            if ((data.flags & PoseFlags.IsStealth) != 0)
            {
                Clear();
                return;
            }

            _intervalEstimator.OnPoseTime(data.poseTime);

            if ((data.flags & PoseFlags.PhysicalValid) != 0 && data.physical != null)
            {
                _physical.AddSnapshot(data.poseTime, data.poseSeq, new PoseSampleData(data.physical.position, data.physical.rotation));
            }
            else
            {
                _physical.Clear();
            }

            if ((data.flags & PoseFlags.HeadValid) != 0 && data.head != null)
            {
                _head.AddSnapshot(data.poseTime, data.poseSeq, new PoseSampleData(data.head.position, data.head.rotation));
            }
            else
            {
                _head.Clear();
            }

            if ((data.flags & PoseFlags.RightValid) != 0 && data.rightHand != null)
            {
                _rightHand.AddSnapshot(data.poseTime, data.poseSeq, new PoseSampleData(data.rightHand.position, data.rightHand.rotation));
            }
            else
            {
                _rightHand.Clear();
            }

            if ((data.flags & PoseFlags.LeftValid) != 0 && data.leftHand != null)
            {
                _leftHand.AddSnapshot(data.poseTime, data.poseSeq, new PoseSampleData(data.leftHand.position, data.leftHand.rotation));
            }
            else
            {
                _leftHand.Clear();
            }

            if ((data.flags & PoseFlags.VirtualsValid) != 0 && data.virtuals != null)
            {
                var count = Math.Min(data.virtuals.Count, _virtuals.Count);
                for (int i = 0; i < count; i++)
                {
                    var td = data.virtuals[i];
                    if (td == null) { continue; }
                    _virtuals[i].AddSnapshot(data.poseTime, data.poseSeq, new PoseSampleData(td.position, td.rotation));
                }
            }
            else
            {
                for (int i = 0; i < _virtuals.Count; i++)
                {
                    _virtuals[i].Clear();
                }
            }
        }

        public void AddSingleSnapshot(double poseTime, ushort poseSeq, Vector3 position, Quaternion rotation)
        {
            if (_singleChannel == null)
            {
                return;
            }

            _intervalEstimator.OnPoseTime(poseTime);
            _singleChannel.AddSnapshot(poseTime, poseSeq, new PoseSampleData(position, rotation));
        }

        public void Clear()
        {
            _physical?.Clear();
            _head?.Clear();
            _rightHand?.Clear();
            _leftHand?.Clear();
            _singleChannel?.Clear();
            for (int i = 0; i < _virtuals.Count; i++)
            {
                _virtuals[i].Clear();
            }
        }

        public void Tick(float deltaTime, double localNow)
        {
            if (_timeEstimator == null)
            {
                return;
            }

            var serverNow = _timeEstimator.EstimateServerNow(localNow);
            var sendInterval = _intervalEstimator.EstimatedIntervalSeconds(_configuredSendIntervalSeconds);
            var bufferMul = _timeEstimator.ComputeDynamicBufferMultiplier(
                sendInterval,
                _settings.BaseBufferMultiplier,
                _settings.DynamicBuffer,
                _settings.DynamicTolerance,
                _settings.MinBufferMultiplier,
                _settings.MaxBufferMultiplier);
            var renderServerTime = serverNow - (bufferMul * sendInterval);

            if (_singleChannel != null)
            {
                var pose = _singleChannel.Tick(renderServerTime, deltaTime);
                ApplyBinding(_singleBinding, pose);
                return;
            }

            if (_physical != null)
            {
                ApplyBinding(_physicalBinding, _physical.Tick(renderServerTime, deltaTime));
            }
            if (_head != null)
            {
                ApplyBinding(_headBinding, _head.Tick(renderServerTime, deltaTime));
            }
            if (_rightHand != null)
            {
                ApplyBinding(_rightBinding, _rightHand.Tick(renderServerTime, deltaTime));
            }
            if (_leftHand != null)
            {
                ApplyBinding(_leftBinding, _leftHand.Tick(renderServerTime, deltaTime));
            }

            for (int i = 0; i < _virtuals.Count && i < _virtualBindings.Count; i++)
            {
                ApplyBinding(_virtualBindings[i], _virtuals[i].Tick(renderServerTime, deltaTime));
            }
        }

        private static void ApplyBinding(in TransformBinding binding, in PoseSampleData pose)
        {
            if (binding.Transform == null) return;

            if (binding.Space == SpaceMode.World)
            {
                binding.Transform.position = pose.Position;
                binding.Transform.rotation = pose.Rotation;
            }
            else
            {
                binding.Transform.localPosition = pose.Position;
                binding.Transform.localRotation = pose.Rotation;
            }
        }
    }
}
