using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;
using Darkwing.Audio;

public static class AudioTimingVerify
{
    public static string Tasks(string folder = "Art/Temp/audio_native_cost_tasks", int expectedCases = 119)
    {
        string root = "D:/Art/SW Code Zero Unity/";
        string path = Path.GetFullPath(Path.Combine(root, folder));
        byte[] before = File.ReadAllBytes(Path.Combine(path, "audio_task_before.bin"));
        byte[] after = File.ReadAllBytes(Path.Combine(path, "audio_task_after.bin"));
        string[] rows = File.ReadAllLines(Path.Combine(path, "audio_tasks.csv"));
        if (before.Length != after.Length || before.Length % 2048 != 0 || rows.Length != before.Length / 1024 + 1)
            throw new InvalidDataException("Incomplete task entry/exit captures");
        byte[] bank3 = File.ReadAllBytes(root + "Art/Unity/CodeZero/Darkwing/Data/audio_bank3.bytes");
        byte[] bank7 = File.ReadAllBytes(root + "Art/Unity/CodeZero/Darkwing/Data/world_bank7.bytes");
        var report = new StringBuilder("poll,id,expected,native,supported,stateDifferences\n");
        int matches = 0;
        int stateMatches = 0;
        int cases = before.Length / 2048;
        if (cases != expectedCases || rows[0] != "poll,id,kind,cycles,offset")
            throw new InvalidDataException("Missing/extra task cases or unknown schema");
        long previousEnd = -1;
        int previousPoll = -1;
        for (int index = 0; index < cases; index++)
        {
            string[] begin = rows[1 + index * 2].Split(',');
            string[] end = rows[2 + index * 2].Split(',');
            int offset = index * 2048;
            if (begin.Length != 5 || end.Length != 5 || int.Parse(begin[0]) < previousPoll
                || long.Parse(begin[3]) <= previousEnd || long.Parse(end[3]) <= long.Parse(begin[3]))
                throw new InvalidDataException("Duplicate/out-of-order task capture");
            if (begin[2] != "begin" || end[2] != "end" || begin[0] != end[0] || begin[1] != end[1] || int.Parse(begin[4]) != offset || int.Parse(end[4]) != offset)
                throw new InvalidDataException("Mismatched task boundaries");
            previousPoll = int.Parse(end[0]);
            previousEnd = long.Parse(end[3]);
            var audio = new AudioSequencer(bank3, bank7);
            for (int address = 0xC0; address <= 0xD8; address++)
                Field("_" + address.ToString("x2")).SetValue(audio, before[offset + address]);
            byte[] page = (byte[])Field("_p7").GetValue(audio);
            Array.Copy(before, offset + 0x700, page, 0, 256);
            audio.Task(int.Parse(begin[1]));
            int expected = checked((int)(long.Parse(end[3]) - long.Parse(begin[3]))) + 6;
            if (audio.TimingSupported && audio.LastTaskCycles == expected) matches++;
            int differences = 0;
            for (int address = 0xC7; address <= 0xD8; address++)
                if ((byte)Field("_" + address.ToString("x2")).GetValue(audio) != after[offset + address]) differences++;
            for (int address = 0; address < 256; address++)
                if (page[address] != after[offset + 0x700 + address]) differences++;
            if (differences == 0) stateMatches++;
            report.Append(begin[0]).Append(',').Append(begin[1]).Append(',').Append(expected).Append(',').Append(audio.LastTaskCycles)
                .Append(',').Append(audio.TimingSupported).Append(',').Append(differences).Append('\n');
        }
        File.WriteAllText(Path.Combine(path, "native_tasks.csv"), report.ToString());
        return string.Format("AudioTasks {0} cycles {1}/{2}; state C7-D8 + 0700-07FF {3}/{2}",
            matches == cases && stateMatches == cases ? "PASS" : "FAIL", matches, cases, stateMatches);
    }

    public static string Run(string folder = "Art/Temp/audio_native_cost_17234", int expectedCases = 37, int firstPoll = 17200)
    {
        if (UnityEditor.EditorApplication.isCompiling || UnityEditor.EditorUtility.scriptCompilationFailed)
            throw new InvalidOperationException("Refuse a stale audio assembly");
        string root = "D:/Art/SW Code Zero Unity/";
        string path = Path.GetFullPath(Path.Combine(root, folder));
        byte[] before = File.ReadAllBytes(Path.Combine(path, "audio_before.bin"));
        byte[] after = File.ReadAllBytes(Path.Combine(path, "audio_after.bin"));
        string[] rows = File.ReadAllLines(Path.Combine(path, "audio_ticks.csv"));
        if (before.Length != after.Length || before.Length % 2048 != 0 || rows.Length != before.Length / 1024 + 1)
            throw new InvalidDataException("Incomplete entry/exit captures");
        byte[] bank3 = File.ReadAllBytes(root + "Art/Unity/CodeZero/Darkwing/Data/audio_bank3.bytes");
        byte[] bank7 = File.ReadAllBytes(root + "Art/Unity/CodeZero/Darkwing/Data/world_bank7.bytes");
        var report = new StringBuilder("poll,expected,native,supported,stateDifferences\n");
        var details = new StringBuilder("poll,routine,cycles\n");
        int matches = 0;
        int stateMatches = 0;
        int cases = before.Length / 2048;
        if (cases != expectedCases || rows[0] != "poll,kind,cycles,offset")
            throw new InvalidDataException("Missing/extra tick cases or unknown schema");
        for (int index = 0; index < cases; index++)
        {
            string[] begin = rows[1 + index * 2].Split(',');
            string[] end = rows[2 + index * 2].Split(',');
            int offset = index * 2048;
            if (begin.Length != 4 || end.Length != 4 || int.Parse(begin[0]) != firstPoll + index
                || long.Parse(end[2]) <= long.Parse(begin[2]))
                throw new InvalidDataException("Missing/extra/duplicate tick poll");
            if (begin[1] != "begin" || end[1] != "end" || begin[0] != end[0] || int.Parse(begin[3]) != offset || int.Parse(end[3]) != offset)
                throw new InvalidDataException("Mismatched audio boundaries");
            var audio = new AudioSequencer(bank3, bank7);
            var totals = new Dictionary<string, int>();
            audio.TimingObserver = (routine, cycles) =>
            {
                totals.TryGetValue(routine, out int total);
                totals[routine] = total + cycles;
            };
            for (int address = 0xC0; address <= 0xD8; address++)
                Field("_" + address.ToString("x2")).SetValue(audio, before[offset + address]);
            byte[] page = (byte[])Field("_p7").GetValue(audio);
            Array.Copy(before, offset + 0x700, page, 0, 256);
            audio.Tick();
            foreach (var total in totals)
                details.Append(begin[0]).Append(',').Append(total.Key).Append(',').Append(total.Value).Append('\n');
            int expected = checked((int)(long.Parse(end[2]) - long.Parse(begin[2]))) - 9;
            if (audio.TimingSupported && audio.LastTickCycles == expected) matches++;
            int differences = 0;
            for (int address = 0xC7; address <= 0xD8; address++)
                if ((byte)Field("_" + address.ToString("x2")).GetValue(audio) != after[offset + address]) differences++;
            for (int address = 0; address < 256; address++)
                if (page[address] != after[offset + 0x700 + address]) differences++;
            if (differences == 0) stateMatches++;
            report.Append(begin[0]).Append(',').Append(expected).Append(',').Append(audio.LastTickCycles)
                .Append(',').Append(audio.TimingSupported).Append(',').Append(differences).Append('\n');
        }
        File.WriteAllText(Path.Combine(path, "native_timing.csv"), report.ToString());
        File.WriteAllText(Path.Combine(path, "native_timing_details.csv"), details.ToString());
        return string.Format("AudioTiming {0} cycles {1}/{2}; state C7-D8 + 0700-07FF {3}/{2}",
            matches == cases && stateMatches == cases ? "PASS" : "FAIL", matches, cases, stateMatches);
    }

    private static FieldInfo Field(string name)
    {
        return typeof(AudioSequencer).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new MissingFieldException(name);
    }
}
