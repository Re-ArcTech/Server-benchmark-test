using UnityEngine;
using UnityEngine.UI;

namespace YubiBench
{
    /// <summary>
    /// ベンチ計測画面の操作UI。シーン上の uGUI 要素を <see cref="BenchmarkRunner"/> に橋渡しする。
    /// 参照はシーン（BenchSceneBuilder）で割り当て済み。
    /// </summary>
    public class BenchmarkUI : MonoBehaviour
    {
        public BenchmarkRunner runner;

        [Header("UI参照")]
        public InputField urlField;
        public Toggle restToggle;
        public Toggle wsToggle;
        public Toggle rtcToggle;
        public Button runButton;
        public Text runButtonLabel;
        public Button clearButton;
        public Text statusText;
        public Text resultsText;

        private void Awake()
        {
            if (runner != null)
            {
                runner.StatusChanged += OnStatus;
                runner.ResultsReady += OnResults;
            }
            if (runButton != null) runButton.onClick.AddListener(OnRunClicked);
            if (clearButton != null) clearButton.onClick.AddListener(OnClearClicked);

            // 初期値をUIに反映
            if (urlField != null && runner != null) urlField.text = runner.serverBaseUrl;
            if (restToggle != null && runner != null) restToggle.isOn = runner.enableRest;
            if (wsToggle != null && runner != null) wsToggle.isOn = runner.enableWebSocket;
            if (rtcToggle != null && runner != null) rtcToggle.isOn = runner.enableWebRtc;

            SetStatus("準備完了。サーバーURLを確認して『計測スタート』");
            if (resultsText != null) resultsText.text = "";
        }

        private void OnDestroy()
        {
            if (runner != null)
            {
                runner.StatusChanged -= OnStatus;
                runner.ResultsReady -= OnResults;
            }
        }

        private void Update()
        {
            // 計測中はボタンを無効化＆ラベル変更
            if (runButton != null && runner != null)
            {
                runButton.interactable = !runner.IsRunning;
                if (runButtonLabel != null)
                    runButtonLabel.text = runner.IsRunning ? "計測中..." : "計測スタート";
            }
        }

        private void OnRunClicked()
        {
            if (runner == null || runner.IsRunning) return;

            if (urlField != null) runner.serverBaseUrl = urlField.text.Trim();
            if (restToggle != null) runner.enableRest = restToggle.isOn;
            if (wsToggle != null) runner.enableWebSocket = wsToggle.isOn;
            if (rtcToggle != null) runner.enableWebRtc = rtcToggle.isOn;

            if (resultsText != null) resultsText.text = "";
            runner.StartBenchmark();
        }

        private void OnClearClicked()
        {
            if (resultsText != null) resultsText.text = "";
            SetStatus("結果をクリアしました");
        }

        private void OnStatus(string s) => SetStatus(s);

        private void OnResults(string text)
        {
            if (resultsText != null) resultsText.text = text;
        }

        private void SetStatus(string s)
        {
            if (statusText != null) statusText.text = "状態: " + s;
        }
    }
}
