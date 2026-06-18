using System;
using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;

namespace SyncLab
{
    /// <summary>
    /// 同期ラボ用の WebSocket クライアント。
    /// 受信は別スレッドで行い、メインスレッドが Update で取り出す（スレッド安全）。
    /// 送信はキューに積んで1本のループが順に送る（ClientWebSocketの同時送信を避ける）。
    /// </summary>
    public class SyncClient
    {
        private ClientWebSocket _ws;
        private readonly ConcurrentQueue<string> _incoming = new ConcurrentQueue<string>();
        private readonly ConcurrentQueue<string> _outgoing = new ConcurrentQueue<string>();
        private CancellationTokenSource _cts;

        public bool Connected => _ws != null && _ws.State == WebSocketState.Open;
        public string LastError { get; private set; } = "";

        public async void Connect(string baseUrl)
        {
            Close();
            try
            {
                string u = baseUrl.TrimEnd('/');
                if (u.StartsWith("https://")) u = "wss://" + u.Substring(8);
                else if (u.StartsWith("http://")) u = "ws://" + u.Substring(7);
                if (!u.EndsWith("/synclab")) u += "/synclab";

                _ws = new ClientWebSocket();
                _cts = new CancellationTokenSource();
                await _ws.ConnectAsync(new Uri(u), _cts.Token);
                LastError = "";
                _ = ReceiveLoop(_cts.Token);
                _ = SendLoop(_cts.Token);
            }
            catch (Exception e)
            {
                LastError = e.Message;
            }
        }

        public void Send(string json)
        {
            if (Connected) _outgoing.Enqueue(json);
        }

        public bool TryReceive(out string json) => _incoming.TryDequeue(out json);

        private async Task ReceiveLoop(CancellationToken ct)
        {
            var buf = new byte[16 * 1024];
            try
            {
                while (_ws.State == WebSocketState.Open && !ct.IsCancellationRequested)
                {
                    var res = await _ws.ReceiveAsync(new ArraySegment<byte>(buf), ct);
                    if (res.MessageType == WebSocketMessageType.Close) break;
                    _incoming.Enqueue(Encoding.UTF8.GetString(buf, 0, res.Count));
                }
            }
            catch (Exception e) { LastError = e.Message; }
        }

        private async Task SendLoop(CancellationToken ct)
        {
            try
            {
                while (_ws.State == WebSocketState.Open && !ct.IsCancellationRequested)
                {
                    if (_outgoing.TryDequeue(out var msg))
                    {
                        var bytes = Encoding.UTF8.GetBytes(msg);
                        await _ws.SendAsync(new ArraySegment<byte>(bytes),
                            WebSocketMessageType.Text, true, ct);
                    }
                    else
                    {
                        await Task.Delay(2, ct);
                    }
                }
            }
            catch (Exception e) { LastError = e.Message; }
        }

        public void Close()
        {
            try { _cts?.Cancel(); } catch { }
            try { _ws?.Dispose(); } catch { }
            _ws = null;
        }
    }
}
