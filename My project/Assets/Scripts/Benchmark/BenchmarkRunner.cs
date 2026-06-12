using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using UnityEngine;

namespace YubiBench
{
    /// <summary>
    /// REST / WebSocket / WebRTC を同じシナリオで計測して比較する本体（UI非依存）。
    /// 表示は <see cref="BenchmarkUI"/> がイベント（StatusChanged / ResultsReady）を購読して行う。
    /// 計測結果は CSV / JSON で persistentDataPath にも書き出す。
    /// </summary>
    public class BenchmarkRunner : MonoBehaviour
    {
        [Header("接続先（実機テスト時は Render の URL に変更）")]
        public string serverBaseUrl = "http://localhost:8080";

        [Header("各シナリオの試行回数")]
        public int pingCount = 30;
        public int moveCount = 50;
        public int kickCount = 20;
        public int goalCount = 20;

        [Header("計測する方式")]
        public bool enableRest = true;
        public bool enableWebSocket = true;
        public bool enableWebRtc = false; // com.unity.webrtc + YUBI_WEBRTC が必要

        [Header("実行")]
        public bool runOnStart = false;
        public bool quitAfterRun = false; // 計測完了後にアプリを終了（自動計測用）

        /// <summary>計測中の進捗テキスト。</summary>
        public event Action<string> StatusChanged;
        /// <summary>計測完了時、整形済み結果テキストを通知。</summary>
        public event Action<string> ResultsReady;

        public bool IsRunning => _running;

        private static readonly string[] ScenarioNames = { "ping", "move", "kick", "goal" };

        private bool _running;
        private readonly List<TransportResult> _results = new List<TransportResult>();
        private Dictionary<string, double> _scores = new Dictionary<string, double>();
        private string _exportPath = "";

        private class TransportResult
        {
            public string Name;
            public double ConnectMs;
            public bool Connected;
            public Dictionary<string, LatencyStats> ByScenario = new Dictionary<string, LatencyStats>();
            public Dictionary<string, LatencyStats> ServerProcByScenario = new Dictionary<string, LatencyStats>();
            public int Sent;
            public int Ok;
            public double SuccessRate => Sent > 0 ? (double)Ok / Sent : 0;
        }

        private void Start()
        {
#if YUBI_WEBRTC
            if (enableWebRtc)
                StartCoroutine(Unity.WebRTC.WebRTC.Update());
#endif
            if (runOnStart)
                StartBenchmark();
        }

        private int CountFor(string scenario)
        {
            switch (scenario)
            {
                case "ping": return pingCount;
                case "move": return moveCount;
                case "kick": return kickCount;
                case "goal": return goalCount;
                default: return 10;
            }
        }

        private void SetStatus(string s) => StatusChanged?.Invoke(s);

        /// <summary>計測を開始する（UIのボタン等から呼ぶ）。</summary>
        public void StartBenchmark()
        {
            if (_running) return;
            _ = RunBenchmarkAsync();
        }

        private async Task RunBenchmarkAsync()
        {
            _running = true;
            _results.Clear();
            _scores.Clear();
            _exportPath = "";

            try
            {
                var transports = BuildTransports();
                if (transports.Count == 0)
                {
                    SetStatus("計測する方式が選択されていません");
                    return;
                }

                foreach (var t in transports)
                {
                    SetStatus($"{t.Name}: 接続中...");
                    var r = new TransportResult { Name = t.Name };

                    bool ok = await t.ConnectAsync();
                    r.Connected = ok;
                    r.ConnectMs = t.LastConnectMillis;

                    if (!ok)
                    {
                        SetStatus($"{t.Name}: 接続失敗（サーバーURLを確認）");
                        _results.Add(r);
                        t.Close();
                        continue;
                    }

                    foreach (var name in ScenarioNames)
                    {
                        int n = CountFor(name);
                        var rtt = new LatencyStats();
                        var srv = new LatencyStats();
                        for (int i = 0; i < n; i++)
                        {
                            SetStatus($"{t.Name}: {name} {i + 1}/{n}");
                            var res = await t.RoundTripAsync(Messages.Build(name, i));
                            r.Sent++;
                            if (res.Ok)
                            {
                                r.Ok++;
                                rtt.Add(res.RttMillis);
                                srv.Add(res.ServerProcMillis);
                            }
                        }
                        r.ByScenario[name] = rtt;
                        r.ServerProcByScenario[name] = srv;
                    }

                    _results.Add(r);
                    t.Close();
                }

                ComputeScores();
                Export();
                string text = BuildResultsText();
                SetStatus("計測完了");
                ResultsReady?.Invoke(text);
                Debug.Log(text);
            }
            catch (Exception e)
            {
                SetStatus("エラー: " + e.Message);
                Debug.LogException(e);
            }
            finally
            {
                _running = false;
            }

            if (quitAfterRun)
            {
                await Task.Delay(500);
#if UNITY_EDITOR
                UnityEditor.EditorApplication.isPlaying = false;
#else
                Application.Quit();
#endif
            }
        }

        private List<IBenchTransport> BuildTransports()
        {
            var list = new List<IBenchTransport>();
            if (enableRest) list.Add(new RestTransport(serverBaseUrl));
            if (enableWebSocket) list.Add(new WebSocketTransport(serverBaseUrl));
            if (enableWebRtc)
            {
#if YUBI_WEBRTC
                list.Add(new WebRtcTransport(serverBaseUrl));
#else
                Debug.LogWarning("[Bench] WebRTC は YUBI_WEBRTC 未定義のためスキップ。");
#endif
            }
            return list;
        }

        private void ComputeScores()
        {
            var summaries = new List<SummaryMetrics>();
            foreach (var r in _results)
            {
                if (!r.Connected) continue;
                var allRtt = new LatencyStats();
                var allJit = new List<double>();
                foreach (var st in r.ByScenario.Values)
                {
                    foreach (var s in st.Samples) allRtt.Add(s);
                    if (st.HasData) allJit.Add(st.Jitter);
                }
                summaries.Add(new SummaryMetrics
                {
                    Name = r.Name,
                    AvgRtt = allRtt.Avg,
                    P95Rtt = allRtt.P95,
                    Jitter = allJit.Count > 0 ? allJit.Average() : 0,
                    ConnectMs = r.ConnectMs,
                    SuccessRate = r.SuccessRate,
                });
            }
            _scores = ScoreCalculator.ComputeScores(summaries);
        }

        /// <summary>画面・ログ表示用の整形済みテキストを作る。</summary>
        private string BuildResultsText()
        {
            var sb = new StringBuilder();
            sb.AppendLine($"接続先: {serverBaseUrl}");
            sb.AppendLine();
            sb.AppendLine("◆ 総合スコア（高いほど良い）");
            foreach (var kv in _scores.OrderByDescending(k => k.Value))
                sb.AppendLine($"   {kv.Key,-10} {kv.Value,6:F1} 点");
            sb.AppendLine();

            sb.AppendLine("◆ RTT(ms)  [avg / p50 / p95 / jitter]");
            foreach (var r in _results)
            {
                sb.AppendLine($" ■ {r.Name}  接続={r.ConnectMs:F0}ms  成功={r.SuccessRate * 100:F0}%");
                if (!r.Connected) { sb.AppendLine("    （接続できませんでした）"); continue; }
                foreach (var sc in ScenarioNames)
                {
                    if (!r.ByScenario.TryGetValue(sc, out var st) || !st.HasData) continue;
                    double sp = r.ServerProcByScenario.TryGetValue(sc, out var sps) ? sps.Avg : 0;
                    sb.AppendLine($"    {sc,-5} {st.Avg,7:F2} / {st.P50,6:F2} / {st.P95,6:F2} / {st.Jitter,5:F2}  (srv {sp:F3})");
                }
            }
            sb.AppendLine();
            sb.AppendLine("◆ シナリオ別おすすめ（p95最小）");
            foreach (var sc in ScenarioNames)
            {
                var best = _results
                    .Where(r => r.Connected && r.ByScenario.TryGetValue(sc, out var st) && st.HasData)
                    .OrderBy(r => r.ByScenario[sc].P95)
                    .FirstOrDefault();
                if (best != null)
                    sb.AppendLine($"    {sc,-5} → {best.Name} (p95 {best.ByScenario[sc].P95:F2}ms)");
            }

            if (!string.IsNullOrEmpty(_exportPath))
            {
                sb.AppendLine();
                sb.AppendLine($"CSV出力: {_exportPath}");
            }
            return sb.ToString();
        }

        private void Export()
        {
            var csv = new StringBuilder();
            csv.AppendLine("transport,scenario,count,avg_ms,p50_ms,p95_ms,min_ms,max_ms,jitter_ms,server_proc_ms,connect_ms,success_rate,score");
            foreach (var r in _results)
            {
                double score = _scores.TryGetValue(r.Name, out var s) ? s : 0;
                foreach (var sc in ScenarioNames)
                {
                    if (!r.ByScenario.TryGetValue(sc, out var st)) continue;
                    double srv = r.ServerProcByScenario.TryGetValue(sc, out var sp) ? sp.Avg : 0;
                    csv.AppendLine(string.Join(",",
                        r.Name, sc, st.Count.ToString(),
                        F(st.Avg), F(st.P50), F(st.P95), F(st.Min), F(st.Max),
                        F(st.Jitter), F(srv), F(r.ConnectMs),
                        F(r.SuccessRate), F(score)));
                }
            }

            string dir = Application.persistentDataPath;
            string csvPath = Path.Combine(dir, "yubi_bench_result.csv");
            File.WriteAllText(csvPath, csv.ToString());
            File.WriteAllText(Path.Combine(dir, "yubi_bench_result.json"), BuildJson());
            _exportPath = csvPath;
            Debug.Log($"[Bench] exported: {csvPath}");
        }

        private string BuildJson()
        {
            var sb = new StringBuilder();
            sb.Append("{\"server\":\"").Append(serverBaseUrl).Append("\",\"transports\":[");
            for (int i = 0; i < _results.Count; i++)
            {
                var r = _results[i];
                if (i > 0) sb.Append(",");
                double score = _scores.TryGetValue(r.Name, out var s) ? s : 0;
                sb.Append("{\"name\":\"").Append(r.Name).Append("\",")
                  .Append("\"connectMs\":").Append(F(r.ConnectMs)).Append(",")
                  .Append("\"successRate\":").Append(F(r.SuccessRate)).Append(",")
                  .Append("\"score\":").Append(F(score)).Append(",")
                  .Append("\"scenarios\":{");
                var names = ScenarioNames.Where(n => r.ByScenario.ContainsKey(n)).ToList();
                for (int j = 0; j < names.Count; j++)
                {
                    var st = r.ByScenario[names[j]];
                    if (j > 0) sb.Append(",");
                    sb.Append("\"").Append(names[j]).Append("\":{")
                      .Append("\"avg\":").Append(F(st.Avg)).Append(",")
                      .Append("\"p50\":").Append(F(st.P50)).Append(",")
                      .Append("\"p95\":").Append(F(st.P95)).Append(",")
                      .Append("\"jitter\":").Append(F(st.Jitter)).Append("}");
                }
                sb.Append("}}");
            }
            sb.Append("]}");
            return sb.ToString();
        }

        private static string F(double v) => v.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);
    }
}
