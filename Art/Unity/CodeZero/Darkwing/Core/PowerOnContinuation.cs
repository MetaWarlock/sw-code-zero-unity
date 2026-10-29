using System;
using System.Collections.Generic;

namespace Darkwing.Core
{
    public sealed class PowerOnContinuation
    {
        public enum Phase { ResetVector, ControlDisable, StatusWait, MemoryInit, BankInit, VideoInit, ProcessInit, MasterProcess, Yield, Committed }

        public readonly struct StatusRead
        {
            public readonly ushort Pc;
            public readonly long BusMaster, PpuMaster;
            public readonly bool Value;

            public StatusRead(ushort pc, long busMaster, long ppuMaster, bool value)
            {
                Pc = pc; BusMaster = busMaster; PpuMaster = ppuMaster; Value = value;
            }
        }

        public readonly struct Checkpoint
        {
            public readonly ushort Pc;
            public readonly long MasterClock;

            public Checkpoint(ushort pc, long masterClock)
            {
                Pc = pc; MasterClock = masterClock;
            }
        }

        public const int CpuMasterTicks = 12;
        public const int PpuMasterTicks = 4;
        public const int FullFrameMaster = 262 * 341 * PpuMasterTicks;
        public const int FirstVblankMaster = (241 * 341 + 2) * PpuMasterTicks;
        public const string PowerOnProfile = "Mesen 2.1.1 NTSC cold reset; alignment randomization disabled";

        private readonly int _ppuOffset;
        private readonly Action<Checkpoint> _blockReached;
        private readonly Action<StatusRead> _readObserved;
        private readonly IEnumerator<int> _execution;
        private int _nextCycles;
        private long _nextVblankEdge = FirstVblankMaster;
        private long _nextVblankClear = FirstVblankMaster + 20 * 341 * PpuMasterTicks;
        private bool _vblank, _preventVblank;
        private bool _active = true;

        public PowerOnContinuation(int cpuOffset = 0, int ppuOffset = 1,
            Action<Checkpoint> blockReached = null, Action<StatusRead> readObserved = null)
        {
            if ((uint)cpuOffset >= CpuMasterTicks || (uint)ppuOffset >= PpuMasterTicks)
                throw new ArgumentOutOfRangeException(nameof(cpuOffset));
            _ppuOffset = ppuOffset;
            _blockReached = blockReached;
            _readObserved = readObserved;
            MasterClock = CpuMasterTicks + cpuOffset + 8 * CpuMasterTicks;
            _execution = Execute().GetEnumerator();
            NextInstruction();
        }

        public bool Active => _active;
        public long MasterClock { get; private set; }
        public long PpuMasterClock => MasterClock - _ppuOffset;
        public ushort InstructionPc { get; private set; }
        public Phase Stage { get; private set; }
        public int SuccessfulStatusReads { get; private set; }
        public int StatusReadCount { get; private set; }
        public int SuppressedVblanks { get; private set; }
        public long LastSuccessfulReadBoundaryMaster { get; private set; }
        public long NmiEnableMaster { get; private set; } = -1;
        public long FirstEnabledNmiEdgeMaster { get; private set; } = -1;
        public long CommitMaster { get; private set; } = -1;
        public long NextNmiEdgeMaster => _nextVblankEdge + _ppuOffset;
        public byte ProcessState { get; private set; }
        public byte ProcessTimer { get; private set; }
        public byte StackPointer { get; private set; } = 0xFD;
        public bool NmiEnabled => NmiEnableMaster >= 0;
        public byte HardwareMask => 0;

        public void AdvanceToMaster(long deadline)
        {
            if (deadline < 0) throw new ArgumentOutOfRangeException(nameof(deadline));
            while (_active && MasterClock < deadline)
            {
                MasterClock += _nextCycles * CpuMasterTicks;
                NextInstruction();
            }
        }

        public void Finish() => AdvanceToMaster(long.MaxValue);

        private void NextInstruction()
        {
            if (_execution.MoveNext()) { _nextCycles = _execution.Current; return; }
            _execution.Dispose();
            _nextCycles = 0;
            _active = false;
        }

        private int At(ushort pc, int cycles)
        {
            InstructionPc = pc;
            return cycles;
        }

        private void Reach(ushort pc)
        {
            InstructionPc = pc;
            _blockReached?.Invoke(new Checkpoint(pc, MasterClock));
        }

        private void AdvanceStatus(long ppuMaster)
        {
            while (Math.Min(_nextVblankEdge, _nextVblankClear) <= ppuMaster)
            {
                if (_nextVblankEdge <= _nextVblankClear)
                {
                    if (_preventVblank) SuppressedVblanks++;
                    else _vblank = true;
                    _preventVblank = false;
                    _nextVblankEdge += FullFrameMaster;
                }
                else
                {
                    _vblank = false;
                    _nextVblankClear += FullFrameMaster;
                }
            }
        }

        private bool ReadStatus(ushort pc, long instructionStart)
        {
            long busMaster = instructionStart + 3 * CpuMasterTicks + 5 - _ppuOffset;
            long ppuMaster = busMaster / PpuMasterTicks * PpuMasterTicks;
            AdvanceStatus(ppuMaster);
            bool value = _vblank;
            _vblank = false;
            if (ppuMaster == _nextVblankEdge - PpuMasterTicks) _preventVblank = true;
            StatusReadCount++;
            _readObserved?.Invoke(new StatusRead(pc, busMaster, ppuMaster, value));
            return value;
        }

        private IEnumerable<int> Execute()
        {
            Stage = Phase.ResetVector;
            yield return At(0xFFE0, 2);
            yield return At(0xFFE1, 6);
            yield return At(0xFFE4, 2);
            yield return At(0xFFE5, 3);
            Reach(0xFE00);
            Stage = Phase.ControlDisable;
            yield return At(0xFE00, 2);
            yield return At(0xFE01, 2);
            yield return At(0xFE02, 2);
            yield return At(0xFE04, 4);
            yield return At(0xFE07, 4);
            yield return At(0xFE0A, 2);
            yield return At(0xFE0B, 2);
            StackPointer = 0xFF;
            yield return At(0xFE0C, 2);
            Stage = Phase.StatusWait;
            for (int remaining = 4; remaining != 0; remaining--)
            {
                bool ready;
                do
                {
                    long start = MasterClock;
                    yield return At(0xFE0E, 4);
                    ready = ReadStatus(0xFE0E, start);
                    if (ready)
                    {
                        SuccessfulStatusReads++;
                        LastSuccessfulReadBoundaryMaster = MasterClock;
                        Reach(0xFE11);
                    }
                    yield return At(0xFE11, ready ? 2 : 3);
                } while (!ready);
                do
                {
                    long start = MasterClock;
                    yield return At(0xFE13, 4);
                    ready = ReadStatus(0xFE13, start);
                    yield return At(0xFE16, ready ? 3 : 2);
                } while (ready);
                yield return At(0xFE18, 2);
                yield return At(0xFE19, remaining == 1 ? 2 : 3);
            }
            yield return At(0xFE1B, 4);
            yield return At(0xFE1E, 4);
            yield return At(0xFE21, 2);
            yield return At(0xFE23, 4);
            Reach(0xFE26);
            Stage = Phase.MemoryInit;
            foreach (int cycles in InitializeMemory()) yield return cycles;
            Reach(0xFE41);
            Stage = Phase.BankInit;
            foreach (int cycles in InitializeBanks()) yield return cycles;
            Reach(0xFE69);
            Stage = Phase.VideoInit;
            foreach (int cycles in InitializeVideo()) yield return cycles;
            Reach(0xFE7E);
            Stage = Phase.ProcessInit;
            foreach (int cycles in InitializeProcess()) yield return cycles;
            ProcessState = FrameScheduler.Running;
            Reach(0xC595);
            Stage = Phase.MasterProcess;
            yield return At(0xC595, 2);
            yield return At(0xC597, 2);
            StackPointer = 0xBF;
            yield return At(0xC598, 3);
            yield return At(0xC59A, 4);
            AdvanceStatus(PpuMasterClock);
            if (_vblank) throw new InvalidOperationException("Power-on enables NMI during vblank");
            NmiEnableMaster = MasterClock;
            FirstEnabledNmiEdgeMaster = NextNmiEdgeMaster;
            Reach(0xC59D);
            yield return At(0xC59D, 2);
            yield return At(0xC59F, 6);
            foreach (int cycles in EnqueueStop()) yield return cycles;
            yield return At(0xC5A2, 6);
            StackPointer -= 2;
            Stage = Phase.Yield;
            yield return At(0xFF06, 2);
            yield return At(0xFF08, 3);
            yield return At(0xFF0A, 2);
            yield return At(0xFF0B, 3);
            StackPointer--;
            yield return At(0xFF0C, 2);
            yield return At(0xFF0D, 3);
            StackPointer--;
            yield return At(0xFF0E, 6);
            yield return At(0xFEF9, 3);
            yield return At(0xFEFB, 2);
            yield return At(0xFEFC, 2);
            yield return At(0xFEFD, 2);
            yield return At(0xFEFE, 6);
            yield return At(0xFF11, 3);
            yield return At(0xFF13, 4);
            ProcessTimer = 1;
            yield return At(0xFF15, 2);
            yield return At(0xFF17, 4);
            AdvanceStatus(PpuMasterClock);
            if (MasterClock >= FirstEnabledNmiEdgeMaster) throw new InvalidOperationException("Power-on commit crosses the first enabled NMI");
            ProcessState = FrameScheduler.Waiting;
            CommitMaster = MasterClock;
            Stage = Phase.Committed;
            Reach(0xFF19);
        }

        private IEnumerable<int> InitializeMemory()
        {
            yield return At(0xFE26, 2);
            for (int index = 0; index < 256; index++)
            {
                yield return At(0xFE27, 5);
                yield return At(0xFE2A, 2);
                yield return At(0xFE2B, index == 255 ? 2 : 3);
            }
            for (int page = 1; page <= 7; page++)
            {
                yield return At(0xFE2D, 5);
                for (int index = 0; index < 256; index++)
                {
                    yield return At(0xFE2F, 6);
                    yield return At(0xFE31, 2);
                    yield return At(0xFE32, index == 255 ? 2 : 3);
                }
                yield return At(0xFE34, 3);
                yield return At(0xFE36, 2);
                yield return At(0xFE38, page == 7 ? 2 : 3);
            }
            yield return At(0xFE3A, 2);
            for (int index = 7; index >= 0; index--)
            {
                yield return At(0xFE3C, 4);
                yield return At(0xFE3E, 2);
                yield return At(0xFE3F, index == 0 ? 2 : 3);
            }
        }

        private IEnumerable<int> InitializeBanks()
        {
            yield return At(0xFE41, 2);
            yield return At(0xFE43, 3);
            yield return At(0xFE45, 2);
            yield return At(0xFE47, 6);
            yield return At(0xFF55, 3);
            yield return At(0xFF57, 3);
            yield return At(0xFF59, 5);
            for (int write = 0; write < 5; write++)
            {
                yield return At((ushort)(0xFF5B + write * 4), 4);
                if (write != 4) yield return At((ushort)(0xFF5E + write * 4), 2);
            }
            yield return At(0xFF6E, 2);
            yield return At(0xFF70, 3);
            yield return At(0xFF72, 3);
            yield return At(0xFF74, 2);
            yield return At(0xFF76, 6);
            yield return At(0xFE4A, 2);
            for (int write = 0; write < 5; write++)
            {
                yield return At((ushort)(0xFE4C + write * 4), 4);
                if (write != 4) yield return At((ushort)(0xFE4F + write * 4), 2);
            }
            yield return At(0xFE5F, 2);
            yield return At(0xFE61, 6);
            foreach (int cycles in SwitchChr(0xFF21)) yield return cycles;
            yield return At(0xFE64, 2);
            yield return At(0xFE66, 6);
            foreach (int cycles in SwitchChr(0xFF3B)) yield return cycles;
        }

        private IEnumerable<int> SwitchChr(ushort entry)
        {
            yield return At(entry, 3);
            for (int write = 0; write < 5; write++)
            {
                yield return At((ushort)(entry + 2 + write * 4), 4);
                if (write != 4) yield return At((ushort)(entry + 5 + write * 4), 2);
            }
            yield return At((ushort)(entry + 21), 2);
            yield return At((ushort)(entry + 23), 3);
            yield return At((ushort)(entry + 25), 6);
        }

        private IEnumerable<int> InitializeVideo()
        {
            yield return At(0xFE69, 6);
            yield return At(0xC21E, 3);
            yield return At(0xC220, 2);
            yield return At(0xC222, 3);
            yield return At(0xC229, 2);
            for (int record = 0; record < 64; record++)
            {
                yield return At(0xC22B, 5);
                for (int index = 0; index < 4; index++) yield return At((ushort)(0xC22E + index), 2);
                yield return At(0xC232, record == 63 ? 2 : 3);
            }
            yield return At(0xC234, 6);
            for (int page = 0; page < 2; page++)
            {
                ushort entry = (ushort)(0xFE6C + page * 9);
                yield return At(entry, 2);
                yield return At((ushort)(entry + 2), 2);
                yield return At((ushort)(entry + 4), 2);
                yield return At((ushort)(entry + 6), 6);
                foreach (int cycles in FillVram()) yield return cycles;
            }
        }

        private IEnumerable<int> FillVram()
        {
            yield return At(0xC1BA, 3);
            yield return At(0xC1BC, 3);
            yield return At(0xC1BE, 3);
            yield return At(0xC1C0, 4);
            yield return At(0xC1C3, 3);
            yield return At(0xC1C5, 2);
            yield return At(0xC1C7, 4);
            yield return At(0xC1CA, 3);
            yield return At(0xC1CC, 4);
            yield return At(0xC1CF, 2);
            yield return At(0xC1D1, 4);
            yield return At(0xC1D4, 2);
            yield return At(0xC1D6, 2);
            yield return At(0xC1D8, 3);
            yield return At(0xC1DC, 2);
            yield return At(0xC1DE, 3);
            for (int page = 0; page < 4; page++)
            {
                for (int index = 0; index < 256; index++)
                {
                    yield return At(0xC1E0, 4);
                    yield return At(0xC1E3, 2);
                    yield return At(0xC1E4, index == 255 ? 2 : 3);
                }
                yield return At(0xC1E6, 2);
                yield return At(0xC1E7, page == 3 ? 2 : 3);
            }
            yield return At(0xC1E9, 3);
            yield return At(0xC1EB, 3);
            yield return At(0xC1ED, 2);
            yield return At(0xC1EF, 2);
            yield return At(0xC1F1, 2);
            yield return At(0xC1F3, 4);
            yield return At(0xC1F6, 2);
            yield return At(0xC1F8, 4);
            yield return At(0xC1FB, 2);
            for (int index = 0; index < 64; index++)
            {
                yield return At(0xC1FD, 4);
                yield return At(0xC200, 2);
                yield return At(0xC201, index == 63 ? 2 : 4);
            }
            yield return At(0xC203, 3);
            yield return At(0xC205, 6);
        }

        private IEnumerable<int> InitializeProcess()
        {
            yield return At(0xFE7E, 2);
            yield return At(0xFE80, 3);
            yield return At(0xFE82, 2);
            yield return At(0xFE84, 3);
            yield return At(0xFE86, 2);
            yield return At(0xFE88, 6);
            yield return At(0xFED7, 6);
            yield return At(0xFEFB, 2);
            yield return At(0xFEFC, 2);
            yield return At(0xFEFD, 2);
            yield return At(0xFEFE, 6);
            yield return At(0xFEDA, 3);
            yield return At(0xFEDC, 4);
            yield return At(0xFEDE, 3);
            yield return At(0xFEE0, 4);
            yield return At(0xFEE2, 2);
            yield return At(0xFEE4, 4);
            ProcessState = FrameScheduler.New;
            yield return At(0xFEE6, 6);
            yield return At(0xFE8B, 2);
            yield return At(0xFE8D, 3);
            yield return At(0xFE8F, 2);
            yield return At(0xFE91, 2);
            yield return At(0xFE92, 2);
            yield return At(0xFE94, 3);
            yield return At(0xFE96, 2);
            yield return At(0xFE98, 4);
            yield return At(0xFE9A, 2);
            yield return At(0xFE9C, 3);
            yield return At(0xFEA8, 3);
            yield return At(0xFEAA, 2);
            yield return At(0xFEAC, 2);
            yield return At(0xFEAD, 2);
            yield return At(0xFEAE, 2);
            yield return At(0xFEB0, 3);
            yield return At(0xFEB2, 4);
            yield return At(0xFEB4, 2);
            yield return At(0xFEB6, 4);
            ProcessState = FrameScheduler.Running;
            yield return At(0xFEB8, 2);
            yield return At(0xFEBA, 2);
            yield return At(0xFEBC, 4);
            yield return At(0xFEBE, 3);
            yield return At(0xFEC0, 4);
            yield return At(0xFEC2, 3);
            yield return At(0xFEC4, 5);
        }

        private IEnumerable<int> EnqueueStop()
        {
            yield return At(0xF84E, 3);
            yield return At(0xF850, 3);
            yield return At(0xF852, 3);
            yield return At(0xF854, 3);
            yield return At(0xF856, 4);
            yield return At(0xF858, 2);
            yield return At(0xF85A, 2);
            yield return At(0xF85C, 3);
            yield return At(0xF85E, 4);
            yield return At(0xF860, 2);
            yield return At(0xF861, 2);
            yield return At(0xF862, 2);
            yield return At(0xF864, 3);
            yield return At(0xF866, 3);
            yield return At(0xF868, 6);
        }
    }
}
