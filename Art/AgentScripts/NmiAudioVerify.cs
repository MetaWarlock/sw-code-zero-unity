using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;
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

    public static string RouteCases(string projectRoot, string ramPath, string probePath,
        int firstPoll, int lastPoll, string outputDirectory)
    {
        if (UnityEditor.EditorApplication.isCompiling || UnityEditor.EditorUtility.scriptCompilationFailed)
            throw new InvalidOperationException("Refuse a stale audio assembly");
        string root = Path.GetFullPath(projectRoot);
        string temp = Path.GetFullPath(Path.Combine(root, "Art", "Temp"));
        string output = Path.GetFullPath(outputDirectory);
        if (!output.StartsWith(temp + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Route audio output must be inside the assigned Art/Temp");
        string reportPath = Path.Combine(output, "route_audio.csv");
        string detailsPath = Path.Combine(output, "route_audio_details.csv");
        if (File.Exists(reportPath) || File.Exists(detailsPath)) throw new InvalidDataException("Route audio output already exists");
        byte[] ram = File.ReadAllBytes(Path.GetFullPath(ramPath));
        if (ram.Length == 0 || ram.Length % 2048 != 0 || firstPoll < 1 || lastPoll < firstPoll
            || lastPoll >= ram.Length / 2048) throw new InvalidDataException("Incomplete route RAM or invalid inclusive poll range");
        var timings = new Dictionary<int, long[]>();
        foreach (string line in File.ReadLines(Path.GetFullPath(probePath)))
        {
            string[] fields = line.Split(',');
            if (fields.Length != 16 || !int.TryParse(fields[1], out int poll) || poll < firstPoll || poll > lastPoll) continue;
            if (!timings.TryGetValue(poll, out var timing)) timings[poll] = timing = new long[] { -1, -1, -1, -1, -1, -1 };
            long cycle = long.Parse(fields[2]);
            if (fields[0] == "nmi_accept") timing[0] = cycle;
            else if (fields[3] == "C000" && fields[4] == "07") timing[1] = cycle;
            else if (fields[3] == "8000" && fields[4] == "03" && timing[2] < 0) timing[2] = cycle;
            else if (fields[3] == "C114" && fields[4] == "07") timing[3] = cycle;
            else if (fields[3] == "CABB" && fields[4] == "07") timing[4] = cycle;
            else if (fields[3] == "CAD8" && fields[4] == "07") timing[5] = cycle;
        }
        byte[] bank3 = File.ReadAllBytes(Path.Combine(root, "Art/Unity/CodeZero/Darkwing/Data/audio_bank3.bytes"));
        byte[] bank7 = File.ReadAllBytes(Path.Combine(root, "Art/Unity/CodeZero/Darkwing/Data/world_bank7.bytes"));
        var drain = typeof(DarkwingGame).GetMethod("DrainAudioQueue", BindingFlags.Instance | BindingFlags.NonPublic);
        if (drain == null) throw new MissingMethodException("Native NMI audio seam");
        var report = new StringBuilder("poll,cTickReturn,nativeTickReturn,nativeFullService,supported,stateDifferences,lastTask,lastTick,cNmi,cPreAudio,cBodyPrefix,cTailToCad8,queued,fullTaskBoundary\n");
        var details = new StringBuilder("poll,routine,cycles\n");
        int exact = 0, stateExact = 0, unsupported = 0;
        for (int poll = firstPoll; poll <= lastPoll; poll++)
        {
            if (!timings.TryGetValue(poll, out var timing) || timing[1] < 0 || timing[2] < 0 || timing[3] < 0)
                throw new InvalidDataException("Missing C000/bank3-8000/C114 boundaries at poll " + poll);
            int beforeOffset = (poll - 1) * 2048, afterOffset = poll * 2048;
            var game = new DarkwingGame { Bank7 = bank7, Bank3 = bank3 };
            var audio = game.Audio;
            var totals = new Dictionary<string, int>();
            audio.TimingObserver = (routine, cycles) =>
            {
                totals.TryGetValue(routine, out int total);
                totals[routine] = total + cycles;
            };
            for (int address = 0xC0; address <= 0xD8; address++)
                Field("_" + address.ToString("x2")).SetValue(audio, ram[beforeOffset + address]);
            Array.Copy(ram, beforeOffset + 0x700, (byte[])Field("_p7").GetValue(audio), 0, 256);
            const BindingFlags binding = BindingFlags.Instance | BindingFlags.NonPublic;
            typeof(DarkwingGame).GetField("_audioWrite", binding).SetValue(game, (int)ram[beforeOffset + 0xDA]);
            typeof(DarkwingGame).GetField("_audioRead", binding).SetValue(game, (int)ram[beforeOffset + 0xDB]);
            Array.Copy(ram, beforeOffset + 0xDC, (byte[])typeof(DarkwingGame).GetField("_audioQueue", binding).GetValue(game), 0, 8);
            var queued = new StringBuilder();
            int queuedCount = 0;
            for (int address = 0xDC; address <= 0xE3; address++)
                if (ram[beforeOffset + address] != 0x88)
                {
                    queued.Append(ram[beforeOffset + address].ToString("X2")).Append(';');
                    queuedCount++;
                }
            if (queuedCount > 1) throw new NotSupportedException("Multiple queued tasks need individual C task boundaries");
            drain.Invoke(game, null);
            int expectedTickReturn = checked((int)(timing[3] - timing[2] + 24));
            int nativeTickReturn = game.LastNmiAudioServiceCycles - (queuedCount == 1 ? 45 + audio.LastTaskCycles : 0);
            int differences = 0;
            for (int address = 0xC7; address <= 0xD8; address++)
                if ((byte)Field("_" + address.ToString("x2")).GetValue(audio) != ram[afterOffset + address]) differences++;
            byte[] nativePage = (byte[])Field("_p7").GetValue(audio);
            for (int address = 0; address < 256; address++)
                if (nativePage[address] != ram[afterOffset + 0x700 + address]) differences++;
            bool supported = game.NmiAudioTimingSupported;
            if (supported && nativeTickReturn == expectedTickReturn) exact++;
            if (differences == 0) stateExact++;
            if (!supported) unsupported++;
            report.Append(poll).Append(',').Append(expectedTickReturn).Append(',').Append(nativeTickReturn)
                .Append(',').Append(game.LastNmiAudioServiceCycles).Append(',').Append(supported).Append(',').Append(differences).Append(',')
                .Append(audio.LastTaskCycles).Append(',').Append(audio.LastTickCycles).Append(',')
                .Append(timing[3] - timing[1]).Append(',').Append(timing[2] - timing[1]).Append(',')
                .Append(timing[4] < 0 ? "" : (timing[4] - timing[3]).ToString()).Append(',')
                .Append(timing[4] < 0 || timing[5] < 0 ? "" : (timing[5] - timing[4]).ToString()).Append(',')
                .Append(queued).Append(',').Append(queuedCount == 0 ? "not-required" : "missing-C0DC-C0F6").Append('\n');
            foreach (var total in totals) details.Append(poll).Append(',').Append(total.Key).Append(',').Append(total.Value).Append('\n');
        }
        Directory.CreateDirectory(output);
        File.WriteAllText(reportPath, report.ToString());
        File.WriteAllText(detailsPath, details.ToString());
        int cases = lastPoll - firstPoll + 1;
        return $"{(exact == cases && stateExact == cases && unsupported == 0 ? "PASS" : "FAIL")} route audio tick/return exact={exact}/{cases} state={stateExact}/{cases} unsupported={unsupported}; full queued-task boundary UNKNOWN without C0DC/C0F6; details={reportPath}";
    }

    private static FieldInfo Field(string name) => typeof(AudioSequencer).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new MissingFieldException(name);
}
