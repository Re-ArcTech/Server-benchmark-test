using System;
using System.Globalization;
using UnityEngine;

namespace SyncLab
{
    /// <summary>
    /// 位置同期の手法を切り替えて比較するデバッグツール本体。
    /// ビジュアル（床・自キャラ・BOT・生位置ゴースト）はコードで生成し、
    /// OnGUI のパネルでトグル/スライダーを操作する。
    ///
    /// 色の意味:
    ///   青 = 自キャラの表示位置 / 赤(小) = サーバーが知ってる自分の位置(生)
    ///   緑 = BOTの表示位置       / 赤(小) = BOTの受信した生位置
    /// </summary>
    public class SyncLabController : MonoBehaviour
    {
        public enum Authority { Client, Hybrid, Server }

        [Header("接続")]
        public string serverUrl = "ws://localhost:8090";

        [Header("設定（OnGUIから操作）")]
        public Authority authority = Authority.Client;
        public bool otherInterpolate = true;
        public float interpDelayMs = 100f;
        public bool extrapolate = false;
        public bool selfPredict = true;
        public bool correctionOn = true;
        public float injectedLatencyMs = 0f;
        public float sendRateHz = 20f;

        private const float Speed = 6f;

        private SyncClient _client;
        private Transform _self, _bot, _selfGhost, _botGhost;

        private Vector3 _intendedPos;   // 入力から積分した「動かしたい」位置（送信＆予測表示に使う）
        private Vector3 _serverSelfPos; // サーバーが知ってる自分の位置（赤ゴースト）
        private readonly SnapshotBuffer _botBuf = new SnapshotBuffer();
        private Vector3 _botRawPos;     // 最新の生BOT位置
        private double _interpClockMs;

        private float _sendAccum;
        private long _seq;
        private int _correctionCount;

        // 設定変更検知（サーバーへconfig送信用）
        private Authority _lastAuth = (Authority)(-1);
        private float _lastLatency = -1, _lastRate = -1;

        // ---- セットアップ ----
        private void Awake()
        {
            BuildVisuals();
            _client = new SyncClient();
        }

        private void Start()
        {
            _client.Connect(serverUrl);
        }

        private void OnDestroy() => _client?.Close();

        private void BuildVisuals()
        {
            // 床
            var ground = GameObject.CreatePrimitive(PrimitiveType.Plane);
            ground.name = "Ground";
            ground.transform.localScale = new Vector3(2.4f, 1, 2.4f); // 24x24（フィールド±10）
            ground.GetComponent<Renderer>().sharedMaterial = Mat(new Color(0.15f, 0.18f, 0.22f));

            _self = MakeCapsule("Self_Display", new Color(0.2f, 0.5f, 1f));
            _bot = MakeCapsule("Bot_Display", new Color(0.2f, 0.85f, 0.4f));
            _selfGhost = MakeSphere("Self_Ghost(raw)", new Color(1f, 0.3f, 0.3f), 0.5f);
            _botGhost = MakeSphere("Bot_Ghost(raw)", new Color(1f, 0.5f, 0.2f), 0.5f);

            // カメラ（斜め見下ろし）
            if (Camera.main == null)
            {
                var camGo = new GameObject("Main Camera");
                camGo.tag = "MainCamera";
                var cam = camGo.AddComponent<Camera>();
                cam.clearFlags = CameraClearFlags.SolidColor;
                cam.backgroundColor = new Color(0.08f, 0.09f, 0.11f);
                camGo.transform.position = new Vector3(0, 20, -16);
                camGo.transform.rotation = Quaternion.Euler(52, 0, 0);
            }
            var lightGo = new GameObject("Sun");
            var lt = lightGo.AddComponent<Light>();
            lt.type = LightType.Directional;
            lightGo.transform.rotation = Quaternion.Euler(50, -30, 0);
        }

        private Transform MakeCapsule(string name, Color c)
        {
            var go = GameObject.CreatePrimitive(PrimitiveType.Capsule);
            go.name = name;
            go.GetComponent<Renderer>().sharedMaterial = Mat(c);
            var col = go.GetComponent<Collider>();
            if (col != null) Destroy(col);
            return go.transform;
        }

        private Transform MakeSphere(string name, Color c, float scale)
        {
            var go = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            go.name = name;
            go.transform.localScale = Vector3.one * scale;
            go.GetComponent<Renderer>().sharedMaterial = Mat(c);
            var col = go.GetComponent<Collider>();
            if (col != null) Destroy(col);
            return go.transform;
        }

        // URP環境でも確実に色がつくマテリアル
        private static Material Mat(Color c)
        {
            var sh = Shader.Find("Universal Render Pipeline/Unlit");
            if (sh == null) sh = Shader.Find("Unlit/Color");
            if (sh == null) sh = Shader.Find("Standard");
            var m = new Material(sh);
            if (m.HasProperty("_BaseColor")) m.SetColor("_BaseColor", c);
            if (m.HasProperty("_Color")) m.SetColor("_Color", c);
            m.color = c;
            return m;
        }

        // ---- メインループ ----
        private void Update()
        {
            float dt = Time.deltaTime;
            PushConfigIfChanged();

            // 1. 入力 → intendedPos を積分（常に「動かしたい位置」を更新）
            float ix = Input.GetAxisRaw("Horizontal");
            float iz = Input.GetAxisRaw("Vertical");
            var dir = new Vector3(ix, 0, iz);
            if (dir.sqrMagnitude > 1f) dir.Normalize();
            _intendedPos += dir * Speed * dt;

            // 2. 送信（sendRateHz で間引き）
            _sendAccum += dt;
            float interval = 1f / Mathf.Max(1f, sendRateHz);
            if (_client.Connected && _sendAccum >= interval)
            {
                _sendAccum = 0;
                if (authority == Authority.Server)
                    _client.Send(MsgInput(_seq, dir, interval));
                else
                    _client.Send(MsgMove(_seq, _intendedPos, dir * Speed));
                _seq++;
            }

            // 3. 受信処理
            while (_client.TryReceive(out var json))
                HandleMessage(json);

            // 4. 補間クロックを進める（最新サーバー時刻 - 補間ディレイ に緩く追従）
            _interpClockMs += dt * 1000.0;
            if (_botBuf.Count > 0)
            {
                double target = _botBuf.NewestT - interpDelayMs;
                _interpClockMs += (target - _interpClockMs) * 0.1; // 緩く同期
            }

            // 5. 表示更新
            Vector3 botShow = otherInterpolate ? _botBuf.Sample(_interpClockMs, extrapolate) : _botRawPos;
            _bot.position = Lift(botShow, 1f);
            _botGhost.position = Lift(_botRawPos, 0.5f);

            Vector3 selfShow = selfPredict ? _intendedPos : _serverSelfPos;
            _self.position = Lift(selfShow, 1f);
            _selfGhost.position = Lift(_serverSelfPos, 0.5f);
        }

        private static Vector3 Lift(Vector3 p, float y) => new Vector3(p.x, y, p.z);

        private void HandleMessage(string json)
        {
            SMsg m;
            try { m = JsonUtility.FromJson<SMsg>(json); }
            catch { return; }
            if (m == null || string.IsNullOrEmpty(m.type)) return;

            switch (m.type)
            {
                case "bot.state":
                    _botRawPos = m.pos.V();
                    _botBuf.Add(m.t, m.pos.V(), m.vel.V());
                    break;

                case "self.echo":
                    _serverSelfPos = m.pos.V();
                    break;

                case "self.auth":
                    _serverSelfPos = m.pos.V();
                    if (selfPredict && correctionOn)
                        _intendedPos = Vector3.Lerp(_intendedPos, _serverSelfPos, 0.15f); // 緩い訂正
                    break;

                case "self.correction":
                    _serverSelfPos = m.pos.V();
                    _correctionCount++;
                    if (correctionOn) _intendedPos = _serverSelfPos; // スナップ訂正
                    break;
            }
        }

        // ---- 設定送信 ----
        private void PushConfigIfChanged()
        {
            if (!_client.Connected) return;
            if (authority == _lastAuth && Mathf.Approximately(injectedLatencyMs, _lastLatency)
                && Mathf.Approximately(sendRateHz, _lastRate)) return;
            _lastAuth = authority; _lastLatency = injectedLatencyMs; _lastRate = sendRateHz;
            _client.Send(MsgConfig(AuthStr(authority), sendRateHz, injectedLatencyMs));
        }

        // ---- メッセージ構築 ----
        private static string F(float v) => v.ToString("0.###", CultureInfo.InvariantCulture);
        private static string V(Vector3 v) => $"{{\"x\":{F(v.x)},\"y\":{F(v.y)},\"z\":{F(v.z)}}}";

        private string MsgMove(long seq, Vector3 pos, Vector3 vel)
            => $"{{\"type\":\"move\",\"seq\":{seq},\"pos\":{V(pos)},\"vel\":{V(vel)}}}";
        private string MsgInput(long seq, Vector3 dir, float dt)
            => $"{{\"type\":\"input\",\"seq\":{seq},\"dir\":{V(dir)},\"dt\":{F(dt)}}}";
        private string MsgConfig(string auth, float hz, float lat)
            => $"{{\"type\":\"config\",\"authority\":\"{auth}\",\"sendRateHz\":{F(hz)},\"latencyMs\":{F(lat)}}}";
        private static string AuthStr(Authority a) => a == Authority.Server ? "server" : a == Authority.Hybrid ? "hybrid" : "client";

        // ---- デバッグUI ----
        private void OnGUI()
        {
            const int w = 340;
            GUILayout.BeginArea(new Rect(8, 8, w, Screen.height - 16), GUI.skin.box);
            GUILayout.Label("<b>同期ラボ (SyncLab)</b>", Rich());

            GUILayout.BeginHorizontal();
            serverUrl = GUILayout.TextField(serverUrl);
            if (GUILayout.Button("接続", GUILayout.Width(56))) _client.Connect(serverUrl);
            GUILayout.EndHorizontal();
            GUILayout.Label("状態: " + (_client.Connected ? "<color=#6f6>接続中</color>" : "<color=#f66>未接続</color> " + _client.LastError), Rich());

            GUILayout.Space(4);
            GUILayout.Label("<b>権威</b>", Rich());
            GUILayout.BeginHorizontal();
            if (GUILayout.Toggle(authority == Authority.Client, "クライアント")) authority = Authority.Client;
            if (GUILayout.Toggle(authority == Authority.Hybrid, "ハイブリッド")) authority = Authority.Hybrid;
            if (GUILayout.Toggle(authority == Authority.Server, "サーバー")) authority = Authority.Server;
            GUILayout.EndHorizontal();

            GUILayout.Space(4);
            GUILayout.Label("<b>他人(BOT)の見せ方</b>", Rich());
            otherInterpolate = GUILayout.Toggle(otherInterpolate, "補間（OFFで直接適用）");
            GUILayout.Label($"補間ディレイ: {interpDelayMs:F0} ms");
            interpDelayMs = GUILayout.HorizontalSlider(interpDelayMs, 0, 300);
            extrapolate = GUILayout.Toggle(extrapolate, "外挿（パケット欠け時に先読み）");

            GUILayout.Space(4);
            GUILayout.Label("<b>自分の見せ方</b>", Rich());
            selfPredict = GUILayout.Toggle(selfPredict, "予測（OFFでサーバー待ち＝遅延体感）");
            correctionOn = GUILayout.Toggle(correctionOn, "サーバー訂正を適用");

            GUILayout.Space(4);
            GUILayout.Label("<b>ネットワーク</b>", Rich());
            GUILayout.Label($"遅延注入: {injectedLatencyMs:F0} ms（片道）");
            injectedLatencyMs = GUILayout.HorizontalSlider(injectedLatencyMs, 0, 300);
            GUILayout.Label($"送信レート: {sendRateHz:F0} Hz");
            sendRateHz = GUILayout.HorizontalSlider(sendRateHz, 5, 60);

            GUILayout.Space(6);
            GUILayout.Label("<b>プリセット</b>", Rich());
            GUILayout.BeginHorizontal();
            if (GUILayout.Button("①素朴")) Preset(Authority.Client, false, false);
            if (GUILayout.Button("②C+補間")) Preset(Authority.Client, true, false);
            GUILayout.EndHorizontal();
            GUILayout.BeginHorizontal();
            if (GUILayout.Button("③ハイブリッド")) Preset(Authority.Hybrid, true, true);
            if (GUILayout.Button("④サーバー")) Preset(Authority.Server, true, false);
            GUILayout.EndHorizontal();

            GUILayout.Space(6);
            GUILayout.Label($"訂正回数: {_correctionCount}  バッファ: {_botBuf.Count}", Rich());
            GUILayout.Label("<color=#9cf>青=自分表示</color> <color=#f88>赤=生位置</color> <color=#9f9>緑=BOT表示</color>", Rich());
            GUILayout.Label("移動: WASD / 矢印キー", Rich());

            GUILayout.EndArea();
        }

        private void Preset(Authority a, bool interp, bool predict)
        {
            authority = a;
            otherInterpolate = interp;
            selfPredict = predict || a != Authority.Client; // サーバー権威は予測しないが訂正で寄せる
            if (a == Authority.Hybrid) { selfPredict = true; correctionOn = true; }
            if (a == Authority.Server) { selfPredict = false; correctionOn = true; }
            if (a == Authority.Client) { selfPredict = interp ? false : false; correctionOn = false; }
        }

        private static GUIStyle _rich;
        private static GUIStyle Rich()
        {
            if (_rich == null) _rich = new GUIStyle(GUI.skin.label) { richText = true };
            return _rich;
        }

        // ---- 受信メッセージ型 ----
        [Serializable] public class SV3 { public float x, y, z; public Vector3 V() => new Vector3(x, y, z); }
        [Serializable]
        public class SMsg
        {
            public string type;
            public long t;
            public long seq;
            public long ackSeq;
            public SV3 pos = new SV3();
            public float rotY;
            public SV3 vel = new SV3();
        }
    }
}
