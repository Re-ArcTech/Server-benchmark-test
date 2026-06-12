using System;
using System.Collections.Generic;
using System.Linq;

namespace YubiBench
{
    /// <summary>
    /// RTT サンプル群から統計量（min/avg/p50/p95/max/jitter）を出す。
    /// jitter は標準偏差で表す（値が小さいほど安定 ＝ カクつきにくい）。
    /// </summary>
    public class LatencyStats
    {
        private readonly List<double> _samples = new List<double>();

        public void Add(double rttMillis)
        {
            if (rttMillis >= 0) _samples.Add(rttMillis);
        }

        public int Count => _samples.Count;
        public bool HasData => _samples.Count > 0;

        public double Min => HasData ? _samples.Min() : 0;
        public double Max => HasData ? _samples.Max() : 0;
        public double Avg => HasData ? _samples.Average() : 0;

        public double Percentile(double p)
        {
            if (!HasData) return 0;
            var sorted = _samples.OrderBy(x => x).ToList();
            double idx = p / 100.0 * (sorted.Count - 1);
            int lo = (int)Math.Floor(idx);
            int hi = (int)Math.Ceiling(idx);
            if (lo == hi) return sorted[lo];
            double frac = idx - lo;
            return sorted[lo] * (1 - frac) + sorted[hi] * frac;
        }

        public double P50 => Percentile(50);
        public double P95 => Percentile(95);

        /// <summary>ジッター（標準偏差）。小さいほど安定。</summary>
        public double Jitter
        {
            get
            {
                if (_samples.Count < 2) return 0;
                double avg = Avg;
                double sumSq = _samples.Sum(x => (x - avg) * (x - avg));
                return Math.Sqrt(sumSq / _samples.Count);
            }
        }

        public IReadOnlyList<double> Samples => _samples;
    }
}
