using System.IO;
using UnityEditor;
using UnityEngine;

namespace YubiBench.Editor
{
    /// <summary>
    /// 操作用 BenchScene を iOS（シミュレータSDK）向けに Xcode プロジェクトとして書き出す
    /// バッチビルド用スクリプト。シミュレータSDKなので Apple Developer 署名は不要。
    ///
    /// CLI:
    ///   Unity -batchmode -quit -projectPath "My project" \
    ///     -executeMethod YubiBench.Editor.BenchBuild.BuildIOSSimulator -logFile build.log
    ///
    /// 実機向けは Unity の File > Build Settings から iOS を選んでビルド（署名は各自のApple ID）。
    /// </summary>
    public static class BenchBuild
    {
        private const string OutputDir = "build/ios";

        public static void BuildIOSSimulator()
        {
            // 操作用シーンを（再）生成
            BenchSceneBuilder.BuildInteractiveScene();

            // iOS シミュレータSDK 設定（署名不要）
            PlayerSettings.iOS.sdkVersion = iOSSdkVersion.SimulatorSDK;
            PlayerSettings.iOS.targetOSVersionString = "15.0";
            PlayerSettings.SetApplicationIdentifier(BuildTargetGroup.iOS, "com.rearctech.yubibench");
            PlayerSettings.SetScriptingBackend(BuildTargetGroup.iOS, ScriptingImplementation.IL2CPP);
            PlayerSettings.SetArchitecture(BuildTargetGroup.iOS, 1); // 1 = ARM64

            EditorUserBuildSettings.SwitchActiveBuildTarget(BuildTargetGroup.iOS, BuildTarget.iOS);

            var options = new BuildPlayerOptions
            {
                scenes = new[] { BenchSceneBuilder.ScenePath },
                locationPathName = OutputDir,
                target = BuildTarget.iOS,
                options = BuildOptions.None,
            };

            var report = BuildPipeline.BuildPlayer(options);
            var summary = report.summary;
            Debug.Log($"[BenchBuild] result={summary.result} size={summary.totalSize} " +
                      $"time={summary.totalTime} out={Path.GetFullPath(OutputDir)}");

            if (summary.result != UnityEditor.Build.Reporting.BuildResult.Succeeded)
                EditorApplication.Exit(1);
        }
    }
}
