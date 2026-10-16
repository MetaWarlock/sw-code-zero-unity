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
        private byte _relX, _relY;          // $13/$12 scratch
        private byte _flipBits;             // $10 (flags&0x40)
        private byte _prioBits;             // $11 (flags&0x20)
        private int _coordPtr;              // $05/$06 (bank5 offset, pre-decremented by 2)
        private int _partPtr;               // $02/$03
        private byte _partCount;            // $04

        private HudComposer _hud;

        /// <summary>Slot 1 belongs to the HUD while the in-game layers are up; bank 4
        /// $8FCD… saves and restores those six fields around the mission screens.</summary>
        public bool HudActive = true;

        /// <summary>Mission-card phase: bank 2 $9600…$9635 primes OAM cursor to $A0
        /// and $9636/$967A blits 40 sprite records at $0200..$029F that must survive
        /// $3C frames while $F51B animates their X offsets. When this flag is set,
        /// RunFrame skips the $C21E wipe below $A0 and starts composition at $A0.
        /// Never set by the current flow — shift 45 wires it in
        /// <see cref="PlayerCore.MissionIntroCard"/>.</summary>
        public bool MissionCardHold;

        public OamDispatcher(byte[] bank5, ObjectSystem objects, Ppu ppu, PlayerCore player)
        {
            _bank5 = bank5;
            _objects = objects;
            _ppu = ppu;
            _player = player;
            // Entry 0 is never composed ($97 starts at $04) and $C21E only resets its Y byte, so
            // the tile/attr/X bytes below keep whatever an earlier screen left: gold captures show
            // F8 C0 00 10 in all 3260 HUD frames.
            _shadow[1] = 0xC0; _shadow[2] = 0x00; _shadow[3] = 0x10;
        }

        private static int B5(int cpu) => cpu - 0x8000;

        /// <summary>Late binding for the bank-5 tables (constructed before data load completes).</summary>
        public void SetBank5(byte[] bank5) => _bank5 = bank5;

        /// <summary>Late binding for the HUD's $05B0…$05C1 block (slot 1's part table).</summary>
        public void SetHud(HudComposer hud) => _hud = hud;

        /// <summary>Byte offset of the next free $0200 shadow slot after the frame's walk
        /// ($97 as the ROM leaves it; 0 once the sprite page has been filled).</summary>
        public int OamCursorBytes => _oamCursor;

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

        /// <summary>Transcription of bank 2 $9636/$967A: mission-card OAM blit.
        /// 40 groups of 4 bytes at $0200,Y (Y=0,4,…,$9C). The X byte counter is advanced per
        /// branch, not once per row (flat_bank2.txt:4325-4355): world 0 reads $973F,Y and only
        /// INX-es on the $9644-not-taken path (a zero Y byte falls through $9646/$9652 to
        /// $97DF,X + INX); worlds 5/6 read $9807,X and INX only while it is non-zero ($9650);
        /// the rest read $97DF,X with an unconditional INX. Tile/attr/X come from $9740/41/42,Y
        /// always. World 7 takes $967A which reads $969F/A0/A1/A2,Y and skips the selector.
        /// Bank 2 file indexed by cpu address − $8000 (see <c>TextEngine.cs:42</c>).
        /// Wired by <see cref="PlayerCore.MissionIntroCard"/> on the _cardWait 0 → $3C transition;
        /// verified byte-exact against every world's original OAM by MissionCardVerify.Suite.</summary>
        public void BlitMissionCard(byte[] bank2, int world)
        {
            if (world == 7)
            {
                for (int y = 0; y < 0xA0; y += 4)
                {
                    _shadow[y]      = bank2[0x169F + y];   // $969F,Y
                    _shadow[y + 1]  = bank2[0x16A0 + y];   // $96A0,Y
                    _shadow[y + 2]  = bank2[0x16A1 + y];   // $96A1,Y
                    _shadow[y + 3]  = bank2[0x16A2 + y];   // $96A2,Y
                }
                return;
            }
            int x = 0;
            for (int y = 0; y < 0xA0; y += 4)
            {
                // $963A is one fall-through chain per OAM record, and the X byte counter is
                // advanced per branch, not once per row, so a zero Y source can either consume
                // the $97DF,X fallback or repeat the current X depending on the world.
                bool inx;
                if (world == 0)
                {
                    byte yb = bank2[0x173F + y];           // $963E LDA $973F,Y / $9641 STA $0200,Y
                    if (yb != 0) { _shadow[y] = yb; inx = false; }   // $9644 BNE $965D (no INX)
                    else { _shadow[y] = bank2[0x17DF + x]; inx = true; } // A=0 → $9646/$9652/$9656 + $965C INX
                }
                else if (world == 5 || world == 6)
                {
                    byte yb = bank2[0x1807 + x];           // $964A LDA $9807,X / STA $0200,Y
                    _shadow[y] = yb;
                    inx = yb != 0;                         // $9650 BNE $965C
                }
                else
                {
                    _shadow[y] = bank2[0x17DF + x];        // $9656 LDA $97DF,X / STA $0200,Y
                    inx = true;                            // $965C INX
                }
                if (inx) x++;
                _shadow[y + 1] = bank2[0x1740 + y];         // $965D LDA $9740,Y / STA $0201,Y
                _shadow[y + 2] = bank2[0x1741 + y];         // $9663 LDA $9741,Y / STA $0202,Y
                _shadow[y + 3] = bank2[0x1742 + y];         // $9669 LDA $9742,Y / STA $0203,Y
            }
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
            _oamCursor = MissionCardHold ? (byte)0xA0 : (byte)4;   // $CAB6 / $9607
            // $C21E, called just before the walk: hides every record from the cursor to the end
            // of the page, so each frame's unused tail is F8 plus last frame's tile/attr/X bytes.
            // During the mission-card phase the front 160 bytes hold the $9636 blit — $C21E's
            // wipe starts at the cursor, so only $02A0..$02FF is reset.
            int wipeStart = MissionCardHold ? 0xA0 : 0;
            for (int i = wipeStart; i < 256; i += 4) _shadow[i] = 0xF8;
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
                if (slot == HudComposer.Slot && HudActive && _hud != null)
                {
                    ComposeHud();                     // $C887 made slot 1 the HUD block
                    continue;
                }
                var s = _objects.Slots[slot];
                if (!s.Active) continue;              // $04D0 bit7
                if (slot == 0 && playerBlinkSkip) continue;
                UpdateSlotCull(slot, s);
                if (!s.Active) continue;
                AnimAdvance(s);
                OamCompose(slot, s);
            }
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
            ComposeParts(_bank5, _partPtr, _coordPtr, _relX, _relY, _flipBits, _prioBits, _partCount);
        }

        /// <summary>Slot 1's part table is the RAM block $05B0…$05C1, so the HUD reaches the same
        /// composition service with a fixed position ($04F0/$0510) and no flip or priority bits
        /// ($0400,1=$80, $0570,1=0 — $C887).</summary>
        private void ComposeHud()
        {
            if (_oamCursor == 0) return;              // $81AD: a wrapped cursor stops composition
            int coordId = _hud.Param[1];              // $05B1 = $24
            int coordCpu = _bank5[B5(0xAE53) + coordId] | (_bank5[B5(0xAF23) + coordId] << 8);
            int coordPtr = B5(coordCpu) - 2;
            if (coordPtr < 0 || coordPtr >= 0x4000 - 64) return;
            ComposeParts(_hud.Param, 0, coordPtr, HudComposer.RelX, HudComposer.RelY, 0, 0, _hud.Param[0]);
        }

        /// <summary>$81AB-$81EF, the record writer shared by every slot including the HUD.
        /// Each iteration reloads X from $97 and stores it back, and a part whose Y or X
        /// coordinate falls off-screen jumps to $81F0/$81F1, which writes $F8 over the Y byte
        /// and skips the INX×4 — so a hidden part leaves no record and the next one reuses the
        /// entry. That is why the HUD's start entry counts slot 0's VISIBLE records only.</summary>
        private void ComposeParts(byte[] parts, int partsBase, int coordBase,
                                  byte relX, byte relY, byte flipBits, byte prioBits, int count)
        {
            int x = _oamCursor;
            int y = 1;                                // $81AF INY → index 2 is the first tile
            for (int n = 0; n <= count; n++)        // $81EB/$81ED: DEC $04, BPL → count+1 parts
            {
                if (x == 0) return;                 // $81AB LDX $97 / BEQ $81EF
                y++;                                   // $81AF: tile index
                _shadow[x + 1] = parts[partsBase + y];
                int cy = _bank5[coordBase + y];
                int sumY = relY + cy;                    // 6502 ADC semantics
                _shadow[x] = (byte)(sumY & 0xFF);        // $81BA
                // $81BD-$81C5: positive coord hides on carry-out (>255), negative
                // hides when no carry (sum stayed < 256 → wrapped negative).
                bool hideY = (cy & 0x80) != 0 ? sumY < 256 : sumY > 255;
                if (hideY) { y++; _shadow[x] = 0xF8; continue; }   // $81F0-$81F3
                y++;                                   // $81C7: attribute index
                _shadow[x + 2] = (byte)((parts[partsBase + y] ^ flipBits) | prioBits);
                int cx = _bank5[coordBase + y];
                int sumX = relX + cx;
                _shadow[x + 3] = (byte)(sumX & 0xFF);    // $81D6
                bool hideX = (cx & 0x80) != 0 ? sumX < 256 : sumX > 255;
                if (hideX) { _shadow[x] = 0xF8; continue; }        // $81F1/$81F3
                x += 4;                                  // $81E3-$81E6
                if ((x & 0xFF) == 0) { _oamCursor = 0; return; }   // $81E7/$81E9: page full
            }
            _oamCursor = (byte)x;
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
