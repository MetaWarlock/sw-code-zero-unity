namespace Darkwing.Core
{
    /// <summary>
    /// Section/door descriptor tables read by $E388 (bank 7 = world_bank7.bytes,
    /// file offset = address - $C000). The 42-byte record addresses are taken from
    /// the $E38F-$E3E3 load sequence, not from the prose table order in
    /// docs/doors_sections.md (that list disagrees with the disassembly).
    /// </summary>
    public sealed class SectionTable
    {
        public const int Count = 42;          // ids 0..$29; id 0 is the $C2 stub

        const int Tileset = 0xE5E7 - 0xC000;  // $36 metatile-set bank ($E38F is the first read)
        const int Cell    = 0xE497 - 0xC000;  // $22/$23 start cell
        const int Row     = 0xE4C1 - 0xC000;  // $FB/$0520 start grid row
        const int Column  = 0xE4EB - 0xC000;  // $FD/$0500 start scroll column = player X hi
        const int Chr0    = 0xE515 - 0xC000;  // $1A CHR request 0 (applied by NMI $FF21)
        const int Chr1    = 0xE53F - 0xC000;  // $1B CHR request 1 ($FF3B)
        const int BgPal   = 0xE569 - 0xC000;  // index byte → $C2F9
        const int SpPal   = 0xE593 - 0xC000;  // index byte → $C319
        const int Flag3DRow = 0xE5BD - 0xC000;  // $3D — semantics still unknown (G16)
        const int RingEv  = 0xE611 - 0xC000;  // $D9 → JSR $F84E event/sound index

        const int BgPalBlock1 = 0xC35D - 0xC000; // 16 records × 16 B
        const int BgPalBlock2 = 0xC45D - 0xC000; // selected when index bit4 is set
        const int SpPalFixed  = 0xC4FD - 0xC000; // $C319 first half: always here
        const int SpPalShift  = 0xC505 - 0xC000; // $C319 second half: base + (v<<3)

        private readonly byte[] _b;

        public SectionTable(byte[] bank7) { _b = bank7; }

        public byte TilesetBank(int id) => _b[Tileset + id];   // $36
        public byte StartCell(int id) => _b[Cell + id];        // $22/$23
        public byte StartRow(int id) => _b[Row + id];          // $FB/$0520
        public byte StartColumn(int id) => _b[Column + id];    // $FD/$0500
        public byte ChrRequest0(int id) => _b[Chr0 + id];      // $1A
        public byte ChrRequest1(int id) => _b[Chr1 + id];      // $1B
        public byte Flag3D(int id) => _b[Flag3DRow + id];         // $3D
        public byte RingEvent(int id) => _b[RingEv + id];      // $D9

        /// <summary>$C2F9: four ASLs put index bit4 into carry (selects the $C45D
        /// block) and the low nibble ×16 into Y; 16 bytes → $0300 and mirror $0320.</summary>
        public void ReadBgPalette(int id, byte[] out16)
        {
            byte v = _b[BgPal + id];
            int baseOff = (v & 0x10) != 0 ? BgPalBlock2 : BgPalBlock1;
            int src = baseOff + ((v & 0x0F) << 4);
            for (int i = 0; i < 16; i++) out16[i] = _b[src + i];
        }

        /// <summary>$C319: entries 0-7 always come from $C4FD (X-indexed, not Y),
        /// entries 8-15 from $C505 + (v&lt;&lt;3); 16 bytes → $0310 and mirror $0330.</summary>
        public void ReadSpritePalette(int id, byte[] out16)
        {
            byte v = _b[SpPal + id];
            int second = SpPalShift + ((v << 3) & 0xFF);
            for (int i = 0; i < 8; i++) out16[i] = _b[SpPalFixed + i];
            for (int i = 0; i < 8; i++) out16[8 + i] = _b[second + i];
        }

        /// <summary>$E454: $6D ∈ {2,4,6,8,$A,$C,$E} → $3B=$78.</summary>
        public static bool SetsTimer3B(byte sectionId) =>
            sectionId is 0x02 or 0x04 or 0x06 or 0x08 or 0x0A or 0x0C or 0x0E;

        /// <summary>$E478: $6D ∈ {4,6,8,$A,$C,$E} → PRG=4 + JSR $9800 (internal
        /// engine); everything else → PRG=5 + JSR $8000. Neither returns; both
        /// fall through to $C270.</summary>
        public static bool UsesInternalEngine(byte sectionId) =>
            sectionId is 0x04 or 0x06 or 0x08 or 0x0A or 0x0C or 0x0E;
    }
}
