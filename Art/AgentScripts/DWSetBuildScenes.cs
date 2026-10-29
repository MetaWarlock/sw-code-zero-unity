using System.IO;
using UnityEditor;

// One-shot editor setup: make the ported DarkwingMain scene part of the player build.
// Non-destructive — keeps SampleScene as a second entry. Uses only Editor APIs (no YAML edits).
public static class DWSetBuildScenes
{
    public static string Run()
    {
        const string mainPath = "Packages/su.learn.codezero/Scenes/DarkwingMain.unity";
        const string samplePath = "Assets/Scenes/SampleScene.unity";
        var current = new System.Collections.Generic.List<EditorBuildSettingsScene>(EditorBuildSettings.scenes);
        bool has(string p) => current.Exists(s => s.path == p);
        if (!has(mainPath))
            current.Insert(0, new EditorBuildSettingsScene(mainPath, true));
        if (!has(samplePath))
            current.Add(new EditorBuildSettingsScene(samplePath, false));
        EditorBuildSettings.scenes = current.ToArray();
        AssetDatabase.SaveAssets();
        var sb = new System.Text.StringBuilder("build scenes:");
        foreach (var s in EditorBuildSettings.scenes)
            sb.Append($" [{(s.enabled ? "on" : "off")}] {s.path}");
        return sb.ToString();
    }
}
