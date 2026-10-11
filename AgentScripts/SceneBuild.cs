using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

public static class SceneBuild
{
    public static int BuildMain()
    {
        var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);

        var camGo = new GameObject("Main Camera");
        var cam = camGo.AddComponent<Camera>();
        cam.tag = "MainCamera";
        cam.orthographic = true;
        cam.orthographicSize = 3.5f; // 240px / 16 PPU / 2 = 7.5? see scale note below
        cam.transform.position = new Vector3(0, 0, -10);
        cam.backgroundColor = new Color(0.05f, 0.05f, 0.05f, 1f);
        cam.clearFlags = CameraClearFlags.SolidColor;

        // Quad showing the Core framebuffer.
        var quadGo = GameObject.CreatePrimitive(PrimitiveType.Quad);
        quadGo.name = "Screen";
        quadGo.transform.position = Vector3.zero;
        var mr = quadGo.GetComponent<MeshRenderer>();
        var shader = Shader.Find("Universal Render Pipeline/Unlit");
        mr.sharedMaterial = new Material(shader) { mainTexture = null };

        quadGo.AddComponent<Darkwing.UnityApp.DarkwingLauncher>();

        EditorSceneManager.SaveScene(scene, "Assets/Scenes/DarkwingMain.unity");
        AssetDatabase.SaveAssets();
        return 0;
    }
}
