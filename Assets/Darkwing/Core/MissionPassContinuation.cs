using System;
using System.Collections.Generic;

namespace Darkwing.Core
{
    public sealed class MissionPassContinuation
    {
        public enum Phase { None, Oam, PostOam, Rng, Yield, Committed }

        private readonly OamDispatcher _oam;
        private readonly PlayerCore _player;
        private IEnumerator<int> _execution;
        private int _nextCycles;
        private bool _carry;
        private byte _accumulator;
        private byte _feedback;

        public MissionPassContinuation(OamDispatcher oam, PlayerCore player)
        {
            _oam = oam ?? throw new ArgumentNullException(nameof(oam));
            _player = player ?? throw new ArgumentNullException(nameof(player));
        }

        public bool Active => _execution != null;
        public int Cycles { get; private set; }
        public Phase Stage { get; private set; }
        public ushort InstructionPc { get; private set; }
        public bool Busy { get; private set; }
        public byte ProcessState { get; private set; }
        public byte ProcessTimer { get; private set; }

        public void Begin(byte parity)
        {
            if (Active) throw new InvalidOperationException("Mission pass is still running");
            _oam.BeginFrameWalk(parity, 4);
            Cycles = 0;
            Busy = true;
            ProcessState = FrameScheduler.Running;
            ProcessTimer = 0;
            _execution = Execute().GetEnumerator();
            NextInstruction();
        }

        public void AdvanceTo(int deadline)
        {
            if (deadline < 0) throw new ArgumentOutOfRangeException(nameof(deadline));
            while (Active && Cycles < deadline)
            {
                Cycles += _nextCycles;
                NextInstruction();
            }
            _player.Flags = _player.Objects.Slots[0].Flags;
        }

        public void Finish() => AdvanceTo(int.MaxValue);

        private void NextInstruction()
        {
            if (_execution.MoveNext()) _nextCycles = _execution.Current;
            else
            {
                _execution.Dispose();
                _execution = null;
                _nextCycles = 0;
            }
        }

        private int At(ushort address, int cycles)
        {
            InstructionPc = address;
            return cycles;
        }

        private IEnumerable<int> Execute()
        {
            Stage = Phase.Oam;
            bool bank4 = _oam.UseBank4Tables;
            yield return At(bank4 ? (ushort)0xCAD1 : (ushort)0xCABB, 6);
            while (_oam.WalkActive)
            {
                InstructionPc = 0;
                yield return _oam.NextWalkInstructionCycles;
                _oam.StepWalkInstruction();
            }
            Stage = Phase.PostOam;
            yield return At(bank4 ? (ushort)0xCAD4 : (ushort)0xCABE, 2);
            yield return At(bank4 ? (ushort)0xCAD6 : (ushort)0xCAC0, 3);
            _oam.RestoreCursor(4);
            if (!bank4) yield return At(0xCAC2, 3);
            yield return At(0xCAD8, 6);
            Stage = Phase.Rng;
            yield return At(0xC2DE, 2);
            yield return At(0xC2E0, 2);
            yield return At(0xC2E2, 4);
            _accumulator = EnemyBehaviors.RngE4;
            yield return At(0xC2E4, 2);
            _accumulator &= 2;
            yield return At(0xC2E6, 3);
            _feedback = _accumulator;
            yield return At(0xC2E8, 4);
            _accumulator = EnemyBehaviors.RngE5;
            yield return At(0xC2EA, 2);
            _accumulator &= 2;
            yield return At(0xC2EC, 3);
            _accumulator ^= _feedback;
            yield return At(0xC2EE, 2);
            _carry = false;
            yield return At(0xC2EF, _accumulator == 0 ? 3 : 2);
            if (_accumulator != 0)
            {
                yield return At(0xC2F1, 2);
                _carry = true;
            }
            for (int register = 0; register < 4; register++)
            {
                yield return At(0xC2F2, 6);
                byte value = register == 0 ? EnemyBehaviors.RngE4 : register == 1 ? EnemyBehaviors.RngE5
                    : register == 2 ? EnemyBehaviors.RngE6 : EnemyBehaviors.RngE7;
                byte rotated = (byte)((value >> 1) | (_carry ? 0x80 : 0));
                _carry = (value & 1) != 0;
                switch (register)
                {
                    case 0: EnemyBehaviors.RngE4 = rotated; break;
                    case 1: EnemyBehaviors.RngE5 = rotated; break;
                    case 2: EnemyBehaviors.RngE6 = rotated; break;
                    case 3: EnemyBehaviors.RngE7 = rotated; break;
                }
                yield return At(0xC2F4, 2);
                yield return At(0xC2F5, 2);
                yield return At(0xC2F6, register == 3 ? 2 : 3);
            }
            yield return At(0xC2F8, 6);
            yield return At(0xCADB, 2);
            yield return At(0xCADD, 3);
            Busy = false;
            Stage = Phase.Yield;
            yield return At(0xCADF, 6);
            yield return At(0xFF06, 2);
            yield return At(0xFF08, 3);
            yield return At(0xFF0A, 2);
            yield return At(0xFF0B, 3);
            yield return At(0xFF0C, 2);
            yield return At(0xFF0D, 3);
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
            ProcessState = FrameScheduler.Waiting;
            InstructionPc = 0xFF19;
            Stage = Phase.Committed;
        }
    }
}
