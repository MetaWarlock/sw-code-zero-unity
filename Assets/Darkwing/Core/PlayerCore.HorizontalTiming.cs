using System;

namespace Darkwing.Core
{
    public sealed partial class PlayerCore
    {
        private void AddRightScrollTiming(int ahead)
        {
            if (!_scrollTimingActive || !_scrollTimingSupported) return;
            try
            {
                var map = (LevelTileMap)_tiles;
                byte cell = CellIndex;
                byte scroll = (byte)ScrollX;
                byte next = CellIndexNext;
                int step = (byte)(X - PrevPlayerX);
                bool allowed = (map.FlagByte((byte)(cell + 1)) & 0x40) != 0;
                int cycles = 3 + 2 + ScrollIndirectCycles(map.World.FlagsAddress, (byte)(cell + 1)) + 2;
                cycles += ScrollBranch(0xDBD5, 0xDC12, !allowed);
                if (!allowed) { FinishRightCallerTiming(cycles + 8, false, ahead); return; }
                cycles += 3 + ScrollBranch(0xDBD9, 0xDBDF, scroll != 0);
                if (scroll == 0)
                {
                    bool empty = GridAt((byte)(cell + 1)) == 0;
                    cycles += ScrollIndirectCycles(map.World.GridAddress, (byte)(cell + 1)) + ScrollBranch(0xDBDD, 0xDC12, empty);
                    if (empty) { FinishRightCallerTiming(cycles + 8, false, ahead); return; }
                }
                cycles += 2 + 3 + ScrollBranch(0xDBE3, 0xDBE9, step <= 6);
                if (step > 6) { step = 1; cycles += 5; }
                cycles += 6 + 3 + 2 + ScrollBranch(0xDD1B, 0xDD30, (ScrollState & 3) != 0);
                bool armed = true;
                if ((ScrollState & 3) == 0)
                {
                    armed = ScrollY == 0;
                    cycles += 3 + ScrollBranch(0xDD1F, 0xDD32, !armed);
                    if (armed)
                    {
                        cycles += 22;
                        next = (byte)(cell + ((PrevScrollMode & 1) != 0 ? 1 : -1));
                    }
                }
                cycles += 8 + ScrollBranch(0xDBEC, 0xDC12, !armed);
                if (!armed) { FinishRightCallerTiming(cycles + 8, false, ahead); return; }
                cycles += 43 + ScrollIndirectCycles(map.World.GridAddress, (byte)(cell + (scroll + step > 255 ? 2 : 1))) - 5;
                if (scroll + step > 255) cell++;
                scroll = (byte)(scroll + step);
                bool nextEmpty = GridAt((byte)(cell + 1)) == 0;
                cycles += ScrollBranch(0xDC07, 0xDC0B, nextEmpty);
                if (nextEmpty) { cycles += 8; scroll = 0; }
                else cycles += ScrollBranch(0xDC09, 0xDC14, true);
                cycles += 6 + map.MeasureHorizontalStreamCycles(scroll, LastRedrawScroll, 1, PrevScrollMode, StageCursor, next, TimingRenderingBlanked?.Invoke()) + 8;
                FinishRightCallerTiming(cycles, true, ahead);
            }
            catch (NotSupportedException exception) { RejectScrollTiming(exception.Message); }
        }

        private void FinishRightCallerTiming(int subroutine, bool carried, int ahead)
        {
            _scrollServiceCycles += 27 + subroutine + ScrollBranch(0xDAE4, 0xDAFD, carried);
            if (carried) return;
            _scrollServiceCycles += 8 + ScrollBranch(0xDAEC, 0xDAFD, ahead < 0xF1);
            if (ahead >= 0xF1) _scrollServiceCycles += 20;
        }
    }
}
