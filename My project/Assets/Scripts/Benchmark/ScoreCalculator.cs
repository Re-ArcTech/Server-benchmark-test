using System.Collections.Generic;
using System.Linq;

namespace YubiBench
{
    /// <summary>1トランスポートを総合評価するための集約メトリクス。</summary>
    public struct SummaryMetrics
    {
        public string Name;
        public double AvgRtt;      // 全シナリオ平均RTT(ms)
        public double P95Rtt;      // 全シナリオp95 RTT(ms)
        public double Jitter;      // 平均ジッター(ms)
        public double ConnectMs;   // 接続確立(ms)
        public double SuccessRate; // 成功率 0..1
    }

    /// <summary>
    /// 複数トランスポートの集約メトリクスから 0-100 の総合スコアを出す。
    /// 各メトリクスは「その項目で最も良かった方式」を満点として相対評価する
    /// （best/value 比）。重み付けは下記。
    /// </summary>
    public static class ScoreCalculator
    {
        // 重み（合計1.0）
        private const double WLatency = 0.40; // 平均RTT + p95RTT
        private const double WJitter = 0.25;  // 安定性（カクつきにくさ）
        private const double WConnect = 0.15; // 接続確立の速さ
        private const double WReliab = 0.20;  // 成功率

        public static Dictionary<string, double> ComputeScores(List<SummaryMetrics> all)
        {
            var result = new Dictionary<string, double>();
            if (all == null || all.Count == 0) return result;

            // 「小さいほど良い」メトリクスの最良値（=最小値、0は除外）
            double bestAvg = MinPositive(all.Select(m => m.AvgRtt));
            double bestP95 = MinPositive(all.Select(m => m.P95Rtt));
            double bestJit = MinPositive(all.Select(m => m.Jitter));
            double bestCon = MinPositive(all.Select(m => m.ConnectMs));

            foreach (var m in all)
            {
                // latency は avg と p95 の平均で評価
                double latScore = 0.5 * Ratio(bestAvg, m.AvgRtt) + 0.5 * Ratio(bestP95, m.P95Rtt);
                double jitScore = Ratio(bestJit, m.Jitter);
                double conScore = Ratio(bestCon, m.ConnectMs);
                double relScore = m.SuccessRate; // 0..1

                double score = 100.0 * (
                    WLatency * latScore +
                    WJitter * jitScore +
                    WConnect * conScore +
                    WReliab * relScore);

                result[m.Name] = score;
            }
            return result;
        }

        /// <summary>best/value。value が0以下なら満点扱い。0..1にクランプ。</summary>
        private static double Ratio(double best, double value)
        {
            if (value <= 0) return 1.0;
            if (best <= 0) return 1.0;
            double r = best / value;
            return r > 1.0 ? 1.0 : r;
        }

        private static double MinPositive(IEnumerable<double> xs)
        {
            var pos = xs.Where(x => x > 0).ToList();
            return pos.Count > 0 ? pos.Min() : 0;
        }
    }
}
