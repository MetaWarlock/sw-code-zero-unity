namespace Darkwing.Core
{
    /// <summary>
    /// Composes the 256x240 background from screen/macrotile/metatile data and
    /// a 32-byte NES palette, writing NES color indices into the framebuffer.
    /// Mirrors the PPU background pipeline: screen (8x8 macrotiles of 32px)
    /// -> macrotile (2x2 metatiles of 16px) -> metatile (2x2 tiles of 8px) -> CHR tile.
    /// Palette per 16x16 metatile quadrant comes from the screen attribute byte
    /// (layout attribute area = screen index base + 0x3C0 within the 64-byte record
    /// is absent: CadEditor screens store only 64 macrotile ids; attributes come
    /// from each metatile's raw attribute (palette slot) — verified against renders).
    /// </summary>
    public sealed class BackgroundComposer
    {
        private readonly GameData _data;
        private readonly byte[] _tile = new byte[64];

        public BackgroundComposer(GameData data) => _data = data;

        /// <param name="pal">32 NES palette indices: 16 background (4 quadrants x 4) then 16 sprite.</param>
        public void Compose(int tileSet, int screenId, int scrollX, int scrollY,
                            byte[] pal, byte[] framebuffer)
        {
            // A screen is 256x256; the visible window is 256x240 scrolled inside it.
            int setBase = tileSet * 1536;
            int screenBase = screenId * 64;
            var tileTmp = _tile;
            for (int py = 0; py < 240; py++)
            {
                int worldY = scrollY + py;
                int macRow = worldY >> 5;            // /32
                int macRowInScreen = macRow & 7;
                int metaY = (worldY >> 4) & 1;       // 16px within macrotile
                int tileY = (worldY >> 3) & 1;       // 8px within metatile
                int pixY = worldY & 7;               // pixel row within tile
                int rowBase = py * 256;
                for (int px = 0; px < 256; px++)
                {
                    int worldX = scrollX + px;
                    int macCol = worldX >> 5;
                    int macColInScreen = macCol & 7;
                    int macroIndex = _data.Screens[screenBase + macRowInScreen * 8 + macColInScreen];
                    int metaIndex = _data.LevelSets[setBase + macroIndex * 4 + metaY * 2 + ((worldX >> 4) & 1)];
                    int tileIndex = _data.LevelSets[setBase + 1024 + metaIndex * 4 + tileY * 2 + ((worldX >> 3) & 1)];
                    int attr = _data.LevelSets[setBase + 2048 + metaIndex];
                    int palSlot = attr & 3;
                    _data.DecodeTile(tileSet + 16, tileIndex, tileTmp, 0);
                    int ci = tileTmp[pixY * 8 + (worldX & 7)];
                    framebuffer[rowBase + px] = pal[palSlot * 4 + ci];
                }
            }
        }
    }
}
