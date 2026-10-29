using System;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

// ZERO-2. One-shot physical layout migration; gameplay bytes and GUIDs are retained.
public static class LayoutMigration
{
    public static string MoveGameFiles()
    {
        if (EditorApplication.isPlayingOrWillChangePlaymode)
            throw new InvalidOperationException("Stop Play Mode before moving source assets.");
        if (!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo())
            throw new InvalidOperationException("Unsaved scene migration canceled.");
        string root = Directory.GetParent(Application.dataPath).FullName;
        string package = Path.Combine(root, "Art/Unity/CodeZero");
        AssetDatabase.DisallowAutoRefresh();
        try
        {
            Move(Path.Combine(root, "Assets/Darkwing"), Path.Combine(package, "Darkwing"));
            Move(Path.Combine(root, "Assets/Darkwing.meta"), Path.Combine(package, "Darkwing.meta"));
            Directory.CreateDirectory(Path.Combine(package, "Scenes"));
            Move(Path.Combine(root, "Assets/Scenes/DarkwingMain.unity"), Path.Combine(package, "Scenes/DarkwingMain.unity"));
            Move(Path.Combine(root, "Assets/Scenes/DarkwingMain.unity.meta"), Path.Combine(package, "Scenes/DarkwingMain.unity.meta"));
        }
        finally { AssetDatabase.AllowAutoRefresh(); }
        return "MOVED; next add local package file:" + package.Replace('\\', '/');
    }

    static void Move(string a, string b)
    {
        if (!File.Exists(a) && !Directory.Exists(a)) return;
        if (File.Exists(b) || Directory.Exists(b)) throw new IOException("Destination exists: " + b);
        if (Directory.Exists(a)) Directory.Move(a, b); else File.Move(a, b);
    }

    public static string Finish()
    {
        AssetDatabase.Refresh(ImportAssetOptions.ForceSynchronousImport);
        const string scene = "Packages/su.learn.codezero/Scenes/DarkwingMain.unity";
        if (AssetDatabase.AssetPathToGUID(scene) != "fb2d4d7901ed4af4f833b8f9f03ee7e1")
            throw new InvalidOperationException("Scene GUID was not preserved.");
        EditorBuildSettings.scenes = EditorBuildSettings.scenes.Select(s =>
            s.guid.ToString() == "fb2d4d7901ed4af4f833b8f9f03ee7e1" || s.path.EndsWith("/DarkwingMain.unity")
            ? new EditorBuildSettingsScene(scene, s.enabled) : s).ToArray();
        EditorSceneManager.OpenScene(scene);
        AssetDatabase.SaveAssets();
        var launcher = UnityEngine.Object.FindFirstObjectByType<Darkwing.UnityApp.DarkwingLauncher>();
        if (launcher == null || launcher.ChrData == null || launcher.Bank7Data == null)
            throw new InvalidOperationException("Missing scene component/data reference.");
        return "PASS scene=" + scene + "; GUID and launcher/data references preserved";
    }

    public static string RestoreInheritedPreloadedAssets()
    {
        // The verified pre-migration working tree had preloadedAssets: [].
        // Unity's Input System adds its generated Actions during a build.
        // Restore the human's original local setting through the Editor API.
        PlayerSettings.SetPreloadedAssets(Array.Empty<UnityEngine.Object>());
        AssetDatabase.SaveAssets();
        return "Restored inherited preloadedAssets=[] through PlayerSettings API; build's automatic addition remains recorded in source_after.json";
    }
}
