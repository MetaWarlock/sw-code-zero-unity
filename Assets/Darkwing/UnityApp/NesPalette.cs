using UnityEngine;

namespace Darkwing.UnityApp
{
    /// <summary>
    /// Fixed 2C02 palette RGB table, byte-identical to Mesen's default video filter
    /// (mesen-agent Core NES NesDefaultVideoFilter.cpp, _ppuPaletteArgb[0] "2C02"),
    /// with the identity palette LUT. Acceptance comparisons lock to this table.
    /// </summary>
    public static class NesPalette
    {
        public static readonly Color32[] Table = Build();

        private static Color32[] Build()
        {
            uint[] argb =
            {
                0xFF666666,0xFF002A88,0xFF1412A7,0xFF3B00A4,0xFF5C007E,0xFF6E0040,0xFF6C0600,0xFF561D00,
                0xFF333500,0xFF0B4800,0xFF005200,0xFF004F08,0xFF00404D,0xFF000000,0xFF000000,0xFF000000,
                0xFFADADAD,0xFF155FD9,0xFF4240FF,0xFF7527FE,0xFFA01ACC,0xFFB71E7B,0xFFB53120,0xFF994E00,
                0xFF6B6D00,0xFF388700,0xFF0C9300,0xFF008F32,0xFF007C8D,0xFF000000,0xFF000000,0xFF000000,
                0xFFFFFEFF,0xFF64B0FF,0xFF9290FF,0xFFC676FF,0xFFF36AFF,0xFFFE6ECC,0xFFFE8170,0xFFEA9E22,
                0xFFBCBE00,0xFF88D800,0xFF5CE430,0xFF45E082,0xFF48CDDE,0xFF4F4F4F,0xFF000000,0xFF000000,
                0xFFFFFEFF,0xFFC0DFFF,0xFFD3D2FF,0xFFE8C8FF,0xFFFBC2FF,0xFFFEC4EA,0xFFFECCC5,0xFFF7D8A5,
                0xFFE4E594,0xFFCFEF96,0xFFBDF4AB,0xFFB3F3CC,0xFFB5EBF2,0xFFB8B8B8,0xFF000000,0xFF000000,
            };
            var t = new Color32[64];
            for (int i = 0; i < 64; i++)
            {
                uint v = argb[i];
                t[i] = new Color32((byte)(v >> 16), (byte)(v >> 8), (byte)v, 255);
            }
            return t;
        }
    }
}
