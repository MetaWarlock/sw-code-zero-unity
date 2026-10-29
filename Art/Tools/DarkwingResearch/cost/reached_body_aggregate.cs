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
            if (args[i] is not ("--project-root" or "--session" or "--reference-dir" or "--legacy-reference-dir" or "--output-root"))
                throw new ArgumentException("Unknown option " + args[i]);
            if (i + 1 == args.Length || args[i + 1].StartsWith("--")) throw new ArgumentException("Missing value for " + args[i]);
            string key = args[i]; options.Add(key, args[++i]);
        }
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
        else if (rest.Count > 0 && mode is not ("city" or "clock" or "census" or "slots" or "audiodiff" or "probe" or "state09" or "dispatch"))
            throw new ArgumentException("Modes: city, clock native|oracle, census, slots, audiodiff, probe, state09, dispatch, m1 (legacy inputs missing)");
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
        if (args.Length > 0 && args[0] == "dispatch") { Dispatch(args); return; }
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

    /// <summary>ZERO-43 focused gate for $D517 (city route, polls 1941..1977). Positive: the native
    /// price equals the original CA59−CA40−18, and the same pre-frame call reproduces the price the
    /// frame itself charged (the measure is read-only). Negative: state, slot activity, landing nibble
    /// and hook bit are perturbed and restored before the frame runs, so every condition outside the
    /// slice must refuse. A shifted expectation must not pass, otherwise the gate is vacuous.</summary>
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
        rows.WriteLine("poll,state,fallByte,native,oracle,verdict,shifted,reproducible,negative,refused");
        int exact = 0, reproduced = 0, refused = 0, shiftedMiss = 0, priced = 0, queueRefusals = 0;
        for (int poll = 1; poll <= 1977; poll++)
        {
            bool gate = poll >= 1941;
            byte savedState = player.State, savedFlags = player.Flags, savedLow = player.Objects.Slots[0].StateLow;
            bool savedActive = player.Objects.Slots[0].Active;
            var slot = player.Objects.Slots[0];
            long oracle = gate ? source[poll]["CA59"][0] - source[poll]["CA40"][0] - 18 : -1;
            int preFrame = -1;
            var negatives = new List<string>();
            if (gate)
            {
                preFrame = player.TryMeasureWorldEntryFall09Cycles(true, out int measured, out _) ? measured : -1;
                void Control(string name, Action perturb)
                {
                    perturb();
                    bool accepted = player.TryMeasureWorldEntryFall09Cycles(true, out _, out string why);
                    player.State = savedState; player.Flags = savedFlags; slot.StateLow = savedLow; slot.Active = savedActive;
                    negatives.Add(name + "=" + (accepted ? "ACCEPTED" : Safe(why)));
                    if (!accepted && why != null) refused++;
                }
                Control("state0B", () => player.State = 0x0B);
                Control("inactive", () => slot.Active = false);
                Control("landed", () => slot.StateLow = (byte)(savedLow | 0x01));
                Control("hookbit", () => player.Flags = (byte)(savedFlags | 0x20));
                if (player.TryMeasureWorldEntryFall09Cycles(null, out _, out string noQueue)) negatives.Add("noQueueOwner=ACCEPTED");
                else { negatives.Add("noQueueOwner=" + Safe(noQueue)); queueRefusals++; }
            }
            game.AdvanceFrame(NesButtons.FromByte(input[poll - 1]));
            if (!gate) continue;
            int native = player.LastPlayerDispatchCycles;
            bool ok = native == oracle && native >= 0;
            if (ok) { exact++; if (preFrame == native) reproduced++; }
            if (oracle + 1 != native) shiftedMiss++;
            if (native >= 0) priced++;
            rows.WriteLine(string.Join(",", poll, savedState.ToString("X2"), ((byte)player.YVelInt).ToString("X2"), native, oracle,
                ok ? "exact" : "MISMATCH", oracle + 1 == native ? "PASS-AS-TRUE" : "rejected", preFrame == native ? 1 : 0,
                Safe(string.Join(";", negatives))));
        }
        Console.WriteLine($"state09 gate: priced={priced}/37 exact={exact}/37 reproducible={reproduced}/37 shiftedRejected={shiftedMiss}/37 " +
            $"negativeRefusals={refused}/148 landingOwnerRefusals={queueRefusals} output={output}");
        if (exact != 37 || reproduced != 37 || shiftedMiss != 37 || refused != 148 || queueRefusals != 1)
        { Console.Error.WriteLine("state09 gate FAIL"); Environment.ExitCode = 1; }
        else Console.WriteLine("state09 gate PASS");
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
