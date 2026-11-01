using System;
using Darkwing.Core;

public static class SchedulerExecutionVerify
{
    public static string MissionClockOutcomes()
    {
        int checks = 0;
        const long threshold = 100000;
        var measure = NewTail();
        while (measure.Busy) measure.AdvanceTo(measure.Cycles + 1);
        int busyEnd = measure.Cycles;
        measure.Finish();
        int total = measure.Cycles;
        Require(busyEnd > 30 && total > busyEnd, "real Busy/yield boundary", ref checks);
        int[] cuts = { busyEnd / 4, busyEnd / 3, busyEnd / 2 };

        var tail = NewTail();
        var clock = ClockAt(cuts, threshold);
        Require(clock.CompleteTail(tail) && clock.Stage == MissionClock.Phase.Running,
            "different instruction cuts, all Busy", ref checks);
        Require(clock.LastOutcome == MissionClock.TailResult.Busy && clock.Candidates == 3,
            "retain all Busy candidates", ref checks);
        Require(clock.ContinueNmi(2, 23, 57, threshold + 30000, out _), "blocked NMI resume", ref checks);
        Require(clock.MaxDeadline - clock.MinDeadline == cuts[2] - cuts[0],
            "resume retains deadline spread", ref checks);
        Require(clock.CompleteTail(tail) && clock.Stage == MissionClock.Phase.Waiting,
            "resumed candidates all commit", ref checks);
        long service = MissionClock.EntryCycles + MissionClock.BlockedPublicationCycles + 23 + MissionClock.BankGateCycles + 57;
        Require(clock.FirstCommit == threshold - cuts[2] + service + total &&
            clock.LastCommit == threshold - cuts[0] + service + total,
            "commit excludes interrupted-instruction offset", ref checks);

        // The multi-candidate continuation must finish at exactly the same times as independent
        // single-candidate executions, even though their interrupted instructions differ.
        var commits = (System.Collections.Generic.List<long>)ClockField("_commits").GetValue(clock);
        for (int i = 0; i < cuts.Length; i++)
        {
            var singleTail = NewTail();
            var single = ClockAt(new[] { cuts[i] }, threshold);
            Require(single.CompleteTail(singleTail) && single.Stage == MissionClock.Phase.Running,
                "single candidate Running " + i, ref checks);
            Require(single.ContinueNmi(2, 23, 57, threshold + 30000, out _) && single.CompleteTail(singleTail),
                "single candidate completion " + i, ref checks);
            Require(commits.Contains(single.FirstCommit), "single/multi commit equality " + i, ref checks);
        }

        var split = ClockAt(new[] { cuts[0], total + 1 }, threshold);
        Require(!split.CompleteTail(NewTail()) && split.Stage == MissionClock.Phase.Unsupported &&
            split.LastOutcome == MissionClock.TailResult.Split && split.OutcomeSplits == 1,
            "Busy/Committed split must refuse", ref checks);
        Require(split.Refusals == 1 && split.UnsupportedPoll == 1, "refusal remains observable", ref checks);
        split.ClearRefusal();
        Require(split.BeginNmi(3, threshold + 60000, 0, 0) && split.Anchors == 2 && split.Refusals == 1,
            "fresh admission after refusal without losing diagnostics", ref checks);

        var unbusy = ClockAt(new[] { busyEnd }, threshold);
        Require(!unbusy.CompleteTail(NewTail()) && unbusy.LastOutcome == MissionClock.TailResult.Unbusy,
            "Busy=0 before yield must refuse", ref checks);
        var committed = ClockAt(new[] { total + 1, total + 10, total + 100 }, threshold);
        Require(committed.CompleteTail(NewTail()) && committed.Stage == MissionClock.Phase.Waiting &&
            committed.RunningPasses == 0, "all committed does not invent lag", ref checks);
        return $"RESULT PASS mission clock outcomes checks={checks} busyEnd={busyEnd} total={total}";
    }

    private static MissionPassContinuation NewTail()
    {
        string root = System.IO.Directory.GetParent(UnityEngine.Application.dataPath).FullName;
        Func<string, byte[]> data = name => System.IO.File.ReadAllBytes(System.IO.Path.Combine(root,
            "Art/Unity/CodeZero/Darkwing/Data/" + name + ".bytes"));
        var game = new DarkwingGame(DataLoader.Load(data("chr_full"), data("levelsets"), data("screens"), data("levels"), data("anims")));
        EnemyBehaviors.RngE4 = 1; EnemyBehaviors.RngE5 = 2; EnemyBehaviors.RngE6 = 3; EnemyBehaviors.RngE7 = 4;
        var tail = new MissionPassContinuation(game.Oam, game.Player);
        tail.Begin(0);
        return tail;
    }

    private static System.Reflection.FieldInfo ClockField(string name) => typeof(MissionClock).GetField(name,
        System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);

    private static MissionClock ClockAt(int[] deadlines, long threshold)
    {
        var clock = new MissionClock();
        clock.BeginNmi(1, 0, 0, 0);
        clock.CompleteNmi(0, 0);
        var starts = (System.Collections.Generic.List<long>)ClockField("_bodyStarts").GetValue(clock);
        starts.Clear();
        foreach (int deadline in deadlines) starts.Add(threshold - deadline);
        if (!clock.BeginTail(0, threshold, out _)) throw new InvalidOperationException("Contract setup refused");
        return clock;
    }

    public static string Run()
    {
        int checks = 0;
        var scheduler = new FrameScheduler();
        scheduler.Reset();
        scheduler.BeginExecutionAfterCommit(0, 1000, 0x1FF);
        while (scheduler.ExecutionPc != 0xFE92) scheduler.StepExecution();
        Require(scheduler.ExecutionCycle == 1017, "FF19 to FE92", ref checks);
        long start = scheduler.ExecutionCycle;
        for (int index = 0; index < FrameScheduler.ScanInstructionStarts.Length; index++)
        {
            Require(scheduler.ExecutionCycle - start == FrameScheduler.ScanInstructionStarts[index], "scan instruction start " + index, ref checks);
            scheduler.StepExecution();
        }
        Require(scheduler.ExecutionPc == 0xFE92 && scheduler.ExecutionCycle == start + 93, "complete empty scan", ref checks);

        for (int phase = 0; phase < FrameScheduler.IdleScanCycles; phase++)
        {
            scheduler.Reset();
            scheduler.BeginExecution(0);
            scheduler.AdvanceExecutionTo(4 + phase + 1);
            ushort interruptedPc = scheduler.ExecutionPc;
            long acceptCycle = scheduler.ExecutionCycle;
            scheduler.Nmi();
            scheduler.ElapseInterrupt(1307);
            Require(scheduler.ExecutionPc == interruptedPc && scheduler.ExecutionCycle == acceptCycle + 1307, "suspend phase " + phase, ref checks);
            scheduler.AdvanceExecutionTo(scheduler.ExecutionCycle + 93);
            Require(!scheduler.DispatchPending, "empty resume phase " + phase, ref checks);
        }

        scheduler.Reset();
        scheduler.BeginExecutionAfterCommit(0, 0, 0x1FF);
        ToPc(scheduler, 0xFE9A);
        scheduler.Nmi();
        scheduler.ElapseInterrupt(2007);
        ToPc(scheduler, 0xFEA5);
        Require(!scheduler.DispatchPending, "FE98 state read survives NMI", ref checks);
        scheduler.AdvanceExecutionTo(scheduler.ExecutionCycle + 200);
        Require(scheduler.DispatchPending && scheduler.DispatchSlot == 0 && scheduler.DispatchNeedsInput, "next scan resumes ready slot", ref checks);

        scheduler.Reset();
        scheduler.State[0] = FrameScheduler.Ready;
        scheduler.BeginExecution(0);
        ToPc(scheduler, 0xFEAA);
        scheduler.Nmi();
        scheduler.ElapseInterrupt(2007);
        scheduler.AdvanceExecutionTo(scheduler.ExecutionCycle + 100);
        Require(scheduler.DispatchPending && scheduler.FrameGate, "FEA8 gate read survives NMI", ref checks);

        scheduler.Reset();
        scheduler.State[0] = FrameScheduler.Waiting;
        scheduler.Timer[0] = 0;
        scheduler.Nmi();
        Require(scheduler.Timer[0] == 0xFF && scheduler.State[0] == FrameScheduler.Waiting, "zero timer DEC wraps", ref checks);

        for (int slot = 0; slot < FrameScheduler.SlotCount; slot++)
        {
            foreach (byte state in new[] { FrameScheduler.Ready, FrameScheduler.New })
            {
                scheduler.Reset();
                scheduler.State[slot] = state;
                scheduler.BeginExecution(0);
                scheduler.AdvanceExecutionTo(1000);
                Require(scheduler.DispatchPending && scheduler.DispatchSlot == slot && scheduler.State[slot] == FrameScheduler.Running,
                    "dispatch slot " + slot, ref checks);
                Require(scheduler.DispatchIsNew == (state == FrameScheduler.New) && scheduler.DispatchNeedsInput == (slot == 0 && state == FrameScheduler.Ready),
                    "dispatch kind " + slot, ref checks);
            }
        }
        return "RESULT PASS scheduler execution checks=" + checks;
    }

    private static void ToPc(FrameScheduler scheduler, ushort pc)
    {
        int remaining = 1000;
        while (scheduler.ExecutionPc != pc && remaining-- > 0) scheduler.StepExecution();
        if (remaining <= 0) throw new InvalidOperationException("Scheduler did not reach " + pc.ToString("X4"));
    }

    private static void Require(bool result, string name, ref int checks)
    {
        if (!result) throw new InvalidOperationException("FAIL " + name);
        checks++;
    }
}
