using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using Darkwing.Core;

public static class ResetNmiVerify
{
    private const string Root = "D:/Art/SW Code Zero Unity/";
    private const BindingFlags PrivateFields = BindingFlags.Instance | BindingFlags.NonPublic;
    private static readonly int[] AudioAddresses =
    {
        0xC0,0xC7,0xC8,0xC9,0xCA,0xCB,0xCC,0xCD,0xCE,0xCF,0xD0,0xD1,0xD2,0xD3,0xD4,0xD5,0xD6,0xD7,0xD8
    };

    private sealed class HardwareWrite
    {
        public int Poll, Address;
        public long Cycle;
        public ushort Pc;
        public byte Value;
    }

    private static byte[] Data(string name) => File.ReadAllBytes(Root + "Assets/Darkwing/Data/" + name + ".bytes");

    private static FieldInfo Field(object target, string name) => target.GetType().GetField(name, PrivateFields)
        ?? throw new MissingFieldException(target.GetType().FullName, name);

    private static DarkwingGame Create()
    {
        var data = DataLoader.Load(Data("chr_full"), Data("levelsets"), Data("screens"), Data("levels"), Data("anims"));
        return new DarkwingGame(data)
        {
            Bank5 = Data("prg_bank5"), Bank7 = Data("world_bank7"), Bank3 = Data("audio_bank3"),
            Bank6 = Data("prg_bank6"), Bank2 = Data("prg_bank2"), Bank4 = Data("prg_bank4"),
            ObjectTypeTable = Data("object_types"), MapNametable = Data("map_nametable"), MapPalette = Data("map_palette"),
            MapChrBank0 = WorldState.MapSpriteChrBank, MapChrBank1 = WorldState.MapChrBank
        };
    }

    private static void Require(bool condition, string label)
    {
        if (!condition) throw new InvalidOperationException("Reset/NMI " + label);
    }

    public static string CopyrightRequest()
    {
        if (UnityEditor.EditorApplication.isCompiling || UnityEditor.EditorUtility.scriptCompilationFailed)
            throw new InvalidOperationException("Refuse stale Core");
        byte[] source = File.ReadAllBytes(Root + "Temp/poweron_resident_source/buffers.bin");
        string[] banks = File.ReadAllLines(Root + "Temp/poweron_resident_source/chr_map.csv");
        Require(source.Length == 263 * 4384 && banks.Length == 264, "Copyright source completeness");
        var game = Create();
        var decoder = new RoomDecoder(Data("prg_bank0"), Data("prg_bank1"), Data("world_bank7"), Data("levelsets"));
        var flow = new GameFlow(game, decoder);
        byte[] input = File.ReadAllBytes(Root + "Playthroughs/20261022T164819Z/input.bin");
        int fields = 0, negatives = 0;
        bool queued = false, published = false;
        for (int poll = 1; poll <= 27; poll++)
        {
            if (poll >= 8)
            {
                int offset = (poll - 1) * 4384;
                string[] mapping = banks[poll].Split(',');
                Require(int.Parse(mapping[0], CultureInfo.InvariantCulture) == poll, "Copyright bank poll");
                byte[] actual = { game.PaletteUploadFlag, game.Player.ChrLatch0, game.Player.ChrLatch1, (byte)game.Ppu.Chr0Bank, (byte)game.Ppu.Chr1Bank };
                byte[] expected = { source[offset + 0x18], source[offset + 0x1A], source[offset + 0x1B], byte.Parse(mapping[2], CultureInfo.InvariantCulture), byte.Parse(mapping[3], CultureInfo.InvariantCulture) };
                Equal(actual, expected, "Copyright requests and hardware poll " + poll);
                fields += actual.Length;
                for (int index = 0; index < actual.Length; index++)
                {
                    byte[] changed = (byte[])actual.Clone();
                    changed[index] ^= 1;
                    Reject(() => Equal(changed, expected, "Changed Copyright request/hardware"), "Copyright changed field");
                    negatives++;
                }
                if (game.Player.ChrLatch1 != 0)
                {
                    Require(game.Player.ChrLatch1 == 0x99 && game.Ppu.Chr1Bank == 0x10, "Copyright request is not an immediate bank write");
                    queued = true;
                }
                if (queued && game.Player.ChrLatch1 == 0 && game.Ppu.Chr1Bank == 0x19) published = true;
            }
            game.AdvanceFrame(NesButtons.FromByte(input[poll - 1]));
        }
        Require(queued && published, "Copyright queued then published coverage");
        return $"PASS Copyright actual reset-input polls8..27: request18/1A/1B plus hardwareCHR0/1={fields} fields; {negatives} mismatch controls; deferred99 request then NMI25-visible bank19. No source-state seed; not RGB or continuous clock acceptance.";
    }

    private static void Equal(byte[] actual, byte[] expected, string label)
    {
        Require(actual.Length == expected.Length, label + " length");
        for (int index = 0; index < actual.Length; index++)
            Require(actual[index] == expected[index], label + " byte " + index);
    }

    private static void Reject(Action action, string label)
    {
        bool refused = false;
        try { action(); }
        catch (InvalidDataException) { refused = true; }
        catch (InvalidOperationException) { refused = true; }
        if (!refused) throw new InvalidOperationException("Reset/NMI comparator accepted " + label);
    }

    private static void Shape(int bytes)
    {
        if (bytes != 8 * 4384) throw new InvalidDataException("Reset source must contain exactly eight complete records");
    }

    private static byte[] Audio(DarkwingGame game)
    {
        var result = new byte[158];
        for (int index = 0; index < AudioAddresses.Length; index++)
            result[index] = (byte)Field(game.Audio, "_" + AudioAddresses[index].ToString("x2")).GetValue(game.Audio);
        int cursor = AudioAddresses.Length;
        result[cursor++] = (byte)Field(game, "_audioLastId").GetValue(game);
        result[cursor++] = Convert.ToByte(Field(game, "_audioWrite").GetValue(game));
        result[cursor++] = Convert.ToByte(Field(game, "_audioRead").GetValue(game));
        Array.Copy((byte[])Field(game, "_audioQueue").GetValue(game), 0, result, cursor, 8);
        Array.Copy((byte[])Field(game.Audio, "_p7").GetValue(game.Audio), 0, result, cursor + 8, 128);
        return result;
    }

    private static byte[] SourceAudio(byte[] records, int offset)
    {
        var result = new byte[158];
        for (int index = 0; index < AudioAddresses.Length; index++) result[index] = records[offset + AudioAddresses[index]];
        int cursor = AudioAddresses.Length;
        Array.Copy(records, offset + 0xD9, result, cursor, 11);
        Array.Copy(records, offset + 0x700, result, cursor + 11, 128);
        return result;
    }

    private static byte[] PpuSnapshot(Ppu ppu)
    {
        using (var stream = new MemoryStream())
        using (var writer = new BinaryWriter(stream))
        {
            writer.Write(ppu.Ctrl); writer.Write(ppu.Mask); writer.Write(ppu.ScrollX); writer.Write(ppu.ScrollY);
            writer.Write(ppu.NametableX); writer.Write(ppu.VramAddr); writer.Write(ppu.Chr0Bank); writer.Write(ppu.Chr1Bank);
            writer.Write(ppu.BgPatternHigh); writer.Write(ppu.SpritePatternHigh);
            writer.Write(ppu.NametableRam); writer.Write(ppu.PaletteRam); writer.Write(ppu.Oam);
            return stream.ToArray();
        }
    }

    private static List<HardwareWrite> Hardware(string folder)
    {
        var result = new List<HardwareWrite>();
        foreach (string line in File.ReadLines(folder + "ppu_writes.csv"))
        {
            string[] cells = line.Split(',');
            if (cells.Length != 7) throw new InvalidDataException("Reset bus schema mismatch");
            result.Add(new HardwareWrite
            {
                Poll = int.Parse(cells[0], CultureInfo.InvariantCulture), Cycle = long.Parse(cells[1], CultureInfo.InvariantCulture),
                Pc = ushort.Parse(cells[2], NumberStyles.HexNumber, CultureInfo.InvariantCulture),
                Address = int.Parse(cells[3], NumberStyles.HexNumber, CultureInfo.InvariantCulture),
                Value = byte.Parse(cells[4], NumberStyles.HexNumber, CultureInfo.InvariantCulture)
            });
        }
        Require(result.Count == 17, "reset bus capture completeness");
        return result;
    }

    private static int[] SourceNmis(string folder)
    {
        var result = new List<int>();
        bool header = true;
        foreach (string line in File.ReadLines(folder + "anchor.csv"))
        {
            if (header)
            {
                if (line != "poll,cycle,pc,a,x,y,sp,scanline,dot") throw new InvalidDataException("Reset anchor schema mismatch");
                header = false;
                continue;
            }
            string[] cells = line.Split(',');
            if (cells.Length != 9) throw new InvalidDataException("Malformed reset anchor");
            if (cells[2] == "C000") result.Add(int.Parse(cells[0], CultureInfo.InvariantCulture));
        }
        Require(result.Count == 3 && result[0] == 6 && result[1] == 7 && result[2] == 8, "first original NMI poll6 and following calls");
        return result.ToArray();
    }

    private static void Controls(byte actualCtrl, byte actualMask, byte actualCounter, byte ctrl, byte mask, byte counter)
    {
        Require(actualCtrl == ctrl && actualMask == mask && actualCounter == counter, "pre-input Ctrl/Mask/NmiCounter");
    }

    private static int DisabledFixture()
    {
        var game = Create();
        game.WorldRunning = false;
        game.ResetMemoryAndVideo();
        game.Ppu.WriteRegister(0x2000, 0);
        Require(game.Ppu.Mask == 0, "synthetic reset keeps hardware mask off");
        for (int index = 0; index < game.Ppu.NametableRam.Length; index++) game.Ppu.NametableRam[index] = (byte)(index ^ 0x5A);
        for (int index = 0; index < 32; index++) game.Ppu.PaletteRam[index] = 0x0F;
        for (int index = 0; index < 256; index++)
        {
            game.Ppu.Oam[index] = (byte)(index ^ 0x69);
            game.Oam.WriteShadowByte(index, (byte)(index ^ 0x96));
        }
        var mirror = (byte[])Field(game, "_palMirror").GetValue(game);
        for (int index = 0; index < mirror.Length; index++) mirror[index] = 0x12;
        Field(game, "_palDirty").SetValue(game, (byte)0xFF);
        game.Ppu.Chr0Bank = 7; game.Ppu.Chr1Bank = 9;
        game.Player.ChrLatch0 = 0x83; game.Player.ChrLatch1 = 0x85;
        game.Ppu.SetScroll(11, 13);
        game.Player.ScrollX = 0x131; game.Player.ScrollY = 47;
        game.NativeVram.TextTile(0x2100, 0x5A);
        game.PlaySound(0xF0);
        byte[] audio = game.Audio.SaveState();
        byte[] mapped = Audio(game);
        byte[] ppu = PpuSnapshot(game.Ppu);
        int disabled = 0;
        for (int attempt = 0; attempt < 3; attempt++)
        {
            game.AdvanceFrame(default);
            Equal(game.Audio.SaveState(), audio, "synthetic disabled sequencer state");
            Equal(Audio(game), mapped, "synthetic disabled audio ring");
            Equal(PpuSnapshot(game.Ppu), ppu, "synthetic disabled PPU publication");
            Require(game.NmiCounter == 0 && game.LastNmiAudioServiceCycles == 0 && game.NmiAudioTimingSupported,
                "synthetic disabled NMI does not run service");
            Require(game.NativeVram.Pending == 0xFF && game.Player.ChrLatch0 == 0x83 && game.Player.ChrLatch1 == 0x85
                && game.PaletteUploadFlag == 0xFF, "synthetic pending publications survive disabled NMI");
            disabled++;
        }
        game.Ppu.WriteRegister(0x2000, 0x90);
        game.AdvanceFrame(default);
        Require(game.NmiCounter == 1 && game.LastNmiAudioServiceCycles > 0 && game.NmiAudioTimingSupported,
            "synthetic re-enable runs exactly one audio service");
        Require(game.NativeVram.Pending == 0 && game.Player.ChrLatch0 == 0 && game.Player.ChrLatch1 == 0
            && game.PaletteUploadFlag == 0 && game.Ppu.Mask == 0x1E && game.Ppu.Chr0Bank == 3 && game.Ppu.Chr1Bank == 5,
            "synthetic re-enable consumes publication owners");
        Require(game.Ppu.ScrollX == 0x31 && game.Ppu.ScrollY == 47 && game.Ppu.NametableX == 1,
            "synthetic re-enable publishes scroll");
        for (int index = 0; index < 256; index++) Require(game.Ppu.Oam[index] == (byte)(index ^ 0x96), "synthetic OAM published");
        for (int index = 0; index < 32; index++) Require(game.Ppu.PaletteRam[index] == 0x12, "synthetic palette published");
        Require(Convert.ToInt32(Field(game, "_audioRead").GetValue(game)) == 1
            && Convert.ToInt32(Field(game, "_audioWrite").GetValue(game)) == 1, "synthetic queued F0 consumed once");
        var queue = (byte[])Field(game, "_audioQueue").GetValue(game);
        Require(queue[0] == 0x88, "synthetic first audio ring cell cleared");
        game.Ppu.WriteRegister(0x2000, 0);
        game.PlaySfx(0x10);
        audio = game.Audio.SaveState(); mapped = Audio(game); ppu = PpuSnapshot(game.Ppu);
        game.AdvanceFrame(default);
        Equal(game.Audio.SaveState(), audio, "synthetic second disabled sequencer state");
        Equal(Audio(game), mapped, "synthetic second disabled ring");
        Equal(PpuSnapshot(game.Ppu), ppu, "synthetic second disabled publication");
        Require(game.NmiCounter == 1 && queue[1] == 0x10 && game.LastNmiAudioServiceCycles == 0, "synthetic suspended SFX remains queued");
        game.Ppu.WriteRegister(0x2000, 0x90);
        game.AdvanceFrame(default);
        Require(game.NmiCounter == 2 && queue[1] == 0x88
            && Convert.ToInt32(Field(game, "_audioRead").GetValue(game)) == 2
            && Convert.ToInt32(Field(game, "_audioWrite").GetValue(game)) == 2
            && game.LastNmiAudioServiceCycles > 0 && game.NmiAudioTimingSupported,
            "synthetic re-enable continues queue without reset");
        return disabled + 1;
    }

    public static string Run()
    {
        if (UnityEditor.EditorApplication.isCompiling || UnityEditor.EditorUtility.scriptCompilationFailed)
            throw new InvalidOperationException("Refuse stale Core");
        string first = Root + "Temp/reset_anchor_source1/", second = Root + "Temp/reset_anchor_source2/";
        byte[] records = File.ReadAllBytes(first + "buffers.bin");
        byte[] replay = File.ReadAllBytes(second + "buffers.bin");
        Shape(records.Length); Shape(replay.Length);
        Equal(File.ReadAllBytes(first + "anchor.csv"), File.ReadAllBytes(second + "anchor.csv"), "independent original anchor bytes");
        Equal(File.ReadAllBytes(first + "ppu_writes.csv"), File.ReadAllBytes(second + "ppu_writes.csv"), "independent original bus bytes");
        Equal(File.ReadAllBytes(first + "status_reads.csv"), File.ReadAllBytes(second + "status_reads.csv"), "independent original status reads");
        var writes = Hardware(first);
        int[] nmis = SourceNmis(first);
        bool firstVisibleMask = false;
        foreach (var write in writes)
        {
            if (write.Address != 0x2001 || write.Value == 0) continue;
            Require(write.Poll == 6 && write.Pc == 0xC095 && write.Value == 0x1E, "original first hardware mask on at C095");
            firstVisibleMask = true;
            break;
        }
        Require(firstVisibleMask, "original hardware mask enable exists");
        byte[] input = File.ReadAllBytes(Root + "Playthroughs/20261022T164819Z/input.bin");
        using (var algorithm = SHA256.Create())
            Require(input.Length == 38886 && BitConverter.ToString(algorithm.ComputeHash(input)).Replace("-", "").ToLowerInvariant()
                == "137190299728c9ea6285ebf057fa25b1efaf916b9aeb2ad6d21fa46efb7f6275", "unchanged native input identity");
        var game = Create();
        var flow = new GameFlow(game, new RoomDecoder(Data("prg_bank0"), Data("prg_bank1"), Data("world_bank7"), Data("levelsets")));
        int audioBytes = 0, controls = 0, negatives = 0;
        byte ctrl = 0, mask = 0;
        for (int poll = 1; poll <= 8; poll++)
        {
            foreach (var write in writes)
            {
                if (write.Poll >= poll) break;
                if (write.Address == 0x2000) ctrl = write.Value;
                else if (write.Address == 0x2001) mask = write.Value;
            }
            byte count = 0;
            foreach (int nmi in nmis) if (nmi < poll) count++;
            Controls(game.Ppu.Ctrl, game.Ppu.Mask, game.NmiCounter, ctrl, mask, count);
            controls += 3;
            Reject(() => Controls((byte)(ctrl ^ 0x80), mask, count, ctrl, mask, count), "Ctrl mismatch");
            Reject(() => Controls(ctrl, (byte)(mask ^ 0x08), count, ctrl, mask, count), "Mask mismatch");
            Reject(() => Controls(ctrl, mask, (byte)(count + 1), ctrl, mask, count), "NMI count mismatch");
            negatives += 3;
            if (poll >= 6)
            {
                int offset = (poll - 1) * 4384;
                var expected = SourceAudio(records, offset);
                Equal(expected, SourceAudio(replay, offset), "independent original selected audio");
                var actual = Audio(game);
                Equal(actual, expected, "cold pre-input persistent audio " + poll);
                Require(records[offset + 0x92] == game.NmiCounter && replay[offset + 0x92] == game.NmiCounter,
                    "post-RAM-clear original92 counter");
                audioBytes += actual.Length;
                if (poll == 6)
                    for (int index = 0; index < actual.Length; index++)
                    {
                        var broken = (byte[])actual.Clone();
                        broken[index] ^= 1;
                        Reject(() => Equal(broken, expected, "deliberate audio mismatch"), "mapped audio mutation");
                        negatives++;
                    }
            }
            game.AdvanceFrame(NesButtons.FromByte(input[poll - 1]));
            Require(game.Frame == poll, "native physical frame callback advances");
            if (poll <= 5) Require(game.LastNmiAudioServiceCycles == 0 && game.NmiAudioTimingSupported, "cold disabled service cost0");
        }
        Require(game.NmiCounter == 3, "eighth callback includes third enabled service");
        Reject(() => Shape(records.Length - 1), "missing source byte");
        Reject(() => Shape(records.Length + 1), "extra source byte");
        negatives += 2;
        int disabled = DisabledFixture();
        Require(controls == 24 && audioBytes == 474 && disabled == 4, "focused coverage completeness");
        return $"PASS cold reset eight pre-input callbacks: Ctrl/Mask/NmiCounter={controls}; selected persistent audio poll6..8={audioBytes} bytes matching two original captures; negatives={negatives}. Synthetic disabled/re-enabled fixture: disabledCalls={disabled}, enabledCalls=2; queued F0/SFX preserved then drained without reset, PPU/CHR/palette/VRAM/OAM owners gated. Audio fields: C0,C7-D8,D9-E3,0700-077F only; no pre-clear RAM1..4 or audio1..5 equality claim. Synthetic fixture is not original-frame equality. Hardware-ownership prerequisite only, not typed RESET or CPU/PPU phase anchor.";
    }
}
