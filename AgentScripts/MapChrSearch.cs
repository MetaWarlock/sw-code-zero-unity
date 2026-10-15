using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEngine;
using Darkwing.Core;

/// <summary>
/// Resolves the city-map CHR bank by evidence instead of guessing: the map nametable
/// and palette were captured from live VRAM (probe_map f30), and probe_flow/final.png is
/// a Mesen screenshot of that same screen. For every 4K CHR bank the port's own Ppu
/// renderer composes the frame and the per-pixel RGB agreement with the screenshot is
/// scored, so the winning bank is the one the original hardware must have had latched.
/// </summary>
public static class MapChrSearch
{
    private const string Root = "D:/Art/SW Code Zero Unity";
    private const string Ref = Root + "/Research/DarkwingDuck/reference/probe_flow/final.png";

    public static string Check()
    {
        byte[] chr = File.ReadAllBytes(Root + "/Assets/Darkwing/Data/chr_full.bytes");
        var data = DataLoader.Load(chr,
            File.ReadAllBytes(Root + "/Assets/Darkwing/Data/levelsets.bytes"),
            File.ReadAllBytes(Root + "/Assets/Darkwing/Data/screens.bytes"),
            File.ReadAllBytes(Root + "/Assets/Darkwing/Data/levels.bytes"),
            File.ReadAllBytes(Root + "/Assets/Darkwing/Data/anims.bytes"));
        var game = new DarkwingGame(data);
        game.Bank7 = File.ReadAllBytes(Root + "/Assets/Darkwing/Data/world_bank7.bytes");
        game.MapNametable = File.ReadAllBytes(Root + "/Assets/Darkwing/Data/map_nametable.bytes");
        game.MapPalette = File.ReadAllBytes(Root + "/Assets/Darkwing/Data/map_palette.bytes");
        game.Ppu.WriteRegister(0x2000, 0x16);          // bg pattern table at $1000
        game.LoadMapScreen();

        var tex = new Texture2D(2, 2, TextureFormat.RGBA32, false);
        tex.LoadImage(File.ReadAllBytes(Ref));
        Color32[] refPx = tex.GetPixels32();          // bottom-up
        int refW = tex.width, refH = tex.height;
        UnityEngine.Object.Destroy(tex);

        var scores = new List<(int bank, int bgHi, int matches)>();
        for (int bgHi = 0; bgHi <= 1; bgHi++)
            for (int bank = 0; bank < chr.Length / 0x1000; bank++)
            {
                if (bgHi == 1) game.Ppu.Chr1Bank = bank; else game.Ppu.Chr0Bank = bank;
                game.Ppu.BgPatternHigh = bgHi == 1;
                game.Ppu.Render(game.Framebuffer);
                scores.Add((bank, bgHi, Score(game.Framebuffer, refPx, refW, refH)));
            }
        scores.Sort((a, b) => b.matches.CompareTo(a.matches));
        var sb = new StringBuilder($"reference {refW}x{refH}, " +
                                   $"{chr.Length / 0x1000} 4K CHR banks x2 pattern tables\n");
        for (int i = 0; i < Math.Min(8, scores.Count); i++)
            sb.AppendLine($"  bg$ {(scores[i].bgHi == 1 ? "1000" : "0000")} bank {scores[i].bank,2}: " +
                          $"{scores[i].matches} / {256 * 240} px match");
        return sb.ToString();
    }

    /// <summary>Exact-RGB agreement; a 224-row screenshot is aligned to the frame's rows 8..231.</summary>
    private static int Score(byte[] fb, Color32[] refPx, int refW, int refH)
    {
        int matches = 0;
        int top = refH == 240 ? 0 : (240 - refH) / 2;
        for (int y = 0; y < refH; y++)
        {
            int srcY = y + top;
            if (srcY >= 240) continue;
            for (int x = 0; x < Math.Min(refW, 256); x++)
            {
                Color32 want = refPx[(refH - 1 - y) * refW + x];
                Color32 got = Darkwing.UnityApp.NesPalette.Table[fb[srcY * 256 + x] & 0x3F];
                if (got.r == want.r && got.g == want.g && got.b == want.b) matches++;
            }
        }
        return matches;
    }
}
