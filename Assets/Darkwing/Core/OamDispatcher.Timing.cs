namespace Darkwing.Core
{
    public sealed partial class OamDispatcher
    {
        private static readonly byte[] CulledAnimationNumbers = { 0x13, 0x14, 0x17, 0x18, 0x19, 0x29 };

        /// <summary>CPU cost of bank 5 $8000 through its RTS, including INC $95; supply the
        /// parity after that increment. Excludes the caller's JSR, $C21E and NMI preemption.
        /// Reads native slot/scroll/animation data without advancing any game state.</summary>
        public int MeasureFrameCycles(byte parityAfterIncrement)
            => MeasureFrameCycles(parityAfterIncrement, _bank5, false);

        public int MeasureInternalFrameCycles(byte parityAfterIncrement, byte[] bank4)
            => MeasureFrameCycles(parityAfterIncrement, bank4, true);

        private int MeasureFrameCycles(byte parityAfterIncrement, byte[] bank, bool internalEngine)
        {
            bool reverse = (parityAfterIncrement & 1) != 0;
            int cycles = 5 + 3 + 2 + (reverse ? 3 : 2) + 2 + 3;
            int cursor = MissionCardHold ? 0xA0 : ReservedCursor != 0 ? ReservedCursor : 4;
            for (int index = 0; index < ObjectSystem.SlotCount; index++)
            {
                int slot = reverse ? ObjectSystem.SlotCount - 1 - index : index;
                var state = _objects.Slots[slot];
                cycles += 4 + (state.Active ? 2 : 3);
                if (state.Active) cycles += 6 + MeasureSlotCycles(slot, parityAfterIncrement, bank, internalEngine, ref cursor);
                cycles += 5 + 3 + (reverse ? 0 : 2) + (index == 15 ? 2 : 3);
            }
            return cycles + 6;
        }

        private static int IndexedReadExtra(int address, int index) => ((address & 255) + index) >> 8;

        private int MeasureSlotCycles(int slot, byte parity, byte[] bank, bool internalEngine, ref int cursor)
        {
            var state = _objects.Slots[slot];
            int cycles = 4 + 2;
            byte flags = state.Flags;
            byte screenX, screenY;
            bool animateOffscreen = false;
            if ((flags & 0x10) == 0)
            {
                cycles += 2 + 3 + 4 + 5 + 4 + 5;
                screenX = (byte)state.X;
                screenY = (byte)state.Y;
            }
            else
            {
                cycles += 3 + 4 + 2 + 3 + 5 + 4 + 3;
                int deltaX = (state.X & 255) - (_player.ScrollX & 255);
                int pageX = (state.X >> 8) - (_player.ScrollX >> 8) - (deltaX < 0 ? 1 : 0);
                screenX = (byte)deltaX;
                if ((byte)pageX != 0)
                {
                    cycles += 3 + 4 + (pageX >= 0 ? 3 : 2 + 2 + 2) + 2;
                    int magnitude = pageX >= 0 ? screenX : (byte)-screenX;
                    if (magnitude >= 16) return cycles + 3 + 30;
                    cycles += 2 + 4;
                    foreach (byte anim in CulledAnimationNumbers)
                    {
                        cycles += 2;
                        if (state.AnimNumber == anim) return cycles + 3 + 30;
                        cycles += 2;
                    }
                    return cycles + 4 + 2 + 5 + 6;
                }
                cycles += 2 + 4 + 2 + 3;
                int deltaY = (state.Y & 255) - _player.ScrollY;
                bool borrowY = deltaY < 0;
                cycles += borrowY ? 2 + 2 + 2 : 3;
                screenY = (byte)(deltaY - (borrowY ? 16 : 0));
                int pageY = (byte)((state.Y >> 8) - _player.ScrollYRow - (borrowY ? 1 : 0));
                cycles += 5 + 4 + 3;
                if (pageY != 0)
                {
                    cycles += 2 + 2;
                    if (slot >= 1) return cycles + 3 + 30;
                    cycles += 2 + 3 + 4 + 2 + 5 + 4 + 2;
                    if (pageY == 1) return cycles + 2 + 2 + 3 + 2 + 3 + 3 + 30;
                    cycles += 3;
                    flags &= 0x7F;
                    animateOffscreen = true;
                }
                else cycles += 3;
            }
            if (!animateOffscreen)
            {
                flags |= 0x80;
                cycles += 4 + 2 + 5 + 4;
                if (state.AnimNumber == 0) return cycles + 2 + 6;
                cycles += 3;
            }
            cycles += 2;
            if (slot != 0) cycles += 3;
            else
            {
                cycles += 2 + 3;
                if (_player.InvulnFrames == 0) cycles += 3;
                else
                {
                    cycles += 2 + 5 + 3 + 2;
                    if ((parity & 4) == 0) return cycles + 2 + 6;
                    cycles += 3;
                }
            }
            bool flip = (flags & 0x40) != 0;
            cycles += 2 + 4 + 2 + 3 + (flip ? 2 + 2 : 3) + 3;
            int animLo = internalEngine ? 0xB222 : 0xB429;
            int animHi = internalEngine ? 0xB292 : 0xB4F0;
            int entry = (bank[B5(animLo) + state.AnimNumber] | bank[B5(animHi) + state.AnimNumber] << 8) - 0x8000;
            int entryCpu = entry + 0x8000;
            cycles += 4 + 4 + IndexedReadExtra(animLo, state.AnimNumber) + 3
                + 4 + IndexedReadExtra(animHi, state.AnimNumber) + 3 + 4;
            int frame = state.AnimFrame;
            if ((state.AnimTimer & 0x80) != 0) cycles += 3;
            else
            {
                cycles += 2 + 2 + 7 + 2 + 5 + IndexedReadExtra(entryCpu, 1);
                if ((state.AnimTimer & 0x7F) != bank[entry + 1]) cycles += 3;
                else
                {
                    cycles += 2 + 2 + 5 + 2 + 5 + 2 + 4 + 3 + 7 + 4;
                    if (state.AnimFrame == (bank[entry] & 0x7F))
                    {
                        cycles += 2 + 2 + 5;
                        frame = 0;
                    }
                    else { cycles += 3; frame = (byte)(frame + 1); }
                }
            }
            cycles += 4 + 2;
            if ((flags & 8) != 0) return cycles + 3 + 6;
            cycles += 2 + 4;
            if ((flags & 0x80) == 0) return cycles + 2 + 6;
            cycles += 3 + 4 + 2 + 2 + 2 + 2;
            int frameIndex = (frame & 0x7F) + 2;
            int frameId = bank[entry + frameIndex];
            cycles += 5 + IndexedReadExtra(entryCpu, frameIndex) + 3;
            if (frameId == 0) return cycles + 2 + 5 + 2 + 5 + 6;
            cycles += 3 + 2 + 5;
            bool high = (bank[entry] & 0x80) != 0;
            int lo = internalEngine ? 0xA325 : high ? 0x9C0B : 0x9B0B;
            int hi = internalEngine ? 0xA3C5 : high ? 0x9D45 : 0x9C45;
            cycles += (high ? 2 : 3) + 3 + 4 + IndexedReadExtra(lo, frameId) + 3
                + 4 + IndexedReadExtra(hi, frameId) + 3 + (high && !internalEngine ? 3 : 0);
            int partsCpu = bank[B5(lo) + frameId] | (bank[B5(hi) + frameId] << 8);
            byte[] parts;
            int partsBase;
            if (partsCpu >= 0x8000) { parts = bank; partsBase = B5(partsCpu); }
            else if (partsCpu == 0x05B0 && _hud != null) { parts = _hud.Param; partsBase = 0; }
            else if (!TryRamPartBlock(partsCpu, out parts, out partsBase))
                throw new System.InvalidOperationException("Unmapped OAM part block: " + partsCpu.ToString("X4"));
            int coordId = parts[partsBase + 1] + (flip ? 1 : 0);
            int coordLo = internalEngine ? 0xAF27 : 0xAE53;
            int coordHi = internalEngine ? 0xAFAF : 0xAF23;
            int coordCpu = bank[B5(coordLo) + coordId] | (bank[B5(coordHi) + coordId] << 8);
            cycles += 4 + 3 + 4 + 3 + 2 + 5 + 3 + 2 + 5 + IndexedReadExtra(partsCpu, 1)
                + 2 + 3 + 3 + 4 + 2 + 3 + 4 + 2
                + 4 + IndexedReadExtra(coordLo, coordId) + 2 + 2 + 3
                + 4 + IndexedReadExtra(coordHi, coordId) + 2 + 3;
            int coordBase = B5(coordCpu) - 2;
            int coordPointer = coordCpu - 2;
            cycles += 3;
            if (cursor == 0) return cycles + 3 + 6;
            cycles += 2;
            int partIndex = 1;
            for (int part = 0; part <= parts[partsBase]; part++)
            {
                partIndex++;
                cycles += 2 + 5 + IndexedReadExtra(partsCpu, partIndex) + 5 + 3 + 2
                    + 5 + IndexedReadExtra(coordPointer, partIndex) + 5
                    + 5 + IndexedReadExtra(coordPointer, partIndex);
                int offsetY = bank[coordBase + partIndex];
                bool carryY = screenY + offsetY > 255;
                bool hideY = (offsetY & 128) != 0 ? !carryY : carryY;
                cycles += (offsetY & 128) != 0 ? 3 + (hideY ? 3 : 2)
                    : 2 + (carryY ? 2 + 3 : 3);
                if (hideY)
                {
                    partIndex++;
                    cycles += 2 + 2 + 5 + 3;
                }
                else
                {
                    partIndex++;
                    cycles += 2 + 5 + IndexedReadExtra(partsCpu, partIndex) + 3 + 3 + 5 + 3 + 2
                        + 5 + IndexedReadExtra(coordPointer, partIndex) + 5
                        + 5 + IndexedReadExtra(coordPointer, partIndex);
                    int offsetX = bank[coordBase + partIndex];
                    bool carryX = screenX + offsetX > 255;
                    bool hideX = (offsetX & 128) != 0 ? !carryX : carryX;
                    cycles += (offsetX & 128) != 0 ? 3 + (hideX ? 3 : 2)
                        : 2 + (carryX ? 2 + 3 : 3);
                    if (hideX) cycles += 2 + 5 + 3;
                    else
                    {
                        cursor = (cursor + 4) & 255;
                        cycles += 2 + 2 + 2 + 2 + 3;
                        if (cursor == 0) return cycles + 3 + 6;
                        cycles += 2;
                    }
                }
                cycles += 5 + (part == parts[partsBase] ? 2 : 3);
            }
            return cycles + 6;
        }
    }
}
