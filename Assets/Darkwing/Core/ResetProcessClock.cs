using System;

namespace Darkwing.Core
{
    public sealed class ResetProcessClock
    {
        public enum Phase { Waiting, Nmi, Body, Stopped, Unsupported }
        private readonly FrameScheduler _scheduler = new FrameScheduler();
        private int _publication, _timers;

        public ResetProcessClock(PowerOnContinuation power)
        {
            if (power == null || power.Active || power.ProcessState != FrameScheduler.Waiting || power.ProcessTimer != 1
                || power.CommitMaster < 0 || power.CommitMaster >= power.FirstEnabledNmiEdgeMaster)
                throw new ArgumentException("A completed native power-on yield before NMI is required", nameof(power));
            CpuOffset = (int)(power.CommitMaster % PowerOnContinuation.CpuMasterTicks);
            PpuOffset = (int)(power.FirstEnabledNmiEdgeMaster % PowerOnContinuation.PpuMasterTicks);
            PhysicalEdgeMaster = power.FirstEnabledNmiEdgeMaster;
            EdgeOrdinal = (PhysicalEdgeMaster - PpuOffset - PowerOnContinuation.FirstVblankMaster) / PowerOnContinuation.FullFrameMaster + 1;
            CommitCycle = (power.CommitMaster - CpuOffset) / PowerOnContinuation.CpuMasterTicks;
            _scheduler.BeginExecutionAfterCommit(0, CommitCycle, power.StackPointer);
        }

        public int CpuOffset { get; }
        public int PpuOffset { get; }
        public bool Supported => Unsupported == null;
        public bool Active => Stage != Phase.Stopped && Stage != Phase.Unsupported;
        public string Unsupported { get; private set; }
        public Phase Stage { get; private set; }
        public ushort InterruptedPc { get; private set; }
        public ushort StopPc { get; private set; }
        public long StopCycle { get; private set; } = -1;
        public byte ProcessState => _scheduler.State[0];
        public byte ProcessTimer => _scheduler.Timer[0];
        public long HandlerCycle { get; private set; }
        public long ResumeCycle { get; private set; }
        public long BodyStartCycle { get; private set; }
        public long CommitCycle { get; private set; }
        public long PhysicalEdgeMaster { get; private set; }
        public long EdgeOrdinal { get; private set; }
        public long LastNmiEdgeMaster { get; private set; }
        public long LastNmiOrdinal { get; private set; }
        public long PreRender339Master { get; private set; }
        public bool RenderingAt339 { get; private set; }
        public int MinimumCommitSlackCycles { get; private set; } = int.MaxValue;

        public bool BeginNmi(Func<long, int> publicationCycles)
        {
            if (Stage != Phase.Waiting) throw new InvalidOperationException("Reset clock NMI has no pending native yield");
            if (publicationCycles == null) throw new ArgumentNullException(nameof(publicationCycles));
            long threshold = (PhysicalEdgeMaster - CpuOffset + 11) / 12 + 1;
            while (!_scheduler.DispatchPending && _scheduler.ExecutionCycle < threshold) _scheduler.StepExecution();
            if (_scheduler.DispatchPending || _scheduler.State[0] != FrameScheduler.Waiting || _scheduler.Timer[0] != 1)
                return Reject("Reset clock lost Waiting before its NMI");
            InterruptedPc = _scheduler.ExecutionPc;
            HandlerCycle = _scheduler.ExecutionCycle + FrameScheduler.NmiEntryCycles;
            LastNmiEdgeMaster = PhysicalEdgeMaster;
            LastNmiOrdinal = EdgeOrdinal;
            PreRender339Master = LastNmiEdgeMaster + (20 * 341 + 337) * 4;
            try { _publication = publicationCycles(HandlerCycle); }
            catch (NotSupportedException exception) { return Refuse(exception.Message); }
            if (_publication < 0) return Reject("Reset native NMI publication cost unsupported");
            _timers = NmiTiming.ProcessTimerCycles(_scheduler.State, _scheduler.Timer);
            Stage = Phase.Nmi;
            return true;
        }

        public bool CompleteNmi(int audioServiceCycles)
        {
            if (Stage != Phase.Nmi) throw new InvalidOperationException("Reset clock has no entered NMI");
            if (audioServiceCycles < 0) return Reject("Reset native NMI audio cost unsupported");
            if ((HandlerCycle + _publication - 12) * 12 + CpuOffset >= PreRender339Master)
                return Reject("Reset NMI mask restore reaches pre-render339");
            int bank = NmiTiming.BankGateCycles(0, 0, out bool audioRuns);
            if (!audioRuns) return Reject("Reset completed bank-switch invariant failed");
            _scheduler.Nmi();
            long service = (long)FrameScheduler.NmiEntryCycles + _publication + _timers + bank + audioServiceCycles;
            if (service > int.MaxValue) return Refuse("Reset NMI service exceeds its clock range");
            _scheduler.ElapseInterrupt((int)service);
            long nextEarliest = LastNmiEdgeMaster + PowerOnContinuation.FullFrameMaster - 4;
            long nextThreshold = (nextEarliest - CpuOffset + 11) / 12 + 1;
            while (!_scheduler.DispatchPending && _scheduler.ExecutionCycle < nextThreshold) _scheduler.StepExecution();
            if (!_scheduler.DispatchPending || _scheduler.DispatchSlot != 0 || !_scheduler.DispatchNeedsInput)
                return Reject("Reset scheduler did not resume its input hook before the next NMI");
            ResumeCycle = _scheduler.ExecutionCycle;
            Stage = Phase.Body;
            return true;
        }

        public bool CommitBody(in ResetResidentTiming.BodyWork work, int inputHookCycles, ushort savedSp)
        {
            if (Stage != Phase.Body) throw new InvalidOperationException("Reset clock has no resumed body");
            if (work.Cycles < 0 || inputHookCycles < 0) return Reject("Reset native body price unsupported");
            if (savedSp != work.YieldStackPointer) throw new ArgumentException("Reset body stack owner mismatch", nameof(savedSp));
            BodyStartCycle = ResumeCycle + InputTiming.ResumeCallCycles + inputHookCycles + InputTiming.ResumeEpilogueCycles;
            long finish = BodyStartCycle + work.Cycles;
            RenderingAt339 = true;
            if (work.CopyrightSetupCycles >= 0)
            {
                long off = (BodyStartCycle + 6 + 5 + 2 + 3 + 4) * 12 + CpuOffset;
                long on = (BodyStartCycle + work.CopyrightSetupCycles - 6) * 12 + CpuOffset;
                if (PreRender339Master >= off && PreRender339Master < on) RenderingAt339 = false;
            }
            bool oddPreRender = ((LastNmiOrdinal + 1) & 1) != 0;
            long nextEdge = LastNmiEdgeMaster + PowerOnContinuation.FullFrameMaster - (RenderingAt339 && oddPreRender ? 4 : 0);
            if (finish * 12 + CpuOffset >= nextEdge) return Reject("Reset native body needs an NMI continuation before its next yield");
            if (!work.Commits)
            {
                _scheduler.ElapseInterrupt(checked((int)(finish - _scheduler.ExecutionCycle)));
                StopPc = 0xC5F7;
                StopCycle = finish;
                CommitCycle = -1;
                Stage = Phase.Stopped;
                return true;
            }
            CommitCycle = finish;
            MinimumCommitSlackCycles = Math.Min(MinimumCommitSlackCycles, checked((int)((nextEdge - CpuOffset + 11) / 12 + 1 - finish)));
            _scheduler.BeginExecutionAfterCommit(0, CommitCycle, savedSp);
            PhysicalEdgeMaster = nextEdge;
            EdgeOrdinal++;
            Stage = Phase.Waiting;
            return true;
        }

        private bool Reject(string reason)
        {
            Unsupported = reason;
            Stage = Phase.Unsupported;
            return false;
        }

        public bool Refuse(string reason)
        {
            if (string.IsNullOrEmpty(reason)) throw new ArgumentException("Unsupported reset-clock reason required", nameof(reason));
            return Reject(reason);
        }
    }
}
