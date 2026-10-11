namespace Darkwing.Core
{
    /// <summary>
    /// Dialog text player, transcription of bank 2 $AA00: stream $62 indexes the
    /// pointer tables ($ABAA lo/$ABBD hi in bank 2 data); stream bytes are font
    /// tile ids ($FE = line break, $FF = end); 3 lines per page written at the PPU
    /// addresses in the line-base table ($AAD7/$AADA); one character per frame via
    /// the $0360 VRAM queue with flush flag $19; every 4th frame plays SFX $3F.
    /// </summary>
    public sealed class TextEngine
    {
        private readonly Ppu _ppu;
        private readonly byte[] _bank2;      // dialog data (bank 2 raw)
        private readonly byte[] _queue = new byte[32];
        private readonly int[] _lineAddr = new int[3];

        private int _streamPtr;          // ($0C/$0D) stream position
        private int _streamBase;         // start of current stream data
        private int _lineIndex;          // $10 (0-2)
        private int _lineOffset;         // $11 (column offset accumulator)
        private bool _active;
        public int StreamId = -1;        // $62
        public bool FrameBlip;           // SFX $3F trigger for this frame

        // Line base addresses $AAD7(lo)/$AADA(hi) tables in bank 2, 3 entries.
        private static readonly int[] LinePpuBase = { 0x2000, 0x2000, 0x2000 }; // patched in ctor

        public TextEngine(Ppu ppu, byte[] bank2)
        {
            _ppu = ppu;
            _bank2 = bank2;
            int b2 = 2 * 0x4000;
            int lo = b2 + (0xAAD7 - 0x8000);
            int hi = b2 + (0xAADA - 0x8000);
            for (int i = 0; i < 3; i++)
            {
                _lineAddr[i] = 0x2000 | (_bank2[hi + i] << 8) | _bank2[lo + i];
            }
        }

        public bool Active => _active;

        public void Start(int streamId)
        {
            StreamId = streamId;
            int b2 = 2 * 0x4000;
            _streamBase = b2 + (_bank2[b2 + (0xABBD - 0x8000) + streamId] << 8)
                              + _bank2[b2 + (0xABAA - 0x8000) + streamId] - 0x8000;
            _streamPtr = _streamBase;
            _lineIndex = 0;
            _lineOffset = 0;
            _active = true;
        }

        /// <summary>One frame of text output. skip: Start pressed (streams 0E/0F ignore).</summary>
        public void Tick(bool skip)
        {
            FrameBlip = false;
            if (!_active) return;
            if (skip && StreamId != 0x0E && StreamId != 0x0F)
            {
                _active = false; // skip ends the stream page ($AA2B RTS)
                return;
            }
            byte b = _bank2[_streamPtr];
            _streamPtr++;
            if (b == 0xFF) { _active = false; return; }      // end of stream
            if (b == 0xFE)
            {                          // line break
                _lineIndex++;
                if (_lineIndex >= 3)
                {
                    _lineIndex = 0;
                    _lineOffset = 0;
                    // page break: wait for the player ($AB57) — world layer handles
                }
                return;
            }
            // Write one character at line base + lineOffset via the VRAM queue.
            int addr = _lineAddr[_lineIndex] + _lineOffset;
            _queue[0] = (byte)(addr >> 8);
            _queue[1] = (byte)addr;
            _queue[2] = 1;
            _queue[3] = b;
            _queue[4] = 0xFF;
            _ppu.FlushQueue(_queue);
            _lineOffset++;
            // Typewriter blip: every 4th frame counter ($92 & 3 == 0) → SFX $3F.
            // The caller supplies the frame parity via Frame property.
        }
    }
}
