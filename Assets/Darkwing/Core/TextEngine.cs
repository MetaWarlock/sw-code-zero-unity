namespace Darkwing.Core
{
    /// <summary>
    /// Dialog text player — the bank-2 coroutine at $AA00 (called from $C60F/$C61B for the
    /// intro streams $0E/$0F, from $C7DD, $9E64, $A948 and $D9EF for the rest).
    ///
    /// ROM truth used here (disasm/bank2_AA00.txt, verified against the original-ROM capture
    /// reference/gold_briefing_b0):
    ///   $AA00  JSR $AADD            — clear the three text lines, yields one frame
    ///   $AA03  LDY $62 / LDA $ABAA,Y / STA $0C / LDA $ABBD,Y / STA $0D
    ///                               — stream pointer pair, low at $ABAA, high at $ABBD
    ///   $AA11/$AA15/$AA19           — $0E (byte index) = $10 (line) = $11 (column) = 0
    ///   $AA1B  for $62 not in {$0E,$0F}: LDA $14 / AND #$10 / BEQ work / RTS
    ///                               — holding START freezes the typewriter (it does not skip)
    ///   $AA2C  LDX $10 / LDA $AAD7,X / STA $0360 / LDA $AADA,X / CLC / ADC $11 / STA $0361
    ///                               — the VRAM queue head is [hi][lo][count][data...] ($C119):
    ///                                 $AAD7 = {22,23,23} is the HIGH byte, $AADA = {E2,22,62}
    ///                                 the LOW byte, so the lines are $22E2/$2322/$2362 and the
    ///                                 column counter is added to the low byte
    ///   $AA42  LDY $0E / INC $0E / LDA ($0C),Y   — one stream byte per frame
    ///   $AA48  $FF → $AA9E end: $AB57 yields one frame for $0E/$0F and RTS's to the caller
    ///   $AA4C  $FE → $AA7D INC $10, $11 = 0 and the NEXT byte is emitted in the SAME frame
    ///   $AA50  otherwise: tile → $0363, $0364 = $FF terminator, $19 = $FF flush flag,
    ///          INC $11, and `LDA $92 / AND #$03 / BNE` → SFX $3F via $F850
    ///   $AA85  third line break → $AB57 (one frame for $0E/$0F) + $AADD clear + restart at
    ///          line 0, column 0 WITHOUT resetting the byte index (that is a page flip).
    /// </summary>
    public sealed class TextEngine
    {
        // Bank-2 addresses. Every exported bank asset is exactly one 16 KB PRG bank mapped at
        // $8000-$BFFF, so the array index of an address is `addr - $8000` (no bank number).
        private const int PtrLoTable = 0xABAA - 0x8000;   // $ABAA stream pointer, low bytes
        private const int PtrHiTable = 0xABBD - 0x8000;   // $ABBD stream pointer, high bytes
        private const int LineHiTable = 0xAAD7 - 0x8000;  // $AAD7 line base, high bytes
        private const int LineLoTable = 0xAADA - 0x8000;  // $AADA line base, low bytes
        private const int LineCount = 3;
        private const int LineTiles = 29;                 // $AADD clears 29 tiles per line
        private const int StartBit = 0x10;                // $14 bit4 = START
        private const int ConfirmBits = 0xD0;             // $AB6D AND #$D0 = A | B | Start

        private readonly Ppu _ppu;
        private readonly byte[] _bank2;
        private readonly int[] _lineAddr = new int[LineCount];
        private readonly byte[] _queue = new byte[3 + LineTiles + 1];

        private int _streamId = -1;      // $62
        private int _ptr;                // $0C/$0D
        private int _byteIndex;          // $0E
        private int _line;               // $10
        private int _column;             // $11
        private int _frame;              // $92 (the caller's frame counter, used for the blip)
        private int _pendingYields;      // frames spent inside $AB57/$AADD before work resumes
        private bool _cleared;           // $AA00's opening JSR $AADD has run
        private bool _running;
        private bool _waiting, _endWait, _mapMode;
        private bool _initialGate;
        public bool WalkRequired { get; private set; }
        public bool DeferWrites;
        private readonly System.Collections.Generic.List<(int addr, byte tile)> _pendingWrites
            = new System.Collections.Generic.List<(int, byte)>();

        public void FlushNmiWrites()
        {
            foreach (var w in _pendingWrites) _ppu.WriteVram(w.addr, w.tile);
            _pendingWrites.Clear();
        }

        public TextEngine(Ppu ppu, byte[] bank2)
        {
            _ppu = ppu;
            _bank2 = bank2;
            for (int i = 0; i < LineCount; i++)
                _lineAddr[i] = (_bank2[LineHiTable + i] << 8) | _bank2[LineLoTable + i];
        }

        /// <summary>The three line bases read from the ROM: $22E2, $2322, $2362.</summary>
        public int[] LineAddresses => _lineAddr;

        public int StreamId => _streamId;
        public bool Running => _running;

        /// <summary>True while the coroutine sits in the $AB57 one-frame yield (or the
        /// $AADD clear): the ROM advances $92 but emits no character.</summary>
        public bool Yielding => _pendingYields > 0;

        /// <summary>Set when $AA00 hit its $FF terminator and RTS'd back to its caller
        /// ($C612 for the intro, where a 30-frame $FEFF delay then starts stream $0F).</summary>
        public bool Done { get; private set; }

        /// <summary>Non-zero on the frame the ROM enqueues SFX $3F through $F850.</summary>
        public int SfxToPlay { get; private set; }

        /// <summary>Bytes consumed from the stream ($0E), lines ($10) and columns ($11) —
        /// the same three counters the original-ROM capture exposes in RAM.</summary>
        public int ByteIndex => _byteIndex;
        public int Line => _line;
        public int Column => _column;

        /// <summary>Queue a stream the way `LDA #$xx / STA $62 / JSR $AA00` does: the first
        /// thing that happens is the $AADD text-box clear, which costs one frame.</summary>
        public void Start(int streamId, bool map = false)
        {
            _streamId = streamId;
            _mapMode = map;
            for (int i = 0; i < LineCount; i++)
                _lineAddr[i] = (_bank2[(map ? 0xA38C : 0xAAD7) - 0x8000 + i] << 8)
                             | _bank2[(map ? 0xA38F : 0xAADA) - 0x8000 + i];
            _ptr = ((_bank2[PtrHiTable + streamId] << 8) | _bank2[PtrLoTable + streamId]) - 0x8000;
            _byteIndex = 0;
            _line = 0;
            _column = 0;
            _cleared = false;
            _pendingYields = 1;      // $AADD ends in JMP $FF06 (ProcessYield)
            _waiting = _endWait = false;
            _running = true;
            Done = false;
            _initialGate = true;
            if (DeferWrites) { ClearTextLines(); _cleared = true; }
        }

        /// <summary>One frame of the coroutine. `padHeld` is the live $14 strobe byte.</summary>
        public void Tick(int frameCounter, byte padHeld)
        {
            SfxToPlay = 0;
            WalkRequired = false;
            if (!_running) return;
            _frame = frameCounter;

            // $AB57 stays resident until A/B/Start ($14 & $D0), for both page
            // breaks and the terminator. It does not auto-advance ordinary dialogs.
            if (_waiting)
            {
                WriteTile(_mapMode ? 0x2379 : 0x22FE, (byte)((_frame & 0x10) == 0 ? 0xFE : 0x02));
                if ((padHeld & (_mapMode ? 0xC3 : ConfirmBits)) == 0) { WalkRequired = true; return; }
                _waiting = false;
                if (_endWait || (!_mapMode && (padHeld & StartBit) != 0))
                { _running = false; Done = true; return; }
                // $AA85 → $AB57 returns on the strobe, then $AA88 falls to $AA98 `JSR $AADD`,
                // and $AADD ends in `JMP $FF06`: the frame the page confirm arrives in spends
                // itself on the box clear and never reaches the $AA60 `STA $97 / JSR $F89A`
                // walk. Only the next frame resumes at $AA13 and types with a walk. Measured
                // on the city route at the briefing page break f1486 ($14=$80): the original
                // held $95 $7B→$7B and the selected bubble's anim fields $0430/$0432/$0434 =
                // $00/$08/$0C/$08 over f1485→f1486, then stepped both on f1487.
                ClearTextLines(); _line = _column = 0;
                _pendingYields = 1; _cleared = true;
                return;
            }

            if (_pendingYields > 0)
            {
                if (!_cleared) ClearTextLines();   // $AADD
                _cleared = true;
                _pendingYields--;
                if (_pendingYields > 0) return;
            }

            // $AA1B is tested once after the opening clear, before the $AA2C loop.
            // A Start edge there returns from the dialog; later typing never re-tests it.
            if (_initialGate)
            {
                _initialGate = false;
                if (!_mapMode && !AutoAdvance && (padHeld & StartBit) != 0)
                { _running = false; Done = true; return; }
            }
            WalkRequired = true;

            // $AA2C: one visible character per frame; $FE line breaks are consumed for free
            // and the character after them lands in the same frame.
            while (true)
            {
                int b = _bank2[_ptr + _byteIndex];
                _byteIndex++;
                if (b == 0xFF)
                {
                    // $AA9E → $AAD3 → $AB57: $0E/$0F yield exactly one frame and RTS out.
                    if (AutoAdvance) { _running = false; Done = true; _pendingYields = 1; }
                    else { _waiting = _endWait = true; }
                    return;
                }
                if (b == 0xFE)
                {
                    _line++;
                    if (_line < LineCount) { _column = 0; continue; }
                    // $AA85: page flip — one yield, then $AADD clears and lines restart at 0.
                    if (!AutoAdvance) { _waiting = true; _endWait = false; }
                    else { _pendingYields = 1; _cleared = false; _line = _column = 0; }
                    return;
                }
                WriteTile(_lineAddr[_line] + _column, (byte)b);
                _column++;
                if ((_frame & 3) == 0) SfxToPlay = 0x3F;   // $AA6F/$AA75
                return;
            }
        }

        /// <summary>$0E/$0F are the intro dialogs: they never wait for the pad.</summary>
        private bool AutoAdvance => _streamId == 0x0E || _streamId == 0x0F;

        /// <summary>The $AB57 wait for every other stream: A/B/START ends the page.</summary>
        public bool WaitingForInput => _running && _waiting;

        /// <summary>$AADD: three 29-tile blank runs at the line bases plus one tile at $23FE.
        /// The filler is tile $00 for $0E/$0F/$11 and tile $02 for every other stream.</summary>
        public void ClearTextLines()
        {
            byte blank = (byte)(_streamId == 0x0E || _streamId == 0x0F || _streamId == 0x11 ? 0x00 : 0x02);
            for (int i = 0; i < LineCount; i++)
                for (int t = 0; t < (_mapMode ? 19 : LineTiles); t++)
                    WriteTile(_lineAddr[i] + t, blank);
            WriteTile(_mapMode ? 0x2379 : 0x22FE, blank);
        }

        /// <summary>One ROM queue entry: [hi][lo][count-1][tile][$FF terminator].</summary>
        private void WriteTile(int addr, byte tile)
        {
            if (DeferWrites) { _pendingWrites.Add((addr, tile)); return; }
            _queue[0] = (byte)(addr >> 8);
            _queue[1] = (byte)addr;
            _queue[2] = 0;               // $0362 = 0 → the $C119 loop writes exactly one tile
            _queue[3] = tile;
            _queue[4] = 0xFF;
            _ppu.FlushQueue(_queue);
        }

        /// <summary>The decoded text of one line, for reports (tile $03 = space, $DA..$F3 = A..Z).</summary>
        public string LineText(int line, int length)
        {
            var sb = new System.Text.StringBuilder();
            for (int i = 0; i < length; i++)
            {
                byte t = _ppu.NametableRam[(_lineAddr[line] + i) & 0x0FFF];
                sb.Append(t == 0x03 ? ' '
                    : t >= 0xDA && t <= 0xF3 ? (char)('A' + t - 0xDA)
                    : t == 0xFA ? '!'
                    : '?');
            }
            return sb.ToString();
        }
    }
}
