namespace Darkwing.Core
{
    /// <summary>
    /// In-game HUD sprite layer. The block is 8 sprites (heart 2x2 then energy 2x2)
    /// written at the shared $97 cursor, so its entry index follows the slot walk:
    /// ROM frames captured from gold_contact/gold_hurt/gold_jump/gold_m1_longroute
    /// put the block at entry 1 when ($95 &amp; 1) == 0 and directly after the last
    /// object sprite otherwise — with no gap in either case (verified over 3260
    /// frames, GAPS G44.1). Layout from the original (frame 660, health 3):
    ///   e0 hidden marker (F8,C0,00,10 — the audio-init path writes it at cursor 4);
    ///   heart 2x2 tiles 80/80/81/81, right column h-flipped, screen (16,20);
    ///   energy 2x2 tile 00 (transparent) at (16,44).
    /// Energy-pip sprites (tiles A0-AB) are Y-hidden at full energy — pixel-invisible;
    /// their table and driver are still unrecovered (GAPS G44.3).
    /// </summary>
    public sealed class HudComposer
    {
        private readonly Ppu _ppu;

        public HudComposer(Ppu ppu) { _ppu = ppu; }

        /// <summary>Sprites the HUD writes: 4 heart + 4 energy = 8 entries.</summary>
        public const int SpriteCount = 8;

        /// <summary>
        /// $97 is primed to $04 by every composition round, so entry 0 is always
        /// reserved and the HUD block sits either at entry 1 (before the slot walk)
        /// or directly after it ($81E7 leaves the cursor one sprite past the last
        /// part) depending on the frame's walk direction.
        /// </summary>
        public void Compose(byte health, int startEntry)
        {
            var oam = _ppu.Oam;
            // Hidden marker at entry 0 (kept from the previous frame's DMA model).
            oam[0] = 0xF8; oam[1] = 0xC0; oam[2] = 0x00; oam[3] = 0x10;
            if ((startEntry + SpriteCount) * 4 > 256) return;
            // Heart icon (2x2 metasprite) — hidden when health reaches 0 is M4.
            // The captures show the heart's right column h-flipped and the energy cells'
            // right column not ($11-$14 attr stays $00), so the flip is per metasprite.
            WriteQuad(startEntry * 4, 0x14, 0x10, 0x80, 0x81, 0x40);
            // Energy gauge cells (tile 0 = transparent at the captured state).
            WriteQuad(startEntry * 4 + 16, 0x2C, 0x10, 0x00, 0x00, 0x00);
        }

        private void WriteQuad(int oamIndex, byte yTop, byte xLeft, byte tileTop, byte tileBottom, byte rightAttr)
        {
            var oam = _ppu.Oam;
            oam[oamIndex + 0] = yTop;      oam[oamIndex + 1] = tileTop;    oam[oamIndex + 2] = 0x00; oam[oamIndex + 3] = xLeft;
            oam[oamIndex + 4] = yTop;      oam[oamIndex + 5] = tileTop;    oam[oamIndex + 6] = rightAttr; oam[oamIndex + 7] = (byte)(xLeft + 8);
            oam[oamIndex + 8] = (byte)(yTop + 8); oam[oamIndex + 9] = tileBottom; oam[oamIndex + 10] = 0x00; oam[oamIndex + 11] = xLeft;
            oam[oamIndex + 12] = (byte)(yTop + 8); oam[oamIndex + 13] = tileBottom; oam[oamIndex + 14] = rightAttr; oam[oamIndex + 15] = (byte)(xLeft + 8);
        }
    }
}
