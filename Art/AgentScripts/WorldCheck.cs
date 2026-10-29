using System.IO;
using System.Text;
using UnityEngine;

/// <summary>
/// Per-world audit of the $DD57 multi-world load path: descriptor fields decoded from
/// bank 7, full-row decode for every grid row, palette/CHR results, and the world-0
/// palette checked against the captured mission-1 gameplay palette.
/// </summary>
public static class WorldCheck
{
    private const string Root = "D:/Art/SW Code Zero Unity";

    /// reference/probe_m1_enter palette_frames frame 700 (original mission-1 gameplay).
    private static readonly byte[] CapturedM1Palette =
    {
        0x0F,0x20,0x27,0x17, 0x0F,0x37,0x07,0x01, 0x0F,0x20,0x31,0x22, 0x0F,0x0F,0x11,0x01,
        0x0F,0x0F,0x30,0x27, 0x0F,0x0F,0x27,0x13, 0x0F,0x0F,0x27,0x15, 0x0F,0x0F,0x37,0x07,
    };

    public static string Run()
    {
        var data = Darkwing.Core.DataLoader.Load(
            File.ReadAllBytes(Root + "/Art/Unity/CodeZero/Darkwing/Data/chr_full.bytes"),
            File.ReadAllBytes(Root + "/Art/Unity/CodeZero/Darkwing/Data/levelsets.bytes"),
            File.ReadAllBytes(Root + "/Art/Unity/CodeZero/Darkwing/Data/screens.bytes"),
            File.ReadAllBytes(Root + "/Art/Unity/CodeZero/Darkwing/Data/levels.bytes"),
            File.ReadAllBytes(Root + "/Art/Unity/CodeZero/Darkwing/Data/anims.bytes"));
        byte[] bank7 = File.ReadAllBytes(Root + "/Art/Unity/CodeZero/Darkwing/Data/world_bank7.bytes");
        var decoder = new Darkwing.Core.RoomDecoder(
            File.ReadAllBytes(Root + "/Art/Unity/CodeZero/Darkwing/Data/prg_bank0.bytes"),
            File.ReadAllBytes(Root + "/Art/Unity/CodeZero/Darkwing/Data/prg_bank1.bytes"),
            bank7, File.ReadAllBytes(Root + "/Art/Unity/CodeZero/Darkwing/Data/levelsets.bytes"));

        var worlds = new System.Collections.Generic.List<string>();
        string world0Note = "";

        for (byte id = 0; id < 7; id++)
        {
            var game = new Darkwing.Core.DarkwingGame(data);
            game.Bank5 = File.ReadAllBytes(Root + "/Art/Unity/CodeZero/Darkwing/Data/prg_bank5.bytes");
            game.Bank7 = bank7;
            game.Bank6 = File.ReadAllBytes(Root + "/Art/Unity/CodeZero/Darkwing/Data/prg_bank6.bytes");
            game.ObjectTypeTable = File.ReadAllBytes(Root + "/Art/Unity/CodeZero/Darkwing/Data/object_types.bytes");

            var w = decoder.GetWorld(id);
            game.LoadWorld(decoder, id);
            var map = game.TileMap;
            var p = game.Player;

            // Every grid row must decode: LoadRow does the room-block + levelset reads.
            int rowFail = 0, cellsNonZero = 0;
            for (int row = 0; row < map.GridRows; row++)
            {
                try
                {
                    map.LoadRow(row);
                    for (int col = 0; col < map.GridWidth; col++)
                        if (w.CellAt(row, col) != 0) cellsNonZero++;
                }
                catch (System.Exception e)
                {
                    rowFail++;
                    Debug.LogError($"world {id} row {row}: {e.Message}");
                }
            }
            map.LoadRow(w.StartRow);
            map.StreamColumns(p.ScrollX);

            // Start-screen nametable window ($2000, 30 rows x 32 columns).
            var nt = game.Ppu.NametableRam;
            var distinct = new System.Collections.Generic.HashSet<byte>();
            int blanks = 0;
            for (int i = 0; i < 30 * 32; i++)
            {
                distinct.Add(nt[i]);
                if (nt[i] == 0x24) blanks++;
            }

            var pal = new byte[32];
            for (int i = 0; i < 32; i++) pal[i] = game.Ppu.PaletteRam[i];

            worlds.Add($"{{\"id\":{id},\"set\":{w.SetBank},\"roomBank\":{w.RoomBank}," +
                $"\"startCell\":{w.StartCell},\"startRow\":{w.StartRow}," +
                $"\"width\":{w.RowWidth},\"rows\":{map.GridRows}," +
                $"\"gridBytes\":{w.Grid.Length},\"flagBytes\":{w.Flags.Length}," +
                $"\"cellsNonZero\":{cellsNonZero},\"rowFailures\":{rowFail}," +
                $"\"chr\":\"{game.Ppu.Chr0Bank:X2}/{game.Ppu.Chr1Bank:X2}\"," +
                $"\"bgPalIdx\":\"{w.BgPalIdx:X2}\",\"spPalIdx\":\"{w.SpPalIdx:X2}\"," +
                $"\"ring\":\"{w.RingEvent:X2}\",\"flag3D\":\"{w.Flag3D:X2}\",\"topRow\":{w.TopRow}," +
                $"\"player\":\"{p.State:X2} X={p.X} Y={p.Y} scroll={p.ScrollX} row={p.ScrollYRow}\"," +
                $"\"ntDistinct\":{distinct.Count},\"ntBlank24\":{blanks}," +
                $"\"pal\":\"{Hex(pal)}\"}}");

            if (id == 0)
            {
                bool same = true;
                for (int i = 0; i < 32; i++) if (pal[i] != CapturedM1Palette[i]) same = false;
                world0Note = $"world0 ROM palette vs captured m1 gameplay palette: {(same ? "MATCH" : "DIFFERS")}; " +
                             $"gold={Hex(CapturedM1Palette)} rom={Hex(pal)}";
                Debug.Log("[world0] " + world0Note);
            }
        }

        string json = "{\n  \"worlds\":[\n    " + string.Join(",\n    ", worlds) +
                      $"\n  ],\n  \"world0PaletteCheck\":\"{world0Note}\"\n}}\n";
        File.WriteAllText(Root + "/Art/Temp/world_check.json", json);
        Debug.Log(json);
        return json;
    }

    private static string Hex(byte[] b)
    {
        var s = new StringBuilder();
        for (int i = 0; i < b.Length; i++) s.Append(b[i].ToString("X2"));
        return s.ToString();
    }
}
