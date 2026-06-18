using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace SyncLab.EditorTools
{
    /// <summary>
    /// 同期ラボのシーンを生成する。SyncLabController が Awake で床・カメラ・キャラ等を
    /// 全部コードで作るので、シーンには空の SyncLab オブジェクトを置くだけでよい。
    /// メニュー「YubiBench/Build SyncLab Scene」または CLI から実行。
    /// </summary>
    public static class SyncLabSceneBuilder
    {
        public const string ScenePath = "Assets/Scenes/SyncLab.unity";

        [MenuItem("YubiBench/Build SyncLab Scene")]
        public static void Build()
        {
            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            var go = new GameObject("SyncLab");
            go.AddComponent<SyncLab.SyncLabController>();

            Directory.CreateDirectory("Assets/Scenes");
            EditorSceneManager.SaveScene(scene, ScenePath);
            Debug.Log("[SyncLab] scene built: " + ScenePath);
        }
    }
}
