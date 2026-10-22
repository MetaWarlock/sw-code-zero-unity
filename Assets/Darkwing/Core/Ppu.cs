namespace Darkwing.Core
{
    /// <summary>
    /// Minimal PPU state mirroring the original dataflow: the game never touches
    /// pixels — it writes VRAM through the queue ($0360/$0380 buffers, format
    /// [addrHi, addrLo, count, data...] with bit7-of-addrHi terminator, NMI $C119)
    /// and registers ($2000-$2007). Rendering composes nametable + attributes +
    /// OAM exactly as the PPU would, outputting NES palette indices.
    /// </summary>
    public sealed class Ppu
    {
        public const int W = 256, H = 240;

        public readonly byte[] NametableRam = new byte[0x1000]; // $2000-$2FFF window
        public readonly byte[] PaletteRam = new byte[32];       // $3F00-$3F1F
        public readonly byte[] Oam = new byte[256];             // $200-$2FF shadow OAM

        public byte Ctrl;        // $2000
        public byte Mask;        // $2001
        public byte ScrollX, ScrollY; // latched at $2005 pairs
        public int NametableX;   // $2000 bit0 / $FD bit0: horizontal nametable page
        public ushort VramAddr;  // $2006 latch
        private byte _addrHi;
        private bool _addrSecond;
        private byte _scrollLatch;

        // MMC1-mapped CHR banks (4K mode): CHR0 = pattern table $0000, CHR1 = $1000.
        public int Chr0Bank = 14;
        public int Chr1Bank = 16;
        public bool BgPatternHigh;    // PPUCTRL bit4: bg pattern table at $1000
        public bool SpritePatternHigh;

        private readonly GameData _data;
        private readonly byte[] _tile = new byte[64];

        public Ppu(GameData data) { _data = data; }

        /// <summary>$C086-$C09E: $2005 gets $FC/$FA, and $2000 gets $FF | ($FD &amp; 1).
        /// The 9th scroll bit lives in PPUCTRL bit0, not in the $2005 byte.</summary>
        public void SetScroll(int x, int y)
        {
            NametableX = (x >> 8) & 1;
            ScrollX = (byte)x;
            ScrollY = (byte)y;
        }

        // ---- CPU port writes ----
        public void WriteRegister(int reg, byte value)
        {
            switch (reg)
            {
                case 0x2000:
                    Ctrl = value;
                    NametableX = value & 1;
                    BgPatternHigh = (value & 0x10) != 0;
                    SpritePatternHigh = (value & 0x08) != 0;
                    break;
                case 0x2001: Mask = value; break;
                case 0x2003: break; // OAM addr: the game always DMAs from $0200
                case 0x2005:
                    if (_scrollLatch == 0) { ScrollX = value; _scrollLatch = 1; }
                    else { ScrollY = value; _scrollLatch = 0; }
                    break;
                case 0x2006:
                    if (!_addrSecond) { _addrHi = (byte)(value & 0x3F); _addrSecond = true; }
                    else { VramAddr = (ushort)((_addrHi << 8) | value); _addrSecond = false; }
                    break;
                case 0x2007:
                    WriteVram(VramAddr, value);
                    VramAddr = (ushort)((VramAddr + ((Ctrl & 0x04) != 0 ? 32 : 1)) & 0x3FFF);
                    break;
                case 0x4014: // DMA from CPU page: caller supplies the page copy
                    break;
            }
        }

        public void DmaFromPage(byte[] cpuRam, int pageBase)
        {
            System.Array.Copy(cpuRam, pageBase, Oam, 0, 256);
        }

        public void WriteVram(int addr, byte value)
        {
            addr &= 0x3FFF;
            if (addr < 0x2000) return; // CHR pattern tables are ROM-mapped
            // $3000-$3FFF mirrors $2000-$2FFF (2 KiB of nametable RAM, mirrored every $800),
            // so a $2007 run whose auto-increment wraps out of $2FFF keeps writing the tables.
            if (addr < 0x3F00) NametableRam[(addr - 0x2000) & 0x07FF] = value;
            else
            {
                int i = addr & 0x1F;
                // $3F10/$14/$18/$1C are the SAME cells as $3F00/$04/$08/$0C, and the other
                // $3F11-$3F1F addresses are writable cells of their own, so a read-back of the
                // whole $3F00-$3F1F window shows the sprite table everywhere except the four
                // aliased slots, which hold whatever the last write to either name stored
                // (gold_title2 f919: $3F10..1F = 0f 0f 30 27 0f 0f 13 24 …).
                int cell = (i & 0x13) == 0x10 ? i & 0x0F : i;
                byte v = (byte)(value & 0x3F);
                PaletteRam[cell] = v;
                if (cell < 0x10 && (cell & 3) == 0) PaletteRam[cell | 0x10] = v;
            }
        }

        /// <summary>Flush one VRAM queue buffer (original $C119 semantics): entries are
        /// [addrHi][addrLo][run-1][tiles...], terminated by a byte with bit7 set. The count
        /// field is one less than the run because $C131 `DEY / BPL` writes the first tile
        /// before it tests, so $0362 = 0 uploads exactly one tile.</summary>
        public void FlushQueue(byte[] queue)
        {
            int x = 0;
            while (x < queue.Length && (queue[x] & 0x80) == 0)
            {
                int addr = (queue[x] << 8) | queue[x + 1];
                int count = queue[x + 2] + 1;
                for (int i = 0; i < count; i++)
                    WriteVram(addr + i, queue[x + 3 + i]);
                x += 3 + count;
            }
        }

        // ---- Rendering ----
        /// <summary>Compose the 256x240 frame (background + sprites) into NES indices.</summary>
        public void Render(byte[] framebuffer)
        {
            bool rendering = (Mask & 0x18) != 0;
            if (!rendering)
            {
                byte bg = PaletteRam[0];
                for (int i = 0; i < W * H; i++) framebuffer[i] = bg;
                return;
            }
            // Mirroring: horizontal (MMC1 ctrl bit0=1 would be vertical; game uses horizontal).
            int scrollX = ScrollX, scrollY = ScrollY;
            for (int py = 0; py < H; py++)
            {
                int wy = py + scrollY;
                bool ntB = wy >= 240;                    // second nametable vertically
                int row = ntB ? wy - 240 : wy;
                int baseNt = (ntB ? 0x0400 : 0x0000) ^ (NametableX != 0 ? 0x0400 : 0);
                for (int px = 0; px < W; px++)
                {
                    int wx = px + scrollX;
                    bool ntRight = wx >= 256;
                    int col = ntRight ? wx - 256 : wx;
                    int ntBase = baseNt ^ (ntRight ? 0x0400 : 0);
                    int tileIdx = NametableRam[ntBase + (row >> 3) * 32 + (col >> 3)];
                    int attrByte = NametableRam[ntBase + 0x3C0 + ((row >> 5) * 8) + (col >> 5)];
                    // NES attribute byte: bits 0-1 TL, 2-3 TR, 4-5 BL, 6-7 BR (16px cells in a 32px block)
                    int shift = ((row & 0x10) != 0 ? 4 : 0) | ((col & 0x10) != 0 ? 2 : 0);
                    int palSlot = (attrByte >> shift) & 3;
                    Decode(tileIdx, BgPatternHigh ? Chr1Bank : Chr0Bank);
                    int ci = _tile[(row & 7) * 8 + (col & 7)];
                    framebuffer[py * W + px] = PaletteRam[palSlot * 4 + ci];
                }
            }
            // Sprites (OAM order, later sprites under earlier on overlap: hardware draws
            // first-found opaque pixel per coordinate — copy OAM forward).
            for (int s = 63; s >= 0; s--) // draw later sprites first so earlier win
            {
                int y = Oam[s * 4] + 1;
                int tile = Oam[s * 4 + 1];
                int attr = Oam[s * 4 + 2];
                int x = Oam[s * 4 + 3];
                bool flipH = (attr & 0x40) != 0, flipV = (attr & 0x80) != 0;
                bool behind = (attr & 0x20) != 0;
                int palSlot = 4 + (attr & 3);
                DecodeSprite(tile, SpritePatternHigh ? Chr1Bank : Chr0Bank);
                for (int ty = 0; ty < 8; ty++)
                {
                    int sy = y + (flipV ? 7 - ty : ty);
                    if ((uint)sy >= H) continue;
                    for (int tx = 0; tx < 8; tx++)
                    {
                        int sx = x + (flipH ? 7 - tx : tx);
                        if ((uint)sx >= W) continue;
                        byte ci = _tile[ty * 8 + tx];
                        if (ci == 0) continue;
                        int di = sy * W + sx;
                        if (behind && framebuffer[di] != PaletteRam[0]) continue;
                        framebuffer[di] = PaletteRam[palSlot * 4 + ci];
                    }
                }
            }
        }

        private void Decode(int tileIndex, int bank)
        {
            _data.DecodeTile(bank, tileIndex, _tile, 0);
        }

        private void DecodeSprite(int tileIndex, int bank)
        {
            _data.DecodeTile(bank, tileIndex, _tile, 0);
        }
    }
}
