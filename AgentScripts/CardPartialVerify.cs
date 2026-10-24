using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using Darkwing.Core;

public static class CardPartialVerify
{
    private const string Root = "D:/Art/SW Code Zero Unity/";

    private sealed class SourceCall
    {
        public int Poll, Offset, Cycles;
        public long Start;
        public byte Frame, Timer, World;
    }

    private sealed class Instruction
    {
        public long Cycle;
        public ushort Pc;
        public byte Accumulator, IndexY;

        public Instruction Copy() => new Instruction
        {
            Cycle = Cycle, Pc = Pc, Accumulator = Accumulator, IndexY = IndexY
        };
    }

    private static int ShadowOffset(ushort address)
    {
        switch (address)
        {
            case 0x9641:
            case 0x964D:
            case 0x9659: return 0;
            case 0x9660: return 1;
            case 0x9666: return 2;
            case 0x966C: return 3;
            default: return -1;
        }
    }

    private static void Shape(int records, int entries, int endings)
    {
        if (records == 0 || entries != checked(records * 2048) || endings != entries)
            throw new InvalidDataException("Incomplete Card partial source records");
    }

    private static void CompareShadow(OamDispatcher oam, byte[] expected, string label)
    {
        for (int address = 0; address < 256; address++)
            if (oam.ReadShadowByte(address) != expected[address])
                throw new InvalidOperationException(label + " shadow $" + (0x200 + address).ToString("X4"));
    }

    private static void CompareEnding(byte[] actual, byte[] endings, int offset)
    {
        for (int address = 0; address < 256; address++)
            if (actual[address] != endings[offset + 0x200 + address])
                throw new InvalidDataException("Card source exec does not reconstruct its ending shadow");
    }

    private static void Reject(Action action, string label)
    {
        bool refused = false;
        try { action(); }
        catch (InvalidDataException) { refused = true; }
        catch (InvalidOperationException) { refused = true; }
        if (!refused) throw new InvalidOperationException("Card partial comparator accepted " + label);
    }

    private static int Replay(SourceCall call, List<Instruction> source, byte[] entries, byte[] endings,
        byte[] bank2, byte[] bank5, bool singleInstruction, bool corruptShadow = false)
    {
        if (source.Count < 2 || source[0].Cycle != call.Start || source[0].Pc != 0xF8B1
            || source[source.Count - 1].Cycle != call.Start + call.Cycles || source[source.Count - 1].Pc != 0xFF19)
            throw new InvalidDataException("Card partial source span mismatch");
        byte Read(int address) => entries[call.Offset + address];
        if (Read(0x410) != call.Frame || Read(0x430) != call.Timer || Read(0x31) != call.World
            || Read(0x9A) != 0 || Read(0xF5) != 0 || Read(0xF6) != 0)
            throw new InvalidDataException("Card partial source entry context mismatch");
        var objects = new ObjectSystem(null);
        var actor = objects.Slots[0];
        actor.Flags = Read(0x400);
        actor.AnimFrame = call.Frame;
        actor.AnimTimer = call.Timer;
        actor.AnimNumber = Read(0x420);
        var player = new PlayerCore(EmptyTileMap.Instance)
        {
            Objects = objects, Flags = actor.Flags, State = Read(0x50)
        };
        var oam = new OamDispatcher(bank5, objects, new Ppu(null), player);
        var expected = new byte[256];
        Array.Copy(entries, call.Offset + 0x200, expected, 0, 256);
        oam.SeedShadow(expected, 0);
        var pass = new CardPassContinuation(oam, player);
        pass.BeginResume(bank2, call.World, (byte)(Read(0x95) + 1));
        CompareShadow(oam, expected, "Card entry " + call.Poll);
        int writes = 0;
        var written = new bool[160];
        for (int index = 0; index < source.Count - 1; index++)
        {
            var current = source[index];
            var next = source[index + 1];
            int elapsed = checked((int)(current.Cycle - call.Start));
            int nextElapsed = checked((int)(next.Cycle - call.Start));
            if (nextElapsed <= elapsed || pass.Cycles != elapsed || pass.InstructionPc != current.Pc)
                throw new InvalidDataException("Card partial missing, extra or reordered instruction " + call.Poll);
            int part = ShadowOffset(current.Pc);
            if (part >= 0 && nextElapsed - elapsed != 5)
                throw new InvalidDataException("Card source STA price mismatch");
            CompareShadow(oam, expected, "Card before $" + current.Pc.ToString("X4"));
            pass.AdvanceTo(singleInstruction ? pass.Cycles + 1 : nextElapsed);
            if (pass.Cycles != nextElapsed || pass.InstructionPc != next.Pc)
                throw new InvalidOperationException("Card partial instruction boundary " + call.Poll + "/$" + current.Pc.ToString("X4"));
            if (part >= 0)
            {
                int address = current.IndexY + part;
                if (address >= written.Length) throw new InvalidDataException("Card blit outside 40 records");
                expected[address] = current.Accumulator;
                written[address] = true;
                writes++;
                if (corruptShadow && writes == 1)
                    oam.WriteShadowByte(address, (byte)(oam.ReadShadowByte(address) ^ 1));
            }
            CompareShadow(oam, expected, "Card after $" + current.Pc.ToString("X4"));
        }
        if (call.Frame == 0x0F)
            foreach (bool present in written)
                if (!present) throw new InvalidDataException("Missing Card source blit write");
        CompareEnding(expected, endings, call.Offset);
        if (pass.Active || pass.Stage != CardPassContinuation.Phase.Committed || pass.ProcessState != FrameScheduler.Waiting
            || pass.ProcessTimer != 1 || pass.WaitCount != 60 || pass.Busy || oam.WalkCycles != 0)
            throw new InvalidOperationException("Card partial terminal yield mismatch");
        if (actor.Flags != Read(0x400) || actor.AnimFrame != call.Frame || actor.AnimTimer != call.Timer
            || actor.AnimNumber != Read(0x420) || player.Flags != actor.Flags || player.State != Read(0x50))
            throw new InvalidOperationException("Card partial changed untouched player fields");
        pass.Finish();
        if (pass.Cycles != call.Cycles) throw new InvalidOperationException("Card partial finish repeated execution");
        return writes;
    }

    public static string Run()
    {
        if (UnityEditor.EditorApplication.isCompiling || UnityEditor.EditorUtility.scriptCompilationFailed)
            throw new InvalidOperationException("Refuse stale Core");
        string folder = Root + "Temp/card_pass_source/";
        string[] records = File.ReadAllLines(folder + "calls.csv");
        if (records.Length < 2 || records[0] != "poll,kind,start,cycles,frame,timer,cursor,world")
            throw new InvalidDataException("Missing Card partial source calls");
        byte[] entries = File.ReadAllBytes(folder + "entry.bin");
        byte[] endings = File.ReadAllBytes(folder + "end.bin");
        Shape(records.Length - 1, entries.Length, endings.Length);
        var calls = new Dictionary<int, SourceCall>();
        var seen = new HashSet<int>();
        for (int index = 1; index < records.Length; index++)
        {
            string[] cells = records[index].Split(',');
            if (cells.Length != 8) throw new InvalidDataException("Malformed Card source call");
            int poll = int.Parse(cells[0], CultureInfo.InvariantCulture);
            if (!seen.Add(poll)) throw new InvalidDataException("Duplicate Card source call");
            byte frame = byte.Parse(cells[4], CultureInfo.InvariantCulture);
            byte timer = byte.Parse(cells[5], CultureInfo.InvariantCulture);
            if (cells[1] != "resume" || (frame != 0x11 && (frame != 0x0F || timer != 4))) continue;
            calls.Add(poll, new SourceCall
            {
                Poll = poll, Offset = (index - 1) * 2048, Start = long.Parse(cells[2], CultureInfo.InvariantCulture),
                Cycles = int.Parse(cells[3], CultureInfo.InvariantCulture), Frame = frame, Timer = timer,
                World = byte.Parse(cells[7], CultureInfo.InvariantCulture)
            });
        }
        if (calls.Count != 3) throw new InvalidDataException("Expected three terminal Card source calls");
        var traces = new Dictionary<int, List<Instruction>>();
        foreach (int poll in calls.Keys) traces.Add(poll, new List<Instruction>());
        bool header = true;
        foreach (string line in File.ReadLines(folder + "exec.csv"))
        {
            if (header)
            {
                if (line != "poll,cycle,pc,bank,a,x,y,sp") throw new InvalidDataException("Card exec schema mismatch");
                header = false;
                continue;
            }
            string[] cells = line.Split(',');
            if (cells.Length != 8) throw new InvalidDataException("Malformed Card source instruction");
            int poll = int.Parse(cells[0], CultureInfo.InvariantCulture);
            if (!traces.TryGetValue(poll, out var instructions)) continue;
            instructions.Add(new Instruction
            {
                Cycle = long.Parse(cells[1], CultureInfo.InvariantCulture), Pc = ushort.Parse(cells[2], NumberStyles.HexNumber, CultureInfo.InvariantCulture),
                Accumulator = byte.Parse(cells[4], NumberStyles.HexNumber, CultureInfo.InvariantCulture),
                IndexY = byte.Parse(cells[6], NumberStyles.HexNumber, CultureInfo.InvariantCulture)
            });
        }
        byte[] bank2 = File.ReadAllBytes(Root + "Assets/Darkwing/Data/prg_bank2.bytes");
        byte[] bank5 = File.ReadAllBytes(Root + "Assets/Darkwing/Data/prg_bank5.bytes");
        int writes = 0, instructionsChecked = 0, negatives = 0;
        foreach (var call in calls.Values)
        {
            var trace = traces[call.Poll];
            int sourceWrites = Replay(call, trace, entries, endings, bank2, bank5, false);
            int steppedWrites = Replay(call, trace, entries, endings, bank2, bank5, true);
            if (sourceWrites != steppedWrites) throw new InvalidOperationException("Card boundary mode discrepancy");
            writes += sourceWrites;
            instructionsChecked += (trace.Count - 1) * 2;
            Reject(() => Replay(call, trace, entries, endings, bank2, bank5, true, true), "shadow mutation");
            negatives++;
            int store = trace.FindIndex(instruction => ShadowOffset(instruction.Pc) >= 0);
            if (store < 0) throw new InvalidDataException("Terminal Card source has no partial blit fixture");
            var changedClock = trace.ConvertAll(instruction => instruction.Copy());
            changedClock[store + 1].Cycle++;
            Reject(() => Replay(call, changedClock, entries, endings, bank2, bank5, true), "cycle mutation");
            negatives++;
            var missing = trace.ConvertAll(instruction => instruction.Copy());
            missing.RemoveAt(store);
            Reject(() => Replay(call, missing, entries, endings, bank2, bank5, true), "missing source instruction");
            negatives++;
            var duplicate = trace.ConvertAll(instruction => instruction.Copy());
            duplicate.Insert(store, duplicate[store].Copy());
            Reject(() => Replay(call, duplicate, entries, endings, bank2, bank5, true), "extra source instruction");
            negatives++;
        }
        Reject(() => Shape(records.Length - 1, entries.Length - 1, endings.Length), "missing source byte");
        negatives++;
        Reject(() => Shape(records.Length - 1, entries.Length + 1, endings.Length), "extra source byte");
        negatives++;
        return $"PASS Card partial terminalCalls={calls.Count}; sourceBlitWrites={writes}; instructionBoundaries={instructionsChecked} in exact-boundary/single-instruction modes; shadowBytes=256 per checkpoint; negativeControls={negatives}; untouched Flags/AnimNumber/Frame/Timer/Player.State; no OAM walk. Source-seeded component fixture, not Game replay or physical anchor; frame11/world1..6 not covered.";
    }
}
