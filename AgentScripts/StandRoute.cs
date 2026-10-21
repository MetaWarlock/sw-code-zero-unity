using System;
using System.Collections.Generic;
using System.IO;
using Darkwing.Core;
using Darkwing.UnityApp;
using UnityEngine;

/// <summary>
/// Headless port runner for a recorded playthrough pad track. Mirrors DarkwingLauncher's Awake
/// wiring and AdvanceFrame order exactly, but emits one fingerprint row per frame in the same
/// convention as Tools/DarkwingResearch/stand/stand.lua.in, so stand.py diff can compare the
/// original and the port frame by frame. Tooling only: nothing here runs in the game build.
/// </summary>
public static class StandRoute
{
    const string Root = "D:/Art/SW Code Zero Unity";
    static byte[] Data(string name) => File.ReadAllBytes(Root + "/Assets/Darkwing/Data/" + name + ".bytes");

    [Serializable]
    sealed class Config
    {
        public string spec;      // ReplaySpec JSON written by stand.py (frames/inputs/output)
        public string session;   // playthrough session holding input.bin
        public int fullEvery;    // emit nt/oam checksum lines every N frames (Lua side: same)
        public int[] shots;      // frames whose framebuffer is dumped as PNG
    }

    public static string Run()
    {
        if (UnityEditor.EditorUtility.scriptCompilationFailed || UnityEditor.EditorApplication.isCompiling)
            throw new InvalidOperationException("Project compilation has not succeeded; refuse a stale Core assembly.");
        var cfg = JsonUtility.FromJson<Config>(File.ReadAllText(Root + "/Temp/stand_spec.json"));
        var spec = JsonUtility.FromJson<DarkwingLauncher.ReplaySpec>(File.ReadAllText(cfg.spec));
        var track = File.ReadAllBytes(Path.Combine(cfg.session, "input.bin"));
        int frames = Math.Min(spec.frames, track.Length);
        var outDir = spec.output;
        Directory.CreateDirectory(outDir);

        var data = DataLoader.Load(Data("chr_full"), Data("levelsets"), Data("screens"),
            Data("levels"), Data("anims"));
        var decoder = new RoomDecoder(Data("prg_bank0"), Data("prg_bank1"),
            Data("world_bank7"), Data("levelsets"));
        var game = new DarkwingGame(data)
        {
            Bank5 = Data("prg_bank5"),
            Bank7 = Data("world_bank7"),
            Bank3 = Data("audio_bank3"),
            Bank6 = Data("prg_bank6"),
            Bank2 = Data("prg_bank2"),
            Bank4 = Data("prg_bank4"),
            ObjectTypeTable = Data("object_types"),
            MapNametable = Data("map_nametable"),
            MapPalette = Data("map_palette"),
            MapChrBank0 = WorldState.MapSpriteChrBank,
            MapChrBank1 = WorldState.MapChrBank,
        };
        game.Ppu.WriteRegister(0x2000, 0x90);
        game.Ppu.WriteRegister(0x2001, 0x1E);
        var flow = new GameFlow(game, decoder);

        var sb = new System.Text.StringBuilder();
        sb.Append("frame,x,y,state,ctrl,mask,pal_sum,phase,phaseFrames,timer,parity,stream,textByte,scrollX,scrollY,cell,next,world,completed,maskCompleted,chr0,chr1,blank,flag18,mapScr,mapCh,worldRun\n");
        using var buffers = new BinaryWriter(File.Create(Path.Combine(outDir,"buffers.bin")));
        var ram = new byte[2048]; // Only explicitly mapped fields below; other RAM is not claimed.
        var shots = new HashSet<int>(cfg.shots ?? new int[0]);
        try
        {
        for (int f = 1; f <= frames; f++)
        {
            // Row and shot describe the console at the START of frame f, which is where the
            // emulator's input poll (and therefore the recorded byte) belongs.
            var p = game.Player;
            sb.Append($"{f},{p.X},{p.Y},{p.State},{game.Ppu.Ctrl},{game.Ppu.Mask}," +
                $"{Sum(game.Ppu.PaletteRam, 32)},{flow.Stage},{flow.PhaseFrames},{p.StateTimer},{game.FrameParity},{flow.World.StreamId},{game.Text.ByteIndex},{p.ScrollX},{p.ScrollY},{p.CellIndex},{p.CellIndexNext},{flow.World.CurrentWorld},{flow.World.CompletedCount},{flow.World.CompletedMask},{game.Ppu.Chr0Bank},{game.Ppu.Chr1Bank},{game.BlankDepth},{game.PaletteUploadFlag},{game.MapScreen},{game.MapChooser},{game.WorldRunning}\n");
            ram[0x50]=p.State;ram[0x51]=p.StateTimer;ram[0x95]=game.FrameParity;
            // $C162 latches $14/$16 at the input poll, so the frame's own poll cannot see its own
            // byte: record f (written before Step(f)) carries the read made during frame f-1.
            // Frames that return before $C162 (loader, reload, entry freeze) leave both stale.
            ram[0x14]=game.PressedP1;ram[0x16]=game.HeldP1;
            ram[0x52]=p.DirBits;ram[0x56]=p.AirSubState;ram[0x7C]=p.InvulnFrames;
            ram[0x53]=p.WeaponActive;ram[0x54]=p.WeaponSelected;ram[0x5F]=p.GasActive;
            ram[0x60]=p.GasEnergyHi;ram[0x61]=p.GasEnergyLo;ram[0x98]=p.GasToggle;
            ram[0x3B]=p.IntroHoldFlag;ram[0xED]=BossBehaviors.InvulnTimer;ram[0xEE]=BossBehaviors.DamageAccum;
            ram[0xEF]=(byte)BossBehaviors.ProjectileSlot;
            ram[0x31]=flow.World.CurrentWorld;ram[0x38]=flow.World.CompletedCount;ram[0x69]=flow.World.CompletedMask;
            ram[0x6B]=p.ScoreLifeState;ram[0xFC]=(byte)p.ScrollX;ram[0xFD]=(byte)(p.ScrollX>>8);
            ram[0xFA]=p.ScrollY;ram[0xFB]=p.ScrollYRow;
            ram[0xE4]=EnemyBehaviors.RngE4;ram[0xE5]=EnemyBehaviors.RngE5;
            ram[0xE6]=EnemyBehaviors.RngE6;ram[0xE7]=EnemyBehaviors.RngE7;
            ram[0x25]=p.StageCursor;ram[0x22]=p.CellIndex;ram[0x23]=p.CellIndexNext;
            // The port never maintains the band driver's bookkeeping, so these mirror 0 while
            // ORIG holds $1F=1 and $28=$FC every ordinary frame. None is in a compared group.
            ram[0x1F]=p.ColsToStream;ram[0x28]=p.LastRedrawScroll;ram[0x20]=p.PrevScrollMode;
            ram[0x2B]=p.ScrollState;ram[0x2F]=p.ScrollMode;
            // $6D armed from the raw cell script byte at $DB27 (and $EBAA/$EBC8 for door
            // type 5), $5B is the door request $CA6E consumes. Both sit in $0000..$07FF but
            // in no compared state group, so the mirror is the only way to see them.
            ram[0x6D]=p.CheckpointId;ram[0x5B]=p.PendingEvent;
            for(int i=0;i<16;i++)
            {
                var s=game.Objects.Slots[i]; ushort x=i==0?p.X:s.X,y=i==0?p.Y:s.Y;
                ram[0x400+i]=i==0?p.Flags:s.Flags;
                ram[0x410+i]=s.AnimFrame;ram[0x420+i]=s.AnimNumber;ram[0x430+i]=s.AnimTimer;
                ram[0x440+i]=s.ScreenY;ram[0x450+i]=s.ScreenX;
                ram[0x460+i]=i==0?p.YFrac:s.YFrac;ram[0x470+i]=i==0?p.XFrac:s.XFrac;
                ram[0x480+i]=s.T480;ram[0x490+i]=s.T490;ram[0x4A0+i]=s.T4A0;
                ram[0x4B0+i]=s.T4B0;ram[0x4C0+i]=s.HitFlash;
                ram[0x4D0+i]=(byte)((s.Active?0x80:0)|(s.StateLow&0x7F));ram[0x4E0+i]=s.Behavior;
                ram[0x4F0+i]=(byte)x;ram[0x500+i]=(byte)(x>>8);ram[0x510+i]=(byte)y;ram[0x520+i]=(byte)(y>>8);
                ram[0x530+i]=i==0?p.XVelFrac:s.XVelFrac;ram[0x540+i]=i==0?p.XVelInt:s.XVelInt;
                ram[0x550+i]=i==0?p.YVelFrac:s.YVelFrac;ram[0x560+i]=unchecked((byte)(i==0?p.YVelInt:s.YVelInt));
                ram[0x570+i]=s.BehaviorParam;ram[0x580+i]=s.Direction;ram[0x590+i]=s.PlacementIndex;ram[0x5A0+i]=s.Health;
            }
            Array.Copy(game.Hud.Param,0,ram,0x5B0,game.Hud.Param.Length);
            for(int i=0;i<5;i++)ram[0x5C4+i*2]=p.ScoreDigits[i];
            ram[0x5E4]=p.EnergyColLeft;ram[0x5E6]=p.EnergyColRight;
            buffers.Write(ram);buffers.Write(game.Ppu.NametableRam,0,2048);
            buffers.Write(game.Ppu.Oam);buffers.Write(game.Ppu.PaletteRam);
            if (f % Math.Max(1, cfg.fullEvery) == 0)
                sb.Append($"# full {f} nt={Sum(game.Ppu.NametableRam, 0x800)} oam={Sum(game.Ppu.Oam, 256)}\n");
            if (shots.Contains(f)) DumpFramebuffer(game, Path.Combine(outDir, $"frame_{f}.png"));
            var pad = NesButtons.FromByte(track[f - 1]);
            flow.Step(in pad);
            game.Step(in pad);
            if (f % 1000 == 0) File.WriteAllText(Path.Combine(outDir,"trace.csv"),sb.ToString());
        }
        }
        finally { File.WriteAllText(Path.Combine(outDir, "trace.csv"), sb.ToString()); }
        return $"port frames={frames} track={track.Length} specRanges={spec.inputs?.Length ?? 0} " +
            $"shots={shots.Count} out={outDir}/trace.csv";
    }

    static int Sum(byte[] bytes, int count)
    {
        int s = 0;
        for (int i = 0; i < count; i++) s += bytes[i];
        return s;
    }

    /// <summary>Exact 256x240 Core framebuffer, NES palette, no scaling or HUD.</summary>
    static void DumpFramebuffer(DarkwingGame game, string path)
    {
        var tex = new Texture2D(DarkwingGame.ScreenWidth, DarkwingGame.ScreenHeight,
            TextureFormat.RGBA32, false);
        var px = new Color32[DarkwingGame.ScreenWidth * DarkwingGame.ScreenHeight];
        var fb = game.Framebuffer;
        for (int y = 0; y < DarkwingGame.ScreenHeight; y++)
        {
            int src = y * DarkwingGame.ScreenWidth;
            int dst = (DarkwingGame.ScreenHeight - 1 - y) * DarkwingGame.ScreenWidth;
            for (int x = 0; x < DarkwingGame.ScreenWidth; x++)
                px[dst + x] = NesPalette.Table[fb[src + x] & 0x3F];
        }
        tex.SetPixels32(px);
        tex.Apply(false);
        File.WriteAllBytes(path, ImageConversion.EncodeToPNG(tex));
        UnityEngine.Object.DestroyImmediate(tex);
    }
}
