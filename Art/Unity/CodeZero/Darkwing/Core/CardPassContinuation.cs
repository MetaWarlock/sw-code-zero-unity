using System;
using System.Collections.Generic;

namespace Darkwing.Core
{
    public sealed class CardPassContinuation
    {
        public enum Phase { None, BankRestore, Card, Blit, Wipe, BankSwitch, Oam, Yield, Committed }

        private readonly OamDispatcher _oam;
        private readonly PlayerCore _player;
        private readonly Action<byte> _waitAssigned;
        private IEnumerator<int> _execution;
        private int _nextCycles;
        private byte _parity, _cursor;
        private byte _accumulator;
        private bool _pauseBeforeFrame;

        public CardPassContinuation(OamDispatcher oam, PlayerCore player, Action<byte> waitAssigned = null)
        {
            _oam = oam ?? throw new ArgumentNullException(nameof(oam));
            _player = player ?? throw new ArgumentNullException(nameof(player));
            _waitAssigned = waitAssigned;
        }

        public bool Active => _execution != null;
        public int Cycles { get; private set; }
        public Phase Stage { get; private set; }
        public string Unsupported { get; private set; }
        public ushort InstructionPc { get; private set; }
        public byte Bracket { get; private set; }
        public bool Busy => Bracket != 0;
        public byte SwitchDepth { get; private set; }
        public byte DeferredAudio;
        public byte WaitCount { get; private set; }
        public byte ProcessState { get; private set; }
        public byte ProcessTimer { get; private set; }
        public bool FramePending { get; private set; }

        public void BeginFrame(byte parity, byte cursor, byte entryBracket)
        {
            if (Active) throw new InvalidOperationException("Card continuation is still running");
            if ((cursor & 3) != 0) throw new ArgumentException("Card shadow cursor must be aligned", nameof(cursor));
            _parity = parity;
            _cursor = cursor;
            _pauseBeforeFrame = false;
            Begin(ExecuteFrame(), entryBracket);
            _oam.RestoreCursor(cursor);
        }

        public void BeginWaitEntry(byte[] bank2, byte world)
        {
            if (bank2 == null || bank2.Length != 0x4000 || world > 6)
                throw new ArgumentException("Mission bank2 and world0..6 required");
            var player = _player.Objects.Slots[0];
            if (player.AnimFrame != 0x11 && (player.AnimFrame != 0x0F || player.AnimTimer != 4))
                throw new InvalidOperationException("Card has not reached its terminal animation branch");
            _pauseBeforeFrame = false;
            Begin(ExecuteResume(bank2, world));
        }

        public void BeginResume(byte[] bank2, byte world, byte parity, bool pauseBeforeFrame = false)
        {
            if (Active) throw new InvalidOperationException("Card continuation is still running");
            if (bank2 == null || bank2.Length != 0x4000 || world > 6)
                throw new ArgumentException("Mission bank2 and world0..6 required");
            _parity = parity;
            _cursor = 0xA0;
            _pauseBeforeFrame = pauseBeforeFrame;
            Begin(ExecuteResume(bank2, world));
        }

        public void BeginEntry(byte parity, byte entryBracket, bool pauseBeforeFrame = false)
        {
            if (Active) throw new InvalidOperationException("Card continuation is still running");
            if (_player.State != 9 && _player.State != 0x18)
                throw new NotSupportedException("D53F card entry state");
            _parity = parity;
            _cursor = 0xA0;
            _pauseBeforeFrame = pauseBeforeFrame;
            Begin(ExecuteEntry(), entryBracket);
        }

        public void ContinueFrame(byte parity)
        {
            if (!Active || !FramePending) throw new InvalidOperationException("Card frame engine is not pending");
            _parity = parity;
            FramePending = false;
        }

        private void Begin(IEnumerable<int> execution, byte entryBracket = 0)
        {
            if (Active) throw new InvalidOperationException("Card continuation is still running");
            if (SwitchDepth != 0 || DeferredAudio != 0)
                throw new NotSupportedException("Card entry requires a completed bank switch");
            Cycles = 0;
            Bracket = entryBracket;
            ProcessState = FrameScheduler.Running;
            ProcessTimer = 0;
            WaitCount = 0;
            FramePending = false;
            Stage = Phase.None;
            Unsupported = null;
            _execution = execution.GetEnumerator();
            NextInstruction();
        }

        public void AdvanceTo(int deadline)
        {
            if (deadline < 0) throw new ArgumentOutOfRangeException(nameof(deadline));
            while (Active && !FramePending && Cycles < deadline)
            {
                Cycles += _nextCycles;
                NextInstruction();
            }
            _player.Flags = _player.Objects.Slots[0].Flags;
        }

        public void Finish() => AdvanceTo(int.MaxValue);

        private void NextInstruction()
        {
            try
            {
                if (_execution.MoveNext()) { _nextCycles = _execution.Current; return; }
            }
            catch (NotSupportedException exception)
            {
                Unsupported = exception.Message;
                _execution.Dispose();
                _execution = null;
                _nextCycles = 0;
                throw;
            }
            _execution.Dispose();
            _execution = null;
            _nextCycles = 0;
        }

        private int At(ushort address, int cycles)
        {
            InstructionPc = address;
            return cycles;
        }

        private IEnumerable<int> SwitchBank()
        {
            yield return At(0xFF55, 3);
            yield return At(0xFF57, 3);
            yield return At(0xFF59, 5);
            SwitchDepth++;
            for (int write = 0; write < 5; write++)
            {
                yield return At((ushort)(0xFF5B + write * 4), 4);
                if (write != 4) yield return At((ushort)(0xFF5E + write * 4), 2);
            }
            yield return At(0xFF6E, 2);
            yield return At(0xFF70, 3);
            SwitchDepth = 0;
            yield return At(0xFF72, 3);
            _accumulator = DeferredAudio;
            yield return At(0xFF74, _accumulator != 0 ? 3 : 2);
            if (_accumulator != 0) throw new NotSupportedException("Card deferred audio needs its native service continuation");
            yield return At(0xFF76, 6);
        }

        private IEnumerable<int> ExecuteFrame()
        {
            yield return At(0xF89A, 3);
            yield return At(0xF89C, 3);
            yield return At(0xF89D, 5);
            Bracket = unchecked((byte)(Bracket + 1));
            yield return At(0xF89F, 6);
            Stage = Phase.Wipe;
            foreach (int cycles in Wipe(_cursor)) yield return cycles;
            yield return At(0xF8A2, 2);
            yield return At(0xF8A4, 6);
            Stage = Phase.BankSwitch;
            foreach (int cycles in SwitchBank()) yield return cycles;
            yield return At(0xF8A7, 6);
            Stage = Phase.Oam;
            _oam.BeginWalk(_parity, _cursor);
            while (_oam.WalkActive)
            {
                InstructionPc = 0;
                yield return _oam.NextWalkInstructionCycles;
                _oam.StepWalkInstruction();
            }
            yield return At(0xF8AA, 2);
            yield return At(0xF8AC, 3);
            Bracket = 0;
            yield return At(0xF8AE, 6);
            foreach (int cycles in CommitYield()) yield return cycles;
        }

        private IEnumerable<int> Wipe(int cursor)
        {
            yield return At(0xC21E, 3);
            yield return At(0xC220, 2);
            yield return At(0xC222, cursor == 4 ? 2 : 3);
            if (cursor == 4)
            {
                yield return At(0xC224, 2);
                yield return At(0xC226, 4);
                _oam.WriteShadowByte(0, 0xF8);
            }
            yield return At(0xC229, 2);
            do
            {
                yield return At(0xC22B, 5);
                _oam.WriteShadowByte(cursor, 0xF8);
                for (int increment = 0; increment < 4; increment++)
                {
                    yield return At((ushort)(0xC22E + increment), 2);
                    cursor = (cursor + 1) & 255;
                }
                yield return At(0xC232, cursor == 0 ? 2 : 3);
            } while (cursor != 0);
            yield return At(0xC234, 6);
        }

        private IEnumerable<int> ExecuteEntry()
        {
            Stage = Phase.Card;
            yield return At(0xD53F, 3);
            yield return At(0xD541, 2);
            yield return At(0xD543, 2);
            yield return At(0xD545, 2);
            yield return At(0xD547, 3);
            _oam.RestoreCursor(4);
            yield return At(0xD549, 6);
            Stage = Phase.Wipe;
            foreach (int cycles in Wipe(4)) yield return cycles;
            yield return At(0xD54C, 2);
            yield return At(0xD54E, 6);
            Stage = Phase.BankSwitch;
            foreach (int cycles in SwitchBank()) yield return cycles;
            yield return At(0xD551, 6);
            Stage = Phase.Card;
            yield return At(0x9600, 2);
            yield return At(0x9602, 6);
            yield return At(0xFA78, 4);
            _player.Objects.Slots[0].AnimNumber = 0x2B;
            yield return At(0xFA7B, 2);
            yield return At(0xFA7D, 4);
            _player.Objects.Slots[0].AnimTimer = 0;
            yield return At(0xFA80, 4);
            _player.Objects.Slots[0].AnimFrame = 0;
            yield return At(0xFA83, 6);
            yield return At(0x9605, 2);
            yield return At(0x9607, 3);
            _oam.RestoreCursor(0xA0);
            yield return At(0x9609, 6);
            FramePending = _pauseBeforeFrame;
            foreach (int cycles in ExecuteFrame()) yield return cycles;
        }

        private IEnumerable<int> ExecuteResume(byte[] bank2, byte world)
        {
            Stage = Phase.BankRestore;
            yield return At(0xF8B1, 4);
            yield return At(0xF8B2, 3);
            foreach (int cycles in SwitchBank()) yield return cycles;
            Stage = Phase.Card;
            yield return At(0x960C, 4);
            byte frame = _player.Objects.Slots[0].AnimFrame;
            yield return At(0x960F, 2);
            yield return At(0x9611, frame == 0x11 ? 3 : 2);
            bool terminal = frame == 0x11;
            if (frame != 0x11)
            {
                yield return At(0x9613, 2);
                yield return At(0x9615, frame == 0x0F ? 2 : 3);
                if (frame == 0x0F)
                {
                    yield return At(0x9617, 3);
                    yield return At(0x9619, 2);
                    yield return At(0x961B, 2);
                    yield return At(0x961D, 6);
                    Stage = Phase.Blit;
                    foreach (int cycles in Blit(bank2, world)) yield return cycles;
                    Stage = Phase.Card;
                    yield return At(0x9620, 3);
                    yield return At(0x9626, 4);
                    byte timer = _player.Objects.Slots[0].AnimTimer;
                    yield return At(0x9629, 2);
                    yield return At(0x962B, timer == 4 ? 3 : 2);
                    terminal = timer == 4;
                    if (!terminal) yield return At(0x962D, 3);
                }
            }
            if (!terminal)
            {
                yield return At(0x9605, 2);
                yield return At(0x9607, 3);
                _oam.RestoreCursor(0xA0);
                yield return At(0x9609, 6);
                FramePending = _pauseBeforeFrame;
                foreach (int cycles in ExecuteFrame()) yield return cycles;
                yield break;
            }
            yield return At(0x9630, 2);
            WaitCount = 0x3C;
            _waitAssigned?.Invoke(WaitCount);
            yield return At(0x9632, 6);
            yield return At(0xFEFF, 6);
            foreach (int cycles in CommitYield()) yield return cycles;
        }

        private IEnumerable<int> Blit(byte[] bank2, byte world)
        {
            yield return At(0x9636, 2);
            int sourceIndex = 0;
            yield return At(0x9638, 2);
            for (int target = 0; target < 0xA0; target += 4)
            {
                yield return At(0x963A, 3);
                byte value = world;
                yield return At(0x963C, world == 0 ? 2 : 3);
                bool ready = false;
                if (world == 0)
                {
                    yield return At(0x963E, 4 + IndexedExtra(0x973F, target));
                    value = bank2[0x173F + target];
                    yield return At(0x9641, 5);
                    _oam.WriteShadowByte(target, value);
                    yield return At(0x9644, value != 0 ? 3 : 2);
                    ready = value != 0;
                }
                if (!ready)
                {
                    yield return At(0x9646, 2);
                    yield return At(0x9648, value == 6 ? 2 : 3);
                    bool alternate = value == 6;
                    if (!alternate)
                    {
                        yield return At(0x9652, 2);
                        yield return At(0x9654, value == 5 ? 3 : 2);
                        alternate = value == 5;
                    }
                    if (alternate)
                    {
                        yield return At(0x964A, 4 + IndexedExtra(0x9807, sourceIndex));
                        value = bank2[0x1807 + sourceIndex];
                        yield return At(0x964D, 5);
                        _oam.WriteShadowByte(target, value);
                        yield return At(0x9650, value != 0 ? 3 : 2);
                        if (value == 0)
                        {
                            yield return At(0x9652, 2);
                            yield return At(0x9654, 2);
                        }
                    }
                    if (!alternate || value == 0)
                    {
                        yield return At(0x9656, 4 + IndexedExtra(0x97DF, sourceIndex));
                        value = bank2[0x17DF + sourceIndex];
                        yield return At(0x9659, 5);
                        _oam.WriteShadowByte(target, value);
                    }
                    yield return At(0x965C, 2);
                    sourceIndex = (sourceIndex + 1) & 255;
                }
                for (int part = 1; part < 4; part++)
                {
                    yield return At((ushort)(0x965D + (part - 1) * 6), 4 + IndexedExtra(0x973F + part, target));
                    value = bank2[0x173F + part + target];
                    yield return At((ushort)(0x9660 + (part - 1) * 6), 5);
                    _oam.WriteShadowByte(target + part, value);
                }
                for (int increment = 0; increment < 4; increment++) yield return At((ushort)(0x966F + increment), 2);
                yield return At(0x9673, 2);
                yield return At(0x9675, target == 0x9C ? 2 : 3);
            }
            yield return At(0x9677, 3);
            _oam.RestoreCursor(0xA0);
            yield return At(0x9679, 6);
        }

        private static int IndexedExtra(int address, int index) => ((address & 255) + index) > 255 ? 1 : 0;

        private IEnumerable<int> CommitYield()
        {
            Stage = Phase.Yield;
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
