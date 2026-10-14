namespace Darkwing.Core
{
    /// <summary>
    /// Level room decoder, transcription of Tools/DarkwingResearch/decode_room.py
    /// (verified 960/960 against the captured mission-1 nametable).
    /// Room = 64-byte block of 8x8 macrotile ids at PRG room bank offset (cell-1)*64.
    /// Macrotile -> 4 metatile ids (set bank $B6E0 table); metatile -> 4 tile ids
    /// ($BEE0.. tables TL,TR,BL,BR) + per-8px-row collision type ($BEE0+0x200... see
    /// metatile type table $BEE0+0x300? exact: type table at set bank $BEE0+... = 0x3EE0
    /// bank-relative; low nibble = top 8px row type, high nibble = bottom).
    /// World descriptors live in bank 7 (world_bank7.bytes).
    /// </summary>
    public sealed class RoomDecoder
    {
        private readonly byte[] _bank0, _bank1, _bank7, _levelSets;

        public RoomDecoder(byte[] bank0, byte[] bank1, byte[] bank7, byte[] levelSets)
        {
            _bank0 = bank0; _bank1 = bank1; _bank7 = bank7; _levelSets = levelSets;
        }

        public sealed class World
        {
            public byte Id;
            public byte SetBank;      // $36 ($DEAA): metatile/macrotile table bank
            public byte RoomBank;     // PRG bank holding 64-byte room blocks (0, or 1 for FOWL)
            public byte StartCell;    // $22/$23 ($DEB9)
            public byte StartRow;     // $FB/$0520 ($DEC8)
            public int RowWidth;      // $2E = $DED7 + 1
            public byte ChrReq0;      // $1A ($DEE6) — CHR0 request, bank = value & $7F
            public byte ChrReq1;      // $1B ($DEF5) — CHR1 request (bg pattern table)
            public byte BgPalIdx;     // $DF04 — index byte handed to $C2F9 (NOT a music id)
            public byte RingEvent;    // $DF13 — queued to $D9/$F84E only when $D9 was nonzero
            public byte SpPalIdx;     // $DF22 — index byte handed to $C319
            public byte Flag3D;       // $DF31 -> $3D
            public byte TopRow;       // $DF41 -> $2D
            public byte[] Grid;       // room id per cell
            public byte[] Flags;      // script byte per cell

            public byte CellAt(int row, int col) => Grid[row * RowWidth + col];
            public byte FlagAt(int row, int col) => Flags[row * RowWidth + col];
            public int GridRows => Grid.Length / RowWidth;
        }

        /// <summary>$DD57 world descriptor table addresses (bank 7, indexed by $31).</summary>
        public static byte DescByte(byte[] bank7, int table, byte id) => bank7[table - 0xC000 + id];

        public World GetWorld(byte id)
        {
            byte t(int addr) => _bank7[addr - 0xC000 + id];
            var w = new World
            {
                Id = id,
                SetBank = t(0xDEAA),
                StartCell = t(0xDEB9),
                StartRow = t(0xDEC8),
                RowWidth = t(0xDED7) + 1,
                ChrReq0 = t(0xDEE6),
                ChrReq1 = t(0xDEF5),
                BgPalIdx = t(0xDF04),
                RingEvent = t(0xDF13),
                SpPalIdx = t(0xDF22),
                Flag3D = t(0xDF31),
                TopRow = t(0xDF41),
            };
            w.RoomBank = w.SetBank >= 6 ? (byte)1 : (byte)0;
            int gridPtr = t(0xDF50) | (t(0xDF60) << 8);
            int flagPtr = t(0xDF70) | (t(0xDF80) << 8);
            // $DD57 gives only the row WIDTH ($2E); the arrays have no length field, so each
            // world's grid/flag tables run up to the next descriptor's table (id 7 = $E17F,
            // the first dialog scene). Verified gaps: 0x44 (17x4), 0x78 (10x12), 0x39 (19x3).
            int nextGrid = id < 7
                ? (_bank7[0xDF50 - 0xC000 + id + 1] | (_bank7[0xDF60 - 0xC000 + id + 1] << 8))
                : 0;
            int count = nextGrid > gridPtr ? nextGrid - gridPtr : 4 * w.RowWidth;
            w.Grid = new byte[count];
            w.Flags = new byte[count];
            System.Array.Copy(_bank7, gridPtr - 0xC000, w.Grid, 0, count);
            System.Array.Copy(_bank7, flagPtr - 0xC000, w.Flags, 0, count);
            return w;
        }

        private byte[] RoomBank(byte bank) => bank == 0 ? _bank0 : _bank1;

        /// <summary>Decode one room: 8x8 macrotiles -> 32x32 tile ids and 16x16 types.</summary>
        public void Decode(World w, byte cell,
                           byte[] macrotiles8x8,   // 64 out
                           byte[] tiles32x32,      // 1024 out (row-major)
                           byte[] types16x16)      // 256 out (row-major)
        {
            var roomBank = RoomBank(w.RoomBank);
            int off = (cell - 1) * 64;
            // levelsets.bytes is 7 x 2304: macrotile table (1024) + 4 metatile tile
            // quadrants (4x256) + type byte per metatile (256). Sets 1-6 need the stride.
            int setBase = w.SetBank * 2304;
            for (int mr = 0; mr < 8; mr++)
            {
                for (int mc = 0; mc < 8; mc++)
                {
                    byte m = roomBank[off + mr * 8 + mc];
                    macrotiles8x8[mr * 8 + mc] = m;
                    for (int q = 0; q < 4; q++)
                    {
                        int mid = _levelSets[setBase + m * 4 + q];
                        int qr = q >> 1, qc = q & 1;
                        int r = mr * 2 + qr, c = mc * 2 + qc;
                        _metaGrid[r * 16 + c] = (byte)mid;
                        _typeBytes[mid] = _levelSets[setBase + 2048 + mid];
                        // 0x3EE0 byte = (collision_type << 4) | bg_palette (level_format.md §5)
                        types16x16[r * 16 + c] = (byte)(_typeBytes[mid] >> 4);
                        // metatile tile arrays: TL,TR,BL,BR = setBase+1024 quadrants
                        tiles32x32[r * 2 * 32 + c * 2] = _levelSets[setBase + 1024 + mid];
                        tiles32x32[r * 2 * 32 + c * 2 + 1] = _levelSets[setBase + 1024 + 256 + mid];
                        tiles32x32[(r * 2 + 1) * 32 + c * 2] = _levelSets[setBase + 1024 + 512 + mid];
                        tiles32x32[(r * 2 + 1) * 32 + c * 2 + 1] = _levelSets[setBase + 1024 + 768 + mid];
                    }
                }
            }
        }

        private readonly byte[] _metaGrid = new byte[256];
        private readonly byte[] _typeBytes = new byte[256];

        public byte[] MetaGrid => _metaGrid;   // last decoded room, 16x16 metatile ids
        public byte[] TypeBytes => _typeBytes; // metatile id -> raw type byte (bits0-1 palette)
    }
}
