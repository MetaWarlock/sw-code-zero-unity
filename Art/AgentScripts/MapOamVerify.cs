using System.IO;
using System.Text;
using Darkwing.Core;

/// <summary>
/// G32.1 acceptance: the city-map sprite layer driven by the real port (DarkwingGame.Step →
/// StepMapFrame), compared byte-for-byte against the original-ROM map captures.
///
/// Pairing is the deferred NMI one the world phase already uses: the shadow composed by the walk
/// of frame f-1 (slot pages and $95 taken from f-1, verified as model G in MapSlotsVerify:
/// 2399/2400 on probe_map2) is what the NMI of frame f transfers, so the port step that walks
/// with the ROM's f-1 state is compared against oam_frames.bin record f.
///
/// Free-running clocks limit the chain test: a capture that starts mid-run leaves the plate ($36),
/// the caret ($39) and the cursor blink ($BB) at a phase the port cannot know, so only a run that
/// begins at the ROM's map-entry frame (probe_flow f1819) can be expected to match throughout.
/// The chooser's own $6C arithmetic is taken from the capture (it is GameFlow's, verified by
/// FlowVerify) and only idle map frames ($14 == $00) are compared, so no press semantics enter.
///
/// Entry: MapOamVerify.Run
/// </summary>
public static class MapOamVerify
{
    private const string Root = "D:/Art/SW Code Zero Unity";
    private const string RefDir = Root + "/Art/Research/DarkwingDuck/reference";

    private static NesButtons Pad(byte bits) => new NesButtons
    {
        A = (bits & 0x80) != 0, B = (bits & 0x40) != 0,
        Select = (bits & 0x20) != 0, Start = (bits & 0x10) != 0,
        Up = (bits & 0x08) != 0, Down = (bits & 0x04) != 0,
        Left = (bits & 0x02) != 0, Right = (bits & 0x01) != 0,
    };

    public static string Run()
    {
        var sb = new StringBuilder();
        foreach (var probe in new[] { "probe_map2", "probe_map_b1", "probe_map_nav", "probe_flow" })
        {
            sb.Append(Pass(probe)).Append('\n');
            sb.Append(PairedPass(probe)).Append('\n');
        }
        File.WriteAllText(Root + "/Art/Temp/map_oam_report.txt", sb.ToString());
        return sb.ToString();
    }

    /// <summary>Paired passes only, capped, with a dump of the ROM's active slots for the first
    /// mismatching frame — the residual diffs sit at record 43+/50+, so the family that produces
    /// them has to be identified before the seed can be extended.</summary>
    public static string RunPaired()
    {
        var sb = new StringBuilder();
        foreach (var probe in new[] { "probe_map2", "probe_map_b1", "probe_map_nav" })
            sb.Append(PairedPass(probe, 220)).Append('\n');
        File.WriteAllText(Root + "/Art/Temp/map_paired_report.txt", sb.ToString());
        return sb.ToString();
    }

    /// <summary>Every slot the capture holds active at the sampled frame, with the pages the
    /// walk reads ($0400 flags, $0420 anim, $04F0/$0510 position).</summary>
    private static string SlotDump(byte[] ram, int b)
    {
        var sb = new StringBuilder();
        for (int i = 0; i < ObjectSystem.SlotCount; i++)
        {
            if ((ram[b + 0x04D0 + i] & 0x80) == 0) continue;
            sb.Append($"s{i}:${ram[b + 0x04D0 + i]:X2} fl=${ram[b + 0x0400 + i]:X2} " +
                      $"an=${ram[b + 0x0420 + i]:X2} fr=${ram[b + 0x0410 + i]:X2} tm=${ram[b + 0x0430 + i]:X2} " +
                      $"xy=${ram[b + 0x0450 + i]:X2},${ram[b + 0x0440 + i]:X2} " +
                      $"pos=${ram[b + 0x04F0 + i]:X2}${ram[b + 0x0500 + i]:X2},${ram[b + 0x0510 + i]:X2}${ram[b + 0x0520 + i]:X2} " +
                      $"bh=${ram[b + 0x04E0 + i]:X2} 480=${ram[b + 0x0480 + i]:X2}; ");
        }
        sb.Append($"$38=${ram[b + 0x38]:X2} $69=${ram[b + 0x69]:X2} $6A=${ram[b + 0x6A]:X2} " +
                  $"$6C=${ram[b + 0x6C]:X2} $95=${ram[b + 0x95]:X2} $97=${ram[b + 0x97]:X2}");
        return sb.ToString();
    }

    /// <summary>End-to-end paired pass: DarkwingGame.Step → StepMapFrame with the ROM's own
    /// per-frame slot pages stamped in, so the only thing under test is the port's map walk
    /// (cull + anim clock + OamCompose + chooser seed) and not the phase its free-running
    /// clocks happen to start at. Timer bit 7 is set because model G in MapSlotsVerify proved
    /// the capture already holds the post-walk $0410/$0430 ($8105 BMI skips the second advance).
    /// The same frame is then repeated without bit 7 to show what a double advance costs.</summary>
    private static string PairedPass(string probe, int cap = 0)
    {
        var dir = RefDir + "/" + probe;
        if (!File.Exists(dir + "/ram_frames.bin")) return $"{probe}: no capture";
        var ram = File.ReadAllBytes(dir + "/ram_frames.bin");
        var oam = File.ReadAllBytes(dir + "/oam_frames.bin");
        int frames = ram.Length / 2048;

        int runStart = -1, runLen = 0, bestStart = -1, bestLen = 0;
        for (int f = 1; f <= frames; f++)
        {
            int b = (f - 1) * 2048;
            bool map = ram[b + 0x0420] == 0x36 && (ram[b + 0x0422] is 0x73 or 0xBB);
            if (map && ram[b + 0x14] == 0)
            {
                if (runLen == 0) runStart = f;
                runLen++;
                if (runLen > bestLen) { bestLen = runLen; bestStart = runStart; }
            }
            else runLen = 0;
        }
        if (bestLen < 8) return $"{probe}: paired — no idle run";

        if (cap > 0 && bestLen > cap) bestLen = cap;

        var sb = new StringBuilder();
        for (int variant = 0; variant < 2; variant++)
        {
            bool suppress = variant == 0;
            var game = NewGame();
            game.MapTier = ram[(bestStart - 1) * 2048 + 0x38];
            game.LoadMapScreen();
            int exact = 0, curBad = 0, worst = 0, worstFrame = -1;
            string first = "", worstDetail = "", dump = "";
            for (int f = bestStart; f < bestStart + bestLen; f++)
            {
                int b = (f - 1) * 2048;
                if (b - 2048 < 0) continue;
                Stamp(game, ram, b - 2048, suppress);
                game.FrameParity = (byte)(ram[b - 2048 + 0x95] - 1);
                game.MapCursorBubble = ram[b - 2048 + 0x6C];
                game.Step(Pad(0));                 // parity → f-1 value, walk → this frame's shadow
                game.Oam.PublishToOam();
                int bad = Diff(game.Ppu.Oam, oam, b / 8, out string d);
                if (bad == 0) exact++;
                else
                {
                    if (first == "") { first = $"f{f}({bad}): {d}"; dump = SlotDump(ram, b - 2048); }
                    if (bad > worst) { worst = bad; worstFrame = f; worstDetail = d; }
                }
                if (game.Oam.OamCursorBytes != ram[b + 0x97]) curBad++;
            }
            sb.Append($"{probe}: paired {(suppress ? "timer|0x80 (model G)" : "plain timer")} " +
                      $"exact {exact}/{bestLen}, $97 mismatches {curBad}\n" +
                      $"  first {first}\n  worst f{worstFrame} ({worst}) {worstDetail}\n" +
                      $"  slots@first-diff {dump}\n");
        }
        return sb.ToString().TrimEnd();
    }

    /// <summary>Copies one captured frame's object pages into the port's live slots.</summary>
    private static void Stamp(DarkwingGame game, byte[] ram, int b, bool suppressTimer)
    {
        for (int i = 0; i < ObjectSystem.SlotCount; i++)
        {
            var s = game.Objects.Slots[i];
            s.StateLow = ram[b + 0x04D0 + i];
            s.Active = (s.StateLow & 0x80) != 0;
            s.Flags = ram[b + 0x0400 + i];
            s.AnimFrame = ram[b + 0x0410 + i];
            s.AnimNumber = ram[b + 0x0420 + i];
            s.AnimTimer = suppressTimer ? (byte)(ram[b + 0x0430 + i] | 0x80) : ram[b + 0x0430 + i];
            s.ScreenY = ram[b + 0x0440 + i];
            s.ScreenX = ram[b + 0x0450 + i];
            s.T480 = ram[b + 0x0480 + i];
            s.HitFlash = ram[b + 0x04C0 + i];
            s.Behavior = ram[b + 0x04E0 + i];
            s.X = (ushort)(ram[b + 0x04F0 + i] | (ram[b + 0x0500 + i] << 8));
            s.Y = (ushort)(ram[b + 0x0510 + i] | (ram[b + 0x0520 + i] << 8));
            s.BehaviorParam = ram[b + 0x0570 + i];
            s.PlacementIndex = ram[b + 0x0590 + i];
        }
        game.Player.ScrollX = (ushort)(ram[b + 0xFC] | (ram[b + 0xFD] << 8));
        game.Player.ScrollY = ram[b + 0xFA];
        game.Player.ScrollYRow = ram[b + 0xFB];
    }

    private static string Pass(string probe)
    {
        var dir = RefDir + "/" + probe;
        if (!File.Exists(dir + "/ram_frames.bin")) return $"{probe}: no capture";
        var ram = File.ReadAllBytes(dir + "/ram_frames.bin");
        var oam = File.ReadAllBytes(dir + "/oam_frames.bin");
        int frames = ram.Length / 2048;

        // The longest run of consecutive idle map frames ($14 == $00): the chooser is live and
        // re-seeding there, and no press semantics can enter the comparison.
        int runStart = -1, runLen = 0, bestStart = -1, bestLen = 0, mapFrames = 0;
        for (int f = 1; f <= frames; f++)
        {
            int b = (f - 1) * 2048;
            bool map = ram[b + 0x0420] == 0x36 && (ram[b + 0x0422] is 0x73 or 0xBB);
            if (map) mapFrames++;
            if (map && ram[b + 0x14] == 0)
            {
                if (runLen == 0) runStart = f;
                runLen++;
                if (runLen > bestLen) { bestLen = runLen; bestStart = runStart; }
            }
            else runLen = 0;
        }
        if (bestLen < 8) return $"{probe}: {mapFrames} map frames, no idle run to compare";

        var game = NewGame();
        int b0 = (bestStart - 1) * 2048;
        game.MapTier = ram[b0 + 0x38];
        game.LoadMapScreen();
        game.FrameParity = (byte)(ram[b0 + 0x95] - 1);

        int exact = 0, curBad = 0, pageBad = 0;
        string firstDiff = "", lateDiff = "", firstCur = "", firstPage = "";
        for (int f = bestStart; f < bestStart + bestLen; f++)
        {
            int b = (f - 1) * 2048;
            game.MapCursorBubble = ram[b + 0x6C];        // chooser state given; sprite layer measured
            game.Step(Pad(0));                            // ticks $95 to the ROM's f value
            // The NMI of frame f transfers the shadow the walk of f-1 composed.
            int bad = Diff(game.Ppu.Oam, oam, b / 8, out string d);
            if (bad == 0) exact++;
            else if (firstDiff == "") firstDiff = $"f{f}({bad}): {d}";
            if (f == bestStart + 90 && bad != 0) lateDiff = $"f{f}({bad}): {d}";
            if (game.Oam.OamCursorBytes != ram[b + 0x97])
            {
                curBad++;
                if (firstCur == "") firstCur = $"f{f} ours=${game.Oam.OamCursorBytes:X2} rom=${ram[b + 0x97]:X2}";
            }
            if (PageDiff(game, ram, b - 2048) != 0)
            {
                pageBad++;
                if (firstPage == "") firstPage = PageDetail(game, ram, b - 2048);
            }
        }
        return $"{probe}: {mapFrames} map frames, idle run f{bestStart}..{bestStart + bestLen - 1}, " +
               $"compared {bestLen}\n" +
               $"  OAM exact {exact}/{bestLen}, $97 mismatches {curBad} ({firstCur}), " +
               $"slot-page mismatches vs f-1 {pageBad}\n" +
               $"  first diff {firstDiff}\n  late diff  {lateDiff}\n  first page {firstPage}";
    }

    /// <summary>Differing bytes below record 0, with the first few as rec.field ours/rom.</summary>
    private static int Diff(byte[] ours, byte[] rom, int baseIdx, out string detail)
    {
        int bad = 0;
        var sb = new StringBuilder();
        int end = System.Math.Min(256, rom.Length - baseIdx);
        for (int i = 4; i < end; i++)
            if (ours[i] != rom[baseIdx + i])
            {
                bad++;
                if (sb.Length < 140) sb.Append($"rec{i / 4}.{i % 4}:${ours[i]:X2}/${rom[baseIdx + i]:X2} ");
            }
        detail = sb.ToString();
        return bad;
    }

    private static int PageDiff(DarkwingGame game, byte[] ram, int b)
    {
        if (b < 0) return 0;
        int bad = 0;
        for (int i = 0; i < 5; i++)
            if (game.Objects.Slots[i].AnimFrame != ram[b + 0x0410 + i] ||
                game.Objects.Slots[i].AnimNumber != ram[b + 0x0420 + i] ||
                game.Objects.Slots[i].AnimTimer != ram[b + 0x0430 + i] ||
                game.Objects.Slots[i].ScreenX != ram[b + 0x0450 + i] ||
                game.Objects.Slots[i].ScreenY != ram[b + 0x0440 + i] ||
                game.Objects.Slots[i].Flags != ram[b + 0x0400 + i] ||
                game.Objects.Slots[i].StateLow != ram[b + 0x04D0 + i]) bad++;
        return bad;
    }

    private static string PageDetail(DarkwingGame game, byte[] ram, int b)
    {
        if (b < 0) return "";
        for (int i = 0; i < 5; i++)
        {
            var s = game.Objects.Slots[i];
            if (s.AnimFrame != ram[b + 0x0410 + i] || s.AnimNumber != ram[b + 0x0420 + i] ||
                s.AnimTimer != ram[b + 0x0430 + i] || s.ScreenX != ram[b + 0x0450 + i] ||
                s.ScreenY != ram[b + 0x0440 + i] || s.Flags != ram[b + 0x0400 + i] ||
                s.StateLow != ram[b + 0x04D0 + i])
                return $"slot{i} ours an=${s.AnimNumber:X2} fr=${s.AnimFrame:X2} tm=${s.AnimTimer:X2} " +
                       $"xy=${s.ScreenX:X2},{s.ScreenY:X2} fl=${s.Flags:X2} st=${s.StateLow:X2} | " +
                       $"rom an=${ram[b + 0x0420 + i]:X2} fr=${ram[b + 0x0410 + i]:X2} tm=${ram[b + 0x0430 + i]:X2} " +
                       $"xy=${ram[b + 0x0450 + i]:X2},{ram[b + 0x0440 + i]:X2} fl=${ram[b + 0x0400 + i]:X2} st=${ram[b + 0x04D0 + i]:X2}";
        }
        return "";
    }

    private static DarkwingGame NewGame()
    {
        var data = DataLoader.Load(
            File.ReadAllBytes(Root + "/Art/Unity/CodeZero/Darkwing/Data/chr_full.bytes"),
            File.ReadAllBytes(Root + "/Art/Unity/CodeZero/Darkwing/Data/levelsets.bytes"),
            File.ReadAllBytes(Root + "/Art/Unity/CodeZero/Darkwing/Data/screens.bytes"),
            File.ReadAllBytes(Root + "/Art/Unity/CodeZero/Darkwing/Data/levels.bytes"),
            File.ReadAllBytes(Root + "/Art/Unity/CodeZero/Darkwing/Data/anims.bytes"));
        var game = new DarkwingGame(data);
        game.Bank5 = File.ReadAllBytes(Root + "/Art/Unity/CodeZero/Darkwing/Data/prg_bank5.bytes");
        game.Bank7 = File.ReadAllBytes(Root + "/Art/Unity/CodeZero/Darkwing/Data/world_bank7.bytes");
        game.Bank3 = File.ReadAllBytes(Root + "/Art/Unity/CodeZero/Darkwing/Data/audio_bank3.bytes");
        game.Bank6 = File.ReadAllBytes(Root + "/Art/Unity/CodeZero/Darkwing/Data/prg_bank6.bytes");
        game.Bank2 = File.ReadAllBytes(Root + "/Art/Unity/CodeZero/Darkwing/Data/prg_bank2.bytes");
        game.ObjectTypeTable = File.ReadAllBytes(Root + "/Art/Unity/CodeZero/Darkwing/Data/object_types.bytes");
        game.MapNametable = File.ReadAllBytes(Root + "/Art/Unity/CodeZero/Darkwing/Data/map_nametable.bytes");
        game.MapPalette = File.ReadAllBytes(Root + "/Art/Unity/CodeZero/Darkwing/Data/map_palette.bytes");
        game.MapChrBank0 = WorldState.MapChrBank;
        game.MapChrBank1 = WorldState.MapChrBank;
        game.Ppu.WriteRegister(0x2000, 0x16);
        return game;
    }
}
