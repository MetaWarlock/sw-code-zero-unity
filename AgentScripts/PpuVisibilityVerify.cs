using System;
using System.Collections.Generic;
using System.IO;
using Darkwing.Core;

public static class PpuVisibilityVerify
{
    public static string PauseAndDeath()
    {
        const string root = "D:/Art/SW Code Zero Unity/";
        byte[] Data(string name) => File.ReadAllBytes(root + "Assets/Darkwing/Data/" + name + ".bytes");
        var data = DataLoader.Load(Data("chr_full"), Data("levelsets"), Data("screens"), Data("levels"), Data("anims"));
        const System.Reflection.BindingFlags binding = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
        var enter = typeof(DarkwingGame).GetMethod("PauseEnter", binding);
        var restore = typeof(DarkwingGame).GetMethod("PauseRestore", binding);
        var nmi = typeof(DarkwingGame).GetMethod("BeginNmi", binding);
        int checks = 0;
        foreach (byte bank in new byte[] { 0, 14, 31 })
        {
            var game = new DarkwingGame(data) { Bank5 = Data("prg_bank5"), Bank7 = Data("world_bank7"), Bank4 = Data("prg_bank4") };
            game.Ppu.Mask = 0x1E;
            game.Ppu.Chr0Bank = bank;
            enter.Invoke(game, null);
            if (game.Player.ChrLatch0 != 0x9F || game.Ppu.Chr0Bank != bank || game.Ppu.Mask != 0x1E)
                throw new Exception("Pause entry must request CHR without applying it early");
            nmi.Invoke(game, null);
            if (game.Ppu.Chr0Bank != 31 || game.Player.ChrLatch0 != 0 || game.Ppu.Mask != 0x16) throw new Exception("Sprite-only pause NMI must consume CHR and mask requests");
            restore.Invoke(game, null);
            if (game.Player.ChrLatch0 != (bank | 0x80) || game.Ppu.Chr0Bank != 31 || game.Ppu.Mask != 0x16)
                throw new Exception("Pause restore must request saved applied CHR");
            game.Player.ScrollX = 0x134;
            game.Player.ScrollY = 0x58;
            nmi.Invoke(game, null);
            if (game.Ppu.Chr0Bank != bank || game.Player.ChrLatch0 != 0 || game.Ppu.Mask != 0x1E) throw new Exception("Restored CHR and mask must apply at next NMI");
            if (game.Ppu.ScrollX != 0x34 || game.Ppu.ScrollY != 0x58 || game.Ppu.NametableX != 1)
                throw new Exception("NMI must publish resident scroll without a player/world pass");
            checks += 4;
            checks++;
            for (int index = 0; index < 256; index++) game.Oam.WriteShadowByte(index, (byte)(index ^ 0x55));
            game.Player.DeathFlag = game.Player.CheckpointId = 0;
            game.Player.ResolveDeath();
            for (int index = 0; index < 256; index++)
            {
                byte expected = index % 4 == 0 ? (byte)0xF8 : (byte)(index ^ 0x55);
                if (game.Oam.ReadShadowByte(index) != expected) throw new Exception("Death D423-C21E must hide Y only");
                checks++;
            }
            nmi.Invoke(game, null);
            for (int index = 0; index < 256; index++)
            {
                if (game.Ppu.Oam[index] != game.Oam.ReadShadowByte(index)) throw new Exception("Resident death NMI must publish shadow without a walk");
                checks++;
            }
            game.RenderOff();
            game.Player.ScrollX = 0x71;
            game.Oam.WriteShadowByte(0, 1);
            nmi.Invoke(game, null);
            if (game.Ppu.Oam[0] != 0xF8) throw new Exception("Blank NMI must retain old OAM");
            if (game.Ppu.ScrollX != 0x34 || game.Ppu.NametableX != 1) throw new Exception("Blank NMI must retain old scroll");
            game.RenderOn();
            nmi.Invoke(game, null);
            if (game.Ppu.Oam[0] != 1) throw new Exception("Render-on NMI must publish pending OAM");
            if (game.Ppu.ScrollX != 0x71 || game.Ppu.NametableX != 0) throw new Exception("Render-on NMI must publish pending scroll");
            checks += 4;
        }
        return $"PASS {checks} pause CHR requests/restore, death Y-only clear, resident NMI DMA and blank retention checks";
    }

    public static string ScenePublication()
    {
        const string root = "D:/Art/SW Code Zero Unity/";
        byte[] Data(string name) => File.ReadAllBytes(root + "Assets/Darkwing/Data/" + name + ".bytes");
        var data = DataLoader.Load(Data("chr_full"), Data("levelsets"), Data("screens"), Data("levels"), Data("anims"));
        var decoder = new RoomDecoder(Data("prg_bank0"), Data("prg_bank1"), Data("world_bank7"), Data("levelsets"));
        int checks = 0, rejected = 0;
        foreach (byte world in new byte[] { 7, 9, 0x0E, 0x0C, 0x0A })
        {
            var game = new DarkwingGame(data) { Bank5 = Data("prg_bank5"), Bank7 = Data("world_bank7"), Bank2 = Data("prg_bank2") };
            game.Ppu.WriteRegister(0x2000, 0x90);
            game.BeginSceneLoad(decoder, world);
            int passes = 0;
            do
            {
                if (++passes > 256) throw new Exception("Scene loader did not reach its streamer yield");
                game.TickSceneLoad();
            } while (game.Player.StageCursor != game.Player.StageTarget);
            byte[] staged = (byte[])game.Ppu.NametableRam.Clone();
            game.TickSceneLoad();
            for (int index = 0; index < staged.Length; index++)
            {
                if (game.Ppu.NametableRam[index] != staged[index]) throw new Exception($"Scene {world:X2} overwrites E698 at {index:X3}");
                checks++;
            }
            game.TileMap.StreamColumns(game.Player.ScrollX);
            bool different = false;
            for (int index = 0; index < staged.Length; index++) different |= game.Ppu.NametableRam[index] != staged[index];
            if (different) rejected++;
        }
        if (rejected != 5) throw new Exception("Scene overwrite negative controls did not distinguish all pseudo-scenes");
        return $"PASS {checks} preserved scene NT bytes, {rejected} whole-window overwrite controls rejected";
    }

    public static string SourcePause()
    {
        const string root = "D:/Art/SW Code Zero Unity/";
        const string reference = root + "Research/DarkwingDuck/reference/gold_pause_m1/";
        byte[] Data(string name) => File.ReadAllBytes(root + "Assets/Darkwing/Data/" + name + ".bytes");
        var data = DataLoader.Load(Data("chr_full"), Data("levelsets"), Data("screens"), Data("levels"), Data("anims"));
        byte[] ram = File.ReadAllBytes(reference + "ram_frames.bin");
        byte[] oam = File.ReadAllBytes(reference + "oam_frames.bin");
        byte[] palette = File.ReadAllBytes(reference + "palette_frames.bin");
        if (ram.Length != 70 * 2048 || oam.Length != 70 * 256 || palette.Length != 70 * 32)
            throw new Exception("Incomplete pause source fixture");
        string output = root + "Temp/ppu_visibility_source";
        Directory.CreateDirectory(output);
        var pixels = new byte[Ppu.W * Ppu.H];
        foreach (int frame in new[] { 30, 40, 50, 60, 70 })
        {
            var ppu = new Ppu(data) { Chr0Bank = 31, Chr1Bank = 16 };
            ppu.WriteRegister(0x2000, ram[(frame - 1) * 2048 + 0xFF]);
            ppu.WriteRegister(0x2001, ram[(frame - 1) * 2048 + 0xFE]);
            if (ppu.Mask != 0x16) throw new Exception("Expected source pause sprite-only mask");
            Array.Copy(oam, (frame - 1) * 256, ppu.Oam, 0, 256);
            Array.Copy(palette, (frame - 1) * 32, ppu.PaletteRam, 0, 32);
            ppu.Render(pixels);
            File.WriteAllBytes(output + "/frame_" + frame + ".bin", pixels);
        }
        return "SOURCE_FIXTURE_EXPORTED 5 sprite-only pause images; compare with original PNGs separately";
    }

    public static string Run()
    {
        var chr = new byte[4096];
        for (int row = 0; row < 8; row++)
        {
            chr[16 + row] = 255;
            chr[32 + 8 + row] = 255;
        }
        var data = new GameData(chr, Array.Empty<byte>(), Array.Empty<byte>(),
            new List<GameData.LevelDef>(), new List<GameData.AnimationDef>(), new List<GameData.FrameDef>());
        var ppu = new Ppu(data) { Chr0Bank = 0, Chr1Bank = 0 };
        for (int index = 0; index < 960; index++) ppu.NametableRam[index] = 1;
        for (int index = 0; index < 64; index++) ppu.Oam[index * 4] = 248;
        ppu.PaletteRam[0] = 0x0F;
        ppu.PaletteRam[1] = 0x21;
        ppu.PaletteRam[18] = 0x30;
        ppu.Oam[0] = ppu.Oam[4] = 15;
        ppu.Oam[1] = ppu.Oam[5] = 2;
        ppu.Oam[7] = 12;
        var pixels = new byte[Ppu.W * Ppu.H];
        int checks = 0;
        for (int layers = 0; layers < 4; layers++)
        {
            for (int left = 0; left < 4; left++)
            {
                ppu.Mask = (byte)((layers << 3) | (left << 1));
                ppu.Render(pixels);
                for (int pixelY = 0; pixelY < Ppu.H; pixelY++)
                {
                    for (int pixelX = 0; pixelX < Ppu.W; pixelX++)
                    {
                        byte expected = (layers & 1) != 0 && (pixelX >= 8 || (left & 1) != 0) ? (byte)0x21 : (byte)0x0F;
                        bool inSprite = pixelY >= 16 && pixelY < 24 && (pixelX < 8 || pixelX >= 12 && pixelX < 20);
                        if ((layers & 2) != 0 && inSprite && (pixelX >= 8 || (left & 2) != 0)) expected = 0x30;
                        if (pixels[pixelY * Ppu.W + pixelX] != expected)
                            throw new Exception($"PPUMASK {ppu.Mask:X2} at {pixelX},{pixelY}: {pixels[pixelY * Ppu.W + pixelX]:X2}/{expected:X2}");
                        checks++;
                    }
                }
            }
        }
        ppu.Mask = 0x1E;
        ppu.PaletteRam[1] = ppu.PaletteRam[0];
        ppu.Oam[2] = ppu.Oam[6] = 0x20;
        ppu.Render(pixels);
        if (pixels[16 * Ppu.W + 12] != 0x0F) throw new Exception("Opaque backdrop-colored tile must cover behind-background sprite");
        ppu.Mask = 0x16;
        ppu.Render(pixels);
        if (pixels[16 * Ppu.W + 12] != 0x30) throw new Exception("Disabled background must not cover behind-background sprite");
        ppu.Mask = 0x1E;
        ppu.Oam[3] = 12;
        ppu.Oam[6] = 0;
        ppu.Render(pixels);
        if (pixels[16 * Ppu.W + 12] != 0x0F) throw new Exception("First opaque sprite must claim pixel even behind background");
        ppu.NametableRam[2 * 32 + 1] = 0;
        ppu.Render(pixels);
        if (pixels[16 * Ppu.W + 12] != 0x30) throw new Exception("Transparent background must reveal the first sprite");
        return $"PASS {checks + 4} layer/left-edge/opacity/priority checks; PPUMASK00/08/10/18 and independent bits1/2";
    }
}
