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
                          $"ctrl=$ {game.Ppu.Ctrl:X2} mask=$ {game.Ppu.Mask:X2} " +
                          $"chr=({game.Ppu.Chr0Bank},{game.Ppu.Chr1Bank}) " +
                          $"WorldRunning={game.WorldRunning} frame={game.Frame} " +
                          $"distinct={hist.Count} " +
                          $"player=$ {game.Player.State:X2}/X={game.Player.X:X4}");
            sb.Append("  indices: ");
            foreach (var kv in hist) sb.Append($"{kv.Key:X2}={kv.Value} ");
            sb.Append("| nt[0..7]: ");
            for (int i = 0; i < 8; i++) sb.Append($"{game.Ppu.NametableRam[i]:X2} ");
            var map = launcher.GetType().GetField("MapNametable", flags)?.GetValue(launcher) as byte[]
                      ?? game.GetType().GetField("MapNametable", System.Reflection.BindingFlags.Public |
                             System.Reflection.BindingFlags.Instance)?.GetValue(game) as byte[];
            if (map != null) { sb.Append("| mapNT[0..7]: "); for (int i = 0; i < 8; i++) sb.Append($"{map[i]:X2} "); }
            sb.AppendLine($"-> {path}");
        }

        Shot("before-input", "D:/Art/SW Code Zero Unity/Temp/adv_title.png");
        Run(1, 0x10);                                       // Start → map
        Shot("map-f0", "D:/Art/SW Code Zero Unity/Temp/adv_map_f0.png");
        Run(60, 0);
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

    /// <summary>
    /// Byte-diff the LIVE nametable against the exported map screen: says whether the
    /// map picture is wrong because VRAM got clobbered after LoadMapScreen or not.
    /// </summary>
    public static string MapDiff()
    {
        const string Root = "D:/Art/SW Code Zero Unity";
        var sb = new StringBuilder();
        var launcher = Object.FindAnyObjectByType<Darkwing.UnityApp.DarkwingLauncher>();
        var flags = System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance;
        var game = launcher?.GetType().GetField("_game", flags)?.GetValue(launcher) as Darkwing.Core.DarkwingGame;
        if (game == null) return "no live launcher/_game";

        // Get to the map screen first: the launcher boots at Title and Title is black.
        var flow = launcher.GetType().GetField("_flow", flags)?.GetValue(launcher) as Darkwing.Core.GameFlow;
        if (flow != null && flow.Stage.ToString() == "Title")
        {
            var start = new Darkwing.Core.NesButtons { Start = true };
            flow.Step(in start); game.Step(in start);
            var none = default(Darkwing.Core.NesButtons);
            for (int i = 0; i < 60; i++) { flow.Step(in none); game.Step(in none); }
        }
        sb.AppendLine($"stage={(flow == null ? "n/a" : flow.Stage.ToString())}");

        byte[] want = File.ReadAllBytes(Root + "/Assets/Darkwing/Data/map_nametable.bytes");
        byte[] nt = game.Ppu.NametableRam;
        sb.AppendLine($"stage-check: ctrl=$ {game.Ppu.Ctrl:X2} mask=$ {game.Ppu.Mask:X2} " +
                      $"chr=({game.Ppu.Chr0Bank},{game.Ppu.Chr1Bank}) frame={game.Frame}");
        sb.AppendLine($"map_nametable.bytes={want.Length} B, nt[0..0x800] diff:");
        int bad = 0, first = -1, last = -1;
        var badRows = new System.Collections.Generic.SortedDictionary<int, int>();
        for (int i = 0; i < System.Math.Min(want.Length, 0x800); i++)
        {
            if (want[i] == nt[i]) continue;
            bad++;
            if (first < 0) first = i;
            last = i;
            int row = i / 32;
            badRows[row] = badRows.TryGetValue(row, out int c) ? c + 1 : 1;
        }
        sb.AppendLine($"  mismatches={bad}/{System.Math.Min(want.Length, 0x800)} " +
                      $"first=$ {first:X3} last=$ {last:X3} rows affected={badRows.Count}");
        if (first >= 0)
            sb.AppendLine($"  first: want=$ {want[first]:X2} live=$ {nt[first]:X2} (row {first / 32}, col {first % 32})");
        foreach (var kv in badRows)
            if (kv.Value >= 16) sb.Append($"row{kv.Key}={kv.Value} ");
        sb.AppendLine();
        return sb.ToString();
    }

    /// <summary>
    /// Long live-launcher traversal of mission 1: drives the real flow with a synthetic
    /// route and reports every place forward progress stops, so the next hole is chosen
    /// by evidence instead of guesswork.
    /// frame budget, so the probe is resumable: call it repeatedly and each call
    /// continues the same live mission.
    /// </summary>
    public static string Traverse(int frames = 1500)
    {
        const string Root = "D:/Art/SW Code Zero Unity/Temp";
        var sb = new StringBuilder();
        var launcher = Object.FindAnyObjectByType<Darkwing.UnityApp.DarkwingLauncher>();
        var flags = System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance;
        var game = launcher?.GetType().GetField("_game", flags)?.GetValue(launcher) as Darkwing.Core.DarkwingGame;
        var flow = launcher?.GetType().GetField("_flow", flags)?.GetValue(launcher) as Darkwing.Core.GameFlow;
        if (game == null) return "no live launcher/_game (enter Play first)";

        void Step(byte bits)
        {
            var pad = new Darkwing.Core.NesButtons
            {
                A = (bits & 0x80) != 0, B = (bits & 0x40) != 0, Select = (bits & 0x20) != 0,
                Start = (bits & 0x10) != 0, Up = (bits & 0x08) != 0, Down = (bits & 0x04) != 0,
                Left = (bits & 0x02) != 0, Right = (bits & 0x01) != 0,
            };
            flow?.Step(in pad); game.Step(in pad);
        }

        // Bootstrap: Title → map → bubble 0 → briefing → mission, skipping done stages.
        for (int guard = 0; guard < 8 && flow != null && flow.Stage.ToString() != "Mission"; guard++)
        {
            switch (flow.Stage.ToString())
            {
                case "Title": Step(0x10); for (int i = 0; i < 60; i++) Step(0); break;
                case "MapCursor": Step(0x40); for (int i = 0; i < 60; i++) Step(0); break;
                case "MapBriefing":
                    for (int i = 0; i < Darkwing.Core.GameFlow.PromptFrames + 5; i++) Step(0);
                    Step(0x80); for (int i = 0; i < 60; i++) Step(0); break;
                default: Step(0); break;
            }
            sb.AppendLine($"bootstrap: stage={flow.Stage} frame={game.Frame}");
        }
        if (flow == null || flow.Stage.ToString() != "Mission") return "could not reach Mission stage";

        int lastX = game.Player.X, stalledSince = 0, maxX = lastX;
        for (int f = 1; f <= frames; f++)
        {
            byte bits = 0x01;
            // The room-0x02 pylon needs a HELD jump: 26 frames of A rise ~52 px onto the
            // pylon top (gold_m1_fulljump); a short tap only hops 14 px and is impassable
            // in the ROM too, so tapping here would measure the input, not the port.
            if ((f / 26) % 5 == 0) bits |= 0x80;
            if (f % 200 < 6) bits |= 0x40;                      // fire/hook probe
            Step(bits);

            if (game.Player.X > maxX) maxX = game.Player.X;
            if (game.Player.X == lastX) { if (stalledSince == 0) stalledSince = f; }
            else stalledSince = 0;
            lastX = game.Player.X;
            if (f % 500 == 0)
                sb.AppendLine($"  f={f} X=$ {game.Player.X:X4} Y=$ {game.Player.Y:X4} state=$ {game.Player.State:X2} " +
                              $"stage={flow.Stage} cleared={game.MissionCleared}");

            bool cleared = game.MissionCleared || flow.Stage.ToString() != "Mission";
            if (stalledSince != 0 && f - stalledSince == 400 || cleared && f > 1)
            {
                string why = cleared ? $"stage->{flow.Stage} cleared={game.MissionCleared}"
                                    : $"STALL x={game.Player.X:X4} for {f - stalledSince}f";
                var shot = $"{Root}/traverse_{(cleared ? "end" : "stall")}_{f}.png";
                launcher.DumpFramebuffer(shot);
                sb.AppendLine($"f={f} {why} state=$ {game.Player.State:X2} Y=$ {game.Player.Y:X4} " +
                              $"wall=$ {game.Player.WallBlocked:X2} sect={game.SectionLoading} " +
                              $"WorldRunning={game.WorldRunning} -> {System.IO.Path.GetFileName(shot)}");
                if (cleared) return sb.ToString();
                stalledSince = 0;
            }
        }
        launcher.DumpFramebuffer($"{Root}/traverse_final.png");
        sb.AppendLine($"end: frame={game.Frame} stage={flow.Stage} X=$ {game.Player.X:X4} maxX=$ {maxX:X4} " +
                      $"Y=$ {game.Player.Y:X4} state=$ {game.Player.State:X2} cleared={game.MissionCleared} " +
                      $"WorldRunning={game.WorldRunning} -> traverse_final.png");
        return sb.ToString();
    }

    /// <summary>
    /// Why does the live route stop at the pylon? Watches the $DAFD hook-column
    /// preconditions ($DB29 ScrollX alignment, $DB31 script nibble) while walking right.
    /// </summary>
    public static string HookProbe(int frames = 900)
    {
        var sb = new StringBuilder();
        var launcher = Object.FindAnyObjectByType<Darkwing.UnityApp.DarkwingLauncher>();
        var flags = System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance;
        var game = launcher?.GetType().GetField("_game", flags)?.GetValue(launcher) as Darkwing.Core.DarkwingGame;
        if (game == null) return "no live launcher/_game (enter Play first)";
        var p = game.Player;

        var seenNibble = new System.Collections.Generic.SortedDictionary<int, int>();
        int aligned = 0, grabWindow = 0, air = 0, maxAirY = p.Y;
        for (int f = 1; f <= frames; f++)
        {
            byte bits = 0x01;
            if (f % 24 < 6) bits |= 0x80;                     // jump higher/more often
            var pad = new Darkwing.Core.NesButtons
            {
                A = (bits & 0x80) != 0, Start = false, Right = true,
            };
            game.Step(in pad);

            int nib = p.CellScriptByte & 0x0F;
            seenNibble[nib] = seenNibble.TryGetValue(nib, out int c) ? c + 1 : 1;
            bool al = (byte)(p.ScrollX & 0xFF) == 0;
            if (al) aligned++;
            if (al && nib == 1) grabWindow++;
            if (p.State == 0x01) { air++; if (p.Y > maxAirY) maxAirY = p.Y; }
            if (f % 100 == 0)
                sb.AppendLine($"  f={f} X=$ {p.X:X4} Y=$ {p.Y:X4} st=$ {p.State:X2} wall=$ {p.WallBlocked:X2} " +
                              $"cell={p.CellIndex} room=$ {p.CellGridByte:X2} script=$ {p.CellScriptByte:X2} " +
                              $"scrollX=$ {p.ScrollX:X4} mode=$ {p.ScrollMode:X2} peg=$ {(p.OnHookTileFlag ? 1 : 0)}");
        }
        sb.Append("script nibbles seen: ");
        foreach (var kv in seenNibble) sb.Append($"n{kv.Key}={kv.Value} ");
        sb.AppendLine();
        sb.AppendLine($"frames={frames} scrollX-aligned($DB29 pass)={aligned} hook-column frames(nibble1+aligned)={grabWindow} " +
                      $"airborne={air} maxY=$ {maxAirY:X4} pegFlagEver={p.OnHookTileFlag}");
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
