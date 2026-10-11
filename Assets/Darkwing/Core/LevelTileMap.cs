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
            _row = world.StartRow;
            int cells = 4 * world.RowWidth; // linear cells; row-major lookup below
            _roomTiles = new byte[world.RowWidth][];
            _roomTypes = new byte[world.RowWidth][];
            _roomAttrs = new byte[world.RowWidth][];
            _roomMeta = new byte[world.RowWidth][];
            for (int col = 0; col < world.RowWidth; col++)
            {
                byte cell = world.CellAt(_row, col);
                var tiles = new byte[1024];
                var types = new byte[256];
                var macros = new byte[64];
                if (cell != 0)
                    decoder.Decode(world, cell, macros, tiles, types);
                _roomTiles[col] = tiles;
                _roomTypes[col] = types;
                _roomMeta[col] = (byte[])decoder.MetaGrid.Clone();
                var attrs = new byte[256];
                var tb = decoder.TypeBytes;
                for (int m = 0; m < 256; m++) attrs[m] = (byte)(tb[m] & 3);
                _roomAttrs[col] = attrs;
            }
        }

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

        public byte TypeAt(int worldX, int worldY)
        {
            int cell = (worldX >> 8) & 0xFFFF;
            if ((uint)cell >= (uint)_world.RowWidth) return 0;
            return _roomTypes[cell][((worldY & 0xFF) >> 4) * 16 + ((worldX & 0xFF) >> 4)];
        }
    }
}
