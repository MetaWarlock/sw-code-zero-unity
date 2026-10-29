using System;
using System.IO;
using System.Text;
using Darkwing.Core;

public static class OamWalkVerify
{
    private const string Root = "D:/Art/SW Code Zero Unity/";

    private sealed class Fixture
    {
        public readonly ObjectSystem Objects = new ObjectSystem(null);
        public readonly PlayerCore Player = new PlayerCore(EmptyTileMap.Instance);
        public readonly HudComposer Hud = new HudComposer();
        public readonly Ppu Ppu = new Ppu(null);
        public readonly OamDispatcher Oam;
        public readonly byte[] Ram = new byte[2048];

        public Fixture()
        {
            Player.Objects = Objects;
            Oam = new OamDispatcher(File.ReadAllBytes(Root + "Art/Unity/CodeZero/Darkwing/Data/prg_bank5.bytes"), Objects, Ppu, Player);
            Oam.SetHud(Hud);
            Oam.MapRamPartBlock(0, Ram);
        }

        public void Load(byte[] source, int offset)
        {
            Array.Copy(source, offset, Ram, 0, 2048);
            Oam.SeedShadow(Ram, 0x200);
            Player.ScrollX = (ushort)(Ram[0xFC] | Ram[0xFD] << 8);
            Player.ScrollY = Ram[0xFA];
            Player.ScrollYRow = Ram[0xFB];
            Player.InvulnFrames = Ram[0x7C];
            Player.State = Ram[0x50];
            Player.ScrollStep = Ram[0x10];
            Array.Copy(Ram, 0x5B0, Hud.Param, 0, Hud.Param.Length);
            for (int slot = 0; slot < 16; slot++)
            {
                var state = Objects.Slots[slot];
                state.Flags = Ram[0x400 + slot];
                state.AnimFrame = Ram[0x410 + slot];
                state.AnimNumber = Ram[0x420 + slot];
                state.AnimTimer = Ram[0x430 + slot];
                state.ScreenY = Ram[0x440 + slot];
                state.ScreenX = Ram[0x450 + slot];
                state.HitFlash = Ram[0x4C0 + slot];
                state.StateLow = (byte)(Ram[0x4D0 + slot] & 0x7F);
                state.Active = (Ram[0x4D0 + slot] & 0x80) != 0;
                state.BehaviorParam = Ram[0x570 + slot];
                state.PlacementIndex = Ram[0x590 + slot];
                state.X = (ushort)(Ram[0x4F0 + slot] | Ram[0x500 + slot] << 8);
                state.Y = (ushort)(Ram[0x510 + slot] | Ram[0x520 + slot] << 8);
            }
        }

        public byte[] Read()
        {
            var result = (byte[])Ram.Clone();
            result[0x10] = Player.ScrollStep;
            result[0x50] = Player.State;
            result[0x7C] = Player.InvulnFrames;
            result[0x95] = Oam.WalkParity;
            result[0x96] = (byte)Oam.WalkSlotIndex;
            result[0x97] = (byte)Oam.OamCursorBytes;
            for (int slot = 0; slot < 16; slot++)
            {
                var state = Objects.Slots[slot];
                result[0x400 + slot] = state.Flags;
                result[0x410 + slot] = state.AnimFrame;
                result[0x430 + slot] = state.AnimTimer;
                result[0x440 + slot] = state.ScreenY;
                result[0x450 + slot] = state.ScreenX;
                result[0x4C0 + slot] = state.HitFlash;
                result[0x4D0 + slot] = (byte)((state.Active ? 128 : 0) | (state.StateLow & 127));
                result[0x570 + slot] = state.BehaviorParam;
                result[0x590 + slot] = state.PlacementIndex;
            }
            Oam.PublishToOam();
            Array.Copy(Ppu.Oam, 0, result, 0x200, 256);
            return result;
        }
    }

    private static void Compare(byte[] actual, byte[] expected, int offset, string label)
    {
        int[] singles = { 0x10, 0x50, 0x7C, 0x95, 0x96, 0x97 };
        foreach (int address in singles)
            if (actual[address] != expected[offset + address]) throw new InvalidOperationException(label + " $" + address.ToString("X4") + " " + actual[address] + "/" + expected[offset + address]);
        int[] bases = { 0x400, 0x410, 0x430, 0x440, 0x450, 0x4C0, 0x4D0, 0x570, 0x590 };
        foreach (int start in bases)
            for (int slot = 0; slot < 16; slot++)
                if (actual[start + slot] != expected[offset + start + slot]) throw new InvalidOperationException(label + " $" + (start + slot).ToString("X4") + " " + actual[start + slot] + "/" + expected[offset + start + slot]);
        for (int address = 0x200; address < 0x300; address++)
            if (actual[address] != expected[offset + address]) throw new InvalidOperationException(label + " shadow $" + address.ToString("X4") + " " + actual[address] + "/" + expected[offset + address]);
    }

    public static string CardPass()
    {
        if (UnityEditor.EditorApplication.isCompiling || UnityEditor.EditorUtility.scriptCompilationFailed)
            throw new InvalidOperationException("Refuse stale Core");
        string source = Root + "Art/Temp/card_pass_source/";
        string[] records = File.ReadAllLines(source + "calls.csv");
        byte[] entries = File.ReadAllBytes(source + "entry.bin");
        byte[] endings = File.ReadAllBytes(source + "end.bin");
        if (records.Length < 2 || entries.Length != (records.Length - 1) * 2048 || endings.Length != entries.Length)
            throw new InvalidDataException("Incomplete Card F89A/F8B1 fixtures");
        byte[] bank2 = File.ReadAllBytes(Root + "Art/Unity/CodeZero/Darkwing/Data/prg_bank2.bytes");
        int exact = 0, terminals = 0, frameEndings = 0, negatives = 0, splits = 0, duplicateGuards = 0, waitReuse = 0;
        var seen = new System.Collections.Generic.HashSet<string>();
        for (int index = 1; index < records.Length; index++)
        {
            string[] record = records[index].Split(',');
            if (!seen.Add(record[0])) throw new InvalidDataException("Duplicate Card body poll");
            int offset = (index - 1) * 2048;
            int expected = int.Parse(record[3]);
            var fixture = new Fixture();
            fixture.Load(entries, offset);
            var pass = new CardPassContinuation(fixture.Oam, fixture.Player);
            byte world = byte.Parse(record[7]);
            bool terminal = record[1] == "resume" && (int.Parse(record[4]) == 0x11
                || (int.Parse(record[4]) == 0x0F && int.Parse(record[5]) == 4));
            void Begin(CardPassContinuation continuation, Fixture state)
            {
                if (record[1] == "frame") continuation.BeginFrame((byte)(state.Ram[0x95] + 1), state.Ram[0x97], state.Ram[0x9A]);
                else if (record[1] == "resume") continuation.BeginResume(bank2, world, (byte)(state.Ram[0x95] + 1));
                else throw new InvalidDataException("Unknown Card entry kind");
            }
            Begin(pass, fixture);
            if (pass.Bracket != (record[1] == "frame" ? fixture.Ram[0x9A] : 0))
                throw new InvalidOperationException("Card entry bracket owner");
            byte[] before = fixture.Read();
            foreach (bool alternate in new[] { false, true })
            {
                bool rejected = false;
                try
                {
                    if (alternate) pass.BeginResume(bank2, world, (byte)(fixture.Ram[0x95] + 2));
                    else pass.BeginFrame((byte)(fixture.Ram[0x95] + 2), 4, 7);
                }
                catch (InvalidOperationException) { rejected = true; }
                if (!rejected || pass.Cycles != 0) throw new InvalidOperationException("Card duplicate start guard");
                duplicateGuards++;
            }
            pass.Finish();
            if (pass.Cycles != expected || pass.Active || pass.ProcessState != FrameScheduler.Waiting
                || pass.ProcessTimer != 1 || pass.Busy || pass.SwitchDepth != 0 || pass.InstructionPc != 0xFF19)
                throw new InvalidOperationException("Card source cost/commit " + record[0] + " " + pass.Cycles + "/" + expected);
            if (terminal)
            {
                if (pass.WaitCount != 0x3C || fixture.Oam.WalkCycles != 0) throw new InvalidOperationException("Card FEFF entry");
                byte[] after = fixture.Read();
                int[] stable = { 0x10, 0x50, 0x7C, 0x95, 0x96 };
                foreach (int address in stable)
                    if (before[address] != after[address]) throw new InvalidOperationException("Card terminal changed retained register");
                for (int address = 0x400; address < 0x5A0; address++)
                    if (before[address] != after[address]) throw new InvalidOperationException("Card terminal changed actor state");
                terminals++;
            }
            else
            {
                Compare(fixture.Read(), endings, offset, "Card engine " + record[0]);
                frameEndings++;
            }
            for (int shadow = 0; shadow < 256; shadow++)
                if (fixture.Oam.ReadShadowByte(shadow) != endings[offset + 0x200 + shadow])
                    throw new InvalidOperationException("Card shadow " + record[0] + "/" + shadow);
            exact++;
            if (pass.Cycles != expected + 1) negatives++;
            int splitCount = terminal ? 61 : 5;
            for (int split = 0; split < splitCount; split++)
            {
                var resumedFixture = new Fixture();
                resumedFixture.Load(entries, offset);
                var resumed = new CardPassContinuation(resumedFixture.Oam, resumedFixture.Player);
                Begin(resumed, resumedFixture);
                resumed.AdvanceTo(expected * split / splitCount);
                if (resumed.ProcessState == FrameScheduler.Waiting && resumed.Active)
                    throw new InvalidOperationException("Card premature commit");
                resumed.Finish();
                resumed.Finish();
                if (resumed.Cycles != expected || resumed.Stage != CardPassContinuation.Phase.Committed)
                    throw new InvalidOperationException("Card split/idempotence");
                for (int shadow = 0; shadow < 256; shadow++)
                    if (resumedFixture.Oam.ReadShadowByte(shadow) != fixture.Oam.ReadShadowByte(shadow))
                        throw new InvalidOperationException("Card split shadow");
                splits++;
            }
            if (terminal)
            {
                pass.BeginFrame((byte)(fixture.Ram[0x95] + 1), 0xA0, 0);
                if (pass.WaitCount != 0) throw new InvalidOperationException("Card stale wait count");
                pass.Finish();
                waitReuse++;
            }
        }
        if (terminals != 3 || frameEndings == 0) throw new InvalidDataException("Incomplete Card branch coverage");
        var deferredFixture = new Fixture();
        deferredFixture.Load(entries, 0);
        var deferred = new CardPassContinuation(deferredFixture.Oam, deferredFixture.Player);
        deferred.BeginFrame((byte)(deferredFixture.Ram[0x95] + 1), 0xA0, deferredFixture.Ram[0x9A]);
        while (deferred.SwitchDepth == 0) deferred.AdvanceTo(deferred.Cycles + 1);
        deferred.DeferredAudio = 1;
        bool refused = false;
        try { deferred.Finish(); }
        catch (NotSupportedException) { refused = true; }
        if (!refused || deferred.Active || deferred.Unsupported == null || deferred.ProcessState == FrameScheduler.Waiting)
            throw new InvalidOperationException("Unowned deferred audio falsely committed");
        return $"PASS Card sourcecosts={exact}, frame406byteEndings={frameEndings}, terminal256byteEndings={terminals}, clockNegatives={negatives}, splits={splits}, duplicateGuards={duplicateGuards}, waitReuse={waitReuse}, deferredUnsupportedControl; component only, not physical anchor";
    }

    public static string Run(string prefix)
    {
        if (UnityEditor.EditorApplication.isCompiling || UnityEditor.EditorUtility.scriptCompilationFailed)
            throw new InvalidOperationException("Refuse stale Core");
        string source = Path.Combine(Root, prefix);
        var entries = File.ReadAllBytes(source + ".bin");
        var records = File.ReadAllLines(source + ".csv");
        if (records.Length < 2 || records[0] != "poll,start,cycles,nmi,engine" || entries.Length != (records.Length - 1) * 2048)
            throw new InvalidDataException("Incomplete OAM snapshots");
        var endings = File.Exists(source + "_end.bin") ? File.ReadAllBytes(source + "_end.bin") : null;
        if (endings != null && endings.Length != entries.Length) throw new InvalidDataException("Incomplete OAM endings");
        var nmiRecords = File.Exists(source + "_nmi.csv") ? File.ReadAllLines(source + "_nmi.csv") : new string[1];
        var nmiStates = File.Exists(source + "_nmi.bin") ? File.ReadAllBytes(source + "_nmi.bin") : new byte[0];
        if (nmiStates.Length != (nmiRecords.Length - 1) * 2048) throw new InvalidDataException("Incomplete NMI states");
        var fixture = new Fixture();
        int matched = 0, partial = 0, finalStates = 0, rejected = 0;
        var failures = new StringBuilder();
        for (int index = 1; index < records.Length; index++)
        {
            var row = records[index].Split(',');
            if (row[4] != "5") throw new NotSupportedException("Bank4 walk is not implemented here");
            int offset = (index - 1) * 2048;
            fixture.Load(entries, offset);
            byte parity = (byte)(fixture.Ram[0x95] + 1);
            int prediction = fixture.Oam.MeasureFrameCycles(parity);
            fixture.Oam.BeginWalk(parity, fixture.Ram[0x97], fixture.Ram[0x96]);
            fixture.Oam.AdvanceWalkTo(0);
            Compare(fixture.Read(), entries, offset, "entry " + row[0]);
            fixture.Oam.FinishWalk();
            int expected = int.Parse(row[2]) - 6;
            if (fixture.Oam.WalkCycles != expected || fixture.Oam.WalkCycles != prediction)
                failures.AppendLine(row[0] + " cycles source/predicted/native " + expected + "/" + prediction + "/" + fixture.Oam.WalkCycles);
            else matched++;
            if (endings != null)
            {
                Compare(fixture.Read(), endings, offset, "end " + row[0]);
                finalStates++;
            }
            var full = fixture.Read();
            if (index == 1)
            {
                var addresses = new System.Collections.Generic.List<int> { 0x10, 0x50, 0x7C, 0x95, 0x96, 0x97 };
                foreach (int start in new[] { 0x400, 0x410, 0x430, 0x440, 0x450, 0x4C0, 0x4D0, 0x570, 0x590 })
                    for (int slot = 0; slot < 16; slot++) addresses.Add(start + slot);
                for (int address = 0x200; address < 0x300; address++) addresses.Add(address);
                foreach (int address in addresses)
                {
                    var broken = (byte[])full.Clone();
                    broken[address] ^= 1;
                    bool failed = false;
                    try { Compare(broken, full, 0, "negative"); } catch (InvalidOperationException) { failed = true; }
                    if (!failed) throw new InvalidOperationException("Relevant mismatch accepted: " + address);
                    rejected++;
                }
            }
            fixture.Oam.FinishWalk();
            Compare(fixture.Read(), full, 0, "finish twice " + row[0]);
            fixture.Load(entries, offset);
            fixture.Oam.BeginWalk(parity, fixture.Ram[0x97], fixture.Ram[0x96]);
            bool refused = false;
            try { fixture.Oam.BeginWalk(parity); } catch (InvalidOperationException) { refused = true; }
            if (!refused) throw new InvalidOperationException("Duplicate walk accepted");
            for (int deadline = 1; deadline <= expected + 137; deadline += 137) fixture.Oam.AdvanceWalkTo(deadline);
            fixture.Oam.FinishWalk();
            Compare(fixture.Read(), full, 0, "split " + row[0]);
            for (int nmiIndex = 1; nmiIndex < nmiRecords.Length; nmiIndex++)
            {
                var nmiRow = nmiRecords[nmiIndex].Split(',');
                if (nmiRow[1] != row[1]) continue;
                fixture.Load(entries, offset);
                fixture.Oam.BeginWalk(parity, fixture.Ram[0x97], fixture.Ram[0x96]);
                int elapsed = int.Parse(nmiRow[2]);
                fixture.Oam.AdvanceWalkTo(elapsed);
                if (fixture.Oam.WalkCycles != elapsed || !fixture.Oam.WalkActive) throw new InvalidOperationException("NMI boundary not reached");
                Compare(fixture.Read(), nmiStates, (nmiIndex - 1) * 2048, "NMI " + nmiRow[0]);
                fixture.Oam.FinishWalk();
                Compare(fixture.Read(), full, 0, "resume " + nmiRow[0]);
                partial++;
            }
        }
        if (partial != nmiRecords.Length - 1) throw new InvalidDataException("Missing, duplicated or extra NMI checkpoint");
        if (failures.Length != 0) throw new InvalidOperationException(failures.ToString());
        return "PASS cycles=" + matched + " source-endings=" + finalStates + " NMI-resume=" + partial + " rejected-mismatches=" + rejected + "; split/idempotent/duplicate-start controls";
    }

    public static string MissionTail()
    {
        if (UnityEditor.EditorApplication.isCompiling || UnityEditor.EditorUtility.scriptCompilationFailed)
            throw new InvalidOperationException("Refuse stale Core");
        string prefix = Root + "Art/Temp/human_rng17234/walk";
        var entries = File.ReadAllBytes(prefix + ".bin");
        var endings = File.ReadAllBytes(prefix + "_end.bin");
        var records = File.ReadAllLines(prefix + ".csv");
        var nmiRecords = File.ReadAllLines(prefix + "_nmi.csv");
        var nmiStates = File.ReadAllBytes(prefix + "_nmi.bin");
        if (records.Length != 76 || entries.Length != 75 * 2048 || endings.Length != entries.Length
            || nmiRecords.Length != 2 || nmiStates.Length != 2048)
            throw new InvalidDataException("Incomplete mission-tail fixtures");
        int matched = 0, partial = 0, phaseChecks = 0;
        var fixture = new Fixture();
        var tail = new MissionPassContinuation(fixture.Oam, fixture.Player);
        for (int index = 1; index < records.Length; index++)
        {
            var row = records[index].Split(',');
            int offset = (index - 1) * 2048;
            fixture.Load(entries, offset);
            if (fixture.Ram[0x97] != 4) throw new NotSupportedException("Not an ordinary CABB tail");
            byte parity = (byte)(fixture.Ram[0x95] + 1);
            int rngCycles = 95 + (((fixture.Ram[0xE4] ^ fixture.Ram[0xE5]) & 2) == 0 ? 0 : 1);
            int engineCycles = int.Parse(row[2]);
            int expectedCycles = engineCycles + 8 + rngCycles + 55;
            var expectedRng = new byte[4];
            fixture.Oam.MissionCardHold = true;
            fixture.Oam.ReservedCursor = 0x20;
            SeedRng(fixture.Ram);
            EnemyBehaviors.AdvanceRng();
            StoreRng(expectedRng);
            SeedRng(fixture.Ram);
            tail.Begin(parity);
            bool duplicateRejected = false;
            try { tail.Begin(parity); } catch (InvalidOperationException) { duplicateRejected = true; }
            if (!duplicateRejected) throw new InvalidOperationException("Duplicate mission tail accepted");
            for (int deadline = 0; deadline < engineCycles; deadline += 137) tail.AdvanceTo(deadline);
            tail.AdvanceTo(engineCycles);
            if (tail.Cycles != engineCycles || tail.InstructionPc != 0xCABE)
                throw new InvalidOperationException("CABE boundary not reached");
            Compare(fixture.Read(), endings, offset, "mission source OAM end " + row[0]);
            tail.Finish();
            if (tail.Cycles != expectedCycles || tail.Active || tail.Busy || tail.ProcessState != FrameScheduler.Waiting
                || tail.ProcessTimer != 1 || tail.InstructionPc != 0xFF19 || tail.Stage != MissionPassContinuation.Phase.Committed)
                throw new InvalidOperationException("Mission tail did not commit " + row[0] + " " + tail.Cycles + "/" + expectedCycles);
            if (fixture.Oam.OamCursorBytes != 4) throw new InvalidOperationException("CAC0 did not restore cursor");
            var actualRng = new byte[4]; StoreRng(actualRng);
            for (int register = 0; register < 4; register++)
                if (actualRng[register] != expectedRng[register]) throw new InvalidOperationException("Mission tail RNG " + row[0]);
            tail.Finish();
            if (tail.Cycles != expectedCycles) throw new InvalidOperationException("Repeated tail advanced time");
            matched++;
            fixture.Load(entries, offset); SeedRng(fixture.Ram);
            tail.Begin(parity);
            tail.AdvanceTo(expectedCycles - 55 - 3);
            if (!tail.Busy || tail.ProcessState != FrameScheduler.Running || tail.ProcessTimer != 0 || tail.InstructionPc != 0xCADD)
                throw new InvalidOperationException("9A cleared before STA " + row[0]);
            tail.AdvanceTo(expectedCycles - 55 - 2);
            if (tail.Busy || tail.Cycles != expectedCycles - 55 || tail.ProcessState != FrameScheduler.Running)
                throw new InvalidOperationException("9A not cleared at STA " + row[0]);
            tail.AdvanceTo(expectedCycles - 4);
            if (tail.ProcessTimer != 1 || tail.ProcessState != FrameScheduler.Running || tail.InstructionPc != 0xFF17)
                throw new InvalidOperationException("Waiting committed before FF17 " + row[0]);
            tail.AdvanceTo(expectedCycles - 3);
            if (tail.ProcessState != FrameScheduler.Waiting || tail.Cycles != expectedCycles || tail.Active)
                throw new InvalidOperationException("FF17 did not commit atomically " + row[0]);
            phaseChecks += 4;
            var nmi = nmiRecords[1].Split(',');
            if (row[1] == nmi[1])
            {
                fixture.Load(entries, offset); SeedRng(fixture.Ram);
                tail.Begin(parity);
                int deadline = int.Parse(nmi[2]) + 6;
                tail.AdvanceTo(deadline);
                if (tail.Cycles != deadline || !tail.Active || !tail.Busy || tail.Stage != MissionPassContinuation.Phase.Oam
                    || tail.ProcessState != FrameScheduler.Running || tail.ProcessTimer != 0)
                    throw new InvalidOperationException("Incorrect mission NMI suspension");
                for (int register = 0; register < 4; register++)
                    if (ReadRng(register) != fixture.Ram[0xE4 + register]) throw new InvalidOperationException("RNG advanced before OAM NMI");
                Compare(fixture.Read(), nmiStates, 0, "mission NMI " + nmi[0]);
                tail.AdvanceTo(engineCycles);
                Compare(fixture.Read(), endings, offset, "mission resume OAM end " + nmi[0]);
                tail.Finish();
                if (tail.Cycles != expectedCycles) throw new InvalidOperationException("NMI changed body cycle count");
                partial++;
            }
        }
        if (partial != 1) throw new InvalidDataException("Missing mission NMI checkpoint");
        return "PASS source OAM tails=" + matched + "/75 NMI partial/resume=" + partial + "/1 9A/timer/FF17 checks=" + phaseChecks
            + "; RNG four-byte algebra, split/duplicate/idempotence; fixture deadlines, not physical-clock integration";
    }

    private static byte ReadRng(int register) => register == 0 ? EnemyBehaviors.RngE4 : register == 1 ? EnemyBehaviors.RngE5
        : register == 2 ? EnemyBehaviors.RngE6 : EnemyBehaviors.RngE7;

    private static void StoreRng(byte[] output)
    {
        for (int register = 0; register < 4; register++) output[register] = ReadRng(register);
    }

    private static void SeedRng(byte[] ram)
    {
        EnemyBehaviors.RngE4 = ram[0xE4]; EnemyBehaviors.RngE5 = ram[0xE5];
        EnemyBehaviors.RngE6 = ram[0xE6]; EnemyBehaviors.RngE7 = ram[0xE7];
    }
}
