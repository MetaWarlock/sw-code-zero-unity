using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using Darkwing.Core;

public static class CardGameVerify
{
    private const string Root = "D:/Art/SW Code Zero Unity/";
    private static readonly int[] ActorFields = { 0x400, 0x410, 0x420, 0x430, 0x440, 0x450, 0x4C0, 0x4D0, 0x570, 0x590 };

    private sealed class SourceCall
    {
        public int Poll, Offset, Cycles;
        public bool Resume;
    }

    private static byte[] Data(string name) => File.ReadAllBytes(Root + "Assets/Darkwing/Data/" + name + ".bytes");

    private static void Shape(int records, int entries, int endings)
    {
        if (records != 240 || entries != records * 2048 || endings != entries)
            throw new InvalidDataException("Incomplete Card Game source records");
    }

    private static int Address(int index) => index < 160 ? ActorFields[index / 16] + index % 16 : 0x200 + index - 160;

    private static byte[] Expected(byte[] source, int offset)
    {
        var result = new byte[416];
        for (int index = 0; index < result.Length; index++) result[index] = source[offset + Address(index)];
        return result;
    }

    private static byte[] Actual(DarkwingGame game)
    {
        var result = new byte[416];
        for (int slot = 0; slot < 16; slot++)
        {
            var actor = game.Objects.Slots[slot];
            byte[] fields =
            {
                actor.Flags, actor.AnimFrame, actor.AnimNumber, actor.AnimTimer, actor.ScreenY, actor.ScreenX,
                actor.HitFlash, (byte)((actor.Active ? 128 : 0) | (actor.StateLow & 127)), actor.BehaviorParam, actor.PlacementIndex
            };
            for (int index = 0; index < fields.Length; index++) result[index * 16 + slot] = fields[index];
        }
        for (int shadow = 0; shadow < 256; shadow++) result[160 + shadow] = game.Oam.ReadShadowByte(shadow);
        return result;
    }

    private static void Compare(byte[] actual, byte[] expected, string label)
    {
        if (actual.Length != 416 || expected.Length != 416) throw new InvalidDataException("Card field shape");
        for (int index = 0; index < actual.Length; index++)
            if (actual[index] != expected[index])
                throw new InvalidOperationException(label + " $" + Address(index).ToString("X4") + " " + actual[index] + "/" + expected[index]);
    }

    private static void Control(byte actual, byte expected, string label)
    {
        if (actual != expected) throw new InvalidOperationException("Card Game " + label + " " + actual + "/" + expected);
    }

    private static void Price(int actual, int expected)
    {
        if (actual != expected) throw new InvalidOperationException("Card Game resume price " + actual + "/" + expected);
    }

    private static void Coverage(HashSet<int> expected, List<int> actual)
    {
        if (actual.Count != expected.Count || new HashSet<int>(actual).Count != actual.Count || !expected.SetEquals(actual))
            throw new InvalidDataException("Missing, extra or duplicate Card Game call");
    }

    private static void Reject(Action action, string label)
    {
        bool refused = false;
        try { action(); }
        catch (InvalidDataException) { refused = true; }
        catch (InvalidOperationException) { refused = true; }
        if (!refused) throw new InvalidOperationException("Card Game comparator accepted " + label);
    }

    public static string Run()
    {
        if (UnityEditor.EditorApplication.isCompiling || UnityEditor.EditorUtility.scriptCompilationFailed)
            throw new InvalidOperationException("Refuse stale Core");
        string source = Root + "Temp/card_pass_source/";
        string[] rows = File.ReadAllLines(source + "calls.csv");
        if (rows.Length < 2 || rows[0] != "poll,kind,start,cycles,frame,timer,cursor,world")
            throw new InvalidDataException("Card source schema mismatch");
        byte[] entries = File.ReadAllBytes(source + "entry.bin");
        byte[] endings = File.ReadAllBytes(source + "end.bin");
        Shape(rows.Length - 1, entries.Length, endings.Length);
        var calls = new Dictionary<int, SourceCall>();
        int firstEntries = 0;
        for (int index = 1; index < rows.Length; index++)
        {
            string[] cells = rows[index].Split(',');
            if (cells.Length != 8 || (cells[1] != "frame" && cells[1] != "resume"))
                throw new InvalidDataException("Malformed Card source call");
            var call = new SourceCall
            {
                Poll = int.Parse(cells[0], CultureInfo.InvariantCulture), Offset = (index - 1) * 2048,
                Cycles = int.Parse(cells[3], CultureInfo.InvariantCulture), Resume = cells[1] == "resume"
            };
            if (calls.ContainsKey(call.Poll)) throw new InvalidDataException("Duplicate Card source call");
            calls.Add(call.Poll, call);
            if (!call.Resume) firstEntries++;
            if (entries[call.Offset + 0x410] != byte.Parse(cells[4], CultureInfo.InvariantCulture)
                || entries[call.Offset + 0x430] != byte.Parse(cells[5], CultureInfo.InvariantCulture)
                || entries[call.Offset + 0x97] != byte.Parse(cells[6], CultureInfo.InvariantCulture)
                || entries[call.Offset + 0x31] != byte.Parse(cells[7], CultureInfo.InvariantCulture)
                || entries[call.Offset + 0x9A] != (call.Resume ? 0 : 1))
                throw new InvalidDataException("Card source entry metadata mismatch");
        }
        if (firstEntries != 3) throw new InvalidDataException("Card source entry coverage mismatch");
        byte[] input = File.ReadAllBytes(Root + "Playthroughs/20261022T164819Z/input.bin");
        using (var algorithm = SHA256.Create())
        {
            string digest = BitConverter.ToString(algorithm.ComputeHash(input)).Replace("-", "").ToLowerInvariant();
            if (input.Length != 38886 || digest != "137190299728c9ea6285ebf057fa25b1efaf916b9aeb2ad6d21fa46efb7f6275")
                throw new InvalidDataException("Unexpected Card native input");
        }
        long capturedBytes = new FileInfo(source + "buffers.bin").Length;
        if (capturedBytes != 14300L * 4384) throw new InvalidDataException("Card source capture completeness");
        int lastPoll = checked((int)(capturedBytes / 4384));
        var data = DataLoader.Load(Data("chr_full"), Data("levelsets"), Data("screens"), Data("levels"), Data("anims"));
        var decoder = new RoomDecoder(Data("prg_bank0"), Data("prg_bank1"), Data("world_bank7"), Data("levelsets"));
        var game = new DarkwingGame(data)
        {
            Bank5 = Data("prg_bank5"), Bank7 = Data("world_bank7"), Bank3 = Data("audio_bank3"),
            Bank6 = Data("prg_bank6"), Bank2 = Data("prg_bank2"), Bank4 = Data("prg_bank4"),
            ObjectTypeTable = Data("object_types"), MapNametable = Data("map_nametable"), MapPalette = Data("map_palette"),
            MapChrBank0 = WorldState.MapSpriteChrBank, MapChrBank1 = WorldState.MapChrBank
        };
        game.Ppu.WriteRegister(0x2000, 0x90);
        game.Ppu.WriteRegister(0x2001, 0x1E);
        var flow = new GameFlow(game, decoder);
        var checkedPolls = new List<int>();
        int endFields = 0, resumeEntryFields = 0, prices = 0, negativeFields = 0, negativePrices = 0, negativeControls = 0;
        for (int poll = 1; poll <= lastPoll; poll++)
        {
            bool expectedCall = calls.TryGetValue(poll, out var call);
            byte beforeWait = game.Player.CardWaitRemaining;
            byte beforeParity = game.FrameParity;
            if (expectedCall)
            {
                Control(beforeParity, entries[call.Offset + 0x95], "entry completed-frame parity");
                if (call.Resume)
                {
                    if (game.Player.CardPass == null) throw new InvalidOperationException("Card resume has no preceding continuation");
                    Compare(Actual(game), Expected(entries, call.Offset), "Card resume entry " + poll);
                    resumeEntryFields += 416;
                    Control(game.Player.CardPass.Bracket, entries[call.Offset + 0x9A], "resume entry bracket");
                    Control((byte)game.Oam.OamCursorBytes, entries[call.Offset + 0x97], "resume entry cursor");
                }
            }
            game.AdvanceFrame(NesButtons.FromByte(input[poll - 1]));
            bool actualCall = beforeWait == 0 && game.Player.IntroCardPhase && !game.Paused;
            if (actualCall != expectedCall) throw new InvalidOperationException("Missing or extra native Card frame " + poll);
            if (!expectedCall) continue;
            var pass = game.Player.CardPass;
            if (pass == null || pass.Active || pass.FramePending || pass.Stage != CardPassContinuation.Phase.Committed
                || pass.ProcessState != FrameScheduler.Waiting || pass.ProcessTimer != 1 || pass.InstructionPc != 0xFF19
                || pass.Unsupported != null || pass.SwitchDepth != 0 || pass.DeferredAudio != 0)
                throw new InvalidOperationException("Actual Card did not commit " + poll);
            byte[] actual = Actual(game);
            byte[] expected = Expected(endings, call.Offset);
            Compare(actual, expected, "Card committed " + poll);
            endFields += actual.Length;
            byte[] controls = { game.FrameParity, pass.Bracket, (byte)game.Oam.OamCursorBytes };
            int[] addresses = { 0x95, 0x9A, 0x97 };
            for (int index = 0; index < controls.Length; index++)
                Control(controls[index], endings[call.Offset + addresses[index]], "committed $" + addresses[index].ToString("X2"));
            bool terminal = pass.WaitCount != 0;
            Control(game.FrameParity, unchecked((byte)(beforeParity + (terminal ? 0 : 1))), "Card engine parity delta");
            if (terminal && (pass.WaitCount != 60 || game.Player.CardWaitRemaining != 60))
                throw new InvalidOperationException("Native Card wait owner mismatch");
            if (call.Resume)
            {
                Price(pass.Cycles, call.Cycles);
                Reject(() => Price(pass.Cycles, call.Cycles + 1), "resume clock mutation");
                prices++;
                negativePrices++;
            }
            if (negativeFields == 0)
            {
                for (int index = 0; index < actual.Length; index++)
                {
                    byte[] broken = (byte[])actual.Clone();
                    broken[index] ^= 1;
                    Reject(() => Compare(broken, expected, "deliberate mismatch"), "mapped field mutation");
                    negativeFields++;
                }
                for (int index = 0; index < controls.Length; index++)
                {
                    byte broken = (byte)(controls[index] ^ 1);
                    byte original = controls[index];
                    Reject(() => Control(broken, original, "deliberate control"), "control mutation");
                    negativeControls++;
                }
            }
            checkedPolls.Add(poll);
        }
        var expectedPolls = new HashSet<int>(calls.Keys);
        Coverage(expectedPolls, checkedPolls);
        var missing = new List<int>(checkedPolls);
        missing.RemoveAt(0);
        Reject(() => Coverage(expectedPolls, missing), "missing actual call");
        var extra = new List<int>(checkedPolls) { lastPoll + 1 };
        Reject(() => Coverage(expectedPolls, extra), "extra actual call");
        Reject(() => Shape(rows.Length - 1, entries.Length - 1, endings.Length), "missing source byte");
        Reject(() => Shape(rows.Length - 1, entries.Length + 1, endings.Length), "extra source byte");
        negativeControls += 4;
        if (checkedPolls.Count != 240 || prices != 237 || endFields != 99840 || resumeEntryFields != 98592)
            throw new InvalidDataException("Incomplete actual Card Game coverage");
        return $"PASS actual reset-input Card calls={checkedPolls.Count}; endMappedBytes={endFields}; resumeEntryMappedBytes={resumeEntryFields}; sourceExactResumePrices={prices}; committed95/9A/97=720; negativeFields={negativeFields}, negativePrices={negativePrices}, negativeControls={negativeControls}. No source RAM/state seeding; 10x16 actor fields plus256shadow only, not whole RAM/RGB/audio. First3 BeginEntry prefixes are not fully priced or source-entry-snapshot compared; parity is checked only at completed boundaries, not a physical-clock anchor. Input prefix1..{lastPoll}; later gameplay and177pureyields/3releases are outside this focused check.";
    }
}
