using System.Collections.Generic;
using UnityEngine;

namespace SyncLab
{
    /// <summary>
    /// 受信した位置スナップショットを溜めて「過去の時刻」を補間で取り出すバッファ。
    /// renderT = 最新サーバー時刻 - 補間ディレイ を渡すと、前後2点を挟んで lerp して返す。
    /// これが「補間で過去を描く(snapshot interpolation)」の中身。
    /// </summary>
    public class SnapshotBuffer
    {
        private struct Snap { public double tMs; public Vector3 pos; public Vector3 vel; }
        private readonly List<Snap> _snaps = new List<Snap>();
        private const int MaxSnaps = 64;

        public void Add(double tMs, Vector3 pos, Vector3 vel)
        {
            _snaps.Add(new Snap { tMs = tMs, pos = pos, vel = vel });
            if (_snaps.Count > MaxSnaps) _snaps.RemoveAt(0);
        }

        public double NewestT => _snaps.Count > 0 ? _snaps[_snaps.Count - 1].tMs : 0;
        public int Count => _snaps.Count;

        /// <summary>renderT 時点の位置を補間で返す。extrapolate=true なら最新より先は速度で外挿。</summary>
        public Vector3 Sample(double renderT, bool extrapolate)
        {
            if (_snaps.Count == 0) return Vector3.zero;
            if (_snaps.Count == 1) return _snaps[0].pos;

            // renderT が最古より前 → 最古
            if (renderT <= _snaps[0].tMs) return _snaps[0].pos;

            // 最新より後 → 外挿 or 最新でクランプ
            var newest = _snaps[_snaps.Count - 1];
            if (renderT >= newest.tMs)
            {
                if (!extrapolate) return newest.pos;
                float dt = (float)(renderT - newest.tMs) / 1000f;
                return newest.pos + newest.vel * dt;
            }

            // 前後2点を探して lerp
            for (int i = 0; i < _snaps.Count - 1; i++)
            {
                var a = _snaps[i];
                var b = _snaps[i + 1];
                if (renderT >= a.tMs && renderT <= b.tMs)
                {
                    double span = b.tMs - a.tMs;
                    float alpha = span <= 0 ? 0f : (float)((renderT - a.tMs) / span);
                    return Vector3.Lerp(a.pos, b.pos, alpha);
                }
            }
            return newest.pos;
        }
    }
}
