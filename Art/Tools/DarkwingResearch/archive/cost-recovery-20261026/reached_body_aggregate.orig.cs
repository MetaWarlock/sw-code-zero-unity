using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Collections.Generic;
using System.Text.Json;
using Darkwing.Core;

public static class ReachedBodyAggregate
{
    static byte[] Data(string name) => File.ReadAllBytes("Assets/Darkwing/Data/" + name + ".bytes");
    static FieldInfo Metric(object target, string name) => target.GetType().GetField("<" + name + ">k__BackingField", BindingFlags.Instance | BindingFlags.NonPublic);
    public static void Main()
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
        game.Ppu.WriteRegister(0x2000, 0x90);
        game.Ppu.WriteRegister(0x2001, 0x1E);
        var flow = new GameFlow(game, decoder);
        byte[] input = File.ReadAllBytes("Playthroughs/20261022T164819Z/input.bin");
        if (input.Length != 38886 || Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(input)).ToLowerInvariant() != "137190299728c9ea6285ebf057fa25b1efaf916b9aeb2ad6d21fa46efb7f6275")
            throw new Exception("Unexpected native input");
        int measuredTail = -1;
        string tailReason = null;
        game.Enemies.Bank1TimingStarted = () => measuredTail = ActorTailTiming.MeasureBank1AndTail(game.Objects,
            game.Player.CellGridByte, game.TileMap.World.GridAddress, game.Player.CellIndex, 0, out tailReason);
        var sourceTimes = new Dictionary<int, Dictionary<string, long>>();
        foreach (string line in File.ReadLines("Temp/human_rng17234/full_phases.csv").Skip(1))
        {
            var row = line.Split(',');
            int poll = int.Parse(row[0]);
            if (poll < 16502 || poll > 17234) continue;
            string pc = row[2];
            if (pc is not ("FECF" or "CAE2" or "CADF" or "FF19")) continue;
            if (!sourceTimes.ContainsKey(poll)) sourceTimes[poll] = new();
            if (!sourceTimes[poll].TryAdd(pc, long.Parse(row[1]))) throw new Exception("Duplicate source point " + poll + " " + pc);
        }
        var actorTimes = new Dictionary<int, Dictionary<string, long>>();
        foreach (string line in File.ReadLines("Temp/actor_prefix.csv").Skip(1))
        {
            var row = line.Split(',');
            int poll = int.Parse(row[0]);
            if (!actorTimes.ContainsKey(poll)) actorTimes[poll] = new();
            if (!actorTimes[poll].TryAdd(row[2], long.Parse(row[1]))) throw new Exception("Duplicate actor boundary");
        }
        var bank5 = File.ReadLines("Temp/human_rng17234/full_bank5.csv").Skip(1).Select(line => line.Split(','))
            .Where(row => long.Parse(row[1]) >= actorTimes[16502]["CABB"] && long.Parse(row[1]) <= actorTimes[17233]["CABB"])
            .ToDictionary(row => long.Parse(row[1]), row => int.Parse(row[2]));
        if (actorTimes.Count != 732 || bank5.Count != 732) throw new Exception("Source completeness");
        using var sourceRam = File.OpenRead("Temp/human_164819_fulla/replay1/buffers.bin");
        using var rows = new StreamWriter("Temp/reached_body_aggregate.csv");
        rows.WriteLine("poll,inputHead,bodyToOam,bank1Tail,walk,rngToYield,bodyToCommit,sourceBodyToOam,sourceWalk,sourceBodyToCommit");
        int exactPrefix = 0, exactWalk = 0, exactCommit = 0;
        for (int poll = 1; poll <= 17233; poll++)
        {
            game.AdvanceFrame(NesButtons.FromByte(input[poll - 1]));
            if (poll < 16502) continue;
            if (game.Player.PendingEvent != 0 || (sbyte)game.Player.PendingMissionFlag < 0 || game.Player.IntroCardPhase)
                throw new Exception("Nonordinary native path");
            if (measuredTail < 0) throw new Exception("Unpriced native environment " + poll + " " + tailReason);
            if (!game.TryMeasurePaletteServiceCycles(0,0,out int palette,out string reason)) throw new Exception(reason);
            sourceRam.Position = (poll - 1L) * 4384;
            var ram = new byte[2048]; sourceRam.ReadExactly(ram);
            if (ram[0x30] != 0 || ram[0xF5] != 0 || ram[0xF6] != 0) throw new Exception("Unproven source wrapper/context scope");
            int bodyPrefix = game.LastMissionHeadCycles + 6 + game.Player.LastPlayerDispatchCycles
                + 3 + 3 + 3 + 3 + game.Player.LastScrollServiceCycles
                + 6 + game.Objects.LastPlacementSpawnCycles + 3 + 3 + 3 + 2
                + game.LastZoneServiceCycles + palette
                + 6 + game.Enemies.LastBank6ScriptCycles + 2 + 6 + 55 + measuredTail;
            int expectedPrefix = checked((int)(actorTimes[poll]["CABB"] - sourceTimes[poll]["CAE2"]));
            if (bodyPrefix != expectedPrefix) throw new Exception("Prefix " + poll + " " + bodyPrefix + "/" + expectedPrefix);
            exactPrefix++;
            int walk = 6 + game.Oam.WalkCycles;
            int expectedWalk = bank5[actorTimes[poll]["CABB"]];
            if (walk != expectedWalk) throw new Exception("Walk " + poll + " " + walk + "/" + expectedWalk);
            exactWalk++;
            byte beforeE4 = (byte)((EnemyBehaviors.RngE4 << 1) | (EnemyBehaviors.RngE5 >> 7));
            byte beforeE5 = (byte)((EnemyBehaviors.RngE5 << 1) | (EnemyBehaviors.RngE6 >> 7));
            int rngToYield = 6 + 2 + 2 + 4 + 2 + 3 + 4 + 2 + 3 + 2
                + (((beforeE4 ^ beforeE5) & 2) == 0 ? 3 : 2 + 2)
                + 4 * (6 + 2 + 2) + 3 * 3 + 2 + 6 + 2 + 3;
            int bodyToCommit = bodyPrefix + walk + 8 + rngToYield + 55;
            long commit = sourceTimes[poll].GetValueOrDefault("FF19", -1);
            if (commit < 0) commit = sourceTimes[poll + 1]["FF19"] - 3002;
            int expectedCommit = checked((int)(commit - sourceTimes[poll]["CAE2"]));
            if (bodyToCommit != expectedCommit) throw new Exception("Commit " + poll + " " + bodyToCommit + "/" + expectedCommit);
            exactCommit++;
            rows.WriteLine($"{poll},{game.LastMissionResumePrefixCycles},{bodyPrefix},{measuredTail},{walk},{rngToYield},{bodyToCommit},{expectedPrefix},{expectedWalk},{expectedCommit}");
        }
        Console.WriteLine($"PASS actual native costs prefix={exactPrefix}/732 bank5={exactWalk}/732 bodyCommit={exactCommit}/732. Fixture assumptions: source30/F5/F6 zero guarded; no production phase seed or interrupt integration. Last source NMI3002 is subtracted for validation only.");
    }
}
