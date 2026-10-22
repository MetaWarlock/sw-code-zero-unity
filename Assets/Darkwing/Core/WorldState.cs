namespace Darkwing.Core
{
    /// <summary>
    /// World/mission progression state, mirroring the original RAM model
    /// (docs/game_flow.md + $A1D0/$A448/$A5AB/$994F disassembly):
    /// $31 = current world id, $38 = MISSIONS COMPLETED counter (0..6 — the bubble
    /// handlers compare it with 3 and 6, so it is a count, not a tier index),
    /// $69 = completed-missions bitmask, $F8 = outcome ($FF only from the briefing
    /// prompt's Left branch at $998C; cleared to 0 by the world loader at $DE66),
    /// $62 = the briefing/dialog text stream id of the node under the cursor.
    /// Descriptor tables live in PRG bank 7 (exported as world_bank7.bytes).
    /// </summary>
    public sealed class WorldState
    {
        // World ids: 0-6 missions, 7-14 cutscene scenes, 31 = city map.
        public const int MapWorld = 31;

        /// <summary>
        /// Map CHR requests are explicit in bank 2 $8E16-$8E1F: $1A=$9F,
        /// $1B=$9A. NMI masks the high bit: sprites bank 31, background bank 26.
        /// probe_flow f1819+ confirms CHR0/CHR1=31/26. The old background-only
        /// screenshot search did not establish the sprite bank.
        /// </summary>
        public const byte MapChrBank = 26; // background $1000
        public const byte MapSpriteChrBank = 31; // probe_flow f1819+: CHR0=$1F, CHR1=$1A

        // --- GAME OVER outro ($D46C) ---
        // gold_gameover/chr_map.csv latches chr0=$1F / chr1=$19 from f265 (the frame the
        // card's palette lands) to the end of the capture, and ppu_writes.csv f300 shows
        // PPUCTRL $90 ($2000=144) — BG pattern table at $1000, so the font tiles
        // ($DA + letter) come from 4K bank 25 while bank 31 covers the unused $0000 half.
        public const byte GameOverChrBank0 = 31;
        public const byte GameOverChrBank1 = 25;

        /// <summary>Bank 2 ($8000-$BFFF) screen-record stream the outro draws ($D49A-$D4A4
        /// switch in bank 2, run its task 3, then $8D00): [addrHi][addrLo][len] followed by
        /// len nametable tiles, ended by a record with bit7 of addrHi set. Decoded from the
        /// ROM: "GAME" @ $20AE, "OVER" @ $20EE, "CONTINUE     A BUTTON" @ $2165,
        /// "STAGE SELECT B BUTTON" @ $21A5 — matching gold_gameover/frame_000300.png.</summary>
        public const int GameOverRecords = 0x8789;

        /// <summary>Bank 2 $8D00 copies these 16 bytes to $0300/$0320 and flags the $C059 NMI
        /// palette upload ($18/$1B=$99): the card's BG palette, byte-identical to
        /// gold_gameover/palette_frames.bin f264-f399.</summary>
        public const int GameOverPalette = 0x8D15;

        public byte CurrentWorld;       // $31
        public byte CompletedCount;     // $38 (INC at $D95B per cleared mission; 0..6)
        public byte CompletedMask;      // $69 (bit N = mission N cleared, set at $A5D1+)
        public byte LastOutcome;        // $F8 (bit7 = the player chose Left at the prompt)
        public byte StreamId;           // $62 (text stream / node index)
        public byte GameCompletedFlag;  // $65 (#$FF written by $D8CD at the end of the ending body)
        public byte CursorBubble;       // $6C (map HELP bubble cursor, 0..2)

        public readonly byte[] Bank7;   // world_bank7.bytes

        public WorldState(byte[] bank7) { Bank7 = bank7; }

        // --- descriptor tables (bank 7, verified by flow research) ---
        public int GridPointer(byte world) => Bank7[0xDF50 - 0xC000 + world] | (Bank7[0xDF60 - 0xC000 + world] << 8);
        public byte StartCell(byte world) => Bank7[0xDEB9 - 0xC000 + world];
        public byte ScrollYInit(byte world) => Bank7[0xDEC8 - 0xC000 + world];
        public byte BgPaletteIndex(byte world) => Bank7[0xDF04 - 0xC000 + world];
        public byte SpritePaletteSet(byte world) => Bank7[0xDF22 - 0xC000 + world];
        public byte ChrRequest0(byte world) => Bank7[0xDEE6 - 0xC000 + world];
        public byte ChrRequest1(byte world) => Bank7[0xDEF5 - 0xC000 + world];
    }
}
