// Test harness for the Darkwing audio port (runs inside the Unity editor via
// `unity command run_script`). Compiles against the project's Darkwing.Audio asmdef.
//
//   Main()       — render song 1 (id 1) for 600 frames from a fresh Apu+Sequencer,
//                  write a 48 kHz 16-bit mono WAV + a per-frame state CSV, and
//                  verify SaveState/LoadState round-tripping.
//   RenderSfx()  — render a single SFX id on top of song 1 (for arbitration checks).
//
// State CSV layout (for structural comparison against the Mesen probe's
// driver_state.bin): one row per frame with the group-B (music) period/hold/
// duration fields for pulse1/pulse2/triangle, extracted from SaveState().

using System;
using System.IO;
using System.Text;
using Darkwing.Audio;

public static class AudioPortRender
{
    const string Proj = @"D:\Art\SW Code Zero Unity";
    const int Frames = 600;

    public static string Main()
    {
        byte[] bank3 = File.ReadAllBytes(Path.Combine(Proj, @"Assets\Darkwing\Data\audio_bank3.bytes"));
        var seq = new AudioSequencer(bank3);

        // SaveState round-trip check on the fresh instance (also warms nothing —
        // the sequencer must be deterministic across a save/load cycle).
        byte[] snap0 = seq.SaveState();
        if (!seq.LoadState(snap0)) return "FAIL: SaveState/LoadState round-trip rejected";
        byte[] snap1 = seq.SaveState();
        if (!BytesEqual(snap0, snap1)) return "FAIL: SaveState not stable across load";

        seq.PlayMusic(1);

        var wav = new MemoryStream();
        WriteWavHeader(wav);
        var buf = new float[800];
        var csv = new StringBuilder();
        csv.AppendLine("frame,p1_period,p2_period,tri_period,p1_hold,p2_hold,tri_hold,p1_dur,p2_dur,tri_dur,p1_env,p2_env,tri_env");
        long total = 0;
        for (int f = 1; f <= Frames; f++)
        {
            int n = seq.RenderFrame(buf, 0);
            for (int i = 0; i < n; i++)
            {
                short s = (short)Math.Max(-32768, Math.Min(32767, (int)(buf[i] * 2.0f * 32767f)));
                wav.WriteByte((byte)(s & 0xFF));
                wav.WriteByte((byte)((s >> 8) & 0xFF));
            }
            total += n;
            csv.AppendLine(StateRow(f, seq.SaveState()));
        }
        FinishWav(wav, 48000);

        string wavPath = Path.Combine(Proj, @"Research\DarkwingDuck\audio_port_song1.wav");
        string csvPath = Path.Combine(Proj, @"Research\DarkwingDuck\audio_port_song1_state.csv");
        Directory.CreateDirectory(Path.GetDirectoryName(wavPath));
        File.WriteAllBytes(wavPath, wav.ToArray());
        File.WriteAllText(csvPath, csv.ToString());
        return $"OK frames={Frames} samples={total} wav={wavPath} ({new FileInfo(wavPath).Length} bytes) csv={csvPath}";
    }

    /// <summary>Render song 1 with SFX id <paramref name="sfxId"/> fired at frame 100.</summary>
    public static string RenderSfx(int sfxId)
    {
        byte[] bank3 = File.ReadAllBytes(Path.Combine(Proj, @"Assets\Darkwing\Data\audio_bank3.bytes"));
        var seq = new AudioSequencer(bank3);
        seq.PlayMusic(1);
        var wav = new MemoryStream();
        WriteWavHeader(wav);
        var buf = new float[800];
        long total = 0;
        for (int f = 1; f <= Frames; f++)
        {
            if (f == 100) seq.PlaySfx(sfxId);
            int n = seq.RenderFrame(buf, 0);
            for (int i = 0; i < n; i++)
            {
                short s = (short)Math.Max(-32768, Math.Min(32767, (int)(buf[i] * 2.0f * 32767f)));
                wav.WriteByte((byte)(s & 0xFF));
                wav.WriteByte((byte)((s >> 8) & 0xFF));
            }
            total += n;
        }
        FinishWav(wav, 48000);
        string wavPath = Path.Combine(Proj, $@"Research\DarkwingDuck\audio_port_song1_sfx{sfxId:X2}.wav");
        Directory.CreateDirectory(Path.GetDirectoryName(wavPath));
        File.WriteAllBytes(wavPath, wav.ToArray());
        return $"OK frames={Frames} samples={total} wav={wavPath}";
    }

    // SaveState layout: tag(4) + schema(2) + 26 zero-page bytes + queueWrite + queueRead
    // (2) + 8 queue bytes + 0x0800 marker (2) + 2048 page-7 bytes.
    const int StateP7Offset = 4 + 2 + 26 + 2 + 8 + 2;

    static string StateRow(int frame, byte[] st)
    {
        int b = StateP7Offset;
        // group B (music) channel c: base = 0x28 + c in page-7 field terms.
        // period lo/hi = $0748/$074C + c; hold = $0760 + c; duration = $0768 + c;
        // env timer = $0738 + c. Page-7 index = address - 0x0700.
        int p7(int addr) => st[b + (addr - 0x0700)];
        string per(int c) => (p7(0x074C + c) << 8 | p7(0x0748 + c)).ToString("X4");
        string fld(int addr, int c) => p7(addr + c).ToString("X2");
        return string.Format("{0},{1},{2},{3},{4},{5},{6},{7},{8},{9},{10},{11},{12}",
            frame, per(3), per(2), per(1),
            fld(0x0760, 3), fld(0x0760, 2), fld(0x0760, 1),
            fld(0x0768, 3), fld(0x0768, 2), fld(0x0768, 1),
            fld(0x0738, 3), fld(0x0738, 2), fld(0x0738, 1));
    }

    static void WriteWavHeader(MemoryStream ms)
    {
        var w = new BinaryWriter(ms);
        w.Write(new[] { 'R', 'I', 'F', 'F' });
        w.Write(0);                       // patched later
        w.Write(new[] { 'W', 'A', 'V', 'E' });
        w.Write(new[] { 'f', 'm', 't', ' ' });
        w.Write(16);
        w.Write((short)1);                // PCM
        w.Write((short)1);                // mono
        w.Write(48000);                   // sample rate
        w.Write(48000 * 2);               // byte rate
        w.Write((short)2);                // block align
        w.Write((short)16);               // bits per sample
        w.Write(new[] { 'd', 'a', 't', 'a' });
        w.Write(0);                       // patched later
    }

    static void FinishWav(MemoryStream ms, int sampleRate)
    {
        byte[] bytes = ms.ToArray();
        Patch(bytes, 4, bytes.Length - 8);      // RIFF chunk size
        Patch(bytes, 40, bytes.Length - 44);    // data chunk size
        ms.SetLength(0);
        ms.Write(bytes, 0, bytes.Length);
    }

    static void Patch(byte[] b, int off, int v)
    {
        b[off] = (byte)v; b[off + 1] = (byte)(v >> 8); b[off + 2] = (byte)(v >> 16); b[off + 3] = (byte)(v >> 24);
    }

    static bool BytesEqual(byte[] a, byte[] b)
    {
        if (a.Length != b.Length) return false;
        for (int i = 0; i < a.Length; i++) if (a[i] != b[i]) return false;
        return true;
    }
}
