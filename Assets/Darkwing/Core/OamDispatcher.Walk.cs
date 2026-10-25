using System;
using System.Collections.Generic;

namespace Darkwing.Core
{
    public sealed partial class OamDispatcher
    {
        private IEnumerator<int> _walk;
        private int _walkNextCycles;

        public bool WalkActive => _walk != null;
        public int WalkCycles { get; private set; }
        public int WalkSlotIndex { get; private set; }
        public int WalkPartIndex { get; private set; }
        public byte WalkParity { get; private set; }
        internal int NextWalkInstructionCycles => _walkNextCycles;
        internal void RestoreCursor(byte cursor) => _oamCursor = cursor;

        public void BeginWalk(byte parity, int cursor = -1, int previousSlot = -1)
        {
            if (WalkActive) throw new InvalidOperationException("OAM walk is already running");
            if (cursor < -1 || cursor > 255) throw new ArgumentOutOfRangeException(nameof(cursor));
            if (previousSlot < -1 || previousSlot > 255) throw new ArgumentOutOfRangeException(nameof(previousSlot));
            _oamCursor = cursor >= 0 ? (byte)cursor : MissionCardHold ? (byte)0xA0 : ReservedCursor != 0 ? ReservedCursor : (byte)4;
            WalkCycles = 0;
            WalkParity = (byte)(parity - 1);
            if (previousSlot >= 0) WalkSlotIndex = previousSlot;
            WalkPartIndex = -1;
            _walk = WalkSlots(parity).GetEnumerator();
            NextWalkInstruction();
        }

        public void AdvanceWalkTo(int deadline)
        {
            if (deadline < 0) throw new ArgumentOutOfRangeException(nameof(deadline));
            while (WalkActive && WalkCycles < deadline)
                StepWalkInstruction();
        }

        internal void StepWalkInstruction()
        {
            if (!WalkActive) throw new InvalidOperationException("OAM walk is not running");
            WalkCycles += _walkNextCycles;
            NextWalkInstruction();
        }

        public void FinishWalk() => AdvanceWalkTo(int.MaxValue);

        private void NextWalkInstruction()
        {
            if (_walk.MoveNext()) _walkNextCycles = _walk.Current;
            else
            {
                _walk.Dispose();
                _walk = null;
                _walkNextCycles = 0;
            }
        }

        private IEnumerable<int> WalkSlots(byte parity)
        {
            yield return 5;
            WalkParity = parity;
            yield return 3;
            yield return 2;
            bool reverse = (parity & 1) != 0;
            yield return reverse ? 3 : 2;
            yield return 2;
            yield return 3;
            WalkSlotIndex = reverse ? 15 : 0;
            for (int index = 0; index < ObjectSystem.SlotCount; index++)
            {
                var state = _objects.Slots[WalkSlotIndex];
                yield return 4;
                bool active = state.Active;
                yield return active ? 2 : 3;
                if (active)
                {
                    yield return 6;
                    foreach (int cycles in WalkSlot(WalkSlotIndex, state, parity)) yield return cycles;
                }
                yield return 5;
                WalkSlotIndex += reverse ? -1 : 1;
                yield return 3;
                if (!reverse) yield return 2;
                yield return index == 15 ? 2 : 3;
            }
            yield return 6;
        }

        private IEnumerable<int> WalkDeactivate(ObjectSystem.Slot state)
        {
            yield return 2;
            yield return 5;
            state.Active = false;
            state.StateLow = 0;
            yield return 5;
            state.HitFlash = 0;
            yield return 5;
            state.BehaviorParam = 0;
            yield return 2;
            yield return 5;
            state.PlacementIndex = 0xFF;
            yield return 6;
        }

        private IEnumerable<int> WalkSlot(int slot, ObjectSystem.Slot state, byte parity)
        {
            WalkPartIndex = -1;
            yield return 4;
            byte flags = state.Flags;
            yield return 2;
            bool animateOffscreen = false;
            if ((flags & 0x10) == 0)
            {
                yield return 2;
                yield return 3;
                yield return 4;
                byte screenX = (byte)state.X;
                yield return 5;
                state.ScreenX = screenX;
                yield return 4;
                byte screenY = (byte)state.Y;
                yield return 5;
                state.ScreenY = screenY;
            }
            else
            {
                yield return 3;
                yield return 4;
                byte objectX = (byte)state.X;
                yield return 2;
                yield return 3;
                int deltaX = objectX - (_player.ScrollX & 255);
                yield return 5;
                state.ScreenX = (byte)deltaX;
                yield return 4;
                byte objectPageX = (byte)(state.X >> 8);
                yield return 3;
                int pageX = objectPageX - (_player.ScrollX >> 8) - (deltaX < 0 ? 1 : 0);
                if ((byte)pageX != 0)
                {
                    yield return 3;
                    yield return 4;
                    int magnitude = state.ScreenX;
                    yield return pageX >= 0 ? 3 : 2;
                    if (pageX < 0)
                    {
                        yield return 2;
                        yield return 2;
                        magnitude = (byte)-magnitude;
                    }
                    yield return 2;
                    if (magnitude >= 16)
                    {
                        yield return 3;
                        foreach (int cycles in WalkDeactivate(state)) yield return cycles;
                        yield break;
                    }
                    yield return 2;
                    yield return 4;
                    byte anim = state.AnimNumber;
                    foreach (byte culled in CulledAnimationNumbers)
                    {
                        yield return 2;
                        bool remove = anim == culled;
                        yield return remove ? 3 : 2;
                        if (!remove) continue;
                        foreach (int cycles in WalkDeactivate(state)) yield return cycles;
                        yield break;
                    }
                    yield return 4;
                    flags = state.Flags;
                    yield return 2;
                    yield return 5;
                    state.Flags = (byte)(flags & 0x7F);
                    yield return 6;
                    yield break;
                }
                yield return 2;
                yield return 4;
                byte objectY = (byte)state.Y;
                yield return 2;
                yield return 3;
                int deltaY = objectY - _player.ScrollY;
                bool borrowY = deltaY < 0;
                yield return borrowY ? 2 : 3;
                if (borrowY)
                {
                    yield return 2;
                    deltaY -= 16;
                    yield return 2;
                }
                yield return 5;
                state.ScreenY = (byte)deltaY;
                yield return 4;
                byte objectPageY = (byte)(state.Y >> 8);
                yield return 3;
                int pageY = (byte)(objectPageY - _player.ScrollYRow - (borrowY ? 1 : 0));
                if (pageY != 0)
                {
                    yield return 2;
                    yield return 2;
                    if (slot >= 1)
                    {
                        yield return 3;
                        foreach (int cycles in WalkDeactivate(state)) yield return cycles;
                        yield break;
                    }
                    yield return 2;
                    yield return 3;
                    yield return 4;
                    flags = state.Flags;
                    yield return 2;
                    yield return 5;
                    state.Flags = (byte)(flags & 0x7F);
                    yield return 4;
                    yield return 2;
                    if (pageY == 1)
                    {
                        yield return 2;
                        yield return 2;
                        yield return 3;
                        _player.InvulnFrames = 0;
                        yield return 2;
                        yield return 3;
                        _player.State = 7;
                        yield return 3;
                        foreach (int cycles in WalkDeactivate(state)) yield return cycles;
                        yield break;
                    }
                    yield return 3;
                    animateOffscreen = true;
                }
                else yield return 3;
            }
            if (!animateOffscreen)
            {
                yield return 4;
                flags = state.Flags;
                yield return 2;
                yield return 5;
                state.Flags = (byte)(flags | 0x80);
                yield return 4;
                byte anim = state.AnimNumber;
                yield return anim == 0 ? 2 : 3;
                if (anim == 0) { yield return 6; yield break; }
            }
            yield return 2;
            yield return slot == 0 ? 2 : 3;
            if (slot == 0)
            {
                yield return 3;
                byte invulnerable = _player.InvulnFrames;
                yield return invulnerable == 0 ? 3 : 2;
                if (invulnerable != 0)
                {
                    yield return 5;
                    _player.InvulnFrames--;
                    yield return 3;
                    yield return 2;
                    yield return (parity & 4) == 0 ? 2 : 3;
                    if ((parity & 4) == 0) { yield return 6; yield break; }
                }
            }
            yield return 2;
            yield return 4;
            flags = state.Flags;
            yield return 2;
            byte flip = (byte)(flags & 0x40);
            yield return 3;
            _player.ScrollStep = flip;
            yield return flip == 0 ? 3 : 2;
            if (flip != 0) yield return 2;
            yield return 3;
            yield return 4;
            byte animation = state.AnimNumber;
            yield return 4 + IndexedReadExtra(UseBank4Tables ? 0xB222 : 0xB429, animation);
            yield return 3;
            yield return 4 + IndexedReadExtra(UseBank4Tables ? 0xB292 : 0xB4F0, animation);
            yield return 3;
            int entry = AnimEntryAddress(animation);
            yield return 4;
            byte timer = state.AnimTimer;
            yield return (timer & 0x80) != 0 ? 3 : 2;
            if ((timer & 0x80) == 0)
            {
                yield return 2;
                byte previous = (byte)(timer & 0x7F);
                yield return 7;
                state.AnimTimer++;
                yield return 2;
                yield return 5 + IndexedReadExtra(entry + 0x8000, 1);
                bool advance = previous == Bank[entry + 1];
                yield return advance ? 2 : 3;
                if (advance)
                {
                    yield return 2;
                    yield return 5;
                    state.AnimTimer = 0;
                    yield return 2;
                    yield return 5;
                    yield return 2;
                    yield return 4;
                    bool wrap = state.AnimFrame == (Bank[entry] & 0x7F);
                    yield return 3;
                    yield return 7;
                    state.AnimFrame++;
                    yield return 4;
                    yield return wrap ? 2 : 3;
                    if (wrap)
                    {
                        yield return 2;
                        yield return 5;
                        state.AnimFrame = 0;
                    }
                }
            }
            foreach (int cycles in WalkParts(state)) yield return cycles;
        }

        private IEnumerable<int> WalkParts(ObjectSystem.Slot state)
        {
            yield return 4;
            byte flags = state.Flags;
            yield return 2;
            yield return (flags & 8) != 0 ? 3 : 2;
            if ((flags & 8) != 0) { yield return 6; yield break; }
            yield return 4;
            flags = state.Flags;
            yield return (flags & 0x80) == 0 ? 2 : 3;
            if ((flags & 0x80) == 0) { yield return 6; yield break; }
            yield return 4;
            int frameIndex = state.AnimFrame;
            yield return 2;
            frameIndex &= 0x7F;
            yield return 2;
            yield return 2;
            frameIndex += 2;
            yield return 2;
            int entry = AnimEntryAddress(state.AnimNumber);
            yield return 5 + IndexedReadExtra(entry + 0x8000, frameIndex);
            int frameId = Bank[entry + frameIndex];
            yield return 3;
            yield return frameId == 0 ? 2 : 3;
            if (frameId == 0)
            {
                yield return 5;
                state.Active = false;
                state.StateLow = 0;
                yield return 2;
                yield return 5;
                state.PlacementIndex = 0xFF;
                yield return 6;
                yield break;
            }
            yield return 2;
            yield return 5;
            // Bank 4's composer has no entry-bit7 split ($995B-$996B: the part CPU always
            // comes from $A325/$A3C5; no bank-4 entry carries bit 7).
            bool high = !UseBank4Tables && (Bank[entry] & 0x80) != 0;
            yield return high ? 2 : 3;
            yield return 3;
            int tableLo = UseBank4Tables ? 0xA325 : high ? 0x9C0B : 0x9B0B;
            int tableHi = UseBank4Tables ? 0xA3C5 : high ? 0x9D45 : 0x9C45;
            yield return 4 + IndexedReadExtra(tableLo, frameId);
            yield return 3;
            yield return 4 + IndexedReadExtra(tableHi, frameId);
            yield return 3;
            if (high) yield return 3;
            int partsCpu = Bank[B5(tableLo) + frameId] | (Bank[B5(tableHi) + frameId] << 8);
            byte[] parts;
            int partsBase;
            if (partsCpu >= 0x8000) { parts = Bank; partsBase = B5(partsCpu); }
            else if (partsCpu == 0x05B0 && _hud != null) { parts = _hud.Param; partsBase = 0; }
            else if (!TryRamPartBlock(partsCpu, out parts, out partsBase))
                throw new InvalidOperationException("Unmapped OAM part block: " + partsCpu.ToString("X4"));
            yield return 4;
            byte relX = state.ScreenX;
            yield return 3;
            yield return 4;
            byte relY = state.ScreenY;
            yield return 3;
            yield return 2;
            yield return 5;
            int count = parts[partsBase];
            yield return 3;
            yield return 2;
            yield return 5 + IndexedReadExtra(partsCpu, 1);
            int coordId = parts[partsBase + 1];
            yield return 2;
            yield return 3;
            coordId += (_player.ScrollStep & 0x40) != 0 ? 1 : 0;
            yield return 3;
            yield return 4;
            flags = state.Flags;
            yield return 2;
            byte priority = (byte)(flags & 0x20);
            byte flip = _player.ScrollStep;
            yield return 3;
            yield return 4;
            yield return 2;
            int coordLo = UseBank4Tables ? 0xAF27 : 0xAE53;
            int coordHi = UseBank4Tables ? 0xAFAF : 0xAF23;
            yield return 4 + IndexedReadExtra(coordLo, coordId);
            yield return 2;
            yield return 2;
            yield return 3;
            yield return 4 + IndexedReadExtra(coordHi, coordId);
            yield return 2;
            yield return 3;
            int coordsCpu = (Bank[B5(coordLo) + coordId] | (Bank[B5(coordHi) + coordId] << 8)) - 2;
            int coordsBase = B5(coordsCpu);
            yield return 3;
            int cursor = _oamCursor;
            yield return cursor == 0 ? 3 : 2;
            if (cursor == 0) { yield return 6; yield break; }
            int partIndex = 1;
            for (int part = 0; part <= count; part++)
            {
                yield return 2;
                partIndex = (partIndex + 1) & 255;
                WalkPartIndex = partIndex;
                yield return 5 + IndexedReadExtra(partsCpu, partIndex);
                byte tile = parts[partsBase + partIndex];
                yield return 5;
                _shadow[cursor + 1] = tile;
                yield return 3;
                yield return 2;
                yield return 5 + IndexedReadExtra(coordsCpu, partIndex);
                int sumY = relY + Bank[coordsBase + partIndex];
                bool carryY = sumY > 255;
                yield return 5;
                _shadow[cursor] = (byte)sumY;
                yield return 5 + IndexedReadExtra(coordsCpu, partIndex);
                bool negativeY = (Bank[coordsBase + partIndex] & 0x80) != 0;
                yield return negativeY ? 3 : 2;
                bool hiddenY = negativeY ? !carryY : carryY;
                yield return negativeY ? hiddenY ? 3 : 2 : carryY ? 2 : 3;
                if (!negativeY && carryY) yield return 3;
                if (hiddenY)
                {
                    yield return 2;
                    partIndex = (partIndex + 1) & 255;
                    WalkPartIndex = partIndex;
                    yield return 2;
                    yield return 5;
                    _shadow[cursor] = 0xF8;
                    yield return 3;
                }
                else
                {
                    yield return 2;
                    partIndex = (partIndex + 1) & 255;
                    WalkPartIndex = partIndex;
                    yield return 5 + IndexedReadExtra(partsCpu, partIndex);
                    byte attributes = parts[partsBase + partIndex];
                    yield return 3;
                    attributes ^= flip;
                    yield return 3;
                    attributes |= priority;
                    yield return 5;
                    _shadow[cursor + 2] = attributes;
                    yield return 3;
                    yield return 2;
                    yield return 5 + IndexedReadExtra(coordsCpu, partIndex);
                    int sumX = relX + Bank[coordsBase + partIndex];
                    bool carryX = sumX > 255;
                    yield return 5;
                    _shadow[cursor + 3] = (byte)sumX;
                    yield return 5 + IndexedReadExtra(coordsCpu, partIndex);
                    bool negativeX = (Bank[coordsBase + partIndex] & 0x80) != 0;
                    yield return negativeX ? 3 : 2;
                    bool hiddenX = negativeX ? !carryX : carryX;
                    yield return negativeX ? hiddenX ? 3 : 2 : carryX ? 2 : 3;
                    if (!negativeX && carryX) yield return 3;
                    if (hiddenX)
                    {
                        yield return 2;
                        yield return 5;
                        _shadow[cursor] = 0xF8;
                        yield return 3;
                    }
                    else
                    {
                        for (int increment = 0; increment < 4; increment++)
                        {
                            yield return 2;
                            cursor = (cursor + 1) & 255;
                        }
                        yield return 3;
                        _oamCursor = (byte)cursor;
                        yield return cursor == 0 ? 3 : 2;
                        if (cursor == 0) { yield return 6; yield break; }
                    }
                }
                yield return 5;
                bool more = (((byte)(count - part - 1)) & 0x80) == 0;
                yield return more ? 3 : 2;
                if (!more) break;
            }
            yield return 6;
        }
    }
}
