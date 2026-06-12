// WebRTC 方式（簡易）。
//
// com.unity.webrtc パッケージが必要なため、Scripting Define Symbol "YUBI_WEBRTC" を
// 立てたときだけコンパイルされる。パッケージ未導入でもプロジェクト全体は壊れない。
//
// 有効化手順:
//   1. Window > Package Manager > + > Add package by name > com.unity.webrtc
//   2. Project Settings > Player > Scripting Define Symbols に YUBI_WEBRTC を追加
//   3. BenchmarkRunner の enableWebRtc を ON
#if YUBI_WEBRTC
using System;
using System.Diagnostics;
using System.Text;
using System.Threading.Tasks;
using Unity.WebRTC;
using UnityEngine.Networking;

namespace YubiBench
{
    /// <summary>
    /// WebRTC DataChannel 方式（簡易）。
    /// offer を1回POSTして answer を受け取るだけの最小シグナリングで接続し、
    /// DataChannel 越しにピンポンして RTT を計測する。
    /// 注意: WebRTC.Update() コルーチンが回っている必要がある（Runner が起動する）。
    /// </summary>
    public class WebRtcTransport : IBenchTransport
    {
        private readonly string _baseUrl;
        private RTCPeerConnection _pc;
        private RTCDataChannel _channel;
        private TaskCompletionSource<string> _pending;

        public string Name => "WebRTC";
        public double LastConnectMillis { get; private set; }

        public WebRtcTransport(string baseUrl)
        {
            _baseUrl = baseUrl.TrimEnd('/');
        }

        public async Task<bool> ConnectAsync()
        {
            var sw = Stopwatch.StartNew();
            try
            {
                var config = new RTCConfiguration
                {
                    iceServers = new[]
                    {
                        new RTCIceServer { urls = new[] { "stun:stun.l.google.com:19302" } }
                    }
                };
                _pc = new RTCPeerConnection(ref config);

                var opened = new TaskCompletionSource<bool>();
                _channel = _pc.CreateDataChannel("bench");
                _channel.OnOpen = () => opened.TrySetResult(true);
                _channel.OnMessage = bytes =>
                {
                    string text = Encoding.UTF8.GetString(bytes);
                    _pending?.TrySetResult(text);
                };

                // offer 作成 → ローカルにセット
                var offerOp = _pc.CreateOffer();
                await AwaitDone(() => offerOp.IsDone);
                var offerDesc = offerOp.Desc;
                var setLocal = _pc.SetLocalDescription(ref offerDesc);
                await AwaitDone(() => setLocal.IsDone);

                // ICE 候補が出揃うまで待つ（non-trickle）
                await WaitIceGatheringComplete(3000);

                // offer SDP をサーバーへ POST して answer を得る
                string answerSdp = await PostOffer(_pc.LocalDescription.sdp);
                if (string.IsNullOrEmpty(answerSdp)) return false;

                var answer = new RTCSessionDescription
                {
                    type = RTCSdpType.Answer,
                    sdp = answerSdp
                };
                var setRemote = _pc.SetRemoteDescription(ref answer);
                await AwaitDone(() => setRemote.IsDone);

                // DataChannel が開くまで待つ（タイムアウト付き）
                var openedTask = await Task.WhenAny(opened.Task, Task.Delay(8000));
                bool ok = openedTask == opened.Task && opened.Task.Result;

                sw.Stop();
                LastConnectMillis = sw.Elapsed.TotalMilliseconds;
                return ok;
            }
            catch (Exception)
            {
                sw.Stop();
                LastConnectMillis = sw.Elapsed.TotalMilliseconds;
                return false;
            }
        }

        public async Task<RoundTripResult> RoundTripAsync(string requestJson)
        {
            if (_channel == null || _channel.ReadyState != RTCDataChannelState.Open)
                return RoundTripResult.Fail;

            _pending = new TaskCompletionSource<string>();
            var sw = Stopwatch.StartNew();
            _channel.Send(requestJson);

            var finished = await Task.WhenAny(_pending.Task, Task.Delay(5000));
            sw.Stop();
            if (finished != _pending.Task)
                return RoundTripResult.Fail;

            var resp = UnityEngine.JsonUtility.FromJson<RespEnvelope>(_pending.Task.Result);
            return new RoundTripResult
            {
                RttMillis = sw.Elapsed.TotalMilliseconds,
                ServerProcMillis = resp != null ? resp.ServerProcessMillis : 0,
                Ok = true,
            };
        }

        public void Close()
        {
            try { _channel?.Close(); } catch (Exception) { }
            try { _pc?.Close(); } catch (Exception) { }
            _channel = null;
            _pc = null;
        }

        // --- helpers ---

        /// <summary>
        /// com.unity.webrtc の非同期オペレーションを await 可能にする（IsDone をポーリング）。
        /// 型ごとに基底クラスが異なるバージョンがあるため、IsDone を Func で受けて版差を吸収する。
        /// </summary>
        private static async Task AwaitDone(Func<bool> isDone)
        {
            while (!isDone())
                await Task.Delay(5);
        }

        private async Task WaitIceGatheringComplete(int timeoutMs)
        {
            var sw = Stopwatch.StartNew();
            while (_pc.GatheringState != RTCIceGatheringState.Complete && sw.ElapsedMilliseconds < timeoutMs)
                await Task.Delay(20);
        }

        private async Task<string> PostOffer(string sdp)
        {
            string json = UnityEngine.JsonUtility.ToJson(new RtcOffer { sdp = sdp });
            using (var req = new UnityWebRequest(_baseUrl + "/rtc/offer", "POST"))
            {
                req.uploadHandler = new UploadHandlerRaw(Encoding.UTF8.GetBytes(json));
                req.downloadHandler = new DownloadHandlerBuffer();
                req.SetRequestHeader("Content-Type", "application/json");

                var tcs = new TaskCompletionSource<bool>();
                var op = req.SendWebRequest();
                op.completed += _ => tcs.TrySetResult(true);
                await tcs.Task;

                if (req.result != UnityWebRequest.Result.Success) return null;
                var ans = UnityEngine.JsonUtility.FromJson<RtcAnswer>(req.downloadHandler.text);
                return ans != null ? ans.sdp : null;
            }
        }
    }
}
#endif
