using System.IO;
using System.Text;
using UnityEditor;
using UnityEngine;

/// <summary>
/// Display-wiring probe: asks the LIVE launcher for the Core framebuffer it just
/// composited, then separately reports what the cameras would draw. That splits a
/// black Game-view capture into "the Core produced black" vs "capture/display hole".
/// </summary>
public static class ScreenProbe
{
    public static string Run(
        string path = "D:/Art/SW Code Zero Unity/Temp/screen_probe.png")
    {
        var sb = new StringBuilder();
        sb.AppendLine($"playMode={EditorApplication.isPlaying}");

        var launcher = Object.FindFirstObjectByType<Darkwing.UnityApp.DarkwingLauncher>();
        if (launcher == null)
        {
            sb.AppendLine("launcher: NOT IN LOADED SCENE (open Assets/Scenes/DarkwingMain.unity and enter Play)");
            return sb.ToString();
        }

        // 1. The Core's own framebuffer, with the launcher's palette conversion.
        File.WriteAllBytes(path, Encode(launcher, out int nonBlack, out int distinct));
        sb.AppendLine($"core framebuffer: non-black={nonBlack}/{256 * 240} distinct colours={distinct}");
        sb.AppendLine($"png: {path} ({new FileInfo(path).Length} B)");

        // 2. What the display side is configured to draw.
        foreach (var cam in Object.FindObjectsByType<Camera>(FindObjectsSortMode.None))
        {
            sb.AppendLine($"camera \"{cam.name}\": active={cam.gameObject.activeInHierarchy} " +
                          $"enabled={cam.enabled} ortho={cam.orthographic} size={cam.orthographicSize:F2} " +
                          $"rect={cam.rect} cullingMask={cam.cullingMask} bg={cam.backgroundColor} " +
                          $"targetTex={(cam.targetTexture == null ? "null(screen)" : cam.targetTexture.name)} " +
                          $"aspect={cam.aspect:F3} depth={cam.depth}");
        }

        foreach (var r in Object.FindObjectsByType<Renderer>(FindObjectsSortMode.None))
        {
            var b = r.bounds;
            var tex = r.sharedMaterial != null ? r.sharedMaterial.mainTexture : null;
            sb.AppendLine($"renderer \"{r.name}\": active={r.gameObject.activeInHierarchy} " +
                          $"layer={r.gameObject.layer} bounds=({b.center.x:F2},{b.center.y:F2},{b.center.z:F2}) " +
                          $"size=({b.size.x:F2},{b.size.y:F2}) mat=\"{r.sharedMaterial?.name}\" " +
                          $"tex={(tex == null ? "NONE" : $"{tex.name} {tex.width}x{tex.height}")}");
        }
        return sb.ToString();
    }

    /// <summary>
    /// Why is the Core's framebuffer empty in Play mode? Reports the PPU mask, the
    /// raw palette-index histogram, and the flow/game phase the live launcher is in.
    /// </summary>
    public static string Deep()
    {
        var sb = new StringBuilder();
        var launcher = Object.FindAnyObjectByType<Darkwing.UnityApp.DarkwingLauncher>();
        if (launcher == null) return "launcher not found";
        var t = launcher.GetType();
        object Get(string f) => t.GetField(f, System.Reflection.BindingFlags.NonPublic |
                                             System.Reflection.BindingFlags.Instance)?.GetValue(launcher);
        var game = Get("_game") as Darkwing.Core.DarkwingGame;
        var flow = Get("_flow") as Darkwing.Core.GameFlow;
        if (game == null) return "_game is null (Awake never completed?)";

        var fb = game.Framebuffer;
        var hist = new System.Collections.Generic.SortedDictionary<int, int>();
        for (int i = 0; i < fb.Length; i++) hist[fb[i]] = hist.TryGetValue(fb[i], out int v) ? v + 1 : 1;
        sb.AppendLine($"frame={game.Frame} WorldRunning={game.WorldRunning} stage={(flow == null ? "no-flow" : flow.Stage.ToString())}");
        sb.AppendLine($"PPU mask=$ {game.Ppu.Mask:X2} " +
                      $"scroll=({game.Ppu.ScrollX},{game.Ppu.ScrollY}) pal[0]=$ {game.Ppu.PaletteRam[0]:X2}");
        sb.Append("fb histogram (index=count): ");
        foreach (var kv in hist) sb.Append($"{kv.Key:X2}={kv.Value} ");
        sb.AppendLine();
        sb.AppendLine($"player state=$ {game.Player.State:X2} X=$ {game.Player.X:X4} Y=$ {game.Player.Y:X4}");
        sb.AppendLine($"useFlow={Get("_flow") != null} screen={(Get("_screen") as Texture2D)?.width}x{(Get("_screen") as Texture2D)?.height}");
        return sb.ToString();
    }

    /// <summary>
    /// Drives the LIVE launcher through title → map → mission by calling the flow's
    /// own Step with synthetic pads, dumping each phase. Proves the whole display
    /// chain without depending on keyboard focus or the Game tab.
    /// </summary>
    public static string Advance()
    {
        var sb = new StringBuilder();
        var launcher = Object.FindAnyObjectByType<Darkwing.UnityApp.DarkwingLauncher>();
        var flags = System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance;
        var game = launcher?.GetType().GetField("_game", flags)?.GetValue(launcher) as Darkwing.Core.DarkwingGame;
        var flow = launcher?.GetType().GetField("_flow", flags)?.GetValue(launcher) as Darkwing.Core.GameFlow;
        if (game == null) return "no live launcher/_game (enter Play first)";

        void Run(int frames, byte bits)
        {
            var pad = new Darkwing.Core.NesButtons
            {
                A = (bits & 0x80) != 0, B = (bits & 0x40) != 0, Select = (bits & 0x20) != 0,
                Start = (bits & 0x10) != 0, Up = (bits & 0x08) != 0, Down = (bits & 0x04) != 0,
                Left = (bits & 0x02) != 0, Right = (bits & 0x01) != 0,
            };
            for (int i = 0; i < frames; i++) { flow?.Step(in pad); game.Step(in pad); }
        }

        void Shot(string label, string path)
        {
            launcher.DumpFramebuffer(path);
            var fb = game.Framebuffer;
            var hist = new System.Collections.Generic.SortedDictionary<int, int>();
            for (int i = 0; i < fb.Length; i++) hist[fb[i]] = hist.TryGetValue(fb[i], out int v) ? v + 1 : 1;
            sb.AppendLine($"{label}: stage={(flow == null ? "n/a" : flow.Stage.ToString())} " +
                          $"mask=$ {game.Ppu.Mask:X2} WorldRunning={game.WorldRunning} frame={game.Frame} " +
                          $"distinct={hist.Count} " +
                          $"player=$ {game.Player.State:X2}/X={game.Player.X:X4}");
            sb.Append("  indices: ");
            foreach (var kv in hist) sb.Append($"{kv.Key:X2}={kv.Value} ");
            sb.AppendLine($"-> {path}");
        }

        Shot("before-input", "D:/Art/SW Code Zero Unity/Temp/adv_title.png");
        Run(1, 0x10); Run(60, 0);                       // Start → map
        Shot("after-Start", "D:/Art/SW Code Zero Unity/Temp/adv_map.png");
        Run(1, 0x40); Run(30, 0);                       // B → confirm bubble 0
        Shot("after-B", "D:/Art/SW Code Zero Unity/Temp/adv_briefing.png");
        Run(Darkwing.Core.GameFlow.PromptFrames + 5, 0);
        Run(1, 0x80);                                   // A → enter mission 1
        Shot("after-A", "D:/Art/SW Code Zero Unity/Temp/adv_enter.png");
        Run(240, 0x01);                                 // hold Right through the entry fall
        Shot("in-mission", "D:/Art/SW Code Zero Unity/Temp/adv_mission.png");
        return sb.ToString();
    }

    /// <summary>Encodes the launcher's DumpFramebuffer output and measures it.</summary>
    private static byte[] Encode(Darkwing.UnityApp.DarkwingLauncher launcher, string path)
    {
        launcher.DumpFramebuffer(path);
        return File.ReadAllBytes(path);
    }

    private static byte[] Encode(Darkwing.UnityApp.DarkwingLauncher launcher,
                                 out int nonBlack, out int distinct)
    {
        const string tmp = "D:/Art/SW Code Zero Unity/Temp/screen_probe_raw.png";
        Encode(launcher, tmp);
        var png = File.ReadAllBytes(tmp);

        var tex = new Texture2D(2, 2, TextureFormat.RGBA32, false);
        tex.LoadImage(png);
        var px = tex.GetPixels32();
        var seen = new System.Collections.Generic.HashSet<Color32>();
        nonBlack = 0;
        for (int i = 0; i < px.Length; i++)
        {
            if (px[i].r != 0 || px[i].g != 0 || px[i].b != 0) nonBlack++;
            seen.Add(px[i]);
        }
        distinct = seen.Count;
        Object.DestroyImmediate(tex);
        return png;
    }
}
