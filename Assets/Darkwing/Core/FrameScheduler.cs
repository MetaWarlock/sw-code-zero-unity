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

        public void Reset()
        {
            Array.Clear(State, 0, State.Length);
            Array.Clear(Timer, 0, Timer.Length);
            Array.Clear(SavedSp, 0, SavedSp.Length);
            FrameGate = false;
            NmiCounter = 0;
            IdleStart = 0;
            Scans = 0;
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
                if (Timer[i] == 0) continue;
                if (--Timer[i] == 0) State[i] = Ready;
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
