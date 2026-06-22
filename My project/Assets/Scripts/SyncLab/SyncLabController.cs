using System;
using System.Collections.Generic;
using System.Globalization;
using UnityEngine;

namespace SyncLab
{
    /// <summary>
    /// マルチプレイ同期ラボ。複数クライアントが同じサーバーに繋ぎ、同じボールを共有する。
    ///
    ///   青   = あなた（操作した瞬間に動く）
    ///   緑   = 他プレイヤー（受信位置を補間して描画）
    ///   橙/色 = ボール（所有者の色になる）。Spaceで蹴ると所有権を奪える
    ///
    /// サーバーが「位置の中継」「ボールの所有権裁定」をするので、ここで初めてサーバーが
    /// 仲介者として意味を持つ。遅延/ジッター/ロスは個別に注入できる。
    /// </summary>
    public class SyncLabController : MonoBehaviour
    {
        [Header("接続")]
        public string serverUrl = "ws://localhost:8090";

        [Header("ネットワーク注入")]
        public float injectedLatencyMs = 100f;
        public float jitterMs = 0f;
        public float lossRate = 0f;
        public float sendRateHz = 20f;

        [Header("他プレイヤーの見せ方")]
        public bool interpolate = true;
        public float interpDelayMs = 100f;
        public bool extrapolate = false;

        [Header("ボール")]
        public BallMode ballMode = BallMode.Extrap;
        public enum BallMode { Raw, Interp, Extrap }

        private const float Speed = 6f;

        private SyncClient _client;
        private string _myId = "";
        private Transform _you, _ball, _ballGhost;
        private Material _ballMat;

        private Vector3 _intendedPos;
        private double _interpClockMs;

        // 他プレイヤー
        private class Remote { public Transform tf; public readonly SnapshotBuffer buf = new SnapshotBuffer(); public Vector3 raw; }
        private readonly Dictionary<string, Remote> _remotes = new Dictionary<string, Remote>();
        private double _newestRemoteT;

        // ボール
        private readonly SnapshotBuffer _ballBuf = new SnapshotBuffer();
        private Vector3 _rawBallPos, _rawBallVel;
        private float _lastBallRecvTime;
        private string _ballOwner = "";

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
            _ball = MakeSphere("ボール", new Color(1f, 0.6f, 0.1f), 0.7f, out _ballMat);
            _ballGhost = MakeSphere("ボール生位置(赤)", new Color(1f, 0.3f, 0.3f), 0.4f, out _);

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
            var col = go.GetComponent<Collider>(); if (col != null) Destroy(col);
            return go.transform;
        }

        private Transform MakeSphere(string name, Color c, float scale, out Material mat)
        {
            var go = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            go.name = name;
            go.transform.localScale = Vector3.one * scale;
            mat = Mat(c);
            go.GetComponent<Renderer>().sharedMaterial = mat;
            var col = go.GetComponent<Collider>(); if (col != null) Destroy(col);
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

            // 入力 → あなた（即）
            float ix = Input.GetAxisRaw("Horizontal");
            float iz = Input.GetAxisRaw("Vertical");
            var dir = new Vector3(ix, 0, iz);
            if (dir.sqrMagnitude > 1f) dir.Normalize();
            _intendedPos += dir * Speed * dt;

            if (_client.Connected && Input.GetKeyDown(KeyCode.Space))
                _client.Send(MsgKick(_intendedPos));

            // 送信（位置）
            _sendAccum += dt;
            float interval = 1f / Mathf.Max(1f, sendRateHz);
            if (_client.Connected && _sendAccum >= interval)
            {
                _sendAccum = 0;
                _client.Send(MsgMove(_seq, _intendedPos, dir * Speed));
                _seq++;
            }

            // 受信
            while (_client.TryReceive(out var json))
                HandleMessage(json);

            // 補間クロック（最新サーバー時刻 - ディレイ に追従）
            _interpClockMs += dt * 1000.0;
            double newest = Math.Max(_newestRemoteT, _ballBuf.NewestT);
            if (newest > 0)
            {
                double target = newest - interpDelayMs;
                _interpClockMs += (target - _interpClockMs) * 0.1;
            }

            // 表示：あなた
            _you.position = Lift(_intendedPos, 1f);

            // 表示：他プレイヤー
            foreach (var r in _remotes.Values)
            {
                Vector3 p = interpolate ? r.buf.Sample(_interpClockMs, extrapolate) : r.raw;
                r.tf.position = Lift(p, 1f);
            }

            // 表示：ボール
            Vector3 ballShow;
            switch (ballMode)
            {
                case BallMode.Interp: ballShow = _ballBuf.Sample(_interpClockMs, false); break;
                case BallMode.Extrap: ballShow = _rawBallPos + _rawBallVel * (Time.time - _lastBallRecvTime); break;
                default: ballShow = _rawBallPos; break;
            }
            _ball.position = Lift(ballShow, 0.7f);
            _ballGhost.position = Lift(_rawBallPos, 0.4f);
            UpdateBallColor();
        }

        private void HandleMessage(string json)
        {
            SMsg m;
            try { m = JsonUtility.FromJson<SMsg>(json); } catch { return; }
            if (m == null || string.IsNullOrEmpty(m.type)) return;

            switch (m.type)
            {
                case "welcome":
                    _myId = m.id;
                    break;

                case "player.state":
                    {
                        if (!_remotes.TryGetValue(m.id, out var r))
                        {
                            r = new Remote { tf = MakeCapsule("他プレイヤー " + m.id, new Color(0.25f, 0.85f, 0.4f)) };
                            _remotes[m.id] = r;
                        }
                        r.raw = m.pos.V();
                        r.buf.Add(m.t, m.pos.V(), m.vel.V());
                        if (m.t > _newestRemoteT) _newestRemoteT = m.t;
                    }
                    break;

                case "player.left":
                    if (_remotes.TryGetValue(m.id, out var gone))
                    {
                        Destroy(gone.tf.gameObject);
                        _remotes.Remove(m.id);
                    }
                    break;

                case "ball.state":
                    _rawBallPos = m.pos.V();
                    _rawBallVel = m.vel.V();
                    _ballBuf.Add(m.t, m.pos.V(), m.vel.V());
                    _lastBallRecvTime = Time.time;
                    _ballOwner = m.owner ?? "";
                    break;
            }
        }

        private void UpdateBallColor()
        {
            Color c;
            if (string.IsNullOrEmpty(_ballOwner)) c = new Color(0.6f, 0.6f, 0.6f);      // 誰も触ってない=灰
            else if (_ballOwner == _myId) c = new Color(0.25f, 0.55f, 1f);              // 自分=青
            else c = new Color(1f, 0.6f, 0.1f);                                          // 他人=橙
            if (_ballMat.HasProperty("_BaseColor")) _ballMat.SetColor("_BaseColor", c);
            if (_ballMat.HasProperty("_Color")) _ballMat.SetColor("_Color", c);
            _ballMat.color = c;
        }

        private static Vector3 Lift(Vector3 p, float y) => new Vector3(p.x, y, p.z);

        private void PushConfigIfChanged()
        {
            if (!_client.Connected) return;
            if (Mathf.Approximately(injectedLatencyMs, _lastLatency) && Mathf.Approximately(sendRateHz, _lastRate)
                && Mathf.Approximately(jitterMs, _lastJitter) && Mathf.Approximately(lossRate, _lastLoss)) return;
            _lastLatency = injectedLatencyMs; _lastRate = sendRateHz; _lastJitter = jitterMs; _lastLoss = lossRate;
            _client.Send(MsgConfig(injectedLatencyMs, jitterMs, lossRate));
        }

        private static string F(float v) => v.ToString("0.###", CultureInfo.InvariantCulture);
        private static string V(Vector3 v) => $"{{\"x\":{F(v.x)},\"y\":{F(v.y)},\"z\":{F(v.z)}}}";
        private string MsgMove(long seq, Vector3 pos, Vector3 vel)
            => $"{{\"type\":\"move\",\"seq\":{seq},\"pos\":{V(pos)},\"vel\":{V(vel)}}}";
        private string MsgConfig(float lat, float jit, float loss)
            => $"{{\"type\":\"config\",\"latencyMs\":{F(lat)},\"jitterMs\":{F(jit)},\"lossRate\":{F(loss)}}}";
        private string MsgKick(Vector3 pos) => $"{{\"type\":\"kick\",\"pos\":{V(pos)}}}";

        // ---- UI ----
        private void OnGUI()
        {
            const int w = 360;
            GUILayout.BeginArea(new Rect(8, 8, w, Screen.height - 16), GUI.skin.box);
            GUILayout.Label("<b>同期ラボ（マルチプレイ・所有権）</b>", Rich());

            GUILayout.BeginHorizontal();
            serverUrl = GUILayout.TextField(serverUrl);
            if (GUILayout.Button("接続", GUILayout.Width(56))) _client.Connect(serverUrl);
            GUILayout.EndHorizontal();
            GUILayout.Label("状態: " + (_client.Connected ? $"<color=#6f6>接続中</color> id={_myId}" : "<color=#f66>未接続</color> " + _client.LastError), Rich());
            GUILayout.Label($"他プレイヤー: {_remotes.Count}人  /  ボール所有者: {OwnerLabel()}", Rich());

            GUILayout.Space(6);
            GUILayout.Label($"遅延注入: {injectedLatencyMs:F0} ms");
            injectedLatencyMs = GUILayout.HorizontalSlider(injectedLatencyMs, 0, 300);
            GUILayout.Label($"ジッター: {jitterMs:F0} ms");
            jitterMs = GUILayout.HorizontalSlider(jitterMs, 0, 200);
            GUILayout.Label($"パケットロス: {lossRate * 100:F0} %");
            lossRate = GUILayout.HorizontalSlider(lossRate, 0, 0.3f);
            GUILayout.Label($"送信レート: {sendRateHz:F0} Hz");
            sendRateHz = GUILayout.HorizontalSlider(sendRateHz, 5, 60);

            GUILayout.Space(6);
            GUILayout.Label("<b>他プレイヤー(緑)の見せ方</b>", Rich());
            interpolate = GUILayout.Toggle(interpolate, "補間");
            GUILayout.Label($"補間ディレイ: {interpDelayMs:F0} ms");
            interpDelayMs = GUILayout.HorizontalSlider(interpDelayMs, 0, 300);
            extrapolate = GUILayout.Toggle(extrapolate, "外挿");

            GUILayout.Space(6);
            GUILayout.Label("<b>ボールの見せ方</b>", Rich());
            GUILayout.BeginHorizontal();
            if (GUILayout.Toggle(ballMode == BallMode.Raw, "生")) ballMode = BallMode.Raw;
            if (GUILayout.Toggle(ballMode == BallMode.Interp, "補間")) ballMode = BallMode.Interp;
            if (GUILayout.Toggle(ballMode == BallMode.Extrap, "外挿")) ballMode = BallMode.Extrap;
            GUILayout.EndHorizontal();

            GUILayout.Space(6);
            GUILayout.Label("<color=#7af>青=あなた</color> <color=#7f7>緑=他人</color>  ボール色=所有者(灰なし/青自分/橙他人)", Rich());
            GUILayout.Label("WASD/矢印=移動、<b>Space=蹴る(=所有権を奪う)</b>", Rich());
            GUILayout.Label("2人目: ターミナルで <b>go run ./cmd/synclab-bot</b>\nまたは ParrelSync で2エディタ", Rich());

            GUILayout.EndArea();
        }

        private string OwnerLabel()
        {
            if (string.IsNullOrEmpty(_ballOwner)) return "なし";
            return _ballOwner == _myId ? "あなた" : _ballOwner;
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
            public string id;
            public long t;
            public SV3 pos = new SV3();
            public float rotY;
            public SV3 vel = new SV3();
            public string owner;
        }
    }
}
