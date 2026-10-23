namespace Darkwing.Core
{
    public sealed partial class PlayerCore
    {
        public int LastScrollServiceCycles { get; private set; } = -1;
        public string ScrollTimingUnsupported { get; private set; }
        private int _scrollServiceCycles;
        private bool _scrollTimingActive;
        private bool _scrollTimingSupported;

        private static int ScrollBranch(int address, int target, bool taken)
        {
            return taken ? 3 + (((address + 2) & 0xFF00) == (target & 0xFF00) ? 0 : 1) : 2;
        }

        private int ScrollIndirectCycles(ushort pointer, int index)
        {
            return 5 + (((pointer & 255) + (byte)index) > 255 ? 1 : 0);
        }

        private void RejectScrollTiming(string reason)
        {
            _scrollTimingSupported = false;
            ScrollTimingUnsupported = reason;
            LastScrollServiceCycles = -1;
        }

        private void BeginScrollServiceTiming()
        {
            LastScrollServiceCycles = -1;
            ScrollTimingUnsupported = null;
            _scrollTimingActive = _scrollTimingSupported = true;
            if (!(_tiles is LevelTileMap)) { RejectScrollTiming("DA70 world addresses unavailable"); return; }
            if (TimingDeferredBankAudio) { RejectScrollTiming("CA68 FF55 deferred audio F6"); return; }
            if (State == 7 || State == 8 || State == 0x12) { RejectScrollTiming("DA70 camera-lock return"); return; }
            _scrollServiceCycles = 6 + 4 + 3 + 4 + 3 + 2 + 6 + 55;
            _scrollServiceCycles += 3 + 2 + 3 + 2 + 2 + 2 + 2 + 34;
            int ahead = (byte)(X - ScrollX);
            bool stationary = X == PrevPlayerX;
            _scrollServiceCycles += ScrollBranch(0xDA96, 0xDAFD, stationary);
            if (stationary) return;
            bool right = X >= PrevPlayerX;
            _scrollServiceCycles += ScrollBranch(0xDA98, 0xDACF, right);
            if (right)
            {
                _scrollServiceCycles += 3 + 2 + ScrollBranch(0xDAD3, 0xDAFD, ahead < 0x89);
                if (ahead >= 0x89) RejectScrollTiming("DBCE horizontal camera movement");
            }
            else
            {
                _scrollServiceCycles += 3 + 2 + 2 + 3 + 3 + 2 + ScrollBranch(0xDAA6, 0xDAFD, ahead >= 0x88);
                if (ahead < 0x88) RejectScrollTiming("DC19 horizontal camera movement");
            }
        }

        private void BeginHookTiming()
        {
            if (!_scrollTimingActive || !_scrollTimingSupported) return;
            var map = (LevelTileMap)_tiles;
            int script = CellScriptByte;
            int read = ScrollIndirectCycles(map.World.FlagsAddress, CellIndex);
            _scrollServiceCycles += 3 + read + 3 + 2 + 2;
            bool checkpoint = (script & 0xCF) >= 0xC1;
            _scrollServiceCycles += ScrollBranch(0xDB07, 0xDB29, !checkpoint);
            if (checkpoint)
            {
                _scrollServiceCycles += read;
                bool skipped = false;
                for (int candidate = 0xC2; candidate <= 0xCE; candidate += 2)
                {
                    int branch = 0xDB0D + (candidate - 0xC2) * 2;
                    _scrollServiceCycles += 2 + ScrollBranch(branch, 0xDB29, script == candidate);
                    if (script == candidate) { skipped = true; break; }
                }
                if (!skipped) _scrollServiceCycles += 3;
            }
            bool misaligned = (byte)ScrollX != 0;
            _scrollServiceCycles += 3 + ScrollBranch(0xDB2B, 0xDB56, misaligned);
            if (misaligned) { _scrollServiceCycles += 6; return; }
            int kind = script & 15;
            _scrollServiceCycles += 2 + 3 + 3 + read + 2 + 2 + ScrollBranch(0xDB39, 0xDB6D, kind == 1);
            if (kind == 1) { RejectScrollTiming("DB6D hook/downward ride"); return; }
            _scrollServiceCycles += 2 + ScrollBranch(0xDB3D, 0xDB56, kind != 2);
            if (kind != 2) { _scrollServiceCycles += 6; return; }
            _scrollServiceCycles += 2 + 3 + 4 + 2 + 3;
            int distance = (byte)Y - ScrollY;
            _scrollServiceCycles += ScrollBranch(0xDB49, 0xDB4E, distance >= 0);
            if (distance < 0) { distance = (byte)(distance - 0x10); _scrollServiceCycles += 2 + 2; }
            _scrollServiceCycles += 3 + 3 + 2 + ScrollBranch(0xDB54, 0xDB57, distance < 0x48);
            if (distance >= 0x48) { _scrollServiceCycles += 6; return; }
            int window = 0x48 - distance;
            _scrollServiceCycles += 2 + 2 + 3 + ScrollBranch(0xDB5C, 0xDB56, window < 0);
            if (window < 0) { _scrollServiceCycles += 6; return; }
            _scrollServiceCycles += 2 + ScrollBranch(0xDB60, 0xDB64, window < 9);
            if (window >= 9) _scrollServiceCycles += 2;
            _scrollServiceCycles += 3 + 2 + 3 + 3;
        }

        private void AddVerticalUpTiming()
        {
            if (!_scrollTimingActive || !_scrollTimingSupported) return;
            var map = (LevelTileMap)_tiles;
            _scrollServiceCycles += 3 + ScrollBranch(0xDCC6, 0xDCD8, ScrollY != 0);
            if (ScrollY == 0)
            {
                int above = (byte)(CellIndex - RowStride);
                bool empty = GridAt(above) == 0;
                _scrollServiceCycles += 3 + 2 + 2 + 3 + 2 + ScrollIndirectCycles(map.World.GridAddress, above);
                _scrollServiceCycles += ScrollBranch(0xDCD1, 0xDCD7, empty);
                if (empty) { _scrollServiceCycles += 6; return; }
                _scrollServiceCycles += 3 + ScrollBranch(0xDCD5, 0xDCD8, ScrollYRow != 0);
                if (ScrollYRow == 0) { _scrollServiceCycles += 6; return; }
            }
            bool armed = (ScrollState & 12) != 0;
            _scrollServiceCycles += 6 + 3 + 2 + ScrollBranch(0xDD3A, 0xDD53, armed);
            if (armed) _scrollServiceCycles += 2 + 6;
            else
            {
                bool rejected = (byte)ScrollX != 0;
                _scrollServiceCycles += 3 + ScrollBranch(0xDD3E, 0xDD55, rejected);
                if (rejected) { _scrollServiceCycles += 2 + 6 + 3 + 6; return; }
                _scrollServiceCycles += 3 + 2 + 3 + 3 + 2 + 3 + 3 + 2 + 3 + 3 + 2 + 6;
            }
            _scrollServiceCycles += 2 + 3 + 2 + 3 + 3;
            int next = ScrollY - ScrollStep;
            _scrollServiceCycles += ScrollBranch(0xDCE4, 0xDD14, next >= 0);
            if (next >= 0) { _scrollServiceCycles += 3; return; }
            RejectScrollTiming("DCE6 upward camera row wrap");
        }

        private void AddVerticalStreamTiming(LevelTileMap map)
        {
            if (!_scrollTimingActive || !_scrollTimingSupported) return;
            if (map.LastVerticalStreamCycles < 0) RejectScrollTiming(map.VerticalTimingUnsupported);
            else _scrollServiceCycles += map.LastVerticalStreamCycles;
        }

        private void FinishScrollServiceTiming()
        {
            if (!_scrollTimingActive) return;
            _scrollTimingActive = false;
            if (_scrollTimingSupported) LastScrollServiceCycles = _scrollServiceCycles;
        }
    }
}
