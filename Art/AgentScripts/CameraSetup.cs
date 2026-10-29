using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

public static class CameraSetup
{
    public static int Configure()
    {
        AssetDatabase.Refresh();
        var scene = EditorSceneManager.OpenScene("Packages/su.learn.codezero/Scenes/DarkwingMain.unity");

        var quad = GameObject.Find("Screen");
        if (quad == null) { Debug.LogError("Screen quad missing"); return 1; }
        // 1 world unit = 16 px (PPU 16). 256x240 px -> 16 x 15 units, centered at origin.
        quad.transform.localScale = new Vector3(16f, 15f, 1f);
        var mr = quad.GetComponent<MeshRenderer>();
        mr.sharedMaterial = new Material(Shader.Find("Universal Render Pipeline/Unlit"));

        var camGo = GameObject.Find("Main Camera");
        var cam = camGo.GetComponent<Camera>();
        cam.transform.position = new Vector3(0f, 0f, -10f);
        cam.orthographicSize = 7.5f;   // 15 units / 2
        cam.orthographic = true;
        cam.nearClipPlane = 0.3f;
        cam.farClipPlane = 20f;
        cam.clearFlags = CameraClearFlags.SolidColor;
        cam.backgroundColor = new Color(0f, 0f, 0f, 1f);
        cam.cullingMatrix = cam.cullingMatrix; // no-op, keep default

        // URP Pixel Perfect Camera: integer scaling, no cropping.
        var ppcType = System.Type.GetType(
            "UnityEngine.Rendering.Universal.PixelPerfectCamera, Unity.RenderPipelines.Universal.Runtime");
        if (ppcType != null)
        {
            var ppc = camGo.GetComponent(ppcType);
            if (ppc == null) ppc = camGo.AddComponent(ppcType);
            var so = new SerializedObject(ppc);
            so.FindProperty("m_RefResolutionX").intValue = 256;
            so.FindProperty("m_RefResolutionY").intValue = 240;
            so.FindProperty("m_PixelsPerUnit").intValue = 16;
            so.FindProperty("m_CropFrame").enumValueIndex = 0; // None
            so.FindProperty("m_GridSnapping").enumValueIndex = 0;
            so.FindProperty("m_UseMSAA").boolValue = false;
            so.ApplyModifiedProperties();
        }
        else
        {
            Debug.LogWarning("PixelPerfectCamera type not found");
        }

        EditorSceneManager.SaveOpenScenes();
        AssetDatabase.SaveAssets();
        return 0;
    }
}
