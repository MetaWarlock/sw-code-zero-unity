using System;
using System.Collections.Generic;
using System.IO;
using Darkwing.Core;

public static class NmiTimingVerify
{
    public static string CardAndEarlyGate()
    {
        if (UnityEditor.EditorApplication.isCompiling || UnityEditor.EditorUtility.scriptCompilationFailed)
            throw new InvalidOperationException("Refuse stale Core");
        const string root = "D:/Art/SW Code Zero Unity/Temp/actor_early_source/";
        string[] records = File.ReadAllLines(root + "early.csv");
        byte[] entries = File.ReadAllBytes(root + "early_entry.bin");
        byte[] endings = File.ReadAllBytes(root + "early_end.bin");
        if (records.Length != 701 || entries.Length != 700 * 2048 || endings.Length != entries.Length)
            throw new InvalidDataException("Incomplete FAAE source fixture");
        int exact = 0, negatives = 0;
        for (int index = 1; index < records.Length; index++)
        {
            string[] row = records[index].Split(',');
            int targetIndex = int.Parse(row[1]);
            int expected = int.Parse(row[3]);
            int offset = (index - 1) * 2048;
            var objects = new ObjectSystem(null);
            for (int slotIndex = 0; slotIndex < 16; slotIndex++)
            {
                var state = objects.Slots[slotIndex];
                state.Active = (entries[offset + 0x4D0 + slotIndex] & 0x80) != 0;
                state.StateLow = (byte)(entries[offset + 0x4D0 + slotIndex] & 0x7F);
                state.Flags = entries[offset + 0x400 + slotIndex];
                state.Behavior = entries[offset + 0x4E0 + slotIndex];
            }
            int actual = ActorBank6Timing.ProjectileTargetEarlyNoHit(objects, objects.Slots[targetIndex]);
            if (actual != expected || actual < 83 || actual > 143)
                throw new InvalidOperationException("FAAE source cost " + row[0] + " " + actual + "/" + expected);
            exact++;
            if (actual != expected + 1) negatives++;
        }
        int low = int.MaxValue, high = 0;
        for (int signature = 0; signature < 64; signature++)
        {
            var objects = new ObjectSystem(null);
            objects.Slots[0].Active = false;
            objects.Slots[0].StateLow = 0;
            for (int slotIndex = 2; slotIndex <= 4; slotIndex++)
            {
                int kind = (signature >> (2 * (slotIndex - 2))) & 3;
                objects.Slots[slotIndex].Active = kind != 0;
                objects.Slots[slotIndex].Flags = kind >= 2 ? (byte)0x80 : (byte)0;
                objects.Slots[slotIndex].Behavior = kind == 2 ? (byte)0x1F : (byte)1;
            }
            int cycles = ActorBank6Timing.ProjectileTargetEarlyNoHit(objects, objects.Slots[0]);
            low = Math.Min(low, cycles); high = Math.Max(high, cycles);
        }
        if (low != 83 || high != 143) throw new InvalidOperationException("FAAE branch bound");
        var rejectedTarget = new ObjectSystem.Slot { StateLow = 1 };
        bool refused = false;
        try { ActorBank6Timing.ProjectileTargetEarlyNoHit(new ObjectSystem(null), rejectedTarget); }
        catch (NotSupportedException) { refused = true; }
        if (!refused) throw new InvalidOperationException("Unverified nonzero target priced");
        int[] hudCycles = { 354, 373, 377, 378 };
        for (byte weapon = 0; weapon < 4; weapon++)
            if (PlayerCore.MeasureCardHudSeedCycles(weapon) != hudCycles[weapon])
                throw new InvalidOperationException("C850 weapon branch cost");
        return "PASS FAAE source=" + exact + "/700 clock negatives=" + negatives
            + "; 64 gate combinations bound83..143, nonzero target rejected, C850 four weapon branches";
    }

    public static string Run()
    {
        const string root = "D:/Art/SW Code Zero Unity/";
        byte[] original = File.ReadAllBytes(root + "Temp/human_164819_fulla/replay1/buffers.bin");
        if (original.Length != 38886 * 4384) throw new Exception("Incomplete original capture");
        var starts = new Dictionary<int, long>();
        var checkedGates = new HashSet<int>();
        int publications = 0, timers = 0, gates = 0, returns = 0, negatives = 0;
        foreach (string line in File.ReadLines(root + "Temp/human_rng17234/full_phases.csv"))
        {
            string[] fields = line.Split(',');
            if (fields.Length != 9 || !int.TryParse(fields[0], out int poll) || poll < 8 || poll > 38886) continue;
            long cycle = long.Parse(fields[1]);
            int offset = (poll - 1) * 4384;
            byte Read(int address) => original[offset + address];
            if (fields[2] == "C000") { starts[poll] = cycle; continue; }
            if (!starts.TryGetValue(poll, out long handlerCycle)) continue;
            if (fields[2] == "C0A1")
            {
                var work = new NmiTiming.Publication(Read(0xF0), Read(0x9A), Read(0x1C), Read(0x1D),
                    Read(0x19), Read(0x1A), Read(0x1B), Read(0x18), Read);
                int expected = checked((int)(cycle - handlerCycle));
                int actual = NmiTiming.PublicationCycles(in work, handlerCycle);
                if (expected != actual) throw new Exception($"Publication {poll}: source {expected}, native {actual}");
                bool publicationBlocked = (Read(0xF0) | Read(0x9A)) != 0;
                var altered = new NmiTiming.Publication(0, 0, Read(0x1C), Read(0x1D), Read(0x19),
                    Read(0x1A), Read(0x1B), publicationBlocked ? Read(0x18) : Read(0x18) == 0 ? (byte)0xFF : (byte)0, Read);
                if (NmiTiming.PublicationCycles(in altered, handlerCycle) == expected)
                    throw new Exception("Relevant publication mismatch escaped at " + poll);
                negatives++;
                publications++;
                starts[-poll] = cycle;
            }
            else if (fields[2] == "C0BF")
            {
                var state = new byte[4]; var timer = new byte[4];
                for (int slot = 0; slot < 4; slot++) { state[slot] = Read(0x80 + slot * 4); timer[slot] = Read(0x81 + slot * 4); }
                int actual = NmiTiming.ProcessTimerCycles(state, timer);
                if (cycle - starts[-poll] != actual) throw new Exception($"Timer {poll}: source {cycle - starts[-poll]}, native {actual}");
                timers++; starts[-poll] = cycle;
            }
            else if (fields[2] == "C0DC" && checkedGates.Add(poll))
            {
                int actual = NmiTiming.BankGateCycles(Read(0xF5), Read(0xF6), out bool audioRuns);
                if (!audioRuns || cycle - starts[-poll] != actual) throw new Exception("Bank gate " + poll);
                gates++;
            }
            else if (fields[2] == "C0F9") starts[-poll] = cycle;
            else if (fields[2] == "C114" && Read(0xF5) == 0)
            {
                if (cycle - starts[-poll] != NmiTiming.RestoreBankAndReturnCycles - 6) throw new Exception("NMI return " + poll);
                returns++;
            }
        }
        if (publications != 38879 || timers != publications || gates != 38877 || returns != 38877 || negatives != publications)
            throw new Exception("Incomplete NMI fixture coverage");
        var quiet = new NmiTiming.Publication(0, 0, 0, 0, 0, 0, 0, 0, address => 0xFF);
        if (NmiTiming.PublicationCycles(in quiet, 0) != NmiTiming.PublicationCycles(in quiet, 1) + 1)
            throw new Exception("DMA parity control");
        var blocked = new NmiTiming.Publication(0, 1, 0xFF, 0xFF, 0xFF, 0x80, 0x90, 0xFF,
            address => throw new Exception("Blocked NMI read VRAM descriptor"));
        if (NmiTiming.PublicationCycles(in blocked, 0) != 27) throw new Exception("Bracket gate control");
        if (NmiTiming.BankGateCycles(1, 0, out bool runs) != 13 || runs) throw new Exception("Deferred bank skip");
        if (NmiTiming.BankGateCycles(1, 0xFF, out runs) != 42 || !runs) throw new Exception("Deferred bank wrap");
        return $"PASS publication={publications} timer={timers} bank-gate={gates} return={returns} rejected={negatives}; DMA/bracket/bank controls; source CPU fixtures only";
    }
}
