namespace Darkwing.Core
{
    /// <summary>
    /// Room-backed collision map and nametable loader. World space: each grid cell
    /// is a 256x256px room (32x32 tiles); worldY = gridRow*256 + inCellY.
    /// The nametable attribute (palette slot) of a metatile = its type byte &amp; 3
    /// (verified 240/240 against the captured mission-1 nametable).
    /// </summary>
    public sealed class LevelTileMap : ITileMap
    {
        private readonly RoomDecoder _decoder;
        private readonly Ppu _ppu;

        public RoomDecoder.World World;
        public byte CurrentCell;
        private readonly byte[] _types = new byte[256];       // 16x16 collision types
        private readonly byte[] _tiles = new byte[1024];      // 32x32 tile ids
        private readonly byte[] _macros = new byte[64];

        public LevelTileMap(RoomDecoder decoder, Ppu ppu)
        {
            _decoder = decoder;
            _ppu = ppu;
        }

        /// <summary>Load a room cell: fills the PPU nametable and the type grid.</summary>
        public void LoadRoom(RoomDecoder.World world, int row, int col)
        {
            World = world;
            byte cell = world.CellAt(row, col);
            CurrentCell = cell;
            if (cell == 0)
            {
                System.Array.Clear(_types, 0, _types.Length);
                System.Array.Clear(_tiles, 0, _tiles.Length);
            }
            else
            {
                _decoder.Decode(world, cell, _macros, _tiles, _types);
            }
            var metaGrid = _decoder.MetaGrid;
            var typeBytes = _decoder.TypeBytes;
            var nt = _ppu.NametableRam;
            System.Array.Clear(nt, 0x3C0, 0x40);
            for (int r = 0; r < 30; r++)
                System.Array.Copy(_tiles, r * 32, nt, r * 32, 32);
            for (int r = 0; r < 15; r++)
            {
                for (int c = 0; c < 16; c++)
                {
                    int metaIndex = metaGrid[r * 16 + c];
                    int typeByte = typeBytes[metaIndex];
                    int attrByteIndex = 0x3C0 + (r >> 1) * 8 + (c >> 1);
                    int shift = ((r & 1) << 2) | ((c & 1) << 1);
                    nt[attrByteIndex] = (byte)((nt[attrByteIndex] & ~(3 << shift)) | ((typeByte & 3) << shift));
                }
            }
            _ppu.Chr1Bank = 16 + world.SetBank;   // bg pattern table (verified: 16 for set 0)
        }

        public byte TypeAt(int worldX, int worldY)
        {
            int x = worldX & 0xFF;
            int y = worldY & 0xFF;
            int col = x >> 4, row = y >> 4;
            if ((uint)col > 15 || (uint)row > 15) return 0;
            return _types[row * 16 + col];
        }
    }
}
