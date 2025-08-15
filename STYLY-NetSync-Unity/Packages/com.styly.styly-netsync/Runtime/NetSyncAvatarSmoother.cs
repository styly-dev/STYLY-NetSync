using System.Collections.Generic;
using UnityEngine;

namespace Styly.NetSync
{
    /// <summary>
    /// Avatar transform interpolation/extrapolation buffer (Phase 1).
    /// 10Hz入力をTime.time基準でバッファし、LateUpdateで時間補間して適用する。
    /// </summary>
    internal sealed class NetSyncAvatarSmoother
    {
        const float InterpDelaySec = 0.12f; // 100–150ms 推奨
        const float MaxExtrapSec = 0.25f;   // 欠落時の短時間外挿
        const float SnapDistanceM = 2.0f;   // 大ジャンプ検出（距離）
        const float SnapAngleDeg = 45.0f;   // 大ジャンプ検出（角度）
        const int BufferCapacity = 64;      // バッファ上限
        const float KeepSeconds = 2.0f;     // 古いスナップショットの保持秒数

        struct Pose
        {
            public Vector3 pos;
            public Quaternion rot;
        }

        class Snapshot
        {
            public float t;
            public Pose physicalLocal;
            public Pose headW;
            public Pose rightW;
            public Pose leftW;
            public Pose[] virtualW;
        }

        readonly List<Snapshot> _buf = new();

        public void Enqueue(ClientTransformData d, float now, int virtualLimit)
        {
            var s = BuildSnapshot(d, now, virtualLimit);
            InsertSorted(s);
            Trim(now);
        }

        public void Apply(Transform physicalLocal, Transform head, Transform right, Transform left, Transform[] virtuals, float now)
        {
            if (_buf.Count == 0) return;

            float target = now - InterpDelaySec;

            if (_buf.Count == 1 || target <= _buf[0].t)
            {
                ApplySnapshotImmediate(_buf[0], physicalLocal, head, right, left, virtuals);
                return;
            }

            var last = _buf[_buf.Count - 1];

            if (target >= last.t)
            {
                var second = _buf[_buf.Count - 2];
                float dtAB = Mathf.Max(1e-4f, last.t - second.t);
                float dtAhead = Mathf.Min(MaxExtrapSec, target - last.t);
                float u = (last.t + dtAhead - second.t) / dtAB;

                var ext = Blend(second, last, u);
                ApplySnapshotImmediate(ext, physicalLocal, head, right, left, virtuals);
                return;
            }

            int lo = 0, hi = _buf.Count - 1;
            while (hi - lo > 1)
            {
                int mid = (lo + hi) >> 1;
                if (_buf[mid].t < target) lo = mid; else hi = mid;
            }

            var a = _buf[lo];
            var b = _buf[hi];

            if (IsWarp(a, b))
            {
                ApplySnapshotImmediate(b, physicalLocal, head, right, left, virtuals);
                _buf.Clear();
                _buf.Add(b);
                return;
            }

            float uLerp = Mathf.InverseLerp(a.t, b.t, target);
            var blended = Blend(a, b, uLerp);
            ApplySnapshotImmediate(blended, physicalLocal, head, right, left, virtuals);
        }

        Snapshot BuildSnapshot(ClientTransformData d, float now, int virtualLimit)
        {
            var s = new Snapshot { t = now };

            s.physicalLocal = ToLocalPose(d.physical);
            s.headW = ToWorldPose(d.head);
            s.rightW = ToWorldPose(d.rightHand);
            s.leftW = ToWorldPose(d.leftHand);

            int vCount = Mathf.Min(virtualLimit, d.virtuals != null ? d.virtuals.Count : 0);
            s.virtualW = new Pose[vCount];
            for (int i = 0; i < vCount; i++)
            {
                s.virtualW[i] = ToWorldPose(d.virtuals[i]);
            }

            return s;
        }

        static Pose ToWorldPose(TransformData td)
        {
            if (td == null) return new Pose { pos = Vector3.zero, rot = Quaternion.identity };
            return new Pose { pos = td.GetPosition(), rot = Quaternion.Euler(td.GetRotation()) };
        }

        static Pose ToLocalPose(TransformData td)
        {
            if (td == null) return new Pose { pos = Vector3.zero, rot = Quaternion.identity };
            return new Pose { pos = td.GetPosition(), rot = Quaternion.Euler(td.GetRotation()) };
        }

        void InsertSorted(Snapshot s)
        {
            if (_buf.Count == 0 || s.t >= _buf[_buf.Count - 1].t)
            {
                _buf.Add(s);
                return;
            }

            int idx = _buf.BinarySearch(s, new SnapshotComparer());
            if (idx < 0) idx = ~idx;
            _buf.Insert(idx, s);
        }

        void Trim(float now)
        {
            float keepFrom = now - KeepSeconds;
            int rm = 0;
            while (rm < _buf.Count && _buf[rm].t < keepFrom) rm++;
            if (rm > 0) _buf.RemoveRange(0, rm);

            int overflow = _buf.Count - BufferCapacity;
            if (overflow > 0) _buf.RemoveRange(0, overflow);
        }

        class SnapshotComparer : IComparer<Snapshot>
        {
            public int Compare(Snapshot x, Snapshot y) => x.t.CompareTo(y.t);
        }

        static bool IsWarp(Snapshot a, Snapshot b)
        {
            float dist = Vector3.Distance(a.headW.pos, b.headW.pos);
            float ang = Quaternion.Angle(a.headW.rot, b.headW.rot);
            return dist > SnapDistanceM || ang > SnapAngleDeg;
        }

        static Snapshot Blend(Snapshot a, Snapshot b, float u)
        {
            var r = new Snapshot();

            r.physicalLocal.pos = Vector3.LerpUnclamped(a.physicalLocal.pos, b.physicalLocal.pos, u);
            r.physicalLocal.rot = Quaternion.SlerpUnclamped(a.physicalLocal.rot, b.physicalLocal.rot, u);

            r.headW.pos = Vector3.LerpUnclamped(a.headW.pos, b.headW.pos, u);
            r.headW.rot = Quaternion.SlerpUnclamped(a.headW.rot, b.headW.rot, u);

            r.rightW.pos = Vector3.LerpUnclamped(a.rightW.pos, b.rightW.pos, u);
            r.rightW.rot = Quaternion.SlerpUnclamped(a.rightW.rot, b.rightW.rot, u);

            r.leftW.pos = Vector3.LerpUnclamped(a.leftW.pos, b.leftW.pos, u);
            r.leftW.rot = Quaternion.SlerpUnclamped(a.leftW.rot, b.leftW.rot, u);

            int vCount = Mathf.Min(a.virtualW?.Length ?? 0, b.virtualW?.Length ?? 0);
            r.virtualW = new Pose[vCount];
            for (int i = 0; i < vCount; i++)
            {
                r.virtualW[i].pos = Vector3.LerpUnclamped(a.virtualW[i].pos, b.virtualW[i].pos, u);
                r.virtualW[i].rot = Quaternion.SlerpUnclamped(a.virtualW[i].rot, b.virtualW[i].rot, u);
            }

            return r;
        }

        static void ApplySnapshotImmediate(Snapshot s, Transform physicalLocal, Transform head, Transform right, Transform left, Transform[] virtuals)
        {
            if (physicalLocal != null)
            {
                physicalLocal.localPosition = s.physicalLocal.pos;
                physicalLocal.localRotation = s.physicalLocal.rot;
            }

            if (head != null)
            {
                head.position = s.headW.pos;
                head.rotation = s.headW.rot;
            }

            if (right != null)
            {
                right.position = s.rightW.pos;
                right.rotation = s.rightW.rot;
            }

            if (left != null)
            {
                left.position = s.leftW.pos;
                left.rotation = s.leftW.rot;
            }

            if (virtuals != null && s.virtualW != null)
            {
                int n = Mathf.Min(virtuals.Length, s.virtualW.Length);
                for (int i = 0; i < n; i++)
                {
                    var t = virtuals[i];
                    if (t == null) continue;
                    t.position = s.virtualW[i].pos;
                    t.rotation = s.virtualW[i].rot;
                }
            }
        }
    }
}

