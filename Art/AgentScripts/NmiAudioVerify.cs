using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using Darkwing.Audio;
using Darkwing.Core;

public static class NmiAudioVerify
{
    public static string Run()
    {
        const string root = "D:/Art/SW Code Zero Unity/";
        const string folder = root + "Art/Temp/audio_native_cost_17234/";
        var phases = new Dictionary<int, Dictionary<string, long>>();
        foreach (string line in File.ReadLines(root + "Art/Temp/human_rng17234/full_phases.csv"))
        {
            string[] fields = line.Split(',');
            if (!int.TryParse(fields[0], out int poll) || poll < 17200 || poll > 17236) continue;
            if (!phases.TryGetValue(poll, out var frame)) phases[poll] = frame = new Dictionary<string, long>();
            if (!frame.ContainsKey(fields[2])) frame[fields[2]] = long.Parse(fields[1]);
        }
        byte[] before = File.ReadAllBytes(folder + "audio_before.bin");
        byte[] after = File.ReadAllBytes(folder + "audio_after.bin");
        byte[] original = File.ReadAllBytes(root + "Art/Temp/human_164819_fulla/replay1/buffers.bin");
        string[] rows = File.ReadAllLines(folder + "audio_ticks.csv");
        if (before.Length != 37 * 2048 || after.Length != before.Length || rows.Length != 75 || original.Length != 38886 * 4384) throw new Exception("Incomplete audio fixture");
        byte[] bank3 = File.ReadAllBytes(root + "Art/Unity/CodeZero/Darkwing/Data/audio_bank3.bytes");
        byte[] bank7 = File.ReadAllBytes(root + "Art/Unity/CodeZero/Darkwing/Data/world_bank7.bytes");
        var drain = typeof(DarkwingGame).GetMethod("DrainAudioQueue", BindingFlags.Instance | BindingFlags.NonPublic);
        if (drain == null) throw new MissingMethodException("Native NMI audio seam");
        int matched = 0, unsupported = 0;
        for (int index = 0; index < 37; index++)
        {
            string[] entry = rows[1 + index * 2].Split(',');
            int poll = int.Parse(entry[0]);
            var phase = phases[poll];
            var game = new DarkwingGame();
            game.Bank7 = bank7; game.Bank3 = bank3;
            var audio = game.Audio;
            int originalOffset = (poll - 1) * 4384;
            for (int address = 0xC0; address <= 0xD8; address++)
                Field("_" + address.ToString("x2")).SetValue(audio, original[originalOffset + address]);
            Array.Copy(original, originalOffset + 0x700, (byte[])Field("_p7").GetValue(audio), 0, 256);
            const BindingFlags binding = BindingFlags.Instance | BindingFlags.NonPublic;
            typeof(DarkwingGame).GetField("_audioWrite", binding).SetValue(game, (int)original[originalOffset + 0xDA]);
            typeof(DarkwingGame).GetField("_audioRead", binding).SetValue(game, (int)original[originalOffset + 0xDB]);
            Array.Copy(original, originalOffset + 0xDC, (byte[])typeof(DarkwingGame).GetField("_audioQueue", binding).GetValue(game), 0, 8);
            drain.Invoke(game, null);
            if (!game.NmiAudioTimingSupported) { unsupported++; continue; }
            int expected = checked((int)(phase["C114"] + 6 - phase["C0DC"]));
            if (game.LastNmiAudioServiceCycles != expected)
                throw new Exception($"Integrated audio meter {poll}: {game.LastNmiAudioServiceCycles}/{expected}");
            for (int address = 0xC7; address <= 0xD8; address++)
                if ((byte)Field("_" + address.ToString("x2")).GetValue(audio) != after[index * 2048 + address])
                    throw new Exception("Integrated audio state " + poll);
            byte[] nativePage = (byte[])Field("_p7").GetValue(audio);
            for (int address = 0; address < 256; address++)
                if (nativePage[address] != after[index * 2048 + 0x700 + address]) throw new Exception("Integrated audio page " + poll);
            matched++;
        }
        if (matched != 37 || unsupported != 0) throw new Exception($"NMI audio matched={matched}, unsupported={unsupported}");
        return $"PASS integrated native NMI audio meter {matched}/37 source fixtures including queued task; no frame-clock or route acceptance";
    }

    private static FieldInfo Field(string name) => typeof(AudioSequencer).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new MissingFieldException(name);
}
