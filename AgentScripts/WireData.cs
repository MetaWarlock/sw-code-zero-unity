using UnityEditor;
using UnityEngine;

public static class WireData
{
    public static int Assign()
    {
        AssetDatabase.Refresh();
        var launcher = Object.FindAnyObjectByType<Darkwing.UnityApp.DarkwingLauncher>();
        if (launcher == null) { Debug.LogError("launcher not found"); return 1; }
        launcher.ChrData = AssetDatabase.LoadAssetAtPath<TextAsset>("Assets/Darkwing/Data/chr_full.bytes");
        launcher.LevelSetsData = AssetDatabase.LoadAssetAtPath<TextAsset>("Assets/Darkwing/Data/levelsets.bytes");
        launcher.ScreensData = AssetDatabase.LoadAssetAtPath<TextAsset>("Assets/Darkwing/Data/screens.bytes");
        launcher.LevelsData = AssetDatabase.LoadAssetAtPath<TextAsset>("Assets/Darkwing/Data/levels.bytes");
        launcher.AnimsData = AssetDatabase.LoadAssetAtPath<TextAsset>("Assets/Darkwing/Data/anims.bytes");
        EditorUtility.SetDirty(launcher);
        var path = "Assets/Scenes/DarkwingMain.unity";
        EditorSceneManager_Save(path);
        return 0;
    }

    private static void EditorSceneManager_Save(string path)
    {
        UnityEditor.SceneManagement.EditorSceneManager.SaveOpenScenes();
    }
}
