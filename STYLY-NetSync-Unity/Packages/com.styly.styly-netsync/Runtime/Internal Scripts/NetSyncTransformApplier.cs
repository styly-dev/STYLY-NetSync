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

        private PoseSampleData _lastHeadSample;
        private PoseSampleData _lastRightSample;
        private PoseSampleData _lastLeftSample;
        private bool _hasHeadSample;
        private bool _hasRightSample;
        private bool _hasLeftSample;
        private readonly List<PoseSampleData> _lastVirtualSamples = new List<PoseSampleData>(8);
        private readonly List<bool> _hasVirtualSamples = new List<bool>(8);

        // True once any snapshot (avatar or single-object) has been ingested.
        // Callers should skip Tick while false — an empty PoseChannel samples as
        // default(PoseSampleData) which would snap the bound transform to origin.
        private bool _hasAnySnapshot;
        public bool HasAnySnapshot => _hasAnySnapshot;

        // Most recent smoothed physical pose produced by Tick. The physical channel
        // has no Transform binding (NetSyncAvatar manages PhysicalPosition itself),
        // so consumers read the smoothed result through these properties to keep
        // PhysicalPosition time-aligned with the head/hand channels.
        private PoseSampleData _lastPhysicalSample;
        private bool _hasPhysicalSample;
        private bool _isReferenceFrameLocal;
        private bool _lastTickApplied;
        public PoseSampleData LastPhysicalSample => _lastPhysicalSample;
        public bool HasPhysicalSample => _hasPhysicalSample;
        public bool IsReferenceFrameLocal => _isReferenceFrameLocal;
        public bool LastTickApplied => _lastTickApplied;

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
            _lastVirtualSamples.Clear();
            _hasVirtualSamples.Clear();
            if (virtuals != null)
            {
                for (int i = 0; i < virtuals.Length; i++)
                {
                    _virtuals.Add(new PoseChannel(_settings.Virtual));
                    _virtualBindings.Add(new TransformBinding(virtuals[i], SpaceMode.World));
                    _lastVirtualSamples.Add(default);
                    _hasVirtualSamples.Add(false);
                }
            }

            _singleChannel = null;
            _hasAnySnapshot = false;
            _isReferenceFrameLocal = false;
            _lastTickApplied = false;
            ClearLastAvatarSamples();
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
            _lastVirtualSamples.Clear();
            _hasVirtualSamples.Clear();

            _hasAnySnapshot = false;
            _isReferenceFrameLocal = false;
            _lastTickApplied = false;
            ClearLastAvatarSamples();
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

            var referenceFrameLocal = (data.flags & PoseFlags.ReferenceFrameLocal) != 0;
            if (_hasAnySnapshot && referenceFrameLocal != _isReferenceFrameLocal)
            {
                Clear();
            }
            _isReferenceFrameLocal = referenceFrameLocal;
            _hasAnySnapshot = true;
            _intervalEstimator.OnPoseTime(data.poseTime);

            if ((data.flags & PoseFlags.PhysicalValid) != 0 && data.physical != null)
            {
                _physical.AddSnapshot(data.poseTime, data.poseSeq, new PoseSampleData(data.physical.position, data.physical.rotation));
            }
            else
            {
                _physical.Clear();
                _hasPhysicalSample = false;
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

            _hasAnySnapshot = true;
            _intervalEstimator.OnPoseTime(poseTime);
            _singleChannel.AddSnapshot(poseTime, poseSeq, new PoseSampleData(position, rotation));
        }

        public void Clear()
        {
            if (_physical != null) _physical.Clear();
            if (_head != null) _head.Clear();
            if (_rightHand != null) _rightHand.Clear();
            if (_leftHand != null) _leftHand.Clear();
            if (_singleChannel != null) _singleChannel.Clear();
            for (int i = 0; i < _virtuals.Count; i++)
            {
                _virtuals[i].Clear();
            }
            _hasAnySnapshot = false;
            _hasPhysicalSample = false;
            _isReferenceFrameLocal = false;
            _lastTickApplied = false;
            ClearLastAvatarSamples();
        }

        public void Tick(float deltaTime, double localNow)
        {
            Tick(deltaTime, localNow, null);
        }

        public void Tick(float deltaTime, double localNow, Transform referenceFrame)
        {
            _lastTickApplied = false;
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
                _lastTickApplied = true;
                return;
            }

            if (_physical != null)
            {
                _lastPhysicalSample = _physical.Tick(renderServerTime, deltaTime);
                _hasPhysicalSample = true;
                ApplyBinding(_physicalBinding, _lastPhysicalSample);
            }

            if (_isReferenceFrameLocal && referenceFrame == null)
            {
                return;
            }

            if (_head != null)
            {
                _lastHeadSample = _head.Tick(renderServerTime, deltaTime);
                _hasHeadSample = true;
                ApplyBinding(_headBinding, _lastHeadSample, _isReferenceFrameLocal ? referenceFrame : null);
            }
            if (_rightHand != null)
            {
                _lastRightSample = _rightHand.Tick(renderServerTime, deltaTime);
                _hasRightSample = true;
                ApplyBinding(_rightBinding, _lastRightSample, _isReferenceFrameLocal ? referenceFrame : null);
            }
            if (_leftHand != null)
            {
                _lastLeftSample = _leftHand.Tick(renderServerTime, deltaTime);
                _hasLeftSample = true;
                ApplyBinding(_leftBinding, _lastLeftSample, _isReferenceFrameLocal ? referenceFrame : null);
            }

            for (int i = 0; i < _virtuals.Count && i < _virtualBindings.Count; i++)
            {
                var virtualSample = _virtuals[i].Tick(renderServerTime, deltaTime);
                _lastVirtualSamples[i] = virtualSample;
                _hasVirtualSamples[i] = true;
                ApplyBinding(_virtualBindings[i], virtualSample, _isReferenceFrameLocal ? referenceFrame : null);
            }
            _lastTickApplied = true;
        }

        public bool ReapplyLatestReferenceFrame(Transform referenceFrame)
        {
            if (!_isReferenceFrameLocal || referenceFrame == null || !_lastTickApplied)
            {
                return false;
            }

            // Keep the network interpolation time fixed, but project the cached
            // frame-local pose through the latest reference-frame transform.
            if (_head != null && _hasHeadSample)
            {
                ApplyBinding(_headBinding, _lastHeadSample, referenceFrame);
            }
            if (_rightHand != null && _hasRightSample)
            {
                ApplyBinding(_rightBinding, _lastRightSample, referenceFrame);
            }
            if (_leftHand != null && _hasLeftSample)
            {
                ApplyBinding(_leftBinding, _lastLeftSample, referenceFrame);
            }

            for (int i = 0; i < _virtuals.Count && i < _virtualBindings.Count; i++)
            {
                if (i < _hasVirtualSamples.Count && _hasVirtualSamples[i])
                {
                    ApplyBinding(_virtualBindings[i], _lastVirtualSamples[i], referenceFrame);
                }
            }

            return true;
        }

        private void ClearLastAvatarSamples()
        {
            _hasHeadSample = false;
            _hasRightSample = false;
            _hasLeftSample = false;
            for (int i = 0; i < _hasVirtualSamples.Count; i++)
            {
                _hasVirtualSamples[i] = false;
            }
        }

        private static void ApplyBinding(in TransformBinding binding, in PoseSampleData pose)
        {
            ApplyBinding(binding, pose, null);
        }

        private static void ApplyBinding(in TransformBinding binding, in PoseSampleData pose, Transform referenceFrame)
        {
            if (binding.Transform == null) return;

            if (binding.Space == SpaceMode.World)
            {
                if (referenceFrame != null)
                {
                    binding.Transform.position = referenceFrame.TransformPoint(pose.Position);
                    binding.Transform.rotation = referenceFrame.rotation * pose.Rotation;
                }
                else
                {
                    binding.Transform.position = pose.Position;
                    binding.Transform.rotation = pose.Rotation;
                }
            }
            else
            {
                binding.Transform.localPosition = pose.Position;
                binding.Transform.localRotation = pose.Rotation;
            }
        }
    }
}
