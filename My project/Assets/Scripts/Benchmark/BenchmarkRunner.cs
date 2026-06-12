using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using UnityEngine;

namespace YubiBench
{
    /// <summary>
    /// REST / WebSocket / WebRTC を同じシナリオで計測して比較する本体。
    /// シーンの空 GameObject に貼り、Play すると（runOnStart=ON なら）自動計測する。
    /// 画面の OnGUI に結果テーブルと総合スコア、シナリオ別の推奨方式を表示し、
    /// 同じ内容を CSV / JSON で persistentDataPath に書き出す。
    /// </summary>
    public class BenchmarkRunner : MonoBehaviour
    {
        [Header("接続先（実機テスト時は Render の URL に変更）")]
        public string serverBaseUrl = "http://localhost:8080";

        [Header("各シナリオの試行回数")]
        public int pingCount = 50;
        public int moveCount = 100;
        public int kickCount = 30;
        public int goalCount = 30;

        [Header("計測する方式")]
        public bool enableRest = true;
        public bool enableWebSocket = true;
        public bool enableWebRtc = false; // com.unity.webrtc + YUBI_WEBRTC が必要

        [Header("実行")]
        public bool runOnStart = false;

        // シナリオ定義（名前, 回数を引く）
        private (string name, Func<int> count)[] Scenarios => new (string, Func<int>)[]
        {
            ("ping", () => pingCount),
            ("move", () => moveCount),
            ("kick", () => kickCount),
            ("goal", () => goalCount),
        };

        private bool _running;
        private string _status = "Idle. Press Run.";
        private readonly List<TransportResult> _results = new List<TransportResult>();
        private Dictionary<string, double> _scores = new Dictionary<string, double>();
        private Vector2 _scroll;
        private string _exportPath = "";

        /// <summary>1トランスポートの全シナリオ結果。</summary>
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
                RunBenchmark();
        }

        // ---- UI ----
        private void OnGUI()
        {
            const int w = 720;
            GUILayout.BeginArea(new Rect(10, 10, w, Screen.height - 20), GUI.skin.box);

            GUILayout.Label("<b>ゆびサッカー 通信方式ベンチ</b> (REST / WebSocket / WebRTC)",
                RichLabel());

            GUILayout.BeginHorizontal();
            GUILayout.Label("Server:", GUILayout.Width(55));
            serverBaseUrl = GUILayout.TextField(serverBaseUrl, GUILayout.Width(420));
            GUILayout.EndHorizontal();

            GUILayout.BeginHorizontal();
            enableRest = GUILayout.Toggle(enableRest, "REST", GUILayout.Width(90));
            enableWebSocket = GUILayout.Toggle(enableWebSocket, "WebSocket", GUILayout.Width(110));
            enableWebRtc = GUILayout.Toggle(enableWebRtc, "WebRTC", GUILayout.Width(90));
            GUILayout.EndHorizontal();

            GUI.enabled = !_running;
            if (GUILayout.Button(_running ? "Running..." : "Run Benchmark", GUILayout.Height(34)))
                RunBenchmark();
            GUI.enabled = true;

            GUILayout.Label("Status: " + _status);

            _scroll = GUILayout.BeginScrollView(_scroll);
            DrawResults();
            GUILayout.EndScrollView();

            if (!string.IsNullOrEmpty(_exportPath))
                GUILayout.Label("Exported: " + _exportPath);

            GUILayout.EndArea();
        }

        private static GUIStyle _rich;
        private static GUIStyle RichLabel()
        {
            if (_rich == null) _rich = new GUIStyle(GUI.skin.label) { richText = true, fontSize = 14 };
            return _rich;
        }

        private void DrawResults()
        {
            if (_results.Count == 0) return;

            GUILayout.Label("<b>総合スコア（高いほど良い / 各項目の最良を満点とした相対評価）</b>", RichLabel());
            foreach (var kv in _scores.OrderByDescending(k => k.Value))
                GUILayout.Label($"  {kv.Key,-10} : {kv.Value,6:F1} 点");

            GUILayout.Space(6);
            GUILayout.Label("<b>RTT (ms) シナリオ別</b>  [avg / p50 / p95 / jitter]", RichLabel());
            foreach (var r in _results)
            {
                GUILayout.Label($"■ {r.Name}  connect={r.ConnectMs:F1}ms  success={r.SuccessRate * 100:F0}%");
                foreach (var sc in Scenarios.Select(s => s.name))
                {
                    if (!r.ByScenario.TryGetValue(sc, out var st) || !st.HasData) continue;
                    double sp = r.ServerProcByScenario.TryGetValue(sc, out var sps) ? sps.Avg : 0;
                    GUILayout.Label(
                        $"    {sc,-5} avg={st.Avg,6:F2} p50={st.P50,6:F2} p95={st.P95,6:F2} " +
                        $"jit={st.Jitter,5:F2} (srv {sp:F3}ms)");
                }
            }

            GUILayout.Space(6);
            GUILayout.Label("<b>シナリオ別の推奨方式（p95 RTT が最小）</b>", RichLabel());
            foreach (var sc in Scenarios.Select(s => s.name))
            {
                var best = _results
                    .Where(r => r.ByScenario.TryGetValue(sc, out var st) && st.HasData)
                    .OrderBy(r => r.ByScenario[sc].P95)
                    .FirstOrDefault();
                if (best != null)
                    GUILayout.Label($"  {sc,-5} → {best.Name}  (p95={best.ByScenario[sc].P95:F2}ms)");
            }
        }

        // ---- 実行 ----
        private async void RunBenchmark()
        {
            if (_running) return;
            _running = true;
            _results.Clear();
            _scores.Clear();
            _exportPath = "";

            try
            {
                var transports = BuildTransports();
                foreach (var t in transports)
                {
                    _status = $"{t.Name}: connecting...";
                    var r = new TransportResult { Name = t.Name };

                    bool ok = await t.ConnectAsync();
                    r.Connected = ok;
                    r.ConnectMs = t.LastConnectMillis;

                    if (!ok)
                    {
                        _status = $"{t.Name}: connect FAILED";
                        _results.Add(r);
                        t.Close();
                        continue;
                    }

                    foreach (var (name, count) in Scenarios)
                    {
                        int n = count();
                        var rtt = new LatencyStats();
                        var srv = new LatencyStats();
                        for (int i = 0; i < n; i++)
                        {
                            _status = $"{t.Name}: {name} {i + 1}/{n}";
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
                _status = "Done.";
                LogSummary();
            }
            catch (Exception e)
            {
                _status = "ERROR: " + e.Message;
                Debug.LogException(e);
            }
            finally
            {
                _running = false;
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
                Debug.LogWarning("[Bench] WebRTC は YUBI_WEBRTC 未定義のためスキップ。" +
                                 "com.unity.webrtc を入れて Scripting Define に YUBI_WEBRTC を追加してください。");
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

        // ---- 出力 ----
        private void Export()
        {
            var csv = new StringBuilder();
            csv.AppendLine("transport,scenario,count,avg_ms,p50_ms,p95_ms,min_ms,max_ms,jitter_ms,server_proc_ms,connect_ms,success_rate,score");
            foreach (var r in _results)
            {
                double score = _scores.TryGetValue(r.Name, out var s) ? s : 0;
                foreach (var sc in Scenarios.Select(x => x.name))
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

            // JSON も（人が読む用の簡易構造）
            string jsonPath = Path.Combine(dir, "yubi_bench_result.json");
            File.WriteAllText(jsonPath, BuildJson());

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
                var names = Scenarios.Select(x => x.name).Where(n => r.ByScenario.ContainsKey(n)).ToList();
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

        private void LogSummary()
        {
            var sb = new StringBuilder();
            sb.AppendLine("===== Yubi Bench Summary =====");
            sb.AppendLine("server: " + serverBaseUrl);
            foreach (var kv in _scores.OrderByDescending(k => k.Value))
                sb.AppendLine($"score {kv.Key,-10}: {kv.Value:F1}");
            foreach (var r in _results)
            {
                sb.AppendLine($"-- {r.Name} connect={r.ConnectMs:F1}ms success={r.SuccessRate * 100:F0}%");
                foreach (var sc in Scenarios.Select(x => x.name))
                {
                    if (!r.ByScenario.TryGetValue(sc, out var st) || !st.HasData) continue;
                    sb.AppendLine($"   {sc,-5} avg={st.Avg:F2} p50={st.P50:F2} p95={st.P95:F2} jit={st.Jitter:F2}");
                }
            }
            Debug.Log(sb.ToString());
        }

        private static string F(double v) => v.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);
    }
}
