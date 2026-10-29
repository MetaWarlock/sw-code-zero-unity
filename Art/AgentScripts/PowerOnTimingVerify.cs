using System;
using System.Collections.Generic;
using System.IO;
using Darkwing.Core;

public static class PowerOnTimingVerify
{
    private const string Root = "D:/Art/SW Code Zero Unity/";

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private static void Reject(Action action)
    {
        bool rejected = false;
        try { action(); }
        catch (InvalidOperationException) { rejected = true; }
        if (!rejected) throw new InvalidOperationException("Power-on negative control accepted");
    }

    private static void CompareReads(List<PowerOnContinuation.StatusRead> actual, List<PowerOnContinuation.StatusRead> expected)
    {
        Require(actual.Count == expected.Count, "Power-on status count");
        for (int index = 0; index < actual.Count; index++)
        {
            var native = actual[index];
            var source = expected[index];
            Require(native.Pc == source.Pc && native.PpuMaster == source.PpuMaster && native.Value == source.Value,
                "Power-on status read " + index);
        }
    }

    private static void Profile(string settings)
    {
        foreach (string value in new[] {
            "MesenVersion 2.1.1", "nes.consoleType Nes001", "nes.randomizeCpuPpuAlignment false",
            "nes.randomizeMapperPowerOnState false", "nes.disablePpuReset false", "nes.restrictPpuAccessOnFirstFrame false",
            "nes.ppuExtraScanlinesAfterNmi 0", "nes.ppuExtraScanlinesBeforeNmi 0" })
            Require(("\n" + settings.Replace("\r", "") + "\n").Contains("\n" + value + "\n"), "Power-on movie profile " + value);
    }

    public static string Run()
    {
        if (UnityEditor.EditorApplication.isCompiling || UnityEditor.EditorUtility.scriptCompilationFailed)
            throw new InvalidOperationException("Refuse stale Core");
        string profile;
        using (var file = File.OpenRead(Root + "Art/Playthroughs/20261022T164819Z/movie.mmo"))
        {
            var archiveType = Type.GetType("System.IO.Compression.ZipArchive, System.IO.Compression", true);
            var modeType = Type.GetType("System.IO.Compression.ZipArchiveMode, System.IO.Compression", true);
            using (var archive = (IDisposable)Activator.CreateInstance(archiveType, file, Enum.Parse(modeType, "Read")))
            {
                object entry = archiveType.GetMethod("GetEntry").Invoke(archive, new object[] { "GameSettings.txt" });
                using (var reader = new StreamReader((Stream)entry.GetType().GetMethod("Open").Invoke(entry, null)))
                    profile = reader.ReadToEnd();
            }
        }
        Profile(profile);
        Reject(() => Profile(profile.Replace("nes.randomizeCpuPpuAlignment false", "nes.randomizeCpuPpuAlignment true")));
        string source = Root + "Art/Temp/reset_anchor_source1/";
        foreach (string name in new[] { "status_reads.csv", "anchor.csv", "ppu_writes.csv" })
            Require(File.ReadAllText(source + name) == File.ReadAllText(Root + "Art/Temp/reset_anchor_source2/" + name), "Independent cold source mismatch");
        var expectedReads = new List<PowerOnContinuation.StatusRead>();
        foreach (string line in File.ReadAllLines(source + "status_reads.csv"))
        {
            string[] cells = line.Split(',');
            if (cells[2] != "FE0E" && cells[2] != "FE13") continue;
            expectedReads.Add(new PowerOnContinuation.StatusRead(Convert.ToUInt16(cells[2], 16), 0,
                long.Parse(cells[6]), (Convert.ToByte(cells[3], 16) & 0x80) != 0));
        }
        Require(expectedReads.Count == 16671, "Incomplete cold status capture");
        var reads = new List<PowerOnContinuation.StatusRead>();
        var checkpoints = new List<PowerOnContinuation.Checkpoint>();
        var native = new PowerOnContinuation(blockReached: checkpoints.Add, readObserved: reads.Add);
        native.Finish();
        CompareReads(reads, expectedReads);
        Require(native.InstructionPc == 0xFF19 && native.Stage == PowerOnContinuation.Phase.Committed && !native.Active
            && native.ProcessState == FrameScheduler.Waiting && native.ProcessTimer == 1 && native.StackPointer == 0xBB,
            "Power-on real yield context");
        Require(native.HardwareMask == 0 && native.NmiEnabled && native.CommitMaster < native.FirstEnabledNmiEdgeMaster,
            "Power-on hardware gate");
        var sourceBoundaries = new Dictionary<ushort, long>();
        var successfulBoundaries = new List<long>();
        foreach (string line in File.ReadAllLines(source + "anchor.csv"))
        {
            string[] cells = line.Split(',');
            if (cells[0] == "poll") continue;
            ushort pc = Convert.ToUInt16(cells[2], 16);
            if (pc == 0xFE11) successfulBoundaries.Add(long.Parse(cells[1]) * 12 + 24);
            if (!sourceBoundaries.ContainsKey(pc)) sourceBoundaries[pc] = long.Parse(cells[1]) * 12 + 24;
        }
        var actualBoundaries = new Dictionary<ushort, long>();
        int success = 0;
        foreach (var point in checkpoints)
        {
            if (point.Pc == 0xFE11)
            {
                Require(success < successfulBoundaries.Count && point.MasterClock == successfulBoundaries[success++], "Power-on successful read boundary");
                continue;
            }
            actualBoundaries[point.Pc] = point.MasterClock;
            if (sourceBoundaries.TryGetValue(point.Pc, out long expected)) Require(point.MasterClock == expected, "Power-on block " + point.Pc.ToString("X4"));
        }
        Require(success == 4 && native.SuccessfulStatusReads == 4, "Power-on four successful reads");
        actualBoundaries[0xFE11] = native.LastSuccessfulReadBoundaryMaster;
        sourceBoundaries[0xFE11] = successfulBoundaries[3];
        ushort[] blockStarts = { 0xFE11, 0xFE26, 0xFE41, 0xFE69, 0xFE7E, 0xC595 };
        ushort[] blockEnds = { 0xFE26, 0xFE41, 0xFE69, 0xFE7E, 0xC595, 0xFF19 };
        int negatives = 1;
        for (int block = 0; block < blockStarts.Length; block++)
        {
            long actual = actualBoundaries[blockEnds[block]] - actualBoundaries[blockStarts[block]];
            long expected = sourceBoundaries[blockEnds[block]] - sourceBoundaries[blockStarts[block]];
            Require(actual == expected, "Power-on block cost");
            Reject(() => Require(actual + 12 == expected, "Changed block cost"));
            negatives++;
        }
        foreach (int index in new[] { 0, reads.FindIndex(value => value.Value), reads.Count - 1 })
        {
            var changed = new List<PowerOnContinuation.StatusRead>(reads);
            var before = changed[index];
            changed[index] = new PowerOnContinuation.StatusRead(before.Pc, before.BusMaster, before.PpuMaster + 4, before.Value);
            Reject(() => CompareReads(changed, expectedReads));
            changed[index] = new PowerOnContinuation.StatusRead(before.Pc, before.BusMaster, before.PpuMaster, !before.Value);
            Reject(() => CompareReads(changed, expectedReads));
            negatives += 2;
        }
        var missing = new List<PowerOnContinuation.StatusRead>(reads);
        missing.RemoveAt(0);
        Reject(() => CompareReads(missing, expectedReads));
        missing.Add(reads[0]);
        missing.Add(reads[0]);
        Reject(() => CompareReads(missing, expectedReads));
        negatives += 2;
        int alternatives = 0, suppressed = 0;
        long minimumMargin = long.MaxValue;
        for (int cpu = 0; cpu < 12; cpu++)
        for (int ppu = 0; ppu < 4; ppu++)
        {
            var phase = new PowerOnContinuation(cpu, ppu);
            while (phase.Active) phase.AdvanceToMaster(phase.MasterClock + 1);
            Require(phase.ProcessState == FrameScheduler.Waiting && phase.ProcessTimer == 1 && phase.SuccessfulStatusReads == 4
                && phase.CommitMaster < phase.FirstEnabledNmiEdgeMaster && phase.NmiEnableMaster < phase.CommitMaster,
                "Power-on phase commit");
            alternatives++;
            if (phase.SuppressedVblanks != 0) suppressed++;
            minimumMargin = Math.Min(minimumMargin, phase.FirstEnabledNmiEdgeMaster - phase.CommitMaster);
        }
        Require(alternatives == 48 && suppressed == 3, "Power-on full raw phase inventory");
        for (int split = 0; split < 20; split++)
        {
            var phase = new PowerOnContinuation();
            phase.AdvanceToMaster(native.CommitMaster * split / 20);
            Require(phase.MasterClock < native.CommitMaster || phase.ProcessState == FrameScheduler.Waiting, "Power-on premature wait");
            phase.Finish();
            phase.Finish();
            Require(phase.CommitMaster == native.CommitMaster && phase.StackPointer == native.StackPointer && !phase.Active, "Power-on split/idempotence");
        }
        return $"PASS PowerOn typed status reads={reads.Count}; successBoundaries={success}; sourceBlocks={blockStarts.Length}; rawAlignments={alternatives}; suppressedAlignments={suppressed}; minCommitMarginMaster={minimumMargin}; negativeControls={negatives}; splits=20. Canonical cold-reset profile from original movie2.1.1; no source clocks/state as runtime inputs. Fixed RESET through FF17 only, not Game/flow or whole RAM/PPU-image acceptance.";
    }
}
