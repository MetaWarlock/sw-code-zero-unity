using UnityEditor;
using UnityEngine;

public static class BuildPlayer
{
    public static int Win64()
    {
        AssetDatabase.Refresh();
        var scenes = new[] { "Packages/su.learn.codezero/Scenes/DarkwingMain.unity" };
        var report = UnityEditor.BuildPipeline.BuildPlayer(
            scenes, "Builds/DarkwingDuck/DarkwingDuck.exe",
            UnityEditor.BuildTarget.StandaloneWindows64,
            UnityEditor.BuildOptions.None);
        if (report.summary.result != UnityEditor.Build.Reporting.BuildResult.Succeeded)
        {
            Debug.LogError("build failed: " + report.summary.result);
            return 1;
        }
        Debug.Log($"build ok: {report.summary.outputPath} ({report.summary.totalSize} bytes)");
        return 0;
    }
}
