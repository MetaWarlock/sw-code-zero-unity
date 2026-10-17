using System.IO;
using System.Text;
using UnityEngine;
using Darkwing.Core;

/// <summary>
/// Frame-exact check of the $C792 title screen against the original-ROM capture gold_title2
/// (Start presses every 200 frames; the window 900-903 opens the title at $C792, so the first
/// visible title frame is ~f918 after $C970's fade + $C1BA clear).
///
/// The golden start frame is FOUND from the RAM signature the $9500 seed writes ($0310 = the
/// $953C literal, $0510=$9B, $04F0=$28, $1A=$9F, $1B=$99) instead of being hard-coded, and the
/// port/golden frame delta is scanned so the alignment is measured, not assumed.
///
/// Entry: TitleVerify.Run
/// </summary>
public static class TitleVerify
{
    private const string Root = "D:/Art/SW Code Zero Unity";
    private const string RefDir = Root + "/Research/DarkwingDuck/reference/gold_title2";
    private const int Window = 60;          // port frames to compare (the title lives ~180)

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
        try { Verify(sb); }
        catch (System.Exception e) { sb.AppendLine("EXCEPTION: " + e); }
        File.WriteAllText(Root + "/Temp/title_verify.txt", sb.ToString());
        return sb.ToString();
    }

    private static void Verify(StringBuilder sb)
    {
        var bank2 = File.ReadAllBytes(Root + "/Assets/Darkwing/Data/prg_bank2.bytes");
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
        game.Bank2 = bank2;
        game.ObjectTypeTable = File.ReadAllBytes(Root + "/Assets/Darkwing/Data/object_types.bytes");
        game.MapNametable = File.ReadAllBytes(Root + "/Assets/Darkwing/Data/map_nametable.bytes");
        game.MapPalette = File.ReadAllBytes(Root + "/Assets/Darkwing/Data/map_palette.bytes");
        game.Ppu.WriteRegister(0x2000, 0x90);      // every ram_frames.bin shows $FF=$90
        game.LoadWorld(decoder, 0);

        var ram = File.ReadAllBytes(RefDir + "/ram_frames.bin");
        var goldOam = File.ReadAllBytes(RefDir + "/oam_frames.bin");
        var goldPal = File.ReadAllBytes(RefDir + "/palette_frames.bin");
        var vram = File.ReadAllBytes(RefDir + "/vram_frames.bin");

        // --- locate the golden title window from the $9500/$9513 RAM signature ---
        int lit = 0x953C - 0x8000;
        int start = -1;
        for (int f = 880; f < 960 && start < 0; f++)
        {
            int r = (f - 1) * 2048;
            if (r + 2048 > ram.Length) break;
            if (ram[r + 0x1A] != 0x9F || ram[r + 0x1B] != 0x99) continue;
            if (ram[r + 0x510] != 0x9B || ram[r + 0x4F0] != 0x28) continue;
            bool pal = true;
            for (int i = 0; i < 16; i++) if (ram[r + 0x310 + i] != bank2[lit + i]) { pal = false; break; }
            if (pal) start = f;
        }
        if (start < 0) { sb.AppendLine("FAIL: no golden frame carries the $9500 title signature"); return; }
        sb.AppendLine($"golden title seed frame = {start} (signature $1A=$9F $1B=$99 $04F0=$28 " +
                      $"$0510=$9B $0310=$953C literal)");
        int r0 = (start - 1) * 2048;
        sb.AppendLine($"golden $0300=${Hex(ram, r0 + 0x300, 16)} $0320=${Hex(ram, r0 + 0x320, 16)}");
        sb.AppendLine($"golden $0310=${Hex(ram, r0 + 0x310, 16)} $0330=${Hex(ram, r0 + 0x330, 16)}");
        sb.AppendLine($"golden $2000=${ram[r0 + 0xFF]:X2} $2001=${ram[r0 + 0xFE]:X2} $97=${ram[r0 + 0x97]}");

        // --- the port's title: GameFlow's ctor calls LoadTitleScreen ($C792-$C7AA) ---
        var flow = new GameFlow(game, decoder);
        sb.AppendLine($"port after ctor: stage={flow.Stage} TitleScreen={game.TitleScreen} " +
                      $"WorldRunning={game.WorldRunning} ctrl=$ {game.Ppu.Ctrl:X2} mask=$ {game.Ppu.Mask:X2} " +
                      $"chr0={game.Ppu.Chr0Bank} chr1={game.Ppu.Chr1Bank}");
        var p0300 = new byte[32]; var p0320 = new byte[32];
        game.GetPaletteModel(p0300, p0320);
        sb.AppendLine($"port   $0300=${Hex(p0300, 0, 16)} $0320=${Hex(p0320, 0, 16)}");
        sb.AppendLine($"port   $0310=${Hex(p0300, 16, 16)} $0330=${Hex(p0320, 16, 16)}");

        // LoadWorld's $DD57/$C270 tail (EntryFreezeFrames = 17) is a harness artifact: $C792's
        // title setup never goes through the world loader, so those 17 Steps returned before
        // $C21E/$8000 and left Ppu.Oam empty while the window kept counting. The title's frame 1
        // is the first $C7AD walk.
        game.Player.EntryFreezeFrames = 0;

        // step the window and stash each frame's published OAM + $3F00 + the walk clocks
        var pad = Pad(0);
        var portOam = new byte[Window][];
        var portPal = new byte[Window][];
        var portX1 = new int[Window];
        var trace = new string[Window];
        for (int k = 0; k < Window; k++)
        {
            flow.Step(in pad);
            game.Step(in pad);
            portOam[k] = new byte[256];
            System.Array.Copy(game.Ppu.Oam, portOam[k], 256);
            portPal[k] = new byte[32];
            System.Array.Copy(game.Ppu.PaletteRam, portPal[k], 32);
            portX1[k] = game.Objects.Slots[1].X;
            var s0 = game.Objects.Slots[0];
            var s1 = game.Objects.Slots[1];
            trace[k] = $"  port k{k + 1,3}  $95={game.FrameParity,3} $97={game.Oam.OamCursorBytes,3}  " +
                       $"slot0 $0410={s0.AnimFrame:X2} $0430={s0.AnimTimer:X2} $0400=${s0.Flags:X2} " +
                       $"$0450={s0.ScreenX:X2} | slot1 $0410={s1.AnimFrame:X2} $0430={s1.AnimTimer:X2} " +
                       $"$0400=${s1.Flags:X2} $04F0={s1.X & 0xFF:X2} X={s1.X}";
        }

        // --- per-frame walk trace: port clocks vs the golden's own $95/$97/slot bytes ---
        sb.AppendLine("walk phase, port frame k vs golden frame start+k (align check):");
        for (int k = 0; k < 24 && k < Window; k++)
        {
            for (int gf = start; gf <= start + 3; gf++)
            {
                int gr = (gf - 1) * 2048;
                sb.AppendLine((gf == start ? "  gold f" : "       f") + gf +
                              $"  $95={ram[gr + 0x95],3} $97={ram[gr + 0x97],3}  " +
                              $"slot0 $0410={ram[gr + 0x410]:X2} $0430={ram[gr + 0x430]:X2} $0400=${ram[gr + 0x400]:X2} " +
                              $"$0450={ram[gr + 0x450]:X2} | slot1 $0410={ram[gr + 0x411]:X2} $0430={ram[gr + 0x431]:X2} " +
                              $"$0400=${ram[gr + 0x401]:X2} $04F0={ram[gr + 0x4F1]:X2} $0541={ram[gr + 0x541]:X2}");
            }
            sb.AppendLine(trace[k] + $"  | OAM-vs-f{start + k} = " +
                          OamDiff(portOam[k], goldOam, (start + k - 1) * 256));
        }

        // --- scan the alignment delta that minimises the per-entry OAM difference ---
        int bestDelta = 0, bestTotal = int.MaxValue;
        for (int d = -3; d <= 3; d++)
        {
            int total = 0;
            for (int k = 0; k < Window; k++)
                total += OamDiff(portOam[k], goldOam, (start + k + d - 1) * 256);
            sb.Append($"delta {d:+0;-0;0}: OAM {total} B   ");
            if (total < bestTotal) { bestTotal = total; bestDelta = d; }
        }
        sb.AppendLine($"\nbest delta = {bestDelta:+0;-0;0} (golden frame = {start} + portFrame {bestDelta:+0;-0;0} - 1)");

        // --- per-frame OAM / $3F00 / PRESS START scroll at the chosen alignment ---
        int oamBadFrames = 0, palBadFrames = 0, firstBad = -1;
        for (int k = 0; k < Window; k++)
        {
            int gf = start + k + bestDelta;
            int oamD = OamDiff(portOam[k], goldOam, (gf - 1) * 256);
            int palD = ByteDiff(portPal[k], 0, goldPal, (gf - 1) * 32, 32);
            if (oamD != 0) { oamBadFrames++; if (firstBad < 0) firstBad = k; }
            if (palD != 0) palBadFrames++;
            if (k < 12 || oamD != 0 && k - (k % 10) == 0)
                sb.AppendLine($"  port f{k + 1,2} ↔ gold f{gf}: OAM {oamD,3}/256 " +
                              $"(head16 {OamHeadDiff(portOam[k], goldOam, (gf - 1) * 256, 16),3}/64)  " +
                              $"$3F00 {palD,2}/32  slot1 X={portX1[k]}");
        }
        sb.AppendLine($"OAM exact on {Window - oamBadFrames}/{Window} frames (total {bestTotal} bytes), " +
                      $"$3F00 exact on {Window - palBadFrames}/{Window} frames" +
                      (firstBad >= 0 ? $", first OAM divergence on port frame {firstBad + 1}" : ""));
        // Port frame 1 publishes the pre-title shadow; the golden's f919 still holds the
        // ATTRACT leftovers (all Y=$F8), so that frame is not a comparable pair. The two
        // residues that matter are the 7-byte one of frames 2..9 and the 1-byte one after.
        foreach (int kk in new[] { 1, 9 })
        {
            int gf = start + kk + bestDelta;
            sb.AppendLine($"differing OAM entries, port frame {kk + 1} ↔ gold f{gf}:");
            ReportOam(sb, portOam[kk], goldOam, (gf - 1) * 256, gf);
            sb.AppendLine($"  $3F00 port = {Hex(portPal[kk], 0, 32)}");
            sb.AppendLine($"  $3F00 gold = {Hex(goldPal, (gf - 1) * 32, 32)}");
        }

        // --- nametable: the bank-2 task 1 record stream ($C79F-$C7A1) ---
        const int stride = 4 + 0x1000;
        int recs = vram.Length / stride;
        int bestRec = -1, bestFrame = -1;
        for (int i = 0; i < recs; i++)
        {
            int f = vram[i * stride] | (vram[i * stride + 1] << 8) |
                    (vram[i * stride + 2] << 16) | (vram[i * stride + 3] << 24);
            if (f >= start + bestDelta && (bestRec < 0 || f < bestFrame)) { bestFrame = f; bestRec = i; }
        }
        if (bestRec < 0) { sb.AppendLine("FAIL: no vram record in the title window"); return; }
        int off = bestRec * stride + 4;
        int nd = 0, firstNt = -1;
        for (int i = 0; i < 0x800; i++)
            if (game.Ppu.NametableRam[i] != vram[off + i])
            {
                if (firstNt < 0) firstNt = i;
                nd++;
            }
        sb.AppendLine($"nametable $2000-$27FF vs golden f{bestFrame}: {nd} mismatched of 2048" +
            (nd == 0 ? " - EXACT" : $", first at ${0x2000 + firstNt:X4}"));
        if (nd != 0)
        {
            int shown = 0;
            for (int i = 0; i < 0x800 && shown < 10; i++)
                if (game.Ppu.NametableRam[i] != vram[off + i])
                {
                    sb.AppendLine($"  ${0x2000 + i:X4}: port=$ {game.Ppu.NametableRam[i]:X2} gold=$ {vram[off + i]:X2}");
                    shown++;
                }
        }

        // --- CHR banks + the sprite-palette aliasing the NMI upload produces ---
        var chr = File.ReadAllText(RefDir + "/chr_map.csv");
        string goldChr = null;
        foreach (var line in chr.Split('\n'))
        {
            int g = LineFrame(line);
            if (g == start + bestDelta + 2) { goldChr = line.Trim(); break; }
        }
        sb.AppendLine($"golden chr_map f{start + bestDelta + 2}: {goldChr ?? "(no row)"}  |  " +
                      $"port chr0={game.Ppu.Chr0Bank} chr1={game.Ppu.Chr1Bank} " +
                      $"$3F00=${Hex(game.Ppu.PaletteRam, 0, 16)}");

        DumpPng(game, Root + "/Temp/title_port.png", sb);
    }

    private static int LineFrame(string line)
    {
        var t = line.Trim();
        if (t.Length == 0) return -1;
        int c = t.IndexOf(',');
        return c < 0 ? -1 : int.Parse(t.Substring(0, c));
    }

    private static int ByteDiff(byte[] a, int aOff, byte[] b, int bOff, int count)
    {
        int n = 0;
        for (int i = 0; i < count; i++)
            if (a[aOff + i] != b[bOff + i]) n++;
        return n;
    }

    /// <summary>Mismatched OAM bytes, plus the byte count of the first 16 entries (64 B).</summary>
    private static int OamDiff(byte[] port, byte[] gold, int goldOff)
    {
        int n = 0;
        for (int i = 0; i < 256; i++) if (port[i] != gold[goldOff + i]) n++;
        return n;
    }

    private static int OamHeadDiff(byte[] port, byte[] gold, int goldOff, int entries)
    {
        int n = 0;
        for (int i = 0; i < entries * 4; i++) if (port[i] != gold[goldOff + i]) n++;
        return n;
    }

    private static void ReportOam(StringBuilder sb, byte[] port, byte[] gold, int goldOff, int gf)
    {
        for (int e = 0; e < 64; e++)
        {
            int i = e * 4;
            bool same = port[i] == gold[goldOff + i] && port[i + 1] == gold[goldOff + i + 1]
                     && port[i + 2] == gold[goldOff + i + 2] && port[i + 3] == gold[goldOff + i + 3];
            if (same) continue;
            sb.AppendLine($"  entry {e,2}: port Y=$ {port[i]:X2} t=$ {port[i + 1]:X2} " +
                          $"a=$ {port[i + 2]:X2} x=$ {port[i + 3]:X2} | gold f{gf} " +
                          $"Y=$ {gold[goldOff + i]:X2} t=$ {gold[goldOff + i + 1]:X2} " +
                          $"a=$ {gold[goldOff + i + 2]:X2} x=$ {gold[goldOff + i + 3]:X2}");
        }
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
