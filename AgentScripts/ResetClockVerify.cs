using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using Darkwing.Core;

public static class ResetClockVerify
{
    private const string Root = "D:/Art/SW Code Zero Unity/";
    private const int FirstPoll = 6;
    private const int LastCommittedPoll = 280;
    private const int RefusalPoll = 281;
    private const int ExpectedRows = LastCommittedPoll - FirstPoll + 1;

    private sealed class Snapshot
    {
        public int Poll;
        public long Handler, Resume, Body, Finish;
        public ushort Interrupted;
        public byte State, Timer;
        public bool Commits;
        public Snapshot Copy() => (Snapshot)MemberwiseClone();
    }

    private sealed class LoaderSource
    {
        public readonly Dictionary<int, Dictionary<ushort, long>> Cycles = new Dictionary<int, Dictionary<ushort, long>>();
        public readonly Dictionary<int, ushort> Interrupted = new Dictionary<int, ushort>();

        public long At(int poll, ushort pc)
        {
            Dictionary<ushort, long> entries;
            long cycle;
            if (!Cycles.TryGetValue(poll, out entries) || !entries.TryGetValue(pc, out cycle))
                throw new InvalidOperationException("Loader source lacks $" + pc.ToString("X4") + " at poll " + poll);
            return cycle;
        }

        public LoaderSource Copy()
        {
            var copy = new LoaderSource();
            foreach (var pair in Cycles)
            {
                var entries = new Dictionary<ushort, long>();
                foreach (var entry in pair.Value) entries.Add(entry.Key, entry.Value);
                copy.Cycles.Add(pair.Key, entries);
            }
            foreach (var pair in Interrupted) copy.Interrupted.Add(pair.Key, pair.Value);
            return copy;
        }
    }

    private static void Require(bool value, string message)
    {
        if (!value) throw new InvalidOperationException(message);
    }

    private static byte[] Data(string name) => File.ReadAllBytes(Root + "Assets/Darkwing/Data/" + name + ".bytes");

    private static List<Snapshot> RunNative(byte[] input, bool dropCopyrightRequest, out int dropped,
        out ResetProcessClock refusal)
    {
        var data = DataLoader.Load(Data("chr_full"), Data("levelsets"), Data("screens"), Data("levels"), Data("anims"));
        var decoder = new RoomDecoder(Data("prg_bank0"), Data("prg_bank1"), Data("world_bank7"), Data("levelsets"));
        var game = new DarkwingGame(data)
        {
            Bank5 = Data("prg_bank5"), Bank7 = Data("world_bank7"), Bank3 = Data("audio_bank3"),
            Bank6 = Data("prg_bank6"), Bank2 = Data("prg_bank2"), Bank4 = Data("prg_bank4"),
            ObjectTypeTable = Data("object_types"), MapNametable = Data("map_nametable"), MapPalette = Data("map_palette"),
            MapChrBank0 = WorldState.MapSpriteChrBank, MapChrBank1 = WorldState.MapChrBank
        };
        var flow = new GameFlow(game, decoder);
        var rows = new List<Snapshot>();
        dropped = 0;
        refusal = null;
        for (int poll = 1; poll <= RefusalPoll; poll++)
        {
            if (dropCopyrightRequest && game.Player.ChrLatch1 == 0x99)
            {
                game.Player.ChrLatch1 = 0;
                dropped++;
            }
            game.AdvanceFrame(NesButtons.FromByte(input[poll - 1]));
            var clock = flow.ResetClock;
            if (poll == RefusalPoll)
            {
                refusal = clock;
                continue;
            }
            if (clock == null || clock.LastNmiOrdinal == 0) continue;
            Require(clock.Supported, "Actual reset clock unsupported at poll " + poll + ": " + clock.Unsupported);
            bool committed = clock.Stage == ResetProcessClock.Phase.Waiting;
            Require(committed, "Reset resident did not commit at poll " + poll + ": " + clock.Stage + " " + clock.Unsupported);
            rows.Add(new Snapshot
            {
                Poll = poll, Handler = clock.HandlerCycle, Resume = clock.ResumeCycle, Body = clock.BodyStartCycle,
                Finish = clock.CommitCycle, Interrupted = clock.InterruptedPc,
                State = clock.ProcessState, Timer = clock.ProcessTimer, Commits = committed
            });
        }
        Require(flow.ResetClock != null && flow.ResetClock.MinimumCommitSlackCycles > 0, "Missing actual reset commit bound");
        return rows;
    }

    private static LoaderSource LoadLoaderSource()
    {
        string folder = Root + "Temp/first_loader_edge_source/";
        byte[] captured = File.ReadAllBytes(folder + "buffers.bin");
        byte[] original = File.ReadAllBytes(Root + "Temp/human_164819_fulla/replay1/buffers.bin");
        Require(captured.Length == 400 * 4384 && original.Length == 38886 * 4384, "Loader source completeness");
        for (int index = 7 * 4384; index < captured.Length; index++)
            Require(captured[index] == original[index], "Loader source differs from unchanged original replay");

        string[] lines = File.ReadAllLines(folder + "body.csv");
        Require(lines.Length > 1 && lines[0] ==
            "poll,cycle,pc,bank,a,x,y,sp,level,delta,count,world,blank,bracket,cursor,ppuFrame,scanline,dot",
            "Loader body source schema");
        var loader = new LoaderSource();
        ushort previous = 0;
        byte previousA = 0, previousY = 0;
        bool hasPrevious = false;
        for (int index = 1; index < lines.Length; index++)
        {
            string[] cells = lines[index].Split(',');
            Require(cells.Length == 18, "Loader body source field count");
            int poll = int.Parse(cells[0], CultureInfo.InvariantCulture);
            Require(poll >= 263 && poll <= RefusalPoll, "Loader body source poll outside scope");
            ushort pc = Convert.ToUInt16(cells[2], 16);
            Dictionary<ushort, long> entries;
            if (!loader.Cycles.TryGetValue(poll, out entries))
            {
                entries = new Dictionary<ushort, long>();
                loader.Cycles.Add(poll, entries);
            }
            if (!entries.ContainsKey(pc)) entries.Add(pc, long.Parse(cells[1], CultureInfo.InvariantCulture));
            if (pc == 0xC000)
            {
                // $C000 is logged on NMI entry; the pushed return address is the successor
                // of the last instruction actually executed before the interrupt.
                Require(hasPrevious && !loader.Interrupted.ContainsKey(poll), "Duplicate loader NMI boundary");
                loader.Interrupted.Add(poll, NmiReturnAddress(previous, previousA, previousY));
            }
            previous = pc;
            previousA = Convert.ToByte(cells[4], 16);
            previousY = Convert.ToByte(cells[6], 16);
            hasPrevious = true;
        }
        return loader;
    }

    // Fixed-bank $FE92-$FEAA wait loop, lengths and branch conditions from the ROM bytes.
    private static ushort NmiReturnAddress(ushort pc, byte a, byte y)
    {
        switch (pc)
        {
            case 0xFE94: return 0xFE96; // STX $90
            case 0xFE96: return 0xFE98; // LDY #$04
            case 0xFE98: return 0xFE9A; // LDA $80,X
            case 0xFE9A: return 0xFE9C; // CMP #$04
            case 0xFE9C: return (ushort)(a >= 4 ? 0xFEA8 : 0xFE9E); // BCS
            case 0xFE9E: return 0xFE9F; // INX
            case 0xFE9F: return 0xFEA0;
            case 0xFEA0: return 0xFEA1;
            case 0xFEA1: return 0xFEA2;
            case 0xFEA2: return 0xFEA3; // DEY
            case 0xFEA3: return (ushort)(y != 0 ? 0xFE98 : 0xFEA5); // BNE
            case 0xFEA5: return 0xFE92; // JMP $FE92
            case 0xFEA8: return 0xFEAA; // LDA $90
            case 0xFEAA: return (ushort)(a != 0 ? 0xFE92 : 0xFEAC); // BNE
            case 0xFEAC: return 0xFEAD; // DEY
            default: throw new InvalidOperationException("Unexpected pre-NMI instruction $" + pc.ToString("X4"));
        }
    }

    private static Dictionary<int, Dictionary<ushort, string[]>> LoadBoundarySource()
    {
        string folder = Root + "Temp/poweron_resident_source/";
        byte[] captured = File.ReadAllBytes(folder + "buffers.bin");
        byte[] original = File.ReadAllBytes(Root + "Temp/human_164819_fulla/replay1/buffers.bin");
        Require(captured.Length == 263 * 4384 && original.Length == 38886 * 4384, "Reset source completeness");
        for (int index = 7 * 4384; index < captured.Length; index++)
            Require(captured[index] == original[index], "Reset source differs from unchanged original replay");
        var rows = new Dictionary<int, Dictionary<ushort, string[]>>();
        string[] lines = File.ReadAllLines(folder + "boundaries.csv");
        Require(lines[0] == "poll,cycle,pc,interrupted,sp,ppuFrame,scanline,dot,f0,fe,ff", "Reset source schema");
        for (int index = 1; index < lines.Length; index++)
        {
            string[] cells = lines[index].Split(',');
            Require(cells.Length == 11, "Reset source field count");
            int poll = int.Parse(cells[0], CultureInfo.InvariantCulture);
            ushort pc = Convert.ToUInt16(cells[2], 16);
            if (!rows.TryGetValue(poll, out var entries)) { entries = new Dictionary<ushort, string[]>(); rows.Add(poll, entries); }
            Require(!entries.ContainsKey(pc), "Duplicate reset source boundary");
            entries.Add(pc, cells);
        }
        return rows;
    }

    private static void ValidateLoader(LoaderSource loader)
    {
        Require(loader.Cycles.Count == RefusalPoll - 263 + 1, "Loader body source poll coverage");
        Require(loader.Interrupted.Count == RefusalPoll - 264 + 1, "Loader interrupted PC coverage");
        Dictionary<ushort, long> first;
        Require(loader.Cycles.TryGetValue(263, out first) && !first.ContainsKey(0xC000),
            "Poll 263 NMI belongs to the reset boundary source");
        for (int poll = 264; poll <= RefusalPoll; poll++)
        {
            loader.At(poll, 0xC000);
            Require(loader.Interrupted.ContainsKey(poll), "Loader interrupted PC missing " + poll);
        }

        long Span(int poll, ushort from, ushort to) => loader.At(poll, to) - loader.At(poll, from);

        Require(Span(263, 0xC5F7, 0xC95C) == 6, "Loader $C5F7->$C95C span");
        Require(Span(263, 0xC95C, 0xC5FA) == 401, "Loader $C95C span");
        Require(Span(263, 0xC235, 0xC95F) == 367, "Loader $C235 slot span");
        Require(Span(263, 0xC5F7, 0xC285) == 450, "Loader $C5F7->$C285 span");
        Require(Span(263, 0xC5F7, 0xFF06) == 1647, "Loader $C5F7->$FF06 span");
        Require(Span(263, 0xFF06, 0xFF19) == 49, "Loader first yield span");
        Require(Span(263, 0xC5F7, 0xFF19) == 1696, "Loader first commit span");
        for (int poll = 264; poll <= 278; poll++)
        {
            Require(Span(poll, 0xFECF, 0xC2AA) == 549, "Loader resume scope " + poll);
            long body = Span(poll, 0xC2AA, 0xFF19);
            bool ramp = poll == 267 || poll == 271 || poll == 275;
            Require(body == (ramp ? 1281 : 69), "Loader fade body " + poll);
        }
        Require(Span(279, 0xFECF, 0xC2AA) == 549, "Loader exit resume scope");
        Require(Span(279, 0xC2AA, 0xC24B) == 32, "Loader exit fade tail");
        Require(Span(279, 0xC24B, 0xC235) == 6, "Loader exit clear call");
        Require(Span(279, 0xC235, 0xC24E) == 367, "Loader exit slot loop");
        Require(Span(279, 0xC24E, 0xC21E) == 6, "Loader exit wipe call");
        Require(Span(279, 0xC21E, 0xC251) == 1028, "Loader exit wipe span");
        Require(Span(279, 0xC251, 0xFF06) == 3, "Loader exit yield");
        Require(Span(279, 0xFF06, 0xFF19) == 49, "Loader exit yield span");
        Require(Span(279, 0xC2AA, 0xFF19) == 1491, "Loader exit total");
        Require(Span(280, 0xFECF, 0xDD5A) == 549, "Loader $DD5A resume scope");
        Require(Span(280, 0xDD5A, 0xFF19) == 55, "Loader $DD5A yield span");
        Require(Span(281, 0xFECF, 0xDD5D) == 549, "Loader $DD5D resume scope");
        Require(Span(281, 0xDD5D, 0xDE86) == 1540, "Loader $DD5D->$DE86 source span");
    }

    private static void Compare(List<Snapshot> actual, Dictionary<int, Dictionary<ushort, string[]>> boundaries,
        LoaderSource loader)
    {
        ValidateLoader(loader);
        Require(actual.Count == ExpectedRows, "Missing or extra actual reset calls: " + actual.Count);
        for (int index = 0; index < actual.Count; index++)
        {
            var row = actual[index];
            Require(row.Poll == index + FirstPoll, "Missing, duplicate or reordered reset input boundary");
            Require(row.Commits, "Reset yield did not commit at poll " + row.Poll);
            Require(row.State == FrameScheduler.Waiting && row.Timer == 1,
                "Reset scheduler state owner mismatch at poll " + row.Poll);
            Require(row.Body - row.Resume == InputTiming.ResumeCallCycles + 525 + InputTiming.ResumeEpilogueCycles,
                "Reset resume/body hook scope changed at poll " + row.Poll);
            if (row.Poll <= 263)
            {
                Dictionary<ushort, string[]> expected;
                Require(boundaries.TryGetValue(row.Poll, out expected) && expected.ContainsKey(0xC000) && expected.ContainsKey(0xFECF),
                    "Missing reset boundary source " + row.Poll);
                Require(row.Handler - 2 == long.Parse(expected[0xC000][1], CultureInfo.InvariantCulture),
                    "Reset handler mismatch " + row.Poll);
                Require(row.Interrupted == Convert.ToUInt16(expected[0xC000][3], 16),
                    "Reset interrupted PC mismatch " + row.Poll);
                Require(row.Resume - 2 == long.Parse(expected[0xFECF][1], CultureInfo.InvariantCulture),
                    "Reset input resume mismatch " + row.Poll);
                long finisher = row.Poll == 263
                    ? loader.At(263, 0xFF19)
                    : long.Parse(expected[0xFF19][1], CultureInfo.InvariantCulture);
                Require(row.Finish - 2 == finisher, "Reset body endpoint mismatch " + row.Poll);
                if (row.Poll == 263)
                {
                    long resumeAnchor = long.Parse(expected[0xFECF][1], CultureInfo.InvariantCulture) + 551;
                    long firstWork = loader.At(263, 0xC5F7) - resumeAnchor + 1696 + 2;
                    Require(row.Finish - row.Body == firstWork, "Reset first-loader work mismatch");
                }
            }
            else
            {
                Require(row.Handler - 2 == loader.At(row.Poll, 0xC000), "Loader handler mismatch " + row.Poll);
                Require(row.Interrupted == loader.Interrupted[row.Poll], "Loader interrupted PC mismatch " + row.Poll
                    + " actual $" + row.Interrupted.ToString("X4") + " source $" + loader.Interrupted[row.Poll].ToString("X4"));
                Require(row.Resume - 2 == loader.At(row.Poll, 0xFECF), "Loader input resume mismatch " + row.Poll);
                ushort anchor = row.Poll <= 279 ? (ushort)0xC2AA : (ushort)0xDD5A;
                Require(row.Body - 2 == loader.At(row.Poll, anchor), "Loader resume anchor mismatch " + row.Poll);
                Require(row.Finish - 2 == loader.At(row.Poll, 0xFF19), "Loader body endpoint mismatch " + row.Poll);
                Require(row.Finish - row.Body == loader.At(row.Poll, 0xFF19) - loader.At(row.Poll, anchor),
                    "Loader work length mismatch " + row.Poll);
            }
        }
    }

    private static void Reject(Action action)
    {
        bool rejected = false;
        try { action(); }
        catch (InvalidOperationException) { rejected = true; }
        if (!rejected) throw new InvalidOperationException("Reset clock comparator accepted a relevant mismatch");
    }

    public static string Run()
    {
        if (UnityEditor.EditorApplication.isCompiling || UnityEditor.EditorUtility.scriptCompilationFailed)
            throw new InvalidOperationException("Refuse stale Core");
        byte[] input = File.ReadAllBytes(Root + "Playthroughs/20261022T164819Z/input.bin");
        using (var algorithm = SHA256.Create())
            Require(input.Length == 38886 && BitConverter.ToString(algorithm.ComputeHash(input)).Replace("-", "").ToLowerInvariant()
                == "137190299728c9ea6285ebf057fa25b1efaf916b9aeb2ad6d21fa46efb7f6275", "Unchanged human input identity");
        var boundaries = LoadBoundarySource();
        var loader = LoadLoaderSource();
        var actual = RunNative(input, false, out int untouched, out ResetProcessClock refusal);
        Require(untouched == 0, "Unexpected native request mutation");
        Require(refusal != null && !refusal.Supported && !refusal.Active
            && refusal.Stage == ResetProcessClock.Phase.Unsupported
            && refusal.Unsupported.Contains("DD5D") && refusal.Unsupported.Contains("DE86"),
            "Unpriced $DD5D continuation was not refused: "
                + (refusal == null ? "missing clock" : refusal.Stage + " " + refusal.Unsupported));
        Compare(actual, boundaries, loader);

        int negatives = 0;
        foreach (int index in new[] { 0, 18, 257, 273, 274 })
        {
            foreach (string field in new[] { "Handler", "Resume", "Body", "Finish", "Interrupted" })
            {
                var changed = new List<Snapshot>(actual);
                changed[index] = actual[index].Copy();
                if (field == "Handler") changed[index].Handler++;
                else if (field == "Resume") changed[index].Resume++;
                else if (field == "Body") changed[index].Body++;
                else if (field == "Finish") changed[index].Finish++;
                else changed[index].Interrupted ^= (ushort)1;
                Reject(() => Compare(changed, boundaries, loader));
                negatives++;
            }
        }
        var missing = new List<Snapshot>(actual); missing.RemoveAt(0);
        Reject(() => Compare(missing, boundaries, loader)); negatives++;
        var extra = new List<Snapshot>(actual); extra.Add(actual[0]);
        Reject(() => Compare(extra, boundaries, loader)); negatives++;
        var duplicate = new List<Snapshot>(actual); duplicate[1] = actual[0];
        Reject(() => Compare(duplicate, boundaries, loader)); negatives++;
        foreach (var target in new[] { new[] { 263, 0xC95F }, new[] { 267, 0xC2AA },
            new[] { 279, 0xC24E }, new[] { 280, 0xFF19 }, new[] { 281, 0xDE86 } })
        {
            var altered = loader.Copy();
            altered.Cycles[target[0]][(ushort)target[1]]++;
            Reject(() => Compare(actual, boundaries, altered)); negatives++;
        }
        var interrupted = loader.Copy();
        interrupted.Interrupted[266] ^= (ushort)1;
        Reject(() => Compare(actual, boundaries, interrupted)); negatives++;

        var dropped = RunNative(input, true, out int droppedCount, out ResetProcessClock droppedRefusal);
        Require(droppedCount == 1, "Missing native CHR-request negative");
        Require(droppedRefusal != null && droppedRefusal.Stage == ResetProcessClock.Phase.Unsupported,
            "Missing CHR request changed the loader refusal");
        Reject(() => Compare(dropped, boundaries, loader)); negatives++;
        Require(actual[18].Handler == dropped[18].Handler && actual[18].Resume - dropped[18].Resume == 49,
            "Missing CHR request did not expose the expected service-path difference");
        return "PASS actual Game.Flow.ResetClock: loader calls275 (polls6-280), handler/interrupted/resume/body/finish each275, commits275, DD5Drefusal1; "
            + "source spans C235=367 C95C=401 C5F7->FF19=1696 ordinary69 ramp1281(267,271,275) exit1491 DD5A=55; "
            + "loader buffer identity393 records from f8; strict negativeControls=" + negatives
            + ". No external clock instantiation or source-state seed. The first unpriced continuation $DD5D-$DE86 (source span1540) remains refused and outside native scope.";
    }
}
