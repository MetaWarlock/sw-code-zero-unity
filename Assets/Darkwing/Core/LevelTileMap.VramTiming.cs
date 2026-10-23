using System;

namespace Darkwing.Core
{
    public sealed partial class LevelTileMap
    {
        public ReadOnlySpan<byte> NativeQueueBandTiles => _bandTable;
        public ReadOnlySpan<byte> NativeQueueBandPalettes => _bandPalettes;
        public bool NativeQueueRoom(int gridIndex, out byte[] tiles, out byte[] meta, out byte[] attributes)
        {
            if (GridByte(gridIndex) == 0) { tiles = null; meta = null; attributes = null; return false; }
            return TryGetRoom((gridIndex % _world.RowWidth) * 32, gridIndex / _world.RowWidth, out tiles, out meta, out attributes);
        }
    }
}
