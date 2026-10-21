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
        private readonly System.Collections.Generic.Dictionary<int, byte[]> _collisionRooms = new();
        private readonly System.Collections.Generic.Dictionary<int, (byte[] tiles, byte[] meta, byte[] attrs)> _visualRooms = new();
        public byte CameraY;

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
        }

        /// <summary>Current grid row ($FB).</summary>
        public int Row => _row;

        /// <summary>$36 — the section loader rewrites the metatile-set bank before it
        /// rebuilds the row ($E38F STA $36 ... JSR $DE24 → $E698), so decoding must
        /// use the new set. Same-world only: $E388 never touches $32/$34/$2E.</summary>
        public void ApplyTilesetBank(byte setBank)
        {
            _world.SetBank = setBank;
            _collisionRooms.Clear();
            _visualRooms.Clear();
        }

        /// <summary>Grid row stride = $2E: linear cell index $22 = row*GridWidth + column.</summary>
        public int GridWidth => _world.RowWidth;

        /// <summary>The world descriptor this map was built from ($DD57 tables).</summary>
        public RoomDecoder.World World => _world;

        /// <summary>Number of grid rows described by the level's cell arrays.</summary>
        public int GridRows => _world.Grid.Length / _world.RowWidth;

        /// <summary>Grid byte ($32),Y — room id for linear cell index Y=$22. 0 = no cell ($DC7A/$DCAD).</summary>
        public byte GridByte(int index) =>
            (uint)index < (uint)_world.Grid.Length ? _world.Grid[index] : (byte)0;

        /// <summary>Script byte ($34),Y — cell flags: bit7 blocks the row change ($DC9B), low nibble 1 = hook column ($DAFD).</summary>
        public byte FlagByte(int index) =>
            (uint)index < (uint)_world.Flags.Length ? _world.Flags[index] : (byte)0;

        /// <summary>
        /// Whole-window repaint: page 0 = the camera cell, page 1 = the cell below (a
        /// vertical ride) or the next one (a horizontal room). This is NOT what the loader
        /// does — measured on the original, the staging window leaves the back page at its
        /// previous-screen bytes and the $E698 ring adds only its own band stripes (m1 f1847:
        /// page 1 = 840 untouched name bytes plus the two stripes of cursors $F8-$FF and
        /// $80-$87, and that state is still unchanged at f2600). No scrolling-world entry
        /// repaints through here any more: the world load ($DD57) and the section load ($E388)
        /// publish scroll only and let <see cref="StreamBand"/> own the nametable, and the row
        /// service ($E7C6, through <see cref="ReloadRow"/>) stopped painting pages here — its
        /// own whole-row writes are still unported. Remaining callers are the resident map
        /// fly-through and the non-scrolling pseudo
        /// scenes, whose own blit is not modelled. The cell/row formula here ($22 from
        /// scrollX, `_row + page`) is still the pre-$23 approximation.
        /// </summary>
        public void StreamColumns(ushort scrollX)
        {
            int firstCell = scrollX >> 8;
            _ppu.ScrollX = (byte)scrollX;
            _ppu.ScrollY = CameraY;
            for (int page = 0; page < 2; page++)
            for (int col = 0; col < 32; col++)
            {
                // Horizontal rooms need two adjacent columns; an aligned vertical ride
                // needs the current 240px row plus the next row below it.
                int cell = firstCell + (CameraY == 0 ? page : 0);
                int sourceRow = _row + (CameraY == 0 ? 0 : page);
                StreamColumn(cell * 32 + col, sourceRow, page);
            }
        }

        /// <summary>Per-frame PPU scroll registers only. Scrolling-world nametable bytes belong
        /// to the $E698 band stream (<see cref="StreamBand"/>), so neither the world load
        /// ($DD57) nor the section load ($E388) repaints a window. The $E7C6 row service does
        /// paint whole rows of the room at $23 and is not ported yet — see
        /// <see cref="ReloadRow"/>.</summary>
        public void SetScroll(ushort scrollX)
        {
            _ppu.ScrollX = (byte)scrollX;
            _ppu.ScrollY = CameraY;
        }

        /// <summary>
        /// One $E698 pass, addressed by the cursor as it stood when the pass entered
        /// ($E69C reads $25 for the records; $E78A–$E790 only advances it afterwards): the
        /// two tile columns 4a+2h, 4a+2h+1 (a = ($25&gt;&gt;4)&amp;7, h = ($25&gt;&gt;3)&amp;1)
        /// on the four rows 4c…4c+3 (c = $25&amp;7) of page bit7($25), plus that page's
        /// attribute byte $3C0+8c+a — the h half of it ($33/$CC masks merge the two halves
        /// across the two passes). Measured on the original buffers: every scroll frame's NT
        /// delta is exactly this band (8 tiles in 57 % of the changed frames) and no other
        /// attribute byte changes in the frame.
        /// The room the band reads from is the grid byte at <c>gridIndex</c>: $E703 does
        /// <c>LDY $23 / JSR $EB27</c> and $EB27 fetches <c>($32),Y</c> (room id) then scales
        /// it by 64 for the metatile chunk. $23 is the loader's linear cell index (row stride
        /// $2E), so the source cell follows the cursor chain — never scrollX plus page — while
        /// bit7 of the cursor only picks the destination page.
        /// </summary>
        public void StreamBand(byte cursor, int gridIndex)
        {
            int page = (cursor >> 7) & 1;
            int bandRow = cursor & 7;
            int attrCol = (cursor >> 4) & 7;
            int halfCol = (cursor >> 3) & 1;
            int cell = gridIndex % _world.RowWidth;
            int sourceRow = gridIndex / _world.RowWidth;
            int inRoomCol = 4 * attrCol + 2 * halfCol;
            if (!TryGetRoom(cell * 32 + inRoomCol, sourceRow, out byte[] tiles, out byte[] meta, out byte[] attrs))
                return;
            var nt = _ppu.NametableRam;
            int baseNt = page * 0x400;
            for (int k = 0; k < 4; k++)
            {
                int row = bandRow * 4 + k;
                if (row >= 30) continue;                 // the status-bar rows are not streamed
                nt[baseNt + row * 32 + inRoomCol] = tiles[row * 32 + inRoomCol];
                nt[baseNt + row * 32 + inRoomCol + 1] = tiles[row * 32 + inRoomCol + 1];
            }
            for (int mrow = bandRow * 2; mrow < bandRow * 2 + 2; mrow++)
            {
                if (mrow >= 15) continue;
                for (int q = 0; q < 2; q++)
                    WriteAttrQuadrant(nt, baseNt, meta, attrs, mrow, 2 * attrCol + q);
            }
        }

        private void StreamColumn(int worldTileCol, int sourceRow, int page)
        {
            int inRoomCol = worldTileCol & 31;            // 32 tile columns per 256px cell
            if (!TryGetRoom(worldTileCol, sourceRow, out byte[] tiles, out byte[] meta, out byte[] attrs)) return;
            var nt = _ppu.NametableRam;
            int baseNt = page * 0x400;
            int ringCol = inRoomCol;
            for (int row = 0; row < 30; row++)
            {
                nt[baseNt + row * 32 + ringCol] = tiles[row * 32 + inRoomCol];
            }
            // Attributes: the 2-bit palette per 16px cell, packed per NES quadrant.
            for (int mrow = 0; mrow < 15; mrow++)
                WriteAttrQuadrant(nt, baseNt, meta, attrs, mrow, inRoomCol >> 1);
        }

        /// <summary>The decoded 32×32 tile / 16×16 metatile / metatile-palette arrays that
        /// own this world tile column, building an off-row room on demand.</summary>
        private bool TryGetRoom(int worldTileCol, int sourceRow, out byte[] tiles, out byte[] meta, out byte[] attrs)
        {
            int cell = worldTileCol >> 5;
            tiles = meta = attrs = null;
            if (cell >= _world.RowWidth || sourceRow >= _world.GridRows) return false;
            if (sourceRow == _row) { tiles = _roomTiles[cell]; meta = _roomMeta[cell]; attrs = _roomAttrs[cell]; return true; }
            int key = sourceRow * _world.RowWidth + cell;
            if (!_visualRooms.TryGetValue(key, out var room))
            {
                tiles = new byte[1024]; attrs = new byte[256];
                byte roomId = _world.CellAt(sourceRow, cell);
                if (roomId != 0) _decoder.Decode(_world, roomId, new byte[64], tiles, new byte[256]);
                meta = (byte[])_decoder.MetaGrid.Clone();
                for (int m = 0; m < 256; m++) attrs[m] = (byte)(_decoder.TypeBytes[m] & 3);
                room = (tiles, meta, attrs); _visualRooms[key] = room;
            }
            (tiles, meta, attrs) = room;
            return true;
        }

        /// <summary>One 2-bit attribute quadrant: the palette of the 16px metatile at
        /// (mrow, mcol) merged into its byte at $3C0 + (mrow&gt;&gt;1)·8 + (mcol&gt;&gt;1).</summary>
        private static void WriteAttrQuadrant(byte[] nt, int baseNt, byte[] meta, byte[] attrs, int mrow, int mcol)
        {
            int metaIndex = meta[mrow * 16 + mcol];
            int attrByteIndex = baseNt + 0x3C0 + (mrow >> 1) * 8 + (mcol >> 1);
            int shift = ((mrow & 1) << 2) | ((mcol & 1) << 1);
            byte pal = (byte)(attrs[metaIndex] & 3);
            nt[attrByteIndex] = (byte)((nt[attrByteIndex] & ~(3 << shift)) | (pal << shift));
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
        /// nibble is bit4 of Y ($EC83 PLA restores the un-shifted Y&gt;&gt;2, so $EC84/$EC85
        /// isolate Y bit4, not bit3) — i.e. the tile row is exactly Y&gt;&gt;4. The bit4 of X
        /// also enters the mask index, but $ECA9 = 0F 0F 00 00 makes it redundant.
        /// </summary>
        public byte TypeAt(int worldX, int worldY)
        {
            int cell = (worldX >> 8) & 0xFFFF;
            if ((uint)cell >= (uint)_world.RowWidth) return 0;
            int row = (worldY & 0xFF) >> 4;
            int worldRow = (worldY >> 8) & 0xFF;
            if ((uint)worldRow >= (uint)GridRows) return 0;
            byte[] types;
            if (worldRow == _row) types = _roomTypes[cell];
            else
            {
                // A vertical camera window spans two 240px rows. Its lower strip still
                // contains the old row after $FB changes; use the probe's actual room.
                int key = worldRow * _world.RowWidth + cell;
                if (!_collisionRooms.TryGetValue(key, out types))
                {
                    types = new byte[256];
                    byte room = _world.CellAt(worldRow, cell);
                    if (room != 0) _decoder.Decode(_world, room, new byte[64], new byte[1024], types);
                    _collisionRooms[key] = types;
                }
            }
            return types[row * 16 + ((worldX & 0xFF) >> 4)];
        }
    }
}
