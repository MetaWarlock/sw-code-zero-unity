using System;
using System.IO;
using Darkwing.Core;
using UnityEngine;

public static class DebugPort
{
    private static string Root => Application.dataPath + "/..";
    private static byte[] Data(string name) => File.ReadAllBytes(Root + "/Assets/Darkwing/Data/" + name + ".bytes");

    public static void Run()
    {
        string session = Path.Combine(Root, "Playthroughs/20261024T183247Z");
        byte[] track = File.ReadAllBytes(Path.Combine(session, "input.bin"));

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

        for (int f = 1; f <= 10095; f++)
        {
            var pad = NesButtons.FromByte(track[f - 1]);
            game.AdvanceFrame(in pad);
            if (f >= 10080 && f <= 10095)
            {
                var p = game.Player;
                string objs = "";
                for (int i = 0; i < 16; i++)
                {
                    var s = game.Objects.Slots[i];
                    if (s.Active)
                    {
                        objs += $"[{i}: beh={s.Behavior:X2} x={s.X} y={s.Y} t480={s.T480} st={s.StateLow:X2} prm={s.BehaviorParam:X2}] ";
                    }
                }
                Debug.Log($"PORT F {f}: P=({p.X}, frac={p.XFrac:X2}, {p.Y}, st={p.State:X2}) {objs}");
            }
        }
    }
}
