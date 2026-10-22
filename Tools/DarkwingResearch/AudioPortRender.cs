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

    public static string SfxLoopContract()
    {
        byte[] bank = File.ReadAllBytes(Path.Combine(Proj, @"Assets\Darkwing\Data\audio_bank3.bytes"));
        byte[] stream = { 0, 3, 0, 1, 2, 0x9E, 0, 7, 0 };
        Array.Copy(stream, 0, bank, 0x1E00, stream.Length);
        const System.Reflection.BindingFlags binding = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
        var parser = typeof(AudioSequencer).GetMethod("SfxParseTick", binding);
        var failures = new System.Collections.Generic.List<string>();
        int cases = 0;
        foreach (byte count in new byte[] { 0, 1, 2, 0x80, 0x81, 0x82 })
        {
            var sequencer = new AudioSequencer(bank);
            void Set(string name, byte value) => typeof(AudioSequencer).GetField(name, binding).SetValue(sequencer, value);
            byte Get(string name) => (byte)typeof(AudioSequencer).GetField(name, binding).GetValue(sequencer);
            Set("_d0", 3); Set("_d1", 0x9E); Set("_d6", count);
            parser.Invoke(sequencer, null);
            bool finished = (count & 0x7F) == 2;
            byte expected = finished ? (byte)(count & 0x80) : (byte)(count + 1);
            if (Get("_d6") != expected || Get("_d0") != (finished ? 9 : 3) || Get("_d3") != (finished ? 7 : 3))
                failures.Add($"count{count:X2}: counter{Get("_d6"):X2} pointer{Get("_d0"):X2} length{Get("_d3")}");
            cases++;
        }
        bank[0x1E04] = 0x80;
        var infinite = new AudioSequencer(bank);
        typeof(AudioSequencer).GetField("_d0", binding).SetValue(infinite, (byte)3);
        typeof(AudioSequencer).GetField("_d1", binding).SetValue(infinite, (byte)0x9E);
        typeof(AudioSequencer).GetField("_d6", binding).SetValue(infinite, (byte)0x81);
        parser.Invoke(infinite, null);
        if ((byte)typeof(AudioSequencer).GetField("_d6", binding).GetValue(infinite) != 0x81)
            failures.Add("ASL parameter80 becomes zero and must preserve counter");
        cases++;
        return failures.Count == 0 ? $"PASS {cases}/{cases} SFX loop cases; doubled count, completed-loop pointer skip, high-bit and infinite controls"
            : $"FAIL {failures.Count}/{cases}: " + string.Join("; ", failures);
    }

    public static string OutputUnderrunContract()
    {
        byte[] bank = File.ReadAllBytes(Path.Combine(Proj, @"Assets\Darkwing\Data\audio_bank3.bytes"));
        const System.Reflection.BindingFlags binding = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
        var feedType = typeof(Darkwing.UnityApp.AudioOutFeed);
        var callback = feedType.GetMethod("OnAudioFilterRead", binding);
        var gameObject = new UnityEngine.GameObject("AudioUnderrunContract");
        try
        {
            var feed = gameObject.AddComponent<Darkwing.UnityApp.AudioOutFeed>();
            feed.Source = new AudioSequencer(bank);
            void Push(float[] samples)
            {
                var queueField = feedType.GetField("_samples", binding);
                if (queueField != null)
                {
                    object queue = queueField.GetValue(feed);
                    queue.GetType().GetMethod("Push").Invoke(queue, new object[] { samples, samples.Length });
                    return;
                }
                var ring = (float[])feedType.GetField("_ring", binding).GetValue(feed);
                var writeField = feedType.GetField("_write", binding);
                int write = (int)writeField.GetValue(feed);
                foreach (float sample in samples) { ring[write] = sample; write = (write + 1) % ring.Length; }
                writeField.SetValue(feed, write);
            }
            float[] Read(int frames, int channels)
            {
                var output = new float[frames * channels];
                callback.Invoke(feed, new object[] { output, channels });
                return output;
            }
            Read(4, 2);
            Push(new float[] { 1, 2, 3, 4 });
            float[] first = Read(8, 2);
            for (int index = 0; index < 16; index++)
                if (first[index] != (index < 8 ? index / 2 + 1 : 0))
                    throw new InvalidOperationException("Initial silence or stereo-copy control failed");
            Push(new float[] { 5, 6, 7, 8, 9, 10, 11, 12 });
            float[] recovered = Read(4, 1);
            for (int index = 0; index < 4; index++)
                if (recovered[index] != 9 + index)
                    return $"FAIL late PCM replayed: expected9..12 after four missed samples; got {string.Join(",", recovered)}";
            return "PASS output timeline after underrun; initial silence and stereo controls; late PCM is not replayed";
        }
        finally { UnityEngine.Object.DestroyImmediate(gameObject); }
    }

    public static string NoteDurationContract()
    {
        byte[] bank = File.ReadAllBytes(Path.Combine(Proj, @"Assets\Darkwing\Data\audio_bank3.bytes"));
        const System.Reflection.BindingFlags binding = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
        var note = typeof(AudioSequencer).GetMethod("NoteEvent", binding);
        var pageField = typeof(AudioSequencer).GetField("_p7", binding);
        int cases = 0;
        var failures = new System.Collections.Generic.List<string>();
        foreach (byte flags in new byte[] { 0, 8, 0x10, 0x18, 0x20, 0x28, 0x30, 0x38 })
        foreach (byte previousHold in new byte[] { 0, 0xFF })
        {
            const int channel = 0x2A;
            var sequencer = new AudioSequencer(bank);
            var page = (byte[])pageField.GetValue(sequencer);
            page[0x30 + channel] = flags;
            page[0x38 + channel] = previousHold;
            int multiplier = bank[((flags & 0x20) != 0 ? 0x915 : 0x91C) + 3];
            bool dotted = (flags & 0x30) == 0x10;
            if (dotted) multiplier += multiplier >> 1;
            byte expectedFlags = dotted ? (byte)(flags & 0xEF) : flags;
            note.Invoke(sequencer, new object[] { channel, (byte)0x80 });
            if (page[0x38 + channel] != (byte)(previousHold + multiplier) || page[0x30 + channel] != expectedFlags)
                failures.Add($"flags{flags:X2} hold{previousHold:X2}: duration{page[0x38 + channel]} flags{page[0x30 + channel]:X2}");
            cases++;
        }
        return failures.Count == 0 ? $"PASS {cases}/{cases} note-duration cases; bank3 $83EF-$83F4 carry is original flags bit4"
            : $"FAIL {failures.Count}/{cases}: " + string.Join("; ", failures);
    }

    public static string EnvelopeHoldContract()
    {
        byte[] bank = File.ReadAllBytes(Path.Combine(Proj, @"Assets\Darkwing\Data\audio_bank3.bytes"));
        if (bank[0x6CB] != 0x20 || bank[0x6CC] != 0x87 || bank[0x6CD] != 2 || bank[0x6CE] != 0x87)
            throw new InvalidOperationException("Unexpected bank3 $86C7 envelope dispatch table");
        const System.Reflection.BindingFlags binding = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
        var envelope = typeof(AudioSequencer).GetMethod("Envelope", binding);
        var instrument = typeof(AudioSequencer).GetMethod("SetInstrumentPtr", binding);
        var pageField = typeof(AudioSequencer).GetField("_p7", binding);
        int cases = 0;
        var failures = new System.Collections.Generic.List<string>();
        foreach (int channel in new[] { 0, 1, 2, 3, 0x28, 0x29, 0x2A, 0x2B })
        {
            var sequencer = new AudioSequencer(bank);
            var page = (byte[])pageField.GetValue(sequencer);
            page[channel] = 1;
            page[4 + channel] = 2;
            page[0x10 + channel] = 0x90;
            instrument.Invoke(sequencer, new object[] { channel });
            for (int tick = 0; tick < 8; tick++)
                envelope.Invoke(sequencer, new object[] { channel });
            if ((page[4 + channel] & 7) != 2 || page[0x10 + channel] != 0x90)
                failures.Add($"hold channel{channel:X2}: phase{page[4 + channel] & 7} timer{page[0x10 + channel]:X2}");
            cases++;
            if ((channel & 3) != 1) continue;
            page[4 + channel] = 3;
            page[0x10 + channel] = 1;
            envelope.Invoke(sequencer, new object[] { channel });
            if ((page[4 + channel] & 7) != 4 || page[0x10 + channel] != 0)
                failures.Add($"release control channel{channel:X2}");
            cases++;
        }
        return failures.Count == 0 ? $"PASS {cases}/{cases} envelope contracts; bank3 $86CB hold->$8720, $86CD release->$8702"
            : $"FAIL {failures.Count}/{cases}: " + string.Join("; ", failures);
    }

    public static string SignalContract()
    {
        var failures = new System.Collections.Generic.List<string>();
        int[] periods = { 4, 8, 16, 32, 64, 96, 128, 160, 202, 254, 380, 508, 762, 1016, 2034, 4068 };
        for (int mode = 0; mode < 2; mode++)
        {
            for (int index = 0; index < periods.Length; index++)
            {
                var apu = new Apu();
                apu.Write(0x400E, (byte)((mode << 7) | index));
                ushort expected = 1;
                for (int cycle = 0; cycle < periods[index] * 100; cycle++)
                {
                    if (cycle % periods[index] == 0)
                    {
                        int feedback = (expected ^ (expected >> (mode == 0 ? 1 : 6))) & 1;
                        expected = (ushort)((expected >> 1) | (feedback << 14));
                    }
                    apu.StepCpu();
                    if (ChannelField<ushort>(apu, "_noise", "Shift") != expected)
                    {
                        failures.Add($"noise mode={mode} index={index} CPU-cycle={cycle + 1}");
                        break;
                    }
                }
            }
        }
        var triangle = new Apu();
        triangle.Write(0x4015, 4);
        triangle.Write(0x4008, 0xFF);
        triangle.Write(0x400A, 31);
        triangle.Write(0x400B, 8);
        for (int cycle = 0; cycle < 1000; cycle++)
        {
            triangle.StepCpu();
            if (ChannelField<int>(triangle, "_tri", "SeqStep") != ((1 + cycle / 32) & 31))
            {
                failures.Add($"triangle CPU/1 period31 CPU-cycle={cycle + 1}");
                break;
            }
        }
        var pulse = new Apu();
        pulse.Write(0x4015, 1);
        pulse.Write(0x4000, 0x3F);
        pulse.Write(0x4002, 31);
        pulse.Write(0x4003, 8);
        for (int cycle = 0; cycle < 1000; cycle++)
        {
            pulse.StepCpu();
            if (ChannelField<int>(pulse, "_p1", "DutyStep") != (((cycle / 2 + 1) / 32) & 7))
            {
                failures.Add($"pulse CPU/2 period31 CPU-cycle={cycle + 1}");
                break;
            }
        }
        return failures.Count == 0 ? "PASS 34/34 signal-clock contracts: 16 noise periods x2 modes; triangle CPU/1; pulse CPU/2 control"
            : $"FAIL {failures.Count}/34: " + string.Join("; ", failures);
    }

    static T ChannelField<T>(Apu apu, string channel, string field)
    {
        const System.Reflection.BindingFlags binding = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
        object state = typeof(Apu).GetField(channel, binding).GetValue(apu);
        return (T)state.GetType().GetField(field).GetValue(state);
    }

    public static string ReplayWrites(string csvPath, string framesPath, string outputDirectory)
    {
        string[] writes = File.ReadAllLines(csvPath);
        string[] frames = File.ReadAllLines(framesPath);
        long endCycle = long.Parse(frames[frames.Length - 1].Split(',')[2]);
        var apu = new Apu();
        using var wav = new MemoryStream();
        WriteWavHeader(wav);
        int writeIndex = 1;
        long nextCycle = long.Parse(writes[writeIndex].Split(',')[1]);
        long accumulator = 0;
        long samples = 0;
        for (long cycle = 0; cycle <= endCycle; cycle++)
        {
            while (writeIndex < writes.Length && nextCycle <= cycle)
            {
                string[] values = writes[writeIndex++].Split(',');
                apu.Write(Convert.ToInt32(values[3], 16), Convert.ToByte(values[4], 16));
                nextCycle = writeIndex < writes.Length ? long.Parse(writes[writeIndex].Split(',')[1]) : long.MaxValue;
            }
            apu.StepCpu();
            accumulator += 48000;
            if (accumulator < Apu.CpuHz) continue;
            accumulator -= Apu.CpuHz;
            short sample = (short)Math.Max(-32768, Math.Min(32767, (int)(apu.CurrentOutput() * 32767f)));
            wav.WriteByte((byte)(sample & 0xFF));
            wav.WriteByte((byte)((sample >> 8) & 0xFF));
            samples++;
        }
        FinishWav(wav, 48000);
        Directory.CreateDirectory(outputDirectory);
        File.WriteAllBytes(Path.Combine(outputDirectory, "source_writes_native_apu.wav"), wav.ToArray());
        return $"APU-only diagnostic: original register writes, no game/CPU execution; cycles={endCycle + 1} samples={samples} writes={writeIndex - 1}";
    }

    public static string Main() => RenderTo(Path.Combine(Proj, @"Research\DarkwingDuck"));

    public static string RenderTo(string outputDirectory)
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

        string wavPath = Path.Combine(outputDirectory, "audio_port_song1.wav");
        string csvPath = Path.Combine(outputDirectory, "audio_port_song1_state.csv");
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
