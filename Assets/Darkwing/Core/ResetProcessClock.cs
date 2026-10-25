using System;

namespace Darkwing.Core
{
    public sealed class ResetProcessClock
    {
        public enum Phase { Waiting, Nmi, Body, Running, Stopped, Unsupported }
        private readonly FrameScheduler _scheduler = new FrameScheduler();
        private int _publication, _timers;
        private long _streamCycle;
        private bool _nmiFromRunning;
        private byte _switchDepth, _deferred;
        private ushort _streamInterruptedPc;

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

        private long NmiThreshold(long edgeMaster) => (edgeMaster - CpuOffset + 11) / 12 + 1;

        /// <summary>True while the resident is inside the loader's in-line $DE86/$E698
        /// loop: slot 0 stays Running across the NMI instead of committing a yield.</summary>
        public bool Streaming => Stage == Phase.Running;

        /// <summary>CPU cycle at the loader body's next instruction boundary. Pass-level
        /// granularity: every priced native instruction completes before NMI acceptance.</summary>
        public long StreamCycle => _streamCycle;

        /// <summary>CPU cycles the loader may still execute before the next NMI edge.
        /// Zero outside the stream or once an instruction reached its edge.</summary>
        public long StreamBudgetCycles
        {
            get
            {
                if (Stage != Phase.Running) return 0;
                long budget = NmiThreshold(PhysicalEdgeMaster) - _streamCycle;
                return budget > 0 ? budget : 0;
            }
        }

        /// <summary>$DD5D-$DE86 runs at the resumed input hook, then $DE24's in-line
        /// $DE86/$DE91 loop never yields: slot 0 stays Running until the $DDCB yield.
        /// $C14E blanks rendering inside the prelude, so the next edge is a whole frame.</summary>
        public bool BeginStreaming(in ResetResidentTiming.BodyWork prelude, int inputHookCycles, ushort savedSp)
        {
            if (Stage != Phase.Body) throw new InvalidOperationException("Reset clock has no resumed body");
            if (prelude.Cycles < 0 || inputHookCycles < 0) return Reject("Reset native loader price unsupported");
            if (savedSp != prelude.YieldStackPointer) throw new ArgumentException("Reset loader stack owner mismatch", nameof(savedSp));
            BodyStartCycle = ResumeCycle + InputTiming.ResumeCallCycles + inputHookCycles + InputTiming.ResumeEpilogueCycles;
            _streamCycle = BodyStartCycle + prelude.Cycles;
            PhysicalEdgeMaster = LastNmiEdgeMaster + PowerOnContinuation.FullFrameMaster;
            if (_streamCycle >= NmiThreshold(PhysicalEdgeMaster))
                return Reject("Reset loader prelude needs an NMI continuation before $DE86");
            RenderingAt339 = false;
            // Slot 0 stays Running across the in-line $DE86 loop; the scheduler resumes
            // exactly at the loop head and NMI service leaves that state alone.
            _scheduler.BeginRunning(_streamCycle, 0xDE86);
            Stage = Phase.Running;
            return true;
        }

        /// <summary>Charge loader passes against the current frame. The caller may fill the
        /// budget at a real instruction boundary, at most six cycles past the threshold.</summary>
        public void AdvanceStream(int cycles, ushort nextPc = 0)
        {
            if (Stage != Phase.Running) throw new InvalidOperationException("Reset loader is not streaming");
            if (cycles < 0) throw new ArgumentOutOfRangeException(nameof(cycles));
            if (_streamCycle + cycles > NmiThreshold(PhysicalEdgeMaster)+6)
                throw new InvalidOperationException("Reset loader stream crossed its next NMI edge");
            _streamCycle += cycles;
            _streamInterruptedPc=nextPc;
        }

        /// <summary>$DE93/$DEA5/$DEA9 and the $DDCB JSR $FF06 after the loop reaches $0F.
        /// The completion keeps rendering off (only $DE00 turns it back on), so the next
        /// edge is a full frame again.</summary>
        public bool CompleteStream(in ResetResidentTiming.BodyWork completion, ushort savedSp)
        {
            if (Stage != Phase.Running) throw new InvalidOperationException("Reset loader is not streaming");
            if (completion.Cycles < 0) return Reject("Reset loader completion price unsupported");
            if (!completion.Commits) return Reject("Reset loader completion must commit its yield");
            if (savedSp != completion.YieldStackPointer) throw new ArgumentException("Reset loader completion stack owner mismatch", nameof(savedSp));
            long finish = _streamCycle + completion.Cycles;
            long threshold = NmiThreshold(PhysicalEdgeMaster);
            if (finish >= threshold) return Reject("Reset loader completion needs an NMI continuation before its yield");
            CommitCycle = finish;
            MinimumCommitSlackCycles = Math.Min(MinimumCommitSlackCycles, checked((int)(threshold - finish)));
            _scheduler.BeginExecutionAfterCommit(0, CommitCycle, savedSp);
            PhysicalEdgeMaster = LastNmiEdgeMaster + PowerOnContinuation.FullFrameMaster;
            EdgeOrdinal++;
            Stage = Phase.Waiting;
            return true;
        }

        public bool BeginNmi(Func<long, int> publicationCycles, byte switchDepth=0, byte deferred=0)
        {
            _switchDepth=switchDepth;_deferred=deferred;
            if (publicationCycles == null) throw new ArgumentNullException(nameof(publicationCycles));
            if (Stage == Phase.Running) return BeginStreamingNmi(publicationCycles);
            if (Stage != Phase.Waiting) throw new InvalidOperationException("Reset clock NMI has no pending native yield");
            long threshold = NmiThreshold(PhysicalEdgeMaster);
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

        /// <summary>NMI enters after the last completed instruction. Its exact next
        /// PC and live bank-switch state survive until RTI resumes the same body.</summary>
        private bool BeginStreamingNmi(Func<long, int> publicationCycles)
        {
            long threshold = NmiThreshold(PhysicalEdgeMaster);
            if (_streamCycle < threshold || _streamCycle > threshold+6) return Reject("Reset loader lacks a reached NMI instruction boundary");
            InterruptedPc = _streamInterruptedPc;
            HandlerCycle = _streamCycle + FrameScheduler.NmiEntryCycles;
            LastNmiEdgeMaster = PhysicalEdgeMaster;
            LastNmiOrdinal = EdgeOrdinal;
            PreRender339Master = LastNmiEdgeMaster + (20 * 341 + 337) * 4;
            try { _publication = publicationCycles(HandlerCycle); }
            catch (NotSupportedException exception) { return Refuse(exception.Message); }
            if (_publication < 0) return Reject("Reset native NMI publication cost unsupported");
            _timers = NmiTiming.ProcessTimerCycles(_scheduler.State, _scheduler.Timer);
            _nmiFromRunning = true;
            Stage = Phase.Nmi;
            return true;
        }

        public bool CompleteNmi(int audioServiceCycles)
        {
            if (Stage != Phase.Nmi) throw new InvalidOperationException("Reset clock has no entered NMI");
            if (audioServiceCycles < 0) return Reject("Reset native NMI audio cost unsupported");
            if ((HandlerCycle + _publication - 12) * 12 + CpuOffset >= PreRender339Master)
                return Reject("Reset NMI mask restore reaches pre-render339");
            int bank = NmiTiming.BankGateCycles(_switchDepth, _deferred, out bool audioRuns);
            if (!audioRuns) audioServiceCycles=NmiTiming.ReturnWithoutBankRestoreCycles;
            _scheduler.Nmi();
            long service = (long)FrameScheduler.NmiEntryCycles + _publication + _timers + bank + audioServiceCycles;
            if (service > int.MaxValue) return Refuse("Reset NMI service exceeds its clock range");
            if (_nmiFromRunning)
            {
                // Both return paths include RTI already (57/26). Do not charge it twice.
                _streamCycle = HandlerCycle + _publication + _timers + bank + audioServiceCycles;
                RenderingAt339 = false;
                PhysicalEdgeMaster = LastNmiEdgeMaster + PowerOnContinuation.FullFrameMaster;
                EdgeOrdinal++;
                _nmiFromRunning = false;
                Stage = Phase.Running;
                // The loader pass resumes in place; there is no new body to commit for
                // this NMI, so the caller must not treat this as a pending body.
                return false;
            }
            _scheduler.ElapseInterrupt((int)service);
            long nextEarliest = LastNmiEdgeMaster + PowerOnContinuation.FullFrameMaster - 4;
            long nextThreshold = NmiThreshold(nextEarliest);
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
            if(work.RenderOnAtCycles>=0)
                RenderingAt339=(BodyStartCycle+work.RenderOnAtCycles)*12+CpuOffset<=PreRender339Master;
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
                _scheduler.BeginRunning(finish,work.StopPc);
                StopPc = work.StopPc;
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

        /// <summary>$DDCB JSR $FF06 is a real yield, but the flow after it is not clocked
        /// yet: record the commit and stop visibly at the first continued instruction.</summary>
        public bool StopAfterCommit(ushort pc)
        {
            if (Stage != Phase.Waiting || CommitCycle < 0) return false;
            if (ProcessState != FrameScheduler.Waiting || ProcessTimer != 1) return false;
            StopPc = pc;
            StopCycle = CommitCycle;
            Stage = Phase.Stopped;
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
