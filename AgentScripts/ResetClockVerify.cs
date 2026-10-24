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
    private const int FirstStreamPoll = 281;
    private const int StopPoll = 289;
    private const int ExpectedRows = StopPoll - FirstPoll + 1;
    private const int LastEdgePoll = 281;

    private sealed class Snapshot
    {
        public int Poll;
        public long Handler, Resume, Body, Finish;
        public long Stream, Commit, Budget;
        public long Edge, LastEdge, Ordinal;
        public int CpuOff, PpuOff;
        public int Cursor;
        public ushort Interrupted, StopPc;
        public byte State, Timer;
        public bool Commits, Streaming;
        public ResetProcessClock.Phase Stage;
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

    private sealed class Head
    {
        public long Cycle;
        public byte Cursor;
    }

    private sealed class StreamSource
    {
        public readonly Dictionary<int, long> Nmi = new Dictionary<int, long>();
        public readonly Dictionary<int, byte> Cursor = new Dictionary<int, byte>();
        public readonly Dictionary<int, long> InputHook = new Dictionary<int, long>();
        public readonly Dictionary<int, List<Head>> Heads = new Dictionary<int, List<Head>>();
        public readonly Dictionary<int, Dictionary<ushort, long>> Cycles = new Dictionary<int, Dictionary<ushort, long>>();

        public StreamSource Copy()
        {
            var copy = new StreamSource();
            foreach (var pair in Nmi) copy.Nmi.Add(pair.Key, pair.Value);
            foreach (var pair in Cursor) copy.Cursor.Add(pair.Key, pair.Value);
            foreach (var pair in InputHook) copy.InputHook.Add(pair.Key, pair.Value);
            foreach (var pair in Heads)
            {
                var heads = new List<Head>();
                foreach (var head in pair.Value) heads.Add(new Head { Cycle = head.Cycle, Cursor = head.Cursor });
                copy.Heads.Add(pair.Key, heads);
            }
            foreach (var pair in Cycles)
            {
                var entries = new Dictionary<ushort, long>();
                foreach (var entry in pair.Value) entries.Add(entry.Key, entry.Value);
                copy.Cycles.Add(pair.Key, entries);
            }
            return copy;
        }
    }

    private static void Require(bool value, string message)
    {
        if (!value) throw new InvalidOperationException(message);
    }

    private static byte[] Data(string name) => File.ReadAllBytes(Root + "Assets/Darkwing/Data/" + name + ".bytes");

    private static List<Snapshot> RunNative(byte[] input, bool dropCopyrightRequest, out int dropped,
        out ResetProcessClock stopClock)
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
        stopClock = null;
        for (int poll = 1; poll <= StopPoll; poll++)
        {
            if (dropCopyrightRequest && game.Player.ChrLatch1 == 0x99)
            {
                game.Player.ChrLatch1 = 0;
                dropped++;
            }
            game.AdvanceFrame(NesButtons.FromByte(input[poll - 1]));
            var clock = flow.ResetClock;
            if (clock == null || clock.LastNmiOrdinal == 0) continue;
            var row = new Snapshot
            {
                Poll = poll, Handler = clock.HandlerCycle, Resume = clock.ResumeCycle, Body = clock.BodyStartCycle,
                Interrupted = clock.InterruptedPc, State = clock.ProcessState, Timer = clock.ProcessTimer,
                Stage = clock.Stage, Streaming = clock.Streaming, Cursor = game.Player.StageCursor,
                Stream = clock.StreamCycle, Commit = clock.CommitCycle,
                Budget = clock.StreamBudgetCycles,
                Edge = clock.PhysicalEdgeMaster, LastEdge = clock.LastNmiEdgeMaster,
                Ordinal = clock.LastNmiOrdinal, CpuOff = clock.CpuOffset, PpuOff = clock.PpuOffset
            };
            if (poll <= LastCommittedPoll)
            {
                Require(clock.Supported, "Actual reset clock unsupported at poll " + poll + ": " + clock.Unsupported);
                Require(clock.Stage == ResetProcessClock.Phase.Waiting,
                    "Reset resident did not commit at poll " + poll + ": " + clock.Stage + " " + clock.Unsupported);
                row.Finish = clock.CommitCycle;
                row.Commits = true;
            }
            else if (poll < StopPoll)
            {
                Require(clock.Supported, "Actual reset clock unsupported at poll " + poll + ": " + clock.Unsupported);
                Require(clock.Stage == ResetProcessClock.Phase.Running,
                    "Streaming loader left Running at poll " + poll + ": " + clock.Stage + " " + clock.Unsupported);
            }
            else
            {
                stopClock = clock;
                row.Finish = clock.StopCycle;
                row.StopPc = clock.StopPc;
            }
            rows.Add(row);
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
            Require(poll >= 263 && poll <= LastEdgePoll, "Loader body source poll outside scope");
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

    private static StreamSource LoadStreamSource()
    {
        string folder = Root + "Temp/first_loader_stream_source/";
        byte[] captured = File.ReadAllBytes(folder + "buffers.bin");
        byte[] original = File.ReadAllBytes(Root + "Temp/human_164819_fulla/replay1/buffers.bin");
        Require(captured.Length == 400 * 4384 && original.Length == 38886 * 4384, "Stream source completeness");
        for (int index = 7 * 4384; index < captured.Length; index++)
            Require(captured[index] == original[index], "Stream source differs from unchanged original replay");

        string[] lines = File.ReadAllLines(folder + "stream.csv");
        Require(lines.Length > 1 && lines[0] ==
            "poll,cycle,pc,a,x,y,sp,r25,r23,r0f,r05,r1f,r19,r2f,rf0,r9a,ppuFrame,scanline,dot",
            "Stream source schema");
        var stream = new StreamSource();
        for (int index = 1; index < lines.Length; index++)
        {
            string[] cells = lines[index].Split(',');
            Require(cells.Length == 19, "Stream source field count");
            int poll = int.Parse(cells[0], CultureInfo.InvariantCulture);
            if (poll < FirstStreamPoll || poll > StopPoll + 2) continue;
            long cycle = long.Parse(cells[1], CultureInfo.InvariantCulture);
            ushort pc = Convert.ToUInt16(cells[2], 16);
            byte cursor = Convert.ToByte(cells[7], 16);
            Dictionary<ushort, long> entries;
            if (!stream.Cycles.TryGetValue(poll, out entries))
            {
                entries = new Dictionary<ushort, long>();
                stream.Cycles.Add(poll, entries);
            }
            if (!entries.ContainsKey(pc)) entries.Add(pc, cycle);
            if (pc == 0xC000)
            {
                Require(!stream.Nmi.ContainsKey(poll), "Duplicate stream NMI entry");
                stream.Nmi.Add(poll, cycle);
                stream.Cursor.Add(poll, cursor);
            }
            else if (pc == 0xFECF)
            {
                Require(!stream.InputHook.ContainsKey(poll), "Duplicate stream input hook");
                stream.InputHook.Add(poll, cycle);
            }
            else if (pc == 0xDE86)
            {
                List<Head> heads;
                if (!stream.Heads.TryGetValue(poll, out heads))
                {
                    heads = new List<Head>();
                    stream.Heads.Add(poll, heads);
                }
                heads.Add(new Head { Cycle = cycle, Cursor = cursor });
            }
        }
        return stream;
    }

    private static void ValidateLoader(LoaderSource loader)
    {
        Require(loader.Cycles.Count == LastEdgePoll - 263 + 1, "Loader body source poll coverage");
        Require(loader.Interrupted.Count == LastEdgePoll - 264 + 1, "Loader interrupted PC coverage");
        Dictionary<ushort, long> first;
        Require(loader.Cycles.TryGetValue(263, out first) && !first.ContainsKey(0xC000),
            "Poll 263 NMI belongs to the reset boundary source");
        for (int poll = 264; poll <= LastEdgePoll; poll++)
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

    private static void RequirePcs(Dictionary<ushort, long> cycles, int poll, params ushort[] pcs)
    {
        Require(cycles.Count == pcs.Length, "Stream source poll " + poll + " observed " + cycles.Count + " PCs");
        foreach (ushort pc in pcs)
            Require(cycles.ContainsKey(pc), "Stream source poll " + poll + " lacks $" + pc.ToString("X4"));
    }

    private static void ValidateStream(StreamSource stream, LoaderSource loader)
    {
        for (int poll = FirstStreamPoll; poll <= StopPoll + 1; poll++)
            Require(stream.Nmi.ContainsKey(poll) && stream.Cursor.ContainsKey(poll),
                "Stream source NMI boundary missing at poll " + poll);
        Require(stream.Nmi[FirstStreamPoll] == loader.At(FirstStreamPoll, 0xC000),
            "Stream source NMI disagrees with reset source");
        Require(stream.InputHook.ContainsKey(FirstStreamPoll)
            && stream.InputHook[FirstStreamPoll] == loader.At(FirstStreamPoll, 0xFECF),
            "Stream source input hook disagrees with reset source");

        byte[] cursors = { 0x00, 0x0F, 0x1F, 0x2F, 0x40, 0x51, 0x61, 0x72, 0x85, 0x88 };
        for (int poll = FirstStreamPoll; poll <= StopPoll + 1; poll++)
            Require(stream.Cursor[poll] == cursors[poll - FirstStreamPoll],
                "Stream source cursor at poll " + poll + " is $" + stream.Cursor[poll].ToString("X2"));
        for (int poll = FirstStreamPoll + 1; poll <= StopPoll; poll++)
        {
            Require(!stream.InputHook.ContainsKey(poll) && !stream.Heads.ContainsKey(poll),
                "Inline loader yielded to the input hook at poll " + poll);
            RequirePcs(stream.Cycles[poll], poll, 0xC000, 0xC114);
        }
        Require(!stream.InputHook.ContainsKey(StopPoll) && !stream.Heads.ContainsKey(StopPoll),
            "Inline loader yielded to the input hook at the stop poll");

        List<Head> heads;
        Require(stream.Heads.TryGetValue(FirstStreamPoll, out heads) && heads.Count == 15,
            "Stream source pass head count");
        Require(heads[0].Cursor == 0x00 && heads[heads.Count - 1].Cursor == 0x0E,
            "Stream source pass head cursors");
        Require(heads[0].Cycle == loader.At(FirstStreamPoll, 0xDE86),
            "Stream source first pass head disagrees with reset source");
        int[] costs = { 1768, 1768, 1768, 1768, 1778, 1773, 1768, 1717, 1768, 1768, 1768, 1768, 1778, 1773 };
        for (int index = 0; index < costs.Length; index++)
        {
            Require(heads[index + 1].Cursor == (byte)(heads[index].Cursor + 1),
                "Stream source pass cursor step at $" + heads[index].Cursor.ToString("X2"));
            Require(heads[index + 1].Cycle - heads[index].Cycle == costs[index],
                "Stream source pass cost at cursor $" + heads[index].Cursor.ToString("X2"));
        }

        // Poll 290 returns to the input hook and resumes $DDCE: $DE00/$C158, $DE03 and its
        // yield, then the $FE9A spin. Poll 291 resumes at $DE06 after that yield.
        var next = stream.Cycles[StopPoll + 1];
        RequirePcs(next, StopPoll + 1, 0xC000, 0xC114, 0xFECF, 0xDE00, 0xC158, 0xDE03, 0xFF06, 0xFF19, 0xFE9A);
        Require(next[0xC114] < next[0xFECF] && next[0xFECF] < next[0xDE00], "Stream poll 290 spawn ordering");
        Require(next[0xDE00] < next[0xC158] && next[0xC158] < next[0xDE03], "Stream poll 290 spawn body ordering");
        Require(next[0xDE03] < next[0xFF06] && next[0xFF06] < next[0xFF19], "Stream poll 290 yield ordering");
        Require(next[0xFF19] < next[0xFE9A], "Stream poll 290 spin must follow its yield");
        var resume = stream.Cycles[StopPoll + 2];
        RequirePcs(resume, StopPoll + 2, 0xC000, 0xC114, 0xFECF, 0xDE06, 0xFF06, 0xFF19, 0xFE9A);
        Require(resume[0xFECF] < resume[0xDE06] && resume[0xDE06] < resume[0xFF06],
            "Stream poll 291 $DE06 resume ordering");
        Require(!resume.ContainsKey(0xDE00) && !resume.ContainsKey(0xDE03),
            "Stream poll 291 repeated the unclocked spawn path");
    }

    private static void Compare(List<Snapshot> actual, Dictionary<int, Dictionary<ushort, string[]>> boundaries,
        LoaderSource loader, StreamSource stream)
    {
        ValidateLoader(loader);
        ValidateStream(stream, loader);
        Require(actual.Count == ExpectedRows, "Missing or extra actual reset calls: " + actual.Count);

        for (int index = 0; index <= LastCommittedPoll - FirstPoll; index++)
        {
            var row = actual[index];
            Require(row.Poll == index + FirstPoll, "Missing, duplicate or reordered reset input boundary");
            Require(row.Commits && row.Stage == ResetProcessClock.Phase.Waiting,
                "Reset yield did not commit at poll " + row.Poll);
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

        var first = actual[FirstStreamPoll - FirstPoll];
        Require(first.Poll == FirstStreamPoll && first.Streaming && !first.Commits,
            "First stream poll is not the $DD5D prelude pass");
        Require(first.Stage == ResetProcessClock.Phase.Running, "First stream poll left Running");
        Require(first.State == FrameScheduler.Running && first.Timer == 0,
            "First stream poll re-selected a scheduler phase: " + first.State + "/" + first.Timer);
        Require(first.Handler - 2 == stream.Nmi[FirstStreamPoll], "First stream poll handler mismatch");
        Require(first.Interrupted == loader.Interrupted[FirstStreamPoll], "First stream poll interrupted PC mismatch");
        Require(first.Resume - 2 == stream.InputHook[FirstStreamPoll], "First stream poll input resume mismatch");
        Require(first.Body - 2 == loader.At(FirstStreamPoll, 0xDD5D), "First stream poll staging anchor mismatch");
        Require(first.Body - first.Resume == InputTiming.ResumeCallCycles + 525 + InputTiming.ResumeEpilogueCycles,
            "First stream poll resume/body hook scope changed");
        // The stream splits the interrupted $E698 pass at the edge-derived threshold, while
        // Mesen logs $C000 at the first instruction boundary after the physical NMI. The
        // loader loop's 2-6 cycle instructions make that boundary land 5-7 CPU cycles after
        // the threshold; the pass-level clock cannot resolve which boundary inside a priced
        // pass the CPU had reached. The band below is that quantization range, not slack:
        // the cursor, budget and completion checks around it stay exact.
        long streamEdge = stream.Nmi[FirstStreamPoll + 1];
        Require(first.Stream <= streamEdge - 5 && first.Stream >= streamEdge - 7,
            "First stream poll did not consume its whole frame budget: stream " + first.Stream
            + " source edge band " + (streamEdge - 7) + ".." + (streamEdge - 5) + " budget " + first.Budget
            + " edge " + first.Edge + " last " + first.LastEdge + " ordinal " + first.Ordinal
            + " cpuOff " + first.CpuOff + " ppuOff " + first.PpuOff);
        Require(first.Budget == 0, "First stream poll left an unused frame budget");
        Require(first.Cursor == stream.Cursor[FirstStreamPoll + 1], "First stream poll cursor mismatch");

        for (int poll = FirstStreamPoll + 1; poll < StopPoll; poll++)
        {
            var row = actual[poll - FirstPoll];
            Require(row.Poll == poll && row.Streaming && !row.Commits, "Stream poll ordering at " + poll);
            Require(row.Stage == ResetProcessClock.Phase.Running, "Stream poll left Running at " + poll);
            Require(row.State == FrameScheduler.Running && row.Timer == 0,
                "Stream poll re-selected a scheduler phase at " + poll + ": " + row.State + "/" + row.Timer);
            Require(row.Handler - 2 <= stream.Nmi[poll] && row.Handler - 2 >= stream.Nmi[poll] - 2,
                "Stream poll handler outside the source quantization band at " + poll + ": model "
                + (row.Handler - 2) + " source " + stream.Nmi[poll]);
            Require(row.Interrupted == 0, "Stream poll invented an interrupted PC at " + poll);
            Require(row.Stream <= stream.Nmi[poll + 1] - 5 && row.Stream >= stream.Nmi[poll + 1] - 7,
                "Stream poll did not consume its whole frame budget at " + poll + ": model "
                + row.Stream + " source band " + (stream.Nmi[poll + 1] - 7) + ".." + (stream.Nmi[poll + 1] - 5));
            Require(row.Budget == 0, "Stream poll left an unused frame budget at " + poll);
            Require(row.Cursor == stream.Cursor[poll + 1],
                "Stream poll cursor mismatch at " + poll + ": $" + row.Cursor.ToString("X2")
                + " source $" + stream.Cursor[poll + 1].ToString("X2"));
        }

        var stop = actual[StopPoll - FirstPoll];
        Require(stop.Poll == StopPoll, "Last reset call is not the $DDCB yield");
        Require(!stop.Commits && stop.Stage == ResetProcessClock.Phase.Stopped,
            "Reset clock did not stop at the unclocked $DDCE resume: " + stop.Stage);
        Require(stop.StopPc == 0xDDCB, "Reset clock stopped at $" + stop.StopPc.ToString("X4"));
        Require(stop.StopPc == 0xDDCB && stop.Commit > 0, "Reset clock did not commit the $DDCB yield");
        Require(stop.Commit == stop.Finish, "Reset stop cycle is not the committed yield cycle");
        Require(stop.State == FrameScheduler.Waiting && stop.Timer == 1,
            "Reset stop re-selected a scheduler phase: " + stop.State + "/" + stop.Timer);
        Require(stop.Interrupted == 0, "Reset stop invented an interrupted PC");
        Require(stop.Handler - 2 <= stream.Nmi[StopPoll] && stop.Handler - 2 >= stream.Nmi[StopPoll] - 2,
            "Reset stop handler outside the source quantization band: model " + (stop.Handler - 2)
            + " source " + stream.Nmi[StopPoll]);
        Require(stop.Cursor == stream.Cursor[StopPoll + 1],
            "Reset stop cursor mismatch: $" + stop.Cursor.ToString("X2"));
        Require(stop.Stream == stop.Finish - 74, "Reset stop completion price mismatch: "
            + (stop.Finish - stop.Stream) + " cycles");
        Require(stop.Finish - 2 > stream.Nmi[StopPoll] && stop.Finish - 2 < stream.Nmi[StopPoll + 1] - 5,
            "Reset stop cycle is outside the $DDCB frame: " + stop.Finish);
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
        var stream = LoadStreamSource();
        var actual = RunNative(input, false, out int untouched, out ResetProcessClock stopClock);
        Require(untouched == 0, "Unexpected native request mutation");
        Require(stopClock != null && stopClock.Stage == ResetProcessClock.Phase.Stopped
            && stopClock.StopPc == 0xDDCB && stopClock.StopCycle > 0 && stopClock.CommitCycle == stopClock.StopCycle,
            "Unpriced $DDCE resume did not stop the clock: "
                + (stopClock == null ? "missing clock" : stopClock.Stage + " $" + stopClock.StopPc.ToString("X4")));
        Compare(actual, boundaries, loader, stream);

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
                Reject(() => Compare(changed, boundaries, loader, stream));
                negatives++;
            }
        }
        foreach (int index in new[] { FirstStreamPoll - FirstPoll, 285 - FirstPoll })
        {
            foreach (string field in new[] { "Handler", "Stream", "Interrupted", "Cursor", "Budget", "Stage", "State", "Timer" })
            {
                var changed = new List<Snapshot>(actual);
                changed[index] = actual[index].Copy();
                var row = changed[index];
                if (field == "Handler") row.Handler += 3;
                else if (field == "Stream") row.Stream += 3;
                else if (field == "Interrupted") row.Interrupted ^= (ushort)1;
                else if (field == "Cursor") row.Cursor ^= 1;
                else if (field == "Budget") row.Budget = 1;
                else if (field == "Stage") row.Stage = ResetProcessClock.Phase.Waiting;
                else if (field == "State") row.State = FrameScheduler.Waiting;
                else row.Timer = 1;
                Reject(() => Compare(changed, boundaries, loader, stream));
                negatives++;
            }
        }
        foreach (string field in new[] { "Resume", "Body" })
        {
            var changed = new List<Snapshot>(actual);
            changed[FirstStreamPoll - FirstPoll] = actual[FirstStreamPoll - FirstPoll].Copy();
            if (field == "Resume") changed[FirstStreamPoll - FirstPoll].Resume++;
            else changed[FirstStreamPoll - FirstPoll].Body++;
            Reject(() => Compare(changed, boundaries, loader, stream));
            negatives++;
        }
        var missing = new List<Snapshot>(actual); missing.RemoveAt(0);
        Reject(() => Compare(missing, boundaries, loader, stream)); negatives++;
        var extra = new List<Snapshot>(actual); extra.Add(actual[0]);
        Reject(() => Compare(extra, boundaries, loader, stream)); negatives++;
        var duplicate = new List<Snapshot>(actual); duplicate[1] = actual[0];
        Reject(() => Compare(duplicate, boundaries, loader, stream)); negatives++;

        int stopIndex = actual.Count - 1;
        foreach (string field in new[] { "Handler", "Interrupted", "StopPc", "Commit", "Finish", "Cursor", "Stage", "State", "Timer", "Stream" })
        {
            var changed = new List<Snapshot>(actual);
            changed[stopIndex] = actual[stopIndex].Copy();
            var row = changed[stopIndex];
            if (field == "Handler") row.Handler++;
            else if (field == "Interrupted") row.Interrupted ^= (ushort)1;
            else if (field == "StopPc") row.StopPc ^= (ushort)0x100;
            else if (field == "Commit") row.Commit++;
            else if (field == "Finish") row.Finish++;
            else if (field == "Cursor") row.Cursor ^= 1;
            else if (field == "Stage") row.Stage = ResetProcessClock.Phase.Waiting;
            else if (field == "State") row.State = FrameScheduler.Running;
            else if (field == "Timer") row.Timer = 0;
            else row.Stream++;
            Reject(() => Compare(changed, boundaries, loader, stream));
            negatives++;
        }
        var noStop = new List<Snapshot>(actual); noStop.RemoveAt(noStop.Count - 1);
        Reject(() => Compare(noStop, boundaries, loader, stream)); negatives++;
        var repeatedStop = new List<Snapshot>(actual); repeatedStop.Add(actual[stopIndex]);
        Reject(() => Compare(repeatedStop, boundaries, loader, stream)); negatives++;

        foreach (var target in new[] { new[] { 263, 0xC95F }, new[] { 267, 0xC2AA },
            new[] { 279, 0xC24E }, new[] { 280, 0xFF19 }, new[] { 281, 0xDE86 } })
        {
            var altered = loader.Copy();
            altered.Cycles[target[0]][(ushort)target[1]]++;
            Reject(() => Compare(actual, boundaries, altered, stream)); negatives++;
        }
        var interrupted = loader.Copy();
        interrupted.Interrupted[266] ^= (ushort)1;
        Reject(() => Compare(actual, boundaries, interrupted, stream)); negatives++;
        foreach (var target in new[] { new[] { 284, 0xC000 }, new[] { 285, 0xC000 } })
        {
            // A one-cycle NMI shift stays inside the handler quantization band by design;
            // shift past it to prove the comparator observes the source boundary.
            var altered = stream.Copy();
            altered.Nmi[target[0]] += 3;
            Reject(() => Compare(actual, boundaries, loader, altered)); negatives++;
        }
        var alteredCursor = stream.Copy();
        alteredCursor.Cursor[285] ^= 1;
        Reject(() => Compare(actual, boundaries, loader, alteredCursor)); negatives++;
        var alteredHook = stream.Copy();
        alteredHook.InputHook[281]++;
        Reject(() => Compare(actual, boundaries, loader, alteredHook)); negatives++;
        var alteredHead = stream.Copy();
        alteredHead.Heads[281][3].Cycle++;
        Reject(() => Compare(actual, boundaries, loader, alteredHead)); negatives++;
        var alteredSpawn = stream.Copy();
        // The comparator's poll-290 contract is spawn ordering, not the raw cycle, so the
        // mutation must reorder $DE00 past $C158 to be observable.
        alteredSpawn.Cycles[290][0xDE00] += 1000;
        Reject(() => Compare(actual, boundaries, loader, alteredSpawn)); negatives++;
        var alteredResume = stream.Copy();
        alteredResume.Cycles[291].Remove(0xDE06);
        Reject(() => Compare(actual, boundaries, loader, alteredResume)); negatives++;

        var dropped = RunNative(input, true, out int droppedCount, out ResetProcessClock droppedStop);
        Require(droppedCount == 1, "Missing native CHR-request negative");
        Require(droppedStop != null && (droppedStop.Stage == ResetProcessClock.Phase.Stopped
                || droppedStop.Stage == ResetProcessClock.Phase.Unsupported),
            "Missing CHR request did not change the clock outcome");
        Reject(() => Compare(dropped, boundaries, loader, stream)); negatives++;
        Require(actual[18].Handler == dropped[18].Handler && actual[18].Resume - dropped[18].Resume == 49,
            "Missing CHR request did not expose the expected service-path difference");
        return "PASS actual Game.Flow.ResetClock: committed loader calls275 (polls6-280), streaming loader8 polls (281-288: handler, "
            + "cursor $0F/$1F/$2F/$40/$51/$61/$72/$85, whole-frame budgets), honest stop1 at the $DDCB yield after poll289 "
            + "(136 passes to $88, SP=B9, completion74, no new Waiting); "
            + "source spans C235=367 C95C=401 C5F7->FF19=1696 ordinary69 ramp1281(267,271,275) exit1491 DD5A=55 DD5D->DE86=1540; "
            + "stream source 15 heads f281 ($00-$0E costs 1768/1778/1773/1717), NMI $00->$88, no input hook polls282-289, "
            + "$DE00/$DE03 yield poll290 and $DE06 resume poll291; loader buffer identity393 records from f8; strict negativeControls="
            + negatives + ". No external clock instantiation or source-state seed. $DE86/$E698 now runs natively; the $DDCE spawn "
            + "path stays outside the clock.";
    }
}
