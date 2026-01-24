using System;
using System.Collections.Generic;
using System.Diagnostics;
using UnityEngine;

namespace Styly.NetSync
{
    /// <summary>
    /// High-resolution clock using Stopwatch for accurate timing.
    /// Provides better precision than Unity's realtimeSinceStartup for network jitter estimation.
    /// </summary>
    internal static class NetSyncClock
    {
        private static readonly double TickToSeconds = 1.0 / Stopwatch.Frequency;

        /// <summary>
        /// Returns the current time in seconds with high precision.
        /// </summary>
        public static double NowSeconds()
        {
            return Stopwatch.GetTimestamp() * TickToSeconds;
        }
    }

    internal struct PoseSampleData
    {
        public Vector3 Position;
        public Quaternion Rotation;

        public PoseSampleData(Vector3 position, Quaternion rotation)
        {
            Position = position;
            Rotation = rotation;
        }
    }

    internal sealed class EwmaMeanStd
    {
        private readonly double _tauSeconds;
        private bool _initialized;
        private double _mean;
        private double _meanSq;

        public EwmaMeanStd(double tauSeconds)
        {
            _tauSeconds = Math.Max(1e-6, tauSeconds);
        }

        public double Mean => _mean;
        public double StdDev => Math.Sqrt(Math.Max(0.0, _meanSq - _mean * _mean));
        public bool Initialized => _initialized;

        public void Reset()
        {
            _initialized = false;
            _mean = 0;
            _meanSq = 0;
        }

        public void AddSample(double x, double dtSeconds)
        {
            dtSeconds = Math.Max(0.0, dtSeconds);
            if (!_initialized)
            {
                _initialized = true;
                _mean = x;
                _meanSq = x * x;
                return;
            }

            var alpha = 1.0 - Math.Exp(-dtSeconds / _tauSeconds);
            _mean = _mean + alpha * (x - _mean);
            _meanSq = _meanSq + alpha * (x * x - _meanSq);
        }
    }

    internal sealed class NetSyncTimeEstimator
    {
        private readonly EwmaMeanStd _offsetStats;
        private double _lastLocalReceiveTime;
        private bool _hasLast;

        public NetSyncTimeEstimator(double tauSeconds = 1.0)
        {
            _offsetStats = new EwmaMeanStd(tauSeconds);
        }

        public bool IsReady => _offsetStats.Initialized;
        public double OffsetJitterStdSeconds => _offsetStats.StdDev;

        public void Reset()
        {
            _offsetStats.Reset();
            _hasLast = false;
            _lastLocalReceiveTime = 0;
        }

        public void OnRoomBroadcast(double serverBroadcastTime, double localReceiveTime)
        {
            var offset = localReceiveTime - serverBroadcastTime;
            double dt = 0.0;
            if (_hasLast)
            {
                dt = localReceiveTime - _lastLocalReceiveTime;
                if (dt < 0) dt = 0;
            }
            _offsetStats.AddSample(offset, dt);
            _lastLocalReceiveTime = localReceiveTime;
            _hasLast = true;
        }

        public double EstimateServerNow(double localNow)
        {
            if (!_offsetStats.Initialized)
            {
                return localNow;
            }
            return localNow - _offsetStats.Mean;
        }

        public double ComputeDynamicBufferMultiplier(
            double sendIntervalSeconds,
            double baseMultiplier,
            bool dynamicEnabled,
            double tolerance,
            double minMultiplier,
            double maxMultiplier)
        {
            baseMultiplier = Math.Max(1.0, baseMultiplier);

            if (!dynamicEnabled || !_offsetStats.Initialized)
            {
                return Clamp(baseMultiplier, minMultiplier, maxMultiplier);
            }

            var jitter = OffsetJitterStdSeconds;
            var multiples = (sendIntervalSeconds + jitter) / Math.Max(1e-6, sendIntervalSeconds);
            var dynamic = multiples + tolerance;
            var chosen = Math.Max(baseMultiplier, dynamic);
            return Clamp(chosen, minMultiplier, maxMultiplier);
        }

        private static double Clamp(double x, double min, double max)
        {
            return x < min ? min : (x > max ? max : x);
        }
    }

    internal sealed class SendIntervalEstimator
    {
        private readonly EwmaMeanStd _dtStats;
        private double _lastPoseTime;
        private bool _hasLast;

        public SendIntervalEstimator(double tauSeconds = 2.0)
        {
            _dtStats = new EwmaMeanStd(tauSeconds);
        }

        public void Reset()
        {
            _dtStats.Reset();
            _hasLast = false;
            _lastPoseTime = 0;
        }

        public void OnPoseTime(double poseTime)
        {
            if (_hasLast)
            {
                var dt = poseTime - _lastPoseTime;
                if (dt > 1e-6 && dt < 1.0)
                {
                    _dtStats.AddSample(dt, dt);
                }
            }
            _lastPoseTime = poseTime;
            _hasLast = true;
        }

        public double EstimatedIntervalSeconds(double fallback)
        {
            if (!_dtStats.Initialized) return fallback;
            return Math.Max(1e-4, _dtStats.Mean);
        }
    }

    internal enum SampleState
    {
        Empty,
        HoldSingle,
        Interpolating,
        Extrapolating
    }

    internal struct PoseSnapshot
    {
        public double Time;
        public ushort Seq;
        public PoseSampleData Pose;

        public PoseSnapshot(double time, ushort seq, PoseSampleData pose)
        {
            Time = time;
            Seq = seq;
            Pose = pose;
        }
    }

    internal struct PoseSample
    {
        public SampleState State;
        public PoseSampleData Target;
        public double SnapshotDt;
        public float LinearSpeed;
        public float AngularSpeedDeg;
    }

    internal sealed class PoseSnapshotBuffer
    {
        private readonly PoseSnapshot[] _items;
        private int _count;

        public PoseSnapshotBuffer(int capacity)
        {
            if (capacity < 2) capacity = 2;
            _items = new PoseSnapshot[capacity];
        }

        public int Count => _count;
        public PoseSnapshot Oldest => _items[0];
        public PoseSnapshot Newest => _items[_count - 1];

        public void Clear()
        {
            _count = 0;
        }

        public bool Add(in PoseSnapshot snap)
        {
            if (_count > 0)
            {
                var last = _items[_count - 1];
                if (snap.Seq != 0 && last.Seq != 0)
                {
                    if (SequenceLE(snap.Seq, last.Seq))
                        return false;
                }
                else
                {
                    if (snap.Time <= last.Time)
                        return false;
                }
            }

            if (_count < _items.Length)
            {
                _items[_count++] = snap;
            }
            else
            {
                for (int i = 1; i < _items.Length; i++)
                {
                    _items[i - 1] = _items[i];
                }
                _items[_items.Length - 1] = snap;
            }

            return true;
        }

        public bool TryGetBracket(double renderTime, out int fromIdx, out int toIdx, out double t)
        {
            fromIdx = toIdx = -1;
            t = 0;

            if (_count == 0) return false;
            if (_count == 1)
            {
                fromIdx = toIdx = 0;
                t = 0;
                return true;
            }

            if (renderTime <= _items[0].Time)
            {
                fromIdx = toIdx = 0;
                t = 0;
                return true;
            }

            if (renderTime >= _items[_count - 1].Time)
            {
                fromIdx = Math.Max(0, _count - 2);
                toIdx = _count - 1;
                t = 1;
                return true;
            }

            for (int i = 0; i < _count - 1; i++)
            {
                var a = _items[i];
                var b = _items[i + 1];
                if (renderTime >= a.Time && renderTime <= b.Time)
                {
                    fromIdx = i;
                    toIdx = i + 1;
                    var dt = b.Time - a.Time;
                    t = dt > 1e-9 ? (renderTime - a.Time) / dt : 0;
                    return true;
                }
            }

            fromIdx = toIdx = _count - 1;
            t = 0;
            return true;
        }

        public PoseSnapshot this[int idx] => _items[idx];

        private static bool SequenceLE(ushort a, ushort b)
        {
            return (ushort)(a - b) >= 0x8000;
        }
    }

    [Serializable]
    internal sealed class PoseChannelSettings
    {
        public double MaxExtrapolationSeconds = 0.05;
        public bool EnableSecondPhaseSmoothing = true;
        public float TauMinSeconds = 0.02f;
        public float TauMaxSeconds = 0.08f;
        public float SpeedForTauMin = 2.0f;
        public float AngularSpeedForTauMin = 360f;
        public float TeleportDistanceMeters = 2.0f;
        public float TeleportAngleDegrees = 140f;
        public float MaxReasonableSpeed = 15.0f;
        public float MaxReasonableAngularSpeed = 2000f;
    }

    [Serializable]
    internal sealed class NetSyncSmoothingSettings
    {
        public double BaseBufferMultiplier = 1.5;
        public bool DynamicBuffer = true;
        public double DynamicTolerance = 0.6;
        public double MinBufferMultiplier = 1.1;
        public double MaxBufferMultiplier = 4.0;

        public PoseChannelSettings Physical = new PoseChannelSettings { MaxExtrapolationSeconds = 0.03, TauMinSeconds = 0.02f, TauMaxSeconds = 0.06f };
        public PoseChannelSettings Head = new PoseChannelSettings { MaxExtrapolationSeconds = 0.04, TauMinSeconds = 0.02f, TauMaxSeconds = 0.05f };
        public PoseChannelSettings Right = new PoseChannelSettings { MaxExtrapolationSeconds = 0.05, TauMinSeconds = 0.03f, TauMaxSeconds = 0.07f };
        public PoseChannelSettings Left = new PoseChannelSettings { MaxExtrapolationSeconds = 0.05, TauMinSeconds = 0.03f, TauMaxSeconds = 0.07f };
        public PoseChannelSettings Virtual = new PoseChannelSettings { MaxExtrapolationSeconds = 0.03, TauMinSeconds = 0.05f, TauMaxSeconds = 0.12f };
    }

    internal sealed class PoseChannel
    {
        private readonly PoseSnapshotBuffer _buffer;
        private readonly PoseChannelSettings _settings;
        private bool _hasCurrent;
        private PoseSampleData _current;

        public PoseChannel(PoseChannelSettings settings, int capacity = 32)
        {
            _settings = settings;
            _buffer = new PoseSnapshotBuffer(capacity);
        }

        public void Clear()
        {
            _buffer.Clear();
            _hasCurrent = false;
        }

        public void AddSnapshot(double poseTime, ushort seq, in PoseSampleData pose)
        {
            var normalized = pose;
            if (normalized.Rotation.w == 0 && normalized.Rotation.x == 0 && normalized.Rotation.y == 0 && normalized.Rotation.z == 0)
            {
                normalized.Rotation = Quaternion.identity;
            }
            else
            {
                normalized.Rotation = Quaternion.Normalize(normalized.Rotation);
            }

            var snap = new PoseSnapshot(poseTime, seq, normalized);

            if (_buffer.Count > 0)
            {
                var last = _buffer.Newest;
                var dt = snap.Time - last.Time;
                if (dt > 1e-6)
                {
                    var dist = Vector3.Distance(last.Pose.Position, snap.Pose.Position);
                    var ang = Quaternion.Angle(last.Pose.Rotation, snap.Pose.Rotation);
                    var speed = dist / (float)dt;
                    var angSpeed = ang / (float)dt;
                    if (dist > _settings.TeleportDistanceMeters ||
                        ang > _settings.TeleportAngleDegrees ||
                        speed > _settings.MaxReasonableSpeed ||
                        angSpeed > _settings.MaxReasonableAngularSpeed)
                    {
                        _buffer.Clear();
                        _buffer.Add(in snap);
                        _current = snap.Pose;
                        _hasCurrent = true;
                        return;
                    }
                }
            }

            if (!_buffer.Add(in snap))
            {
                return;
            }

            if (!_hasCurrent)
            {
                _current = snap.Pose;
                _hasCurrent = true;
            }
        }

        private PoseSample Sample(double renderServerTime)
        {
            if (_buffer.Count == 0)
            {
                return new PoseSample { State = SampleState.Empty, Target = _hasCurrent ? _current : default };
            }

            if (_buffer.Count == 1)
            {
                var only = _buffer[0];
                return new PoseSample { State = SampleState.HoldSingle, Target = only.Pose };
            }

            if (!_buffer.TryGetBracket(renderServerTime, out var fromIdx, out var toIdx, out var t))
            {
                return new PoseSample { State = SampleState.Empty, Target = _hasCurrent ? _current : default };
            }

            var a = _buffer[fromIdx];
            var b = _buffer[toIdx];
            var dt = b.Time - a.Time;
            float linearSpeed = 0f;
            float angularSpeed = 0f;
            if (dt > 1e-6)
            {
                linearSpeed = Vector3.Distance(a.Pose.Position, b.Pose.Position) / (float)dt;
                angularSpeed = Quaternion.Angle(a.Pose.Rotation, b.Pose.Rotation) / (float)dt;
            }

            if (renderServerTime <= b.Time + 1e-9)
            {
                var target = new PoseSampleData(
                    Vector3.LerpUnclamped(a.Pose.Position, b.Pose.Position, (float)t),
                    Quaternion.SlerpUnclamped(a.Pose.Rotation, b.Pose.Rotation, (float)t)
                );
                return new PoseSample
                {
                    State = SampleState.Interpolating,
                    Target = target,
                    SnapshotDt = dt,
                    LinearSpeed = linearSpeed,
                    AngularSpeedDeg = angularSpeed
                };
            }

            var beyond = renderServerTime - b.Time;
            if (beyond <= _settings.MaxExtrapolationSeconds && dt > 1e-6)
            {
                var tEx = 1.0 + beyond / dt;
                var target = new PoseSampleData(
                    Vector3.LerpUnclamped(a.Pose.Position, b.Pose.Position, (float)tEx),
                    Quaternion.SlerpUnclamped(a.Pose.Rotation, b.Pose.Rotation, (float)tEx)
                );
                return new PoseSample
                {
                    State = SampleState.Extrapolating,
                    Target = target,
                    SnapshotDt = dt,
                    LinearSpeed = linearSpeed,
                    AngularSpeedDeg = angularSpeed
                };
            }

            return new PoseSample
            {
                State = SampleState.HoldSingle,
                Target = b.Pose,
                SnapshotDt = dt,
                LinearSpeed = linearSpeed,
                AngularSpeedDeg = angularSpeed
            };
        }

        public PoseSampleData Tick(double renderServerTime, float deltaTime)
        {
            var sample = Sample(renderServerTime);

            if (!_hasCurrent)
            {
                _current = sample.Target;
                _hasCurrent = true;
                return _current;
            }

            if (!_settings.EnableSecondPhaseSmoothing)
            {
                _current = sample.Target;
                return _current;
            }

            var tau = EvaluateAdaptiveTau(sample.LinearSpeed, sample.AngularSpeedDeg);
            var alpha = 1f - Mathf.Exp(-deltaTime / Mathf.Max(1e-5f, tau));
            _current.Position = Vector3.Lerp(_current.Position, sample.Target.Position, alpha);
            _current.Rotation = Quaternion.Slerp(_current.Rotation, sample.Target.Rotation, alpha);
            _current.Rotation = Quaternion.Normalize(_current.Rotation);
            return _current;
        }

        private float EvaluateAdaptiveTau(float linearSpeed, float angularSpeedDeg)
        {
            var tSpeed = Mathf.Clamp01(linearSpeed / Mathf.Max(1e-3f, _settings.SpeedForTauMin));
            var tAng = Mathf.Clamp01(angularSpeedDeg / Mathf.Max(1e-3f, _settings.AngularSpeedForTauMin));
            var t = Mathf.Max(tSpeed, tAng);
            return Mathf.Lerp(_settings.TauMaxSeconds, _settings.TauMinSeconds, t);
        }
    }
}
