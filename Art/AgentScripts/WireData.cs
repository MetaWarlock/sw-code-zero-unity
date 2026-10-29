using UnityEditor;
using UnityEngine;

public static class WireData
{
    public static int Assign()
    {
        AssetDatabase.Refresh();
        var launcher = Object.FindAnyObjectByType<Darkwing.UnityApp.DarkwingLauncher>();
        if (launcher == null) { Debug.LogError("launcher not found"); return 1; }
        launcher.ChrData = AssetDatabase.LoadAssetAtPath<TextAsset>("Packages/su.learn.codezero/Darkwing/Data/chr_full.bytes");
        launcher.LevelSetsData = AssetDatabase.LoadAssetAtPath<TextAsset>("Packages/su.learn.codezero/Darkwing/Data/levelsets.bytes");
        launcher.ScreensData = AssetDatabase.LoadAssetAtPath<TextAsset>("Packages/su.learn.codezero/Darkwing/Data/screens.bytes");
        launcher.LevelsData = AssetDatabase.LoadAssetAtPath<TextAsset>("Packages/su.learn.codezero/Darkwing/Data/levels.bytes");
        launcher.AnimsData = AssetDatabase.LoadAssetAtPath<TextAsset>("Packages/su.learn.codezero/Darkwing/Data/anims.bytes");
        launcher.Bank0Data = AssetDatabase.LoadAssetAtPath<TextAsset>("Packages/su.learn.codezero/Darkwing/Data/prg_bank0.bytes");
        launcher.Bank1Data = AssetDatabase.LoadAssetAtPath<TextAsset>("Packages/su.learn.codezero/Darkwing/Data/prg_bank1.bytes");
        launcher.Bank7Data = AssetDatabase.LoadAssetAtPath<TextAsset>("Packages/su.learn.codezero/Darkwing/Data/world_bank7.bytes");
        launcher.Bank5Data = AssetDatabase.LoadAssetAtPath<TextAsset>("Packages/su.learn.codezero/Darkwing/Data/prg_bank5.bytes");
        launcher.Bank3Data = AssetDatabase.LoadAssetAtPath<TextAsset>("Packages/su.learn.codezero/Darkwing/Data/audio_bank3.bytes");
        launcher.Bank6Data = AssetDatabase.LoadAssetAtPath<TextAsset>("Packages/su.learn.codezero/Darkwing/Data/prg_bank6.bytes");
        launcher.Bank2Data = AssetDatabase.LoadAssetAtPath<TextAsset>("Packages/su.learn.codezero/Darkwing/Data/prg_bank2.bytes");
        launcher.Bank4Data = AssetDatabase.LoadAssetAtPath<TextAsset>("Packages/su.learn.codezero/Darkwing/Data/prg_bank4.bytes");
        launcher.ObjectTypesData = AssetDatabase.LoadAssetAtPath<TextAsset>("Packages/su.learn.codezero/Darkwing/Data/object_types.bytes");
        launcher.MapNametableData = AssetDatabase.LoadAssetAtPath<TextAsset>("Packages/su.learn.codezero/Darkwing/Data/map_nametable.bytes");
        launcher.MapPaletteData = AssetDatabase.LoadAssetAtPath<TextAsset>("Packages/su.learn.codezero/Darkwing/Data/map_palette.bytes");
        EditorUtility.SetDirty(launcher);
        var path = "Packages/su.learn.codezero/Scenes/DarkwingMain.unity";
        EditorSceneManager_Save(path);
        return 0;
    }

    private static void EditorSceneManager_Save(string path)
    {
        UnityEditor.SceneManagement.EditorSceneManager.SaveOpenScenes();
    }
}
