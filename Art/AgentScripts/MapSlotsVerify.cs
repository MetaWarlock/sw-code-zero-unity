using System.IO;
using System.Linq;
using System.Text;
using System.Collections.Generic;

/// <summary>
/// Replays every frame of the original city-map captures through the port's real
/// bank-5 composition service: slot RAM ($0400-$05FF) and the scroll page ($FA-$FD)
/// are read straight out of ram_frames.bin, OamDispatcher.RunFrame runs with that
/// state, and the produced $0200 shadow is compared byte-for-byte against the
/// captured OAM up to the frame's own $97 cursor.
/// Content-only replay — no port physics, no invented constants.
/// </summary>
public static class MapSlotsVerify
{
    static readonly string Root = "D:/Art/SW Code Zero Unity";

    public static string Suite()
    {
        var sb = new StringBuilder();
        foreach (string cap in new[] { "probe_map2", "probe_map_nav", "probe_map_b1", "probe_flow" })
            sb.AppendLine(RunCase(cap));
        File.WriteAllText(Root + "/Art/Temp/map_slots.txt", sb.ToString());
        return sb.ToString();
    }

    static string RunCase(string name)
    {
        string dir = $"{Root}/Research/DarkwingDuck/reference/{name}";
        if (!File.Exists(dir + "/ram_frames.bin")) return name + ": NO CAPTURE";
        byte[] ram = File.ReadAllBytes(dir + "/ram_frames.bin");
        byte[] oam = File.ReadAllBytes(dir + "/oam_frames.bin");
        int frames = ram.Length / 2048;

        byte[] bank5 = File.ReadAllBytes(Root + "/Art/Unity/CodeZero/Darkwing/Data/prg_bank5.bytes");
        var data = Darkwing.Core.DataLoader.Load(
            File.ReadAllBytes(Root + "/Art/Unity/CodeZero/Darkwing/Data/chr_full.bytes"),
            File.ReadAllBytes(Root + "/Art/Unity/CodeZero/Darkwing/Data/levelsets.bytes"),
            File.ReadAllBytes(Root + "/Art/Unity/CodeZero/Darkwing/Data/screens.bytes"),
            File.ReadAllBytes(Root + "/Art/Unity/CodeZero/Darkwing/Data/levels.bytes"),
            File.ReadAllBytes(Root + "/Art/Unity/CodeZero/Darkwing/Data/anims.bytes"));
        var ppu = new Darkwing.Core.Ppu(data);
        var player = new Darkwing.Core.PlayerCore(Darkwing.Core.EmptyTileMap.Instance);
        var objects = new Darkwing.Core.ObjectSystem(data);
        var disp = new Darkwing.Core.OamDispatcher(bank5, objects, ppu, player);

        int mapFrames = 0, exact = 0, cursorBad = 0, worstFrame = -1, worstBad = 0;
        int phaseBad = 0, contentBad = 0, splitExact = 0, worldExact = 0;
        int prevVerbatimWorldExact = 0;
        int worldFailDup = 0, worldFailOther = 0;
        var dupDetail = new StringBuilder();
        var otherDetail = new StringBuilder();
        string firstContent = "", worstDetail = "";
        var fields = new Dictionary<string, int>();
        for (int f = 1; f <= frames; f++)
        {
            int b = (f - 1) * 2048;
            // The map phase is identified by its own bubble family: slots 2/3/4 seeded
            // with $73/$BB by $A4AE/$A503/$A53A and nothing else active.
            if (!IsMap(ram, b))
                continue;
            mapFrames++;

            int bad = ComposeAndDiff(objects, player, disp, ppu, ram, b, b, oam, (f - 1) * 256, false,
                                     out int ours, out int rom);
            if (ours != rom) cursorBad++;
            if (bad == 0) exact++;
            else
            {
                // Record 1 holds slot 0's first tile when the walk was forward and slot 4's when it
                // was reversed, so its tile byte gives the order for each side independently:
                // $5F = the $36 plate (slot 0 first), $C5 = a $73/$BB bubble (slot 4 first).
                byte rt = oam[(f - 1) * 256 + 5];
                int ro = OrderOf(rt), oo = OrderOf(ppu.Oam[5]);
                if (ro == 0 || oo == 0 || ro != oo) phaseBad++;
                else
                {
                    contentBad++;
                    string sig = DiffFields(ppu.Oam, oam, (f - 1) * 256, ours);
                    fields[sig] = fields.TryGetValue(sig, out int c) ? c + 1 : 1;
                    if (firstContent == "") firstContent = Detail(f, rt, ppu.Oam, oam, (f - 1) * 256, ours);
                    if (bad > worstBad)
                    {
                        worstBad = bad; worstFrame = f;
                        worstDetail = Detail(f, rt, ppu.Oam, oam, (f - 1) * 256, ours);
                    }
                }
            }

            // Model B pairs RAM[f-1]'s slot/scroll state with RAM[f]'s $95 parity and $97 cursor,
            // i.e. the deferred NMI publish the world phase already models.
            if (f > 1 && ComposeAndDiff(objects, player, disp, ppu, ram, b - 2048, b, oam, (f - 1) * 256,
                                        false, out _, out _) == 0)
                splitExact++;
            // Model C is that same deferred walk expressed with the world's own polarity: content,
            // parity and cursor all from f-1. It is algebraically the same direction as B; if it
            // matches too, the map needs no special-case flag at all.
            if (f > 1)
            {
                int c = ComposeAndDiff(objects, player, disp, ppu, ram, b - 2048, b - 2048, oam, (f - 1) * 256,
                                       false, out _, out _);
                if (c == 0) worldExact++;
                else if (ram[b + 0x95] == ram[b - 2048 + 0x95])
                {
                    // The capturer sampled $95 twice inside one engine pass, so no fixed shift
                    // can align this frame's content; counted separately from real failures.
                    worldFailDup++;
                    if (dupDetail.Length < 900) dupDetail.Append($"f{f}({c}) ");
                }
                else
                {
                    worldFailOther++;
                    if (otherDetail.Length < 900)
                        otherDetail.Append($"f{f}({c}) ");
                }
            }
            // Model G: the deferred walk with the anim fields taken verbatim from f-1, i.e. the
            // walk does no second advance. This is the model that matched (2399/2400).
            if (f > 1)
            {
                if (ComposeAndDiff(objects, player, disp, ppu, ram, b - 2048, b - 2048, oam, (f - 1) * 256,
                                  true, out _, out _) == 0) prevVerbatimWorldExact++;
            }
        }
        if (mapFrames == 0) return name + ": no map frames";
        var sigs = new List<string>(fields.Keys);
        sigs.Sort();
        var top = new StringBuilder();
        foreach (string s in sigs.Take(8)) top.Append($"{s}×{fields[s]}  ");
        return $"{name}: map frames {mapFrames}, exact OAM {exact}/{mapFrames}, " +
               $"cursor mismatches {cursorBad}, order-phase failures {phaseBad}, " +
               $"same-order content failures {contentBad}\n" +
               $"  model B (content f-1, parity f) {splitExact} | model C (all f-1) {worldExact} | " +
               $"model G (f-1 verbatim anim) {prevVerbatimWorldExact} of {mapFrames}\n" +
               $"  {ChainedPass(objects, player, disp, ppu, ram, oam, frames)}\n" +
               $"  model C failures: duplicate-$95 sampling {worldFailDup} (frames {dupDetail}), other {worldFailOther} (frames {otherDetail})\n" +
               $"  content field signatures: {top}\n" +
               $"  first content: {firstContent}\n" +
               $"  worst f{worstFrame} ({worstBad} bytes) {worstDetail}";
    }

    /// <summary>Model H — the runtime chain, no re-seeding: load slot state once at the head of
    /// a map segment, then for each frame publish the previous walk's shadow, compare it against
    /// the captured OAM, and run the port's own walk with the ROM's $95 tick. This is what the
    /// port actually does on the map, so anim arithmetic ($8100-$8141) is exercised too.</summary>
    static string ChainedPass(Darkwing.Core.ObjectSystem objects, Darkwing.Core.PlayerCore player,
                              Darkwing.Core.OamDispatcher disp, Darkwing.Core.Ppu ppu,
                              byte[] ram, byte[] oam, int frames)
    {
        int frames3 = 0, exact = 0, animBad = 0, cursorBad = 0, segments = 0;
        string first = "", animFirst = "";
        bool live = false;
        for (int f = 1; f <= frames; f++)
        {
            int b = (f - 1) * 2048;
            if (!IsMap(ram, b)) { live = false; continue; }
            if (!live)
            {
                if (f == 1) continue;                     // need f-1 as the pre-walk state
                SeedFrom(objects, player, disp, ram, b - 2048, false);
                disp.RunFrame(ram[b - 2048 + 0x95]);      // the walk that produced this frame's OAM
                segments++;
                live = true;
            }
            disp.PublishToOam();
            frames3++;
            int bad = 0;
            for (int i = 4; i < 256; i++) if (ppu.Oam[i] != oam[b / 8 + i]) bad++;
            if (bad == 0) exact++;
            else if (first == "" && b >= 2048) first = $"f{f}: {bad} bytes, ours t=${ppu.Oam[5]:X2} rom t=${oam[b / 8 + 5]:X2}";

            disp.RunFrame(ram[b + 0x95]);
            if (disp.OamCursorBytes != ram[b + 0x97]) cursorBad++;
            bool aBad = false;
            for (int i = 0; i < 16; i++)
            {
                var s = objects.Slots[i];
                if (s.AnimFrame != ram[b + 0x0410 + i] || s.AnimTimer != ram[b + 0x0430 + i]) aBad = true;
            }
            if (aBad)
            {
                animBad++;
                if (animFirst == "")
                    for (int i = 0; i < 6; i++)
                        if (objects.Slots[i].AnimFrame != ram[b + 0x0410 + i] || objects.Slots[i].AnimTimer != ram[b + 0x0430 + i])
                        {
                            animFirst = $"f{f} slot{i} ours fr=${objects.Slots[i].AnimFrame:X2} tm=${objects.Slots[i].AnimTimer:X2} " +
                                        $"rom fr=${ram[b + 0x0410 + i]:X2} tm=${ram[b + 0x0430 + i]:X2} an=${ram[b + 0x0420 + i]:X2}";
                            break;
                        }
            }
        }
        return $"model H chained: {exact}/{frames3} exact over {segments} segment(s), " +
               $"cursor mismatches {cursorBad}, anim-page drift {animBad} ({animFirst}) [{first}]";
    }

    /// <summary>The map phase identifies itself by its bubble family in slots 2/3/4
    /// ($73/$BB seeded by $A4AE/$A503/$A53A).</summary>
    static bool IsMap(byte[] ram, int b) =>
        (ram[b + 0x0422] is 0x73 or 0xBB) && (ram[b + 0x0423] is 0x73 or 0xBB) && (ram[b + 0x0424] is 0x73 or 0xBB);

    /// <summary>Walk order read from record 1's tile: +1 forward (slot 0's plate first),
    /// -1 reversed (slot 4's bubble first), 0 when the frame says nothing.</summary>
    static int OrderOf(byte firstTile) => firstTile switch { 0x5F => 1, 0xC5 => -1, _ => 0 };

    /// <summary>Which OAM field kinds differ (y/tile/attr/x), plus the total byte count.</summary>
    static string DiffFields(byte[] ours, byte[] rom, int romBase, int limit)
    {
        int[] n = new int[4];
        int total = 0;
        for (int i = 4; i < (limit < 256 ? limit : 256); i++)
            if (ours[i] != rom[romBase + i]) { n[i % 4]++; total++; }
        return $"y{n[0]} t{n[1]} a{n[2]} x{n[3]} ({total})";
    }

    static string Detail(int f, byte rt, byte[] ours, byte[] rom, int romBase, int limit)
    {
        var d = new StringBuilder($"f{f} rt=${rt:X2} ours=${ours[5]:X2}: ");
        for (int i = 4; i < (limit < 256 ? limit : 256); i++)
            if (ours[i] != rom[romBase + i])
            {
                d.Append($"rec{i / 4}.{i % 4}:ours${ours[i]:X2}/rom${rom[romBase + i]:X2} ");
                if (d.Length > 420) break;
            }
        return d.ToString();
    }

    /// <summary>Seed the 16 slots plus the scroll page from one RAM frame, run the real bank-5
    /// dispatcher, and count differing shadow bytes below the produced cursor.
    /// cb selects the frame the slot/scroll state comes from, pb the frame $95 comes from.</summary>
    static int ComposeAndDiff(Darkwing.Core.ObjectSystem objects, Darkwing.Core.PlayerCore player,
                              Darkwing.Core.OamDispatcher disp, Darkwing.Core.Ppu ppu,
                              byte[] ram, int cb, int pb, byte[] oam, int oamBase,
                              bool suppressAdvance,
                              out int ours, out int rom)
    {
        SeedFrom(objects, player, disp, ram, cb, suppressAdvance, mapPhase);

        disp.RunFrame(ram[pb + 0x95]);
        disp.PublishToOam();

        ours = disp.OamCursorBytes;
        rom = ram[pb + 0x97];
        int bad = 0;
        // Record 0 is never composed ($97 starts at $04); only its Y byte is wiped, so its
        // tile/attr/X keep whatever the previous screen left. Excluded from the diff.
        for (int i = 4; i < (ours < 256 ? ours : 256); i++)
            if (ppu.Oam[i] != oam[oamBase + i]) bad++;
        return bad;
    }

    /// <summary>Load the 16 slots plus the scroll page from one sampled RAM frame.
    /// suppressAdvance sets $0430 bit 7 ($8105 BMI) so the compose reads the sampled $0410
    /// verbatim instead of stepping it once more.</summary>
    static void SeedFrom(Darkwing.Core.ObjectSystem objects, Darkwing.Core.PlayerCore player,
                         Darkwing.Core.OamDispatcher disp, byte[] ram, int b,
                         bool suppressAdvance)
    {
        for (int i = 0; i < 16; i++)
        {
            var s = objects.Slots[i];
            s.Active = (ram[b + 0x04D0 + i] & 0x80) != 0;
            s.StateLow = ram[b + 0x04D0 + i];
            s.Flags = ram[b + 0x0400 + i];
            s.AnimFrame = ram[b + 0x0410 + i];
            s.AnimNumber = ram[b + 0x0420 + i];
            s.AnimTimer = suppressAdvance ? (byte)(ram[b + 0x0430 + i] | 0x80) : ram[b + 0x0430 + i];
            s.ScreenY = ram[b + 0x0440 + i];
            s.ScreenX = ram[b + 0x0450 + i];
            s.T480 = ram[b + 0x0480 + i];
            s.Behavior = ram[b + 0x04E0 + i];
            s.X = (ushort)(ram[b + 0x04F0 + i] | (ram[b + 0x0500 + i] << 8));
            s.Y = (ushort)(ram[b + 0x0510 + i] | (ram[b + 0x0520 + i] << 8));
            s.BehaviorParam = ram[b + 0x0570 + i];
            s.HitFlash = ram[b + 0x04C0 + i];
            s.PlacementIndex = ram[b + 0x0590 + i];
        }
        player.ScrollX = (ushort)(ram[b + 0xFC] | (ram[b + 0xFD] << 8));
        player.ScrollY = ram[b + 0xFA];
        player.ScrollYRow = ram[b + 0xFB];
        player.InvulnFrames = 0;
        disp.HudActive = false;      // $C887 never makes slot 1 the HUD on the map
        disp.MissionCardHold = false;
    }
}
