using System.IO;
using UnityEngine;

public static class OamVerify
{
    // Loads captured original RAM+OAM at frame 660, seeds the ObjectSystem slots,
    // runs the OamDispatcher, and compares produced OAM against the capture.
    public static string Run()
    {
        string root = "D:/Art/SW Code Zero Unity";
        byte[] ramAll = File.ReadAllBytes(root + "/Research/DarkwingDuck/reference/probe_f660/ram_frames.bin");
        byte[] oamAll = File.ReadAllBytes(root + "/Research/DarkwingDuck/reference/probe_f660/oam_frames.bin");
        int frame = 660;
        byte[] ram = new byte[2048];
        System.Array.Copy(ramAll, (frame - 1) * 2048, ram, 0, 2048);
        byte[] oam = new byte[256];
        System.Array.Copy(oamAll, (frame - 1) * 256, oam, 0, 256);

        var data = Darkwing.Core.DataLoader.Load(
            File.ReadAllBytes(root + "/Assets/Darkwing/Data/chr_full.bytes"),
            File.ReadAllBytes(root + "/Assets/Darkwing/Data/levelsets.bytes"),
            File.ReadAllBytes(root + "/Assets/Darkwing/Data/screens.bytes"),
            File.ReadAllBytes(root + "/Assets/Darkwing/Data/levels.bytes"),
            File.ReadAllBytes(root + "/Assets/Darkwing/Data/anims.bytes"));
        var ppu = new Darkwing.Core.Ppu(data);
        var player = new Darkwing.Core.PlayerCore(Darkwing.Core.EmptyTileMap.Instance);
        var objects = new Darkwing.Core.ObjectSystem(data);

        // Seed slots from captured RAM ($0400 stride $10 fields, 16 slots).
        for (int i = 0; i < 16; i++)
        {
            int b = 0x400 + i * 0x10;
            var s = objects.Slots[i];
            s.Flags = ram[b + 0x00];
            s.AnimFrame = ram[b + 0x10];
            s.AnimNumber = ram[b + 0x20];
            s.AnimTimer = ram[b + 0x30];
            s.ScreenY = ram[b + 0x40];
            s.ScreenX = ram[b + 0x50];
            s.Behavior = ram[b + 0xE0];
            s.X = (ushort)(ram[b + 0xF0] | (ram[b + 0x100] << 8));
            s.Y = (ushort)(ram[b + 0x110] | (ram[b + 0x120] << 8));
            s.Direction = ram[b + 0x180];
            s.PlacementIndex = ram[b + 0x190];
            byte st = ram[b + 0xD0];
            s.StateLow = (byte)(st & 0x7F);
            s.Active = (st & 0x80) != 0;
        }
        // Seed the relative coords directly (bypass the absolute-copy path):
        // flags bit4 is set in the capture, so UpdateSlotCull recomputes relative
        // positions from scroll — set player scroll fields from RAM.
        player.ScrollX = (ushort)(ram[0xFC] | (ram[0xFD] << 8));
        player.ScrollYRow = ram[0xFB];

        var sb0 = new System.Text.StringBuilder();
        sb0.Append("parity=" + ram[0x95] + " ");
        for (int i = 0; i < 4; i++)
        {
            var s = objects.Slots[i];
            sb0.Append($"s{i}:act={s.Active} flg={s.Flags:X2} anim={s.AnimNumber:X2} fr={s.AnimFrame} scr={s.ScreenX:X2},{s.ScreenY:X2} abs={s.X:X4},{s.Y:X4} ");
        }
        var disp = new Darkwing.Core.OamDispatcher(
            File.ReadAllBytes(root + "/Assets/Darkwing/Data/prg_bank5.bytes"),
            objects, ppu, player);
        // Single-frame probe: compose the shadow from the RAM state seeded above and publish it
        // immediately (the game loop publishes the PREVIOUS walk, which is what the captures pair
        // with the frame index).
        disp.RunFrame(ram[0x95]);
        disp.PublishToOam();
        sb0.Append(" oam4..15=" + ppu.Oam[4].ToString("X2") + ppu.Oam[5].ToString("X2") + ppu.Oam[6].ToString("X2") + ppu.Oam[7].ToString("X2") + ppu.Oam[8].ToString("X2"));
        return sb0.ToString() + "|" + resultJson(ppu, oam);

        int match = 0, diff = 0;
        return resultJson(ppu, oam);
    }

    private static string resultJson(Darkwing.Core.Ppu ppu, byte[] oam)
    {
        int match = 0;
        var sb = new System.Text.StringBuilder();
        for (int i = 0; i < 256; i++)
        {
            if (ppu.Oam[i] == oam[i]) { match++; continue; }
            if (sb.Length < 2400)
                sb.Append($" b{i}:port={ppu.Oam[i]:X2}/orig={oam[i]:X2}");
        }
        return $"OAM match {match}/256 diff {256-match}. Diffs:" + sb;
    }
}
