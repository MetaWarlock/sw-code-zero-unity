using System.Collections.Generic;
using System.IO;

namespace Darkwing.Core
{
    /// <summary>Loads the binary data exported by export_unity.py.</summary>
    public static class DataLoader
    {
        public static GameData Load(byte[] chr, byte[] levelSets, byte[] screens,
                                    byte[] levelsBin, byte[] animsBin)
        {
            var levels = new List<GameData.LevelDef>();
            using (var ms = new MemoryStream(levelsBin))
            using (var r = new BinaryReader(ms))
            {
                int count = r.ReadInt32();
                for (int i = 0; i < count; i++)
                {
                    var lv = new GameData.LevelDef
                    {
                        Id = r.ReadInt32(),
                        Width = r.ReadInt32(),
                        Height = r.ReadInt32(),
                        ObjectCount = r.ReadInt32(),
                        TileSet = r.ReadInt32(),
                        BgChrBank = r.ReadInt32(),
                        FirstScreen = r.ReadInt32(),
                        LayoutOffset = r.ReadInt32(),
                    };
                    lv.Cells = r.ReadBytes(lv.Width * lv.Height);
                    lv.ScrollDoor = r.ReadBytes(lv.Width * lv.Height);
                    lv.Objects = new GameData.Placement[lv.ObjectCount];
                    for (int o = 0; o < lv.ObjectCount; o++)
                    {
                        lv.Objects[o] = new GameData.Placement
                        {
                            Index = o,                      // $0590/$0340 index in the level list
                            ScreenX = r.ReadByte(),
                            X = r.ReadByte(),
                            ScreenY = r.ReadByte(),
                            Y = r.ReadByte(),
                            Type = r.ReadByte(),
                        };
                    }
                    levels.Add(lv);
                }
            }

            var coords = new int[208];
            var frames = new List<GameData.FrameDef>();
            var anims = new List<GameData.AnimationDef>();
            using (var ms = new MemoryStream(animsBin))
            using (var r = new BinaryReader(ms))
            {
                int nCoords = r.ReadInt32();
                for (int i = 0; i < nCoords; i++) coords[i] = r.ReadUInt16();
                int nFrames = r.ReadInt32();
                for (int i = 0; i < nFrames; i++)
                {
                    var f = new GameData.FrameDef
                    {
                        Id = r.ReadUInt16(),
                        Kind = r.ReadByte() == 0 ? "rom" : "ram_dynamic",
                        CoordId = r.ReadUInt16(),
                    };
                    int nParts = r.ReadUInt16();
                    f.Parts = new GameData.FrameDef.Part[nParts];
                    for (int p = 0; p < nParts; p++)
                    {
                        int tile = r.ReadByte(), attr = r.ReadByte(), x = r.ReadByte(), y = r.ReadByte();
                        f.Parts[p] = new GameData.FrameDef.Part
                        {
                            Tile = tile, Attr = attr,
                            X = (sbyte)x, Y = (sbyte)y,
                        };
                    }
                    frames.Add(f);
                }
                int nAnims = r.ReadInt32();
                for (int i = 0; i < nAnims; i++)
                {
                    var a = new GameData.AnimationDef
                    {
                        Id = r.ReadUInt16(),
                        RawHeader = r.ReadByte(),
                        TimerRaw = r.ReadByte(),
                    };
                    int n = r.ReadUInt16();
                    a.Frames = new int[n];
                    for (int k = 0; k < n; k++) a.Frames[k] = r.ReadUInt16();
                    anims.Add(a);
                }
            }

            return new GameData(chr, levelSets, screens, levels, anims, frames);
        }
    }
}
