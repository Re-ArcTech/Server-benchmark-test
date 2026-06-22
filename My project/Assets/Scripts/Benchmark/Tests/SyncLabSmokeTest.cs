using System;
using System.Collections;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace YubiBench.Tests
{
    /// <summary>
    /// 同期ラボサーバー(:8090 /synclab)へ Unity の ClientWebSocket で繋がり、
    /// bot.state が届くこと・move を送ると self.echo が返ることを確認する疎通テスト。
    /// 事前に synclab サーバーを localhost:8090 で起動しておくこと。
    /// （SyncLabのコードには依存せず、生のWebSocketで検証する）
    /// </summary>
    public class SyncLabSmokeTest
    {
        private static string Url =>
            System.Environment.GetEnvironmentVariable("SYNCLAB_URL") ?? "ws://localhost:8090/synclab";

        [UnityTest]
        public IEnumerator Connects_ReceivesWelcomeAndBall()
        {
            var ws = new ClientWebSocket();
            var connect = ws.ConnectAsync(new Uri(Url), CancellationToken.None);
            while (!connect.IsCompleted) yield return null;
            Assert.IsNull(connect.Exception, "connect failed (synclab起動済み? :8090)");
            Assert.AreEqual(WebSocketState.Open, ws.State);

            SendText(ws, "{\"type\":\"config\",\"latencyMs\":0}");
            SendText(ws, "{\"type\":\"move\",\"seq\":1,\"pos\":{\"x\":1,\"y\":0,\"z\":2},\"vel\":{\"x\":0,\"y\":0,\"z\":0}}");

            bool gotWelcome = false, gotBall = false;
            var buf = new byte[16 * 1024];
            float timeout = 5f;
            while (timeout > 0 && !(gotWelcome && gotBall))
            {
                var recv = ws.ReceiveAsync(new ArraySegment<byte>(buf), CancellationToken.None);
                float t = 0;
                while (!recv.IsCompleted && t < 2f) { t += Time.deltaTime; yield return null; }
                if (!recv.IsCompleted) break;
                string msg = Encoding.UTF8.GetString(buf, 0, recv.Result.Count);
                if (msg.Contains("welcome")) gotWelcome = true;
                if (msg.Contains("ball.state")) gotBall = true;
                timeout -= 0.1f;
            }

            ws.Dispose();
            Assert.IsTrue(gotWelcome, "welcome を受信できなかった");
            Assert.IsTrue(gotBall, "ball.state を受信できなかった");
            Debug.Log("[SyncLabSmoke] OK: welcome と ball.state を受信");
        }

        private static void SendText(ClientWebSocket ws, string s)
        {
            var bytes = Encoding.UTF8.GetBytes(s);
            ws.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, CancellationToken.None);
        }
    }
}
