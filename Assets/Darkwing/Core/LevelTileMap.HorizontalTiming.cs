using System;

namespace Darkwing.Core
{
    public sealed partial class LevelTileMap
    {
        public int MeasureHorizontalBandCycles(byte cursor, byte gridIndex, ref byte descriptor)
        {
            int macro = ((cursor & 7) << 3) | ((cursor & 0x78) >> 4);
            int cycles = 173 + 6 + RoomPointerCycles(gridIndex) + 6 + MeasureMacroExpansionCycles(gridIndex, macro) + 11;
            int payload = (byte)(descriptor + 3);
            cycles += 0x5E + payload > 255 ? 1 : 0;
            for (int tileRow = 0; tileRow < 4; tileRow++)
            {
                cycles += 38 + TakenBranch(0xE72A, 0xE713, tileRow != 3);
                payload = (byte)(payload + 5);
            }
            bool trimmed = macro >= 0x38;
            cycles += 5 + TakenBranch(0xE730, 0xE742, !trimmed);
            if (trimmed)
            {
                cycles += 8;
                payload = (byte)(payload - 13);
                for (int copied = 0; copied < 3; copied++)
                {
                    cycles += 13 + (0x6A + payload > 255 ? 1 : 0) + TakenBranch(0xE740, 0xE738, copied != 2);
                    payload = (byte)(payload + 1);
                }
            }
            cycles += 82 + TakenBranch(0xE778, 0xE77F, (cursor & 8) != 0);
            cycles += (cursor & 8) != 0 ? 5 : 6;
            cycles += 30;
            descriptor = (byte)(payload + 1);
            return cycles;
        }

        public int MeasureHorizontalStreamCycles(byte scroll, byte last, byte mode, byte previousMode, byte cursor, byte gridIndex, bool? renderingBlanked = null)
        {
            int difference = (sbyte)(byte)(scroll - last);
            int cycles = 8 + TakenBranch(0xE641, 0xE648, difference >= 0);
            if (difference < 0) { difference = -difference; cycles += 6; }
            bool left = (mode & 2) != 0;
            cycles += 8 + TakenBranch(0xE64E, 0xE65D, left);
            int passes = (difference + ((left ? last ^ 255 : last) & 1)) >> 1;
            cycles += left ? 17 : 15;
            cycles += TakenBranch(left ? 0xE669 : 0xE65A, 0xE66C, passes != 0);
            if (passes == 0) return cycles + 6;
            if (renderingBlanked != false) throw new NotSupportedException("E7A8 native blank owner unavailable or set");
            cycles += 15 + TakenBranch(0xE676, 0xE698, mode == previousMode);
            if (mode != previousMode)
            {
                int direction = mode & 2;
                int beforeBit = cursor & 128;
                cursor = (byte)(cursor + (left ? 0x6F : 0x91));
                bool samePage = (cursor & 128) == beforeBit;
                cycles += 33 + TakenBranch(0xE68D, 0xE690, samePage) + (samePage ? 0 : 2) + 12;
                gridIndex = (byte)(gridIndex + CellStepOnFlipForTiming(direction + (samePage ? 0 : 1)));
            }
            cycles += 2;
            byte descriptor = 0;
            for (int pass = 0; pass < passes; pass++)
            {
                cycles += MeasureHorizontalBandCycles(cursor, gridIndex, ref descriptor);
                cursor = (byte)(cursor + (left ? -1 : 1));
                bool boundary = (cursor & 127) == (left ? 127 : 0);
                cycles += TakenBranch(0xE797, 0xE7A1, !boundary);
                if (boundary) { cycles += 12; gridIndex = (byte)(gridIndex + (left ? -1 : 1)); }
                cycles += 5 + TakenBranch(0xE7A3, 0xE7A8, pass == passes - 1);
                if (pass != passes - 1) cycles += 3;
            }
            return cycles + 3 + TakenBranch(0xE7AA, 0xE7AF, true) + 5 + 3 + 3 + 6;
        }

        private static int CellStepOnFlipForTiming(int index)
        {
            return index == 0 ? 2 : index == 1 ? 1 : index == 2 ? -2 : -1;
        }
    }

}
