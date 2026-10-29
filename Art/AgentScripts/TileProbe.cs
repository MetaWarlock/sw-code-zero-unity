using System;
using System.IO;
using System.Text;
using Darkwing.Core;

/// <summary>
/// G88 probe: what does the port's tile grid actually hold at the wolf-duck arena floor row,
/// and does streaming at the captured scroll change it? Reads the same captures the harness
/// seeds from, so the rows compared are the ROM's own landing rows.
/// </summary>
public static class TileProbe
{
    public static string Run()
    {
        string root = "D:/Art/SW Code Zero Unity";
        var data = DataLoader.Load(
            File.ReadAllBytes(root + "/Art/Unity/CodeZero/Darkwing/Data/chr_full.bytes"),
            File.ReadAllBytes(root + "/Art/Unity/CodeZero/Darkwing/Data/levelsets.bytes"),
            File.ReadAllBytes(root + "/Art/Unity/CodeZero/Darkwing/Data/screens.bytes"),
            File.ReadAllBytes(root + "/Art/Unity/CodeZero/Darkwing/Data/levels.bytes"),
            File.ReadAllBytes(root + "/Art/Unity/CodeZero/Darkwing/Data/anims.bytes"));
        var game = new DarkwingGame(data);
        var decoder = new RoomDecoder(
            File.ReadAllBytes(root + "/Art/Unity/CodeZero/Darkwing/Data/prg_bank0.bytes"),
            File.ReadAllBytes(root + "/Art/Unity/CodeZero/Darkwing/Data/prg_bank1.bytes"),
            File.ReadAllBytes(root + "/Art/Unity/CodeZero/Darkwing/Data/world_bank7.bytes"),
            File.ReadAllBytes(root + "/Art/Unity/CodeZero/Darkwing/Data/levelsets.bytes"));
        var world0 = decoder.GetWorld(0);
        var sb = new StringBuilder();
        sb.AppendLine($"world GridWidth={world0.RowWidth}");

        foreach (var cap in new[] { "tmp_boss_pilot14_wolf_defeat", "tmp_boss_pilot1" })
        {
            byte[] ram = File.ReadAllBytes(
                $"{root}/Research/DarkwingDuck/reference/{cap}/ram_frames.bin");
            int seed = cap.EndsWith("pilot1") ? 27 : 0;
            Func<int, int> g = a => ram[seed * 2048 + a];
            int px = g(0x4F0) | (g(0x500) << 8);
            int py = g(0x510) | (g(0x520) << 8);
            ushort scrollX = (ushort)(g(0xFC) | (g(0xFD) << 8));
            int scrollY = (g(0xFB) << 8) | g(0xFA);
            var map = new LevelTileMap(decoder, game.Ppu, world0);
            map.LoadRow(scrollY >> 8);                 // $FB: the captures live on grid row 1
            map.StreamColumns(scrollX);
            sb.AppendLine($"\n== {cap} seed f{seed}: player X={px:X4} Y={py:X4} scrollX={scrollX:X4} scrollY={scrollY:X4} " +
                          $"$22={g(0x22):X2} $6D={g(0x6D):X2} row={map.Row}/{map.GridRows}");
            // ROM landing row: py = Y + 12 sampled by $EB52; row index = (py & FF) >> 4.
            int probeY = (py + 12) & 0xFF;
            sb.AppendLine($"ROM floor sample py={probeY:X2} row={probeY >> 4}");
            foreach (int col in new[] { 2, 3, 4 })
            {
                int wx = (px & 0xFF00) | (col << 4);
                var line = new StringBuilder($"  cell={wx >> 8:X2} room={world0.CellAt(map.Row, wx >> 8)} colidx={col}: ");
                for (int row = 0; row < 16; row++)
                    line.Append(map.TypeAt(wx, (scrollY & 0xFF00) | (row << 4)) & 0x0F).Append(' ');
                sb.AppendLine(line.ToString());
            }
            // What the three floor probes of $EB52 see for the ROM's own Y and for Y+2 rows.
            foreach (int dy in new[] { 0, 16, 32 })
            {
                int y = py + dy;
                var t = new StringBuilder();
                foreach (int dx in new[] { -11, 0, 11 })
                    t.Append(map.TypeAt((short)(px + dx), y + 12)).Append('/');
                sb.AppendLine($"  Y={y:X4} probeY={(y + 12) & 0xFF:X2} row={((y + 12) & 0xFF) >> 4} types={t}");
            }
        }
        return sb.ToString();
    }
}
