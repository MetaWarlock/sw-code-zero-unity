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
            Oam = new OamDispatcher(File.ReadAllBytes(Root + "Assets/Darkwing/Data/prg_bank5.bytes"), Objects, Ppu, Player);
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
}
