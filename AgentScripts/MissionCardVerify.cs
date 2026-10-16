using System.IO;

/// <summary>
/// Drives the real port's OamDispatcher.BlitMissionCard against every world's own
/// original-ROM capture and compares the produced $0200 shadow (the mission-card OAM
/// the bank-2 $9636/$967A routine leaves behind) against the captured OAM. This is a
/// content comparison only: no engine tick, since the card is a pure blit.
/// </summary>
public static class MissionCardVerify
{
    public static string Suite()
    {
        string root = "D:/Art/SW Code Zero Unity";
        byte[] bank2 = File.ReadAllBytes(root + "/Assets/Darkwing/Data/prg_bank2.bytes");
        byte[] bank5 = File.ReadAllBytes(root + "/Assets/Darkwing/Data/prg_bank5.bytes");
        var sb = new System.Text.StringBuilder();
        bool all = true;
        for (int w = 0; w <= 6; w++)
        {
            string dir = root + $"/Research/DarkwingDuck/reference/gold_w0{w}";
            if (!File.Exists(dir + "/oam_frames.bin")) { sb.AppendLine($"w{w}: NO CAPTURE"); continue; }
            byte[] oamAll = File.ReadAllBytes(dir + "/oam_frames.bin");
            int frames = oamAll.Length / 256;

            var data = Darkwing.Core.DataLoader.Load(
                File.ReadAllBytes(root + "/Assets/Darkwing/Data/chr_full.bytes"),
                File.ReadAllBytes(root + "/Assets/Darkwing/Data/levelsets.bytes"),
                File.ReadAllBytes(root + "/Assets/Darkwing/Data/screens.bytes"),
                File.ReadAllBytes(root + "/Assets/Darkwing/Data/levels.bytes"),
                File.ReadAllBytes(root + "/Assets/Darkwing/Data/anims.bytes"));
            var ppu = new Darkwing.Core.Ppu(data);
            var player = new Darkwing.Core.PlayerCore(Darkwing.Core.EmptyTileMap.Instance);
            var objects = new Darkwing.Core.ObjectSystem(data);
            var disp = new Darkwing.Core.OamDispatcher(bank5, objects, ppu, player);
            disp.MissionCardHold = true;
            disp.BlitMissionCard(bank2, w);
            disp.PublishToOam();

            int best = -1, bf = -1;
            for (int f = 0; f < frames; f++)
            {
                int m = 0;
                for (int i = 0; i < 0xA0; i++) if (ppu.Oam[i] == oamAll[f * 256 + i]) m++;
                if (m > best) { best = m; bf = f; }
            }
            sb.AppendLine($"w{w}: card OAM match {best}/160 at frame {bf}");
            all &= best == 160;
        }
        sb.AppendLine(all ? "RESULT: ALL WORLDS EXACT" : "RESULT: DIVERGENCE");
        File.WriteAllText(root + "/Temp/mission_card.txt", sb.ToString());
        return sb.ToString();
    }
}
