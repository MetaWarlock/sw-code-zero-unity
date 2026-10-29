using System;

namespace Darkwing.Core
{
    public sealed class FrameScheduler
    {
        public const int SlotCount = 4;
        public const int SlotStride = 4;
        public const int IdleScanCycles = 93;
        public const int CommitToIdleCycles = 17;
        public const int NmiEntryCycles = 7;

        public const byte Free = 0;
        public const byte Waiting = 1;
        public const byte Running = 2;
        public const byte Ready = 4;
        public const byte New = 8;

        public static readonly int[] ScanInstructionStarts =
        {
            0, 2, 5, 7, 11, 13, 15, 17, 19, 21, 23, 25, 28,
            32, 34, 36, 38, 40, 42, 44, 46, 49, 53, 55,
            57, 59, 61, 63, 65, 67, 70, 74, 76, 78, 80,
            82, 84, 86, 88, 90
        };

        public readonly byte[] State = new byte[SlotCount];
        public readonly byte[] Timer = new byte[SlotCount];
        public readonly ushort[] SavedSp = new ushort[SlotCount];

        public bool FrameGate { get; private set; }
        public byte NmiCounter { get; private set; }
        public long IdleStart { get; private set; }
        public int Scans { get; private set; }
        public long NextScanCycle => IdleStart + (long)Scans * IdleScanCycles;
        public long ExecutionCycle { get; private set; }
        public ushort ExecutionPc { get; private set; }
        public bool DispatchPending { get; private set; }
        public int DispatchSlot { get; private set; }
        public bool DispatchIsNew { get; private set; }
        public bool DispatchNeedsInput { get; private set; }
        public ulong ExecutionSignature => (ulong)ExecutionPc | ((ulong)_executionIndex << 16)
            | ((ulong)_executionRemaining << 24) | ((ulong)_executionLoadedState << 32)
            | (_executionLoadedGate ? 1UL << 40 : 0) | (_executionIsNew ? 1UL << 41 : 0)
            | (FrameGate ? 1UL << 42 : 0);
        private int _executionIndex;
        private int _executionRemaining;
        private byte _executionLoadedState;
        private bool _executionLoadedGate;
        private bool _executionIsNew;

        public void BeginExecution(long cycle)
        {
            ExecutionCycle = cycle;
            ExecutionPc = 0xFE8F;
            DispatchPending = false;
        }

        public void BeginExecutionAfterCommit(int slot, long cycle, ushort savedSp)
        {
            CommitWait(slot, cycle, savedSp);
            ExecutionCycle = cycle;
            ExecutionPc = 0xFF19;
            _executionIndex = slot * SlotStride;
            DispatchPending = false;
        }

        public void ElapseInterrupt(int cycles)
        {
            if (cycles < 0) throw new ArgumentOutOfRangeException(nameof(cycles));
            ExecutionCycle += cycles;
        }

        /// <summary>The loader's in-line $DE86 loop keeps slot 0 Running between an FF06
        /// dispatch and its next yield; NMI service leaves that state alone and resumes the
        /// interrupted PC after RTI.</summary>
        public void BeginRunning(long cycle, ushort pc)
        {
            if (State[0] != Running) throw new InvalidOperationException("Scheduler slot 0 is not running");
            ExecutionCycle = cycle;
            ExecutionPc = pc;
            _executionIndex = 0;
            DispatchPending = false;
        }

        public void AdvanceExecutionTo(long deadline)
        {
            while (!DispatchPending && ExecutionCycle < deadline)
                StepExecution();
        }

        public void StepExecution()
        {
            if (DispatchPending) throw new InvalidOperationException("Scheduler dispatch has not been consumed");
            int slot = _executionIndex / SlotStride;
            switch (ExecutionPc)
            {
                case 0xFF19: ExecutionCycle += 2; ExecutionPc = 0xFF1A; break;
                case 0xFF1A: ExecutionCycle += 2; ExecutionPc = 0xFF1B; break;
                case 0xFF1B: ExecutionCycle += 2; ExecutionPc = 0xFF1C; break;
                case 0xFF1C: ExecutionCycle += 4; ExecutionPc = 0xFF1E; break;
                case 0xFF1E: ExecutionCycle += 3; ExecutionPc = 0xFE8F; break;
                case 0xFE8F: ExecutionCycle += 2; ExecutionPc = 0xFE91; break;
                case 0xFE91: ExecutionCycle += 2; ExecutionPc = 0xFE92; break;
                case 0xFE92:
                    _executionIndex = 0;
                    ExecutionCycle += 2; ExecutionPc = 0xFE94; break;
                case 0xFE94:
                    FrameGate = false;
                    ExecutionCycle += 3; ExecutionPc = 0xFE96; break;
                case 0xFE96:
                    _executionRemaining = SlotCount;
                    ExecutionCycle += 2; ExecutionPc = 0xFE98; break;
                case 0xFE98:
                    _executionLoadedState = State[slot];
                    ExecutionCycle += 4; ExecutionPc = 0xFE9A; break;
                case 0xFE9A: ExecutionCycle += 2; ExecutionPc = 0xFE9C; break;
                case 0xFE9C:
                    ExecutionCycle += _executionLoadedState >= Ready ? 3 : 2;
                    ExecutionPc = (ushort)(_executionLoadedState >= Ready ? 0xFEA8 : 0xFE9E); break;
                case 0xFE9E: ExecutionCycle += 2; ExecutionPc = 0xFE9F; break;
                case 0xFE9F: ExecutionCycle += 2; ExecutionPc = 0xFEA0; break;
                case 0xFEA0: ExecutionCycle += 2; ExecutionPc = 0xFEA1; break;
                case 0xFEA1:
                    _executionIndex += SlotStride;
                    ExecutionCycle += 2; ExecutionPc = 0xFEA2; break;
                case 0xFEA2:
                    _executionRemaining--;
                    ExecutionCycle += 2; ExecutionPc = 0xFEA3; break;
                case 0xFEA3:
                    ExecutionCycle += _executionRemaining != 0 ? 3 : 2;
                    ExecutionPc = (ushort)(_executionRemaining != 0 ? 0xFE98 : 0xFEA5); break;
                case 0xFEA5: ExecutionCycle += 3; ExecutionPc = 0xFE92; break;
                case 0xFEA8:
                    _executionLoadedGate = FrameGate;
                    ExecutionCycle += 3; ExecutionPc = 0xFEAA; break;
                case 0xFEAA:
                    ExecutionCycle += _executionLoadedGate ? 3 : 2;
                    ExecutionPc = (ushort)(_executionLoadedGate ? 0xFE92 : 0xFEAC); break;
                case 0xFEAC:
                    _executionRemaining--;
                    ExecutionCycle += 2; ExecutionPc = 0xFEAD; break;
                case 0xFEAD: ExecutionCycle += 2; ExecutionPc = 0xFEAE; break;
                case 0xFEAE: ExecutionCycle += 2; ExecutionPc = 0xFEB0; break;
                case 0xFEB0: ExecutionCycle += 3; ExecutionPc = 0xFEB2; break;
                case 0xFEB2:
                    _executionIsNew = State[slot] == New;
                    ExecutionCycle += 4; ExecutionPc = 0xFEB4; break;
                case 0xFEB4: ExecutionCycle += 2; ExecutionPc = 0xFEB6; break;
                case 0xFEB6:
                    State[slot] = Running;
                    ExecutionCycle += 4; ExecutionPc = 0xFEB8; break;
                case 0xFEB8: ExecutionCycle += 2; ExecutionPc = 0xFEBA; break;
                case 0xFEBA:
                    ExecutionCycle += _executionIsNew ? 2 : 3;
                    ExecutionPc = (ushort)(_executionIsNew ? 0xFEBC : 0xFEC7); break;
                case 0xFEBC: ExecutionCycle += 4; ExecutionPc = 0xFEBE; break;
                case 0xFEBE: ExecutionCycle += 3; ExecutionPc = 0xFEC0; break;
                case 0xFEC0: ExecutionCycle += 4; ExecutionPc = 0xFEC2; break;
                case 0xFEC2: ExecutionCycle += 3; ExecutionPc = 0xFEC4; break;
                case 0xFEC4:
                    ExecutionCycle += 5;
                    SetDispatch(slot, true, false); break;
                case 0xFEC7: ExecutionCycle += 4; ExecutionPc = 0xFEC9; break;
                case 0xFEC9: ExecutionCycle += 2; ExecutionPc = 0xFECA; break;
                case 0xFECA: ExecutionCycle += 2; ExecutionPc = 0xFECB; break;
                case 0xFECB: ExecutionCycle += 3; ExecutionPc = 0xFECD; break;
                case 0xFECD:
                    ExecutionCycle += slot == 0 ? 2 : 3;
                    ExecutionPc = (ushort)(slot == 0 ? 0xFECF : 0xFED2);
                    if (slot == 0) SetDispatch(slot, false, true);
                    break;
                case 0xFED2: ExecutionCycle += 4; ExecutionPc = 0xFED3; break;
                case 0xFED3: ExecutionCycle += 2; ExecutionPc = 0xFED4; break;
                case 0xFED4: ExecutionCycle += 4; ExecutionPc = 0xFED5; break;
                case 0xFED5: ExecutionCycle += 2; ExecutionPc = 0xFED6; break;
                case 0xFED6:
                    ExecutionCycle += 6;
                    SetDispatch(slot, false, false); break;
                default: throw new InvalidOperationException("Unsupported scheduler continuation " + ExecutionPc.ToString("X4"));
            }
        }

        private void SetDispatch(int slot, bool isNew, bool needsInput)
        {
            DispatchPending = true;
            DispatchSlot = slot;
            DispatchIsNew = isNew;
            DispatchNeedsInput = needsInput;
        }

        public void Reset()
        {
            Array.Clear(State, 0, State.Length);
            Array.Clear(Timer, 0, Timer.Length);
            Array.Clear(SavedSp, 0, SavedSp.Length);
            FrameGate = false;
            NmiCounter = 0;
            IdleStart = 0;
            Scans = 0;
            ExecutionCycle = 0;
            ExecutionPc = 0;
            DispatchPending = false;
            DispatchSlot = -1;
            DispatchIsNew = DispatchNeedsInput = false;
        }

        public void AnchorAfterCommit(long commitCycle)
        {
            IdleStart = commitCycle + CommitToIdleCycles;
            Scans = 0;
        }

        public void AdvanceScans(long cycle)
        {
            if (IdleStart == 0) return;
            while (NextScanCycle <= cycle)
            {
                Scans++;
                FrameGate = false;
            }
        }

        public void Nmi()
        {
            FrameGate = true;
            NmiCounter++;
            for (int i = 0; i < SlotCount; i++)
            {
                if (State[i] != Waiting) continue;
                Timer[i] = unchecked((byte)(Timer[i] - 1));
                if (Timer[i] == 0) State[i] = Ready;
            }
        }

        public bool TryTakeReady(out int slot)
        {
            slot = -1;
            if (FrameGate) return false;
            for (int i = 0; i < SlotCount; i++)
            {
                if (State[i] < Ready) continue;
                slot = i;
                return true;
            }
            return false;
        }

        public void StartNew(int slot, ushort sp)
        {
            State[slot] = New;
            Timer[slot] = 0;
            SavedSp[slot] = sp;
        }

        public void Launch(int slot)
        {
            State[slot] = Running;
            Timer[slot] = 0;
        }

        public void Wait(int slot, byte frames, ushort sp)
        {
            State[slot] = Waiting;
            Timer[slot] = frames;
            SavedSp[slot] = sp;
        }

        public void CommitWait(int slot, long commitCycle, ushort sp)
        {
            Wait(slot, 1, sp);
            AnchorAfterCommit(commitCycle);
        }

        public static int PhaseAt(long cycle, long idleStart)
        {
            long delta = cycle - idleStart;
            long phase = delta % IdleScanCycles;
            if (phase < 0) phase += IdleScanCycles;
            return (int)phase;
        }

        public static int AcceptPhaseAt(long c000Cycle, long idleStart)
        {
            return PhaseAt(c000Cycle - NmiEntryCycles, idleStart);
        }

        public static bool IsInstructionStart(int phase)
        {
            for (int i = 0; i < ScanInstructionStarts.Length; i++)
                if (ScanInstructionStarts[i] == phase) return true;
            return false;
        }
    }
}
