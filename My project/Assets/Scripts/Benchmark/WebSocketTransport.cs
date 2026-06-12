using System;
using System.Diagnostics;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace YubiBench
{
    /// <summary>
    /// WebSocket 方式。標準の System.Net.WebSockets.ClientWebSocket を使う
    /// （iOS/IL2CPP で動作。WebGL では動かないが今回のターゲットは iOS なので問題なし）。
    /// 一度ハンドシェイクしたら接続を維持し、何度でも送受信できる。
    /// </summary>
    public class WebSocketTransport : IBenchTransport
    {
        private readonly Uri _wsUri;
        private ClientWebSocket _ws;
        private readonly byte[] _recvBuffer = new byte[64 * 1024];

        public string Name => "WebSocket";
        public double LastConnectMillis { get; private set; }

        public WebSocketTransport(string baseUrl)
        {
            // http(s) -> ws(s) に変換して /ws を付ける
            string b = baseUrl.TrimEnd('/');
            if (b.StartsWith("https://")) b = "wss://" + b.Substring("https://".Length);
            else if (b.StartsWith("http://")) b = "ws://" + b.Substring("http://".Length);
            _wsUri = new Uri(b + "/ws");
        }

        public async Task<bool> ConnectAsync()
        {
            try
            {
                _ws = new ClientWebSocket();
                var sw = Stopwatch.StartNew();
                await _ws.ConnectAsync(_wsUri, CancellationToken.None);
                sw.Stop();
                LastConnectMillis = sw.Elapsed.TotalMilliseconds;
                return _ws.State == WebSocketState.Open;
            }
            catch (Exception)
            {
                return false;
            }
        }

        public async Task<RoundTripResult> RoundTripAsync(string requestJson)
        {
            if (_ws == null || _ws.State != WebSocketState.Open)
                return RoundTripResult.Fail;

            try
            {
                byte[] send = Encoding.UTF8.GetBytes(requestJson);
                var sw = Stopwatch.StartNew();
                await _ws.SendAsync(new ArraySegment<byte>(send),
                    WebSocketMessageType.Text, true, CancellationToken.None);

                // 1メッセージぶん受信（このベンチは1リクエスト1レスポンスのピンポン）
                var result = await _ws.ReceiveAsync(new ArraySegment<byte>(_recvBuffer), CancellationToken.None);
                sw.Stop();

                if (result.MessageType == WebSocketMessageType.Close)
                    return RoundTripResult.Fail;

                string text = Encoding.UTF8.GetString(_recvBuffer, 0, result.Count);
                var resp = UnityEngine.JsonUtility.FromJson<RespEnvelope>(text);
                return new RoundTripResult
                {
                    RttMillis = sw.Elapsed.TotalMilliseconds,
                    ServerProcMillis = resp != null ? resp.ServerProcessMillis : 0,
                    Ok = true,
                };
            }
            catch (Exception)
            {
                return RoundTripResult.Fail;
            }
        }

        public void Close()
        {
            try
            {
                if (_ws != null && _ws.State == WebSocketState.Open)
                {
                    _ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "bye", CancellationToken.None)
                        .GetAwaiter().GetResult();
                }
            }
            catch (Exception) { /* ignore */ }
            finally
            {
                _ws?.Dispose();
                _ws = null;
            }
        }
    }
}
