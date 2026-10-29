using System;
using System.IO;
using System.Text;
using Darkwing.Core;

public static class OamTimingVerify
{
    public static string Run(string prefix)
    {
        if (UnityEditor.EditorApplication.isCompiling || UnityEditor.EditorUtility.scriptCompilationFailed)
            throw new InvalidOperationException("Refuse a stale Core assembly while compilation is incomplete");
        const string root = "D:/Art/SW Code Zero Unity/";
        var bank = File.ReadAllBytes(root + "Art/Unity/CodeZero/Darkwing/Data/prg_bank5.bytes");
        var bank4 = File.ReadAllBytes(root + "Art/Unity/CodeZero/Darkwing/Data/prg_bank4.bytes");
        string source = Path.GetFullPath(Path.Combine(root, prefix));
        var ram = File.ReadAllBytes(source + ".bin");
        var records = File.ReadAllLines(source + ".csv");
        if (records.Length < 2 || (records[0] != "poll,start,cycles,nmi" && records[0] != "poll,start,cycles,nmi,engine")
            || ram.Length != (records.Length - 1) * 2048)
            throw new InvalidDataException("Incomplete OAM entry snapshots or unknown trace schema");
        var objects = new ObjectSystem(null);
        var player = new PlayerCore(EmptyTileMap.Instance);
        var hud = new HudComposer();
        var oam = new OamDispatcher(bank, objects, new Ppu(null), player);
        oam.SetHud(hud);
        var snapshot = new byte[2048];
        oam.MapRamPartBlock(0,snapshot);
        var result = new StringBuilder();
        int matched = 0, different = 0;
        for (int index = 1; index < records.Length; index++)
        {
            int offset = (index - 1) * 2048;
            Array.Copy(ram,offset,snapshot,0,2048);
            player.ScrollX = (ushort)(ram[offset + 0xFC] | ram[offset + 0xFD] << 8);
            player.ScrollY = ram[offset + 0xFA];
            player.ScrollYRow = ram[offset + 0xFB];
            player.InvulnFrames = ram[offset + 0x7C];
            Array.Copy(ram,offset+0x5B0,hud.Param,0,hud.Param.Length);
            for (int slot = 0; slot < 16; slot++)
            {
                var state = objects.Slots[slot];
                state.Flags = ram[offset + 0x400 + slot];
                state.AnimFrame = ram[offset + 0x410 + slot];
                state.AnimNumber = ram[offset + 0x420 + slot];
                state.AnimTimer = ram[offset + 0x430 + slot];
                state.Active = (ram[offset + 0x4D0 + slot] & 128) != 0;
                state.X = (ushort)(ram[offset+0x4F0+slot] | ram[offset+0x500+slot]<<8);
                state.Y = (ushort)(ram[offset+0x510+slot] | ram[offset+0x520+slot]<<8);
            }
            var columns = records[index].Split(',');
            int actual = 6 + (columns.Length > 4 && columns[4] == "4"
                ? oam.MeasureInternalFrameCycles((byte)(ram[offset+0x95]+1),bank4)
                : oam.MeasureFrameCycles((byte)(ram[offset+0x95]+1)));
            int expected = int.Parse(columns[2]);
            if (actual == expected) matched++;
            else
            {
                different++;
                if (different <= 16) result.AppendLine(records[index].Split(',')[0]+": "+expected+" / "+actual+" delta="+(actual-expected));
            }
        }
        string summary = "matched="+matched+" different="+different+"\n"+result;
        if (different != 0) throw new InvalidOperationException(summary);
        return summary;
    }
}
