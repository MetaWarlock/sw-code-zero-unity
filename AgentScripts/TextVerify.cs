using System.IO;
using System.Text;
using UnityEngine;
using Darkwing.Core;

/// <summary>
/// Headless check of the bank-2 dialog coroutine ($AA00) against the original-ROM capture
/// reference/gold_briefing_b0 (title → credits → the $C5F7 intro scene, streams $0E and $0F).
///
/// Compares, per stream:
///   1. the pointer pair the ROM laded into $0C/$0D,
///   2. the per-frame progression of the byte index and line counter ($0E/$10 in the capture),
///   3. the three 29-tile nametable lines ($22E2/$2322/$2362) once the stream is finished.
///
/// Entry: TextVerify.Run
/// </summary>
public static class TextVerify
{
    private const string Root = "D:/Art/SW Code Zero Unity";
    private const string Cap = Root + "/Research/DarkwingDuck/reference/gold_briefing_b0";
    private const int VramEvery = 30;
    private const int LineTiles = 29;

    public static string Run()
    {
        var sb = new StringBuilder();
        var bank2 = File.ReadAllBytes(Root + "/Assets/Darkwing/Data/prg_bank2.bytes");
        var ram = File.ReadAllBytes(Cap + "/ram_frames.bin");
        var vram = File.ReadAllBytes(Cap + "/vram_frames.bin");
        int frames = ram.Length / 2048;
        int records = vram.Length / (4 + 0x1000);
        sb.AppendLine($"capture: {frames} RAM frames, {records} VRAM records (every {VramEvery})");

        var data = DataLoader.Load(
            File.ReadAllBytes(Root + "/Assets/Darkwing/Data/chr_full.bytes"),
            File.ReadAllBytes(Root + "/Assets/Darkwing/Data/levelsets.bytes"),
            File.ReadAllBytes(Root + "/Assets/Darkwing/Data/screens.bytes"),
            File.ReadAllBytes(Root + "/Assets/Darkwing/Data/levels.bytes"),
            File.ReadAllBytes(Root + "/Assets/Darkwing/Data/anims.bytes"));

        // --- the tables the engine reads, straight out of the 16 KB bank image ---
        sb.AppendLine($"$ABAA/$ABBD pointer pairs: $0E=${(bank2[0xABBD - 0x8000 + 0x0E] << 8 | bank2[0xABAA - 0x8000 + 0x0E]):X4} " +
                      $"$0F=${(bank2[0xABBD - 0x8000 + 0x0F] << 8 | bank2[0xABAA - 0x8000 + 0x0F]):X4}");
        var probe = new TextEngine(new Ppu(data), bank2);
        sb.Append("line bases from $AAD7/$AADA:");
        for (int i = 0; i < 3; i++) sb.Append($" ${probe.LineAddresses[i]:X4}");
        sb.AppendLine();

        bool all = true;
        all &= CheckStream(sb, probe, bank2, ram, vram, data, 0x0E, 390);
        all &= CheckStream(sb, probe, bank2, ram, vram, data, 0x0F, 480);
        sb.AppendLine(all ? "RESULT: MATCH text engine vs gold_briefing_b0"
                          : "RESULT: MISMATCH text engine vs gold_briefing_b0");
        File.WriteAllText(Root + "/Temp/text_verify_trace.txt", sb.ToString());
        return sb.ToString();
    }

    private static bool CheckStream(StringBuilder sb, TextEngine engine, byte[] bank2,
                                    byte[] ram, byte[] vram, GameData data, int stream, int settleFrame)
    {
        int start = FirstFrameOf(ram, stream);
        if (start < 0) { sb.AppendLine($"stream ${stream:X2}: never started in the capture"); return false; }
        sb.AppendLine($"--- stream ${stream:X2}: ROM $AADD clear at f{start}, settle compare at f{settleFrame} ---");

        var ppu = new Ppu(data);
        var eng = new TextEngine(ppu, bank2);
        eng.Start(stream);
        int frameCounter = ram[start * 2048 + 0x92];
        int pacingBad = 0, firstBad = -1, tick = 0;
        while (eng.Running && tick < 400)
        {
            eng.Tick(frameCounter + tick, 0x00);
            tick++;
            int f = start + tick;
            if (f >= ram.Length / 2048 - 1) break;
            if (!eng.Running) break;
            int romBytes = ram[f * 2048 + 0x0E];
            int romLine = ram[f * 2048 + 0x10];
            if (romBytes != eng.ByteIndex || romLine != eng.Line)
            {
                if (pacingBad == 0)
                    sb.AppendLine($"  first pacing diff f{f}: port bytes={eng.ByteIndex} line={eng.Line} " +
                                  $"| ROM $0E={romBytes} $10={romLine}");
                pacingBad++;
                if (firstBad < 0) firstBad = f;
            }
        }
        sb.AppendLine($"  typed in {tick} frames, Done={eng.Done}; per-frame $0E/$10 diffs: {pacingBad}");

        // The ROM's own final text, read out of the VRAM record of the settled frame.
        int rec = settleFrame / VramEvery - 1;
        var nt = new byte[0x1000];
        System.Array.Copy(vram, rec * (4 + 0x1000) + 4, nt, 0, 0x1000);

        int bad = 0;
        for (int line = 0; line < 3; line++)
        {
            int addr = eng.LineAddresses[line];
            var gold = new StringBuilder();
            var port = new StringBuilder();
            int lineBad = 0;
            for (int t = 0; t < LineTiles; t++)
            {
                byte g = nt[addr - 0x2000 + t], p = ppu.NametableRam[addr - 0x2000 + t];
                if (g != p) { lineBad++; bad++; }
                gold.Append(Decode(g));
                port.Append(Decode(p));
            }
            sb.AppendLine($"  ${addr:X4} tiles {LineTiles - lineBad}/{LineTiles} match");
            sb.AppendLine($"     ROM : \"{gold.ToString().TrimEnd()}\"");
            sb.AppendLine($"     port: \"{port.ToString().TrimEnd()}\"");
        }
        bool ok = bad == 0 && pacingBad == 0 && eng.Done;
        sb.AppendLine($"  stream ${stream:X2}: {(ok ? "MATCH" : "MISMATCH")} " +
                      $"(nametable diffs {bad}, pacing diffs {pacingBad})");
        return ok;
    }

    /// <summary>First frame whose $62 equals the stream id (the ROM's `STA $62` before JSR $AA00).</summary>
    private static int FirstFrameOf(byte[] ram, int stream)
    {
        int frames = ram.Length / 2048;
        for (int f = 1; f < frames; f++)
            if (ram[f * 2048 + 0x62] == stream && ram[(f - 1) * 2048 + 0x62] != stream) return f;
        return -1;
    }

    private static char Decode(byte t) =>
        t == 0x03 || t == 0x00 || t == 0x02 ? ' '
        : t >= 0xDA && t <= 0xF3 ? (char)('A' + t - 0xDA)
        : t == 0xFA ? '!'
        : '#';
}
