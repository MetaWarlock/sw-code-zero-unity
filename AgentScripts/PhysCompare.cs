using System.IO;
using UnityEngine;

public static class PhysCompare
{
    /// <summary>
    /// Golden physics comparison through the full DarkwingGame stack (movement +
    /// anim advance + slot sync), replaying the captured original input schedule.
    /// </summary>
    private static string Compare(string name, int frames, System.Func<int, byte> input,
        int seedFrame = 0, bool camera = false, bool enemies = false, int world = 0)
    {
        string root = "D:/Art/SW Code Zero Unity";
        byte[] ramAll = File.ReadAllBytes(root + $"/Research/DarkwingDuck/reference/gold_{name}/ram_frames.bin");
        int avail = ramAll.Length / 2048;

        var data = Darkwing.Core.DataLoader.Load(
            File.ReadAllBytes(root + "/Assets/Darkwing/Data/chr_full.bytes"),
            File.ReadAllBytes(root + "/Assets/Darkwing/Data/levelsets.bytes"),
            File.ReadAllBytes(root + "/Assets/Darkwing/Data/screens.bytes"),
            File.ReadAllBytes(root + "/Assets/Darkwing/Data/levels.bytes"),
            File.ReadAllBytes(root + "/Assets/Darkwing/Data/anims.bytes"));
        var game = new Darkwing.Core.DarkwingGame(data);
        game.Bank5 = File.ReadAllBytes(root + "/Assets/Darkwing/Data/prg_bank5.bytes");
        var decoder = new Darkwing.Core.RoomDecoder(
            File.ReadAllBytes(root + "/Assets/Darkwing/Data/prg_bank0.bytes"),
            File.ReadAllBytes(root + "/Assets/Darkwing/Data/prg_bank1.bytes"),
            File.ReadAllBytes(root + "/Assets/Darkwing/Data/world_bank7.bytes"),
            File.ReadAllBytes(root + "/Assets/Darkwing/Data/levelsets.bytes"));
        var world0 = decoder.GetWorld((byte)world);
        var map = new Darkwing.Core.LevelTileMap(decoder, game.Ppu, world0);
        map.StreamColumns(0);
        game.Player.TileMap = map;
        game.TileMap = map;
        game.WireWorldFeed();
        if (enemies)
        {
            game.Bank6 = File.ReadAllBytes(root + "/Assets/Darkwing/Data/prg_bank6.bytes");
            game.ObjectTypeTable = File.ReadAllBytes(root + "/Assets/Darkwing/Data/object_types.bytes");
            // RunWorldServices only spawns when a level def is live; the route golden needs
            // the mission-1 placements so the room-0x02 walker exists for the f337 contact.
            game.CurrentLevel = data.Levels[world];
        }

        // Seed from the golden frame `seedFrame`.
        byte[] ram0 = new byte[2048];
        System.Array.Copy(ramAll, seedFrame * 2048, ram0, 0, 2048);
        var p = game.Player;
        p.X = (ushort)(ram0[0x4F0] | (ram0[0x500] << 8));
        p.Y = (ushort)(ram0[0x510] | (ram0[0x520] << 8));
        p.YVelFrac = ram0[0x550];
        p.YVelInt = (sbyte)ram0[0x560];
        p.YFrac = ram0[0x460];
        p.XFrac = ram0[0x470];
        p.XVelFrac = ram0[0x530];
        p.XVelInt = ram0[0x540];
        p.State = ram0[0x50];
        p.ScrollX = (ushort)(ram0[0xFC] | (ram0[0xFD] << 8));
        p.ScrollYRow = ram0[0xFB];
        p.Flags = ram0[0x400];
        // Camera/level-table state the hook mechanic reads ($FA, $22, $2E, $2D).
        p.ScrollY = ram0[0xFA];
        p.CellIndex = ram0[0x22];
        p.CellIndexNext = ram0[0x23];
        p.ScrollState = ram0[0x2B];
        p.ScrollStep = ram0[0x10];
        p.ScrollMode = ram0[0x2F];
        p.RowStride = ram0[0x2E];
        p.BottomRow = ram0[0x2D];
        // $E63C driver state: the cursor phase is captured state, the step rule is the port's.
        p.StageCursor = ram0[0x25];
        p.LastRedrawScroll = ram0[0x28];
        p.PrevScrollMode = ram0[0x20];
        p.PrevPlayerX = (ushort)(ram0[0x6E] | (ram0[0x6F] << 8));
        var s0 = game.Objects.Slots[0];
        if (enemies)
        {
            p.StateTimer = ram0[0x51];
            p.SavedState = ram0[0x58];
            p.InvulnFrames = ram0[0x7C];
            p.DirBits = ram0[0x52];
            game.FrameParity = ram0[0x95];
            for (int s = 0; s < 16; s++)
            {
                var sl = game.Objects.Slots[s];
                sl.Flags = ram0[0x400 + s];
                sl.AnimFrame = ram0[0x410 + s];
                sl.AnimNumber = ram0[0x420 + s];
                sl.AnimTimer = ram0[0x430 + s];
                sl.ScreenY = ram0[0x440 + s];
                sl.ScreenX = ram0[0x450 + s];
                sl.YFrac = ram0[0x460 + s];
                sl.XFrac = ram0[0x470 + s];
                sl.T480 = ram0[0x480 + s];
                sl.T490 = ram0[0x490 + s];
                sl.T4A0 = ram0[0x4A0 + s];
                sl.T4B0 = ram0[0x4B0 + s];
                sl.HitFlash = ram0[0x4C0 + s];
                sl.StateLow = ram0[0x4D0 + s];
                sl.Active = (ram0[0x4D0 + s] & 0x80) != 0;
                sl.Behavior = ram0[0x4E0 + s];
                sl.X = (ushort)(ram0[0x4F0 + s] | (ram0[0x500 + s] << 8));
                sl.Y = (ushort)(ram0[0x510 + s] | (ram0[0x520 + s] << 8));
                sl.XVelFrac = ram0[0x530 + s];
                sl.XVelInt = ram0[0x540 + s];
                sl.YVelFrac = ram0[0x550 + s];
                sl.YVelInt = (sbyte)ram0[0x560 + s];
                sl.BehaviorParam = ram0[0x570 + s];
                sl.Direction = ram0[0x580 + s];
                sl.PlacementIndex = ram0[0x590 + s];
                sl.Health = ram0[0x5A0 + s];
            }
            // $0340 = СБОРАННЫЕ размещения (бит ставит только pickup-путь через $FC29),
            // не «заспавненные»: захват начинается в середине уровня, поэтому уже
            // собранные предметы нельзя возродить в отыгрываемом прогоне.
            game.Objects.SeedSpawnMask(ram0);
            map.LoadRow(p.ScrollYRow);
            map.StreamColumns(p.ScrollX);
        }
        else
        {
            s0.AnimNumber = ram0[0x420];
            s0.AnimFrame = ram0[0x410];
            s0.AnimTimer = ram0[0x430];
        }

        // Per-field first-divergence report. The strict RESULT below is unchanged; this
        // only localises WHICH field breaks first, so a known rendering-cadence residual
        // (e.g. $23) cannot hide a real physics divergence further down the same capture.
        var fieldNames = new[] { "X", "Y", "$50 state", "$FA scrollY", "$FB row",
                                 "$22 cell", "$23 next", "$2B scrollstate", "$25 streamcursor" };
        var firstBad = new int[fieldNames.Length];
        var badPort = new int[fieldNames.Length];
        var badOrig = new int[fieldNames.Length];
        for (int i = 0; i < fieldNames.Length; i++) firstBad[i] = -1;

        var sb = new System.Text.StringBuilder();
        sb.Append($"[player_mechanics] {name}: ");
        int firstDiv = -1;
        int matchFrames = 0;
        for (int f = seedFrame + 1; f < avail; f++)
        {
            var pad = Darkwing.Core.NesButtons.FromByte(input(f));
            game.Step(pad);
            byte[] g = new byte[2048];
            System.Array.Copy(ramAll, f * 2048, g, 0, 2048);
            int gx = g[0x4F0] | (g[0x500] << 8);
            int gy = g[0x510] | (g[0x520] << 8);
            if (f - seedFrame <= 2 || (f >= 238 && f <= 262) || (f >= 348 && f <= 366))
            {
                string List(byte[] r)
                {
                    var s = new System.Text.StringBuilder();
                    for (int sl = 1; sl < 15; sl++)
                        if ((r[0x4D0 + sl] & 0x80) != 0)
                            s.Append($"{sl:X2}:{r[0x4F0 + sl] | (r[0x500 + sl] << 8):X4},{r[0x510 + sl] | (r[0x520 + sl] << 8):X4}/an{r[0x420 + sl]:X2}bh{r[0x4E0 + sl]:X2}st{r[0x4D0 + sl]:X2}t{r[0x480 + sl]:X2}d{r[0x580 + sl]:X2}pl{r[0x590 + sl]:X2} ");
                    return s.ToString();
                }
                byte[] pr = new byte[2048];
                for (int sl = 0; sl < 15; sl++)
                {
                    var sl2 = game.Objects.Slots[sl];
                    if (!sl2.Active) continue;
                    pr[0x4D0 + sl] = (byte)(0x80 | sl2.StateLow);
                    pr[0x4E0 + sl] = sl2.Behavior;
                    pr[0x480 + sl] = sl2.T480;
                    pr[0x580 + sl] = sl2.Direction;
                    pr[0x590 + sl] = sl2.PlacementIndex;
                    pr[0x4F0 + sl] = (byte)(sl2.X & 0xFF); pr[0x500 + sl] = (byte)(sl2.X >> 8);
                    pr[0x510 + sl] = (byte)(sl2.Y & 0xFF); pr[0x520 + sl] = (byte)(sl2.Y >> 8);
                    pr[0x420 + sl] = sl2.AnimNumber;
                }
                // Spawner diagnostic ($80DA/$80FC): the placement records near the
                // divergence, the port's own window test inputs, and its live $0590 set.
                int scx = p.ScrollX, scy = (p.ScrollYRow << 8) | p.ScrollY;
                var sp = new System.Text.StringBuilder(
                    $"[SP scx={scx:X4} scy={scy:X4}");
                foreach (int pl in game.CurrentLevel?.Objects != null ? new[] { 2, 3, 4, 5, 6 } : System.Array.Empty<int>())
                {
                    var rec = game.CurrentLevel.Objects[pl];
                    int px = (rec.ScreenX << 8) | rec.X, py = (rec.ScreenY << 8) | rec.Y;
                    int dxb = (px & 0xFF) - (scx & 0xFF), dyb = (py & 0xFF) - (scy & 0xFF);
                    bool win = ((px >> 8) - (scx >> 8) - (dxb < 0 ? 1 : 0)) == 0
                            && ((py >> 8) - (scy >> 8) - (dyb < 0 ? 1 : 0)) == 0;
                    int slot = -1;
                    for (int sl = 1; sl < 15; sl++)
                        if (game.Objects.Slots[sl].Active && game.Objects.Slots[sl].PlacementIndex == pl) { slot = sl; break; }
                    sp.Append($" {pl:X2}:({px:X4},{py:X4})t{rec.Type:X2}w{(win ? 1 : 0)}s{slot:X2}");
                }
                int freeSlot = -1;
                for (int sl = 14; sl >= 4; sl--)
                    if (!game.Objects.Slots[sl].Active) { freeSlot = sl; break; }
                sp.Append($" mask0={game.Objects.SpawnedBitmask(0):X2} free={freeSlot:X2}]");
                sb.Append(sp.ToString() + " ");
                sb.Append($"[f{f} P({p.X:X4},{p.Y:X4}) st={p.State:X2} FA={p.ScrollY:X2} FB={p.ScrollYRow:X2} c={p.CellIndex:X2}/{p.CellIndexNext:X2} 25={p.StageCursor:X2}/{g[0x25]:X2} 28={p.LastRedrawScroll:X2}/{g[0x28]:X2} 23o={g[0x23]:X2} 2B={p.ScrollState:X2} 10={p.ScrollStep:X2} 2F={p.ScrollMode:X2} vx={p.XVelInt},{p.XVelFrac:X2} xfc={s0.XFrac:X2} at={s0.AnimTimer:X2} pf={s0.AnimFrame} dur={(game.Oam == null ? 0 : game.Oam.AnimHeader(s0.AnimNumber).duration)} iv={p.InvulnFrames:X2} vy={p.YVelInt},{p.YVelFrac:X2} ffc={p.YFrac:X2} hl={p.HitLadder} D={p.DeathFlag} scx={p.ScrollX:X4}/{g[0xFC]:X2},{g[0xFD]:X2} | O({gx:X4},{gy:X4}) st={g[0x50]:X2} FA={g[0xFA]:X2} FB={g[0xFB]:X2} c={g[0x22]:X2} 2B={g[0x2B]:X2} 10={g[0x10]:X2} 2F={g[0x2F]:X2} vx={(sbyte)g[0x540]},{g[0x530]:X2} xfc={g[0x470]:X2} at={g[0x430]:X2} vy={(sbyte)g[0x560]},{g[0x550]:X2} ffc={g[0x460]:X2} ivc={g[0x7C]:X2} af={s0.AnimFrame}/{g[0x410]} an={s0.AnimNumber:X2}/{g[0x420]:X2}] [E {List(pr)}| {List(g)}] ");
            }
            bool pos = p.X == gx && p.Y == gy;
            bool cam = !camera || (p.State == g[0x50] && p.ScrollY == g[0xFA] && p.ScrollYRow == g[0xFB]
                                   && p.CellIndex == g[0x22] && p.CellIndexNext == g[0x23] && p.ScrollState == g[0x2B]);
            int[] pv = { p.X, p.Y, p.State, p.ScrollY, p.ScrollYRow,
                         p.CellIndex, p.CellIndexNext, p.ScrollState, p.StageCursor };
            int[] ov = { gx, gy, g[0x50], g[0xFA], g[0xFB],
                         g[0x22], g[0x23], g[0x2B], g[0x25] };
            for (int i = 0; i < pv.Length; i++)
                if (pv[i] != ov[i] && firstBad[i] < 0) { firstBad[i] = f; badPort[i] = pv[i]; badOrig[i] = ov[i]; }
            if (pos && cam) matchFrames++;
            else
            {
                if (firstDiv < 0) firstDiv = f;
                if (f - firstDiv < 6 || f % 25 == 0)
                    sb.Append($"f{f}: port=({p.X:X4},{p.Y:X4} st={p.State:X2} FA={p.ScrollY:X2} FB={p.ScrollYRow:X2} c={p.CellIndex:X2}) " +
                              $"orig=({gx:X4},{gy:X4} st={g[0x50]:X2} FA={g[0xFA]:X2} FB={g[0xFB]:X2} c={g[0x22]:X2}) ");
            }
        }
        int span = avail - 1 - seedFrame;
        sb.Append("FIELDS[");
        for (int i = 0; i < fieldNames.Length; i++)
            if (firstBad[i] >= 0)
                sb.Append($"{fieldNames[i]}:f{firstBad[i]} {badPort[i]:X4}!={badOrig[i]:X4} ");
        sb.Append("] ");
        sb.Append(firstDiv < 0
            ? $"RESULT: MATCH {matchFrames}/{span} frames (seed {seedFrame})"
            : $"RESULT: first divergence frame {firstDiv} (matched {matchFrames}/{span}, seed {seedFrame})");
        File.WriteAllText(root + $"/Temp/{name}_trace.txt", sb.ToString());   // the run_script console truncates
        return sb.ToString();
    }

    public static string Walk() => Compare("walk", 120,
        f => (f >= 10 && f <= 70) ? (byte)0x01 : (byte)0x00);

    public static string Jump() => Compare("jump", 120, f =>
    {
        byte b = 0;
        if (f >= 10 && f <= 40) b |= 0x01;
        if (f >= 20 && f <= 22) b |= 0x80;
        return b;
    });

    /// <summary>
    /// Mission-1 traversal golden `gold_traverse_m1`: the original resumed from
    /// m1_standing.mss with Right held from frame 2. The player walks to the room-0x02 pylon
    /// wall and stops at X=$114 (frame 159) — the first end-to-end check of the world feed
    /// (cell/row advance, camera, spawn on cell entry), so camera state is compared too.
    /// </summary>
    public static string Traverse() => Compare("traverse_m1", 600,
        f => (byte)(f >= 2 ? 0x01 : 0x00), camera: true);

    /// <summary>
    /// Mission-1 route golden `gold_m1_fulljump`: Right held from f2 plus ONE 26-frame
    /// A-hold at f160-185. A 3-frame tap only hops 14 px, so the pylon in room 0x02
    /// (solid rows 6-15, cols 2-3) is impassable; the held jump (apex ~$0425 velocity,
    /// 52 px rise) lands the player on the pylon top at Y=$354, he then walks right,
    /// drops into the lower floor of cell $34 ($22 advances at f254, scroll crosses
    /// $0100) and takes a NATURAL enemy hit at f338 ($50=$06, anim $09). One independent
    /// trajectory that covers world feed + cell advance + spawn + bank-6 dispatch + $813B.
    /// </summary>
    public static string M1Route() => Compare("m1_fulljump", 400,
        f => (byte)((f >= 2 && f <= 390 ? 0x01 : 0x00) | (f >= 160 && f <= 185 ? 0x80 : 0x00)),
        camera: true, enemies: true);

    /// <summary>
    /// Hook ride golden: `gold_hook_ride2` is mission 1 (world 0) with the original
    /// seeded by probe_run --patch into cell $22=$1C (row 1, col 11, script byte $41 =
    /// hook column) at the row bottom; the grab fires next frame and the camera rides
    /// down one 240-px row ($FA 3 px/frame), then $D7F9 releases the player ($50=1).
    /// Frame 0 of the capture is the patched seed frame, so the port starts at frame 1
    /// (first ridden frame) and every later frame is compared, camera state included.
    /// </summary>
    public static string Hook() => Compare("hook_ride2", 300, f => 0, seedFrame: 1, camera: true);

    /// <summary>Per-world `$DD57` seeding. `reference/gold_wNN` are original-ROM captures made
    /// with `probe_run --patch 31=NN --patch-frame 314`: $31 is only READ by the level entry
    /// (bank 7 `$C8B5 JSR $FF55 #$04` → `$C8BD JSR $DD57`), so injecting the world id one frame
    /// before the loader runs selects the world without touching the ROM. The loader's seed
    /// ($36/$FB/$22/$23/$2E/$2D/$1A/$1B/$3D/$04F0/$0510/$FC) must equal DarkwingGame.LoadWorld.
    /// Fixture note: the world id is injected, the loader and all data are the original ROM's.</summary>
    public static string WorldSeeds()
    {
        string root = "D:/Art/SW Code Zero Unity";
        var data = Darkwing.Core.DataLoader.Load(
            File.ReadAllBytes(root + "/Assets/Darkwing/Data/chr_full.bytes"),
            File.ReadAllBytes(root + "/Assets/Darkwing/Data/levelsets.bytes"),
            File.ReadAllBytes(root + "/Assets/Darkwing/Data/screens.bytes"),
            File.ReadAllBytes(root + "/Assets/Darkwing/Data/levels.bytes"),
            File.ReadAllBytes(root + "/Assets/Darkwing/Data/anims.bytes"));
        var sb = new System.Text.StringBuilder();
        int okAll = 0, nAll = 0;
        for (int world = 0; world < 7; world++)
        {
            string dir = root + $"/Research/DarkwingDuck/reference/gold_w{world:X2}";
            if (!File.Exists(dir + "/ram_frames.bin")) { sb.Append($"w{world}: NO CAPTURE; "); continue; }
            byte[] ram = File.ReadAllBytes(dir + "/ram_frames.bin");
            int frames = ram.Length / 2048;

            var game = new Darkwing.Core.DarkwingGame(data);
            game.Bank5 = File.ReadAllBytes(root + "/Assets/Darkwing/Data/prg_bank5.bytes");
            game.Bank7 = File.ReadAllBytes(root + "/Assets/Darkwing/Data/world_bank7.bytes");
            var decoder = new Darkwing.Core.RoomDecoder(
                File.ReadAllBytes(root + "/Assets/Darkwing/Data/prg_bank0.bytes"),
                File.ReadAllBytes(root + "/Assets/Darkwing/Data/prg_bank1.bytes"),
                File.ReadAllBytes(root + "/Assets/Darkwing/Data/world_bank7.bytes"),
                File.ReadAllBytes(root + "/Assets/Darkwing/Data/levelsets.bytes"));
            game.LoadWorld(decoder, (byte)world);
            var p = game.Player;

            // $2E is $01 before the load and 0x0A..0x13 for every world after it.
            int lf = -1;
            for (int f = 300; f < frames; f++)
                if (ram[f * 2048 + 0x2E] != 0x01) { lf = f; break; }
            if (lf < 0) { sb.Append($"w{world}: loader never ran; "); continue; }
            byte[] g = new byte[2048];
            System.Array.Copy(ram, lf * 2048, g, 0, 2048);

            // $DD76-$DDB6 (descriptor) lands in the load frame; the slot-0/player seed
            // ($DDDB-$DDFE) only shows in the snapshot once the ~10-frame $DE24 staging
            // loop has run, so the ROM reports it from f328 for these fixtures and the
            // port needs the same number of Steps. $1A/$1B hold the CHR request until
            // the NMI at f330, which is why the latch itself is compared two frames later.
            int pf = System.Math.Min(lf + 10, frames - 1);
            int cf = System.Math.Min(lf + 12, frames - 1);
            byte[] q = new byte[2048];
            System.Array.Copy(ram, pf * 2048, q, 0, 2048);
            byte[] r = new byte[2048];
            System.Array.Copy(ram, cf * 2048, r, 0, 2048);
            var pad = Darkwing.Core.NesButtons.FromByte(0);
            for (int i = lf; i < pf; i++) game.Step(pad);

            var fields = new System.Collections.Generic.List<string>();
            void One(string label, byte got, byte want)
            {
                nAll++;
                bool ok = got == want;
                if (ok) okAll++;
                fields.Add(ok ? label : $"{label}={want:X2}!={got:X2}");
            }
            One("$36", p.TilesetBank, g[0x36]);
            One("$FB", p.ScrollYRow, g[0xFB]);
            One("$2E", p.RowStride, g[0x2E]);
            One("$2D", p.BottomRow, g[0x2D]);
            One("$3D", p.SectionFlag3D, g[0x3D]);
            One("$22", p.CellIndex, q[0x22]);
            // $DD7B/$DD7D seed $23 = $22, $DE7E DEC $23 puts it one cell BEHIND the camera,
            // and the $DE86-$DE91 loop walks the $25 cursor to $0F while stepping $23 with
            // it (144 passes), ending at StartCell+1. The ROM time-slices that loop over
            // ~10 frames, the port runs it inline inside ApplyWorldLoad.
            One("$23", p.CellIndexNext, q[0x23]);
            One("$04F0", (byte)(p.X & 0xFF), q[0x4F0]);
            One("$0500", (byte)(p.X >> 8), q[0x500]);
            One("$0510", (byte)(p.Y & 0xFF), q[0x510]);
            One("$0520", (byte)(p.Y >> 8), q[0x520]);
            One("$FC", (byte)(p.ScrollX & 0xFF), q[0xFC]);
            One("$FD", (byte)(p.ScrollX >> 8), q[0xFD]);
            One("$0400", p.Flags, q[0x400]);
            One("$50", p.State, q[0x50]);
            // The port applies the request inside LoadWorld and zeroes the latch there, so
            // only the consumed-by-f330 semantics can be compared — the banks are below.
            One("$1A", p.ChrLatch0, r[0x1A]);
            One("$1B", p.ChrLatch1, r[0x1B]);
            One("CHR1", (byte)game.Ppu.Chr1Bank, (byte)(g[0x1B] & 0x7F));
            One("CHR0", (byte)game.Ppu.Chr0Bank, (byte)(g[0x1A] & 0x7F));
            sb.Append($"w{world}@f{lf}/{pf}: {okAll}/{nAll} cum [{string.Join(" ", fields)}]\n");
        }
        sb.Append($"RESULT: world seeds {okAll}/{nAll} fields over 7 captures\n");
        return sb.ToString();
    }

    /// <summary>Per-mission gameplay golden. Every `gold_wNN` capture is the level-entry
    /// fixture (`probe_run --patch 31=NN --patch-frame 314 --input` idle pad), so the port is
    /// seeded from f330 — two frames after the $DDFE state-$09 handover — and replayed with
    /// zero input through the full stack (gravity, collision map, camera, column streamer,
    /// spawner). Missions 2-7 had no frame comparisons at all before these captures.</summary>
    public static string WorldWalks()
    {
        var sb = new System.Text.StringBuilder();
        for (int world = 0; world < 7; world++)
            sb.Append(Compare($"w{world:X2}", 360, f => (byte)0,
                seedFrame: 330, camera: true, enemies: true, world: world)).Append('\n');
        return sb.ToString();
    }

    /// <summary>Dumps the mission-1 placement list the spawner reads, to find why the
    /// route golden has no live objects (activation compares ScreenX/ScreenY against
    /// $FD/$FB, and the ROM walker slot $0D spawns one cell AHEAD of the camera).</summary>
    public static string ProbeLevel()
    {
        string root = "D:/Art/SW Code Zero Unity";
        var data = Darkwing.Core.DataLoader.Load(
            File.ReadAllBytes(root + "/Assets/Darkwing/Data/chr_full.bytes"),
            File.ReadAllBytes(root + "/Assets/Darkwing/Data/levelsets.bytes"),
            File.ReadAllBytes(root + "/Assets/Darkwing/Data/screens.bytes"),
            File.ReadAllBytes(root + "/Assets/Darkwing/Data/levels.bytes"),
            File.ReadAllBytes(root + "/Assets/Darkwing/Data/anims.bytes"));
        var sb = new System.Text.StringBuilder();
        sb.Append($"levels={data.Levels.Length} ");
        for (int li = 0; li < System.Math.Min(3, data.Levels.Length); li++)
        {
            var l = data.Levels[li];
            int maxSX = 0, maxSY = 0;
            foreach (var o in l.Objects ?? new Darkwing.Core.GameData.Placement[0])
            {
                if (o.ScreenX > maxSX) maxSX = o.ScreenX;
                if (o.ScreenY > maxSY) maxSY = o.ScreenY;
            }
            sb.Append($"[{li} id={l.Id} {l.Name} wh={l.Width}x{l.Height} n={l.ObjectCount} sx<= {maxSX} sy<= {maxSY}] ");
        }
        var lv = data.Levels[0];
        foreach (var o in lv.Objects ?? new Darkwing.Core.GameData.Placement[0])
            if (o.ScreenY <= 4 && o.ScreenX <= 3)
                sb.Append($"#{o.Index} t={o.Type:X2} sx={o.ScreenX} x={o.X:X2} sy={o.ScreenY} y={o.Y:X2} wx={(o.ScreenX << 8) | o.X:X4} wy={(o.ScreenY << 8) | o.Y:X4}; ");
        return sb.ToString();
    }

    /// <summary>Runs the whole player-physics suite and returns only the RESULT lines.</summary>
    public static string Suite()
    {
        var sb = new System.Text.StringBuilder();
        void One(string label, string full)
        {
            int i = full.LastIndexOf("RESULT:");
            sb.Append(label).Append(": ").Append(i < 0 ? "no result" : full.Substring(i, System.Math.Min(60, full.Length - i))).Append('\n');
        }
        One("walk", Walk());
        One("jump", Jump());
        One("hook", Hook());
        One("traverse", Traverse());
        One("m1route", M1Route());
        One("hurt", Hurt());
        One("door", Door());
        return sb.ToString();
    }

    /// <summary>
    /// Damage/knockback golden: `gold_hurt_a41` is mission 1 where the capture patches the
    /// player onto slot 13 (beh $0C, anim $41 => $814F C9 3F BCS passes, so this enemy DOES
    /// hurt) at capture frame 174; frame 173 is therefore already recorded with the patched
    /// position and is the seed. The original detects contact inside frame 174 ($50 06, $51 0F,
    /// $7C 3B, anim $09 via $FA78) and then runs the state-6 handler, whose knockback drift is
    /// $0540/$0530 = 00,80 — i.e. $F9C2 does NOT zero the velocity.
    /// </summary>
    public static string Hurt()
    {
        string root = "D:/Art/SW Code Zero Unity";
        byte[] ramAll = File.ReadAllBytes(root + "/Research/DarkwingDuck/reference/gold_hurt_a41/ram_frames.bin");
        int avail = ramAll.Length / 2048;
        var data = Darkwing.Core.DataLoader.Load(
            File.ReadAllBytes(root + "/Assets/Darkwing/Data/chr_full.bytes"),
            File.ReadAllBytes(root + "/Assets/Darkwing/Data/levelsets.bytes"),
            File.ReadAllBytes(root + "/Assets/Darkwing/Data/screens.bytes"),
            File.ReadAllBytes(root + "/Assets/Darkwing/Data/levels.bytes"),
            File.ReadAllBytes(root + "/Assets/Darkwing/Data/anims.bytes"));
        var game = new Darkwing.Core.DarkwingGame(data);
        game.Bank5 = File.ReadAllBytes(root + "/Assets/Darkwing/Data/prg_bank5.bytes");
        game.Bank6 = File.ReadAllBytes(root + "/Assets/Darkwing/Data/prg_bank6.bytes");
        game.ObjectTypeTable = File.ReadAllBytes(root + "/Assets/Darkwing/Data/object_types.bytes");
        game.Bank7 = File.ReadAllBytes(root + "/Assets/Darkwing/Data/world_bank7.bytes");
        var decoder = new Darkwing.Core.RoomDecoder(
            File.ReadAllBytes(root + "/Assets/Darkwing/Data/prg_bank0.bytes"),
            File.ReadAllBytes(root + "/Assets/Darkwing/Data/prg_bank1.bytes"),
            game.Bank7,
            File.ReadAllBytes(root + "/Assets/Darkwing/Data/levelsets.bytes"));
        var world0 = decoder.GetWorld(0);
        var map = new Darkwing.Core.LevelTileMap(decoder, game.Ppu, world0);
        game.Player.TileMap = map;
        game.TileMap = map;
        game.WireWorldFeed();

        const int seed = 173;                 // recorded WITH the teleport already applied
        byte[] ram0 = new byte[2048];
        System.Array.Copy(ramAll, seed * 2048, ram0, 0, 2048);
        var p = game.Player;
        p.X = (ushort)(ram0[0x4F0] | (ram0[0x500] << 8));
        p.Y = (ushort)(ram0[0x510] | (ram0[0x520] << 8));
        p.YVelFrac = ram0[0x550];
        p.YVelInt = (sbyte)ram0[0x560];
        p.YFrac = ram0[0x460];
        p.XFrac = ram0[0x470];
        p.XVelFrac = ram0[0x530];
        p.XVelInt = ram0[0x540];
        p.State = ram0[0x50];
        p.StateTimer = ram0[0x51];
        p.SavedState = ram0[0x58];
        p.InvulnFrames = ram0[0x7C];
        p.DirBits = ram0[0x52];
        // $95 increments in the NMI before the frame's logic, so the pre-Step value
        // is the seed frame's capture and Step() produces frame f's value.
        game.FrameParity = ram0[0x95];
        p.ScrollX = (ushort)(ram0[0xFC] | (ram0[0xFD] << 8));
        p.ScrollYRow = ram0[0xFB];
        p.Flags = ram0[0x400];
        p.ScrollY = ram0[0xFA];
        p.CellIndex = ram0[0x22];
        p.CellIndexNext = ram0[0x23];
        p.ScrollState = ram0[0x2B];
        p.ScrollStep = ram0[0x10];
        p.ScrollMode = ram0[0x2F];
        p.RowStride = ram0[0x2E];
        p.BottomRow = ram0[0x2D];
        // $E63C driver state: the cursor phase is captured state, the step rule is the port's.
        p.StageCursor = ram0[0x25];
        p.LastRedrawScroll = ram0[0x28];
        p.PrevScrollMode = ram0[0x20];
        p.PrevPlayerX = (ushort)(ram0[0x6E] | (ram0[0x6F] << 8));
        // Bulk-seed every object slot table the dispatcher reads ($0400..$05A0, 16 bytes/slot).
        for (int s = 0; s < 16; s++)
        {
            var sl = game.Objects.Slots[s];
            sl.Flags = ram0[0x400 + s];
            sl.AnimFrame = ram0[0x410 + s];
            sl.AnimNumber = ram0[0x420 + s];
            sl.AnimTimer = ram0[0x430 + s];
            sl.ScreenY = ram0[0x440 + s];
            sl.ScreenX = ram0[0x450 + s];
            sl.YFrac = ram0[0x460 + s];
            sl.XFrac = ram0[0x470 + s];
            sl.T480 = ram0[0x480 + s];
            sl.T490 = ram0[0x490 + s];
            sl.T4A0 = ram0[0x4A0 + s];
            sl.T4B0 = ram0[0x4B0 + s];
            sl.HitFlash = ram0[0x4C0 + s];
            sl.StateLow = ram0[0x4D0 + s];
            sl.Active = (ram0[0x4D0 + s] & 0x80) != 0;
            sl.Behavior = ram0[0x4E0 + s];
            sl.X = (ushort)(ram0[0x4F0 + s] | (ram0[0x500 + s] << 8));
            sl.Y = (ushort)(ram0[0x510 + s] | (ram0[0x520 + s] << 8));
            sl.XVelFrac = ram0[0x530 + s];
            sl.XVelInt = ram0[0x540 + s];
            sl.YVelFrac = ram0[0x550 + s];
            sl.YVelInt = (sbyte)ram0[0x560 + s];
            sl.BehaviorParam = ram0[0x570 + s];
            sl.Direction = ram0[0x580 + s];
            sl.PlacementIndex = ram0[0x590 + s];
            sl.Health = ram0[0x5A0 + s];
        }
        map.LoadRow(p.ScrollYRow);
        map.StreamColumns(p.ScrollX);
        var dbg = new System.Text.StringBuilder("TYPES ");
        for (int r = 0; r < 16; r++)
        {
            dbg.Append($"r{r:X}:");
            for (int q = 0; q < 16; q++) dbg.Append(map.TypeAt(0x108 + q * 16, 0x308 + r * 16).ToString("X"));
            dbg.Append(" ");
        }
        var names = new[] { "$50 state", "$51 timer", "$7C invuln", "$0420 anim", "$0410 aframe",
                            "$0430 atimer", "X", "Y", "$0540/$0530 vx", "$0560/$0550 vy",
                            "$0460 ffc", "$0400 flags", "13:$04D0", "13:X", "13:Y", "13:$0420" };
        var ok = new int[names.Length];
        var firstBad = new int[names.Length];
        for (int i = 0; i < names.Length; i++) firstBad[i] = -1;
        var sb = new System.Text.StringBuilder("[hurt_a41] " + dbg);
        int span = avail - 1 - seed;
        for (int f = seed + 1; f < avail; f++)
        {
            // The capture held right from frame 10 to 170, so nothing is pressed from the seed on.
            game.Step(Darkwing.Core.NesButtons.FromByte(0));
            byte[] g = new byte[2048];
            System.Array.Copy(ramAll, f * 2048, g, 0, 2048);
            var e13 = game.Objects.Slots[13];
            if (f <= seed + 4)
                sb.Append($"[f{f} P({p.X:X4},{p.Y:X4}) st={p.State:X2} t={p.StateTimer:X2} vx={p.XVelInt},{p.XVelFrac:X2} xfc={p.XFrac:X2} fl={p.Flags:X2} dir={p.DirBits:X2} iv={p.InvulnFrames:X2} E13({e13.X:X4},{e13.Y:X4})an={e13.AnimNumber:X2} | O({(g[0x4F0] | (g[0x500] << 8)):X4},{(g[0x510] | (g[0x520] << 8)):X4}) st={g[0x50]:X2} t={g[0x51]:X2} vx={(sbyte)g[0x540]},{g[0x530]:X2} xfc={g[0x470]:X2} fl={g[0x400]:X2} dir={g[0x52]:X2} iv={g[0x7C]:X2} 13({g[0x4F0 + 13] | (g[0x500 + 13] << 8):X4})an={g[0x420 + 13]:X2}] ");
            int[] port = { p.State, p.StateTimer, p.InvulnFrames, game.Objects.Slots[0].AnimNumber,
                           game.Objects.Slots[0].AnimFrame, game.Objects.Slots[0].AnimTimer,
                           p.X, p.Y, (p.XVelInt << 8) | p.XVelFrac, ((byte)p.YVelInt << 8) | p.YVelFrac,
                           p.YFrac, p.Flags, e13.StateLow, e13.X, e13.Y, e13.AnimNumber };
            int[] orig = { g[0x50], g[0x51], g[0x7C], g[0x420], g[0x410], g[0x430],
                           g[0x4F0] | (g[0x500] << 8), g[0x510] | (g[0x520] << 8),
                           (g[0x540] << 8) | g[0x530], (g[0x560] << 8) | g[0x550],
                           g[0x460], g[0x400], g[0x4D0 + 13],
                           g[0x4F0 + 13] | (g[0x500 + 13] << 8), g[0x510 + 13] | (g[0x520 + 13] << 8),
                           g[0x420 + 13] };
            for (int i = 0; i < names.Length; i++)
            {
                if (port[i] == orig[i]) { ok[i]++; continue; }
                if (firstBad[i] < 0)
                {
                    firstBad[i] = f;
                    sb.Append($"FIRST {names[i]}: f{f} port={port[i]:X4} orig={orig[i]:X4}; ");
                }
            }
        }
        for (int i = 0; i < names.Length; i++)
            sb.Append($"{names[i]}={ok[i]}/{span} (first bad {(firstBad[i] < 0 ? "-" : firstBad[i].ToString())}) ");
        File.WriteAllText(root + "/Temp/hurt_trace.txt", sb.ToString());   // full text, the console truncates
        return sb.ToString();
    }

    /// <summary>
    /// Door/section golden: `gold_door_id1` is mission 1 (world 0) with the request byte
    /// $5B seeded to 1 at capture frame 5. The original consumes it at $CA6E (LDX $5B /
    /// JSR $E388) 18 frames later; the loader yields per row-redraw batch ($FF06/$E698),
    /// so staging spans 10 redraw frames plus a tail frame. Seeds the port at index 21
    /// (last pre-load frame) and compares every RAM byte $E388 writes.
    /// </summary>
    public static string Door()
    {
        string root = "D:/Art/SW Code Zero Unity";
        byte[] ramAll = File.ReadAllBytes(root + "/Research/DarkwingDuck/reference/gold_door_id1/ram_frames.bin");
        int avail = ramAll.Length / 2048;
        var data = Darkwing.Core.DataLoader.Load(
            File.ReadAllBytes(root + "/Assets/Darkwing/Data/chr_full.bytes"),
            File.ReadAllBytes(root + "/Assets/Darkwing/Data/levelsets.bytes"),
            File.ReadAllBytes(root + "/Assets/Darkwing/Data/screens.bytes"),
            File.ReadAllBytes(root + "/Assets/Darkwing/Data/levels.bytes"),
            File.ReadAllBytes(root + "/Assets/Darkwing/Data/anims.bytes"));
        var game = new Darkwing.Core.DarkwingGame(data);
        game.Bank5 = File.ReadAllBytes(root + "/Assets/Darkwing/Data/prg_bank5.bytes");
        game.Bank7 = File.ReadAllBytes(root + "/Assets/Darkwing/Data/world_bank7.bytes");
        var decoder = new Darkwing.Core.RoomDecoder(
            File.ReadAllBytes(root + "/Assets/Darkwing/Data/prg_bank0.bytes"),
            File.ReadAllBytes(root + "/Assets/Darkwing/Data/prg_bank1.bytes"),
            game.Bank7,
            File.ReadAllBytes(root + "/Assets/Darkwing/Data/levelsets.bytes"));
        var map = new Darkwing.Core.LevelTileMap(decoder, game.Ppu, decoder.GetWorld(0));
        game.Player.TileMap = map;
        game.TileMap = map;
        game.WireWorldFeed();

        const int seed = 21;             // capture frame 22: last frame before the load
        byte[] ram0 = new byte[2048];
        System.Array.Copy(ramAll, seed * 2048, ram0, 0, 2048);
        var p = game.Player;
        p.X = (ushort)(ram0[0x4F0] | (ram0[0x500] << 8));
        p.Y = (ushort)(ram0[0x510] | (ram0[0x520] << 8));
        p.YVelFrac = ram0[0x550];
        p.YVelInt = (sbyte)ram0[0x560];
        p.YFrac = ram0[0x460];
        p.XFrac = ram0[0x470];
        p.XVelFrac = ram0[0x530];
        p.XVelInt = ram0[0x540];
        p.State = ram0[0x50];
        p.ScrollX = (ushort)(ram0[0xFC] | (ram0[0xFD] << 8));
        p.ScrollYRow = ram0[0xFB];
        p.Flags = ram0[0x400];
        p.ScrollY = ram0[0xFA];
        p.CellIndex = ram0[0x22];
        p.CellIndexNext = ram0[0x23];
        p.ScrollState = ram0[0x2B];
        p.ScrollStep = ram0[0x10];
        p.ScrollMode = ram0[0x2F];
        p.RowStride = ram0[0x2E];
        p.BottomRow = ram0[0x2D];
        // $E63C driver state: the cursor phase is captured state, the step rule is the port's.
        p.StageCursor = ram0[0x25];
        p.LastRedrawScroll = ram0[0x28];
        p.PrevScrollMode = ram0[0x20];
        p.PrevPlayerX = (ushort)(ram0[0x6E] | (ram0[0x6F] << 8));
        byte doorId = ram0[0x5B];
        // Seed the player's object slot exactly as the capture has it at the seed frame:
        // $E388 leaves $0410/$0420/$0430 alone until the $FA78 seed after its staging.
        var slot0 = game.Objects.Slots[0];
        slot0.AnimNumber = ram0[0x420];
        slot0.AnimFrame = ram0[0x410];
        slot0.AnimTimer = ram0[0x430];
        slot0.Active = (ram0[0x4D0] & 0x80) != 0;
        map.LoadRow(p.ScrollYRow);
        map.StreamColumns(p.ScrollX);
        // $E388 was already entered 16 frames earlier (the capture's $5B patch at f5), so
        // the port starts the frame at index 22 INSIDE the loader: enter it at the seed.
        if (doorId != 0) game.LoadSection(doorId);

        var names = new[] { "$22 cell", "$23 next", "$FB row", "$FD colhi", "$FC collo",
                            "$FA scrollY", "$50 state", "$36 tileset", "$1A chr0", "$1B chr1",
                            "$0500/$04F0 X", "$0520/$0510 Y", "$0400 flags", "$0420 anim",
                            "$0460 ffc" };
        var ok = new int[names.Length];
        var firstBad = new int[names.Length];
        for (int i = 0; i < names.Length; i++) firstBad[i] = -1;
        var sb = new System.Text.StringBuilder("[door_id1] ");
        int span = avail - 1 - seed;
        for (int f = seed + 1; f < avail; f++)
        {
            game.Step(Darkwing.Core.NesButtons.FromByte(0));
            byte[] g = new byte[2048];
            System.Array.Copy(ramAll, f * 2048, g, 0, 2048);
            int[] port = { p.CellIndex, p.CellIndexNext, p.ScrollYRow, p.ScrollX >> 8, p.ScrollX & 0xFF,
                           p.ScrollY, p.State, p.TilesetBank, p.ChrLatch0, p.ChrLatch1,
                           p.X, p.Y, p.Flags, game.Objects.Slots[0].AnimNumber, p.YFrac };
            int[] orig = { g[0x22], g[0x23], g[0xFB], g[0xFD], g[0xFC],
                           g[0xFA], g[0x50], g[0x36], g[0x1A], g[0x1B],
                           g[0x4F0] | (g[0x500] << 8), g[0x510] | (g[0x520] << 8),
                           g[0x400], g[0x420], g[0x460] };
            for (int i = 0; i < names.Length; i++)
            {
                if (port[i] == orig[i]) { ok[i]++; continue; }
                if (firstBad[i] < 0)
                {
                    firstBad[i] = f;
                    sb.Append($"FIRST {names[i]}: f{f} port={port[i]:X4} orig={orig[i]:X4}; ");
                }
            }
        }
        for (int i = 0; i < names.Length; i++)
            sb.Append($"{names[i]}={ok[i]}/{span} (first bad {(firstBad[i] < 0 ? "-" : firstBad[i].ToString())}) ");
        return sb.ToString();
    }
}
