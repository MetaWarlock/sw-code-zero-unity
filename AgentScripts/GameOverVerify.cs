using System.IO;
using System.Text;
using UnityEngine;
using Darkwing.Core;

/// <summary>
/// Headless check of the $D46C GAME OVER outro and its two $9900 branches, against the
/// original-ROM captures gold_gameover (card), gold_gameover_continue (A) and
/// gold_gameover_select (B).
///
/// Entry: GameOverVerify.Run
/// </summary>
public static class GameOverVerify
{
    private const string Root = "D:/Art/SW Code Zero Unity";
    private const string RefDir = Root + "/Research/DarkwingDuck/reference";
    private const int CardFrame = 300;   // the frame every golden pair agrees on

    private static NesButtons Pad(byte bits) => new NesButtons
    {
        A = (bits & 0x80) != 0, B = (bits & 0x40) != 0,
        Select = (bits & 0x20) != 0, Start = (bits & 0x10) != 0,
        Up = (bits & 0x08) != 0, Down = (bits & 0x04) != 0,
        Left = (bits & 0x02) != 0, Right = (bits & 0x01) != 0,
    };

    public static string Run()
    {
        var sb = new StringBuilder();
        var data = DataLoader.Load(
            File.ReadAllBytes(Root + "/Assets/Darkwing/Data/chr_full.bytes"),
            File.ReadAllBytes(Root + "/Assets/Darkwing/Data/levelsets.bytes"),
            File.ReadAllBytes(Root + "/Assets/Darkwing/Data/screens.bytes"),
            File.ReadAllBytes(Root + "/Assets/Darkwing/Data/levels.bytes"),
            File.ReadAllBytes(Root + "/Assets/Darkwing/Data/anims.bytes"));
        var decoder = new RoomDecoder(
            File.ReadAllBytes(Root + "/Assets/Darkwing/Data/prg_bank0.bytes"),
            File.ReadAllBytes(Root + "/Assets/Darkwing/Data/prg_bank1.bytes"),
            File.ReadAllBytes(Root + "/Assets/Darkwing/Data/world_bank7.bytes"),
            File.ReadAllBytes(Root + "/Assets/Darkwing/Data/levelsets.bytes"));

        var game = new DarkwingGame(data);
        game.Bank5 = File.ReadAllBytes(Root + "/Assets/Darkwing/Data/prg_bank5.bytes");
        game.Bank7 = File.ReadAllBytes(Root + "/Assets/Darkwing/Data/world_bank7.bytes");
        game.Bank3 = File.ReadAllBytes(Root + "/Assets/Darkwing/Data/audio_bank3.bytes");
        game.Bank6 = File.ReadAllBytes(Root + "/Assets/Darkwing/Data/prg_bank6.bytes");
        game.Bank2 = File.ReadAllBytes(Root + "/Assets/Darkwing/Data/prg_bank2.bytes");
        game.ObjectTypeTable = File.ReadAllBytes(Root + "/Assets/Darkwing/Data/object_types.bytes");
        game.MapNametable = File.ReadAllBytes(Root + "/Assets/Darkwing/Data/map_nametable.bytes");
        game.MapPalette = File.ReadAllBytes(Root + "/Assets/Darkwing/Data/map_palette.bytes");
        game.MapChrBank0 = WorldState.MapChrBank;
        game.MapChrBank1 = WorldState.MapChrBank;
        game.Ppu.WriteRegister(0x2000, 0x16);
        game.LoadWorld(decoder, 0);
        sb.AppendLine($"bank2 = {game.Bank2.Length} B, records @ ${WorldState.GameOverRecords:X4}, " +
                      $"palette @ ${WorldState.GameOverPalette:X4}");

        // --- boot to the mission: title → map → bubble 0 → briefing → A ---
        var flow = new GameFlow(game, decoder);
        StepFrames(game, flow, 1, Pad(0x10));
        StepFrames(game, flow, 200, Pad(0));                   // $C5F7 intro dialog, then the map
        StepFrames(game, flow, 1, Pad(0x40));                        // B: confirm bubble 0
        StepFrames(game, flow, GameFlow.PromptFrames + 5, Pad(0));
        StepFrames(game, flow, 1, Pad(0x80));                        // A: enter the world
        StepFrames(game, flow, 30, Pad(0));
        sb.AppendLine($"in mission: stage={flow.Stage} $31={flow.World.CurrentWorld} " +
                      $"$64={game.Player.DeathFlag:X2} WorldRunning={game.WorldRunning}");

        // --- the golden's seed: $50=07 with both HUD energy columns empty ---
        // gold_gameover/patch_report.txt: --patch-frame 200 --patch 050=07
        // --patch 05E6=70 --patch 05E4=70. $CB27 then INCs $64 and $D41D takes the outro.
        game.Player.State = 0x07;
        game.Player.EnergyColLeft = 0x70;
        game.Player.EnergyColRight = 0x70;
        game.Player.DeathFlag = 0;
        game.Player.CheckpointId = 0;
        game.Player.DeathService();
        sb.AppendLine($"DeathService: $64={game.Player.DeathFlag:X2} GameOverRequest={game.Player.GameOverRequest}");
        StepFrames(game, flow, 1, Pad(0));
        sb.AppendLine($"stage={flow.Stage} WorldRunning={game.WorldRunning} " +
                      $"ctrl=$ {game.Ppu.Ctrl:X2} mask=$ {game.Ppu.Mask:X2} " +
                      $"chr0={game.Ppu.Chr0Bank} chr1={game.Ppu.Chr1Bank}");

        // --- card content vs gold_gameover/vram_frames.bin + palette_frames.bin f300 ---
        var vram = File.ReadAllBytes(RefDir + "/gold_gameover/vram_frames.bin");
        const int stride = 4 + 0x1000;
        int rec = CardFrame / 60 - 1;                          // records start at f60
        int goldFrame = vram[rec * stride] | (vram[rec * stride + 1] << 8) |
                        (vram[rec * stride + 2] << 16) | (vram[rec * stride + 3] << 24);
        int off = rec * stride + 4;
        sb.AppendLine($"golden vram record {rec} holds frame {goldFrame} (expected {CardFrame})");

        int diffs = 0, firstDiff = -1;
        for (int i = 0; i < 0x400; i++)
        {
            if (game.Ppu.NametableRam[i] == vram[off + i]) continue;
            if (firstDiff < 0) firstDiff = i;
            diffs++;
        }
        sb.AppendLine("nametable $2000-$23FF vs golden: " + diffs + " mismatched bytes" +
            (diffs == 0 ? " - EXACT" : ", first at $" + (0x2000 + firstDiff).ToString("X4")));
        if (diffs != 0)
        {
            int shown = 0;
            for (int i = 0; i < 0x400 && shown < 12; i++)
                if (game.Ppu.NametableRam[i] != vram[off + i])
                {
                    sb.AppendLine($"  ${0x2000 + i:X4}: port=$ {game.Ppu.NametableRam[i]:X2} " +
                                  $"gold=$ {vram[off + i]:X2}");
                    shown++;
                }
        }
        int goldTail = 0;
        for (int i = 0x400; i < 0x1000; i++) if (vram[off + i] != 0) goldTail++;
        sb.AppendLine($"golden beyond the card window ($2400-$2FFF): {goldTail} non-zero bytes " +
                      "(the ROM's $C1BA only clears $2000-$23FF, so leftovers live there)");

        var pal = File.ReadAllBytes(RefDir + "/gold_gameover/palette_frames.bin");
        int p = CardFrame * 32;
        int pdiffs = 0;
        for (int i = 0; i < 16; i++)
            if (game.Ppu.PaletteRam[i] != pal[p + i]) pdiffs++;
        sb.AppendLine($"BG palette vs golden f{CardFrame}: {pdiffs} mismatched of 16; " +
                      "port=" + Hex(game.Ppu.PaletteRam, 0, 16) +
                      " gold=" + Hex(pal, p, 16));

        // --- expected text, straight from the ROM records ---
        for (int row = 0; row < 15; row++)
            sb.AppendLine($"  row {row,2}: {TextRow(game, row)}");
        DumpPng(game, Root + "/Temp/gameover.png", sb);

        // --- A = $D4E9 CONTINUE: reload world $31, the mission, $38 untouched ---
        int countBefore = flow.World.CompletedCount;
        byte worldBefore = flow.World.CurrentWorld;
        StepFrames(game, flow, 1, Pad(0x80));
        StepFrames(game, flow, 3, Pad(0));
        sb.AppendLine($"A branch: stage={flow.Stage} $31={flow.World.CurrentWorld} " +
                      $"(was {worldBefore}) $38={flow.World.CompletedCount} (was {countBefore}) " +
                      $"$F8=$ {flow.World.LastOutcome:X2} $64={game.Player.DeathFlag:X2} " +
                      $"WorldRunning={game.WorldRunning}");

        // --- B = $D4B3 STAGE SELECT: city map, $62=07, $38 NOT incremented ---
        var flow2 = new GameFlow(game, decoder);
        StepFrames(game, flow2, 1, Pad(0x10));
        StepFrames(game, flow2, 200, Pad(0));                      // $C5F7 intro dialog, then the map
        StepFrames(game, flow2, 1, Pad(0x40));
        StepFrames(game, flow2, GameFlow.PromptFrames + 5, Pad(0));
        StepFrames(game, flow2, 1, Pad(0x80));
        StepFrames(game, flow2, 20, Pad(0));
        game.Player.State = 0x07;
        game.Player.EnergyColLeft = 0x70;
        game.Player.EnergyColRight = 0x70;
        game.Player.DeathFlag = 0;
        game.Player.CheckpointId = 0;
        game.Player.DeathService();
        StepFrames(game, flow2, 1, Pad(0));
        int before = flow2.World.CompletedCount;
        StepFrames(game, flow2, 1, Pad(0x40));                        // B → $998C → $D4B3
        StepFrames(game, flow2, 3, Pad(0));
        sb.AppendLine($"B branch: stage={flow2.Stage} $F8=$ {flow2.World.LastOutcome:X2} " +
                      $"$62={flow2.World.StreamId:X2} $38={flow2.World.CompletedCount} (was {before}) " +
                      $"WorldRunning={game.WorldRunning} ctrl=$ {game.Ppu.Ctrl:X2}");
        DumpPng(game, Root + "/Temp/gameover_select.png", sb);
        return sb.ToString();
    }

    private static string Hex(byte[] data, int offset, int count)
    {
        var s = new StringBuilder();
        for (int i = 0; i < count; i++)
        {
            if (i > 0) s.Append(' ');
            s.Append(data[offset + i].ToString("x2"));
        }
        return s.ToString();
    }

    private static string TextRow(DarkwingGame game, int row)
    {
        var s = new StringBuilder();
        for (int col = 0; col < 32; col++)
        {
            byte t = game.Ppu.NametableRam[row * 32 + col];
            s.Append(t == 0x03 ? ' ' : (t >= 0xDA && t <= 0xFF) ? (char)('A' + (t - 0xDA)) : '.');
        }
        return s.ToString().TrimEnd();
    }

    private static void StepFrames(DarkwingGame game, GameFlow flow, int frames, NesButtons pad)
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
