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

    public static string Check()
    {
        var sb = new StringBuilder();
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
        game.ObjectTypeTable = File.ReadAllBytes(Root + "/Assets/Darkwing/Data/object_types.bytes");
        game.MapNametable = File.ReadAllBytes(Root + "/Assets/Darkwing/Data/map_nametable.bytes");
        game.MapPalette = File.ReadAllBytes(Root + "/Assets/Darkwing/Data/map_palette.bytes");

        game.MapChrBank0 = WorldState.MapChrBank;    // evidence: probe_flow chr($1000)=$26
        game.MapChrBank1 = WorldState.MapChrBank;
        sb.AppendLine($"map CHR bank {WorldState.MapChrBank} (probe_flow latch + MapChrSearch score); " +
                      $"nametable {game.MapNametable.Length} B, palette {game.MapPalette.Length} B");

        game.Ppu.WriteRegister(0x2000, 0x16);   // bg pattern table at $1000
        game.LoadWorld(decoder, 0);             // the harness preloads a world; the flow freezes it
        var flow = new GameFlow(game, decoder);
        sb.AppendLine($"after ctor: stage={flow.Stage} WorldRunning={game.WorldRunning} " +
                      $"mask=$ {game.Ppu.Mask:X2} (title black)");

        // --- Title → MapCursor (Start $10) ---
        Run(game, flow, 1, Pad(0x10));
        Run(game, flow, 30, Pad(0));
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
        var boss = game.Objects.Slots[3];
        boss.Active = true; boss.Behavior = 0x62; boss.Health = 0x80;
        BossBehaviors.DamageAccum = 0x10; BossBehaviors.InvulnTimer = 16;
        BossBehaviors.HitFlash(game.Enemies, boss);
        sb.AppendLine($"BossDefeat fired: MissionCleared={game.MissionCleared} bossBehavior=$ {boss.Behavior:X2}");
        Run(game, flow, 2, Pad(0));
        sb.AppendLine($"stage={flow.Stage} $38={flow.World.CompletedCount} $69=$ {flow.World.CompletedMask:X2} " +
                      $"WorldRunning={game.WorldRunning}");

        // --- the $A4B0 rule: with $38=1 bubble 0 is still mission 0, and all 7 bits end the game ---
        sb.AppendLine($"MissionForBubble(0) at $38={flow.World.CompletedCount} -> {flow.MissionForBubble(0)}");
        flow.World.CompletedMask = 0x7F;
        sb.AppendLine($"all seven cleared: AllMissionsComplete={flow.AllMissionsComplete}");

        // --- the B branch of the prompt ($998C) goes back to the map without counting ---
        flow.World.CompletedMask = 0x01;
        game.LoadWorld(decoder, 1);
        var flow2 = new GameFlow(game, decoder);
        Run(game, flow2, 1, Pad(0x10));                       // Start -> map
        Run(game, flow2, 2, Pad(0));
        Run(game, flow2, 1, Pad(0x40));                       // B -> confirm bubble 0
        Run(game, flow2, GameFlow.PromptFrames + 5, Pad(0));
        Run(game, flow2, 1, Pad(0x40));                       // B -> $F8=$FF, back to map
        sb.AppendLine($"B branch: stage={flow2.Stage} $F8=$ {flow2.World.LastOutcome:X2} " +
                      $"$38={flow2.World.CompletedCount} WorldRunning={game.WorldRunning}");
        return sb.ToString();
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
