namespace Darkwing.Core
{
    /// <summary>
    /// In-game HUD sprite layer, composed before the object slots (OAM entries 0-8;
    /// the slot dispatcher starts at cursor 36). Layout captured from the original
    /// (frame 660, health 3):
    ///   e0 hidden marker (F8,C0,00,10 — the audio-init path writes it at cursor 4);
    ///   e1-4 heart icon: 2x2 tiles 80/81, right column h-flipped, screen (16,20);
    ///   e5-8 energy gauge cells: tile 00 (transparent) at (16,44).
    /// Energy-pip sprites (tiles A0-AB) compose after the slot loop and are
    /// Y-hidden at full energy — pixel-invisible; wired in M7 with the bank-4
    /// HUD decoder.
    /// </summary>
    public sealed class HudComposer
    {
        private readonly Ppu _ppu;

        public HudComposer(Ppu ppu) { _ppu = ppu; }

        public void Compose(byte health)
        {
            var oam = _ppu.Oam;
            // Hidden marker at entry 0 (kept from the previous frame's DMA model).
            oam[0] = 0xF8; oam[1] = 0xC0; oam[2] = 0x00; oam[3] = 0x10;
            // Heart icon (2x2 metasprite) — hidden when health reaches 0 is M4.
            WriteQuad(4, 0x14, 0x10, 0x80, 0x81);
            // Energy gauge cells (tile 0 = transparent at the captured state).
            WriteQuad(20, 0x2C, 0x10, 0x00, 0x00);
        }

        private void WriteQuad(int oamIndex, byte yTop, byte xLeft, byte tileTop, byte tileBottom)
        {
            var oam = _ppu.Oam;
            oam[oamIndex + 0] = yTop;      oam[oamIndex + 1] = tileTop;    oam[oamIndex + 2] = 0x00; oam[oamIndex + 3] = xLeft;
            oam[oamIndex + 4] = yTop;      oam[oamIndex + 5] = tileTop;    oam[oamIndex + 6] = 0x40; oam[oamIndex + 7] = (byte)(xLeft + 8);
            oam[oamIndex + 8] = (byte)(yTop + 8); oam[oamIndex + 9] = tileBottom; oam[oamIndex + 10] = 0x00; oam[oamIndex + 11] = xLeft;
            oam[oamIndex + 12] = (byte)(yTop + 8); oam[oamIndex + 13] = tileBottom; oam[oamIndex + 14] = 0x40; oam[oamIndex + 15] = (byte)(xLeft + 8);
        }
    }
}
