using UnityEngine;

namespace Darkwing.UnityApp
{
    /// <summary>
    /// Fixed 2C02 palette RGB table (64 entries, "default" Mesen-compatible variant).
    /// Acceptance comparisons lock to this conversion; do not change per-scene.
    /// </summary>
    public static class NesPalette
    {
        public static readonly Color32[] Table = Build();

        private static Color32[] Build()
        {
            // Values taken from the reference configuration (Mesen default palette).
            int[] rgb =
            {
                0x666666,0x002A88,0x1412A7,0x3B00A4,0x5C007E,0x6E0040,0x6C0600,0x561D00,
                0x333500,0x0B4800,0x005200,0x004F08,0x00404D,0x000000,0x000000,0x000000,
                0xADADAD,0x155FD9,0x4240FF,0x7527FE,0xA01ACC,0xB71E5B,0xB53120,0x994E00,
                0x6B6D00,0x388700,0x0C9300,0x008F32,0x007C8D,0x000000,0x000000,0x000000,
                0xFFFEFF,0x64B0FF,0x9290FF,0xC676FF,0xF36AFF,0xFE6ECC,0xFE8170,0xEA9E22,
                0xBCBE00,0x88D800,0x5CE430,0x45E082,0x48CDDE,0x4F4F4F,0x000000,0x000000,
                0xFFFEFF,0xC0DFFF,0xD3D2FF,0xE8C8FF,0xFBC2FF,0xFEC4EA,0xFECCC5,0xF7D8A5,
                0xE4E594,0xCFEF96,0xBDF4AB,0xB3F3CC,0xA5D4DE,0xC8C8C8,0x000000,0x000000,
            };
            var t = new Color32[64];
            for (int i = 0; i < 64; i++)
            {
                t[i] = new Color32((byte)(rgb[i] >> 16), (byte)((rgb[i] >> 8) & 0xFF), (byte)(rgb[i] & 0xFF), 255);
            }
            return t;
        }
    }
}
