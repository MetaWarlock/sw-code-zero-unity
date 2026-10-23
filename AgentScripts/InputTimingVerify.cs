using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using Darkwing.Core;

public static class InputTimingVerify
{
    public static string Run(string sourcePrefix = "Temp/human_rng17234/full_phases", string inputPath = "Playthroughs/20261022T164819Z/input.bin", string buffersPath = "Temp/human_164819_fulla/replay1/buffers.bin", int expectedHooks = 30681)
    {
        const string root = "D:/Art/SW Code Zero Unity/";
        sourcePrefix = Path.GetFullPath(Path.Combine(root, sourcePrefix));
        inputPath = Path.GetFullPath(Path.Combine(root, inputPath));
        buffersPath = Path.GetFullPath(Path.Combine(root, buffersPath));
        int combinations = 0;
        for (int raw = 0; raw < 256; raw++)
        for (int previous = 0; previous < 256; previous++)
        {
            Check((byte)raw, 0, (byte)previous, 0);
            Check(0, (byte)raw, 0, (byte)previous);
            Check((byte)raw, (byte)(raw ^ 0xFF), (byte)previous, (byte)(previous ^ 0xFF));
            combinations += 3;
        }

        MethodInfo pollMethod = typeof(DarkwingGame).GetMethod("PollInput", BindingFlags.NonPublic | BindingFlags.Instance);
        if (pollMethod == null) throw new InvalidOperationException("Production input poll is missing");
        var game = new DarkwingGame();
        foreach (byte raw in new byte[] { 0x05, 0x03, 0x05, 0x0C, 0x0F, 0xA3, 0xA1, 0xA0, 0 })
        {
            InputTiming.PollResult expected = InputTiming.Poll(raw, 0, game.HeldP1, 0);
            pollMethod.Invoke(game, new object[] { NesButtons.FromByte(raw) });
            if (game.HeldP1 != expected.HeldP1 || game.PressedP1 != expected.PressedP1 || game.LastInputHookCycles != expected.HookCycles)
                throw new InvalidOperationException("Production input differs from helper at " + raw.ToString("X2"));
        }

        byte[] input = File.ReadAllBytes(inputPath);
        byte[] buffers = File.ReadAllBytes(buffersPath);
        if (buffers.Length != input.Length * 4384) throw new InvalidDataException("Input/capture record count mismatch");
        var rows = ReadPhases(File.ReadLines(sourcePrefix + ".csv"), input.Length);
        int sourceHooks = VerifyHooks(rows, input, buffers, expectedHooks);
        int target = 0;
        foreach (var frame in rows)
        {
            var stamps = frame.Value;
            if (frame.Key >= 8 && stamps.ContainsKey("FECF") && stamps.ContainsKey("C162") && stamps.ContainsKey("CAE2")
                && stamps["C162"] - stamps["FECF"] == InputTiming.ResumeCallCycles)
            { target = frame.Key; break; }
        }
        long originalCycle = rows[target]["CAE2"];
        rows[target]["CAE2"]++;
        Reject(() => VerifyHooks(rows, input, buffers, expectedHooks), "one-cycle mismatch");
        rows[target]["CAE2"] = originalCycle;
        long removedCycle = rows[target]["C162"];
        rows[target].Remove("C162");
        Reject(() => VerifyHooks(rows, input, buffers, expectedHooks), "missing hook");
        rows[target]["C162"] = removedCycle;
        Reject(() => ReadPhases(AlterLines(sourcePrefix + ".csv", true), input.Length), "duplicate hook");
        Reject(() => ReadPhases(AlterLines(sourcePrefix + ".csv", false), input.Length), "missing poll");
        return "RESULT PASS input combinations=" + combinations + " production=9 sourceHookAndEpilogue=" + sourceHooks + " rejected=4 boundaries=C162-through-RTS,FECF-JSR6,FED2-through-RTS18";
    }

    private static Dictionary<int, Dictionary<string, long>> ReadPhases(IEnumerable<string> lines, int inputLength)
    {
        var rows = new Dictionary<int, Dictionary<string, long>>();
        var polls = new HashSet<int>();
        foreach (string line in lines)
        {
            if (line.StartsWith("poll,")) continue;
            string[] fields = line.Split(',');
            if (fields.Length != 9) throw new InvalidDataException("Unknown phase trace schema");
            int poll = int.Parse(fields[0]);
            if (fields[2] == "0000" && !polls.Add(poll)) throw new InvalidDataException("Duplicate input poll " + poll);
            if (!rows.ContainsKey(poll)) rows[poll] = new Dictionary<string, long>();
            if (fields[2] == "FECF" || fields[2] == "C162" || fields[2] == "CAE2")
            {
                if (rows[poll].ContainsKey(fields[2])) throw new InvalidDataException("Duplicate phase mark " + poll + " " + fields[2]);
                rows[poll][fields[2]] = long.Parse(fields[1]);
            }
        }
        if (polls.Count != inputLength + 1) throw new InvalidDataException("Phase capture poll count mismatch");
        for (int poll = 1; poll <= inputLength + 1; poll++)
            if (!polls.Contains(poll)) throw new InvalidDataException("Missing input poll " + poll);
        return rows;
    }

    private static int VerifyHooks(Dictionary<int, Dictionary<string, long>> rows, byte[] input, byte[] buffers, int expectedHooks)
    {
        int sourceHooks = 0;
        foreach (var frame in rows)
        {
            int poll = frame.Key;
            var stamps = frame.Value;
            if (poll < 8 || poll > input.Length || !stamps.ContainsKey("FECF") || !stamps.ContainsKey("C162") || !stamps.ContainsKey("CAE2")) continue;
            if (stamps["C162"] - stamps["FECF"] != InputTiming.ResumeCallCycles) continue;
            int record = (poll - 1) * 4384;
            InputTiming.PollResult result = InputTiming.Poll(input[poll - 1], 0, buffers[record + 0x16], buffers[record + 0x17]);
            long actual = stamps["CAE2"] - stamps["C162"];
            if (actual != result.HookCycles + InputTiming.ResumeEpilogueCycles)
                throw new InvalidOperationException("Source hook cost poll=" + poll + " actual=" + actual + " computed=" + (result.HookCycles + InputTiming.ResumeEpilogueCycles));
            sourceHooks++;
        }
        if (sourceHooks != expectedHooks) throw new InvalidDataException("Source hook count " + sourceHooks + " expected " + expectedHooks);
        return sourceHooks;
    }

    private static IEnumerable<string> AlterLines(string path, bool duplicate)
    {
        bool changed = false;
        foreach (string line in File.ReadLines(path))
        {
            if (!changed && line.Contains(duplicate ? ",C162," : ",0000,"))
            {
                changed = true;
                if (!duplicate) continue;
                yield return line;
            }
            yield return line;
        }
    }

    private static void Reject(Action operation, string name)
    {
        try { operation(); }
        catch (InvalidDataException) { return; }
        catch (InvalidOperationException) { return; }
        throw new InvalidOperationException("Accepted deliberate " + name);
    }

    private static void Check(byte rawP1, byte rawP2, byte previousP1, byte previousP2)
    {
        byte pressedP1 = (byte)(rawP1 & ~previousP1);
        byte pressedP2 = (byte)(rawP2 & ~previousP2);
        int expectedCycles = 525;
        foreach (byte buttons in new[] { rawP1, rawP2, pressedP1, pressedP2 })
            if ((buttons & 0x0C) != 0x0C && (buttons & 0x03) == 0x03) expectedCycles += 9;
        InputTiming.PollResult result = InputTiming.Poll(rawP1, rawP2, previousP1, previousP2);
        if (result.PressedP1 != Filter(pressedP1) || result.PressedP2 != Filter(pressedP2)
            || result.HeldP1 != Filter(rawP1) || result.HeldP2 != Filter(rawP2) || result.HookCycles != expectedCycles)
            throw new InvalidOperationException("Controller operation/cost mismatch");
    }

    private static byte Filter(byte buttons)
    {
        return (buttons & 0x0C) == 0x0C || (buttons & 0x03) == 0x03 ? (byte)(buttons & 0xF0) : buttons;
    }
}
