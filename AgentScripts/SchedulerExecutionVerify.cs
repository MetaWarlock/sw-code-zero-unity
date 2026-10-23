using System;
using Darkwing.Core;

public static class SchedulerExecutionVerify
{
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
