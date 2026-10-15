namespace Darkwing.Core
{
    /// <summary>
    /// Transcription of bank 5 $8000-$81F7: per-slot animation advance ($8100-$8141)
    /// and OAM composition ($8143-$81F7), plus camera culling ($802F-$80C6).
    /// Runs from the master loop each frame with the slot order alternating by the
    /// frame parity byte ($95). All tables are read from the raw PRG bank 5 export
    /// (pointer semantics identical to the original: CPU $8000+off).
    /// OAM cursor starts at 4 ($97): entry 0 is reserved (hidden marker, $C21E path).
    /// </summary>
    public sealed class OamDispatcher
    {
        private byte[] _bank5;
        private readonly ObjectSystem _objects;
        private readonly Ppu _ppu;
        private readonly PlayerCore _player;

        private byte _oamCursor = 4;        // $97
        private readonly byte[] _shadow = new byte[256];   // $0200-$02FF sprite buffer
        private int _walkEndCursor = 4;     // $97 as the last published walk left it
        private byte _relX, _relY;          // $13/$12 scratch
        private byte _flipBits;             // $10 (flags&0x40)
        private byte _prioBits;             // $11 (flags&0x20)
        private int _coordPtr;              // $05/$06 (bank5 offset, pre-decremented by 2)
        private int _partPtr;               // $02/$03
        private byte _partCount;            // $04

        public OamDispatcher(byte[] bank5, ObjectSystem objects, Ppu ppu, PlayerCore player)
        {
            _bank5 = bank5;
            _objects = objects;
            _ppu = ppu;
            _player = player;
        }

        private static int B5(int cpu) => cpu - 0x8000;

        /// <summary>Late binding for the bank-5 tables (constructed before data load completes).</summary>
        public void SetBank5(byte[] bank5) => _bank5 = bank5;

        /// <summary>Byte offset of the next free $0200 shadow slot after the frame's walk
        /// ($97 as the ROM leaves it). The HUD of the NEXT frame appends after it when
        /// ($95 &amp; 1) is set.</summary>
        public int OamCursorBytes => _oamCursor;

        /// <summary>Entry index the published buffer's object block ends at — the slot the HUD
        /// writes into on ($95 &amp; 1) == 1 frames. One walk behind RunFrame.</summary>
        public int PreviousWalkEndEntry => _walkEndCursor >> 2;

        /// <summary>The NMI DMA: copy the $0200 shadow the last walk composed into the live
        /// sprite buffer. Called once per frame, before the HUD is laid out.</summary>
        public void PublishToOam() => System.Array.Copy(_shadow, _ppu.Oam, 256);

        /// <summary>Seed the shadow from a golden capture frame so a resumed run starts with the
        /// buffer the previous ROM walk left behind (harness only).</summary>
        public void SeedShadow(byte[] oam, int offset)
        {
            System.Array.Copy(oam, offset, _shadow, 0, 256);
            System.Array.Copy(oam, offset, _ppu.Oam, 0, 256);
        }

        /// <summary>One frame of the bank-5 walk at its ROM place ($CABB, after the player,
        /// scroll, spawner, door and behaviour services). $802F-$80C6 is one fallthrough chain
        /// per slot — cull/relative update, then the blink/flip scratch, then the animation
        /// advance ($8100-$8141) and the compose ($8143-$81F7) — so a slot's $0450/$0440 and its
        /// advanced $0410 are the values its own composition reads.
        ///
        /// The capture pairs OAM[f] with RAM[f] for the walk parity but with RAM[f-1] for the
        /// record content, because the NMI publishes the shadow one frame after the walk wrote
        /// it; PublishToOam models that. Proven by a hypothesis scan over gold_contact and
        /// gold_jump (Temp/oam_lag.js): 622/622 and 631/631 object records byte-exact with
        /// content=f-1 plus HUD placement from RAM[f].$95, and 0 for every other pairing.</summary>
        public void RunFrame(byte parity)
        {
            _oamCursor = 4;                       // $CAB6 LDA #$04 / STA $97
            // Player invulnerability blink runs first ($80E1-$80F1 side effects).
            bool playerBlinkSkip = false;
            if (_player.InvulnFrames > 0)
            {
                _player.InvulnFrames--;
                playerBlinkSkip = (parity & 4) == 0; // ($95 & 4) == 0 → invisible 4 of 8
            }
            int start = (parity & 1) == 0 ? 0 : ObjectSystem.SlotCount - 1;
            int step = (parity & 1) == 0 ? 1 : -1;
            for (int i = 0; i < ObjectSystem.SlotCount; i++)
            {
                int slot = start + step * i;
                var s = _objects.Slots[slot];
                if (!s.Active) continue;              // $04D0 bit7
                if (slot == 0 && playerBlinkSkip) continue;
                UpdateSlotCull(slot, s);
                if (!s.Active) continue;
                AnimAdvance(s);
                OamCompose(slot, s);
            }
            _walkEndCursor = _oamCursor;
        }

        // ---------- $802F-$80C6: relative update + culling ----------
        private void UpdateSlotCull(int slot, ObjectSystem.Slot s)
        {
            byte flags = s.Flags;
            if ((flags & 0x10) == 0)
            {
                // $80C7-$80D8: absolute-position copy ($04F0→$0450, $0510→$0440) then
                // $80D3 ORA #$80 ONLY. Bit 4 is a type-table property ($8AB3+0, e.g. $90
                // for the walker, $80 for slot 1's fixed object), so a slot whose bit 4 is
                // clear never enters the relative-move path and is never culled — that is
                // why gold_m1_fulljump keeps slot 1 alive at $0018 for the whole level.
                s.ScreenX = (byte)s.X;
                s.ScreenY = (byte)s.Y;
                s.Flags = (byte)(flags | 0x80);
                return;
            }
            // $8039-$8047 byte-exact: $803C SEC makes SBC subtract only M (A−M−1+C with C=1),
            // so the screen byte is a plain low-byte difference — no extra −1. The borrow it
            // leaves feeds the high-byte SBC $FD, so the page difference is
            // (Xhi − ScrollXhi − borrow), not a plain byte delta.
            ushort sx = _player.ScrollX;
            int dxLo = (s.X & 0xFF) - (sx & 0xFF);          // $0450,X ($8039/$803D)
            s.ScreenX = (byte)dxLo;
            int dxHi = (s.X >> 8) - (sx >> 8) - (dxLo < 0 ? 1 : 0);
            bool xNoBorrow = dxHi >= 0;                      // C after the SBC at $8045
            if ((dxHi & 0xFF) != 0)
            {
                // $8096-$80A1: the magnitude tested is the SCREEN X byte, negated when
                // the object sits at a lower page than the scroll. So a slot stays alive
                // up to one page + $0F px away and frees at one page + $10 px.
                int mag = xNoBorrow ? s.ScreenX : (byte)(-s.ScreenX & 0xFF);
                if (mag >= 0x10) { Deactivate(s); return; }              // $809F/$80A1 → $807A
                if (s.AnimNumber is 0x13 or 0x14 or 0x17 or 0x18 or 0x19 or 0x29)
                { Deactivate(s); return; }                                // $80A3-$80B8
                s.Flags = (byte)(flags & 0x7F);                           // $80BE, no advance
                return;
            }
            // $8049-$805A byte-exact: sy = $0510 - $FA; if it borrows, $8051 SBC #$0F
            // runs with C still clear (−$10 more) and $8053 CLC makes the row compare
            // $0520 - $FB subtract ONE EXTRA row. So the borrow belongs in the row
            // difference — that is what keeps the hook ride onscreen: gold_hook_ride2
            // f17-f79 has $0520=02, $FB=01 and $0510 < $FA, giving 2-1-1 = 0.
            int sy = (s.Y & 0xFF) - _player.ScrollY;                    // $0510 - $FA
            bool borrow = sy < 0;
            if (borrow) sy -= 0x10;
            s.ScreenY = (byte)(sy & 0xFF);                              // $0440
            int yHi = ((s.Y >> 8) - _player.ScrollYRow - (borrow ? 1 : 0)) & 0xFF;
            if (yHi == 0) { s.Flags |= 0x80; return; }                   // $805C → $80D3
            if (slot >= 1) { Deactivate(s); return; }                    // $805E/$8060
            // $8062-$8078: slot 0 goes offscreen; it dies only when the row difference
            // is exactly 1 (the original PLA/CMP #$01), otherwise it stays active.
            s.Flags = (byte)(s.Flags & 0x7F);
            if (yHi != 1) return;
            _player.InvulnFrames = 0;                                    // $806A STA $7C
            _player.State = 7;                                           // $806E STA $50
            // $8072 D0 00 / BNE $807A: the last LDA wrote #$07, so Z is always clear and
            // slot 0 ALWAYS takes the deactivate payload. Without it $CAFC would keep
            // running the state machine instead of the death service $CB01.
            Deactivate(s);
        }

        /// <summary>$807A-$808A: the four writes a culled slot gets. Behavior ($04E0)
        /// and direction ($0580) are NOT touched, so a respawned placement reuses them.</summary>
        private static void Deactivate(ObjectSystem.Slot s)
        {
            s.Active = false;
            s.StateLow = 0;        // $04D0,X
            s.HitFlash = 0;        // $04C0,X
            s.BehaviorParam = 0;   // $0570,X
            s.PlacementIndex = 0xFF;   // $0590,X: the placement is released
        }

        // ---------- $8100-$8141: animation advance ----------
        public void AnimAdvance(ObjectSystem.Slot s)
        {
            int entry = AnimEntryAddress(s.AnimNumber);
            byte timer = s.AnimTimer;
            if ((timer & 0x80) != 0) return;          // paused animation
            byte old = (byte)(timer & 0x7F);
            s.AnimTimer = (byte)(old + 1);
            if (old != _bank5[entry + 1]) return;     // duration not reached
            s.AnimTimer = 0;
            byte last = (byte)(_bank5[entry] & 0x7F);
            byte oldFrame = s.AnimFrame;
            if (oldFrame == last) s.AnimFrame = 0;    // $8127-$8133: wrap after last frame
            else s.AnimFrame = (byte)(oldFrame + 1);
        }

        // ---------- $8143-$81F7: OAM composition ----------
        private void OamCompose(int slot, ObjectSystem.Slot s)
        {
            if ((s.Flags & 0x08) != 0) return;        // compose-disabled flag
            if ((s.Flags & 0x80) == 0) return;        // not onscreen
            int entry = AnimEntryAddress(s.AnimNumber);
            int frameIdx = (s.AnimFrame & 0x7F) + 2;
            int frameId = _bank5[entry + frameIdx];
            if (frameId == 0)
            {                                          // $8152: end-of-animation
                s.Active = false;
                s.StateLow = 0;
                s.PlacementIndex = 0xFF;
                return;
            }
            int lo, hi;
            if ((_bank5[entry] & 0x80) != 0) { lo = 0x9C0B; hi = 0x9D45; }
            else { lo = 0x9B0B; hi = 0x9C45; }
            int partCpu = _bank5[B5(lo) + frameId] | (_bank5[B5(hi) + frameId] << 8);
            _partPtr = B5(partCpu);
            if (_partPtr < 0 || _partPtr >= 0x4000 - 64)
                return; // dynamic RAM frame ($05B0/$05C2/$05E8 template path — separate mechanism)
            _relY = s.ScreenY;
            _relX = s.ScreenX;
            int dirOffset = (s.Flags & 0x40) != 0 ? 1 : 0;
            int coordId = _bank5[_partPtr + 1] + dirOffset;
            int coordCpu = _bank5[B5(0xAE53) + coordId] | (_bank5[B5(0xAF23) + coordId] << 8);
            _coordPtr = B5(coordCpu) - 2;
            if (_coordPtr < 0 || _coordPtr >= 0x4000 - 64)
                return;
            _flipBits = (byte)(s.Flags & 0x40);
            _prioBits = (byte)(s.Flags & 0x20);
            _partCount = _bank5[_partPtr];
            int x = _oamCursor;
            int y = 1;
            while (true)
            {
                y++;                                   // $81AF: tile index
                if (x + 4 > 256) break;                // OAM full ($97 wrap guard)
                _shadow[x + 1] = _bank5[_partPtr + y];
                int cy = _bank5[_coordPtr + y];
                int sumY = _relY + cy;                    // 6502 ADC semantics
                // $81BD-$81C5: positive coord hides on carry-out (>255), negative
                // hides when no carry (sum stayed < 256 → wrapped negative).
                bool hideY = (cy & 0x80) != 0 ? sumY < 256 : sumY > 255;
                _shadow[x] = hideY ? (byte)0xF8 : (byte)(sumY & 0xFF);
                y++;                                   // $81C7: attribute index
                _shadow[x + 2] = (byte)((_bank5[_partPtr + y] ^ _flipBits) | _prioBits);
                int cx = _bank5[_coordPtr + y];
                int sumX = _relX + cx;
                bool hideX = (cx & 0x80) != 0 ? sumX < 256 : sumX > 255;
                _shadow[x + 3] = hideX ? (byte)0xF8 : (byte)(sumX & 0xFF);
                x += 4;
                if (_partCount-- == 0) break;   // $81EB: DEC; BPL → count+1 iterations
            }
            _oamCursor = (byte)(x & 0xFF);
            if (_oamCursor == 0) _oamCursor = 4;       // $97 wrap: stop composing
        }

        /// <summary>Anim entry pointer (bank5 offset) from $B429/$B4F0 tables.</summary>
        private int AnimEntryAddress(byte anim)
        {
            int cpu = _bank5[B5(0xB429) + anim] | (_bank5[B5(0xB4F0) + anim] << 8);
            return B5(cpu);
        }

        /// <summary>Diagnostics: entry[0] (last frame) and entry[1] (duration) for an anim.</summary>
        public (byte last, byte duration) AnimHeader(byte anim)
        {
            int entry = AnimEntryAddress(anim);
            if (_bank5 == null || entry < 0 || entry + 1 >= _bank5.Length) return (0, 0);
            return ((byte)(_bank5[entry] & 0x7F), _bank5[entry + 1]);
        }
    }
}
