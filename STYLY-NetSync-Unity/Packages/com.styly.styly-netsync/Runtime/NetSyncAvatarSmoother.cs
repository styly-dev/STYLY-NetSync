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
        // --- Fixed parameters (no Inspector) ---
        const float InterpDelaySec   = 0.12f; // 100–150ms推奨。見た目の滑らかさ優先
        const float MaxExtrapSec     = 0.25f; // 欠落時の短時間外挿
        const float SnapDistanceM    = 2.0f;  // 大ジャンプ検出（距離）
        const float SnapAngleDeg     = 45.0f; // 大ジャンプ検出（角度）
        const int   BufferCapacity   = 64;    // バッファ上限
        const float KeepSeconds      = 2.0f;  // 古いスナップショットの保持秒数

        struct Pose
        {
            public Vector3 pos;
            public Quaternion rot;
        }

        class Snapshot
        {
            public float t;                 // 到着時刻（Time.time）
            public Pose physicalLocal;      // 物理（ローカル）
            public Pose headW;              // 頭（ワールド）
            public Pose rightW;             // 右手（ワールド）
            public Pose leftW;              // 左手（ワールド）
            public Pose[] virtualW;         // 仮想トランスフォーム群（ワールド）
        }

        readonly List<Snapshot> _buf = new();

        // === Public API ===

        /// <summary>
        /// 新しい受信データをバッファへ投入（即時反映しない）
        /// </summary>
        public void Enqueue(ClientTransformData d, float now, int virtualLimit)
        {
            var s = BuildSnapshot(d, now, virtualLimit);
            InsertSorted(s);
            Trim(now);
        }

        /// <summary>
        /// 指定Transform群へ、現在時刻の補間結果を適用
        /// </summary>
        public void Apply(Transform physicalLocal, Transform head, Transform right, Transform left, Transform[] virtuals, float now)
        {
            if (_buf.Count == 0) return;

            float target = now - InterpDelaySec;

            // 1) バッファ不足/対象が最古以前 → 最古にスナップ
            if (_buf.Count == 1 || target <= _buf[0].t)
            {
                ApplySnapshotImmediate(_buf[0], physicalLocal, head, right, left, virtuals);
                return;
            }

            var last = _buf[_buf.Count - 1];

            // 2) 対象が最新以後 → 短時間外挿（超過は最新へ）
            if (target >= last.t)
            {
                var second = _buf[_buf.Count - 2];
                float dtAB = Mathf.Max(1e-4f, last.t - second.t);
                float dtAhead = Mathf.Min(MaxExtrapSec, target - last.t);
                float u = (last.t + dtAhead - second.t) / dtAB; // 1〜1+α

                var ext = Blend(second, last, u);
                ApplySnapshotImmediate(ext, physicalLocal, head, right, left, virtuals);
                return;
            }

            // 3) 区間探索（2点補間）
            int lo = 0, hi = _buf.Count - 1;
            while (hi - lo > 1)
            {
                int mid = (lo + hi) >> 1;
                if (_buf[mid].t < target) lo = mid; else hi = mid;
            }
            var a = _buf[lo];
            var b = _buf[hi];

            // ワープ検出 → スナップしてバッファを最新に
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

        // === Build / Buffer ===

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

        Snapshot BuildSnapshot(ClientTransformData d, float now, int virtualLimit)
        {
            var s = new Snapshot { t = now };

            // physical（ローカル）
            s.physicalLocal = ToLocalPose(d.physical);

            // head/right/left（ワールド）
            s.headW  = ToWorldPose(d.head);
            s.rightW = ToWorldPose(d.rightHand);
            s.leftW  = ToWorldPose(d.leftHand);

            // virtuals（ワールド）
            int vCount = Mathf.Min(virtualLimit, d.virtuals != null ? d.virtuals.Count : 0);
            s.virtualW = new Pose[vCount];
            for (int i = 0; i < vCount; i++)
            {
                s.virtualW[i] = ToWorldPose(d.virtuals[i]);
            }

            return s;
        }

        void InsertSorted(Snapshot s)
        {
            if (_buf.Count == 0 || s.t >= _buf[_buf.Count - 1].t)
            {
                _buf.Add(s);
                return;
            }
            // 低頻度なので挿入ソートで十分
            int idx = _buf.BinarySearch(s, new SnapshotComparer());
            if (idx < 0) idx = ~idx;
            _buf.Insert(idx, s);
        }

        void Trim(float now)
        {
            // 古いものを削除
            float keepFrom = now - KeepSeconds;
            int rm = 0;
            while (rm < _buf.Count && _buf[rm].t < keepFrom) rm++;
            if (rm > 0) _buf.RemoveRange(0, rm);

            // 容量超過なら先頭から削除
            int overflow = _buf.Count - BufferCapacity;
            if (overflow > 0) _buf.RemoveRange(0, overflow);
        }

        class SnapshotComparer : IComparer<Snapshot>
        {
            public int Compare(Snapshot x, Snapshot y) => x.t.CompareTo(y.t);
        }

        // === Math / Apply ===

        static bool IsWarp(Snapshot a, Snapshot b)
        {
            float dist = Vector3.Distance(a.headW.pos, b.headW.pos);
            float ang  = Quaternion.Angle(a.headW.rot, b.headW.rot);
            return dist > SnapDistanceM || ang > SnapAngleDeg;
        }

        static Snapshot Blend(Snapshot a, Snapshot b, float u)
        {
            var r = new Snapshot();

            // physical local
            r.physicalLocal.pos = Vector3.LerpUnclamped(a.physicalLocal.pos, b.physicalLocal.pos, u);
            r.physicalLocal.rot = Quaternion.SlerpUnclamped(a.physicalLocal.rot, b.physicalLocal.rot, u);

            // head/right/left world
            r.headW.pos  = Vector3.LerpUnclamped(a.headW.pos,  b.headW.pos,  u);
            r.headW.rot  = Quaternion.SlerpUnclamped(a.headW.rot,  b.headW.rot,  u);

            r.rightW.pos = Vector3.LerpUnclamped(a.rightW.pos, b.rightW.pos, u);
            r.rightW.rot = Quaternion.SlerpUnclamped(a.rightW.rot, b.rightW.rot, u);

            r.leftW.pos  = Vector3.LerpUnclamped(a.leftW.pos,  b.leftW.pos,  u);
            r.leftW.rot  = Quaternion.SlerpUnclamped(a.leftW.rot,  b.leftW.rot,  u);

            // virtuals
            int vCount = Mathf.Min(a.virtualW?.Length ?? 0, b.virtualW?.Length ?? 0);
            r.virtualW = new Pose[vCount];
            for (int i = 0; i < vCount; i++)
            {
                r.virtualW[i].pos = Vector3.LerpUnclamped(a.virtualW[i].pos, b.virtualW[i].pos, u);
                r.virtualW[i].rot = Quaternion.SlerpUnclamped(a.virtualW[i].rot, b.virtualW[i].rot, u);
            }
            return r;
        }

        static void ApplySnapshotImmediate(Snapshot s,
            Transform physicalLocal,
            Transform head, Transform right, Transform left,
            Transform[] virtuals)
        {
            if (physicalLocal != null)
            {
                physicalLocal.localPosition = s.physicalLocal.pos;
                physicalLocal.localRotation = s.physicalLocal.rot;
            }

            if (head  != null) { head.position  = s.headW.pos;  head.rotation  = s.headW.rot; }
            if (right != null) { right.position = s.rightW.pos; right.rotation = s.rightW.rot; }
            if (left  != null) { left.position  = s.leftW.pos;  left.rotation  = s.leftW.rot; }

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

