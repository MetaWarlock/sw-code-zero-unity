using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Collections.Generic;
using System.Text.Json;
using Darkwing.Core;

public static class ReachedBodyAggregate
{
    static string ProjectRoot = Directory.GetCurrentDirectory();
    static string Session;
    static string ReferenceDir;
    static string LegacyReferenceDir;
    static string OutputRoot;
    static string RevisionOverride;
    static string Required(string path)
    {
        path = Path.GetFullPath(path, ProjectRoot);
        if (!File.Exists(path)) throw new FileNotFoundException("Missing preserved input; see Art/Tools/DarkwingResearch/README.xhtml (do not recreate a baseline silently)", path);
        return path;
    }
    static string SessionFile(string name) => Required(Path.Combine(Session, name));
    static string ReferenceFile(string name) => Required(Path.Combine(ReferenceDir, name));
    static string LegacyFile(string name) => Required(Path.Combine(LegacyReferenceDir, name));
    static string Output(string path)
    {
        path = Path.GetFullPath(path, ProjectRoot);
        if (!path.StartsWith(ProjectRoot.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
            || !path.EndsWith(".csv", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Output must be a new CSV in the assigned project");
        if (File.Exists(path)) throw new IOException("Output exists; use a new path instead of overwriting evidence: " + path);
        Directory.CreateDirectory(Path.GetDirectoryName(path));
        return path;
    }
    static string[] Configure(string[] args)
    {
        var rest = new List<string>();
        var options = new Dictionary<string, string>();
        for (int i = 0; i < args.Length; i++)
        {
            if (!args[i].StartsWith("--")) { rest.Add(args[i]); continue; }
            if (args[i] is not ("--project-root" or "--session" or "--reference-dir" or "--legacy-reference-dir" or "--output-root" or "--revision"))
                throw new ArgumentException("Unknown option " + args[i]);
            if (i + 1 == args.Length || args[i + 1].StartsWith("--")) throw new ArgumentException("Missing value for " + args[i]);
            string key = args[i]; options.Add(key, args[++i]);
        }
        RevisionOverride = options.GetValueOrDefault("--revision");
        ProjectRoot = Path.GetFullPath(options.GetValueOrDefault("--project-root", ProjectRoot));
        if (!File.Exists(Path.Combine(ProjectRoot, "AGENTS.md")) || !Directory.Exists(Path.Combine(ProjectRoot, "Art/Unity/CodeZero/Darkwing")))
            throw new ArgumentException("Use the assigned project root (cwd or --project-root)");
        Directory.SetCurrentDirectory(ProjectRoot);
        string mode = rest.Count == 0 ? "m1" : rest[0];
        Session = Path.GetFullPath(options.GetValueOrDefault("--session", "Art/Playthroughs/" + (mode == "m1" ? "20261022T164819Z" : "20261019T212535Z")), ProjectRoot);
        ReferenceDir = Path.GetFullPath(options.GetValueOrDefault("--reference-dir", "Art/Research/DarkwingDuck/reference/cost-harness/data"), ProjectRoot);
        LegacyReferenceDir = Path.GetFullPath(options.GetValueOrDefault("--legacy-reference-dir", Path.Combine(ReferenceDir, "legacy-m1")), ProjectRoot);
        OutputRoot = Path.GetFullPath(options.GetValueOrDefault("--output-root", "Art/Temp/cost-harness"), ProjectRoot);
        if (rest.Count > 0 && mode == "m1") rest.RemoveAt(0);
        else if (rest.Count > 0 && mode is not ("city" or "clock" or "census" or "slots" or "audiodiff" or "probe" or "state09" or "state09cases" or "cardrelease" or "dispatch" or "buffers" or "bufdiff" or "bufcontrols" or "paletteprofile"))
            throw new ArgumentException("Modes: city, clock native|oracle, census, slots, audiodiff, probe, state09, state09cases, cardrelease, dispatch, buffers, bufdiff, bufcontrols, paletteprofile, m1 (legacy inputs missing)");
        return rest.ToArray();
    }
    static byte[] Data(string name) => File.ReadAllBytes("Art/Unity/CodeZero/Darkwing/Data/" + name + ".bytes");
    static FieldInfo Metric(object target, string name) => target.GetType().GetField("<" + name + ">k__BackingField", BindingFlags.Instance | BindingFlags.NonPublic);

    static (DarkwingGame game, GameFlow flow) NewGame()
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
        return (game, new GameFlow(game, decoder));
    }

    /// <summary>$CA8C→$CABB minus the bank-1 script: depends only on the player's cell and grid.</summary>
    static int EnvironmentTail(DarkwingGame game, out string reason)
        => ActorTailTiming.MeasureEnvironmentTail(game.Player.CellGridByte, game.TileMap.World.GridAddress, game.Player.CellIndex, 0, out reason);

    public static void Main(string[] args)
    {
        try { Run(args); }
        catch (Exception error)
        {
            Console.Error.WriteLine("ERROR: " + error);
            Environment.ExitCode = 2;
        }
    }

    static void Run(string[] args)
    {
        args = Configure(args);
        if (args.Length > 0 && args[0] == "city") { City(args); return; }
        if (args.Length > 0 && args[0] == "clock") { ClockRun(args); return; }
        if (args.Length > 0 && args[0] == "state09") { State09(args); return; }
        if (args.Length > 0 && args[0] == "state09cases") { State09Cases(args); return; }
        if (args.Length > 0 && args[0] == "cardrelease") { CardRelease(args); return; }
        if (args.Length > 0 && args[0] == "dispatch") { Dispatch(args); return; }
        if (args.Length > 0 && args[0] == "buffers") { Buffers(args); return; }
        if (args.Length > 0 && args[0] == "bufdiff") { BufferDiff(args); return; }
        if (args.Length > 0 && args[0] == "bufcontrols") { BufferControls(args); return; }
        if (args.Length > 0 && args[0] == "paletteprofile") { PaletteProfile(args); return; }
        if (args.Length > 0 && args[0] == "census") { Census(args); return; }
        if (args.Length > 0 && args[0] == "slots") { Slots(args); return; }
        if (args.Length > 0 && args[0] == "audiodiff") { AudioDiff(args); return; }
        if (args.Length > 0 && args[0] == "probe") { ProbeAt(args); return; }
        var (game, flow) = NewGame();
        byte[] input = File.ReadAllBytes(SessionFile("input.bin"));
        if (input.Length != 38886 || Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(input)).ToLowerInvariant() != "137190299728c9ea6285ebf057fa25b1efaf916b9aeb2ad6d21fa46efb7f6275")
            throw new Exception("Unexpected native input");
        var sourceTimes = new Dictionary<int, Dictionary<string, long>>();
        foreach (string line in File.ReadLines(LegacyFile("full_phases.csv")).Skip(1))
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
        foreach (string line in File.ReadLines(LegacyFile("actor_prefix.csv")).Skip(1))
        {
            var row = line.Split(',');
            int poll = int.Parse(row[0]);
            if (!actorTimes.ContainsKey(poll)) actorTimes[poll] = new();
            if (!actorTimes[poll].TryAdd(row[2], long.Parse(row[1]))) throw new Exception("Duplicate actor boundary");
        }
        var bank5 = File.ReadLines(LegacyFile("full_bank5.csv")).Skip(1).Select(line => line.Split(','))
            .Where(row => long.Parse(row[1]) >= actorTimes[16502]["CABB"] && long.Parse(row[1]) <= actorTimes[17233]["CABB"])
            .ToDictionary(row => long.Parse(row[1]), row => int.Parse(row[2]));
        if (actorTimes.Count != 732 || bank5.Count != 732) throw new Exception("Source completeness");
        using var sourceRam = File.OpenRead(LegacyFile("buffers.bin"));
        using var rows = new StreamWriter(Output(Path.Combine(OutputRoot, "reached_body_aggregate.csv")));
        rows.WriteLine("poll,inputHead,bodyToOam,bank1Tail,walk,rngToYield,bodyToCommit,sourceBodyToOam,sourceWalk,sourceBodyToCommit");
        int exactPrefix = 0, exactWalk = 0, exactCommit = 0;
        for (int poll = 1; poll <= 17233; poll++)
        {
            game.AdvanceFrame(NesButtons.FromByte(input[poll - 1]));
            if (poll < 16502) continue;
            if (game.Player.PendingEvent != 0 || (sbyte)game.Player.PendingMissionFlag < 0 || game.Player.IntroCardPhase)
                throw new Exception("Nonordinary native path");
            int tail = EnvironmentTail(game, out string tailReason);
            if (tail < 0 || game.Enemies.LastBank1ScriptCycles < 0) throw new Exception("Unpriced native environment " + poll + " " + tailReason);
            int measuredTail = game.Enemies.LastBank1ScriptCycles + tail;
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
            int rngToYield = RngToYield();
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

    /// <summary>$CAD8→$CADF from the native E4/E5 after the walk (the RNG has already rotated once).</summary>
    static int RngToYield()
    {
        byte beforeE4 = (byte)((EnemyBehaviors.RngE4 << 1) | (EnemyBehaviors.RngE5 >> 7));
        byte beforeE5 = (byte)((EnemyBehaviors.RngE5 << 1) | (EnemyBehaviors.RngE6 >> 7));
        return 6 + 2 + 2 + 4 + 2 + 3 + 4 + 2 + 3 + 2
            + (((beforeE4 ^ beforeE5) & 2) == 0 ? 3 : 2 + 2)
            + 4 * (6 + 2 + 2) + 3 * 3 + 2 + 6 + 2 + 3;
    }

    /// <summary>City coverage (research only): every native per-frame price next to the original
    /// interval of the same poll. No source value is fed to the game.</summary>
    static void City(string[] args)
    {
        int first = args.Length > 1 ? int.Parse(args[1]) : 1924;
        // first==0: record every poll from power-on (thresholds only matter there)
        int last = args.Length > 2 ? int.Parse(args[2]) : 6400;
        string output = Output(args.Length > 3 ? args[3] : Path.Combine(OutputRoot, "city_cost_coverage.csv"));
        var (game, flow) = NewGame();
        byte[] input = File.ReadAllBytes(SessionFile("input.bin"));
        if (input.Length != 29360 || Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(input)).ToLowerInvariant() != "510925941e2397b6d86aae459cbd4dde291a6574963a01b104f5f7d3b88a9100")
            throw new Exception("Unexpected city input");
        var head = Metric(game, "LastMissionHeadCycles");
        var audioAddresses = new[] { 0xC0, 0xC7, 0xC8, 0xC9, 0xCA, 0xCB, 0xCC, 0xCD, 0xCE, 0xCF, 0xD0, 0xD1, 0xD2, 0xD3, 0xD4, 0xD5, 0xD6, 0xD7, 0xD8 };
        var audioType = game.Audio.GetType();
        var audioFields = audioAddresses.Select(a => audioType.GetField("_" + a.ToString("x2"), BindingFlags.Instance | BindingFlags.NonPublic)).ToArray();
        var page7 = (byte[])audioType.GetField("_p7", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(game.Audio);
        using var original = File.OpenRead(SessionFile("replay1/buffers.bin"));
        var ram = new byte[2048];
        using var rows = new StreamWriter(output);
        rows.WriteLine("poll,T,skip,resetEdge,resetStage,audioState,stage,head,hook,player,playerWhy,scroll,scrollWhy,spawn,spawnWhy,zone,zoneWhy,palette,paletteWhy,bank6,bank6Why,bank1,bank1Why,tail,tailWhy,walk,bank4,rng,pub0,pub1,pubWhy,timers,audio,blank,cell,world,audioLine,part0,part1");
        for (int poll = 1; poll <= last; poll++)
        {
            head.SetValue(game, int.MinValue);
            string audioState = "";
            if (poll >= first)
            {
                original.Position = (poll - 1L) * 4384; original.ReadExactly(ram);
                for (int index = 0; index < audioAddresses.Length && audioState == ""; index++)
                    if ((byte)audioFields[index].GetValue(game.Audio) != ram[audioAddresses[index]]) audioState = audioAddresses[index].ToString("X2");
                for (int index = 0; index < 128 && audioState == ""; index++)
                    if (page7[index] != ram[0x700 + index]) audioState = (0x700 + index).ToString("X3");
                if (audioState == "") audioState = "=";
            }
            long threshold = game.PhysicalFrames?.EdgeMaster ?? -1;
            string resetEdge = flow.ResetClock == null ? "" : flow.ResetClock.PhysicalEdgeMaster + "," + flow.ResetClock.Stage;
            if (resetEdge == "") resetEdge = ",";
            int pub0 = -1, pub1 = -1; string pubWhy = null;
            if (poll >= first)
            {
                if (!game.TryMeasureNativeNmiPublication(0, 0, 0, 0, out pub0, out pubWhy)) pub0 = -1;
                if (!game.TryMeasureNativeNmiPublication(1, 0, 0, 0, out pub1, out _)) pub1 = -1;
            }
            game.AdvanceFrame(NesButtons.FromByte(input[poll - 1]));
            if (poll < first) continue;
            bool ran = game.LastMissionHeadCycles != int.MinValue;
            string P(string s) => s == null ? "" : s.Replace(',', ';');
            if (!ran) { rows.WriteLine($"{poll},{threshold},{(game.PhysicalFrames?.LastSkipped == true ? 1 : 0)},{resetEdge},{audioState},{flow.Stage},,,,,,,,,,,,,,,,,,,,,,{pub0},{pub1},{P(pubWhy)},,{game.LastNmiAudioServiceCycles},{game.BlankDepth},,"); continue; }
            int palette = game.TryMeasurePaletteServiceCycles(0, 0, out int pal, out string palWhy) ? pal : -1;
            int tail = game.TileMap != null ? EnvironmentTail(game, out string tailWhy) : -1;
            tailWhy = null;
            if (tail < 0 && game.TileMap != null) EnvironmentTail(game, out tailWhy);
            int timers = NmiTiming.ProcessTimerCycles(new byte[] { FrameScheduler.Waiting, 0, 0, 0 }, new byte[] { 1, 0, 0, 0 });
            byte cell = game.TileMap != null ? game.TileMap.GridByte(game.Player.CellIndex) : (byte)0;
            rows.WriteLine(string.Join(",", poll, threshold, game.PhysicalFrames?.LastSkipped == true ? 1 : 0, resetEdge, audioState, flow.Stage, game.LastMissionHeadCycles, game.LastInputHookCycles,
                game.Player.LastPlayerDispatchCycles, P(game.Player.PlayerDispatchTimingUnsupported),
                game.Player.LastScrollServiceCycles, P(game.Player.ScrollTimingUnsupported),
                game.Objects.LastPlacementSpawnCycles, P(game.Objects.PlacementSpawnTimingUnsupportedReason),
                game.LastZoneServiceCycles, P(game.ZoneTimingUnsupported), palette, P(palWhy),
                game.Enemies.LastBank6ScriptCycles, P(game.Enemies.Bank6TimingUnsupported),
                game.Enemies.LastBank1ScriptCycles, P(game.Enemies.Bank1TimingUnsupported), tail, P(tailWhy),
                6 + game.Oam.WalkCycles, game.Oam.UseBank4Tables ? 1 : 0, RngToYield(), pub0, pub1, P(pubWhy), timers,
                game.LastNmiAudioServiceCycles, game.BlankDepth, cell.ToString("X2"), game.TileMap?.World.Id, game.Audio.TimingUnsupportedLine,
                game.Player.TimingParts[0], game.Player.TimingParts[1]));
        }
        Console.WriteLine("city coverage written " + output);
    }


    static readonly FieldInfo BandTable = typeof(LevelTileMap).GetField("_bandTable", BindingFlags.Instance | BindingFlags.NonPublic);

    /// <summary>Copy of Art/AgentScripts/StandRoute.cs record mapping (RAM fields + NT2048 + OAM + PAL),
    /// so the existing stand.py comparator reads a dotnet Core run.</summary>
    static void WriteStandRecord(DarkwingGame game, GameFlow flow, byte[] ram, BinaryWriter buffers)
    {
        var p = game.Player;
        ram[0x50]=p.State;ram[0x51]=p.StateTimer;ram[0x95]=game.FrameParity;
        ram[0x14]=game.PressedP1;ram[0x16]=game.HeldP1;
        ram[0x52]=p.DirBits;ram[0x56]=p.AirSubState;ram[0x7C]=p.InvulnFrames;
        ram[0x53]=p.WeaponActive;ram[0x54]=p.WeaponSelected;ram[0x5F]=p.GasActive;
        ram[0x60]=p.GasEnergyHi;ram[0x61]=p.GasEnergyLo;ram[0x98]=p.GasToggle;
        ram[0x9B]=p.DeathSoundGate;
        ram[0x3B]=p.IntroHoldFlag;ram[0xED]=BossBehaviors.InvulnTimer;ram[0xEE]=BossBehaviors.DamageAccum;
        ram[0xEF]=(byte)BossBehaviors.ProjectileSlot;
        ram[0x31]=flow.World.CurrentWorld;ram[0x38]=flow.World.CompletedCount;ram[0x69]=flow.World.CompletedMask;
        ram[0x6B]=p.ScoreLifeState;ram[0xFC]=(byte)p.ScrollX;ram[0xFD]=(byte)(p.ScrollX>>8);
        ram[0xFA]=p.ScrollY;ram[0xFB]=p.ScrollYRow;
        ram[0xE4]=EnemyBehaviors.RngE4;ram[0xE5]=EnemyBehaviors.RngE5;
        ram[0xE6]=EnemyBehaviors.RngE6;ram[0xE7]=EnemyBehaviors.RngE7;
        ram[0x25]=p.StageCursor;ram[0x22]=p.CellIndex;ram[0x23]=p.CellIndexNext;
        if (game.TileMap != null) Array.Copy((byte[])BandTable.GetValue(game.TileMap), 0, ram, 0x140, 16);
        ram[0x1F]=p.ColsToStream;ram[0x28]=p.LastRedrawScroll;ram[0x20]=p.PrevScrollMode;
        ram[0x2B]=p.ScrollState;ram[0x2F]=p.ScrollMode;
        ram[0x6D]=p.CheckpointId;ram[0x5B]=p.PendingEvent;
        for(int i=0;i<16;i++)
        {
            var s=game.Objects.Slots[i];
            bool player = i == 0;
            int x = player ? p.X : s.X, y = player ? p.Y : s.Y;
            ram[0x400 + i] = player ? game.Player.Flags : s.Flags;
            ram[0x4F0 + i] = (byte)x; ram[0x500 + i] = (byte)(x >> 8);
            ram[0x510 + i] = (byte)y; ram[0x520 + i] = (byte)(y >> 8);
            ram[0x410+i]=s.AnimFrame;ram[0x420+i]=s.AnimNumber;ram[0x430+i]=s.AnimTimer;
            ram[0x440+i]=s.ScreenY;ram[0x450+i]=s.ScreenX;
            ram[0x460+i]=i==0?p.YFrac:s.YFrac;ram[0x470+i]=i==0?p.XFrac:s.XFrac;
            ram[0x480+i]=s.T480;ram[0x490+i]=s.T490;ram[0x4A0+i]=s.T4A0;
            ram[0x4B0+i]=s.T4B0;ram[0x4C0+i]=s.HitFlash;
            ram[0x4D0+i]=(byte)((s.Active?0x80:0)|(s.StateLow&0x7F));ram[0x4E0+i]=s.Behavior;
            ram[0x530+i]=i==0?p.XVelFrac:s.XVelFrac;ram[0x540+i]=i==0?p.XVelInt:s.XVelInt;
            ram[0x550+i]=i==0?p.YVelFrac:s.YVelFrac;ram[0x560+i]=unchecked((byte)(i==0?p.YVelInt:s.YVelInt));
            ram[0x570+i]=s.BehaviorParam;ram[0x580+i]=s.Direction;ram[0x590+i]=s.PlacementIndex;ram[0x5A0+i]=s.Health;
        }
        Array.Copy(game.Hud.Param,0,ram,0x5B0,game.Hud.Param.Length);
        for(int i=0;i<5;i++)ram[0x5C4+i*2]=p.ScoreDigits[i];
        ram[0x5E4]=p.EnergyColLeft;ram[0x5E6]=p.EnergyColRight;
        buffers.Write(ram);buffers.Write(game.Ppu.NametableRam,0,2048);
        buffers.Write(game.Ppu.Oam);buffers.Write(game.Ppu.PaletteRam);
    }

    static Dictionary<int, Dictionary<string, List<long>>> SourcePoints(string path)
    {
        var points = new Dictionary<int, Dictionary<string, List<long>>>();
        foreach (string line in File.ReadLines(path).Skip(1))
        {
            var row = line.Split(',');
            int poll = int.Parse(row[0]);
            if (!points.TryGetValue(poll, out var map)) points[poll] = map = new();
            if (!map.TryGetValue(row[2], out var list)) map[row[2]] = list = new();
            list.Add(long.Parse(row[1]));
        }
        return points;
    }

    /// <summary>City replay from reset with the mission clock (research). Mode "native" leaves the
    /// game untouched; mode "oracle" replaces unsupported or differing prices with the original's
    /// (fixture only, to exercise the deadline/continuation mechanism past missing prices).</summary>
    static void ClockRun(string[] args)
    {
        string mode = args.Length > 1 ? args[1] : "native";
        if (mode is not ("native" or "oracle")) throw new ArgumentException("clock mode must be native or oracle");
        int last = args.Length > 2 ? int.Parse(args[2]) : 6420;
        string output = Output(args.Length > 3 ? args[3] : Path.Combine(OutputRoot, "city_clock_" + mode + ".csv"));
        var (game, flow) = NewGame();
        byte[] input = File.ReadAllBytes(SessionFile("input.bin"));
        if (input.Length != 29360 || Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(input)).ToLowerInvariant() != "510925941e2397b6d86aae459cbd4dde291a6574963a01b104f5f7d3b88a9100")
            throw new Exception("Unexpected city input");
        var source = SourcePoints(ReferenceFile("phases/city_phases.csv"));
        var commits = new Dictionary<int, List<long>>();
        foreach (string line in File.ReadLines(ReferenceFile("phases/city_idle_phase.csv")).Skip(1))
        {
            var row = line.Split(',');
            if (row[2] != "commit") continue;
            int poll = int.Parse(row[0]);
            if (!commits.TryGetValue(poll, out var list)) commits[poll] = list = new();
            list.Add(long.Parse(row[1]));
        }
        long Point(int poll, string pc) => source.TryGetValue(poll, out var map) && map.TryGetValue(pc, out var list) ? list[0] : -1;
        int overrides = 0, prefixOverrides = 0, audioOverrides = 0, pubOverrides = 0;
        if (mode == "oracle")
        {
            game.DiagnosticBodyPrefix = (poll, native) =>
            {
                long fecf = Point((int)poll, "FECF"), cabb = Point((int)poll, "CABB");
                if (fecf < 0 || cabb < 0) return native;
                int value = (int)(cabb - fecf);
                if (value != native) { prefixOverrides++; overrides++; }
                return value;
            };
            game.DiagnosticNmiAudio = (poll, native) =>
            {
                long dc = Point((int)poll, "C0DC"), rti = Point((int)poll, "C114");
                if (dc < 0 || rti < 0) return native;
                int value = (int)(rti + 6 - dc);
                if (value != native) { audioOverrides++; overrides++; }
                return value;
            };
            game.DiagnosticPublication = (poll, parity, native) =>
            {
                long c000 = Point((int)poll, "C000"), a1 = Point((int)poll, "C0A1");
                if (c000 < 0 || a1 < 0) return native;
                if ((c000 & 1) != parity) return native;
                int value = (int)(a1 - c000);
                if (value != native) { pubOverrides++; overrides++; }
                return value;
            };
        }
        using var original = File.OpenRead(SessionFile("replay1/buffers.bin"));
        var ram = new byte[2048];
        string bufferDir = Path.ChangeExtension(output, null);
        if (Directory.Exists(bufferDir)) throw new IOException("Buffer directory exists; use a new output path: " + bufferDir);
        Directory.CreateDirectory(bufferDir);
        using var buffers = new BinaryWriter(File.Create(Path.Combine(bufferDir, "buffers.bin")));
        var standRam = new byte[2048];
        using var rows = new StreamWriter(output);
        rows.WriteLine("poll,stage,candidates,firstCommit,lastCommit,sourceCommit,inSet,anchors,timed,running,unsupportedPoll,unsupported,e4,sourceE4,prefixNative,prefixWhy");
        string lastReason = null;
        for (int poll = 1; poll <= last; poll++)
        {
            original.Position = (poll - 1L) * 4384; original.ReadExactly(ram);
            byte e4 = EnemyBehaviors.RngE4;
            WriteStandRecord(game, flow, standRam, buffers);
            game.AdvanceFrame(NesButtons.FromByte(input[poll - 1]));
            var clock = game.Clock;
            long src = -1;
            if (commits.TryGetValue(poll, out var list)) src = list[list.Count - 1];
            string inSet = "";
            if (clock.Stage == MissionClock.Phase.Waiting && src >= 0)
                inSet = (src >= clock.FirstCommit && src <= clock.LastCommit) ? (clock.Candidates == 1 ? (clock.FirstCommit == src ? "exact" : "MISS") : "range") : "MISS";
            if (poll >= 1900 || clock.Unsupported != lastReason)
                rows.WriteLine(string.Join(",", poll, clock.Stage, clock.Candidates, clock.FirstCommit, clock.LastCommit, src, inSet,
                    clock.Anchors, clock.TimedPasses, clock.RunningPasses, clock.UnsupportedPoll, (clock.Unsupported ?? "").Replace(',', ';'),
                    e4.ToString("X2"), ram[0xE4].ToString("X2"), game.LastClockBodyPrefixCycles, (game.LastClockBodyPrefixUnsupported ?? "").Replace(',', ';')));
            lastReason = clock.Unsupported;
            if (clock.Stage == MissionClock.Phase.Unsupported && mode == "oracle") clock.ClearRefusal();
        }
        Console.WriteLine($"clock {mode} written {output}; overrides prefix={prefixOverrides} audio={audioOverrides} publication={pubOverrides}");
    }

    /// <summary>Bound through reflection so this single exporter file compiles on both the baseline
    /// revision (no state-09 measure yet) and the after revision. A missing measure is an error, never
    /// a silent pass.</summary>
    static readonly MethodInfo Measure09Method = typeof(PlayerCore).GetMethod("TryMeasureWorldEntryFall09Cycles");

    static bool Measure09(PlayerCore player, bool? queueAccepts, out int cycles, out string unsupported)
    {
        if (Measure09Method == null) throw new InvalidOperationException("TryMeasureWorldEntryFall09Cycles is absent from this revision");
        var argv = new object[] { queueAccepts, 0, null };
        bool accepted = (bool)Measure09Method.Invoke(player, argv);
        cycles = (int)argv[1]; unsupported = (string)argv[2];
        return accepted;
    }

    /// <summary>The one comparator both gates use: agreement only on a real, non-negative price.
    /// Its own negative control lives in state09 (perturbed input) and state09cases (paired
    /// fixture mutation), so a shifted expectation cannot pass it by construction.</summary>
    public static string Verdict(int actual, int expected) => actual == expected && actual >= 0 ? "exact" : "MISMATCH";

    /// <summary>ZERO-43 focused gate for $D517 (city route, polls 1941..1977). Positive: the native
    /// price equals the original CA59−CA40−18, and the same pre-frame call reproduces the price the
    /// frame itself charged (the measure is read-only). Negative: state, slot activity, landing nibble
    /// and hook bit are perturbed and restored before the frame runs, so every condition outside the
    /// slice must refuse; and the $D523 plain branch is flipped to feed the comparator an input it
    /// must reject. A shifted expectation must not pass, otherwise the gate is vacuous.</summary>
    static void State09(string[] args)
    {
        string output = Output(args.Length > 1 ? args[1] : Path.Combine(OutputRoot, "state09_gate.csv"));
        var (game, flow) = NewGame();
        byte[] input = File.ReadAllBytes(SessionFile("input.bin"));
        if (input.Length != 29360 || Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(input)).ToLowerInvariant() != "510925941e2397b6d86aae459cbd4dde291a6574963a01b104f5f7d3b88a9100")
            throw new Exception("Unexpected city input");
        var source = SourcePoints(ReferenceFile("phases/city_phases.csv"));
        var player = game.Player;
        using var rows = new StreamWriter(output);
        rows.WriteLine("poll,state,fallByte,native,oracle,verdict,perturbedInputControl,reproducible,negative,refused");
        int exact = 0, reproduced = 0, refused = 0, shiftedMiss = 0, priced = 0, queueRefusals = 0;
        for (int poll = 1; poll <= 1977; poll++)
        {
            bool gate = poll >= 1941;
            byte savedState = player.State, savedFlags = player.Flags, savedLow = player.Objects.Slots[0].StateLow;
            bool savedActive = player.Objects.Slots[0].Active;
            var slot = player.Objects.Slots[0];
            long oracle = gate ? source[poll]["CA59"][0] - source[poll]["CA40"][0] - 18 : -1;
            int preFrame = -1, perturbed = -1;
            string control = "NOT RUN";
            var negatives = new List<string>();
            if (gate)
            {
                preFrame = Measure09(player, true, out int measured, out _) ? measured : -1;
                void Control(string name, Action perturb)
                {
                    perturb();
                    bool accepted = Measure09(player, true, out _, out string why);
                    player.State = savedState; player.Flags = savedFlags; slot.StateLow = savedLow; slot.Active = savedActive;
                    negatives.Add(name + "=" + (accepted ? "ACCEPTED" : Safe(why)));
                    if (!accepted && why != null) refused++;
                }
                Control("state0B", () => player.State = 0x0B);
                Control("inactive", () => slot.Active = false);
                Control("landed", () => slot.StateLow = (byte)(savedLow | 0x01));
                Control("hookbit", () => player.Flags = (byte)(savedFlags | 0x20));
                if (Measure09(player, null, out _, out string noQueue)) negatives.Add("noQueueOwner=ACCEPTED");
                else { negatives.Add("noQueueOwner=" + Safe(noQueue)); queueRefusals++; }
                // Zero-47: a real negative control of the same comparator, replacing the old
                // "oracle + 1 != native". On this very pre-frame state one input byte is made
                // deliberately wrong in a branch-relevant way ($D523 plain <-> $D525 probe), the
                // measure runs again and Verdict must reject it against the untouched price.
                // Relevance is required too: a perturbation that leaves the price alone proves
                // nothing, so such a row is reported instead of passing.
                byte velBefore = unchecked((byte)player.YVelInt);
                player.YVelInt = unchecked((sbyte)(velBefore >= 0xFA ? 0x05 : 0xFA));
                if (Measure09(player, true, out int measuredPerturbed, out _)) perturbed = measuredPerturbed;
                player.YVelInt = unchecked((sbyte)velBefore);
                control = Verdict(perturbed, preFrame);
                if (control == "MISMATCH" && perturbed != preFrame) shiftedMiss++;
                negatives.Add("velBefore=" + velBefore.ToString("X2") + " perturbedVel=" +
                    (velBefore >= 0xFA ? "05" : "FA") + " perturbed=" + perturbed);
            }
            game.AdvanceFrame(NesButtons.FromByte(input[poll - 1]));
            if (!gate) continue;
            int native = player.LastPlayerDispatchCycles;
            string verdict = Verdict(native, (int)oracle);
            if (verdict == "exact") { exact++; if (preFrame == native) reproduced++; }
            if (native >= 0) priced++;
            rows.WriteLine(string.Join(",", poll, savedState.ToString("X2"), ((byte)player.YVelInt).ToString("X2"), native, oracle,
                verdict, control, preFrame == native ? 1 : 0,
                Safe(string.Join(";", negatives))));
        }
        Console.WriteLine($"state09 gate: priced={priced}/37 exact={exact}/37 reproducible={reproduced}/37 comparatorRejectsPerturbedInput={shiftedMiss}/37 " +
            $"negativeRefusals={refused}/148 landingOwnerRefusals={queueRefusals} output={output}");
        if (exact != 37 || reproduced != 37 || shiftedMiss != 37 || refused != 148 || queueRefusals != 1)
        { Console.Error.WriteLine("state09 gate FAIL"); Environment.ExitCode = 1; }
        else Console.WriteLine("state09 gate PASS");
    }

    /// <summary>Map data for the synthetic cases: the class the executed $EBE8 returned at each of
    /// its own probe coordinates, handed back to the port's probe. This is map data, not ROM
    /// behaviour — the geometry, branch structure and cycle totals still come from the C# under
    /// test, and every expected value comes from the bank-7 walk. A coordinate the original never
    /// probed is counted as a fallback rather than quietly answered with a plausible class.</summary>
    sealed class FixtureMap : ITileMap
    {
        readonly Dictionary<long, byte> classes = new();
        public byte Fallback;
        public int Calls, Hits, Fallbacks;
        public long LastFallback;
        static long Key(int x, int y) => ((long)(uint)x << 32) | (uint)y;
        public void Add(int x, int y, int cls) => classes[Key(x, y)] = (byte)cls;
        public byte TypeAt(int x, int y)
        {
            Calls++;
            if (classes.TryGetValue(Key(x, y), out byte cls)) { Hits++; return cls; }
            Fallbacks++; LastFallback = Key(x, y);
            return Fallback;
        }
    }

    /// <summary>One fixture case, flattened from what the walker wrote.</summary>
    sealed class Case
    {
        public string Id, Kind, Fields, Audio, Fill, Expect, Refuse, Mutation, Pair;
        public int Poll, ExpectPrice, Fallback;
        public int[][] Probes;
        public string[] Labels;
        public bool RouteMap => Fill == "route";
        public bool Priced => Expect == "price";
        public int Actual = -1;
        public bool Accepted;
        public string Why = "", VerdictText = "NOT RUN";
    }

    record PlayerSnapshot(byte State, byte Gravity, byte Flags, byte YFrac, bool Active, byte Low,
                          ushort X, ushort Y, byte YVelFrac, sbyte YVelInt, byte ScrollY, byte ScrollYRow);

    static PlayerSnapshot Snapshot(PlayerCore p, ObjectSystem.Slot s) =>
        new(p.State, p.GravityFrac, p.Flags, p.YFrac, s.Active, s.StateLow, p.X, p.Y,
            p.YVelFrac, p.YVelInt, p.ScrollY, p.ScrollYRow);

    static void Restore(PlayerCore p, ObjectSystem.Slot s, ITileMap map, PlayerSnapshot v)
    {
        p.State = v.State; p.GravityFrac = v.Gravity; p.Flags = v.Flags; p.YFrac = v.YFrac;
        s.Active = v.Active; s.StateLow = v.Low; p.X = v.X; p.Y = v.Y; p.YVelFrac = v.YVelFrac;
        p.YVelInt = v.YVelInt; p.ScrollY = v.ScrollY; p.ScrollYRow = v.ScrollYRow; p.TileMap = map;
    }

    /// <summary>Writes the fixture's own input fields onto the live player, byte for byte, using
    /// the same $04D0 split the exporter reads (bit7 active, bits 0-6 state) and the same
    /// $04F0/$0500, $0510/$0520 little-endian pairs.</summary>
    static void Apply(PlayerCore p, ObjectSystem.Slot s, Case c)
    {
        var v = c.Fields.Split(' ');
        byte B(int i) => Convert.ToByte(v[i], 16);
        p.State = B(0); p.GravityFrac = B(1); p.Flags = B(2); p.YFrac = B(3);
        s.Active = (B(4) & 0x80) != 0; s.StateLow = (byte)(B(4) & 0x7F);
        p.X = (ushort)(B(5) | B(6) << 8); p.Y = (ushort)(B(7) | B(8) << 8);
        p.YVelFrac = B(9); p.YVelInt = unchecked((sbyte)B(10)); p.ScrollY = B(11); p.ScrollYRow = B(12);
    }

    static Case ReadCase(System.Text.Json.JsonElement e)
    {
        var expect = e.GetProperty("expect");
        var map = e.GetProperty("map");
        return new Case
        {
            Id = e.GetProperty("id").GetString(),
            Kind = e.GetProperty("kind").GetString(),
            Poll = e.GetProperty("poll").GetInt32(),
            Fields = e.GetProperty("fields").GetString(),
            Audio = e.GetProperty("audio").GetString(),
            Fill = map.ValueKind == System.Text.Json.JsonValueKind.String ? map.GetString()
                 : map.GetProperty("fill").GetString(),
            Fallback = map.ValueKind == System.Text.Json.JsonValueKind.Object
                ? map.GetProperty("fallback").GetInt32() : 0,
            Pair = e.GetProperty("pair").GetString(),
            Expect = expect.EnumerateObject().First().Name,
            Refuse = expect.TryGetProperty("refuse", out var r) ? r.GetString() : "",
            ExpectPrice = expect.TryGetProperty("price", out var p) ? p.GetInt32() : -1,
            Mutation = e.TryGetProperty("mutation", out var m) ? m.GetString() : "",
            Labels = e.GetProperty("labels").EnumerateArray().Select(x => x.GetString()).ToArray(),
            Probes = e.GetProperty("probes").EnumerateArray()
                .Select(x => x.EnumerateArray().Select(n => n.GetInt32()).ToArray()).ToArray(),
        };
    }

    /// <summary>ZERO-47: executable controls of TryMeasureWorldEntryFall09Cycles on inputs the
    /// bank-7 walker priced. Three tallies stay separate on purpose: the cycle formula (fixture
    /// map data, so a disagreement cannot be blamed on the tile map), LevelTileMap.TypeAt itself
    /// (the port's class at the coordinates the executed $EBE8 used), and the audio-queue owner
    /// (free, busy, and no owner at all). Nothing here computes an expected value from the C#:
    /// every expectation was written by state09_source_price.py from original RAM plus bank 7.</summary>
    static void State09Cases(string[] args)
    {
        string fixture = Required(args.Length > 1 ? args[1]
            : "Art/Tools/DarkwingResearch/cost/fixtures/state09_csharp_cases.json");
        string output = Output(args.Length > 2 ? args[2] : Path.Combine(OutputRoot, "state09_cases.csv"));
        var cases = new List<Case>();
        var labelSeen = new List<string>();
        string bank7Wanted, tool;
        using (var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(fixture)))
        {
            var root = doc.RootElement;
            foreach (var item in root.GetProperty("cases").EnumerateArray()) cases.Add(ReadCase(item));
            foreach (var label in root.GetProperty("labels").EnumerateArray()) labelSeen.Add(label.GetString());
            bank7Wanted = root.GetProperty("bank7_sha256").GetString();
            tool = root.GetProperty("tool").GetString();
        }
        var byId = cases.ToDictionary(c => c.Id);
        var byPoll = cases.GroupBy(c => c.Poll).ToDictionary(g => g.Key, g => g.ToList());
        int maxPoll = cases.Max(c => c.Poll);
        var (game, flow) = NewGame();
        string bank7Got = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(game.Bank7)).ToLowerInvariant();
        if (bank7Got != bank7Wanted)
            throw new Exception("Fixture bank-7 provenance mismatch: harness " + bank7Got + ", " + tool + " wrote " + bank7Wanted);
        byte[] input = File.ReadAllBytes(SessionFile("input.bin"));
        if (input.Length != 29360) throw new Exception("Unexpected city input length " + input.Length);
        var player = game.Player;
        int formulaOk = 0, formulaAll = 0, routeOk = 0, routeAll = 0;
        int typeAtOk = 0, typeAtAll = 0, typeAtCases = 0;
        int audioOk = 0, audioAll = 0, ownerOk = 0, ownerAll = 0;
        int controlOk = 0, controlAll = 0, fallbackCases = 0, unassertable = 0;
        var asserted = new Dictionary<string, int>();
        using var rows = new StreamWriter(output);
        rows.WriteLine("id,kind,poll,map,audio,expect,actual,verdict,probeCalls,fallbacks,labels,note");
        for (int poll = 1; poll <= maxPoll; poll++)
        {
            if (byPoll.TryGetValue(poll, out var batch))
                foreach (var c in batch)
                {
                    // Re-read the slot every case: the level load can hand the player a new
                    // ObjectSystem, and a slot captured before the replay would then be stale.
                    var slot = player.Objects.Slots[0];
                    var saved = Snapshot(player, slot);
                    var liveMap = player.TileMap;   // the row/world load may have replaced it
                    Apply(player, slot, c);
                    var fake = c.RouteMap ? null : new FixtureMap { Fallback = (byte)c.Fallback };
                    if (fake != null)
                    {
                        foreach (var probe in c.Probes) fake.Add(probe[2], probe[3], probe[1]);
                        player.TileMap = fake;
                    }
                    bool? accepts = c.Audio == "accepts" ? true : c.Audio == "busy" ? false : (bool?)null;
                    c.Accepted = Measure09(player, accepts, out c.Actual, out c.Why);
                    if (c.Priced)
                        c.VerdictText = c.Accepted ? Verdict(c.Actual, c.ExpectPrice)
                                                   : "REFUSED-WHERE-PRICED " + Safe(c.Why);
                    else if (c.Expect == "refuse")
                        c.VerdictText = c.Accepted ? "PRICED-WHERE-REFUSED " + c.Actual : "refused";
                    else c.VerdictText = "UNASSERTABLE " + Safe(c.Expect);
                    if (c.Expect == "unsupported") unassertable++;
                    bool agreed = c.VerdictText == "exact" || c.VerdictText == "refused";
                    if (c.Kind is "route" or "negative" or "refusal") { routeAll++; if (agreed) routeOk++; }
                    else { formulaAll++; if (agreed) formulaOk++; }
                    if (agreed) foreach (string label in c.Labels) asserted[label] = asserted.GetValueOrDefault(label) + 1;
                    // The port's own tile map, at the coordinates the executed $EBE8 used.
                    if (c.RouteMap && c.Probes.Any(p => p[4] == 1))
                    {
                        typeAtCases++;
                        foreach (var probe in c.Probes.Where(p => p[4] == 1))
                        {
                            typeAtAll++;
                            byte got = liveMap.TypeAt(probe[2], probe[3]);
                            if (got == probe[1]) typeAtOk++;
                            else rows.WriteLine(string.Join(",", c.Id, "TYPEAT-MISMATCH", poll, "route", c.Audio,
                                probe[1], got, "class at $" + probe[2].ToString("X") + "/$" + probe[3].ToString("X"),
                                "", "", Safe(string.Join(";", c.Labels)), ""));
                        }
                    }
                    // The landing sound has to be refused outright when no owner was wired.
                    if (agreed && c.Priced && c.Labels.Any(l => l.StartsWith("F85C") || l.StartsWith("F866")))
                    {
                        ownerAll++;
                        bool refused = !Measure09(player, null, out _, out string why) && why != null;
                        if (refused) ownerOk++;
                        else rows.WriteLine(string.Join(",", c.Id, "OWNER-ACCEPTED", poll, c.Fill, "none",
                            "refuse", c.Actual, "ACCEPTED", "", "", Safe(string.Join(";", c.Labels)), Safe(why)));
                    }
                    if (fake != null && fake.Fallbacks > 0) fallbackCases++;
                    rows.WriteLine(string.Join(",", c.Id, c.Kind, poll, c.Fill, c.Audio,
                        c.Priced ? c.ExpectPrice.ToString() : Safe(c.Expect), c.Accepted ? c.Actual.ToString() : "",
                        c.VerdictText, fake?.Calls ?? 0, fake?.Fallbacks ?? 0,
                        Safe(string.Join(";", c.Labels)), Safe(c.Mutation + (c.Refuse == "" ? "" : " | " + c.Refuse))));
                    Restore(player, slot, liveMap, saved);
                }
            game.AdvanceFrame(NesButtons.FromByte(input[poll - 1]));
        }
        // Pairs: every audio sibling and every fixture mutation is checked against its own case,
        // so the comparator sees a deliberate relevant error on real measured numbers.
        foreach (var c in cases.Where(c => c.Kind == "audio"))
        {
            audioAll++;
            var other = byId[c.Pair];
            if (c.VerdictText == "exact" && other.VerdictText == "exact" && c.Actual != other.Actual) audioOk++;
        }
        foreach (var c in cases.Where(c => c.Kind == "negative"))
        {
            controlAll++;
            var original = byId[c.Pair];
            string againstUntouched = Verdict(c.Actual, original.ExpectPrice);
            string againstOwn = Verdict(c.Actual, c.ExpectPrice);
            if (againstUntouched == "MISMATCH" && againstOwn == "exact" && c.Actual != original.Actual &&
                original.VerdictText == "exact") controlOk++;
            else rows.WriteLine(string.Join(",", c.Id, "COMPARATOR-FAIL", c.Poll, c.Fill, c.Audio,
                original.ExpectPrice, c.Actual, againstUntouched + "/" + againstOwn, "", "",
                Safe(string.Join(";", c.Labels)), "paired " + original.VerdictText + "/" + original.Actual));
        }
        rows.Flush();
        var uncovered = labelSeen.Where(l => !asserted.ContainsKey(l)).ToList();
        Console.WriteLine("state09cases " + tool + " cases=" + cases.Count + " output=" + output);
        Console.WriteLine("  formula (fixture map data, expected from bank-7 walk) " +
            Report(formulaOk, formulaAll) + "; route (real LevelTileMap, native price) " + Report(routeOk, routeAll));
        Console.WriteLine("  TileMap.TypeAt vs executed $EBE8 class " + Report(typeAtOk, typeAtAll) +
            " over " + typeAtCases + " cases; audio busy/free pairs " + Report(audioOk, audioAll) +
            ", missing-owner refusals " + Report(ownerOk, ownerAll));
        Console.WriteLine("  comparator negative controls " + Report(controlOk, controlAll) +
            "; fixture-coordinate fallbacks " + fallbackCases + "; unassertable cases " + unassertable);
        foreach (string label in labelSeen.OrderBy(l => l))
            Console.WriteLine("  branch " + label.PadRight(40) + (asserted.GetValueOrDefault(label) == 0
                ? "UNCOVERED by an agreeing case" : "asserted by " + asserted[label] + " case(s)"));
        if (uncovered.Count > 0) Console.Error.WriteLine("uncovered branches: " + string.Join("; ", uncovered));
        bool pass = formulaAll > 0 && routeAll > 0 && typeAtAll > 0 && audioAll > 0 && ownerAll > 0 && controlAll > 0
            && formulaOk == formulaAll && routeOk == routeAll && typeAtOk == typeAtAll && audioOk == audioAll
            && ownerOk == ownerAll && controlOk == controlAll && unassertable == 0 && uncovered.Count == 0;
        Console.WriteLine("state09cases " + (pass ? "PASS" : "FAIL"));
        Environment.ExitCode = pass ? 0 : 1;
    }

    static string Report(int ok, int all) => all == 0 ? "NOT RUN (0 cases)" : ok + "/" + all + (ok == all ? " PASS" : " FAIL");

    /// <summary>Read the real Core palette model beside original records on the same pre-input poll.
    /// The original bytes are observations only; they never seed or drive the native game.</summary>
    static void PaletteProfile(string[] args)
    {
        if (args.Length != 4) throw new ArgumentException("paletteprofile needs <first> <last> <out.csv>");
        int first = int.Parse(args[1]), last = int.Parse(args[2]);
        byte[] input = File.ReadAllBytes(SessionFile("input.bin"));
        if (first < 2 || last < first || last >= input.Length)
            throw new ArgumentException("Need 2 <= first <= last < input length, including real predecessor/successor samples");
        string originalPath = SessionFile("replay1/buffers.bin");
        using var original = File.OpenRead(originalPath);
        if (original.Length != (long)input.Length * 4384)
            throw new InvalidDataException("Original capture length must equal input length * 4384");
        string output = Output(args[3]);
        var (game, flow) = NewGame();
        var ramRecord = new byte[4384];
        var mirror = new byte[32]; var source = new byte[32];
        using (var rows = new StreamWriter(new FileStream(output, FileMode.CreateNew, FileAccess.Write)))
        {
            rows.WriteLine("poll,nativeState,nativeX,nativeY,original18,native18,original0300,native0300,original0320,native0320,original3f00,native3f00");
            for (int poll = 1; poll <= last + 1; poll++)
            {
                if (poll >= first - 1)
                {
                    original.Position = (poll - 1L) * 4384;
                    original.ReadExactly(ramRecord);
                    game.GetPaletteModel(mirror, source);
                    rows.WriteLine(string.Join(",", poll, game.Player.State.ToString("X2"), game.Player.X, game.Player.Y,
                        ramRecord[0x18].ToString("X2"), game.PaletteUploadFlag.ToString("X2"),
                        Convert.ToHexString(ramRecord.AsSpan(0x300, 32)), Convert.ToHexString(mirror),
                        Convert.ToHexString(ramRecord.AsSpan(0x320, 32)), Convert.ToHexString(source),
                        Convert.ToHexString(ramRecord.AsSpan(4352, 32)), Convert.ToHexString(game.Ppu.PaletteRam)));
                }
                game.AdvanceFrame(NesButtons.FromByte(input[poll - 1]));
            }
        }
        original.Position = 0;
        string originalSha = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(original)).ToLowerInvariant();
        string inputSha = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(input)).ToLowerInvariant();
        Console.WriteLine($"paletteprofile samples={last - first + 3} pre-input polls={first - 1}..{last + 1} inputSha={inputSha} originalSha={originalSha} output={output}");
        Console.WriteLine("Scope: observed palette/model bytes only; no CPU write or C059 invocation count, no state seed or original-to-port equivalence claim.");
    }

    /// <summary>ZERO-46 full-record exporter. Replays any available route input from reset and writes,
    /// for every poll, the same 4384-byte record the existing stand comparator reads
    /// (RAM 2048 + NT 2048 + OAM 256 + PAL 32) through the existing WriteStandRecord, using the same
    /// pre-frame convention as "clock". Port-to-port regression only: no original bytes are read here,
    /// and the record carries zero-page audio state, not synthesised PCM.</summary>
    static void Buffers(string[] args)
    {
        int frames = args.Length > 1 ? int.Parse(args[1]) : throw new ArgumentException("buffers needs <frames> <out.csv>");
        string output = Output(args.Length > 2 ? args[2] : Path.Combine(OutputRoot, "buffers_column.csv"));
        var (game, flow) = NewGame();
        byte[] input = File.ReadAllBytes(SessionFile("input.bin"));
        if (input.Length != frames) throw new Exception("Session input is " + input.Length + " bytes, requested " + frames + " frames");
        string inputSha = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(input)).ToLowerInvariant();
        string directory = Path.ChangeExtension(output, null);
        if (Directory.Exists(directory)) throw new IOException("Buffer directory exists; use a new output path: " + directory);
        Directory.CreateDirectory(directory);
        string records = Path.Combine(directory, "buffers.bin");
        using (var file = new BinaryWriter(File.Create(records)))
        {
            var ram = new byte[2048];
            for (int poll = 1; poll <= frames; poll++)
            {
                WriteStandRecord(game, flow, ram, file);
                game.AdvanceFrame(NesButtons.FromByte(input[poll - 1]));
            }
        }
        long bytes = new FileInfo(records).Length;
        if (bytes != (long)frames * 4384) throw new Exception("Record size " + bytes + " != frames*4384");
        string sha = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(records))).ToLowerInvariant();
        using var rows = new StreamWriter(output);
        rows.WriteLine("frames,recordBytes,bytes,sha256,inputSha256,input,revision");
        rows.WriteLine(string.Join(",", frames, 4384, bytes, sha, inputSha, Safe(Session), Safe(Revision())));
        Console.WriteLine("buffers " + records + " frames=" + frames + " bytes=" + bytes + " sha256=" + sha);
    }

    /// <summary>Revision stamped by <c>--revision</c>; a generated baseline copy has no Git metadata of
    /// its own, so the caller must name the revision it was extracted from. Only a tree that really is
    /// a checkout falls back to <c>git rev-parse</c>, and an override always wins.</summary>
    static string Revision()
    {
        if (!string.IsNullOrEmpty(RevisionOverride)) return RevisionOverride;
        try
        {
            var info = new System.Diagnostics.ProcessStartInfo("git", "rev-parse HEAD")
            { WorkingDirectory = ProjectRoot, RedirectStandardOutput = true, UseShellExecute = false };
            using var process = System.Diagnostics.Process.Start(info);
            string text = process.StandardOutput.ReadToEnd().Trim();
            process.WaitForExit();
            return text.Length == 40 ? text : "";
        }
        catch { return ""; }
    }

    /// <summary>Pair-level sanity before any byte comparison: both sides must hold at least one whole
    /// 4384-byte record and the same number of them, so a missing tail, an empty export or an extra
    /// complete record fails as a length error instead of silently comparing fewer frames.</summary>
    static void ValidateRecordPair(long beforeLength, long afterLength)
    {
        if (beforeLength == 0 || afterLength == 0)
            throw new Exception("Empty record stream: before=" + beforeLength + " after=" + afterLength + " bytes");
        if (beforeLength != afterLength)
            throw new Exception("Record length differs: " + beforeLength + " vs " + afterLength +
                " (" + (Math.Abs(afterLength - beforeLength) % 4384 == 0
                    ? (Math.Abs(afterLength - beforeLength) / 4384) + " whole record(s) extra"
                    : "not a whole number of records") + ")");
        if (beforeLength % 4384 != 0) throw new Exception("Truncated record stream: " + beforeLength + " bytes is not a multiple of 4384");
    }

    /// <summary>Strict record comparison over any two streams: rejects an empty stream, a length that is
    /// not a whole number of 4384-byte records; otherwise reports the first mismatch (poll, byte offset,
    /// section, before/after value) plus per-section diff counts.</summary>
    static (long records, long diffRecords, long diffBytes, string first) CompareRecords(
        Stream before, Stream after, long length, StreamWriter report)
    {
        if (length == 0) throw new Exception("Empty record stream: 0 bytes");
        if (length % 4384 != 0) throw new Exception("Truncated record stream: " + length + " bytes is not a multiple of 4384");
        const int ram = 2048, nt = 2048, oam = 256, pal = 32;
        long records = length / 4384, diffRecords = 0, diffBytes = 0;
        string first = "";
        var left = new byte[4384]; var right = new byte[4384];
        for (long index = 0; index < records; index++)
        {
            before.Position = index * 4384; after.Position = index * 4384;
            before.ReadExactly(left); after.ReadExactly(right);
            if (left.AsSpan().SequenceEqual(right)) continue;
            diffRecords++;
            for (int offset = 0; offset < 4384; offset++)
            {
                if (left[offset] == right[offset]) continue;
                diffBytes++;
                string section = offset < ram ? "RAM" : offset < ram + nt ? "NT" : offset < ram + nt + oam ? "OAM" : "PAL";
                if (first == "") first = "poll" + (index + 1) + " offset" + offset + " " + section + " " +
                    left[offset].ToString("X2") + "->" + right[offset].ToString("X2");
                report?.WriteLine((index + 1) + "," + offset + "," + section + "," + left[offset].ToString("X2") + "," + right[offset].ToString("X2"));
            }
        }
        return (records, diffRecords, diffBytes, first);
    }

    static void BufferDiff(string[] args)
    {
        if (args.Length < 3) throw new ArgumentException("bufdiff needs <before.bin> <after.bin> [out.csv]");
        string beforePath = Path.GetFullPath(args[1]), afterPath = Path.GetFullPath(args[2]);
        if (!File.Exists(beforePath)) throw new FileNotFoundException("Missing baseline record file", beforePath);
        if (!File.Exists(afterPath)) throw new FileNotFoundException("Missing after record file", afterPath);
        long beforeLength = new FileInfo(beforePath).Length, afterLength = new FileInfo(afterPath).Length;
        ValidateRecordPair(beforeLength, afterLength);
        string output = args.Length > 3 ? Output(args[3]) : null;
        using var report = output == null ? null : new StreamWriter(output);
        if (report != null) report.WriteLine("poll,offset,section,before,after");
        using var a = File.OpenRead(beforePath); using var b = File.OpenRead(afterPath);
        var result = CompareRecords(a, b, beforeLength, report);
        string shaA = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(beforePath))).ToLowerInvariant();
        string shaB = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(afterPath))).ToLowerInvariant();
        Console.WriteLine($"bufdiff records={result.records} diffRecords={result.diffRecords} diffBytes={result.diffBytes} " +
            $"first={result.first} beforeSha={shaA} afterSha={shaB} bytes={beforeLength}");
        if (result.diffRecords != 0) { Console.Error.WriteLine("bufdiff FAIL: port-to-port records differ"); Environment.ExitCode = 1; }
        else Console.WriteLine("bufdiff PASS: every exported byte identical");
    }

    /// <summary>Detector controls for the record comparator. Derived in memory from one real record
    /// file so no stray mutated evidence is left behind: a missing file, a truncated stream, a changed
    /// OAM byte and a changed PAL byte must be rejected/named, and the file against itself must pass.</summary>
    static void BufferControls(string[] args)
    {
        if (args.Length < 2) throw new ArgumentException("bufcontrols needs <records.bin>");
        string path = Path.GetFullPath(args[1]);
        byte[] data = File.ReadAllBytes(path);
        int failures = 0;
        void Check(string name, bool ok, string detail)
        {
            Console.WriteLine("control " + name + ": " + (ok ? "OK" : "FAIL") + " " + detail);
            if (!ok) failures++;
        }
        using (var self = new MemoryStream(data))
        using (var same = new MemoryStream((byte[])data.Clone()))
        {
            var ok = CompareRecords(self, same, data.Length, null);
            Check("identical-must-pass", ok.diffRecords == 0 && ok.diffBytes == 0, "records=" + ok.records + " diffBytes=" + ok.diffBytes);
        }
        bool missingRejected = false; string missingDetail = "opened";
        try { using var missing = File.OpenRead(path + ".absent"); missing.Close(); }
        catch (FileNotFoundException) { missingRejected = true; missingDetail = "FileNotFound"; }
        catch (Exception error) { missingRejected = true; missingDetail = error.GetType().Name; }
        Check("missing-file", missingRejected, missingDetail);
        Check("reference-file-shaped", data.Length > 0 && data.Length % 4384 == 0, "length=" + data.Length + " records=" + data.Length / 4384);

        // Length/pair controls run on in-memory slices of the same comparator; no mutated large files.
        bool Rejected(Action action)
        {
            try { action(); return false; } catch (Exception) { return true; }
        }
        Check("empty-pair", Rejected(() => ValidateRecordPair(0, 0)), "empty both sides must throw");
        Check("empty-vs-records", Rejected(() => ValidateRecordPair(0, data.Length)), "empty before a full export must throw");
        Check("records-vs-empty", Rejected(() => ValidateRecordPair(data.Length, 0)), "empty after a full export must throw");
        Check("extra-complete-record", Rejected(() => ValidateRecordPair(data.Length, data.Length + 4384)), "one extra whole record must throw");
        Check("missing-complete-record", Rejected(() => ValidateRecordPair(data.Length, data.Length - 4384)), "one dropped whole record must throw");
        Check("truncated-pair", Rejected(() => ValidateRecordPair(data.Length, data.Length - 1)), "off-by-one tail must throw");
        Check("unaligned-self", Rejected(() => { using var a = new MemoryStream(data); using var b = new MemoryStream(data); CompareRecords(a, b, data.Length - 1, null); }), "unaligned length must throw");
        Check("empty-stream-compare", Rejected(() => { using var a = new MemoryStream(); using var b = new MemoryStream(); CompareRecords(a, b, 0, null); }), "zero-byte compare must throw");

        var truncated = new byte[data.Length - 1];
        Array.Copy(data, truncated, truncated.Length);
        bool truncatedRejected = false; string truncatedDetail = "accepted";
        try { using var a = new MemoryStream(truncated); using var b = new MemoryStream(truncated); CompareRecords(a, b, truncated.Length, null); }
        catch (Exception error) { truncatedRejected = true; truncatedDetail = error.Message.Substring(0, Math.Min(52, error.Message.Length)); }
        Check("truncated-record", truncatedRejected, truncatedDetail);

        // A relevant change on exactly one poll must be found and named, not averaged away.
        foreach (var (name, offset) in new[] { ("changed-oam-byte", 2048 + 2048 + 8), ("changed-pal-byte", 2048 + 2048 + 256 + 1), ("changed-nt-byte", 2048 + 5), ("changed-ram-byte", 0x4D0) })
        {
            var mutated = (byte[])data.Clone();
            mutated[offset] ^= 0xFF;
            using var a = new MemoryStream(data); using var b = new MemoryStream(mutated);
            var result = CompareRecords(a, b, data.Length, null);
            Check(name, result.diffRecords == 1 && result.diffBytes == 1 && result.first.Contains("poll1 offset" + offset), result.first);
        }
        Console.WriteLine(failures == 0 ? "bufcontrols PASS" : "bufcontrols FAIL " + failures);
        if (failures != 0) Environment.ExitCode = 1;
    }

    /// <summary>Bound through reflection for the same reason as Measure09: a revision that does not
    /// have the card-release owner or the prefix assembler must fail loudly, not pass quietly.</summary>
    static readonly MethodInfo PrefixMethod =
        typeof(DarkwingGame).GetMethod("MeasureClockBodyPrefix", BindingFlags.Instance | BindingFlags.NonPublic);
    static readonly MethodInfo CardOwnerMethod =
        typeof(PlayerCore).GetMethod("BeginCardContinuationTiming", BindingFlags.Instance | BindingFlags.NonPublic);
    static readonly FieldInfo CardWaitField = typeof(PlayerCore).GetField("_cardWait", BindingFlags.Instance | BindingFlags.NonPublic);
    static readonly FieldInfo CardPhaseField = typeof(PlayerCore).GetField("_cardPhase", BindingFlags.Instance | BindingFlags.NonPublic);

    static (int price, string why) MeasurePrefix(DarkwingGame game)
    {
        if (PrefixMethod == null) throw new InvalidOperationException("MeasureClockBodyPrefix is absent from this revision");
        var argv = new object[] { null };
        return ((int)PrefixMethod.Invoke(game, argv), (string)argv[0]);
    }

    static void MeasureCardOwner(PlayerCore player)
    {
        if (CardOwnerMethod == null) throw new InvalidOperationException("BeginCardContinuationTiming is absent from this revision");
        CardOwnerMethod.Invoke(player, null);
    }

    /// <summary>The native prices the body-prefix assembler adds, after the common 6 + hook + 18 and
    /// the owner span: [0] DA70 scroll, [1] 8090 spawn, [2] EDF1 zone, [3] palette service,
    /// [4] bank 6 script, [5] bank 1 script, [6] environment tail.</summary>
    static int[] PrefixParts(DarkwingGame game, PlayerCore player)
    {
        var enemies = game.Enemies;
        return new[]
        {
            player.LastScrollServiceCycles, game.Objects.LastPlacementSpawnCycles, game.LastZoneServiceCycles,
            game.TryMeasurePaletteServiceCycles(0, 0, out int palette, out _) ? palette : -1,
            enemies?.LastBank6ScriptCycles ?? -1, enemies?.LastBank1ScriptCycles ?? -1,
            game.TileMap == null ? -1 : EnvironmentTail(game, out _)
        };
    }

    static string PartsText(string label, int[] parts) => label + " scroll=" + parts[0] + " spawn=" + parts[1] +
        " zone=" + parts[2] + " palette=" + parts[3] + " bank6=" + parts[4] + " bank1=" + parts[5] + " tail=" + parts[6];

    /// <summary>ZERO-52 focused controls for the owner of the body prefix on the frame the mission
    /// intro card releases. Positive: on that frame the assembled C# prefix equals the original
    /// $CABB−$FECF, and $LastCardResumeCycles + 12 equals the original $CA59−$FECF — the common
    /// 6 + hook + 18 appears once, because a doubled prefix would miss 4986 by 549.
    ///
    /// The negatives are asserted as three separate things, because ZERO-51 showed that bundling them
    /// into one "refused" verdict proves nothing: on the release frame the pre-frame probe always
    /// refuses with a foreign reason (the mission head never ran on the previous hold frame, so
    /// $LastMissionHeadCycles is still −1), which masked any price or cause a broken guard produced.
    /// Family A (the real release frame) therefore asserts the continuation flag, the guard's own
    /// refusal text and the continuation span the owner computed, per shape, and records the assembler
    /// refusal as the known stale-head mask instead of counting it as evidence.
    /// Family B (an ordinary pre-frame whose untouched head branch the assembler priced without any
    /// refusal, i.e. every component it consumed is valid) asserts the choice itself: with a
    /// release-shaped owner the assembler must move off head + 6 + player onto the continuation span,
    /// and for each withdrawn shape it must charge exactly the untouched head price. Here the ordinary
    /// branch is not a failure — per contract it legitimately owns the frame — so the expectation is a
    /// specific number, and a hold span that leaked into the body would change that number.
    /// Control: the next poll is ordinary, still priced by head + 6 + player, and still equals the
    /// original. No source price is fed to the game and no poll number selects a branch.</summary>
    static void CardRelease(string[] args)
    {
        string output = Output(args.Length > 1 ? args[1] : Path.Combine(OutputRoot, "card_release_controls.csv"));
        int replayTo = args.Length > 2 ? int.Parse(args[2]) : 2125;
        int first = args.Length > 3 ? int.Parse(args[3]) : 2110;
        int last = args.Length > 4 ? int.Parse(args[4]) : 2125;
        var (game, flow) = NewGame();
        byte[] input = File.ReadAllBytes(SessionFile("input.bin"));
        if (input.Length != 29360 || Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(input)).ToLowerInvariant() != "510925941e2397b6d86aae459cbd4dde291a6574963a01b104f5f7d3b88a9100")
            throw new Exception("Unexpected city input");
        var player = game.Player;
        var source = SourcePoints(ReferenceFile("phases/city_phases.csv"));
        long Point(int poll, string pc) => source.TryGetValue(poll, out var map) && map.TryGetValue(pc, out var list) ? list[0] : -1;
        using var rows = new StreamWriter(output);
        rows.WriteLine("poll,preWait,preState,preCardPhase,world,weapon,continuation,cardWhy,released,resume,hook,head,player," +
            "prefixPre,prefixWhyPre,prefixFrame,origCa59,verdictCa59,origCabb,verdictCabb,tailPre,tailFrame,timedThisFrame," +
            "controls,ownerSelection,validContextControls");
        int releaseFrames = 0, releaseFramesPriced = 0, releaseCa59Exact = 0, releaseCabbExact = 0, releaseNoPhase = 0;
        int ordinaryPolls = 0, ordinaryExact = 0, holdUntimed = 0, holdViolations = 0, negativeRefusals = 0, negativesExpected = 0;
        int ownerSpans = 0, anchorExact = 0, frameCabbExact = 0, preFrameDeltaExact = 0, preFrameDeltaOther = 0;
        // Family A on the release frame: three separate assertions per shape, plus the mask record.
        int negativeFlags = 0, negativeReasons = 0, negativeSpans = 0, negativeMasked = 0;
        // Family B on a poll whose untouched head branch is fully priced by the assembler itself.
        int validPolls = 0, validSelected = 0, validNegatives = 0, validFlags = 0, validReasons = 0, validSpans = 0,
            validPrices = 0, validLeaks = 0, validLeaksExpected = 0;
        var controlProblems = new List<string>();
        string ownerNote = "";
        var releaseLog = new List<string>();
        for (int poll = 1; poll <= replayTo; poll++)
        {
            bool gate = poll >= first && poll <= last;
            byte waitBefore = (byte)CardWaitField.GetValue(player);
            byte stateBefore = player.State, worldBefore = player.WorldId, weaponBefore = player.WeaponSelected;
            bool phaseBefore = (bool)CardPhaseField.GetValue(player);
            int timedBefore = game.Clock.TimedPasses;
            var negatives = new List<string>();
            var validRows = new List<string>();
            int prefixPre = -1, resume = -1, tailPre = -1; string prefixWhyPre = null;
            string ownerSelection = "skip";
            void Restore()
            {
                CardWaitField.SetValue(player, waitBefore);
                player.State = stateBefore; player.WorldId = worldBefore; player.WeaponSelected = weaponBefore;
                CardPhaseField.SetValue(player, phaseBefore);
            }
            bool ownerPre = false;
            int[] preParts = null;
            if (gate)
            {
                MeasureCardOwner(player);
                (prefixPre, prefixWhyPre) = MeasurePrefix(game);
                resume = game.LastCardResumeCycles;
                tailPre = game.TileMap == null ? -1 : EnvironmentTail(game, out _);
                preParts = PrefixParts(game, player);
                // The negatives only mean something where the untouched owner is actually released:
                // on an ordinary poll withdrawing the card owner legitimately leaves the head + 6 +
                // player span, so a refusal there would be an artefact of the probe, not a control.
                ownerPre = player.LastCardContinuationReleased;
                int headSpan = game.LastMissionHeadCycles, playerSpan = player.LastPlayerDispatchCycles;
                const string ReleaseContextRefusal = "D554 mission release context";
                // name, perturbation, and what the real source contract owes for it: the guard's own
                // refusal text and the continuation span the owner leaves behind (61 = 2+4+6+49 is the
                // measured hold span, which must stay out of the body; −1 means nothing was priced).
                var shapes = new (string name, Action perturb, string guardReason, int span)[]
                {
                    ("holdWait2", () => CardWaitField.SetValue(player, (byte)2), null, 61),
                    ("holdWait20", () => CardWaitField.SetValue(player, (byte)0x14), null, 61),
                    ("worldId7", () => player.WorldId = 7, ReleaseContextRefusal, -1),
                    ("weapon4", () => player.WeaponSelected = 4, ReleaseContextRefusal, -1),
                    ("wait0", () => CardWaitField.SetValue(player, (byte)0), null, -1),
                    ("state0B", () => player.State = 0x0B, null, -1),
                    ("cardPhaseOff", () => CardPhaseField.SetValue(player, false), null, -1),
                };
                void ReleaseFrameControl(string name, Action perturb, string guardReason, int span)
                {
                    if (!ownerPre) return;
                    negativesExpected++;
                    perturb();
                    MeasureCardOwner(player);
                    var (price, why) = MeasurePrefix(game);
                    bool released = player.LastCardContinuationReleased;
                    string actualReason = player.CardTimingUnsupported;
                    int actualSpan = player.LastCardContinuationCycles;
                    Restore();
                    bool flagOk = !released, reasonOk = actualReason == guardReason, spanOk = actualSpan == span;
                    if (flagOk) negativeFlags++;
                    if (reasonOk) negativeReasons++;
                    if (spanOk) negativeSpans++;
                    if (flagOk && reasonOk && spanOk) negativeRefusals++;
                    else controlProblems.Add("release-frame@" + poll + " " + name + " flag=" + released +
                        " reason=" + (actualReason ?? "-") + " span=" + actualSpan);
                    // ZERO-51: on this frame the assembler refuses for a foreign reason - the previous
                    // frame was a hold, so the head it would fall back to is still −1. Recorded, never
                    // counted as proof; the price proof lives in the fully valid poll below.
                    if (price < 0 && why == "CAE2 head") negativeMasked++;
                    negatives.Add(name + "=flag:" + (flagOk ? "ok" : "BAD(" + released + ")") +
                        ",reason:" + (reasonOk ? "ok" : "BAD(" + (actualReason ?? "-") + ")") +
                        ",span:" + (spanOk ? span : "BAD(" + actualSpan + ")") +
                        ",assembler=" + price + "(" + (why ?? "-") + ")");
                }
                // The assembler's own head branch priced this pre-frame without naming any refusal, which
                // is its attestation that every component it consumed is valid. Only the owner it selects
                // can move the price, so each shape below is decided by the guard or by the owner choice.
                bool fullyValid = !ownerPre && prefixPre >= 0 && prefixWhyPre == null
                    && headSpan >= 0 && playerSpan >= 0 && worldBefore <= 6 && weaponBefore <= 3;
                if (fullyValid)
                {
                    validPolls++;
                    void ReleaseShape(byte wait)
                    {
                        player.State = 0x09; CardPhaseField.SetValue(player, true); CardWaitField.SetValue(player, wait);
                    }
                    ReleaseShape(1);
                    MeasureCardOwner(player);
                    var (contPrice, contWhy) = MeasurePrefix(game);
                    int contSpan = player.LastCardContinuationCycles;
                    bool contReleased = player.LastCardContinuationReleased;
                    Restore();
                    int expected = prefixPre - headSpan - 6 - playerSpan + contSpan;
                    bool stable = game.LastMissionHeadCycles == headSpan && player.LastPlayerDispatchCycles == playerSpan;
                    bool selected = contReleased && contSpan > 0 && contWhy == null && contPrice == expected
                        && contPrice != prefixPre && stable;
                    if (selected) validSelected++;
                    else controlProblems.Add("owner-selection@" + poll + " price=" + contPrice + " expected=" + expected +
                        " headPrice=" + prefixPre + " span=" + contSpan + " released=" + contReleased +
                        " why=" + (contWhy ?? "-") + " stable=" + stable);
                    ownerSelection = (selected ? "ok" : "BAD") + "(" + prefixPre + "->" + contPrice + "==" + expected + ",span=" + contSpan + ")";
                    void ValidControl(string name, Action perturb, string guardReason, int span)
                    {
                        validNegatives++;
                        ReleaseShape(1);
                        perturb();
                        MeasureCardOwner(player);
                        var (price, why) = MeasurePrefix(game);
                        bool released = player.LastCardContinuationReleased;
                        string actualReason = player.CardTimingUnsupported;
                        int actualSpan = player.LastCardContinuationCycles;
                        Restore();
                        bool flagOk = !released, reasonOk = actualReason == guardReason, spanOk = actualSpan == span;
                        // Withdrawing the owner must land the assembler on exactly the untouched head
                        // price: the ordinary branch is legitimately valid here, so a refusal would be
                        // a defect, and a different number would mean the card span leaked into the body.
                        bool priceOk = price == prefixPre && why == null;
                        bool leakOk = true;
                        if (actualSpan >= 0)
                        {
                            validLeaksExpected++;
                            int leak = prefixPre - headSpan - 6 - playerSpan + actualSpan;
                            leakOk = leak != prefixPre && price == prefixPre;
                            if (leakOk) validLeaks++;
                        }
                        if (flagOk) validFlags++;
                        if (reasonOk) validReasons++;
                        if (spanOk) validSpans++;
                        if (priceOk) validPrices++;
                        if (!flagOk || !reasonOk || !spanOk || !priceOk || !leakOk)
                            controlProblems.Add("valid-context@" + poll + " " + name + " flag=" + released +
                                " reason=" + (actualReason ?? "-") + " span=" + actualSpan + " price=" + price +
                                " headPrice=" + prefixPre + " why=" + (why ?? "-") + " leakOk=" + leakOk);
                        validRows.Add(name + "=flag:" + (flagOk ? "ok" : "BAD(" + released + ")") +
                            ",reason:" + (reasonOk ? "ok" : "BAD(" + (actualReason ?? "-") + ")") +
                            ",span:" + (spanOk ? span : "BAD(" + actualSpan + ")") +
                            ",price:" + (priceOk ? prefixPre : "BAD(" + price + "!=" + prefixPre + ")") +
                            ",leak:" + (leakOk ? (actualSpan >= 0 ? "excluded" : "n/a") : "BAD"));
                    }
                    foreach (var shape in shapes) ValidControl(shape.name, shape.perturb, shape.guardReason, shape.span);
                }
                foreach (var shape in shapes) ReleaseFrameControl(shape.name, shape.perturb, shape.guardReason, shape.span);
                MeasureCardOwner(player);
            }
            game.AdvanceFrame(NesButtons.FromByte(input[poll - 1]));
            bool releasedFrame = player.LastCardContinuationReleased;
            bool timed = game.Clock.TimedPasses > timedBefore;
            if (releasedFrame)
            {
                releaseFrames++;
                if (game.LastClockBodyPrefixCycles >= 0) releaseFramesPriced++;
                long ca59 = Point(poll, "FECF") >= 0 && Point(poll, "CA59") >= 0 ? Point(poll, "CA59") - Point(poll, "FECF") : -1;
                long cabb = Point(poll, "FECF") >= 0 && Point(poll, "CABB") >= 0 ? Point(poll, "CABB") - Point(poll, "FECF") : -1;
                if (ca59 < 0 && cabb < 0) releaseNoPhase++;
                else
                {
                    if (ca59 >= 0 && Verdict(game.LastCardResumeCycles + 12, (int)ca59) == "exact") releaseCa59Exact++;
                    if (cabb >= 0 && Verdict(game.LastClockBodyPrefixCycles, (int)cabb) == "exact") releaseCabbExact++;
                }
                releaseLog.Add(poll + ":prefix=" + game.LastClockBodyPrefixCycles + "/" + cabb +
                    " anchor=" + (game.LastCardResumeCycles < 0 ? -1 : game.LastCardResumeCycles + 12) + "/" + ca59);
            }
            if (gate)
            {
                int[] frameParts = PrefixParts(game, player);
                int tailFrame = frameParts[6];
                long origCa59 = Point(poll, "FECF") >= 0 && Point(poll, "CA59") >= 0 ? Point(poll, "CA59") - Point(poll, "FECF") : -1;
                long origCabb = Point(poll, "FECF") >= 0 && Point(poll, "CABB") >= 0 ? Point(poll, "CABB") - Point(poll, "FECF") : -1;
                if (ownerPre)
                {
                    ownerSpans++;
                    // resume already holds 6 + hook + 18 and the 12 is CA43..CA57, so a prefix that
                    // counted the common part twice would miss this anchor by exactly 549.
                    if (origCa59 >= 0 && Verdict(resume + 12, (int)origCa59) == "exact") anchorExact++;
                    if (origCabb >= 0 && Verdict(game.LastClockBodyPrefixCycles, (int)origCabb) == "exact") frameCabbExact++;
                    // The assembler runs mid-frame, so a pre-frame probe legitimately lags by exactly
                    // the two actor scripts this frame's own dispatch produces (bank 6 at $CE39 and
                    // the bank 1 tail). Everything else must be equal and substituting just those two
                    // must close the gap: that names the deviation instead of tolerating it.
                    bool othersEqual = preParts[0] == frameParts[0] && preParts[1] == frameParts[1]
                        && preParts[2] == frameParts[2] && preParts[3] == frameParts[3] && preParts[6] == frameParts[6];
                    if (origCabb >= 0 && othersEqual && Verdict(
                            prefixPre + frameParts[4] - preParts[4] + frameParts[5] - preParts[5], (int)origCabb) == "exact")
                        preFrameDeltaExact++;
                    else preFrameDeltaOther++;
                    ownerNote = "prefixPre=" + prefixPre + " charged=" + game.LastClockBodyPrefixCycles +
                        " original=" + origCabb + " othersEqual=" + othersEqual + " | " +
                        PartsText("pre", preParts) + " | " + PartsText("frame", frameParts);
                }
                if (!ownerPre && timed)
                {
                    ordinaryPolls++;
                    if (origCabb >= 0 && Verdict(game.LastClockBodyPrefixCycles, (int)origCabb) == "exact") ordinaryExact++;
                }
                if (!releasedFrame && waitBefore > 1) { holdUntimed++; if (timed) holdViolations++; }
                string verdictCa59 = origCa59 < 0 ? "NO PHASE" : ownerPre ? Verdict(resume + 12, (int)origCa59) : "NO OWNER";
                string verdictCabb = origCabb < 0 ? "NO PHASE" : Verdict(game.LastClockBodyPrefixCycles, (int)origCabb);
                rows.WriteLine(string.Join(",", poll, waitBefore.ToString("X2"), stateBefore.ToString("X2"), phaseBefore ? 1 : 0,
                    worldBefore, weaponBefore, player.LastCardContinuationCycles, Safe(player.CardTimingUnsupported), releasedFrame ? 1 : 0,
                    resume, game.LastInputHookCycles, game.LastMissionHeadCycles, player.LastPlayerDispatchCycles,
                    prefixPre, Safe(prefixWhyPre), game.LastClockBodyPrefixCycles, origCa59, verdictCa59, origCabb, verdictCabb,
                    tailPre, tailFrame, timed ? 1 : 0, Safe(string.Join(";", negatives)),
                    Safe(ownerSelection), Safe(string.Join(";", validRows))));
            }
        }
        Console.WriteLine($"card-release owner spans={ownerSpans} anchorExact(6+hook+18+cont+12)={anchorExact}/{ownerSpans} " +
            $"framePrefixExact={frameCabbExact}/{ownerSpans} preFrameDeltaClosed={preFrameDeltaExact}/{ownerSpans} " +
            $"preFrameDeltaUnexplained={preFrameDeltaOther} [{ownerNote}]");
        Console.WriteLine($"card-release frames in replay: releaseFrames={releaseFrames} priced={releaseFramesPriced} " +
            $"ca59Exact={releaseCa59Exact} cabbExact={releaseCabbExact} noPhase={releaseNoPhase} [{string.Join(" ", releaseLog)}]");
        Console.WriteLine($"card-release ordinary control: pricedPolls={ordinaryPolls} exact={ordinaryExact}/{ordinaryPolls} " +
            $"holdUntimed={holdUntimed} holdTimedViolations={holdViolations} negativeRefusals={negativeRefusals}/{negativesExpected} output={output}");
        Console.WriteLine($"card-release release-frame negatives (real context): flag={negativeFlags}/{negativesExpected} " +
            $"reason={negativeReasons}/{negativesExpected} span={negativeSpans}/{negativesExpected} " +
            $"assemblerRefusalMaskedByStaleHead={negativeMasked}/{negativesExpected}");
        Console.WriteLine($"card-release valid-context assembler controls: polls={validPolls} " +
            $"ownerSelection={validSelected}/{validPolls} negatives={validNegatives}/{validPolls * 7} " +
            $"flag={validFlags}/{validNegatives} reason={validReasons}/{validNegatives} span={validSpans}/{validNegatives} " +
            $"headPrice={validPrices}/{validNegatives} holdSpanExcludedFromBody={validLeaks}/{validLeaksExpected}");
        foreach (string problem in controlProblems.Take(12)) Console.Error.WriteLine("CONTROL FAIL: " + problem);
        if (ownerSpans == 0 || anchorExact != ownerSpans || frameCabbExact != ownerSpans
            || preFrameDeltaExact != ownerSpans || preFrameDeltaOther != 0
            || releaseFrames != ownerSpans || releaseCabbExact != releaseFrames || releaseCa59Exact != releaseFrames
            || releaseFramesPriced != releaseFrames || ordinaryPolls == 0 || ordinaryExact != ordinaryPolls
            || holdViolations != 0 || negativesExpected != ownerSpans * 7 || negativeRefusals != negativesExpected
            || negativeFlags != negativesExpected || negativeReasons != negativesExpected || negativeSpans != negativesExpected
            || validPolls == 0 || validSelected != validPolls || validNegatives != validPolls * 7
            || validFlags != validNegatives || validReasons != validNegatives || validSpans != validNegatives
            || validPrices != validNegatives || validLeaksExpected != validPolls * 2 || validLeaks != validLeaksExpected
            || controlProblems.Count != 0)
        { Console.Error.WriteLine("cardrelease gate FAIL"); Environment.ExitCode = 1; }
        else Console.WriteLine("cardrelease gate PASS");
    }

    /// <summary>Shared-timing regression column: replays any available route input from reset and writes
    /// the native player-dispatch price and refusal per poll. Diffing two columns is the preservation check.</summary>
    static void Dispatch(string[] args)
    {
        int frames = args.Length > 1 ? int.Parse(args[1]) : throw new ArgumentException("dispatch needs <frames> <out.csv>");
        string output = Output(args.Length > 2 ? args[2] : Path.Combine(OutputRoot, "dispatch_column.csv"));
        var (game, flow) = NewGame();
        byte[] input = File.ReadAllBytes(SessionFile("input.bin"));
        if (input.Length != frames) throw new Exception("Session input is " + input.Length + " bytes, requested " + frames + " frames");
        Console.WriteLine("dispatch session=" + Session + " bytes=" + input.Length +
            " sha256=" + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(input)).ToLowerInvariant());
        var head = Metric(game, "LastMissionHeadCycles");
        using var rows = new StreamWriter(output);
        rows.WriteLine("poll,ran,state,player,playerWhy");
        for (int poll = 1; poll <= frames; poll++)
        {
            head.SetValue(game, int.MinValue);
            byte state = game.Player.State;
            game.AdvanceFrame(NesButtons.FromByte(input[poll - 1]));
            rows.WriteLine(string.Join(",", poll, game.LastMissionHeadCycles != int.MinValue ? 1 : 0, state.ToString("X2"),
                game.Player.LastPlayerDispatchCycles, Safe(game.Player.PlayerDispatchTimingUnsupported)));
        }
        Console.WriteLine("dispatch column written " + output + " frames=" + frames);
    }

    static string Safe(string s) => s == null ? "" : s.Replace(',', ';');

    /// <summary>Reached actor behaviours/states and player states per poll window (pre-frame).</summary>
    static void Census(string[] args)
    {
        int first = int.Parse(args[1]), last = int.Parse(args[2]);
        var (game, flow) = NewGame();
        byte[] input = File.ReadAllBytes(SessionFile("input.bin"));
        var actors = new SortedDictionary<string, (int count, int first, int last)>();
        var players = new SortedDictionary<string, (int count, int first, int last)>();
        void Add(SortedDictionary<string, (int count, int first, int last)> map, string key, int poll)
        {
            var v = map.GetValueOrDefault(key);
            map[key] = (v.count + 1, v.count == 0 ? poll : v.first, poll);
        }
        for (int poll = 1; poll <= last; poll++)
        {
            if (poll >= first && game.Objects != null)
            {
                for (int slot = 1; slot < ObjectSystem.SlotCount; slot++)
                {
                    var s = game.Objects.Slots[slot];
                    if (!s.Active) continue;
                    Add(actors, $"{(s.Behavior >= 0x47 ? "bank1" : "bank6")} {s.Behavior:X2}/{s.StateLow & 0x0F:X1} slot{(slot < 5 ? slot.ToString() : "5+")}", poll);
                }
                Add(players, $"state {game.Player.State:X2}", poll);
            }
            game.AdvanceFrame(NesButtons.FromByte(input[poll - 1]));
            if (poll >= first && game.Player.PlayerDispatchTimingUnsupported != null && game.LastMissionHeadCycles >= 0)
                Add(players, "why " + game.Player.PlayerDispatchTimingUnsupported, poll);
        }
        foreach (var pair in actors) Console.WriteLine($"{pair.Key}: {pair.Value.count} [{pair.Value.first}..{pair.Value.last}]");
        foreach (var pair in players) Console.WriteLine($"{pair.Key}: {pair.Value.count} [{pair.Value.first}..{pair.Value.last}]");
    }

    /// <summary>Native per-slot bank-6 prices (pre-behaviour state) for comparison with the original
    /// $8008/$8027/$8032 segments. Columns: behaviour/state are the values the handler saw.</summary>
    static void Slots(string[] args)
    {
        int first = int.Parse(args[1]), last = int.Parse(args[2]);
        string output = Output(args.Length > 3 ? args[3] : Path.Combine(OutputRoot, "city_bank6_native.csv"));
        var (game, flow) = NewGame();
        byte[] input = File.ReadAllBytes(SessionFile("input.bin"));
        using var rows = new StreamWriter(output);
        rows.WriteLine("poll,slot,behavior,state,active,envelope,behaviorCycles,post,why,playerState,bank6Total,dir,yvel,anim,frame,timer");
        var before = new (byte behavior, byte state, bool active, byte dir, sbyte yvel, byte anim, byte frame, byte timer)[16];
        for (int poll = 1; poll <= last; poll++)
        {
            if (game.Objects != null)
                for (int slot = 1; slot < 16; slot++)
                {
                    var o = game.Objects.Slots[slot];
                    before[slot] = (o.Behavior, o.StateLow, o.Active, o.Direction, o.YVelInt, o.AnimNumber, o.AnimFrame, o.AnimTimer);
                }
            byte playerState = game.Player.State;
            game.AdvanceFrame(NesButtons.FromByte(input[poll - 1]));
            if (poll < first || game.Enemies == null || game.LastMissionHeadCycles < 0) continue;
            var e = game.Enemies;
            for (int slot = 1; slot < 16; slot++)
                rows.WriteLine(string.Join(",", poll, slot, before[slot].behavior.ToString("X2"), before[slot].state.ToString("X2"),
                    before[slot].active ? 1 : 0, e.Bank6EnvelopeCycles[slot], e.Bank6BehaviorCycles[slot], e.Bank6PostCycles[slot],
                    (e.Bank6SlotUnsupported[slot] ?? "").Replace(',', ';'), playerState.ToString("X2"), e.LastBank6ScriptCycles,
                    before[slot].dir.ToString("X2"), before[slot].yvel, before[slot].anim.ToString("X2"), before[slot].frame.ToString("X2"), before[slot].timer));
        }
        Console.WriteLine("slots written " + output);
    }

    /// <summary>Native vs original persistent audio fields ($C0,$C7-$E3,$0700-$077F) around a window.</summary>
    static void AudioDiff(string[] args)
    {
        int first = int.Parse(args[1]), last = int.Parse(args[2]);
        var (game, flow) = NewGame();
        byte[] input = File.ReadAllBytes(SessionFile("input.bin"));
        var audioType = game.Audio.GetType();
        var addresses = new List<int> { 0xC0 }; for (int a = 0xC7; a <= 0xD8; a++) addresses.Add(a);
        var fields = addresses.ToDictionary(a => a, a => audioType.GetField("_" + a.ToString("x2"), BindingFlags.Instance | BindingFlags.NonPublic));
        var page7 = (byte[])audioType.GetField("_p7", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(game.Audio);
        var queue = (byte[])typeof(DarkwingGame).GetField("_audioQueue", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(game);
        using var original = File.OpenRead(SessionFile("replay1/buffers.bin"));
        var ram = new byte[2048];
        for (int poll = 1; poll <= last; poll++)
        {
            if (poll >= first)
            {
                original.Position = (poll - 1L) * 4384; original.ReadExactly(ram);
                var diffs = new List<string>();
                foreach (int a in addresses) { byte n = (byte)fields[a].GetValue(game.Audio); if (n != ram[a]) diffs.Add($"{a:X2}:{ram[a]:X2}/{n:X2}"); }
                for (int i = 0; i < 128; i++) if (page7[i] != ram[0x700 + i]) diffs.Add($"{0x700 + i:X3}:{ram[0x700 + i]:X2}/{page7[i]:X2}");
                for (int i = 0; i < 8; i++) if (queue[i] != ram[0xDC + i]) diffs.Add($"q{i}:{ram[0xDC + i]:X2}/{queue[i]:X2}");
                Console.WriteLine(poll + " " + string.Join(" ", diffs));
            }
            game.AdvanceFrame(NesButtons.FromByte(input[poll - 1]));
        }
    }

    /// <summary>Diagnostic: before poll N, evaluate a private PlayerCore price by reflection.</summary>
    static void ProbeAt(string[] args)
    {
        int poll = int.Parse(args[1]);
        var (game, flow) = NewGame();
        byte[] input = File.ReadAllBytes(SessionFile("input.bin"));
        for (int p = 1; p < poll; p++) game.AdvanceFrame(NesButtons.FromByte(input[p - 1]));
        var player = game.Player;
        Console.WriteLine($"player X={player.X:X4} Y={player.Y:X4} vel={player.YVelInt}.{player.YVelFrac:X2} state={player.State:X2}");
        var t = typeof(PlayerCore);
        var vertical = t.GetMethod("ExpandedGroundVertical", BindingFlags.Instance | BindingFlags.NonPublic);
        var parms = new object[] { 0, 0 };
        try { Console.WriteLine("ground vertical " + vertical.Invoke(player, parms) + " nextY=" + ((int)parms[0]).ToString("X4") + " max=" + parms[1]); }
        catch (TargetInvocationException e) { Console.WriteLine("ground vertical: " + e.InnerException.Message); }
        var pair = t.GetMethod("ExpandedVerticalPair", BindingFlags.Instance | BindingFlags.NonPublic);
        var land = t.GetMethod("PlayerActorLandCycles", BindingFlags.Instance | BindingFlags.NonPublic);
        foreach (int dy in new[] { 0, 1 })
        {
            var a = new object[] { (int)player.X, (int)player.Y + dy, false, 0 };
            Console.WriteLine($"pair y+{dy} " + pair.Invoke(player, a) + " hit=" + a[2] + " push=" + a[3]);
            var b = new object[] { (int)player.X, (int)player.Y + dy, (int)player.Y, (int)a[3], 0 };
            Console.WriteLine($"  land " + land.Invoke(player, b) + " y=" + ((int)b[1]).ToString("X4") + " cls=" + b[4]);
        }
        Console.WriteLine($"YFrac={player.YFrac:X2}");
        var probe = t.GetMethod("ExpandedProbeSet", BindingFlags.Instance | BindingFlags.NonPublic);
        for (int y = player.Y - 6; y <= player.Y + 2; y++)
        {
            var a = new object[] { (int)player.X, y, 9, 0, 0, 5 };
            try { Console.WriteLine($"probe9 y={y:X4} " + probe.Invoke(player, a) + " max=" + a[3]); }
            catch (TargetInvocationException e) { Console.WriteLine($"probe9 y={y:X4}: " + e.InnerException.Message); }
        }
    }
}
