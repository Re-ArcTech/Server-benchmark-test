using System;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace YubiBench.Editor
{
    /// <summary>
    /// ベンチ計測用シーンを生成し、iOS（シミュレータSDK）向けに Xcode プロジェクトを書き出す
    /// バッチビルド用スクリプト。シミュレータSDKなので Apple Developer 署名は不要。
    ///
    /// 使い方（CLI）:
    ///   Unity -batchmode -quit -projectPath "My project" \
    ///     -executeMethod YubiBench.Editor.BenchBuild.BuildIOSSimulator \
    ///     -benchUrl http://localhost:8080 -logFile build.log
    /// </summary>
    public static class BenchBuild
    {
        private const string ScenePath = "Assets/Scenes/BenchScene.unity";
        private const string OutputDir = "build/ios";

        public static void BuildIOSSimulator()
        {
            string url = GetArg("-benchUrl") ?? "http://localhost:8080";
            Debug.Log($"[BenchBuild] benchUrl = {url}");

            CreateBenchScene(url);

            // iOS シミュレータSDK 設定（署名不要）
            PlayerSettings.iOS.sdkVersion = iOSSdkVersion.SimulatorSDK;
            PlayerSettings.iOS.targetOSVersionString = "15.0";
            PlayerSettings.SetApplicationIdentifier(BuildTargetGroup.iOS, "com.rearctech.yubibench");
            PlayerSettings.SetScriptingBackend(BuildTargetGroup.iOS, ScriptingImplementation.IL2CPP);
            PlayerSettings.SetArchitecture(BuildTargetGroup.iOS, 1); // 1 = ARM64

            EditorUserBuildSettings.SwitchActiveBuildTarget(BuildTargetGroup.iOS, BuildTarget.iOS);

            var options = new BuildPlayerOptions
            {
                scenes = new[] { ScenePath },
                locationPathName = OutputDir,
                target = BuildTarget.iOS,
                options = BuildOptions.None,
            };

            var report = BuildPipeline.BuildPlayer(options);
            var summary = report.summary;
            Debug.Log($"[BenchBuild] result={summary.result} " +
                      $"size={summary.totalSize} time={summary.totalTime} " +
                      $"out={Path.GetFullPath(OutputDir)}");

            if (summary.result != UnityEditor.Build.Reporting.BuildResult.Succeeded)
            {
                EditorApplication.Exit(1);
            }
        }

        /// <summary>BenchmarkRunner + Camera を持つ計測シーンを生成して保存する。</summary>
        private static void CreateBenchScene(string url)
        {
            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);

            var camGo = new GameObject("Main Camera");
            var cam = camGo.AddComponent<Camera>();
            cam.clearFlags = CameraClearFlags.SolidColor;
            cam.backgroundColor = new Color(0.1f, 0.12f, 0.15f);
            camGo.tag = "MainCamera";

            var go = new GameObject("BenchmarkRunner");
            var runner = go.AddComponent<BenchmarkRunner>();
            runner.serverBaseUrl = url;
            runner.runOnStart = true;
            runner.quitAfterRun = true;
            // シミュレータ計測は試行回数を控えめに
            runner.pingCount = 30;
            runner.moveCount = 50;
            runner.kickCount = 20;
            runner.goalCount = 20;
            runner.enableRest = true;
            runner.enableWebSocket = true;
            runner.enableWebRtc = false;

            Directory.CreateDirectory("Assets/Scenes");
            EditorSceneManager.SaveScene(scene, ScenePath);

            EditorBuildSettings.scenes = new[]
            {
                new EditorBuildSettingsScene(ScenePath, true)
            };
            Debug.Log("[BenchBuild] BenchScene 作成完了");
        }

        private static string GetArg(string name)
        {
            var args = Environment.GetCommandLineArgs();
            for (int i = 0; i < args.Length - 1; i++)
                if (args[i] == name)
                    return args[i + 1];
            return null;
        }
    }
}
