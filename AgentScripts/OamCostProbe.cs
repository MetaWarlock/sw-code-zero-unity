using System;
using System.IO;
using Darkwing.Core;

public static class OamCostProbe
{
    public static string Run(string prefix = "Temp/oam_probe")
    {
        if (UnityEditor.EditorApplication.isCompiling || UnityEditor.EditorUtility.scriptCompilationFailed)
            throw new InvalidOperationException("Refuse a stale Core assembly while compilation is incomplete");
        const string root = "D:/Art/SW Code Zero Unity/";
        var bank = File.ReadAllBytes(root + "Assets/Darkwing/Data/prg_bank5.bytes");
        var bank4 = File.ReadAllBytes(root + "Assets/Darkwing/Data/prg_bank4.bytes");
        var ram = File.ReadAllBytes(Path.GetFullPath(Path.Combine(root, prefix + ".bin")));
        if (ram.Length != 2048) throw new InvalidDataException("Expected one 2048-byte RAM snapshot");
        var objects = new ObjectSystem(null);
        var player = new PlayerCore(EmptyTileMap.Instance);
        var hud = new HudComposer();
        var oam = new OamDispatcher(bank, objects, new Ppu(null), player);
        oam.SetHud(hud);
        oam.MapRamPartBlock(0, ram);
        player.ScrollX = (ushort)(ram[0xFC] | ram[0xFD] << 8);
        player.ScrollY = ram[0xFA];
        player.ScrollYRow = ram[0xFB];
        player.InvulnFrames = ram[0x7C];
        Array.Copy(ram, 0x5B0, hud.Param, 0, hud.Param.Length);
        for (int slot = 0; slot < 16; slot++)
        {
            var state = objects.Slots[slot];
            state.Flags = ram[0x400 + slot];
            state.AnimFrame = ram[0x410 + slot];
            state.AnimNumber = ram[0x420 + slot];
            state.AnimTimer = ram[0x430 + slot];
            state.Active = (ram[0x4D0 + slot] & 128) != 0;
            state.X = (ushort)(ram[0x4F0 + slot] | ram[0x500 + slot] << 8);
            state.Y = (ushort)(ram[0x510 + slot] | ram[0x520 + slot] << 8);
        }
        int external = oam.MeasureFrameCycles((byte)(ram[0x95] + 1));
        return $"external={external} parity={ram[0x95] + 1}";
    }
}
