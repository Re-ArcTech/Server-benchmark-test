using System.Collections;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace YubiBench.Tests
{
    /// <summary>
    /// localhost のベンチサーバーに対して REST / WebSocket を実走させ、
    /// 接続・往復・RTT計測が機能することを確認する PlayMode スモークテスト。
    /// 事前に `go run .`（server/）を localhost:8080 で起動しておくこと。
    /// </summary>
    public class TransportSmokeTest
    {
        private const string Url = "http://localhost:8080";
        private const int Iterations = 10;

        [UnityTest]
        public IEnumerator Rest_RoundTrip_Works()
        {
            yield return Run(new RestTransport(Url));
        }

        [UnityTest]
        public IEnumerator WebSocket_RoundTrip_Works()
        {
            yield return Run(new WebSocketTransport(Url));
        }

        /// <summary>各シナリオを少数回まわして RTT が取れることを検証する。</summary>
        private IEnumerator Run(IBenchTransport t)
        {
            // 接続（Task を毎フレームポーリングして待つ）
            var connect = t.ConnectAsync();
            while (!connect.IsCompleted) yield return null;
            Assert.IsTrue(connect.Result, $"{t.Name}: connect failed (server起動済み? localhost:8080)");
            Debug.Log($"[SmokeTest] {t.Name} connect={t.LastConnectMillis:F2}ms");

            string[] scenarios = { "ping", "move", "kick", "goal" };
            foreach (var sc in scenarios)
            {
                int ok = 0;
                double sum = 0;
                double srv = 0;
                for (int i = 0; i < Iterations; i++)
                {
                    var rt = t.RoundTripAsync(Messages.Build(sc, i));
                    while (!rt.IsCompleted) yield return null;
                    if (rt.Result.Ok)
                    {
                        ok++;
                        sum += rt.Result.RttMillis;
                        srv += rt.Result.ServerProcMillis;
                    }
                }
                Assert.Greater(ok, 0, $"{t.Name}/{sc}: 成功した往復が0");
                Debug.Log($"[SmokeTest] {t.Name,-9} {sc,-5} ok={ok}/{Iterations} " +
                          $"avgRTT={sum / ok:F3}ms srvProc={srv / ok:F3}ms");
            }

            t.Close();
        }
    }
}
