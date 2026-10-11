namespace Darkwing.Core
{
    /// <summary>
    /// World/mission progression state, mirroring the original RAM model
    /// (docs/game_flow.md): $31 = current world id, $38 = completed-count tier,
    /// $69 = completed-missions bitmask, $F8 = last mission outcome (bit7 = success).
    /// Descriptor tables live in PRG bank 7 (exported as world_bank7.bytes).
    /// </summary>
    public sealed class WorldState
    {
        // World ids: 0-6 missions, 7-14 cutscene scenes, 31 = city map.
        public const int MapWorld = 31;

        public byte CurrentWorld;       // $31
        public byte CompletedTier;      // $38 (0->{0,1,2}, 3->{3,4,5}, 6->{6})
        public byte CompletedMask;      // $69
        public byte LastOutcome;        // $F8
        public byte CursorBubble;       // $6C (map HELP bubble cursor)
        public byte ConfirmState;       // 0 = none, 1 = asking yes/no

        public readonly byte[] Bank7;   // world_bank7.bytes

        public WorldState(byte[] bank7) { Bank7 = bank7; }

        // --- descriptor tables (bank 7, verified by flow research) ---
        public int GridPointer(byte world) => Bank7[0xDF50 - 0xC000 + world] | (Bank7[0xDF60 - 0xC000 + world] << 8);
        public byte StartCell(byte world) => Bank7[0xDEB9 - 0xC000 + world];
        public byte ScrollYInit(byte world) => Bank7[0xDEC8 - 0xC000 + world];
        public byte MusicId(byte world) => Bank7[0xDF04 - 0xC000 + world];
        public byte SpritePaletteSet(byte world) => Bank7[0xDF22 - 0xC000 + world];
        public byte ChrRequest0(byte world) => Bank7[0xDEE6 - 0xC000 + world];
        public byte ChrRequest1(byte world) => Bank7[0xDEF5 - 0xC000 + world];

        /// <summary>Is the mission selectable on the map given current tier ($A400 rules)?</summary>
        public static bool IsSelectable(int mission, byte tier) => tier switch
        {
            0 => mission is 0 or 1 or 2,
            1 => mission is 3 or 4 or 5,
            2 => mission == 6,
            _ => false,
        };

        /// <summary>Mission completion (flow $D4B3): set mask bit, advance tier.</summary>
        public void CompleteMission(int mission)
        {
            CompletedMask |= (byte)(1 << mission);
            LastOutcome = 0x80;
            CompletedTier = CompletedTier switch
            {
                0 => CompletedMask switch { var m when (m & 0b00000111) == 0b00000111 => (byte)1, _ => (byte)0 },
                1 => CompletedMask switch { var m when (m & 0b01111000) == 0b01111000 => (byte)2, _ => (byte)1 },
                _ => 2,
            };
        }

        /// <summary>Map Select handler ($C9B3 rules simplified): cycle gas weapon on/off.</summary>
        public void ToggleGas(ref byte gasToggle) => gasToggle ^= 1;
    }
}
