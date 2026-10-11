using UnityEditor;
using UnityEngine;

public static class PlayProbe
{
    public static string Capture(string path)
    {
        ScreenCapture.CaptureScreenshot(path);
        return path;
    }
}
