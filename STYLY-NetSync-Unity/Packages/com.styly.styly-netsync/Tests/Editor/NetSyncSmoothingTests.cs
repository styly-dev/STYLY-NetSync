// NetSyncSmoothingTests.cs - EditMode tests for clock, estimators, sequence math,
// snapshot buffering and pose channel smoothing (all pure C# with injected time).
using NUnit.Framework;
using UnityEngine;

namespace Styly.NetSync.Tests
{
    public class NetSyncSmoothingTests
    {
        #region === NetSyncClock ===

        [Test]
        public void NetSyncClock_IsMonotonicNonDecreasing()
        {
            var first = NetSyncClock.NowSeconds();
            var second = NetSyncClock.NowSeconds();
            Assert.GreaterOrEqual(second, first);
        }

        #endregion

        #region === EwmaMeanStd ===

        [Test]
        public void EwmaMeanStd_FirstSampleInitializesMean()
        {
            var ewma = new EwmaMeanStd(1.0);
            Assert.IsFalse(ewma.Initialized);

            ewma.AddSample(5.0, 0.1);

            Assert.IsTrue(ewma.Initialized);
            Assert.AreEqual(5.0, ewma.Mean, 1e-9);
            Assert.AreEqual(0.0, ewma.StdDev, 1e-9);
        }

        [Test]
        public void EwmaMeanStd_ConvergesTowardConstantInput()
        {
            var ewma = new EwmaMeanStd(0.5);
            ewma.AddSample(0.0, 0.0);
            for (int i = 0; i < 100; i++)
            {
                ewma.AddSample(10.0, 0.1);
            }
            Assert.AreEqual(10.0, ewma.Mean, 0.01);
        }

        [Test]
        public void EwmaMeanStd_Reset_ClearsState()
        {
            var ewma = new EwmaMeanStd(1.0);
            ewma.AddSample(5.0, 0.1);
            ewma.Reset();
            Assert.IsFalse(ewma.Initialized);
            Assert.AreEqual(0.0, ewma.Mean, 1e-9);
        }

        #endregion

        #region === NetSyncTimeEstimator ===

        [Test]
        public void TimeEstimator_EstimatesConstantOffset()
        {
            var estimator = new NetSyncTimeEstimator(tauSeconds: 0.5);
            const double offset = 3.5; // local clock is 3.5 s ahead of server clock

            double serverTime = 100.0;
            for (int i = 0; i < 100; i++)
            {
                estimator.OnRoomBroadcast(serverTime, serverTime + offset);
                serverTime += 0.1;
            }

            Assert.IsTrue(estimator.IsReady);
            double localNow = serverTime + offset;
            Assert.AreEqual(serverTime, estimator.EstimateServerNow(localNow), 0.01);
            Assert.AreEqual(0.0, estimator.OffsetJitterStdSeconds, 0.01);
        }

        [Test]
        public void TimeEstimator_NotReady_ReturnsLocalNow()
        {
            var estimator = new NetSyncTimeEstimator();
            Assert.IsFalse(estimator.IsReady);
            Assert.AreEqual(42.0, estimator.EstimateServerNow(42.0), 1e-9);
        }

        [Test]
        public void DynamicBufferMultiplier_Disabled_ClampsBaseValue()
        {
            var estimator = new NetSyncTimeEstimator();
            var result = estimator.ComputeDynamicBufferMultiplier(
                sendIntervalSeconds: 0.1, baseMultiplier: 1.3, dynamicEnabled: false,
                tolerance: 0.3, minMultiplier: 1.25, maxMultiplier: 2.0);
            Assert.AreEqual(1.3, result, 1e-9);

            var clampedHigh = estimator.ComputeDynamicBufferMultiplier(
                0.1, baseMultiplier: 5.0, dynamicEnabled: false, 0.3, 1.25, 2.0);
            Assert.AreEqual(2.0, clampedHigh, 1e-9);
        }

        [Test]
        public void DynamicBufferMultiplier_ZeroJitter_UsesBasePlusToleranceFloor()
        {
            var estimator = new NetSyncTimeEstimator(0.5);
            for (int i = 0; i < 50; i++)
            {
                estimator.OnRoomBroadcast(i * 0.1, i * 0.1 + 1.0); // constant offset => no jitter
            }

            var result = estimator.ComputeDynamicBufferMultiplier(
                sendIntervalSeconds: 0.1, baseMultiplier: 1.3, dynamicEnabled: true,
                tolerance: 0.3, minMultiplier: 1.25, maxMultiplier: 2.0);

            // With zero jitter, dynamic = 1 + tolerance = 1.3 => max(base, dynamic) = 1.3
            Assert.AreEqual(1.3, result, 0.05);
        }

        #endregion

        #region === SendIntervalEstimator ===

        [Test]
        public void SendIntervalEstimator_EstimatesRegularInterval()
        {
            var estimator = new SendIntervalEstimator(tauSeconds: 0.5);
            for (int i = 0; i < 100; i++)
            {
                estimator.OnPoseTime(i * 0.1);
            }
            Assert.AreEqual(0.1, estimator.EstimatedIntervalSeconds(fallback: 9.9), 0.01);
        }

        [Test]
        public void SendIntervalEstimator_NoSamples_ReturnsFallback()
        {
            var estimator = new SendIntervalEstimator();
            Assert.AreEqual(9.9, estimator.EstimatedIntervalSeconds(9.9), 1e-9);
        }

        [Test]
        public void SendIntervalEstimator_IgnoresOutOfRangeIntervals()
        {
            var estimator = new SendIntervalEstimator();
            estimator.OnPoseTime(0.0);
            estimator.OnPoseTime(5.0);  // dt = 5 s > 1 s => ignored
            estimator.OnPoseTime(5.0);  // dt = 0 => ignored
            Assert.AreEqual(9.9, estimator.EstimatedIntervalSeconds(9.9), 1e-9);
        }

        #endregion

        #region === SequenceUtil ===

        [TestCase((ushort)1, (ushort)0, true)]
        [TestCase((ushort)0, (ushort)1, false)]
        [TestCase((ushort)0, (ushort)0, false)]
        [TestCase((ushort)0, (ushort)65535, true)]   // wrap-around: 0 comes after 65535
        [TestCase((ushort)65535, (ushort)0, false)]
        [TestCase((ushort)32767, (ushort)0, true)]   // just inside the window
        [TestCase((ushort)32768, (ushort)0, false)]  // outside the window
        public void SequenceUtil_IsNewer_HandlesWrapAround(ushort a, ushort b, bool expected)
        {
            Assert.AreEqual(expected, SequenceUtil.IsNewer(a, b));
        }

        #endregion

        #region === PoseSnapshotBuffer ===

        private static PoseSnapshot Snap(double time, ushort seq, float x = 0f)
        {
            return new PoseSnapshot(time, seq, new PoseSampleData(new Vector3(x, 0f, 0f), Quaternion.identity));
        }

        [Test]
        public void SnapshotBuffer_RejectsOlderOrDuplicateSeq()
        {
            var buffer = new PoseSnapshotBuffer(8);
            Assert.IsTrue(buffer.Add(Snap(1.0, 10)));
            Assert.IsFalse(buffer.Add(Snap(2.0, 10)), "duplicate seq must be rejected");
            Assert.IsFalse(buffer.Add(Snap(3.0, 9)), "older seq must be rejected");
            Assert.IsTrue(buffer.Add(Snap(1.5, 11)));
            Assert.AreEqual(2, buffer.Count);
        }

        [Test]
        public void SnapshotBuffer_AcceptsSeqWrapAround()
        {
            var buffer = new PoseSnapshotBuffer(8);
            Assert.IsTrue(buffer.Add(Snap(1.0, 65535)));
            Assert.IsTrue(buffer.Add(Snap(1.1, 0)), "seq 0 after 65535 must be accepted (wrap)");
        }

        [Test]
        public void SnapshotBuffer_ZeroSeq_FallsBackToTimeOrdering()
        {
            var buffer = new PoseSnapshotBuffer(8);
            Assert.IsTrue(buffer.Add(Snap(1.0, 0)));
            Assert.IsFalse(buffer.Add(Snap(1.0, 0)), "equal time must be rejected");
            Assert.IsFalse(buffer.Add(Snap(0.5, 0)), "older time must be rejected");
            Assert.IsTrue(buffer.Add(Snap(1.5, 0)));
        }

        [Test]
        public void SnapshotBuffer_OverCapacity_DropsOldest()
        {
            var buffer = new PoseSnapshotBuffer(2);
            buffer.Add(Snap(1.0, 1, x: 1f));
            buffer.Add(Snap(2.0, 2, x: 2f));
            buffer.Add(Snap(3.0, 3, x: 3f));

            Assert.AreEqual(2, buffer.Count);
            Assert.AreEqual(2f, buffer[0].Pose.Position.x);
            Assert.AreEqual(3f, buffer.Newest.Pose.Position.x);
        }

        [Test]
        public void SnapshotBuffer_TryGetBracket_InterpolatesBetweenSnapshots()
        {
            var buffer = new PoseSnapshotBuffer(8);
            buffer.Add(Snap(1.0, 1));
            buffer.Add(Snap(2.0, 2));

            Assert.IsTrue(buffer.TryGetBracket(1.25, out var fromIdx, out var toIdx, out var t));
            Assert.AreEqual(0, fromIdx);
            Assert.AreEqual(1, toIdx);
            Assert.AreEqual(0.25, t, 1e-9);
        }

        [Test]
        public void SnapshotBuffer_TryGetBracket_ClampsOutsideRange()
        {
            var buffer = new PoseSnapshotBuffer(8);
            buffer.Add(Snap(1.0, 1));
            buffer.Add(Snap(2.0, 2));

            Assert.IsTrue(buffer.TryGetBracket(0.5, out var fromBefore, out var toBefore, out var tBefore));
            Assert.AreEqual(0, fromBefore);
            Assert.AreEqual(0, toBefore);
            Assert.AreEqual(0.0, tBefore, 1e-9);

            Assert.IsTrue(buffer.TryGetBracket(5.0, out var fromAfter, out var toAfter, out var tAfter));
            Assert.AreEqual(0, fromAfter);
            Assert.AreEqual(1, toAfter);
            Assert.AreEqual(1.0, tAfter, 1e-9);
        }

        #endregion

        #region === RelayAgeEnvelope ===

        [Test]
        public void RelayAgeEnvelope_RisesInstantlyAndDecaysSlowly()
        {
            var envelope = new RelayAgeEnvelope();
            envelope.AddSample(0.2, localNow: 10.0);
            Assert.AreEqual(0.2, envelope.Current(10.0), 1e-9);

            // Decay is 0.05 per second
            Assert.AreEqual(0.15, envelope.Current(11.0), 1e-9);
            Assert.AreEqual(0.0, envelope.Current(20.0), 1e-9);

            // A worse sample raises the envelope immediately
            envelope.AddSample(0.3, localNow: 11.0);
            Assert.AreEqual(0.3, envelope.Current(11.0), 1e-9);
        }

        [Test]
        public void RelayAgeEnvelope_ClampsPathologicalSamples()
        {
            var envelope = new RelayAgeEnvelope();
            envelope.AddSample(99.0, localNow: 0.0);
            Assert.AreEqual(0.5, envelope.Current(0.0), 1e-9, "ages are capped at 0.5 s");

            envelope.Reset();
            envelope.AddSample(-1.0, localNow: 0.0);
            Assert.AreEqual(0.0, envelope.Current(0.0), 1e-9, "negative ages clamp to zero");
        }

        #endregion

        #region === PoseChannel ===

        [Test]
        public void PoseChannel_WithoutSecondPhaseSmoothing_InterpolatesExactly()
        {
            var settings = new PoseChannelSettings { EnableSecondPhaseSmoothing = false };
            var channel = new PoseChannel(settings);
            channel.AddSnapshot(1.0, 1, new PoseSampleData(Vector3.zero, Quaternion.identity));
            channel.AddSnapshot(2.0, 2, new PoseSampleData(new Vector3(1f, 0f, 0f), Quaternion.identity));

            var result = channel.Tick(renderServerTime: 1.5, deltaTime: 0.016f);

            Assert.AreEqual(0.5f, result.Position.x, 1e-4f);
        }

        [Test]
        public void PoseChannel_TeleportDistance_SnapsWithoutSmoothing()
        {
            var settings = new PoseChannelSettings { EnableSecondPhaseSmoothing = true };
            var channel = new PoseChannel(settings);
            channel.AddSnapshot(1.0, 1, new PoseSampleData(Vector3.zero, Quaternion.identity));
            channel.Tick(1.0, 0.016f);

            // 10 m jump in 0.1 s exceeds TeleportDistanceMeters (2 m) => snap
            var teleported = new Vector3(10f, 0f, 0f);
            channel.AddSnapshot(1.1, 2, new PoseSampleData(teleported, Quaternion.identity));

            var result = channel.Tick(renderServerTime: 1.1, deltaTime: 0.016f);
            Assert.AreEqual(teleported.x, result.Position.x, 1e-4f);
        }

        [Test]
        public void PoseChannel_SingleSnapshot_HoldsPose()
        {
            var settings = new PoseChannelSettings { EnableSecondPhaseSmoothing = false };
            var channel = new PoseChannel(settings);
            var pose = new PoseSampleData(new Vector3(2f, 3f, 4f), Quaternion.Euler(0f, 90f, 0f));
            channel.AddSnapshot(1.0, 1, pose);

            var result = channel.Tick(renderServerTime: 5.0, deltaTime: 0.016f);

            Assert.AreEqual(pose.Position, result.Position);
        }

        [Test]
        public void PoseChannel_ClearedChannel_DoesNotAdoptOriginPose()
        {
            var settings = new PoseChannelSettings { EnableSecondPhaseSmoothing = false };
            var channel = new PoseChannel(settings);
            channel.Clear();

            // Empty channel with no current pose must not adopt the default origin
            var result = channel.Tick(renderServerTime: 1.0, deltaTime: 0.016f);
            Assert.AreEqual(Vector3.zero, result.Position);

            // First snapshot after recovery snaps cleanly instead of gliding from origin
            var pose = new PoseSampleData(new Vector3(5f, 0f, 0f), Quaternion.identity);
            channel.AddSnapshot(2.0, 1, pose);
            var afterRecovery = channel.Tick(renderServerTime: 2.0, deltaTime: 0.016f);
            Assert.AreEqual(5f, afterRecovery.Position.x, 1e-4f);
        }

        #endregion
    }
}
