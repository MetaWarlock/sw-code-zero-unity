using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using Darkwing.Core;

public static class PauseTimingVerify
{
    private sealed class Stamp
    {
        public int Poll, Entry;
        public long Cycle;
        public string Pc;
    }

    private static void Equal(int actual, long expected, string label)
    {
        if (actual != expected) throw new InvalidDataException(label + ": " + actual + "/" + expected);
    }

    public static string Run()
    {
        if (UnityEditor.EditorApplication.isCompiling || UnityEditor.EditorUtility.scriptCompilationFailed)
            throw new InvalidOperationException("Refuse stale Core");
        const string root = "D:/Art/SW Code Zero Unity/";
        byte[] entries = File.ReadAllBytes(root + "Temp/pause_clock_source/entry.bin");
        string[] lines = File.ReadAllLines(root + "Temp/pause_clock_source/stamps.csv");
        var stamps = new List<Stamp>();
        var boundaries = new Dictionary<string, long>();
        int entryCount = 0;
        for (int index = 1; index < lines.Length; index++)
        {
            string[] row = lines[index].Split(',');
            var stamp = new Stamp { Poll = int.Parse(row[0]), Cycle = long.Parse(row[1]), Pc = row[2], Entry = int.Parse(row[8]) };
            if (!boundaries.TryAdd(stamp.Poll + "/" + stamp.Pc, stamp.Cycle)) throw new InvalidDataException("Duplicate source boundary");
            if (stamp.Entry >= 0)
            {
                if (stamp.Entry != entryCount++) throw new InvalidDataException("Source entry order");
            }
            stamps.Add(stamp);
        }
        if (entries.Length != entryCount * 2048 || entryCount != 2098) throw new InvalidDataException("Source entries missing/extra");
        int plates = 0, enters = 0, restores = 0, negatives = 0;
        var phases = new HashSet<int>();
        foreach (var stamp in stamps)
        {
            if (stamp.Pc == "90E1" || stamp.Pc == "9148")
            {
                int slotIndex = stamp.Pc == "90E1" ? 5 : 4;
                int offset = stamp.Entry * 2048;
                var slot = new ObjectSystem.Slot
                {
                    StateLow = entries[offset + 0x4D0 + slotIndex],
                    X = (ushort)(entries[offset + 0x4F0 + slotIndex] | entries[offset + 0x500 + slotIndex] << 8),
                    Y = (ushort)(entries[offset + 0x510 + slotIndex] | entries[offset + 0x520 + slotIndex] << 8)
                };
                phases.Add(slot.StateLow & 15);
                long expected = boundaries[stamp.Poll + (slotIndex == 5 ? "/9148" : "/8FC4")] - stamp.Cycle - (slotIndex == 5 ? 6 : 0);
                int actual = PauseTiming.Plate(slot, slotIndex == 4);
                Equal(actual, expected, "Plate source block");
                bool rejected = false;
                try { Equal(actual, expected + 1, "Deliberate clock mismatch"); }
                catch (InvalidDataException) { rejected = true; }
                if (!rejected) throw new InvalidDataException("Clock negative accepted");
                negatives++;
                plates++;
            }
            else if (stamp.Pc == "8F00")
            {
                Equal(PauseTiming.Enter(entries[stamp.Entry * 2048 + 0x54]), boundaries[stamp.Poll + "/8FB7"] - stamp.Cycle, "Enter source block");
                enters++;
            }
            else if (stamp.Pc == "8FCD")
            {
                Equal(PauseTiming.Restore(), boundaries[stamp.Poll + "/CA40"] - stamp.Cycle, "Restore source block");
                restores++;
            }
        }
        if (plates != 1396 || enters != 1 || restores != 1 || phases.Count != 5) throw new InvalidDataException("Incomplete source pause branches");
        if (PauseTiming.Plate(new ObjectSystem.Slot { StateLow = 5 }, false) != -1
            || PauseTiming.Resume(525, 50, 50, false, 1) != -1 || PauseTiming.F89AToCommit(100, 1) != -1)
            throw new InvalidDataException("Unsupported guard accepted");
        var game = new DarkwingGame();
        game.Player.State = 0;
        game.Player.LevelActive = 0;
        game.Player.WeaponSelected = 3;
        SetAuto(game, "LastInputHookCycles", 525);
        SetAuto(game, "PressedP1", (byte)0x10);
        Invoke(game, "BeginPauseEntryTiming");
        Equal(game.LastPausePrefixCycles, 2182, "Native entry owner");
        var queue = (byte[])typeof(DarkwingGame).GetField("_audioQueue", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(game);
        queue[0] = 0;
        Invoke(game, "BeginPauseEntryTiming");
        Equal(game.LastPausePrefixCycles, 2167, "Actual enqueue full branch");
        game.Objects.Slots[5].X = 0xAC;
        game.Objects.Slots[4].X = 0x54;
        Invoke(game, "BeginPauseResumeTiming", false);
        Equal(game.LastPausePrefixCycles, 742, "Native resident prefix");
        Invoke(game, "BeginPauseResumeTiming", true);
        Equal(game.LastPausePrefixCycles, 1558, "Native restore prefix");
        Equal(game.LastPauseCommitCycles, -1, "Restore does not claim a commit");
        game.Player.TimingDeferredBankAudio = true;
        Invoke(game, "BeginPauseEntryTiming");
        if (game.LastPausePrefixCycles != -1 || game.PauseTimingUnsupported == null) throw new InvalidDataException("Production F6 guard");
        return "PASS pause source plates1396 enter1 restore1 phases5 clockNegatives1396 nativeHookBranches5 guards3. No physical anchor or route claim.";
    }

    private static void SetAuto(object target, string name, object value)
        => target.GetType().GetField("<" + name + ">k__BackingField", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(target, value);

    private static void Invoke(object target, string name, params object[] arguments)
        => target.GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic).Invoke(target, arguments);
}
