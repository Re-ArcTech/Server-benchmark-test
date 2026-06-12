using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace YubiBench.Editor
{
    /// <summary>
    /// 操作用の計測シーン（BenchScene.unity）を uGUI で組み立てて保存するエディタスクリプト。
    /// メニュー「YubiBench/Build Bench Scene」または CLI から実行できる。
    /// 生成物: URL入力欄・方式トグル(REST/WS/WebRTC)・計測ボタン・状態表示・結果表示。
    /// </summary>
    public static class BenchSceneBuilder
    {
        public const string ScenePath = "Assets/Scenes/BenchScene.unity";

        private static readonly Color Bg = new Color(0.10f, 0.12f, 0.16f);
        private static readonly Color Panel = new Color(0.16f, 0.19f, 0.24f);
        private static readonly Color Accent = new Color(0.20f, 0.55f, 0.95f);
        private static readonly Color Accent2 = new Color(0.35f, 0.38f, 0.45f);
        private static readonly Color TextCol = new Color(0.92f, 0.94f, 0.97f);

        [MenuItem("YubiBench/Build Bench Scene")]
        public static void BuildMenu()
        {
            BuildInteractiveScene();
            EditorUtility.DisplayDialog("YubiBench", "BenchScene.unity を生成しました。\n" + ScenePath, "OK");
        }

        public static void BuildInteractiveScene()
        {
            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);

            // Camera（背景色）
            var camGo = new GameObject("Main Camera");
            var cam = camGo.AddComponent<Camera>();
            cam.clearFlags = CameraClearFlags.SolidColor;
            cam.backgroundColor = Bg;
            camGo.tag = "MainCamera";

            // EventSystem
            new GameObject("EventSystem", typeof(EventSystem), typeof(StandaloneInputModule));

            // Canvas
            var canvasGo = new GameObject("Canvas", typeof(Canvas), typeof(CanvasScaler), typeof(GraphicRaycaster));
            var canvas = canvasGo.GetComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            var scaler = canvasGo.GetComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1080, 1920);
            scaler.matchWidthOrHeight = 0.5f;

            // Root panel（縦並びレイアウト）
            var panel = NewUIObject("Panel", canvasGo.transform);
            Stretch(panel.GetComponent<RectTransform>(), 30, 30, 30, 30);
            var panelImg = panel.AddComponent<Image>();
            panelImg.color = Bg;
            var vlg = panel.AddComponent<VerticalLayoutGroup>();
            vlg.padding = new RectOffset(24, 24, 24, 24);
            vlg.spacing = 18;
            vlg.childControlWidth = true;
            vlg.childControlHeight = true;
            vlg.childForceExpandWidth = true;
            vlg.childForceExpandHeight = false;
            vlg.childAlignment = TextAnchor.UpperCenter;

            // タイトル
            var title = MakeText("Title", panel.transform,
                "ゆびサッカー 通信方式ベンチ\nREST / WebSocket / WebRTC", 40, TextAnchor.MiddleCenter);
            title.fontStyle = FontStyle.Bold;
            SetHeight(title.gameObject, 130);

            // URL行
            var urlRow = MakeRow("UrlRow", panel.transform, 90);
            MakeText("UrlLabel", urlRow.transform, "Server", 30, TextAnchor.MiddleLeft).gameObject
                .AddComponent<LayoutElement>().preferredWidth = 150;
            var urlField = MakeInputField("UrlField", urlRow.transform, "http://localhost:8080");
            urlField.GetComponent<LayoutElement>().flexibleWidth = 1;

            // トグル行
            var togRow = MakeRow("ToggleRow", panel.transform, 80);
            var restTog = MakeToggle("RestToggle", togRow.transform, "REST", true);
            var wsTog = MakeToggle("WsToggle", togRow.transform, "WebSocket", true);
            var rtcTog = MakeToggle("RtcToggle", togRow.transform, "WebRTC", false);

            // 計測ボタン
            Text runLabel;
            var runBtn = MakeButton("RunButton", panel.transform, "計測スタート", Accent, 44, out runLabel);
            SetHeight(runBtn.gameObject, 130);

            // クリアボタン
            Text clearLabel;
            var clearBtn = MakeButton("ClearButton", panel.transform, "結果をクリア", Accent2, 32, out clearLabel);
            SetHeight(clearBtn.gameObject, 80);

            // 状態テキスト
            var status = MakeText("StatusText", panel.transform, "状態: 準備中...", 28, TextAnchor.MiddleLeft);
            status.color = new Color(0.7f, 0.85f, 1f);
            SetHeight(status.gameObject, 60);

            // 結果テキスト（残りスペースを埋める）
            var resultsBg = NewUIObject("ResultsBg", panel.transform);
            resultsBg.AddComponent<Image>().color = Panel;
            var resLe = resultsBg.AddComponent<LayoutElement>();
            resLe.flexibleHeight = 1;
            resLe.minHeight = 600;
            var results = MakeText("ResultsText", resultsBg.transform, "", 24, TextAnchor.UpperLeft);
            Stretch(results.GetComponent<RectTransform>(), 16, 16, 16, 16);
            results.horizontalOverflow = HorizontalWrapMode.Wrap;
            results.verticalOverflow = VerticalWrapMode.Overflow;

            // ロジック + UI コンポーネントを作って参照を割り当て
            var logicGo = new GameObject("Benchmark");
            var runner = logicGo.AddComponent<BenchmarkRunner>();
            runner.serverBaseUrl = "http://localhost:8080";
            runner.runOnStart = false;
            runner.quitAfterRun = false;

            var ui = logicGo.AddComponent<BenchmarkUI>();
            ui.runner = runner;
            ui.urlField = urlField;
            ui.restToggle = restTog;
            ui.wsToggle = wsTog;
            ui.rtcToggle = rtcTog;
            ui.runButton = runBtn;
            ui.runButtonLabel = runLabel;
            ui.clearButton = clearBtn;
            ui.statusText = status;
            ui.resultsText = results;

            Directory.CreateDirectory("Assets/Scenes");
            EditorSceneManager.SaveScene(scene, ScenePath);
            EditorBuildSettings.scenes = new[] { new EditorBuildSettingsScene(ScenePath, true) };
            Debug.Log("[BenchSceneBuilder] 生成完了: " + ScenePath);
        }

        // ---------- UI ヘルパー ----------

        private static Font BuiltinFont =>
            Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");

        private static GameObject NewUIObject(string name, Transform parent)
        {
            var go = new GameObject(name, typeof(RectTransform));
            go.transform.SetParent(parent, false);
            return go;
        }

        private static Text MakeText(string name, Transform parent, string content, int size, TextAnchor anchor)
        {
            var go = NewUIObject(name, parent);
            var t = go.AddComponent<Text>();
            t.text = content;
            t.font = BuiltinFont;
            t.fontSize = size;
            t.alignment = anchor;
            t.color = TextCol;
            t.supportRichText = false;
            return t;
        }

        private static GameObject MakeRow(string name, Transform parent, float height)
        {
            var go = NewUIObject(name, parent);
            var h = go.AddComponent<HorizontalLayoutGroup>();
            h.spacing = 16;
            h.childControlWidth = true;
            h.childControlHeight = true;
            h.childForceExpandWidth = false;
            h.childForceExpandHeight = true;
            h.childAlignment = TextAnchor.MiddleLeft;
            SetHeight(go, height);
            return go;
        }

        private static Button MakeButton(string name, Transform parent, string label, Color color, int size, out Text labelText)
        {
            var go = NewUIObject(name, parent);
            var img = go.AddComponent<Image>();
            img.color = color;
            var btn = go.AddComponent<Button>();
            btn.targetGraphic = img;
            labelText = MakeText(name + "Label", go.transform, label, size, TextAnchor.MiddleCenter);
            labelText.fontStyle = FontStyle.Bold;
            Stretch(labelText.GetComponent<RectTransform>(), 0, 0, 0, 0);
            return btn;
        }

        private static InputField MakeInputField(string name, Transform parent, string value)
        {
            var go = NewUIObject(name, parent);
            var img = go.AddComponent<Image>();
            img.color = new Color(0.08f, 0.09f, 0.12f);
            var field = go.AddComponent<InputField>();
            go.AddComponent<LayoutElement>().minWidth = 200;

            var text = MakeText("Text", go.transform, "", 28, TextAnchor.MiddleLeft);
            text.supportRichText = false;
            Stretch(text.GetComponent<RectTransform>(), 14, 14, 6, 6);
            text.color = Color.white;

            var placeholder = MakeText("Placeholder", go.transform, "サーバーURL", 28, TextAnchor.MiddleLeft);
            placeholder.fontStyle = FontStyle.Italic;
            placeholder.color = new Color(0.6f, 0.6f, 0.6f);
            Stretch(placeholder.GetComponent<RectTransform>(), 14, 14, 6, 6);

            field.textComponent = text;
            field.placeholder = placeholder;
            field.text = value;
            field.targetGraphic = img;
            return field;
        }

        private static Toggle MakeToggle(string name, Transform parent, string label, bool isOn)
        {
            var go = NewUIObject(name, parent);
            go.AddComponent<LayoutElement>().flexibleWidth = 1;
            var h = go.AddComponent<HorizontalLayoutGroup>();
            h.spacing = 8;
            h.childControlWidth = false;
            h.childControlHeight = true;
            h.childForceExpandHeight = true;
            h.childAlignment = TextAnchor.MiddleLeft;

            var toggle = go.AddComponent<Toggle>();

            // チェックボックス背景
            var box = NewUIObject("Box", go.transform);
            var boxImg = box.AddComponent<Image>();
            boxImg.color = new Color(0.08f, 0.09f, 0.12f);
            var boxLe = box.AddComponent<LayoutElement>();
            boxLe.preferredWidth = 48; boxLe.preferredHeight = 48;

            // チェックマーク
            var check = NewUIObject("Check", box.transform);
            var checkImg = check.AddComponent<Image>();
            checkImg.color = Accent;
            Stretch(check.GetComponent<RectTransform>(), 8, 8, 8, 8);

            var lbl = MakeText("Label", go.transform, label, 28, TextAnchor.MiddleLeft);
            lbl.gameObject.AddComponent<LayoutElement>().flexibleWidth = 1;

            toggle.targetGraphic = boxImg;
            toggle.graphic = checkImg;
            toggle.isOn = isOn;
            return toggle;
        }

        // ---------- RectTransform ヘルパー ----------

        private static void Stretch(RectTransform rt, float left, float right, float top, float bottom)
        {
            rt.anchorMin = Vector2.zero;
            rt.anchorMax = Vector2.one;
            rt.offsetMin = new Vector2(left, bottom);
            rt.offsetMax = new Vector2(-right, -top);
        }

        private static void SetHeight(GameObject go, float h)
        {
            var le = go.GetComponent<LayoutElement>();
            if (le == null) le = go.AddComponent<LayoutElement>();
            le.minHeight = h;
            le.preferredHeight = h;
        }
    }
}
