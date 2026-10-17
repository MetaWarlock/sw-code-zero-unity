using System.IO;
using UnityEngine;

public static class PhysCompare
{
    /// <summary>
    /// Golden physics comparison through the full DarkwingGame stack (movement +
    /// anim advance + slot sync), replaying the captured original input schedule.
    /// </summary>
    private static string Compare(string name, int frames, System.Func<int, byte> input,
        int seedFrame = 0, bool camera = false, bool enemies = false, int world = 0,
        int entryFreeze = 0, bool trace = false, int limit = 0, bool palette = false,
        string goldenDir = null, bool scoreEnemies = false, bool scoreOam = false, bool pause = false)
    {
        string root = "D:/Art/SW Code Zero Unity";
        string gdir = goldenDir ?? $"gold_{name}";
        byte[] ramAll = File.ReadAllBytes(root + $"/Research/DarkwingDuck/reference/{gdir}/ram_frames.bin");
        byte[] palAll = palette
            ? File.ReadAllBytes(root + $"/Research/DarkwingDuck/reference/{gdir}/palette_frames.bin")
            : System.Array.Empty<byte>();
        byte[] oamAll = scoreOam
            ? File.ReadAllBytes(root + $"/Research/DarkwingDuck/reference/{gdir}/oam_frames.bin")
            : System.Array.Empty<byte>();
        int avail = ramAll.Length / 2048;

        var data = Darkwing.Core.DataLoader.Load(
            File.ReadAllBytes(root + "/Assets/Darkwing/Data/chr_full.bytes"),
            File.ReadAllBytes(root + "/Assets/Darkwing/Data/levelsets.bytes"),
            File.ReadAllBytes(root + "/Assets/Darkwing/Data/screens.bytes"),
            File.ReadAllBytes(root + "/Assets/Darkwing/Data/levels.bytes"),
            File.ReadAllBytes(root + "/Assets/Darkwing/Data/anims.bytes"));
        var game = new Darkwing.Core.DarkwingGame(data);
        game.Bank5 = File.ReadAllBytes(root + "/Assets/Darkwing/Data/prg_bank5.bytes");
        // $E388's section descriptors, the $E5BD $3D column and the $EFBE/$F008/$F049 palette-patch
        // tables all live in bank 7: a boot without Bank7 leaves Sections == null, so those services
        // are silently skipped and the $3E/$3F cursors never move.
        game.Bank7 = File.ReadAllBytes(root + "/Assets/Darkwing/Data/world_bank7.bytes");
        // The pause phase ($8F00…$9025) installs its panel from the bank-4 tables, so a boot
        // without Bank4 leaves the install reading a null array.
        game.Bank4 = File.ReadAllBytes(root + "/Assets/Darkwing/Data/prg_bank4.bytes");
        var decoder = new Darkwing.Core.RoomDecoder(
            File.ReadAllBytes(root + "/Assets/Darkwing/Data/prg_bank0.bytes"),
            File.ReadAllBytes(root + "/Assets/Darkwing/Data/prg_bank1.bytes"),
            File.ReadAllBytes(root + "/Assets/Darkwing/Data/world_bank7.bytes"),
            File.ReadAllBytes(root + "/Assets/Darkwing/Data/levelsets.bytes"));
        var world0 = decoder.GetWorld((byte)world);
        game.SetWorldSource(decoder, (byte)world);   // $D437 re-runs $DD57 from the death resolve
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
        game.Objects.CursorFwd = ram0[0x70];      // $70/$71 spawner entry cursors
        game.Objects.CursorRev = ram0[0x71];
        // $95 is the frame engines' own counter (bank5 $8000 / bank4 $9800) and bank5 $80EB
        // gates the player's anim tick on $95&$04 while $7C != 0, so the blink phase is
        // captured state: the port's boot value can never match the ROM's.
        game.FrameParity = ram0[0x95];
        // $72-$7B: the spawner dereferences pointers, so the placement list is pointer
        // state. Every gold_w capture latches $31 to $01..$06 and keeps $72/$73 on the
        // $31=$00 list — bank-4:$8000 is not in the world-load path, so the list has to
        // be seeded from the capture rather than derived from the world id.
        game.Objects.SelectPlacementListByPointer(ram0[0x72], ram0[0x73]);
        p.RowStride = ram0[0x2E];
        p.BottomRow = ram0[0x2D];
        // $E63C driver state: the cursor phase is captured state, the step rule is the port's.
        p.StageCursor = ram0[0x25];
        p.LastRedrawScroll = ram0[0x28];
        p.PrevScrollMode = ram0[0x20];
        p.PrevPlayerX = (ushort)(ram0[0x6E] | (ram0[0x6F] << 8));
        // $DDFE STA $50 runs during the load frame itself, so a capture seeded on the first
        // frame that shows $50=$09 still owes 16 of the 17 fade frames ($DE03/$C270).
        p.EntryFreezeFrames = entryFreeze;
        // $0320/$0300/$3F00 are loader state the RAM-seeded start point does not carry: without
        // this the first fade ramps from zeros instead of from the live world table. The three
        // tables are seeded SEPARATELY (the ROM's own $0320/$0300/$3F00 values at seedFrame) —
        // they are legitimately out of step, e.g. gold_m1_longroute f0 has $0300[13]=$0F while
        // $3F00[13]=$05 is still on screen.
        if (palette)
        {
            var seedPal = new byte[32];
            System.Array.Copy(palAll, seedFrame * 32, seedPal, 0, 32);
            game.SeedPaletteTables(ram0, seedPal);
        }
        // $3D/$3E/$3F: the $EFBE palette-patch script is mid-cycle in every route capture, so its
        // cursor is captured state like $22 or $95. $66 gates the zone service ($CA75 BMI).
        p.SectionFlag3D = ram0[0x3D];
        p.PalStep3E = ram0[0x3E];
        p.PalIndex3F = ram0[0x3F];
        p.PendingMissionFlag = ram0[0x66];
        // $54 picks the panel's weapon name ($904E) and the slot-3 anim override, $60/$61 are
        // the two gas digits the panel draws — pause goldens have to carry them as captured.
        if (pause)
        {
            p.WeaponSelected = ram0[0x54];
            p.GasEnergyHi = ram0[0x60];
            p.GasEnergyLo = ram0[0x61];
            // $FE is the live $2001 mirror. Gameplay never writes it in the port (the ROM's
            // per-frame $2001 writes are not modelled), so the pause phase would otherwise start
            // from 0 and score $8FB1's AND/$901C's OR against a mask the ROM never held.
            game.Ppu.Mask = ram0[0xFE];
        }
        var s0 = game.Objects.Slots[0];
        if (enemies)
        {
            p.StateTimer = ram0[0x51];
            p.SavedState = ram0[0x58];
            p.InvulnFrames = ram0[0x7C];
            // Mid-mission route seeds (gold_m*_route) start with up to 3 hearts already spent,
            // and the ROM keeps that count in the player's RAM metasprite tile bytes, not a counter:
            // $F9C2 marks $05B4 ($80→$82), $05B8/$05B6 ($81→$83), then $05B2 plus the death write.
            // Without this the port reads the ROM's 4th (lethal) hit as a 1st and stays in $06.
            p.HitLadder = (byte)(ram0[0x5B2] != 0x80 ? 4
                                 : (ram0[0x5B4] != 0x80 ? 1 : 0) + (ram0[0x5B8] != 0x81 ? 1 : 0) +
                                   (ram0[0x5B6] != 0x81 ? 1 : 0));
            p.DirBits = ram0[0x52];
            // The $CE39 path is indexed by the EQUIPPED weapon ($53) and the kind 1/2/3
            // gate reads the live meter $05BE/$05C0, so a projectile golden has to carry them.
            p.WeaponActive = ram0[0x53];
            p.ActiveGasEnergyHi = ram0[0x5BE];
            p.ActiveGasEnergyLo = ram0[0x5C0];
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
            // $CAFC gates the whole player state machine on $04D0 bit 7, so the light seed
            // path has to carry the real flag byte too (ROM keeps $04D0=$81 while walking).
            s0.StateLow = ram0[0x4D0];
            s0.Active = (ram0[0x4D0] & 0x80) != 0;
            s0.AnimNumber = ram0[0x420];
            s0.AnimFrame = ram0[0x410];
            s0.AnimTimer = ram0[0x430];
        }

        // Per-field first-divergence report, and the input to the STRICT verdict below. RESULT
        // itself only gates X/Y (plus the camera fields when asked), so this table is what makes
        // a "$25 drifts but RESULT says MATCH" run legible instead of misleading.
        var fieldNames = new[] { "X", "Y", "$50 state", "$FA scrollY", "$FB row",
                                 "$22 cell", "$23 next", "$2B scrollstate", "$25 streamcursor",
                                 "$0460 Yfrac", "$0470 Xfrac", "$0560 YvelI", "$0550 YvelF",
                                 "ladder($05B2-B8)", "$2F scrollmode", "$10 colmask",
                                 "$28 redrawscroll", "$FC scrollXlo" };
        var firstBad = new int[fieldNames.Length];
        var badPort = new int[fieldNames.Length];
        var badOrig = new int[fieldNames.Length];
        for (int i = 0; i < fieldNames.Length; i++) firstBad[i] = -1;

        var sb = new System.Text.StringBuilder();
        sb.Append($"[player_mechanics] {name}: ");
        int firstDiv = -1;
        int matchFrames = 0;
        // Enemy-trajectory scoring: per frame, EVERY slot (active in either side) must match on
        // position (X/Y), the state/active byte, behavior and anim number. This is what proves the
        // bank-6 dispatcher deterministically, not just the player.
        int enemyMatchFrames = 0, enemyTotalFrames = 0, enemyFirstDiv = -1, enemyDivSlot = -1;
        string enemyDivField = null; int enemyDivPort = 0, enemyDivOrig = 0;
        // Per-frame outcomes of every clause STRICT is allowed to fail on (see the verdict block).
        bool fEnemies = true, fPal = true, fOam = true;
        int fieldBadIdx = -1, strictMatch = 0, strictFirstDiv = -1, strictNoOam = 0;
        string strictFirstDetail = null;
        int palOk = 0, palFirstBad = -1, winOk = 0, winTotal = 0;   // $3F00 byte compare
        // $3F00 can only diverge through the $0300 content or the $18 upload timing, so score the
        // port's two internal palette states against the captured RAM to localise which one is wrong.
        int mirOk = 0, srcOk = 0, flagOk = 0, mirFirstBad = -1, srcFirstBad = -1, flagFirstBad = -1;
        string mirFirstDetail = "", srcFirstDetail = "", flagFirstDetail = "";
        var mirP = new byte[32]; var srcP = new byte[32];
        int oamFrameOk = 0, oamTotal = 0, oamFirstBad = -1, oamByteSum = 0;
        int pauseOk = 0, pauseTotal = 0, pauseFirstBad = -1;
        bool panelOk = true;
        string pauseFirstDetail = "";
        int[] oamEntryBad = new int[64];
        string oamFirstDetail = "";
        const int OamLag = 0;
        int oamFramesAvail = oamAll.Length / 256;
        if (scoreOam)
            // The golden pairs OAM[f] with RAM[f] for the HUD placement but with RAM[f-1] for the
            // object records: the capture reads the live sprite buffer, which the NMI of frame f
            // filled from the shadow the walk of frame f-1 composed (Temp/oam_lag.js: 622/622 and
            // 631/631 object records byte-exact with that pairing, 0 with any other). The port
            // reproduces it with a shadow + PublishToOam, so the comparison stays same-index and
            // the seed has to fill the SHADOW, not the live buffer.
            game.Oam.SeedShadow(oamAll, seedFrame * 256);
        for (int f = seedFrame + 1; f < avail && (limit <= 0 || f - seedFrame <= limit); f++)
        {
            fEnemies = fPal = fOam = true; fieldBadIdx = -1;
            var pad = Darkwing.Core.NesButtons.FromByte(input(f));
            game.Step(pad);
            byte[] g = new byte[2048];
            System.Array.Copy(ramAll, f * 2048, g, 0, 2048);
            int gx = g[0x4F0] | (g[0x500] << 8);
            int gy = g[0x510] | (g[0x520] << 8);
            // $8F2A/$8F24 save slot 0's X/Y LOW bytes to $0378/$0372 and the install at
            // $8F78/$8F72 overwrites only $04F0/$0510, so while $8F00 owns the frame the ROM's
            // live player position is the page byte still in $0500/$0520 plus the saved low byte.
            if (pause && game.Paused)
            {
                gx = (g[0x500] << 8) | g[0x378];
                gy = (g[0x520] << 8) | g[0x372];
            }
            if (scoreEnemies)
            {
                byte[] prE = PackedSlots(game);
                enemyTotalFrames++;
                bool allMatch = true;
                for (int s = 1; s < 16 && allMatch; s++)
                {
                    // The active bit is a COMPARED field, not a filter. Scanning only the slots the
                    // ROM happens to hold alive silently accepted the opposite error: a slot the
                    // port spawns and the ROM does not.
                    bool romOn = (g[0x4D0 + s] & 0x80) != 0;
                    bool portOn = (prE[0x4D0 + s] & 0x80) != 0;
                    if (romOn != portOn)
                    {
                        allMatch = false;
                        if (enemyFirstDiv < 0)
                        { enemyFirstDiv = f; enemyDivSlot = s; enemyDivField = "active";
                          enemyDivPort = portOn ? 1 : 0; enemyDivOrig = romOn ? 1 : 0; }
                        break;
                    }
                    if (!romOn) continue;
                    // $0510/$0520 are the Y pair: the verdict below claims Y, so Y has to be read.
                    int[] addrs = { 0x4F0, 0x500, 0x510, 0x520, 0x4D0, 0x4E0, 0x420 };
                    string[] fld = { "Xlo", "Xhi", "Ylo", "Yhi", "state", "beh", "anim" };
                    for (int k = 0; k < addrs.Length; k++)
                        if (prE[addrs[k] + s] != g[addrs[k] + s])
                        {
                            allMatch = false;
                            if (enemyFirstDiv < 0)
                            { enemyFirstDiv = f; enemyDivSlot = s; enemyDivField = fld[k];
                              enemyDivPort = prE[addrs[k] + s]; enemyDivOrig = g[addrs[k] + s]; }
                            break;
                        }
                }
                if (allMatch) enemyMatchFrames++;
                fEnemies = allMatch;
            }
            if (pause)
            {
                // The panel is six re-installed slots plus the $05C2 block, so this is the field
                // list $8F12…$8F7B writes — save/restore included, because a resume golden scores
                // the frames AFTER $8FCD too.
                pauseTotal++;
                byte[] prP = PackedSlots(game);
                string bad = null;
                int[] pAddrs = { 0x4D0, 0x400, 0x420, 0x4F0, 0x500, 0x510, 0x520 };
                for (int s = 0; s <= 5 && bad == null; s++)
                    for (int k = 0; k < pAddrs.Length && bad == null; k++)
                        if (prP[pAddrs[k] + s] != g[pAddrs[k] + s])
                            bad = $"slot{s} ${pAddrs[k]:X3} port={prP[pAddrs[k] + s]:X2} rom={g[pAddrs[k] + s]:X2}";
                var blk = game.PauseBlockForTest;
                for (int i = 0; i < 38 && bad == null; i++)
                    if (blk[i] != g[0x5C2 + i]) bad = $"${0x5C2 + i:X3} port={blk[i]:X2} rom={g[0x5C2 + i]:X2}";
                if (bad == null && p.InvulnFrames != g[0x7C]) bad = $"$7C port={p.InvulnFrames:X2} rom={g[0x7C]:X2}";
                if (bad == null && game.Ppu.Mask != g[0xFE]) bad = $"$FE port={game.Ppu.Mask:X2} rom={g[0xFE]:X2}";
                // PackedSlots does not carry $0410/$0430, so read the two anim bytes off the slots.
                for (int s = 0; s <= 5 && bad == null; s++)
                {
                    var sl = game.Objects.Slots[s];
                    if (sl.AnimFrame != g[0x410 + s]) bad = $"slot{s} $410 port={sl.AnimFrame:X2} rom={g[0x410 + s]:X2}";
                    else if (sl.AnimTimer != g[0x430 + s]) bad = $"slot{s} $430 port={sl.AnimTimer:X2} rom={g[0x430 + s]:X2}";
                }
                panelOk = bad == null;
                if (bad == null) pauseOk++;
                else if (pauseFirstBad < 0) { pauseFirstBad = f; pauseFirstDetail = $"f{f} {bad}"; }
            }
            if (trace)
            {
                sb.Append($"f{f} P X={p.X:X4} xv={p.XVelInt:X2},{p.XVelFrac:X2} xfc={p.XFrac:X2} Y={p.Y:X4} vy={p.YVelInt:X2},{p.YVelFrac:X2} ffc={p.YFrac:X2} st={p.State:X2} an={s0.AnimNumber:X2}/{s0.AnimFrame:X2}/{s0.AnimTimer:X2} | O X={gx:X4} xv={g[0x540]:X2},{g[0x530]:X2} xfc={g[0x470]:X2} Y={gy:X4} vy={g[0x560]:X2},{g[0x550]:X2} ffc={g[0x460]:X2} st={g[0x50]:X2} an={g[0x420]:X2}/{g[0x410]:X2}/{g[0x430]:X2} p95={game.FrameParity:X2}/{g[0x95]:X2} r97={g[0x97]:X2} t92={g[0x92]:X2}");
                sb.Append($" H pre={p.PrevState:X2} ho={p.HurtOrigin:X2} p10={p.ProbeResultForTest:X2} iv={p.InvulnFrames:X2} hl={p.HitLadder:X2} | O10={g[0x10]:X2} O7C={g[0x7C]:X2} Ohl={g[0x5B2]:X2},{g[0x5B4]:X2},{g[0x5B6]:X2},{g[0x5B8]:X2}{DeathDiag(g, s0)}");
                sb.Append($" | S 22={p.CellIndex:X2}/{g[0x22]:X2} 23={p.CellIndexNext:X2}/{g[0x23]:X2} 25={p.StageCursor:X2}/{g[0x25]:X2} 28={p.LastRedrawScroll:X2}/{g[0x28]:X2} 2F={p.ScrollMode:X2}/{g[0x2F]:X2} FC={p.ScrollX & 0xFF:X2}/{g[0xFC]:X2}");
                var hs = game.Enemies != null && game.Enemies.HurtSlot >= 0
                    ? game.Objects.Slots[game.Enemies.HurtSlot] : null;
                if (hs != null)
                    sb.Append($" HS={game.Enemies.HurtSlot:X2}@{game.Enemies.HurtSlotX:X4},{game.Enemies.HurtSlotY:X4} an{game.Enemies.HurtSlotAnim:X2} bh{game.Enemies.HurtSlotBh:X2} p{game.Enemies.HurtSlotParam:X2} fl{game.Enemies.HurtSlotFlags:X2} st{game.Enemies.HurtSlotState:X2} act{(hs.Active ? 1 : 0)}");
                if (f >= _enemyDumpLo && f <= _enemyDumpHi)
                    sb.Append($" E {ListSlots(PackedSlots(game), true)} | {ListSlots(g, true)}");
                sb.Append('\n');
            }
            if (trace && f == _tileDumpFrame && game.TileMap != null)
            {
                var tm = game.TileMap;
                int py = p.Y + 12;
                var strip = new System.Text.StringBuilder();
                for (int c = 0; c < 16; c++) strip.Append(tm.TypeAt((p.X & 0xFF00) | (c * 16 + 8), py).ToString("X"));
                var win = new System.Text.StringBuilder();
                for (int b = 0x600; b < 0x680; b++) win.Append(g[b].ToString("X2"));
                sb.Append($"DUMP cell={p.X >> 8} row={(py & 0xFF) >> 4} probes={tm.TypeAt(p.X - 11, py):X}{tm.TypeAt(p.X, py):X}{tm.TypeAt(p.X + 11, py):X} strip={strip} ROM600={win}\n");
            }
            if (!trace && (f - seedFrame <= 2 || (f >= 238 && f <= 262) || (f >= 348 && f <= 366)
                           || (f >= _enemyDumpLo && f <= _enemyDumpHi)))
            {
                byte[] pr = PackedSlots(game);
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
                    for (int sl = 1; sl <= 0x0F; sl++)
                        if (game.Objects.Slots[sl].Active && game.Objects.Slots[sl].PlacementIndex == pl) { slot = sl; break; }
                    sp.Append($" {pl:X2}:({px:X4},{py:X4})t{rec.Type:X2}w{(win ? 1 : 0)}s{slot:X2}");
                }
                int freeSlot = -1;
                for (int sl = 14; sl >= 4; sl--)
                    if (!game.Objects.Slots[sl].Active) { freeSlot = sl; break; }
                sp.Append($" mask0={game.Objects.SpawnedBitmask(0):X2} free={freeSlot:X2}]");
                sb.Append(sp.ToString() + " ");
                sb.Append($"[f{f} P({p.X:X4},{p.Y:X4}) st={p.State:X2} FA={p.ScrollY:X2} FB={p.ScrollYRow:X2} c={p.CellIndex:X2}/{p.CellIndexNext:X2} 25={p.StageCursor:X2}/{g[0x25]:X2} 28={p.LastRedrawScroll:X2}/{g[0x28]:X2} 23o={g[0x23]:X2} 2B={p.ScrollState:X2} 10={p.ScrollStep:X2} 2F={p.ScrollMode:X2} vx={p.XVelInt},{p.XVelFrac:X2} xfc={s0.XFrac:X2} at={s0.AnimTimer:X2} pf={s0.AnimFrame:X2} dur={(game.Oam == null ? 0 : game.Oam.AnimHeader(s0.AnimNumber).duration)} iv={p.InvulnFrames:X2} vy={p.YVelInt},{p.YVelFrac:X2} ffc={p.YFrac:X2} hl={p.HitLadder} D={p.DeathFlag} d0={pr[0x4D0]:X2} e6={p.EnergyColRight:X2} e4={p.EnergyColLeft:X2} scx={p.ScrollX:X4}/{g[0xFC]:X2},{g[0xFD]:X2} | O({gx:X4},{gy:X4}) st={g[0x50]:X2} FA={g[0xFA]:X2} FB={g[0xFB]:X2} c={g[0x22]:X2} 2B={g[0x2B]:X2} 10={g[0x10]:X2} 2F={g[0x2F]:X2} vx={(sbyte)g[0x540]},{g[0x530]:X2} xfc={g[0x470]:X2} at={g[0x430]:X2} vy={(sbyte)g[0x560]},{g[0x550]:X2} ffc={g[0x460]:X2} ivc={g[0x7C]:X2} d0={g[0x4D0]:X2} e6={g[0x5E6]:X2} e4={g[0x5E4]:X2} 64={g[0x64]:X2} 6d={g[0x6D]:X2} af={s0.AnimFrame}/{g[0x410]} an={s0.AnimNumber:X2}/{g[0x420]:X2}] [E {ListSlots(pr)}| {ListSlots(g)}] ");
            }
            bool pos = p.X == gx && p.Y == gy;
            bool cam = !camera || (p.State == g[0x50] && p.ScrollY == g[0xFA] && p.ScrollYRow == g[0xFB]
                                   && p.CellIndex == g[0x22] && p.CellIndexNext == g[0x23] && p.ScrollState == g[0x2B]);
            int[] pv = { p.X, p.Y, p.State, p.ScrollY, p.ScrollYRow,
                         p.CellIndex, p.CellIndexNext, p.ScrollState, p.StageCursor,
                         p.YFrac, p.XFrac, p.YVelInt & 0xFF, p.YVelFrac, p.HitLadder,
                         p.ScrollMode, p.ScrollStep, p.LastRedrawScroll, p.ScrollX & 0xFF };
            int[] ov = { gx, gy, g[0x50], g[0xFA], g[0xFB],
                         g[0x22], g[0x23], g[0x2B], g[0x25],
                         g[0x460], g[0x470], g[0x560], g[0x550],
                         g[0x5B2] != 0x80 ? 4 : (g[0x5B4] != 0x80 ? 1 : 0) +
                             (g[0x5B8] != 0x81 ? 1 : 0) + (g[0x5B6] != 0x81 ? 1 : 0),
                         g[0x2F], g[0x10], g[0x28], g[0xFC] };
            for (int i = 0; i < pv.Length; i++)
                if (pv[i] != ov[i])
                {
                    if (fieldBadIdx < 0) fieldBadIdx = i;
                    if (firstBad[i] < 0) { firstBad[i] = f; badPort[i] = pv[i]; badOrig[i] = ov[i]; }
                }
            if (palette)
            {
                bool same = true;
                for (int i = 0; i < 32; i++)
                    if (game.Ppu.PaletteRam[i] != palAll[f * 32 + i]) { same = false; break; }
                fPal = same;
                if (same) palOk++;
                else if (palFirstBad < 0) palFirstBad = f;
                if (f >= 795 && f <= 841) { winTotal++; if (same) winOk++; }
                game.GetPaletteModel(mirP, srcP);
                bool m = true, s2 = true;
                for (int i = 0; i < 32; i++)
                {
                    if (m && mirP[i] != g[0x300 + i])
                    {
                        m = false;
                        if (mirFirstBad < 0) { mirFirstBad = f; mirFirstDetail = $"f{f} i{i} port={mirP[i]:X2} rom={g[0x300 + i]:X2} 3D={p.SectionFlag3D:X2} 3E={p.PalStep3E:X2} 3F={p.PalIndex3F:X2}"; }
                    }
                    if (s2 && srcP[i] != g[0x320 + i])
                    {
                        s2 = false;
                        if (srcFirstBad < 0) { srcFirstBad = f; srcFirstDetail = $"f{f} i{i} port={srcP[i]:X2} rom={g[0x320 + i]:X2}"; }
                    }
                }
                if (m) mirOk++;
                if (s2) srcOk++;
                byte rom18 = g[0x18];
                if (game.PaletteUploadFlag == rom18) flagOk++;
                else if (flagFirstBad < 0) flagFirstDetail = $"f{f} port={game.PaletteUploadFlag:X2} rom={rom18:X2}";
                if (flagFirstBad < 0 && game.PaletteUploadFlag != rom18) flagFirstBad = f;
            }
            if (scoreOam && f + OamLag < oamFramesAvail)
            {
                int o = (f + OamLag) * 256;
                int ok = 0, badByte = -1;
                for (int i = 0; i < 256; i++)
                {
                    if (game.Ppu.Oam[i] == oamAll[o + i]) ok++;
                    else if (badByte < 0) badByte = i;
                }
                oamTotal++; oamByteSum += ok;
                if (trace && scoreOam)
                {
                    var od = new System.Text.StringBuilder($" [OAM f{f} ok={ok}/256 P:");
                    for (int k = 0; k < 64; k++) od.Append($"{game.Ppu.Oam[k * 4]:X2}{game.Ppu.Oam[k * 4 + 1]:X2}{game.Ppu.Oam[k * 4 + 2]:X2}{game.Ppu.Oam[k * 4 + 3]:X2} ");
                    od.Append("| O:");
                    for (int k = 0; k < 64; k++) od.Append($"{oamAll[o + k * 4]:X2}{oamAll[o + k * 4 + 1]:X2}{oamAll[o + k * 4 + 2]:X2}{oamAll[o + k * 4 + 3]:X2} ");
                    od.Append(']');
                    sb.Append(od);
                }
                for (int e = 0; e < 64; e++)
                    if (game.Ppu.Oam[e * 4] != oamAll[o + e * 4] || game.Ppu.Oam[e * 4 + 1] != oamAll[o + e * 4 + 1] ||
                        game.Ppu.Oam[e * 4 + 2] != oamAll[o + e * 4 + 2] || game.Ppu.Oam[e * 4 + 3] != oamAll[o + e * 4 + 3])
                        oamEntryBad[e]++;
                fOam = ok == 256;
                if (ok == 256) oamFrameOk++;
                else if (oamFirstBad < 0)
                {
                    oamFirstBad = f;
                    int e = badByte >> 2;
                    var detail = new System.Text.StringBuilder();
                    for (int k = 0; k < 64 && detail.Length < 700; k++)
                        if (game.Ppu.Oam[k * 4] != oamAll[o + k * 4] || game.Ppu.Oam[k * 4 + 1] != oamAll[o + k * 4 + 1] ||
                            game.Ppu.Oam[k * 4 + 2] != oamAll[o + k * 4 + 2] || game.Ppu.Oam[k * 4 + 3] != oamAll[o + k * 4 + 3])
                            detail.Append($"e{k}:{game.Ppu.Oam[k*4]:X2},{game.Ppu.Oam[k*4+1]:X2},{game.Ppu.Oam[k*4+2]:X2},{game.Ppu.Oam[k*4+3]:X2}/" +
                                          $"{oamAll[o + k * 4]:X2},{oamAll[o + k * 4 + 1]:X2},{oamAll[o + k * 4 + 2]:X2},{oamAll[o + k * 4 + 3]:X2} ");
                    oamFirstDetail = $" f{oamFirstBad}: {ok}/256 bytes, first diff b{badByte} (entry {e})" + detail;
                }
            }
            else if (scoreOam) strictNoOam++;
            if (pos && cam) matchFrames++;
            else
            {
                if (firstDiv < 0) firstDiv = f;
                if (f - firstDiv < 6 || f % 25 == 0)
                    sb.Append($"f{f}: port=({p.X:X4},{p.Y:X4} st={p.State:X2} FA={p.ScrollY:X2} FB={p.ScrollYRow:X2} c={p.CellIndex:X2}) " +
                              $"orig=({gx:X4},{gy:X4} st={g[0x50]:X2} FA={g[0xFA]:X2} FB={g[0xFB]:X2} c={g[0x22]:X2}) ");
            }
            // STRICT = every field this comparison actually reads, so a match here — and only here —
            // means "frame-identical on all scored state", not "the two position bytes agree".
            bool strict = pos && cam && fieldBadIdx < 0 && fEnemies && fPal && fOam && (!pause || panelOk);
            if (strict) strictMatch++;
            else if (strictFirstDiv < 0)
            {
                strictFirstDiv = f;
                strictFirstDetail =
                    !pos ? $"player X/Y port=({p.X:X4},{p.Y:X4}) rom=({gx:X4},{gy:X4})"
                  : !cam ? $"cam $50 {p.State:X2}!={g[0x50]:X2} $FA {p.ScrollY:X2}!={g[0xFA]:X2} $FB {p.ScrollYRow:X2}!={g[0xFB]:X2} " +
                         $"$22 {p.CellIndex:X2}!={g[0x22]:X2} $23 {p.CellIndexNext:X2}!={g[0x23]:X2} $2B {p.ScrollState:X2}!={g[0x2B]:X2}"
                  : fieldBadIdx >= 0 ? $"{fieldNames[fieldBadIdx]} port={pv[fieldBadIdx]:X4} rom={ov[fieldBadIdx]:X4}"
                  : !fEnemies ? "ENEMIES (see ENEMIES clause)"
                  : !fPal ? "PAL $3F00"
                  : !fOam ? "OAM"
                  : !panelOk ? "PANEL" : "?";
            }
        }
        int span = avail - 1 - seedFrame;
        if (limit > 0) span = System.Math.Min(limit, span);
        // Which fields each verdict is allowed to certify. Printed inline so a RESULT match cannot
        // be read as full-frame identity: RESULT covers 2 (or 8, with camera) fields, STRICT covers
        // every field this comparison reads, including the per-slot enemy state.
        string resultOf = camera
            ? string.Join(",", new[] { fieldNames[0], fieldNames[1], fieldNames[2], fieldNames[3],
                                       fieldNames[4], fieldNames[5], fieldNames[6], fieldNames[7] })
            : fieldNames[0] + "," + fieldNames[1];
        string strictOf = string.Join(",", fieldNames)
            + (scoreEnemies ? ",ENEMIES" : "") + (palette ? ",PAL$3F00" : "")
            + (scoreOam ? ",OAM" : "") + (pause ? ",PANEL" : "");
        if (palette)
        {
            sb.Append(palFirstBad < 0
                ? $"PAL $3F00: MATCH {palOk}/{span} "
                : $"PAL $3F00: {palOk}/{span} (first bad f{palFirstBad}, death window f795-f841 {winOk}/{winTotal}) ");
            sb.Append($"PALMODEL[$0300 {mirOk}/{span}{((mirFirstBad >= 0) ? " first " + mirFirstDetail : " MATCH")} | $0320 {srcOk}/{span}{((srcFirstBad >= 0) ? " first " + srcFirstDetail : " MATCH")} | $18 {flagOk}/{span}{((flagFirstBad >= 0) ? " first " + flagFirstDetail : " MATCH")}] ");
        }
        sb.Append("FIELDS[");
        for (int i = 0; i < fieldNames.Length; i++)
            if (firstBad[i] >= 0)
                sb.Append($"{fieldNames[i]}:f{firstBad[i]} {badPort[i]:X4}!={badOrig[i]:X4} ");
        sb.Append("] ");
        sb.Append(firstDiv < 0
            ? $"RESULT: MATCH {matchFrames}/{span} frames (seed {seedFrame}) [of {resultOf}]"
            : $"RESULT: first divergence frame {firstDiv} (matched {matchFrames}/{span}, seed {seedFrame}) [of {resultOf}]");
        sb.Append(strictFirstDiv < 0
            ? $"  STRICT: MATCH {strictMatch}/{span} [of {strictOf}]"
            : $"  STRICT: first divergence f{strictFirstDiv} {strictFirstDetail} (matched {strictMatch}/{span}) [of {strictOf}]");
        if (strictNoOam > 0) sb.Append($" [OAM data absent on {strictNoOam} frames: not scored]");
        if (scoreOam)
        {
            sb.Append($"  OAM: exact {oamFrameOk}/{oamTotal} frames, mean {(oamTotal == 0 ? 0 : oamByteSum / oamTotal)}/256 bytes,{oamFirstDetail}");
            var hist = new System.Text.StringBuilder();
            for (int e = 0; e < 64 && hist.Length < 420; e++)
                if (oamEntryBad[e] > 0) hist.Append($"e{e}:{oamEntryBad[e]} ");
            sb.Append($"  OAM-BAD-ENTRIES(of {oamTotal}): {hist}");
        }
        if (scoreEnemies)
            sb.Append(enemyFirstDiv < 0
                ? $"  ENEMIES: MATCH {enemyMatchFrames}/{enemyTotalFrames} frames (slots 01-0F: X/Y/state+active/beh/anim identical both ways)"
                : $"  ENEMIES: first divergence f{enemyFirstDiv} slot {enemyDivSlot:X2} field {enemyDivField} port={enemyDivPort:X2} orig={enemyDivOrig:X2} (matched {enemyMatchFrames}/{enemyTotalFrames})");
        if (pause)
            sb.Append(pauseFirstBad < 0
                ? $"  PANEL: MATCH {pauseOk}/{pauseTotal} frames (slots 0-5 $04D0/$0400/$0410/$0420/$0430/$04F0/$0500/$0510/$0520 + $05C2 block + $7C + $FE)"
                : $"  PANEL: first divergence {pauseFirstDetail} (matched {pauseOk}/{pauseTotal})");
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

    /// <summary>G58 pause golden: mission 1 resumed from `gold_w00/gameplay.mss` with Start
    /// pressed at f20 and held to f23, so f20…69 are the bank-4 $8F00 phase. `$14` is an
    /// edge byte, so the held press cannot resume — the resume needs the second capture.</summary>
    public static string PauseHold() => Compare("pause_hold", 70,
        f => (f >= 20 && f < 24) ? (byte)0x10 : (byte)0x00,
        camera: true, enemies: true, palette: true, scoreEnemies: true, scoreOam: true,
        goldenDir: "gold_pause_m1", pause: true);

    /// <summary>The resume path $8FCD…$9025: a second Start edge at f45 closes the phase,
    /// so the same six slots, block, palette and mask have to come back byte-exactly.</summary>
    public static string PauseResume() => Compare("pause_resume", 80,
        f => ((f >= 20 && f < 24) || (f >= 45 && f < 49)) ? (byte)0x10 : (byte)0x00,
        camera: true, enemies: true, palette: true, scoreEnemies: true, scoreOam: true,
        goldenDir: "gold_pause_resume", pause: true);

    /// <summary>Same run as <see cref="PauseHold"/> but with the full per-frame trace plus the
    /// 64-entry port/ROM OAM dump, written to Temp/pause_trace_trace.txt.</summary>
    public static string PauseTrace() => Compare("pause_trace", 70,
        f => (f >= 20 && f < 24) ? (byte)0x10 : (byte)0x00,
        camera: true, enemies: true, palette: true, scoreEnemies: true, scoreOam: true,
        goldenDir: "gold_pause_m1", pause: true, trace: true);

    /// <summary><see cref="PauseResume"/> with the per-frame OAM dump, used to attribute which
    /// frames still diverge: Temp/pause_res_trace_trace.txt.</summary>
    public static string PauseResumeTrace() => Compare("pause_res_trace", 80,
        f => ((f >= 20 && f < 24) || (f >= 45 && f < 49)) ? (byte)0x10 : (byte)0x00,
        camera: true, enemies: true, palette: true, scoreEnemies: true, scoreOam: true,
        goldenDir: "gold_pause_resume", pause: true, trace: true);

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
    /// Natural contact golden `gold_contact` (objective item 3/7): mission 1 from
    /// `m1_standing.mss`, original input is `0:right@10-360` — NO jump, so the player walks
    /// right until the room-0x02 tower wall (the G14 finding: the walker sits behind that
    /// wall, so a straight walk can't reach it). Replaying the EXACT schedule and comparing
    /// byte-exact tells whether the port stops at the same wall at the same X as the ROM.
    /// If it diverges that is a real finding (G14 climb), not something to fake.
    /// </summary>
    public static string Contact() => Compare("contact", 400,
        f => (byte)(f >= 10 && f <= 360 ? 0x01 : 0x00),
        camera: true, enemies: true);

    /// <summary>
    /// M7 HUD (G44): full 256-byte OAM compare against gameplay goldens, frame by frame.
    /// This is the instrument that measures where the 8-sprite HUD block lands; before the
    /// $95-driven cursor it sat at a fixed entries 1-8 and the score plateaued near
    /// OamVerify's ~101/256.
    /// </summary>
    public static string HudOam()
    {
        var sb = new System.Text.StringBuilder();
        sb.Append(Compare("hud_oam_contact", 400,
            f => (byte)(f >= 10 && f <= 360 ? 0x01 : 0x00),
            camera: true, enemies: true, goldenDir: "gold_contact", scoreOam: true));
        sb.Append(" ||| ");
        sb.Append(Compare("hud_oam_jump", 120, f =>
        {
            byte b = 0;
            if (f >= 10 && f <= 40) b |= 0x01;
            if (f >= 20 && f <= 22) b |= 0x80;
            return b;
        }, goldenDir: "gold_jump", scoreOam: true));
        sb.Append(" ||| ");
        sb.Append(Compare("hud_oam_longroute", 2500,
            f =>
            {
                byte b = (f >= 2 && f <= 2490) ? (byte)0x01 : (byte)0x00;
                for (int k = 0; k < 18; k++)
                {
                    int s = 160 + 130 * k;
                    if (f >= s && f <= s + 25) b |= 0x80;
                }
                return b;
            },
            camera: true, enemies: true, goldenDir: "gold_m1_longroute", scoreOam: true));
        File.WriteAllText("D:/Art/SW Code Zero Unity/Temp/hud_oam_sweep.txt", sb.ToString());
        return sb.ToString();
    }

    /// <summary>
    /// G36 step 1 — the long mission-1 route `gold_m1_longroute` (2500 frames from
    /// m1_standing.mss, Right held f2-2490 plus 26-frame A-holds every 130 frames from f160,
    /// NO B so the unported $CE39 weapon spawn cannot pollute the diff). The ROM on this exact
    /// schedule clears the pylon, parks in state $04 at X=$01C1 for 126 frames, escapes it via
    /// $D21E on the next A-hold ($50=$0B), reaches maxX=$0394 and only then dies in a pit
    /// (f795, $50=$07) — so the live port's death at X=$01CF is a port divergence, not input
    /// policy. Full 2500-frame capture: the 620-frame cap was only ever a guess at the
    /// run_script budget, and the whole comparison executes in ~75 ms.
    /// </summary>
    public static string M1LongRoute() => Compare("m1_longroute", 2500,
        f =>
        {
            byte b = (f >= 2 && f <= 2490) ? (byte)0x01 : (byte)0x00;
            for (int k = 0; k < 18; k++)
            {
                int s = 160 + 130 * k;
                if (f >= s && f <= s + 25) b |= 0x80;
            }
            return b;
        },
        camera: true, enemies: true, trace: true, limit: 0, palette: true);

    /// <summary>
    /// Shared schedule for the mission route goldens `gold_m2_route` … `gold_m7_route`:
    /// 1500 frames captured from each `gold_wNN/gameplay.mss` — the state probe_run saves at
    /// the END of that world's entry capture, so the port seeds mid-mission (no $DDFE entry
    /// handover, hence seedFrame 0 and no entryFreeze). Rhythm is the m1 long route: Right held
    /// f2-1490 plus 26-frame A-holds every 130 frames from f120, and NO B so the unported
    /// $CE39 weapon spawn cannot pollute the diff. First traversal comparison per mission
    /// beyond the 371-frame entry fall.
    /// </summary>
    private static string Route(string name, int world, bool trace = false) => Compare(name, 1500,
        f =>
        {
            byte b = (f >= 2 && f <= 1490) ? (byte)0x01 : (byte)0x00;
            for (int k = 0; k < 11; k++)
            {
                int s = 120 + 130 * k;
                if (f >= s && f <= s + 25) b |= 0x80;
            }
            return b;
        },
        camera: true, enemies: true, world: world, palette: true, trace: trace,
        // G51 root cause, measured in the golden itself: `gold_m6_route` is the only route captured
        // while the world loader is still resident. f0…f2 hold $50=$09, $10=$40, $95=$FE, $97=$70,
        // $0460/$0550/$0560 and slot $0F byte-identical, and only $92 ticks. $97 goes to $04 and
        // $95 to $FF on f3, but $0460/$0550 stay at 9E/AB on f3 and only move to F3/6B on f4 —
        // so m6's release frame ticks the engine (parity + anim) one frame BEFORE the player pass
        // runs, unlike gold_w05 f345 where they coincide. The three frozen iterations reproduce
        // the tail of `$DE03 JSR $FF06 + $DE06 JMP $C254→$C270`; the parity catch-up fires on
        // iteration 3 (port f3 $95=FF matches golden), and gravity starts on iteration 4.
        entryFreeze: world == 5 ? 3 : 0);

    public static string M2Route() => Route("m2_route", 1);
    public static string M3Route() => Route("m3_route", 2);
    public static string M4Route() => Route("m4_route", 3);
    public static string M5Route() => Route("m5_route", 4);
    public static string M6Route() => Route("m6_route", 5);
    public static string M7Route() => Route("m7_route", 6);

    /// <summary>Per-frame dumps for the two diverging routes, written to
    /// Temp/{name}_trace.txt (m3 diverges at f31 on Y/$0550/$0560, m4 at f63 on
    /// state $06-vs-$07 plus the whole gravity vector).</summary>
    public static string RouteTraces()
    {
        M3RouteTrace(); M4RouteTrace();
        return "wrote Temp/m3_route_trace.txt and Temp/m4_route_trace.txt";
    }
    public static string M3RouteTrace() => Route("m3_route", 2, trace: true);
    public static string M4RouteTrace() => Route("m4_route", 3, trace: true);

    /// <summary>G51: `gold_m6_route` diverges on the SECOND frame, so the diff has to start at the
    /// seed. Full per-frame lines plus the ROM/port slot lists for f0…f12 → Temp/m6_route_trace.txt.
    /// Known so far: the seed is already `$50=$09` with `$10=$40` (the port gives `$10=$01`) and slot
    /// `$0F` is live with `beh=$69`, which the bank-6 dispatcher never has to serve in m1…m5.</summary>
    public static string M6EntryDump()
    {
        _enemyDumpLo = 0; _enemyDumpHi = 12;
        try { return Route("m6_route", 5, trace: true); }
        finally { _enemyDumpLo = _enemyDumpHi = -1; }
    }

    /// <summary>G52: m3 f1314 is the ROM's post-death world reload ($D41B -> $D423), not a
    /// fourth heart-ladder hit - f1305..f1313 match byte-for-byte with a frozen death
    /// snapshot and a FULL ladder on both sides. Dumps the full per-frame line for
    /// f1240-f1320 (ladders, $64/$6D branch counters, slot lists) so the reload transition
    /// itself is visible. Pair with Tools/DarkwingResearch/frame_diff.py for the byte list.</summary>
    public static string M3ReloadDump()
    {
        _enemyDumpLo = 1240; _enemyDumpHi = 1320;
        try { return Route("m3_route", 2, trace: true); }
        finally { _enemyDumpLo = _enemyDumpHi = -1; }
    }

    /// <summary>Missions 2-7 traversal scoreboard: one RESULT line each (Compare keeps the
    /// full per-frame detail in Temp/{name}_trace.txt because the console truncates).</summary>
    public static string Routes()
    {
        var sb = new System.Text.StringBuilder();
        void One(string label, System.Func<string> run)
        {
            string full;
            try { full = run(); }
            catch (System.Exception e) { sb.Append(label).Append(": ERROR ").Append(e.Message).Append('\n'); return; }
            int i = full.LastIndexOf("RESULT:");
            sb.Append(label).Append(": ")
              .Append(i < 0 ? "no result" : full.Substring(i, System.Math.Min(500, full.Length - i)))
              .Append('\n');
        }
        One("m2(w01)", M2Route);
        One("m3(w02)", M3Route);
        One("m4(w03)", M4Route);
        One("m5(w04)", M5Route);
        One("m6(w05)", M6Route);
        One("m7(w06)", M7Route);
        File.WriteAllText("D:/Art/SW Code Zero Unity/Temp/routes_summary.txt", sb.ToString());
        return sb.ToString();
    }

    /// <summary>G25 acceptance: the per-frame PPU-palette verdict ($3F00-$3F1F, 32 bytes) of the
    /// captures whose route runs the zone service $EDF1. Of the zone handlers' outputs, the ones
    /// these fixtures record frame by frame are the $C2F9/$C319 palette reload and the $18 upload
    /// flag ($1A/$6D/$70 are zero on every frame of all seven — see the note in GAPS), so this is
    /// the metric that decides whether a handler's cell bands and palette indices are right.
    /// `Routes()` truncates at RESULT:, which drops the PAL clause that precedes it.</summary>
    public static string ZoneScore()
    {
        var sb = new System.Text.StringBuilder();
        void One(string label, System.Func<string> run)
        {
            string full;
            try { full = run(); }
            catch (System.Exception e) { sb.Append(label).Append(": ERROR ").Append(e.Message).Append('\n'); return; }
            int i = full.IndexOf("PAL $3F00:");
            int j = full.IndexOf("RESULT:");
            sb.Append(label).Append(": ")
              .Append(i < 0 ? "PAL NOT SCORED " : full.Substring(i, j > i ? j - i : full.Length - i))
              .Append(j < 0 ? "(no result)" : full.Substring(j, System.Math.Min(58, full.Length - j)))
              .Append('\n');
        }
        One("w0(m1_longroute)", M1LongRoute);
        One("w1(m2_route)", M2Route);
        One("w2(m3_route)", M3Route);
        One("w3(m4_route)", M4Route);
        One("w4(m5_route)", M5Route);
        One("w5(m6_route)", M6Route);
        One("w6(m7_route)", M7Route);
        File.WriteAllText("D:/Art/SW Code Zero Unity/Temp/zone_score.txt", sb.ToString());
        return sb.ToString();
    }

    /// <summary>
    /// Hook ride golden: `gold_hook_ride2` is mission 1 (world 0) with the original
    /// seeded by probe_run --patch into cell $22=$1C (row 1, col 11, script byte $41 =
    /// hook column) at the row bottom; the grab fires next frame and the camera rides
    /// down one 240-px row ($FA 3 px/frame), then $D7F9 releases the player ($50=1).
    /// Frame 0 of the capture is the patched seed frame, so the port starts at frame 1
    /// (first ridden frame) and every later frame is compared, camera state included.
    /// </summary>
    public static string Hook() => Compare("hook_ride2", 300, f => 0, seedFrame: 1, camera: true);

    /// <summary>
    /// M6 enemy-dispatcher determinism: replays the probe_enemy_verify capture (600 frames from
    /// the mid-mission m1_gameplay save state, NO input — the player idles and every bank-6 enemy
    /// self-moves). Every slot the ROM holds active must match the port on X/Y/state/behavior/anim,
    /// so this scores the dispatcher trajectory rather than only printing it.
    /// </summary>
    public static string EnemyVerify() => Compare("enemy_verify", 600, f => 0,
        enemies: true, world: 0, goldenDir: "probe_enemy_verify", scoreEnemies: true, trace: false);

    /// <summary>Sweep every probe_enemy_verify capture and keep only the ENEMIES verdict line,
    /// so the bank-6 frame-exact coverage claim spans all multi-enemy captures, not just one.</summary>
    public static string EnemySweep()
    {
        var sb = new System.Text.StringBuilder();
        foreach (string dir in new[] { "probe_enemy_verify", "probe_enemy_verify2", "probe_enemy_verify3",
                                        "probe_enemy_verify4", "probe_enemy_verify5" })
        {
            if (!System.IO.File.Exists("D:/Art/SW Code Zero Unity/Research/DarkwingDuck/reference/" + dir + "/ram_frames.bin"))
            { sb.Append($"{dir}: NO CAPTURE\n"); continue; }
            string r = Compare("enemy_sweep", 600, f => 0, enemies: true, world: 0,
                goldenDir: dir, scoreEnemies: true, trace: false);
            int i = r.IndexOf("ENEMIES:");
            sb.Append($"{dir}: {(i >= 0 ? r.Substring(i) : "(no enemy verdict)")} \n");
        }
        System.IO.File.WriteAllText("D:/Art/SW Code Zero Unity/Temp/enemy_sweep.txt", sb.ToString());
        return sb.ToString();
    }

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
            sb.Append(Compare($"w{world:X2}", 360, WorldInput,
                seedFrame: 328, camera: true, enemies: true, world: world, entryFreeze: 16)).Append('\n');
        return sb.ToString();
    }

    /// <summary>All seven `gold_wNN/request.json` files carry the identical schedule
    /// (`0:right@30-33 0:a@120-123 0:a@300-303 0:a@460-463 0:right@520-690`). Only the last
    /// two fall inside the f328-seeded window; replaying them is mandatory — with zero input
    /// the port stands still while the golden walks right from f520.</summary>
    private static byte WorldInput(int f) =>
        (byte)(((f >= 520 && f <= 690) ? 0x01 : 0) | ((f >= 460 && f <= 463) ? 0x80 : 0));

    /// <summary>
    /// Compact per-frame trace of the $D517 entry fall for one world (G28): the golden is
    /// seeded on f328, the first frame that shows $50=$09, and only Y/vy/$0460/state are
    /// printed so the whole 700-frame capture still fits in the tool output.
    /// </summary>
    /// <summary>Frame at which the trace also dumps the port's tile-type strip and the
    /// ROM's own $0600 collision window (-1 = off; set by the *Dump entries below).</summary>
    private static int _tileDumpFrame = -1;

    /// <summary>w03 falls through the floor the ROM lands on at f582 (G30) — dump the probe data.</summary>
    public static string FloorProbeDumpW03()
    {
        _tileDumpFrame = 582;
        try { return TraceEntry(3, 578, 584); }
        finally { _tileDumpFrame = -1; }
    }

    public static string WorldEntryTrace() => TraceEntry(0, 505, 600);

    /// <summary>G33: the pit-death frame of gold_w05 (f656, $04D0→$00) plus the death-service
    /// frames that follow, where $D41B tail-calls $DD57 and the world stays frozen. Prints
    /// $04D0/$05E6/$05E4/$64/$6D on both sides.</summary>
    public static string DeathTraceW05()
    {
        _enemyDumpLo = 648; _enemyDumpHi = 700;
        try { return Compare("w05", 360, WorldInput, seedFrame: 328,
                             camera: true, enemies: true, world: 5, entryFreeze: 16); }
        finally { _enemyDumpLo = _enemyDumpHi = -1; }
    }

    /// <summary>Shift-16 G41 follow-up: the w03 card park (gold $95 frozen f460-520, exactly
    /// as in w01/w02/w04, which all match 371/371) is not what moved w03's first divergence
    /// from f664 to f604, so dump the per-frame anim/X/Xfrac clock and the live slots across
    /// the release (f521) and the f568-f599 state-1/6 event.</summary>
    public static string GaitTraceW03()
    {
        _enemyDumpLo = 560; _enemyDumpHi = 612;
        try
        {
            return Compare("w03", 360, WorldInput, seedFrame: 328, camera: true,
                enemies: true, world: 3, entryFreeze: 16, trace: true, limit: 300);
        }
        finally { _enemyDumpLo = _enemyDumpHi = -1; }
    }

    /// <summary>Inclusive frame window for the per-frame active-slot dump inside the trace
    /// (-1 disables it). G31 needs the w03 landing frames 575-592.</summary>
    private static int _enemyDumpLo = -1, _enemyDumpHi = -1;

    /// <summary>Maps the port's object slots onto the ROM's $04xx address layout.</summary>
    private static byte[] PackedSlots(Darkwing.Core.DarkwingGame game)
    {
        var pr = new byte[2048];
        for (int sl = 0; sl <= 0x0F; sl++)
        {
            var s = game.Objects.Slots[sl];
            pr[0x4D0 + sl] = (byte)(s.Active ? 0x80 | s.StateLow : s.StateLow);
            pr[0x400 + sl] = s.Flags;
            pr[0x570 + sl] = s.BehaviorParam;
            pr[0x4E0 + sl] = s.Behavior;
            pr[0x480 + sl] = s.T480;
            pr[0x490 + sl] = s.T490;
            pr[0x580 + sl] = s.Direction;
            pr[0x590 + sl] = s.PlacementIndex;
            pr[0x4F0 + sl] = (byte)(s.X & 0xFF); pr[0x500 + sl] = (byte)(s.X >> 8);
            pr[0x510 + sl] = (byte)(s.Y & 0xFF); pr[0x520 + sl] = (byte)(s.Y >> 8);
            pr[0x420 + sl] = s.AnimNumber;
            pr[0x530 + sl] = s.XVelFrac; pr[0x540 + sl] = (byte)s.XVelInt;
            pr[0x550 + sl] = s.YVelFrac; pr[0x560 + sl] = (byte)s.YVelInt;
        }
        return pr;
    }

    /// <summary>G36 step 6: ROM slot-0 death counters vs the port's, so the $D353 timer
    /// ladder is read from the capture instead of guessed.</summary>
    private static string DeathDiag(byte[] r, Darkwing.Core.ObjectSystem.Slot s0) =>
        $" | D d0={r[0x4D0]:X2}/{s0.StateLow:X2} t80={r[0x480]:X2}/{s0.T480:X2} t90={r[0x490]:X2}/{s0.T490:X2} f00={r[0x400]:X2}/{s0.Flags:X2} hp={r[0x5A0]:X2}/{s0.Health:X2}";

    /// <summary>G36 step 6: read the m1_longroute golden directly — death-window zero-page
    /// bytes plus the full changed-address list per frame, so the f814 respawn transition is
    /// identified by RAM address instead of guessed.</summary>
    public static string DeathDump()
    {
        string root = "D:/Art/SW Code Zero Unity";
        byte[] ramAll = File.ReadAllBytes(root + "/Research/DarkwingDuck/reference/gold_m1_longroute/ram_frames.bin");
        int avail = ramAll.Length / 2048;
        var sb = new System.Text.StringBuilder();
        int[] keys = { 0x14, 0x31, 0x3B, 0x50, 0x51, 0x53, 0x54, 0x58, 0x5F, 0x60, 0x61, 0x64, 0x65, 0x66,
                       0x6D, 0x7C, 0x97, 0x98, 0x9A, 0x9B,
                       0x400, 0x420, 0x4D0, 0x4F0, 0x500, 0x510, 0x520, 0x5E4, 0x5E6, 0xFA, 0xFB, 0xFC, 0xFD };
        sb.Append($"frames={avail}\nKEYS\n");
        for (int f = 770; f <= 830 && f < avail; f++)
        {
            byte[] g = new byte[2048];
            System.Array.Copy(ramAll, f * 2048, g, 0, 2048);
            sb.Append($"f{f} ");
            for (int k = 0; k < keys.Length; k++) sb.Append($"{keys[k]:X3}={g[keys[k]]:X2} ");
            sb.Append('\n');
        }
        sb.Append("\nDIFFS\n");
        byte[] prev = new byte[2048];
        System.Array.Copy(ramAll, 770 * 2048, prev, 0, 2048);
        for (int f = 771; f <= 830 && f < avail; f++)
        {
            byte[] g = new byte[2048];
            System.Array.Copy(ramAll, f * 2048, g, 0, 2048);
            sb.Append($"f{f}: ");
            int shown = 0;
            for (int a = 0; a < 2048; a++)
            {
                if (g[a] == prev[a]) continue;
                if (a > 0xFF && !(a >= 0x400 && a <= 0x600)) continue;
                sb.Append($"{a:X3}={prev[a]:X2}>{g[a]:X2} ");
                if (++shown >= 90) { sb.Append("+more "); break; }
            }
            sb.Append('\n');
            System.Array.Copy(g, prev, 2048);
        }
        File.WriteAllText(root + "/Temp/death_dump.txt", sb.ToString());
        return $"death_dump.txt written: {sb.Length} chars";
    }

    private static string ListSlots(byte[] r, bool inactive = false)
    {
        var s = new System.Text.StringBuilder();
        for (int sl = 1; sl <= 0x0F; sl++)
        {
            bool active = (r[0x4D0 + sl] & 0x80) != 0;
            if (!active && !inactive) continue;
            // An inactive slot is only interesting while it still carries a template.
            if (!active && r[0x4E0 + sl] == 0 && r[0x420 + sl] == 0) continue;
            s.Append($"{(active ? "" : "-")}{sl:X2}:{r[0x4F0 + sl] | (r[0x500 + sl] << 8):X4},{r[0x510 + sl] | (r[0x520 + sl] << 8):X4}/an{r[0x420 + sl]:X2}bh{r[0x4E0 + sl]:X2}st{r[0x4D0 + sl]:X2}t{r[0x480 + sl]:X2}q{r[0x490 + sl]:X2}d{r[0x580 + sl]:X2}pl{r[0x590 + sl]:X2}p{r[0x570 + sl]:X2}f{r[0x400 + sl]:X2}vy{r[0x560 + sl]:X2},{r[0x550 + sl]:X2} ");
        }
        return s.ToString();
    }

    /// <summary>G31: which placement does the ROM activate at the w03 landing point, where
    /// the golden goes to $50=$06 (hurt) at f583 while the port keeps walking.</summary>
    public static string EnemyDumpW03()
    {
        _enemyDumpLo = 575; _enemyDumpHi = 592;
        try { return TraceEntry(3, 575, 592); }
        finally { _enemyDumpLo = _enemyDumpHi = -1; }
    }

    /// <summary>The two remaining world divergences (w03 f664, w05 f656 — w01 matched after
    /// the spawner became pointer state) with the active-slot dump, to tell tile hazards
    /// from enemy contact.</summary>
    public static string DivTraceAll()
    {
        var sb = new System.Text.StringBuilder();
        int[][] w = { new[] { 3, 658, 670 }, new[] { 5, 652, 664 } };
        foreach (var t in w)
        {
            _enemyDumpLo = t[1]; _enemyDumpHi = t[2];
            sb.Append($"=== w{t[0]:X2} ===\n").Append(TraceEntry(t[0], t[1], t[2])).Append('\n');
            _enemyDumpLo = _enemyDumpHi = -1;
        }
        return sb.ToString();
    }

    /// <summary>w03 starts falling on its own at f568 — the release-window trace for it.</summary>
    public static string ReleaseTraceW03() => TraceEntry(3, 540, 600);

    public static string TraceEntry(int world, int from, int to)
    {
        var full = Compare($"w{world:X2}", 360, WorldInput,
            seedFrame: 328, camera: true, enemies: true, world: world, entryFreeze: 16, trace: true);
        var lines = full.Split('\n');
        var keep = new System.Text.StringBuilder();
        foreach (var l in lines)
        {
            if (l.StartsWith("DUMP")) { keep.Append(l).Append('\n'); continue; }
            int sp = l.IndexOf(' ');
            if (!l.StartsWith("f") || sp < 2) continue;
            if (!int.TryParse(l.Substring(1, sp - 1), out int f)) continue;
            if (f >= from && f <= to) keep.Append(l).Append('\n');
        }
        keep.Append(lines[lines.Length - 1]);
        return keep.ToString();
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

    /// <summary>
    /// $CE39 weapon-spawn golden `gold_b_attack`: mission 1 standing still, B held for four
    /// frames at f30-33, f100-103 and f160-163 ($16 = $40 there, $14 = $40 only on the first
    /// frame of each window). Slot 4 must appear on the PRESS frame already initialized
    /// ($04D0=$81, $0540=$04, X = player $2C + $0F + one 4 px step = $3F), travel 4 px/frame
    /// to the right edge kill, and the player must hold the fire pose $05 until the anim
    /// reaches frame 2 / timer 4, where $CBF6-$CC02 releases it back to idle $01.
    /// Slot 1 is live for the whole capture (beh $00, anim $1B) and $CE48 never tests it,
    /// so this also proves the projectile scan is restricted to slots 4..2.
    /// </summary>
    public static string BAttack() => Compare("b_attack", 200,
        f => (f >= 30 && f <= 33) || (f >= 100 && f <= 103) || (f >= 160 && f <= 163)
             ? (byte)0x40 : (byte)0x00,
        seedFrame: 29, camera: true, enemies: true, goldenDir: "gold_b_attack", scoreEnemies: true);

    /// <summary>The corrected ENEMIES check (Y + the active bit both ways) found a class the old
    /// ROM-active filter could not see: b_attack f79 holds slot 04 alive in the port and dead in
    /// the ROM. Same run with both slot lists printed around that frame.</summary>
    public static string BAttackSlotDump()
    {
        _enemyDumpLo = 74; _enemyDumpHi = 84;
        try
        {
            return Compare("b_attack_dump", 200,
                f => (f >= 30 && f <= 33) || (f >= 100 && f <= 103) || (f >= 160 && f <= 163)
                     ? (byte)0x40 : (byte)0x00,
                seedFrame: 29, camera: true, enemies: true, goldenDir: "gold_b_attack",
                scoreEnemies: true, trace: true);
        }
        finally { _enemyDumpLo = _enemyDumpHi = -1; }
    }

    /// <summary>Runs the whole player-physics suite and returns only the RESULT lines.</summary>
    public static string Suite()
    {
        var sb = new System.Text.StringBuilder();
        void One(string label, string full)
        {
            int i = full.LastIndexOf("RESULT:");
            sb.Append(label).Append(": ").Append(i < 0 ? "no result" : full.Substring(i, System.Math.Min(500, full.Length - i))).Append('\n');
        }
        One("walk", Walk());
        One("jump", Jump());
        One("b_attack", BAttack());
        One("hook", Hook());
        One("traverse", Traverse());
        One("contact", Contact());
        One("m1route", M1Route());
        One("hurt", Hurt());
        One("door", Door());
        One("worldseeds", WorldSeeds());
        foreach (var line in WorldWalks().Split('\n'))      // seven worlds, one line each
        {
            int i = line.LastIndexOf("RESULT:");
            int j = line.IndexOf("] w");
            if (i < 0 || j < 0) continue;
            sb.Append(line.Substring(j + 3, 3)).Append("fall: ")
              .Append(line.Substring(i, System.Math.Min(500, line.Length - i))).Append('\n');
        }
        // the run_script console truncates, so keep the scoreboard on disk too
        One("longroute", M1LongRoute());
        sb.Append(Routes());     // missions 2-7 traversal, one line each
        File.WriteAllText("D:/Art/SW Code Zero Unity/Temp/suite_summary.txt", sb.ToString());
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
        // $F9C2's heart ladder is not a counter but the player's own RAM metasprite tile bytes:
        // the $C8CF template leaves $05B2=$80, $05B4=$80, $05B6=$81, $05B8=$81, and each survived
        // hit marks the next one ($05B4→$82, $05B8→$83, $05B6→$83); the 4th marks $05B2→$82 and
        // writes $50=$07. Mid-mission seeds (gold_m*_route) start at up to 3 marks, so without
        // this the port takes the 4th hit as a 1st and stays in state $06 where the ROM dies.
        p.HitLadder = (byte)(ram0[0x5B2] != 0x80 ? 4                       // $FA11: lethal hit landed
                             : (ram0[0x5B4] != 0x80 ? 1 : 0) + (ram0[0x5B8] != 0x81 ? 1 : 0) +
                               (ram0[0x5B6] != 0x81 ? 1 : 0));
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
        game.Objects.CursorFwd = ram0[0x70];      // $70/$71 spawner entry cursors
        game.Objects.CursorRev = ram0[0x71];
        // $95 is the frame engines' own counter (bank5 $8000 / bank4 $9800) and bank5 $80EB
        // gates the player's anim tick on $95&$04 while $7C != 0, so the blink phase is
        // captured state: the port's boot value can never match the ROM's.
        game.FrameParity = ram0[0x95];
        // $72-$7B: the spawner dereferences pointers, so the placement list is pointer
        // state. Every gold_w capture latches $31 to $01..$06 and keeps $72/$73 on the
        // $31=$00 list — bank-4:$8000 is not in the world-load path, so the list has to
        // be seeded from the capture rather than derived from the world id.
        game.Objects.SelectPlacementListByPointer(ram0[0x72], ram0[0x73]);
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
        FieldResult(sb, names, ok, firstBad, span);
        File.WriteAllText(root + "/Temp/hurt_trace.txt", sb.ToString());   // full text, the console truncates
        return sb.ToString();
    }

    /// <summary>
    /// Per-field golden checks (Hurt/Door) report one line per field, so Suite's RESULT-only
    /// scoreboard used to print "no result" for checks that actually passed every field of every
    /// frame. This folds them into the same verdict line the frame-based checks use: all
    /// field-frames must match, and the first diverging frame is the first field divergence.
    /// </summary>
    private static void FieldResult(System.Text.StringBuilder sb, string[] names, int[] ok, int[] firstBad, int span)
    {
        int totalOk = 0, allFields = 0, firstDiv = -1;
        for (int i = 0; i < names.Length; i++)
        {
            totalOk += ok[i]; allFields += span;
            if (firstBad[i] >= 0 && (firstDiv < 0 || firstBad[i] < firstDiv)) firstDiv = firstBad[i];
        }
        sb.Append(firstDiv < 0
            ? $"RESULT: MATCH {allFields}/{allFields} field-frames ({names.Length} fields x {span} frames)"
            : $"RESULT: first divergence frame {firstDiv} (matched {totalOk}/{allFields} field-frames)");
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
        game.Objects.CursorFwd = ram0[0x70];      // $70/$71 spawner entry cursors
        game.Objects.CursorRev = ram0[0x71];
        // $95 is the frame engines' own counter (bank5 $8000 / bank4 $9800) and bank5 $80EB
        // gates the player's anim tick on $95&$04 while $7C != 0, so the blink phase is
        // captured state: the port's boot value can never match the ROM's.
        game.FrameParity = ram0[0x95];
        // $72-$7B: the spawner dereferences pointers, so the placement list is pointer
        // state. Every gold_w capture latches $31 to $01..$06 and keeps $72/$73 on the
        // $31=$00 list — bank-4:$8000 is not in the world-load path, so the list has to
        // be seeded from the capture rather than derived from the world id.
        game.Objects.SelectPlacementListByPointer(ram0[0x72], ram0[0x73]);
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
        slot0.StateLow = ram0[0x4D0];   // $CAFC gates on this byte, so the seed must carry it
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
        FieldResult(sb, names, ok, firstBad, span);
        return sb.ToString();
    }

    /// <summary>
    /// ROM palette ($3F00-$3F1F) per frame across the death→reload window of
    /// gold_m1_longroute, side by side with the port's own Ppu.PaletteRam. The golden
    /// artifact is 32 bytes/frame written by the same endFrame callback as ram_frames.bin,
    /// so the harness frame index maps identically.
    /// </summary>
    public static string PaletteDeath() => PaletteWindow(786, 860);

    public static string PaletteWindow(int lo, int hi, string golden = "m1_longroute", bool runPort = false)
    {
        string root = "D:/Art/SW Code Zero Unity";
        string dir = root + $"/Research/DarkwingDuck/reference/gold_{golden}";
        byte[] palAll = File.ReadAllBytes(dir + "/palette_frames.bin");
        byte[] ramAll = File.ReadAllBytes(dir + "/ram_frames.bin");
        var sb = new System.Text.StringBuilder($"[PaletteWindow {golden} f{lo}..f{hi}]\n");
        for (int f = lo; f <= hi && f * 32 < palAll.Length; f++)
        {
            byte[] g = new byte[2048];
            System.Array.Copy(ramAll, f * 2048, g, 0, 2048);
            sb.Append($"f{f} st={g[0x50]:X2} d0={g[0x4D0]:X2} x={g[0x500]:X2}{g[0x4F0]:X2} y={g[0x520]:X2}{g[0x510]:X2} " +
                      $"w31={g[0x31]:X2} 64={g[0x64]:X2} 66={g[0x66]:X2} 0F={g[0x0F]:X2} 0D={g[0x0D]:X2} 0E={g[0x0E]:X2} FE={g[0xFE]:X2} F0={g[0xF0]:X2} " +
                      $"src320={g[0x320]:X2}{g[0x321]:X2}{g[0x322]:X2} mir300={g[0x300]:X2}{g[0x301]:X2}{g[0x302]:X2} ");
            for (int i = 0; i < 32; i++) sb.Append(palAll[f * 32 + i].ToString("X2"));
            sb.Append('\n');
        }
        string text = sb.ToString();
        File.WriteAllText(root + "/Temp/palette_window.txt", text);
        return $"wrote Temp/palette_window.txt ({text.Length} chars)";
    }
}
