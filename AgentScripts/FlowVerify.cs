using System.IO;
using System.Text;
using UnityEngine;
using Darkwing.Core;

/// <summary>
/// M7 flow smoke test (headless, no scene): title → city map → bubble confirm →
/// briefing prompt → mission → $B315 boss defeat → map return with $38/$69 updated.
/// Also renders the city-map screen from the ROM-derived nametable/palette so the
/// descriptor CHR banks for $31=31 can be eyeballed against the original.
/// </summary>
public static class FlowVerify
{
    private const string Root = "D:/Art/SW Code Zero Unity";

    /// <summary>bits are the raw NES pad byte ($14) the port reads: A=$80 B=$40
    /// Select=$20 Start=$10 Up=$08 Down=$04 Left=$02 Right=$01 (NesInput.cs).</summary>
    private static NesButtons Pad(byte bits) => new NesButtons
    {
        A = (bits & 0x80) != 0, B = (bits & 0x40) != 0,
        Select = (bits & 0x20) != 0, Start = (bits & 0x10) != 0,
        Up = (bits & 0x08) != 0, Down = (bits & 0x04) != 0,
        Left = (bits & 0x02) != 0, Right = (bits & 0x01) != 0,
    };

    private static readonly StringBuilder _sb = new StringBuilder();

    /// <summary>The full flow now runs past the pipeline's 30 s main-thread cap, so the report is
    /// flushed to Temp/flow_verify.txt as well: read that file when run_script returns HTTP 400.</summary>
    public static string Check()
    {
        try { Body(); }
        catch (System.Exception e) { _sb.AppendLine("EXCEPTION: " + e); }
        finally { File.WriteAllText(Root + "/Temp/flow_verify.txt", _sb.ToString()); }
        return _sb.ToString();
    }

    private static void Body()
    {
        var sb = _sb;
        var data = DataLoader.Load(
            File.ReadAllBytes(Root + "/Assets/Darkwing/Data/chr_full.bytes"),
            File.ReadAllBytes(Root + "/Assets/Darkwing/Data/levelsets.bytes"),
            File.ReadAllBytes(Root + "/Assets/Darkwing/Data/screens.bytes"),
            File.ReadAllBytes(Root + "/Assets/Darkwing/Data/levels.bytes"),
            File.ReadAllBytes(Root + "/Assets/Darkwing/Data/anims.bytes"));
        byte[] bank7 = File.ReadAllBytes(Root + "/Assets/Darkwing/Data/world_bank7.bytes");
        var decoder = new RoomDecoder(
            File.ReadAllBytes(Root + "/Assets/Darkwing/Data/prg_bank0.bytes"),
            File.ReadAllBytes(Root + "/Assets/Darkwing/Data/prg_bank1.bytes"),
            bank7, File.ReadAllBytes(Root + "/Assets/Darkwing/Data/levelsets.bytes"));

        var game = new DarkwingGame(data);
        game.Bank5 = File.ReadAllBytes(Root + "/Assets/Darkwing/Data/prg_bank5.bytes");
        game.Bank7 = bank7;
        game.Bank3 = File.ReadAllBytes(Root + "/Assets/Darkwing/Data/audio_bank3.bytes");
        game.Bank6 = File.ReadAllBytes(Root + "/Assets/Darkwing/Data/prg_bank6.bytes");
        game.Bank2 = File.ReadAllBytes(Root + "/Assets/Darkwing/Data/prg_bank2.bytes");   // $AA00 dialog
        game.ObjectTypeTable = File.ReadAllBytes(Root + "/Assets/Darkwing/Data/object_types.bytes");
        game.MapNametable = File.ReadAllBytes(Root + "/Assets/Darkwing/Data/map_nametable.bytes");
        game.MapPalette = File.ReadAllBytes(Root + "/Assets/Darkwing/Data/map_palette.bytes");

        game.MapChrBank0 = WorldState.MapChrBank;    // evidence: probe_flow chr($1000)=$26
        game.MapChrBank1 = WorldState.MapChrBank;
        sb.AppendLine($"map CHR bank {WorldState.MapChrBank} (probe_flow latch + MapChrSearch score); " +
                      $"nametable {game.MapNametable.Length} B, palette {game.MapPalette.Length} B");

        game.Ppu.WriteRegister(0x2000, 0x90);   // $FF=$90 in every ram_frames.bin: bg $1000, +1 increment
        game.LoadWorld(decoder, 0);             // the harness preloads a world; the flow freezes it
        var flow = new GameFlow(game, decoder);
        sb.AppendLine($"after ctor: stage={flow.Stage} WorldRunning={game.WorldRunning} " +
                      $"mask=$ {game.Ppu.Mask:X2} (title black)");

        // --- Title → Intro ($C5F7 dialog) → MapCursor (Start $10) ---
        Run(game, flow, 1, Pad(0x10));
        sb.AppendLine($"after Start: stage={flow.Stage} $31={flow.World.CurrentWorld:X2} " +
                      $"stream=${(game.Text != null ? game.Text.StreamId : -1):X2}");
        UntilMap(game, flow, sb);
        sb.AppendLine($"stage={flow.Stage} $38={flow.World.CompletedCount} WorldRunning={game.WorldRunning} " +
                      $"mask=$ {game.Ppu.Mask:X2}");
        DumpPng(game, Root + "/Temp/flow_map.png", sb);

        // --- bubble cursor: Right ($01) twice, confirm with B ($40) ---
        Run(game, flow, 1, Pad(0x01)); Run(game, flow, 2, Pad(0));
        Run(game, flow, 1, Pad(0x01)); Run(game, flow, 2, Pad(0));
        sb.AppendLine($"cursor after 2x Right = {flow.World.CursorBubble} -> mission {flow.MissionForBubble(flow.World.CursorBubble)}");
        Run(game, flow, 1, Pad(0x40));          // B: $A452 AND #$0F = 0 → confirm
        sb.AppendLine($"stage={flow.Stage} $31={flow.World.CurrentWorld} $62={flow.World.StreamId}");

        // --- briefing prompt: wait out the card, then A ($80) = enter the world ---
        Run(game, flow, GameFlow.PromptFrames + 5, Pad(0));
        Run(game, flow, 1, Pad(0x80));          // A
        sb.AppendLine($"stage={flow.Stage} WorldRunning={game.WorldRunning} $F8=$ {flow.World.LastOutcome:X2} " +
                      $"playerState=$ {game.Player.State:X2} X=$ {game.Player.X:X4}");
        Run(game, flow, 120, Pad(0x01));        // walk right inside the mission
        sb.AppendLine($"120 mission frames ok: state=$ {game.Player.State:X2} X=$ {game.Player.X:X4} " +
                      $"Y=$ {game.Player.Y:X4} frame={game.Frame}");
        Run(game, flow, 240, Pad(0x01));        // the $09 entry fall must land, then walking starts
        sb.AppendLine($"360 mission frames: state=$ {game.Player.State:X2} X=$ {game.Player.X:X4} " +
                      $"Y=$ {game.Player.Y:X4} $51={game.Player.StateTimer:X2}");

        // --- $B315: the boss's 16th damage point kills it and raises the event ---
        ClearMission(game);
        sb.AppendLine($"BossDefeat fired: MissionCleared={game.MissionCleared} bossBehavior=$ {game.Objects.Slots[3].Behavior:X2}");
        Run(game, flow, 2, Pad(0));
        sb.AppendLine($"stage={flow.Stage} $38={flow.World.CompletedCount} $69=$ {flow.World.CompletedMask:X2} " +
                      $"WorldRunning={game.WorldRunning}");

        // --- the $A4B0 rule: with $38=1 bubble 0 is still mission 0 ---
        sb.AppendLine($"MissionForBubble(0) at $38={flow.World.CompletedCount} -> {flow.MissionForBubble(0)}");

        // --- the NATURAL seven-win transition. Clearing the mask by hand proves nothing about the
        // branch, and $D838 gates on $31, not on $69: the bubbles run 0,1,2 (missions 0,1,2), then
        // 3,4,5, and at $38=6 $A46E/$A48B freeze the cursor on bubble 0, which $A4EB maps to
        // mission 6. That seventh clear must take BCS $D841 = Phase.Ending, and $69 bit 6 stays
        // clear because the $A5AB write lives in the $D8D1 map-return tail the ending path skips.
        var sweep = new GameFlow(game, decoder);
        Run(game, sweep, 1, Pad(0x10));
        UntilMap(game, sweep, null);
        for (int i = 0; i < 7; i++)
        {
            int bubble = i % 3;
            while (sweep.Stage == GameFlow.Phase.MapCursor && sweep.World.CursorBubble != bubble)
            { Run(game, sweep, 1, Pad(0x01)); Run(game, sweep, 2, Pad(0)); }   // $A46E Right, edge-per-press
            if (sweep.Stage != GameFlow.Phase.MapCursor) { sb.AppendLine($"win {i + 1}: map not reached, stage={sweep.Stage}"); break; }
            Run(game, sweep, 1, Pad(0x40));                        // confirm the bubble under $6C
            int cleared = sweep.World.CurrentWorld;
            Run(game, sweep, GameFlow.PromptFrames + 5, Pad(0));   // $C970 fade + $9900 settle
            Run(game, sweep, 1, Pad(0x80));                        // A → $995E flight → $D4E9 load
            if (sweep.Stage != GameFlow.Phase.Mission) { sb.AppendLine($"win {i + 1}: enter gave {sweep.Stage}"); break; }
            ClearMission(game);
            Run(game, sweep, 2, Pad(0));
            sb.AppendLine($"win {i + 1}: cleared $31={cleared} -> stage={sweep.Stage} $38={sweep.World.CompletedCount} $69=${sweep.World.CompletedMask:X2}");
        }
        sb.AppendLine($"NATURAL ENDING TRANSITION: reached={sweep.Stage == GameFlow.Phase.Ending} " +
                      $"stage={sweep.Stage} $38={sweep.World.CompletedCount} $69=${sweep.World.CompletedMask:X2}");

        // --- G67: run the ending body itself ($D841-$D8D0). There is no ending capture, so the
        // acceptance is behavioural: all three blocks execute, each $AA00 dialog terminates, the
        // two $FEFF counts ($1E=30 at $D89D, $F0=240 at $9282) elapse, every $8000 palette blit
        // reaches $3F00, and $D8CD's $65 gets its #$FF.
        int endFrames = 0, lastEndStage = -1;
        while (sweep.Stage == GameFlow.Phase.Ending && sweep.EndingStage < 6 && endFrames < 6000)
        {
            Run(game, sweep, 1, Pad(0));
            endFrames++;
            if (sweep.EndingStage != lastEndStage)
            {
                lastEndStage = sweep.EndingStage;
                sb.AppendLine($"ending -> stage {sweep.EndingStage} at f{endFrames} (delay={sweep.EndingDelay}) " +
                              $"$62={sweep.World.StreamId:X2} $31={sweep.World.CurrentWorld:X2} " +
                              $"mask=$ {game.Ppu.Mask:X2} chr=${game.Ppu.Chr0Bank:X2}/${game.Ppu.Chr1Bank:X2} " +
                              $"pal=${game.Ppu.PaletteRam[1]:X2},{game.Ppu.PaletteRam[2]:X2},{game.Ppu.PaletteRam[3]:X2}" +
                              $"/{game.Ppu.PaletteRam[0x11]:X2},{game.Ppu.PaletteRam[0x12]:X2},{game.Ppu.PaletteRam[0x13]:X2} " +
                              $"ntTiles={game.Ppu.NametableRam[0]}/{game.Ppu.NametableRam[0x200]} " +
                              $"slots=[{Anim(game)}]");
            }
        }
        sb.AppendLine($"ENDING BODY: stage={sweep.EndingStage} (6 = past the $D8D0 RTS) frames={endFrames} " +
                      $"$65=${sweep.World.GameCompletedFlag:X2} WorldRunning={game.WorldRunning} flowStage={sweep.Stage}");
        DumpPng(game, Root + "/Temp/flow_ending.png", sb);

        // --- the B branch of the prompt ($998C) goes back to the map without counting ---
        flow.World.CompletedMask = 0x01;
        game.LoadWorld(decoder, 1);
        var flow2 = new GameFlow(game, decoder);
        Run(game, flow2, 1, Pad(0x10));                       // Start -> $C5F7 intro
        UntilMap(game, flow2, sb);
        Run(game, flow2, 1, Pad(0x40));                       // B -> confirm bubble 0
        Run(game, flow2, GameFlow.PromptFrames + 5, Pad(0));
        Run(game, flow2, 1, Pad(0x40));                       // B -> $F8=$FF, back to map
        sb.AppendLine($"B branch: stage={flow2.Stage} $F8=$ {flow2.World.LastOutcome:X2} " +
                      $"$38={flow2.World.CompletedCount} WorldRunning={game.WorldRunning}");
    }

    /// <summary>Ending-slot digest: <c>slot: anim($0420)@($0450,$0440)</c> for slots 1-5, so a
    /// block's $FA6C seeds are readable in the log without a full object-RAM dump.</summary>
    private static string Anim(DarkwingGame game)
    {
        var s = new StringBuilder();
        for (int i = 1; i <= 5; i++)
        {
            var slot = game.Objects.Slots[i];
            if (i > 1) s.Append(' ');
            s.Append($"{i}:$ {slot.AnimNumber:X2}@({slot.ScreenX:X2},{slot.ScreenY:X2})" +
                     $"{(slot.Active ? "+" : "-")}");
        }
        return s.ToString();
    }

    /// <summary>$B315: the boss's 16th damage point kills it, which raises
    /// <c>Enemies.BossDefeated</c> and so <c>DarkwingGame.MissionCleared</c> — the same event the
    /// world engine sets when the real boss dies.</summary>
    private static void ClearMission(DarkwingGame game)
    {
        var boss = game.Objects.Slots[3];
        boss.Active = true; boss.Behavior = 0x62; boss.Health = 0x80;
        BossBehaviors.DamageAccum = 0x10; BossBehaviors.InvulnTimer = 16;
        BossBehaviors.HitFlash(game.Enemies, boss);
    }

    /// <summary>$C5F7 plays two $AA00 dialog streams ($0E then $0F, $C612 delays 30 frames
    /// between them) before the map is built. Steps until the flow leaves Phase.Intro and
    /// reports the nametable text held on the last Intro frame — the ROM leaves stream $0F's
    /// three lines on screen ($AADD only clears when the NEXT stream starts).</summary>
    /// <returns>frames spent inside Phase.Intro</returns>
    private static int UntilMap(DarkwingGame game, GameFlow flow, StringBuilder sb)
    {
        var pad = Pad(0);
        string last = null;
        int f = 0;
        for (; f < 600 && flow.Stage == GameFlow.Phase.Intro; f++)
        {
            var t = game.Text;
            if (t != null)
                last = $"$ {t.StreamId:X2}: \"{t.LineText(0, 29)}\" | \"{t.LineText(1, 29)}\" | \"{t.LineText(2, 29)}\"";
            flow.Step(in pad);
            game.Step(in pad);
        }
        sb?.AppendLine($"intro: {f} frames, exits to {flow.Stage}, last frame drew {last ?? "(no bank 2)"}");
        return f;
    }

    private static void Run(DarkwingGame game, GameFlow flow, int frames, NesButtons pad)
    {
        for (int i = 0; i < frames; i++)
        {
            flow.Step(in pad);
            game.Step(in pad);
        }
    }

    private static void DumpPng(DarkwingGame game, string path, StringBuilder sb)
    {
        var tex = new Texture2D(DarkwingGame.ScreenWidth, DarkwingGame.ScreenHeight,
            TextureFormat.RGBA32, false);
        var px = new Color32[DarkwingGame.ScreenWidth * DarkwingGame.ScreenHeight];
        var fb = game.Framebuffer;
        for (int y = 0; y < DarkwingGame.ScreenHeight; y++)
        {
            int src = y * DarkwingGame.ScreenWidth;
            int dst = (DarkwingGame.ScreenHeight - 1 - y) * DarkwingGame.ScreenWidth;
            for (int x = 0; x < DarkwingGame.ScreenWidth; x++)
                px[dst + x] = Darkwing.UnityApp.NesPalette.Table[fb[src + x] & 0x3F];
        }
        tex.SetPixels32(px);
        tex.Apply(false);
        File.WriteAllBytes(path, ImageConversion.EncodeToPNG(tex));
        Object.Destroy(tex);
        int nonBg = 0;
        for (int i = 0; i < fb.Length; i++) if (fb[i] != game.Ppu.PaletteRam[0]) nonBg++;
        sb.AppendLine($"dumped {path}: {nonBg} non-background pixels");
    }
}
