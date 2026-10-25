using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
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

    /// <summary>Field mismatches stay a FAIL of the whole run, but are collected so the run still
    /// reports every differing call and the independent K1 timing evidence before failing.</summary>
    private static void Collect(byte[] actual, byte[] expected, string label, List<string> mismatches)
    {
        try { Compare(actual, expected, label); }
        catch (InvalidOperationException exception) { mismatches.Add(exception.Message); }
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

    // K1: a Card body is dispatched only after the NMI that made slot0 Ready, so its FF17 commit is
    // bounded from that NMI, not from the unknown earlier clock. 17 + latest dispatch + body must fit
    // the shortest NMI-to-NMI interval for the next NMI to find slot0 Waiting in the idle scan.
    private static void K1Waiting(byte state, byte timer, byte bracket, byte depth, byte deferred)
    {
        if (state != FrameScheduler.Waiting || timer != 1 || bracket != 0 || depth != 0 || deferred != 0)
            throw new InvalidOperationException("K1 dispatch NMI lacks a native Card Waiting commit");
    }

    private static int K1Handler(int publication, int timers, int bank, bool audioRuns, int audio)
    {
        if (publication < 0 || !audioRuns || audio < 0) throw new InvalidOperationException("K1 native NMI cost unsupported");
        return publication + timers + bank + audio;
    }

    private static int K1Slack(int delay, int body, int interval) => interval - (FrameScheduler.CommitToIdleCycles + delay + body);

    private static void K1Bound(int delay, int body, int interval)
    {
        if (K1Slack(delay, body, interval) < 0) throw new InvalidOperationException("K1 Card commit may reach the next NMI");
    }

    /// <summary>Shortest sampling-threshold distance: edges 89341/89342 dots apart (4 master ticks
    /// per dot), sampled at ceil(edge/12)+1 for every CPU/PPU master alignment and CPU parity.</summary>
    private static int K1MinimumInterval()
    {
        int result = int.MaxValue;
        foreach (int dots in new[] { 262 * 341 - 1, 262 * 341 })
            for (int alignment = 0; alignment < 24; alignment++)
                result = Math.Min(result, (alignment + 4 * dots + 11) / 12 - (alignment + 11) / 12);
        return result;
    }

    /// <summary>Latest FECF dispatch after the sampling threshold (cycle 0) over every Waiting
    /// position: each idle-scan residue and the FF19…FE91 tail, including an FF17 that straddles
    /// the threshold.</summary>
    private static int K1MaxDispatchDelay(int handler)
    {
        int result = int.MinValue;
        for (int entry = 0; entry < 95 + 22; entry++)
        {
            var scheduler = new FrameScheduler();
            if (entry < 95)
            {
                scheduler.Wait(0, 1, 0x1B5);
                scheduler.BeginExecution(-entry - 4);
            }
            else scheduler.BeginExecutionAfterCommit(0, 98 - entry, 0x1B5);
            while (!scheduler.DispatchPending && scheduler.ExecutionCycle < 0) scheduler.StepExecution();
            if (scheduler.DispatchPending) throw new InvalidOperationException("K1 Waiting entry dispatched before NMI");
            scheduler.Nmi();
            scheduler.ElapseInterrupt(FrameScheduler.NmiEntryCycles + handler);
            for (int step = 0; !scheduler.DispatchPending; step++)
            {
                if (step > 400) throw new InvalidOperationException("K1 scheduler did not dispatch");
                scheduler.StepExecution();
            }
            if (scheduler.DispatchSlot != 0 || !scheduler.DispatchNeedsInput)
                throw new InvalidOperationException("K1 scheduler did not resume slot0 input");
            result = Math.Max(result, checked((int)scheduler.ExecutionCycle));
        }
        return result;
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
        int k1Interval = K1MinimumInterval(), k1Evaluated = 0, k1Failed = 0, k1Offset = -1;
        int k1MinSlack = int.MaxValue, k1MinPoll = 0, k1MinDelay = 0, k1MinBody = 0, k1MinPublication = 0, k1MinTimers = 0, k1MinBank = 0;
        int k1MaxBody = 0, k1MaxHandler = 0, k1MinAllowedBody = int.MaxValue;
        var k1Unsupported = new List<string>();
        var fieldMismatches = new List<string>();
        var k1Rows = new StringBuilder();
        for (int poll = 1; poll <= lastPoll; poll++)
        {
            bool expectedCall = calls.TryGetValue(poll, out var call);
            byte beforeWait = game.Player.CardWaitRemaining;
            byte beforeParity = game.FrameParity;
            string k1Reason = null;
            int k1Publication = -1;
            byte k1State = 0, k1Timer = 0, k1Bracket = 0, k1Depth = 0, k1Deferred = 0;
            if (expectedCall)
            {
                Control(beforeParity, entries[call.Offset + 0x95], "entry completed-frame parity");
                if (call.Resume)
                {
                    if (game.Player.CardPass == null) throw new InvalidOperationException("Card resume has no preceding continuation");
                    Collect(Actual(game), Expected(entries, call.Offset), "Card resume entry " + poll, fieldMismatches);
                    resumeEntryFields += 416;
                    Control(game.Player.CardPass.Bracket, entries[call.Offset + 0x9A], "resume entry bracket");
                    Control((byte)game.Oam.OamCursorBytes, entries[call.Offset + 0x97], "resume entry cursor");
                    // The dispatch NMI publishes what the previous native Card body left. Native Card
                    // paths return before HorizontalScroll/$CA59, so $1C/$1D have no producer here.
                    var prior = game.Player.CardPass;
                    k1State = prior.ProcessState; k1Timer = prior.ProcessTimer; k1Bracket = prior.Bracket;
                    k1Depth = prior.SwitchDepth; k1Deferred = prior.DeferredAudio;
                    if (prior.Active || prior.Stage != CardPassContinuation.Phase.Committed)
                        k1Reason = "K1 previous Card continuation is not committed";
                    else if (checkedPolls.Count == 0 || checkedPolls[checkedPolls.Count - 1] != poll - 1)
                        k1Reason = "K1 previous poll is not a committed native Card call";
                    else if ((game.Ppu.Ctrl & 0x80) == 0)
                        k1Reason = "K1 dispatch NMI disabled";
                    for (int handlerParity = 0; handlerParity < 2 && k1Reason == null; handlerParity++)
                    {
                        if (!game.TryMeasureNativeNmiPublication(handlerParity, k1Bracket, 0, 0, out int cycles, out string reason))
                            k1Reason = "K1 publication " + reason;
                        else k1Publication = Math.Max(k1Publication, cycles);
                    }
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
            Collect(actual, expected, "Card committed " + poll, fieldMismatches);
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
                if (k1Reason == null)
                {
                    try
                    {
                        K1Waiting(k1State, k1Timer, k1Bracket, k1Depth, k1Deferred);
                        // Slots 1-3 have no native Mission owner; they are taken Free here and reported.
                        int timers = NmiTiming.ProcessTimerCycles(new[] { k1State, FrameScheduler.Free, FrameScheduler.Free, FrameScheduler.Free },
                            new byte[] { k1Timer, 0, 0, 0 });
                        int bank = NmiTiming.BankGateCycles(k1Depth, k1Deferred, out bool audioRuns);
                        int handler = K1Handler(k1Publication, timers, bank, audioRuns,
                            game.NmiAudioTimingSupported ? game.LastNmiAudioServiceCycles : -1);
                        if (game.LastInputHookCycles < 0) throw new InvalidOperationException("K1 native input hook unsupported");
                        if ((game.Ppu.Ctrl & 0x80) == 0) throw new InvalidOperationException("K1 next NMI disabled");
                        int body = InputTiming.ResumeCallCycles + game.LastInputHookCycles + InputTiming.ResumeEpilogueCycles + pass.Cycles;
                        int delay = K1MaxDispatchDelay(handler);
                        if (k1Offset < 0) k1Offset = delay - handler;
                        else if (delay - handler != k1Offset) throw new InvalidOperationException("K1 dispatch delay is not handler-linear");
                        int slack = K1Slack(delay, body, k1Interval);
                        k1Evaluated++;
                        if (slack < 0) k1Failed++;
                        k1MaxBody = Math.Max(k1MaxBody, body);
                        k1MaxHandler = Math.Max(k1MaxHandler, handler);
                        k1MinAllowedBody = Math.Min(k1MinAllowedBody, slack + body);
                        if (slack < k1MinSlack)
                        {
                            k1MinSlack = slack; k1MinPoll = poll; k1MinDelay = delay; k1MinBody = body;
                            k1MinPublication = k1Publication; k1MinTimers = timers; k1MinBank = bank;
                        }
                        k1Rows.Append(k1Rows.Length == 0 ? "" : ",").Append('[').Append(poll).Append(',').Append(pass.WaitCount != 0 ? 1 : 0)
                            .Append(',').Append(k1Publication).Append(',').Append(timers).Append(',').Append(bank)
                            .Append(',').Append(game.LastNmiAudioServiceCycles).Append(',').Append(handler).Append(',').Append(delay)
                            .Append(',').Append(game.LastInputHookCycles).Append(',').Append(pass.Cycles).Append(',').Append(body)
                            .Append(',').Append(slack).Append(']');
                    }
                    catch (InvalidOperationException exception) { k1Reason = exception.Message; }
                }
                if (k1Reason != null) k1Unsupported.Add(poll + ":" + k1Reason);
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
        int k1Controls = 0;
        if (k1Evaluated > 0)
        {
            K1Bound(k1MinDelay, k1MinBody, k1Interval);
            Reject(() => K1Bound(k1MinDelay, k1MinBody + k1MinSlack + 1, k1Interval), "K1 body beyond slack");
            Reject(() => K1Handler(k1MinPublication, k1MinTimers, k1MinBank, true, -1), "K1 unsupported audio");
            Reject(() => K1Handler(-1, k1MinTimers, k1MinBank, true, 0), "K1 unsupported publication");
            Reject(() => K1Handler(k1MinPublication, k1MinTimers, k1MinBank, false, 0), "K1 deferred bank-switch NMI");
            Reject(() => K1Waiting(FrameScheduler.Running, 1, 0, 0, 0), "K1 Running dispatch NMI");
            Reject(() => K1Waiting(FrameScheduler.Waiting, 2, 0, 0, 0), "K1 multi-frame wait");
            Reject(() => K1Waiting(FrameScheduler.Waiting, 1, 1, 0, 0), "K1 busy bracket");
            Reject(() => K1Waiting(FrameScheduler.Waiting, 1, 0, 1, 0), "K1 interrupted bank switch");
            k1Controls = 8;
        }
        string k1Verdict = k1Evaluated == 237 && k1Unsupported.Count == 0 && k1Failed == 0 ? "PASS" : k1Failed != 0 ? "FAIL" : "UNSUPPORTED";
        File.WriteAllText(Root + "Temp/card_k1_slack.json",
            "{\n  \"verdict\": \"" + k1Verdict + "\",\n  \"evaluated\": " + k1Evaluated + ",\n  \"failed\": " + k1Failed
            + ",\n  \"unsupported\": [" + string.Join(",", k1Unsupported.ConvertAll(value => "\"" + value.Replace("\"", "'") + "\"")) + "]"
            + ",\n  \"minimumInterval\": " + k1Interval + ",\n  \"commitToIdle\": " + FrameScheduler.CommitToIdleCycles
            + ",\n  \"dispatchDelayMinusHandler\": " + k1Offset + ",\n  \"minSlack\": " + (k1Evaluated > 0 ? k1MinSlack : 0)
            + ",\n  \"minSlackPoll\": " + k1MinPoll + ",\n  \"maxBody\": " + k1MaxBody + ",\n  \"maxHandler\": " + k1MaxHandler
            + ",\n  \"minAllowedBody\": " + (k1Evaluated > 0 ? k1MinAllowedBody : 0) + ",\n  \"negativeControls\": " + k1Controls
            + ",\n  \"cardFieldComparison\": \"" + (fieldMismatches.Count == 0 ? "PASS" : "FAIL " + fieldMismatches.Count + " calls, first " + fieldMismatches[0]) + "\""
            + ",\n  \"guards\": \"previous poll native Card commit Waiting/1, bracket0, bank switch complete; NMI enabled at both NMIs; $1C/$1D structurally absent in native Card paths; slots1-3 ASSUMED Free (no native Mission owner)\""
            + ",\n  \"scope\": \"Native Core on 164819Z input 1..14300, 237 Card resume calls. Publication max over DMA parity, audio/hook/CardPass from native objects; source clocks/RAM not used. Pure FEFF yields, releases and entry frames not evaluated.\""
            + ",\n  \"columns\": [\"poll\",\"terminal\",\"publication\",\"timers\",\"bank\",\"audio\",\"handler\",\"maxDispatchAfterThreshold\",\"inputHook\",\"cardPass\",\"body\",\"slack\"]"
            + ",\n  \"rows\": [" + k1Rows + "]\n}\n");
        if (fieldMismatches.Count != 0)
            throw new InvalidOperationException(fieldMismatches[0] + "; mismatched calls=" + fieldMismatches.Count
                + "; K1 " + k1Verdict + " evaluated=" + k1Evaluated + " minSlack=" + (k1Evaluated > 0 ? k1MinSlack : 0) + " (Temp/card_k1_slack.json)");
        if (k1Verdict != "PASS")
            throw new InvalidOperationException("K1 " + k1Verdict + " evaluated=" + k1Evaluated + " failed=" + k1Failed
                + " unsupported=" + k1Unsupported.Count + (k1Unsupported.Count > 0 ? " first " + k1Unsupported[0] : ""));
        return $"PASS actual reset-input Card calls={checkedPolls.Count}; endMappedBytes={endFields}; resumeEntryMappedBytes={resumeEntryFields}; sourceExactResumePrices={prices}; committed95/9A/97=720; negativeFields={negativeFields}, negativePrices={negativePrices}, negativeControls={negativeControls}. No source RAM/state seeding; 10x16 actor fields plus256shadow only, not whole RAM/RGB/audio. First3 BeginEntry prefixes are not fully priced or source-entry-snapshot compared; parity is checked only at completed boundaries, not a physical-clock anchor. Input prefix1..{lastPoll}; later gameplay and177pureyields/3releases are outside this focused check."
            + $" K1 PASS resume={k1Evaluated}/237 minSlack={k1MinSlack}@{k1MinPoll} (interval{k1Interval}, dispatch=handler+{k1Offset}, maxBody{k1MaxBody}, maxHandler{k1MaxHandler}, minAllowedBody{k1MinAllowedBody}); controls={k1Controls}; slots1-3 Free ASSUMED; Temp/card_k1_slack.json.";
    }
}
