using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using Darkwing.Core;

public static class ResetClockVerify
{
    private const string Root = "D:/Art/SW Code Zero Unity/";

    private sealed class Snapshot
    {
        public int Poll;
        public long Handler, Resume, Body, Finish;
        public ushort Interrupted;
        public byte State, Timer;
        public bool Commits;
        public Snapshot Copy() => (Snapshot)MemberwiseClone();
    }

    private static void Require(bool value, string message)
    {
        if (!value) throw new InvalidOperationException(message);
    }

    private static byte[] Data(string name) => File.ReadAllBytes(Root + "Assets/Darkwing/Data/" + name + ".bytes");

    private static List<Snapshot> RunNative(byte[] input, bool dropCopyrightRequest, out int dropped)
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
        for (int poll = 1; poll <= 263; poll++)
        {
            if (dropCopyrightRequest && game.Player.ChrLatch1 == 0x99)
            {
                game.Player.ChrLatch1 = 0;
                dropped++;
            }
            game.AdvanceFrame(NesButtons.FromByte(input[poll - 1]));
            var clock = flow.ResetClock;
            if (clock == null || clock.LastNmiOrdinal == 0) continue;
            Require(clock.Supported, "Actual reset clock unsupported: " + clock.Unsupported);
            bool committed = clock.Stage == ResetProcessClock.Phase.Waiting;
            Require(committed || clock.Stage == ResetProcessClock.Phase.Stopped, "Reset clock did not finish its native body");
            rows.Add(new Snapshot
            {
                Poll = poll, Handler = clock.HandlerCycle, Resume = clock.ResumeCycle, Body = clock.BodyStartCycle,
                Finish = committed ? clock.CommitCycle : clock.StopCycle, Interrupted = clock.InterruptedPc,
                State = clock.ProcessState, Timer = clock.ProcessTimer, Commits = committed
            });
            if (!committed)
                Require(!clock.Active && clock.StopPc == 0xC5F7 && clock.ProcessState == FrameScheduler.Running,
                    "Opaque loader was falsely reseeded as Waiting");
        }
        Require(flow.ResetClock != null && flow.ResetClock.MinimumCommitSlackCycles > 0, "Missing actual reset commit bound");
        return rows;
    }

    private static Dictionary<int, Dictionary<ushort, string[]>> Source()
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

    private static void Compare(List<Snapshot> actual, Dictionary<int, Dictionary<ushort, string[]>> expected)
    {
        Require(actual.Count == 258, "Missing or extra actual reset calls");
        for (int index = 0; index < actual.Count; index++)
        {
            var row = actual[index];
            Require(row.Poll == index + 6, "Missing, duplicate or reordered reset input boundary");
            var source = expected[row.Poll];
            Require(row.Handler - 2 == long.Parse(source[0xC000][1], CultureInfo.InvariantCulture), "Reset handler mismatch " + row.Poll);
            Require(row.Interrupted == Convert.ToUInt16(source[0xC000][3], 16), "Reset interrupted PC mismatch " + row.Poll);
            Require(row.Resume - 2 == long.Parse(source[0xFECF][1], CultureInfo.InvariantCulture), "Reset input resume mismatch " + row.Poll);
            Require(row.Body - row.Resume == InputTiming.ResumeCallCycles + 525 + InputTiming.ResumeEpilogueCycles,
                "Reset initial no-buttons hook scope changed");
            Require(row.Commits == (index != actual.Count - 1), "Reset commit/opaque-stop coverage");
            ushort target = row.Commits ? (ushort)0xFF19 : (ushort)0xC5F7;
            Require(row.Finish - 2 == long.Parse(source[target][1], CultureInfo.InvariantCulture), "Reset body endpoint mismatch " + row.Poll);
            Require(row.State == (row.Commits ? FrameScheduler.Waiting : FrameScheduler.Running)
                && row.Timer == (row.Commits ? 1 : 0), "Reset scheduler state owner mismatch");
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
        var expected = Source();
        var actual = RunNative(input, false, out int untouched);
        Require(untouched == 0, "Unexpected native request mutation");
        Compare(actual, expected);
        int negatives = 0;
        foreach (int index in new[] { 0, 18, actual.Count - 1 })
        {
            foreach (string field in new[] { "Handler", "Resume", "Finish" })
            {
                var changed = new List<Snapshot>(actual);
                changed[index] = actual[index].Copy();
                if (field == "Handler") changed[index].Handler++;
                else if (field == "Resume") changed[index].Resume++;
                else changed[index].Finish++;
                Reject(() => Compare(changed, expected));
                negatives++;
            }
        }
        var missing = new List<Snapshot>(actual); missing.RemoveAt(0);
        Reject(() => Compare(missing, expected)); negatives++;
        var extra = new List<Snapshot>(actual); extra.Add(actual[0]);
        Reject(() => Compare(extra, expected)); negatives++;
        var duplicate = new List<Snapshot>(actual); duplicate[1] = actual[0];
        Reject(() => Compare(duplicate, expected)); negatives++;
        var dropped = RunNative(input, true, out int droppedCount);
        Require(droppedCount == 1, "Missing native CHR-request negative");
        Reject(() => Compare(dropped, expected)); negatives++;
        Require(actual[18].Handler == dropped[18].Handler && actual[18].Resume - dropped[18].Resume == 49,
            "Missing CHR request did not expose the expected service-path difference");
        return $"PASS actual Game.Flow.ResetClock: arrivals258/interruptedPC258/resumes258/commits257/RunningStopC5F7=1; native state/timer258; strict negativeControls={negatives}, including actual99CHRrequest-drop/49cycle service. No external clock instantiation or source-state seed. Original reset profile/263poll prefix only; later loader and mission timing are outside scope.";
    }
}
