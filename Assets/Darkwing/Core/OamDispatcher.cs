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

        /// <summary>Full per-frame pass: advance + compose for all slots in parity order.
        /// startCursor: OAM entries already used by earlier layers (hidden entry 0 +
        /// 8 HUD sprites in gameplay = 36).</summary>
        public void RunFrame(byte parity, int startCursor = 4)
        {
            _oamCursor = (byte)startCursor;
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
        }

        // ---------- $802F-$80C6: relative update + culling ----------
        private void UpdateSlotCull(int slot, ObjectSystem.Slot s)
        {
            byte flags = s.Flags;
            if ((flags & 0x10) == 0)
            {
                // $80C7: first-position copy, mark onscreen.
                s.ScreenX = (byte)s.X;
                s.ScreenY = (byte)s.Y;
                s.Flags |= 0x10 | 0x80;
                return;
            }
            int dxHi = (short)(((s.X & 0xFF) - _player.ScrollX) & 0xFF); // low
            int xHi = (s.X >> 8) - (_player.ScrollX >> 8);               // signed screens
            if (xHi != 0)
            {
                int mag = xHi < 0 ? -xHi : xHi;
                if (mag >= 0x10) { Deactivate(s); return; }           // $807A
                if (s.AnimNumber is 0x13 or 0x14 or 0x17 or 0x18 or 0x19 or 0x29)
                { Deactivate(s); return; }                            // projectiles cull closer
                s.Flags &= 0x7F;                                      // offscreen: keep active
                return;
            }
            s.ScreenX = (byte)dxHi;
            // Y: sy = yLo - fa, borrow → extra -15 ($8049-$8054)
            int yLo = s.Y & 0xFF;
            int fa = _player.ScrollYRow * 0; // $FA in-cell offset (0 in M3 scope)
            int sy = yLo - fa;
            if (sy < 0) sy -= 0x0F;
            s.ScreenY = (byte)(sy & 0xFF);
            int yHi = (s.Y >> 8) - _player.ScrollYRow;
            if (yHi == 0) { s.Flags |= 0x80; return; }               // onscreen
            if (slot >= 1) { Deactivate(s); return; }                // enemies cull vertically
            // player offscreen vertically ($8062-$8078): death path
            s.Flags = (byte)(s.Flags & 0x7F);
            _player.InvulnFrames = 0;
            _player.State = 7;
        }

        private static void Deactivate(ObjectSystem.Slot s)
        {
            s.Active = false;
            s.StateLow = 0;
            s.Behavior = 0;
            s.Direction = 0;
            s.PlacementIndex = 0xFF;
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
                _ppu.Oam[x + 1] = _bank5[_partPtr + y];
                int cy = _bank5[_coordPtr + y];
                int sumY = _relY + cy;                    // 6502 ADC semantics
                // $81BD-$81C5: positive coord hides on carry-out (>255), negative
                // hides when no carry (sum stayed < 256 → wrapped negative).
                bool hideY = (cy & 0x80) != 0 ? sumY < 256 : sumY > 255;
                _ppu.Oam[x] = hideY ? (byte)0xF8 : (byte)(sumY & 0xFF);
                y++;                                   // $81C7: attribute index
                _ppu.Oam[x + 2] = (byte)((_bank5[_partPtr + y] ^ _flipBits) | _prioBits);
                int cx = _bank5[_coordPtr + y];
                int sumX = _relX + cx;
                bool hideX = (cx & 0x80) != 0 ? sumX < 256 : sumX > 255;
                _ppu.Oam[x + 3] = hideX ? (byte)0xF8 : (byte)(sumX & 0xFF);
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
    }
}
