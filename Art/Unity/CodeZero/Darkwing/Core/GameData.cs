using System.Collections.Generic;

namespace Darkwing.Core
{
    /// <summary>
    /// Immutable, engine-free game data exported from the original ROM by
    /// Art/Tools/DarkwingResearch/export_unity.py (see Data/manifest.json for provenance).
    /// Byte layouts mirror the exporter exactly.
    /// </summary>
    public sealed class GameData
    {
        public const int ChrBanks = 32;
        public const int ChrBankBytes = 4096;
        public const int LevelSetCount = 7;
        public const int ScreenCount = 300;

        public readonly byte[] Chr;                       // 32 * 4096
        public readonly byte[] LevelSets;                 // per set: 1024 macro + 1024 meta ids + 256 attrs
        public readonly byte[] Screens;                   // 300 * 64 macrotile indices
        public readonly LevelDef[] Levels;
        public readonly AnimationDef[] Animations;
        public readonly FrameDef[] Frames;

        public GameData(byte[] chr, byte[] levelSets, byte[] screens,
                        List<LevelDef> levels, List<AnimationDef> animations, List<FrameDef> frames)
        {
            Chr = chr;
            LevelSets = levelSets;
            Screens = screens;
            Levels = levels.ToArray();
            Animations = animations.ToArray();
            Frames = frames.ToArray();
        }

        /// <summary>Decode one 8x8 tile from a 4 KiB CHR bank into 64 2-bit pixels.</summary>
        public void DecodeTile(int chrBank, int tileIndex, byte[] dest, int destOffset)
        {
            int baseOff = chrBank * ChrBankBytes + tileIndex * 16;
            for (int y = 0; y < 8; y++)
            {
                int lo = Chr[baseOff + y];
                int hi = Chr[baseOff + y + 8];
                int bit = 0x80;
                for (int x = 0; x < 8; x++)
                {
                    int c = ((lo & bit) != 0 ? 1 : 0) | ((hi & bit) != 0 ? 2 : 0);
                    dest[destOffset + y * 8 + x] = (byte)c;
                    bit >>= 1;
                }
            }
        }

        public sealed class LevelDef
        {
            public int Id;
            public string Name;
            public int Width, Height;         // screens
            public int LayoutOffset;
            public byte[] Cells;              // raw layout cell per screen index
            public byte[] ScrollDoor;
            public int ObjectCount;
            public Placement[] Objects;
            public int TileSet;
            public int BgChrBank;             // verified for set 0: 16
            public int FirstScreen;
        }

        public struct Placement
        {
            public int Index, Type, ScreenX, X, ScreenY, Y;
        }

        public sealed class AnimationDef
        {
            public int Id, RawHeader, TimerRaw;
            public int[] Frames;
        }

        public sealed class FrameDef
        {
            public int Id;
            public string Kind;               // "rom" or "ram_dynamic"
            public int CoordId = -1;
            public Part[] Parts;

            public struct Part
            {
                public int Tile, Attr, X, Y;
            }
        }
    }
}
