// PoseInterpolatorTests.cs
// Two-snapshot midpoint lerp, teleport snap, owner skip, not-joined skip.

using NUnit.Framework;
using Styly.NetSync;
using Styly.NetSync.Internal;
using UnityEditor;
using UnityEngine;

namespace Styly.NetSync.Tests.EditorTests
{
    [TestFixture]
    public class PoseInterpolatorTests
    {
        private sealed class FakeClock : IServerClock
        {
            public ulong NowUs { get; set; }
        }

        private GameObject _go;
        private NetSyncObject _obj;
        private ulong _entityId;

        [SetUp]
        public void SetUp()
        {
            _go = new GameObject("InterpTestObj");
            _obj = _go.AddComponent<NetSyncObject>();
            SerializedObject so = new SerializedObject(_obj);
            so.FindProperty("_guid").stringValue = "deadbeef-0000-0000-0000-000000000001";
            so.ApplyModifiedPropertiesWithoutUndo();
            _entityId = _obj.EntityId;
            EntityRegistry.Instance.Register(_obj);
        }

        [TearDown]
        public void TearDown()
        {
            if (_obj != null) EntityRegistry.Instance.Unregister(_obj);
            if (_go != null) Object.DestroyImmediate(_go);
        }

        private static PoseSample Sample(ushort seq, ulong timeUs, Vector3 pos, StateFlags flags = StateFlags.None)
        {
            return new PoseSample
            {
                ServerTimeUs = timeUs,
                PoseSeq = seq,
                Flags = flags,
                Mask = ChangedMask.Position | ChangedMask.Rotation,
                State = new TransformState
                {
                    Position = pos,
                    Rotation = Quaternion.identity,
                    Scale = Vector3.one,
                },
            };
        }

        [Test]
        public void LerpsBetweenTwoSnapshots()
        {
            PoseBuffer buf = new PoseBuffer();
            FakeClock clock = new FakeClock();
            PoseInterpolator interp = new PoseInterpolator(buf, clock)
            {
                InterpolationBackTimeSec = 0f, // render exactly at NowUs
            };

            buf.Add(_entityId, Sample(1, 1_000_000, new Vector3(0f, 0f, 0f)));
            buf.Add(_entityId, Sample(2, 3_000_000, new Vector3(2f, 0f, 0f)));

            // Render at t=2_000_000 — midpoint.
            clock.NowUs = 2_000_000UL;
            interp.Tick();

            Assert.AreEqual(new Vector3(1f, 0f, 0f), _obj.transform.localPosition);
        }

        [Test]
        public void SnapsToNewestWhenGapExceedsSamples()
        {
            PoseBuffer buf = new PoseBuffer();
            FakeClock clock = new FakeClock();
            PoseInterpolator interp = new PoseInterpolator(buf, clock)
            {
                InterpolationBackTimeSec = 0f,
            };

            buf.Add(_entityId, Sample(1, 1_000_000, new Vector3(5f, 0f, 0f)));
            clock.NowUs = 10_000_000UL;
            interp.Tick();
            Assert.AreEqual(new Vector3(5f, 0f, 0f), _obj.transform.localPosition);
        }

        [Test]
        public void SkipsOwnedEntities()
        {
            PoseBuffer buf = new PoseBuffer();
            FakeClock clock = new FakeClock();
            PoseInterpolator interp = new PoseInterpolator(buf, clock)
            {
                InterpolationBackTimeSec = 0f,
                IsOwnedLocally = id => id == _entityId,
            };

            buf.Add(_entityId, Sample(1, 1_000_000, new Vector3(99f, 0f, 0f)));
            clock.NowUs = 1_000_000UL;
            interp.Tick();

            Assert.AreEqual(Vector3.zero, _obj.transform.localPosition,
                "Owner entity must not be touched by the interpolator.");
        }

        [Test]
        public void SkipsWhenNotJoined()
        {
            PoseBuffer buf = new PoseBuffer();
            FakeClock clock = new FakeClock();
            PoseInterpolator interp = new PoseInterpolator(buf, clock)
            {
                InterpolationBackTimeSec = 0f,
                IsJoined = () => false,
            };

            buf.Add(_entityId, Sample(1, 1_000_000, new Vector3(99f, 0f, 0f)));
            clock.NowUs = 1_000_000UL;
            interp.Tick();

            Assert.AreEqual(Vector3.zero, _obj.transform.localPosition);
        }
    }
}
