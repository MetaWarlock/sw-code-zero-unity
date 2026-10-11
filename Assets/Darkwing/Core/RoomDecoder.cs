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
            public byte SetBank;      // metatile/room-table bank
            public byte RoomBank;     // PRG bank holding 64-byte room blocks (0, or 1 for FOWL)
            public byte StartCell;    // $DEB9
            public byte StartRow;     // $DEC8 (scroll Y init = grid row)
            public int RowWidth;      // $DED7 + 1
            public byte ChrBg;        // $DEF5
            public byte Music;        // $DF04
            public byte TopRow;       // $DF41
            public byte[] Grid;       // room id per cell
            public byte[] Flags;      // script byte per cell

            public byte CellAt(int row, int col) => Grid[row * RowWidth + col];
            public byte FlagAt(int row, int col) => Flags[row * RowWidth + col];
        }

        public World GetWorld(byte id)
        {
            int b7 = 0xDF50 - 0xC000; // descriptor table region base $DEAA
            var w = new World
            {
                Id = id,
                SetBank = _bank7[0xDEAA - 0xC000 + id],
                StartCell = _bank7[0xDEB9 - 0xC000 + id],
                StartRow = _bank7[0xDEC8 - 0xC000 + id],
                RowWidth = _bank7[0xDED7 - 0xC000 + id] + 1,
                ChrBg = _bank7[0xDEF5 - 0xC000 + id],
                Music = _bank7[0xDF04 - 0xC000 + id],
                TopRow = _bank7[0xDF41 - 0xC000 + id],
            };
            w.RoomBank = w.SetBank >= 6 ? (byte)1 : (byte)0;
            int gridPtr = _bank7[0xDF50 - 0xC000 + id] | (_bank7[0xDF60 - 0xC000 + id] << 8);
            int flagPtr = _bank7[0xDF70 - 0xC000 + id] | (_bank7[0xDF80 - 0xC000 + id] << 8);
            int gb = gridPtr - 0xC000, fb = flagPtr - 0xC000;
            int count = 4 * w.RowWidth;
            w.Grid = new byte[count];
            w.Flags = new byte[count];
            System.Array.Copy(_bank7, gb, w.Grid, 0, count);
            System.Array.Copy(_bank7, fb, w.Flags, 0, count);
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
            int setBase = w.SetBank * 1536;
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
