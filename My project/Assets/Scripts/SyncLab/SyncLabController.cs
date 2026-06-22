using System;
using System.Globalization;
using UnityEngine;

namespace SyncLab
{
    /// <summary>
    /// 「あなた」vs「相手から見えるあなた」を並べて、遅延と補間が何なのかを体感するデバッグツール。
    ///
    ///   青  = あなた（操作した瞬間に動く＝自分の画面の自分）
    ///   緑  = 相手の画面に映るあなた（サーバー経由で遅れて届いた姿を補間したもの）
    ///   赤(小) = 補間する前の生の受信位置（カクカク）
    ///
    /// 自分でWASDで動かすと、緑が遅れてついてくる。補間ON/OFFや遅延でその見え方が変わる。
    /// </summary>
    public class SyncLabController : MonoBehaviour
    {
        [Header("接続")]
        public string serverUrl = "ws://localhost:8090";

        [Header("設定（OnGUIから操作）")]
        public float injectedLatencyMs = 100f; // 片道のネットワーク遅延を再現
        public float jitterMs = 0f;            // 遅延のばらつき（到着ムラ）
        public float lossRate = 0f;            // パケットロス率(0〜1)
        public bool interpolate = true;          // 相手側が補間するか
        public float interpDelayMs = 100f;       // 補間ディレイ（過去をどれだけ遡って描くか）
        public bool extrapolate = false;
        public float sendRateHz = 20f;

        [Header("ボール")]
        public BallMode ballMode = BallMode.Extrap;
        public enum BallMode { Raw, Interp, Extrap }

        private const float Speed = 6f;

        private SyncClient _client;
        private Transform _you, _shadow, _rawGhost, _ball, _ballGhost;

        private Vector3 _intendedPos;            // 入力から積分した「あなた」の位置
        private readonly SnapshotBuffer _echoBuf = new SnapshotBuffer();
        private Vector3 _rawEchoPos;             // 最新の生エコー位置
        private double _interpClockMs;

        // ボール
        private readonly SnapshotBuffer _ballBuf = new SnapshotBuffer();
        private Vector3 _rawBallPos, _rawBallVel;
        private float _lastBallRecvTime;

        private float _sendAccum;
        private long _seq;

        private float _lastLatency = -1, _lastRate = -1, _lastJitter = -1, _lastLoss = -1;

        private void Awake()
        {
            BuildVisuals();
            _client = new SyncClient();
        }

        private void Start() => _client.Connect(serverUrl);
        private void OnDestroy() => _client?.Close();

        private void BuildVisuals()
        {
            var ground = GameObject.CreatePrimitive(PrimitiveType.Plane);
            ground.name = "Ground";
            ground.transform.localScale = new Vector3(2.4f, 1, 2.4f);
            ground.GetComponent<Renderer>().sharedMaterial = Mat(new Color(0.15f, 0.18f, 0.22f));

            _you = MakeCapsule("あなた(青)", new Color(0.25f, 0.55f, 1f));
            _shadow = MakeCapsule("相手から見えるあなた(緑)", new Color(0.25f, 0.85f, 0.4f));
            _rawGhost = MakeSphere("生位置(赤)", new Color(1f, 0.3f, 0.3f), 0.5f);
            _ball = MakeSphere("ボール(橙)", new Color(1f, 0.6f, 0.1f), 0.7f);
            _ballGhost = MakeSphere("ボール生位置(赤)", new Color(1f, 0.3f, 0.3f), 0.4f);

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

        private void Update()
        {
            float dt = Time.deltaTime;
            PushConfigIfChanged();

            // 1. 入力 → あなたの位置（即反映）
            float ix = Input.GetAxisRaw("Horizontal");
            float iz = Input.GetAxisRaw("Vertical");
            var dir = new Vector3(ix, 0, iz);
            if (dir.sqrMagnitude > 1f) dir.Normalize();
            _intendedPos += dir * Speed * dt;

            // 2. 位置をサーバーに送る（相手に届く想定。クライアント権威）
            _sendAccum += dt;
            float interval = 1f / Mathf.Max(1f, sendRateHz);
            if (_client.Connected && _sendAccum >= interval)
            {
                _sendAccum = 0;
                _client.Send(MsgMove(_seq, _intendedPos, dir * Speed));
                _seq++;
            }

            // キック（Space）。ボールに近ければサーバーが蹴る
            if (_client.Connected && Input.GetKeyDown(KeyCode.Space))
                _client.Send(MsgKick(_intendedPos));

            // 3. 受信（自分のエコー／ボール）
            while (_client.TryReceive(out var json))
            {
                SMsg m;
                try { m = JsonUtility.FromJson<SMsg>(json); } catch { continue; }
                if (m == null) continue;
                if (m.type == "self.echo")
                {
                    _rawEchoPos = m.pos.V();
                    _echoBuf.Add(m.t, m.pos.V(), m.vel.V());
                }
                else if (m.type == "ball.state")
                {
                    _rawBallPos = m.pos.V();
                    _rawBallVel = m.vel.V();
                    _ballBuf.Add(m.t, m.pos.V(), m.vel.V());
                    _lastBallRecvTime = Time.time;
                }
            }

            // 4. 補間クロック
            _interpClockMs += dt * 1000.0;
            if (_echoBuf.Count > 0)
            {
                double target = _echoBuf.NewestT - interpDelayMs;
                _interpClockMs += (target - _interpClockMs) * 0.1;
            }

            // 5. 表示
            _you.position = Lift(_intendedPos, 1f);                       // 青=あなた（今）
            Vector3 shadow = interpolate ? _echoBuf.Sample(_interpClockMs, extrapolate) : _rawEchoPos;
            _shadow.position = Lift(shadow, 1f);                          // 緑=相手から見えるあなた
            _rawGhost.position = Lift(_rawEchoPos, 0.5f);                 // 赤=生位置

            // ボール表示（生 / 補間=過去 / 外挿=未来）
            Vector3 ballShow;
            switch (ballMode)
            {
                case BallMode.Interp: ballShow = _ballBuf.Sample(_interpClockMs, false); break;
                case BallMode.Extrap: ballShow = _rawBallPos + _rawBallVel * (Time.time - _lastBallRecvTime); break;
                default: ballShow = _rawBallPos; break;
            }
            _ball.position = Lift(ballShow, 0.7f);
            _ballGhost.position = Lift(_rawBallPos, 0.4f);
        }

        private static Vector3 Lift(Vector3 p, float y) => new Vector3(p.x, y, p.z);

        private void PushConfigIfChanged()
        {
            if (!_client.Connected) return;
            if (Mathf.Approximately(injectedLatencyMs, _lastLatency) && Mathf.Approximately(sendRateHz, _lastRate)
                && Mathf.Approximately(jitterMs, _lastJitter) && Mathf.Approximately(lossRate, _lastLoss)) return;
            _lastLatency = injectedLatencyMs; _lastRate = sendRateHz; _lastJitter = jitterMs; _lastLoss = lossRate;
            _client.Send(MsgConfig(sendRateHz, injectedLatencyMs, jitterMs, lossRate));
        }

        private static string F(float v) => v.ToString("0.###", CultureInfo.InvariantCulture);
        private static string V(Vector3 v) => $"{{\"x\":{F(v.x)},\"y\":{F(v.y)},\"z\":{F(v.z)}}}";
        private string MsgMove(long seq, Vector3 pos, Vector3 vel)
            => $"{{\"type\":\"move\",\"seq\":{seq},\"pos\":{V(pos)},\"vel\":{V(vel)}}}";
        private string MsgConfig(float hz, float lat, float jit, float loss)
            => $"{{\"type\":\"config\",\"authority\":\"client\",\"sendRateHz\":{F(hz)},\"latencyMs\":{F(lat)},\"jitterMs\":{F(jit)},\"lossRate\":{F(loss)}}}";
        private string MsgKick(Vector3 pos)
            => $"{{\"type\":\"kick\",\"pos\":{V(pos)}}}";

        // ---- UI ----
        private void OnGUI()
        {
            const int w = 360;
            GUILayout.BeginArea(new Rect(8, 8, w, Screen.height - 16), GUI.skin.box);
            GUILayout.Label("<b>同期ラボ：あなた vs 相手から見えるあなた</b>", Rich());

            GUILayout.BeginHorizontal();
            serverUrl = GUILayout.TextField(serverUrl);
            if (GUILayout.Button("接続", GUILayout.Width(56))) _client.Connect(serverUrl);
            GUILayout.EndHorizontal();
            GUILayout.Label("状態: " + (_client.Connected ? "<color=#6f6>接続中</color>" : "<color=#f66>未接続</color> " + _client.LastError), Rich());

            GUILayout.Space(6);
            GUILayout.Label("<b>● 何を見てるか</b>", Rich());
            GUILayout.Label("<color=#7af>青=あなた（今ここ）</color>\n<color=#7f7>緑=相手の画面に映るあなた</color>\n<color=#fb4>橙=ボール</color> <color=#f77>赤=補間前の生位置</color>", Rich());

            GUILayout.Space(6);
            GUILayout.Label($"<b>遅延注入: {injectedLatencyMs:F0} ms（片道）</b>", Rich());
            injectedLatencyMs = GUILayout.HorizontalSlider(injectedLatencyMs, 0, 300);
            GUILayout.Label($"ジッター: {jitterMs:F0} ms（遅延のばらつき＝到着ムラ）");
            jitterMs = GUILayout.HorizontalSlider(jitterMs, 0, 200);
            GUILayout.Label($"パケットロス: {lossRate * 100:F0} %");
            lossRate = GUILayout.HorizontalSlider(lossRate, 0, 0.3f);
            interpolate = GUILayout.Toggle(interpolate, "相手側が補間する");
            GUILayout.Label($"補間ディレイ: {interpDelayMs:F0} ms");
            interpDelayMs = GUILayout.HorizontalSlider(interpDelayMs, 0, 300);
            extrapolate = GUILayout.Toggle(extrapolate, "外挿（パケット欠け時に先読み）");
            GUILayout.Label($"送信レート: {sendRateHz:F0} Hz");
            sendRateHz = GUILayout.HorizontalSlider(sendRateHz, 5, 60);

            GUILayout.Space(6);
            GUILayout.Label("<b>● ボール（橙）の見せ方</b>", Rich());
            GUILayout.BeginHorizontal();
            if (GUILayout.Toggle(ballMode == BallMode.Raw, "生")) ballMode = BallMode.Raw;
            if (GUILayout.Toggle(ballMode == BallMode.Interp, "補間(過去)")) ballMode = BallMode.Interp;
            if (GUILayout.Toggle(ballMode == BallMode.Extrap, "外挿(未来)")) ballMode = BallMode.Extrap;
            GUILayout.EndHorizontal();
            GUILayout.Label("ボールに近づいて <b>Space</b> で蹴る", Rich());

            GUILayout.Space(8);
            GUILayout.Label("<b>● 今おきていること</b>", Rich());
            GUILayout.Label(Explain(), Rich());

            GUILayout.Space(8);
            GUILayout.Label("<b>● 試してみて</b>", Rich());
            GUILayout.Label("WASD/矢印で青を動かす。Spaceでボールを蹴る。\n" +
                            "・遅延を上げる → 緑(あなたの分身)が遅れる\n" +
                            "<b>ボール実験(遅延を150+に):</b>\n" +
                            "・橙を「補間」に → ボールが赤(生)より遅れる＝過去。当てに行くと外す\n" +
                            "・橙を「外挿」に → ボールが今ある所に近い＝当てやすい\n" +
                            "・緑(あなた=過去)と橙(外挿=未来)の<b>時間軸のズレ</b>も見える", Rich());

            GUILayout.EndArea();
        }

        // 現在の設定で何が起きているかを日本語で説明
        private string Explain()
        {
            float gap = injectedLatencyMs + (interpolate ? interpDelayMs : 0);
            string s;
            if (!interpolate)
            {
                s = $"<color=#fc8>補間OFF：</color>緑は届いた最新位置にパッと飛ぶ＝<color=#fc8>カクカク</color>。\n" +
                    $"あなたの動きは約 {injectedLatencyMs:F0}ms 遅れて相手に届く。";
            }
            else
            {
                s = $"<color=#8f8>補間ON：</color>緑は過去 {interpDelayMs:F0}ms を描くので<color=#8f8>滑らか</color>。\n" +
                    $"代わりに相手にはあなたが約 <b>{gap:F0}ms 過去</b>に見える（遅延{injectedLatencyMs:F0}+ディレイ{interpDelayMs:F0}）。";
            }
            if (jitterMs > 1f || lossRate > 0.001f)
            {
                s += $"\n<color=#fcc>悪条件：ジッター{jitterMs:F0}ms / ロス{lossRate * 100:F0}%。</color>";
                if (interpDelayMs < jitterMs + 50f)
                    s += " <color=#f88>補間ディレイがジッターに足りず、緑がガタつく/飛ぶ。</color>";
                else
                    s += " <color=#8f8>補間ディレイがジッターを吸収して滑らか。これがディレイの仕事。</color>";
            }
            return s;
        }

        private static GUIStyle _rich;
        private static GUIStyle Rich()
        {
            if (_rich == null) _rich = new GUIStyle(GUI.skin.label) { richText = true, wordWrap = true };
            return _rich;
        }

        [Serializable] public class SV3 { public float x, y, z; public Vector3 V() => new Vector3(x, y, z); }
        [Serializable]
        public class SMsg
        {
            public string type;
            public long t;
            public long seq;
            public SV3 pos = new SV3();
            public float rotY;
            public SV3 vel = new SV3();
        }
    }
}
