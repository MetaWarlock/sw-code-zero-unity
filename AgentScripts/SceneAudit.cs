using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using System.IO;
using System.Text;

public static class SceneAudit
{
    static string DetectPipeline()
    {
        var asset=UnityEngine.Rendering.GraphicsSettings.currentRenderPipeline;
        if (asset==null) return "BuiltIn";
        string name=asset.GetType().FullName;
        return name.Contains("Universal") ? "URP" : name.Contains("HighDefinition") ? "HDRP" : "Custom";
    }
    public static string Inspect()
    {
        const string path = "Assets/Scenes/DarkwingMain.unity";
        var scene = SceneManager.GetSceneByPath(path);
        bool opened = !scene.isLoaded;
        if (opened) scene = EditorSceneManager.OpenScene(path, OpenSceneMode.Additive);
        var sb = new StringBuilder($"scene={scene.path} dirty={scene.isDirty}\n");
        var pipeline = UnityEngine.Rendering.GraphicsSettings.currentRenderPipeline;
        sb.AppendLine("pipeline=" + DetectPipeline() + " asset=" + (pipeline == null ? "none" : pipeline.GetType().FullName));
        try
        {
            foreach (var root in scene.GetRootGameObjects())
            {
                foreach (var launcher in root.GetComponentsInChildren<Darkwing.UnityApp.DarkwingLauncher>(true))
                {
                    sb.AppendLine($"launcher={launcher.name} UseFlow={launcher.UseFlow} WorldId={launcher.WorldId}");
                    foreach (var field in launcher.GetType().GetFields())
                        if (field.FieldType == typeof(TextAsset))
                            sb.AppendLine(field.Name + "=" + AssetDatabase.GetAssetPath((TextAsset)field.GetValue(launcher)));
                }
                foreach (var camera in root.GetComponentsInChildren<Camera>(true))
                {
                    var extra = camera.GetComponent<UnityEngine.Rendering.Universal.UniversalAdditionalCameraData>();
                    sb.AppendLine($"camera={camera.name} HDR={camera.allowHDR} MSAA={camera.allowMSAA} dynamicResolution={camera.allowDynamicResolution} qualityAA={QualitySettings.antiAliasing} postProcessing={(extra != null && extra.renderPostProcessing)}");
                }
            }
        }
        finally { if (opened) EditorSceneManager.CloseScene(scene, true); }
        File.WriteAllText("D:/Art/SW Code Zero Unity/Temp/scene_audit.txt", sb.ToString());
        return sb.ToString();
    }
}
