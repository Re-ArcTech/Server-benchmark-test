using System.Diagnostics;
using System.Text;
using System.Threading.Tasks;
using UnityEngine.Networking;

namespace YubiBench
{
    /// <summary>
    /// REST（HTTP POST）方式。1メッセージ = 1リクエスト。
    /// 接続を張りっぱなしにしないので、毎回 HTTP の往復オーバーヘッドが乗る
    /// （keep-alive が効けば多少緩和されるが、WebSocket/WebRTC より不利になりやすい）。
    /// </summary>
    public class RestTransport : IBenchTransport
    {
        private readonly string _baseUrl; // 例: https://xxx.onrender.com
        public string Name => "REST";
        public double LastConnectMillis { get; private set; }

        public RestTransport(string baseUrl)
        {
            _baseUrl = baseUrl.TrimEnd('/');
        }

        /// <summary>REST は持続接続がないので、ヘルスチェック1回を warm-up として計測する。</summary>
        public async Task<bool> ConnectAsync()
        {
            var sw = Stopwatch.StartNew();
            using (var req = UnityWebRequest.Get(_baseUrl + "/health"))
            {
                await SendAsync(req);
                sw.Stop();
                LastConnectMillis = sw.Elapsed.TotalMilliseconds;
                return req.result == UnityWebRequest.Result.Success;
            }
        }

        public async Task<RoundTripResult> RoundTripAsync(string requestJson)
        {
            using (var req = new UnityWebRequest(_baseUrl + "/api/echo", "POST"))
            {
                byte[] body = Encoding.UTF8.GetBytes(requestJson);
                req.uploadHandler = new UploadHandlerRaw(body);
                req.downloadHandler = new DownloadHandlerBuffer();
                req.SetRequestHeader("Content-Type", "application/json");

                var sw = Stopwatch.StartNew();
                await SendAsync(req);
                sw.Stop();

                if (req.result != UnityWebRequest.Result.Success)
                    return RoundTripResult.Fail;

                var resp = UnityEngine.JsonUtility.FromJson<RespEnvelope>(req.downloadHandler.text);
                return new RoundTripResult
                {
                    RttMillis = sw.Elapsed.TotalMilliseconds,
                    ServerProcMillis = resp != null ? resp.ServerProcessMillis : 0,
                    Ok = true,
                };
            }
        }

        public void Close() { /* REST は持続接続なし */ }

        /// <summary>UnityWebRequest を await 可能にするラッパー。</summary>
        private static Task SendAsync(UnityWebRequest req)
        {
            var tcs = new TaskCompletionSource<bool>();
            var op = req.SendWebRequest();
            op.completed += _ => tcs.TrySetResult(true);
            return tcs.Task;
        }
    }
}
