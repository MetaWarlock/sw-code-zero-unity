// SFX verification: play the punch SFX ($3F) from a fresh sequencer and dump the
// group-A state + SFX zero-page per frame, in the same field layout as the Mesen
// probe's driver_state.bin, for direct comparison (probe queued $3F at its frame 88;
// probe frames 88.. = port frames 1..).
using System;
using System.IO;
using System.Text;
using Darkwing.Audio;

public static class AudioPortSfxCompare
{
    public static string Main() => RenderTo(@"D:\Art\SW Code Zero Unity\Art\Research\DarkwingDuck");

    public static string RenderTo(string outputDirectory)
    {
        string proj = @"D:\Art\SW Code Zero Unity";
        byte[] bank3 = File.ReadAllBytes(Path.Combine(proj, @"Assets\Darkwing\Data\audio_bank3.bytes"));
        var seq = new AudioSequencer(bank3);
        seq.PlaySfx(0x3F);
        var sb = new StringBuilder();
        sb.AppendLine("frame,CE,CF,D0,D1,D2,D3,D4,D5,D6,D7,D8,ga00,ga04,ga08,ga0C,ga10,ga14,ga18,ga1C,ga20,ga24,ga28,ga2C,ga30,ga34,ga38,ga3C,ga40,ga44");
        var buf = new float[800];
        for (int f = 1; f <= 40; f++)
        {
            seq.RenderFrame(buf, 0);
            byte[] st = seq.SaveState();
            int b = 44;
            var v = new System.Collections.Generic.List<string> { f.ToString() };
            // zero page order in SaveState: C0..C6,C7,C8,C9,CA,CB,CC,CD,CE,CF,D0..D9
            int[] zpIdx = { 14, 15, 16, 17, 18, 19, 20, 21, 22, 23, 24 };  // CE,CF,D0..D8
            foreach (int i in zpIdx) v.Add(st[6 + i].ToString("X2"));
            // group A field bytes: $0700+$00..$47 (X=0 slice)
            for (int a = 0x00; a <= 0x44; a += 4) v.Add(st[b + a].ToString("X2"));
            sb.AppendLine(string.Join(",", v));
        }
        string path = Path.Combine(outputDirectory, "audio_port_sfx3F_state.csv");
        Directory.CreateDirectory(outputDirectory);
        File.WriteAllText(path, sb.ToString());
        return "OK " + path;
    }
}
