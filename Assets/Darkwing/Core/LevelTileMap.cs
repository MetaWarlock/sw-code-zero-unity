namespace Darkwing.Core
{
    /// <summary>
    /// World-backed collision map + column-streaming nametable (G13).
    /// All rooms of the player's grid row are pre-decoded at LoadWorldRow; the
    /// nametable is a sliding window: as ScrollX advances, columns entering the
    /// view are streamed from the decoded rooms into the 32-column ring
    /// (the original streams packed rows via $E63C queues; the visible result
    /// is the same sliding window). Collision types cover the whole row
    /// (the original's $0600 window is a cache of the same data).
    /// </summary>
    public sealed class LevelTileMap : ITileMap
    {
        private readonly RoomDecoder _decoder;
        private readonly Ppu _ppu;
        private readonly RoomDecoder.World _world;

        // Per-cell decoded data for the current grid row.
        private byte[][] _roomTiles;   // [cell] -> 32x32 tile ids
        private byte[][] _roomTypes;   // [cell] -> 16x16 collision types
        private byte[][] _roomAttrs;   // [cell] -> metatile id -> attr byte
        private byte[][] _roomMeta;    // [cell] -> 16x16 metatile ids
        private int _row;

        public LevelTileMap(RoomDecoder decoder, Ppu ppu, RoomDecoder.World world)
        {
            _decoder = decoder;
            _ppu = ppu;
            _world = world;
            _roomTiles = new byte[world.RowWidth][];
            _roomTypes = new byte[world.RowWidth][];
            _roomAttrs = new byte[world.RowWidth][];
            _roomMeta = new byte[world.RowWidth][];
            LoadRow(world.StartRow);
        }

        /// <summary>
        /// Pre-decode all cells of one grid row. The original redraws the whole row
        /// through $E7C6 when $FA wraps and $FB/$22 advance ($DC6E/$DCC4).
        /// </summary>
        public void LoadRow(int row)
        {
            _row = row;
            for (int col = 0; col < _world.RowWidth; col++)
            {
                byte cell = _world.CellAt(_row, col);
                var tiles = new byte[1024];
                var types = new byte[256];
                var macros = new byte[64];
                if (cell != 0)
                    _decoder.Decode(_world, cell, macros, tiles, types);
                _roomTiles[col] = tiles;
                _roomTypes[col] = types;
                _roomMeta[col] = (byte[])_decoder.MetaGrid.Clone();
                var attrs = new byte[256];
                var tb = _decoder.TypeBytes;
                for (int m = 0; m < 256; m++) attrs[m] = (byte)(tb[m] & 3);
                _roomAttrs[col] = attrs;
            }
            _lastStreamed = -1;
        }

        /// <summary>Current grid row ($FB).</summary>
        public int Row => _row;

        /// <summary>$36 — the section loader rewrites the metatile-set bank before it
        /// rebuilds the row ($E38F STA $36 ... JSR $DE24 → $E698), so decoding must
        /// use the new set. Same-world only: $E388 never touches $32/$34/$2E.</summary>
        public void ApplyTilesetBank(byte setBank) => _world.SetBank = setBank;

        /// <summary>Grid row stride = $2E: linear cell index $22 = row*GridWidth + column.</summary>
        public int GridWidth => _world.RowWidth;

        /// <summary>Number of grid rows described by the level's cell arrays.</summary>
        public int GridRows => _world.Grid.Length / _world.RowWidth;

        /// <summary>Grid byte ($32),Y — room id for linear cell index Y=$22. 0 = no cell ($DC7A/$DCAD).</summary>
        public byte GridByte(int index) =>
            (uint)index < (uint)_world.Grid.Length ? _world.Grid[index] : (byte)0;

        /// <summary>Script byte ($34),Y — cell flags: bit7 blocks the row change ($DC9B), low nibble 1 = hook column ($DAFD).</summary>
        public byte FlagByte(int index) =>
            (uint)index < (uint)_world.Flags.Length ? _world.Flags[index] : (byte)0;

        /// <summary>Stream nametable columns so the window covers scroll..scroll+256.</summary>
        public void StreamColumns(ushort scrollX)
        {
            int firstCol = scrollX >> 3;                  // world tile column at the left edge
            for (int k = 0; k < 32; k++)                  // exactly the 32-column window
            {
                StreamColumn(firstCol + k);
            }
        }

        private int _lastStreamed = -1;

        private void StreamColumn(int worldTileCol)
        {
            if (worldTileCol == _lastStreamed) return;
            _lastStreamed = worldTileCol;
            int inRoomCol = worldTileCol & 31;            // 32 tile columns per 256px cell
            int cell = worldTileCol >> 5;
            if (cell >= _world.RowWidth) return;
            var tiles = _roomTiles[cell];
            var meta = _roomMeta[cell];
            var nt = _ppu.NametableRam;
            int ringCol = inRoomCol;
            for (int row = 0; row < 30; row++)
            {
                nt[row * 32 + ringCol] = tiles[row * 32 + inRoomCol];
            }
            // Attributes: the 2-bit palette per 16px cell, packed per NES quadrant.
            for (int mrow = 0; mrow < 15; mrow++)
            {
                int mcol = inRoomCol >> 1;
                int metaIndex = meta[mrow * 16 + mcol];
                int attrByteIndex = 0x3C0 + (mrow >> 1) * 8 + (mcol >> 1);
                int shift = ((mrow & 1) << 2) | ((mcol & 1) << 1);
                byte pal = (byte)(_roomAttrs[cell][metaIndex] & 3);
                nt[attrByteIndex] = (byte)((nt[attrByteIndex] & ~(3 << shift)) | (pal << shift));
            }
        }

        /// <summary>Level script byte ($34 flag table) for the player's cell column ($DAFD feed).</summary>
        public byte FlagAtPlayer(PlayerCore player)
        {
            return FlagByte(player.CellIndex);
        }

        /// <summary>
        /// $EBE8 index math ($EC05-$EC14 columns, $EC72-$EC90 rows, $ECA9 nibble mask):
        /// the $0600 window is 8 bands x 16 columns x 2 nibbles, addressed by the LOW BYTE
        /// of the position with bit7 of the index = X-hi bit0. The band is Y&gt;&gt;5 and the
        /// nibble is bit3 of Y, so the cell row the original reads is 2*(Y&gt;&gt;5)+((Y&gt;&gt;3)&amp;1)
        /// — NOT Y&gt;&gt;4. The two agree only while Y bits 4 and 3 match, which is exactly the
        /// aligned positions $EDD2/$EDB3 snap to; mid-cell probes (e.g. a side probe 8 px
        /// into a cell) read the row ABOVE the naive one for half the Y range. gold_m1_fulljump
        /// f167/f208: the original keeps blocking on the pylon one frame longer than a Y&gt;&gt;4
        /// lookup does, and stays airborne when walking off its top edge.
        /// </summary>
        public byte TypeAt(int worldX, int worldY)
        {
            int cell = (worldX >> 8) & 0xFFFF;
            if ((uint)cell >= (uint)_world.RowWidth) return 0;
            int y = worldY & 0xFF;
            int row = ((y >> 4) & 0xFE) | ((y >> 3) & 1);
            return _roomTypes[cell][row * 16 + ((worldX & 0xFF) >> 4)];
        }
    }
}
