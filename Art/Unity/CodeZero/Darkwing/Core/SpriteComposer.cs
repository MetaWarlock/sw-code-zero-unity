namespace Darkwing.Core
{
    /// <summary>
    /// Sprite composition: metasprite frame parts -> OAM entries -> framebuffer,
    /// mirroring the original OAM builder (bank 5 $8150, shadow OAM $0200, DMA).
    /// Parts: tile id, attribute byte (palette bits 0-1, flip H 0x40, flip V 0x80,
    /// priority 0x20), signed X/Y offsets relative to the object origin.
    /// Sprite pixels respect background priority and the 8-sprites-per-scanline
    /// rule is not emulated (the original flickers; the game tolerates the loss —
    /// flicker policy to be matched in M6 verification).
    /// </summary>
    public sealed class SpriteComposer
    {
        private readonly GameData _data;
        private readonly byte[] _tile = new byte[64];

        public SpriteComposer(GameData data) => _data = data;

        /// <summary>Draw one metasprite frame at the given origin and scroll.</summary>
        public void DrawFrame(GameData.FrameDef frame, int chrBank, int originX, int originY,
                              int scrollX, int scrollY, byte[] pal, byte[] framebuffer)
        {
            foreach (var part in frame.Parts)
            {
                int px = originX + part.X - scrollX;
                int py = originY + part.Y - scrollY;
                bool flipH = (part.Attr & 0x40) != 0;
                bool flipV = (part.Attr & 0x80) != 0;
                bool behindBg = (part.Attr & 0x20) != 0;
                int palSlot = 4 + (part.Attr & 3); // sprite palettes live at $3F10+
                _data.DecodeTile(chrBank, part.Tile, _tile, 0);
                for (int y = 0; y < 8; y++)
                {
                    int sy = py + (flipV ? 7 - y : y);
                    if ((uint)sy >= 240) continue;
                    for (int x = 0; x < 8; x++)
                    {
                        int sx = px + (flipH ? 7 - x : x);
                        if ((uint)sx >= 256) continue;
                        byte ci = _tile[y * 8 + x];
                        if (ci == 0) continue; // transparent
                        int di = sy * 256 + sx;
                        if (behindBg && framebuffer[di] != pal[0]) continue; // bg pixel wins unless transparent
                        framebuffer[di] = pal[palSlot * 4 + ci - 0];
                        // NOTE: sprite color index uses palette entry palSlot*4+ci with the
                        // sprite palette base offset applied by the caller (pal layout below).
                    }
                }
            }
        }
    }
}
