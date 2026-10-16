namespace Darkwing.Core
{
    /// <summary>
    /// Deterministic game core. One Step = one NTSC frame of original game logic
    /// (the original updates game state once per NMI-delimited frame).
    /// All mutable game state lives here; no UnityEngine types.
    ///
    /// Time model: NTSC frame = 4 CPU cycles master clock / 12 per PPU pixel;
    /// the game-visible cadence is 60.098814 frames/sec. The host feeds Steps
    /// from an accumulator; Step count is the game clock (never host wall time).
    /// </summary>
    public sealed class DarkwingGame
    {
        public const int ScreenWidth = 256;
        public const int ScreenHeight = 240;
        public const double NtscFramesPerSecond = 60.098814; // 39375000 / (341*262*2) exact rational 39375000/178684

        public readonly byte[] Framebuffer = new byte[ScreenWidth * ScreenHeight]; // NES palette indices 0..63
        public long Frame;
        /// <summary>$95: the NMI frame counter. Bit 2 gates the player's invulnerability
        /// blink (bank5 $80EB) and bit 0 alternates the slot update order.</summary>
        public byte FrameParity;
        public readonly PlayerCore Player;
        public readonly Ppu Ppu;
        public readonly ObjectSystem Objects;
        public readonly OamDispatcher Oam;
        public readonly HudComposer Hud;
        private readonly GameData _data;

        public DarkwingGame() : this(null) { }

        public DarkwingGame(GameData data) : this(data, EmptyTileMap.Instance) { }

        public DarkwingGame(GameData data, ITileMap tiles)
        {
            _data = data;
            Player = new PlayerCore(tiles);
            Ppu = new Ppu(data);
            Objects = new ObjectSystem(data);
            Player.Objects = Objects;
            Player.Sfx = id => PlaySfx(id);
            Hud = new HudComposer();
            Oam = data != null
                ? new OamDispatcher(_bank5 ?? System.Array.Empty<byte>(), Objects, Ppu, Player)
                : null;
            Oam?.SetHud(Hud);
        }

        /// <summary>Raw PRG bank 5 (anim/frame/coord tables) for the OAM dispatcher.</summary>
        public byte[] Bank5 { get => _bank5; set { _bank5 = value; if (Oam != null) Oam.SetBank5(value); } }
        private byte[] _bank5;

        /// <summary>
        /// Bank-6 enemy dispatcher (plus bank-1 boss routing inside it). Setting
        /// Bank6 constructs it; set ObjectTypeTable to load the bank-4 spawn tables.
        /// </summary>
        public EnemyDispatcher Enemies { get; private set; }
        public byte[] Bank6
        {
            get => _bank6;
            set
            {
                _bank6 = value;
                Enemies = value != null ? new EnemyDispatcher(value, Objects, Player, Audio) : null;
                if (Enemies != null)
                {
                    Enemies.Tiles = TileMap;   // $EB52 probe backend
                    Enemies.BossDefeated = () => MissionCleared = true;  // $B315 → $D838
                }
            }
        }
        private byte[] _bank6;

        /// <summary>Bank-4 object type tables (object_types.bytes: 105×4 + 105 HP).</summary>
        public byte[] ObjectTypeTable
        {
            set => EnemyBehaviors.LoadTypeTable(value);
        }

        /// <summary>Ported bank-3 audio driver; null until Bank3 is set.</summary>
        public Darkwing.Audio.AudioSequencer Audio { get; private set; }
        public byte[] Bank3
        {
            get => _bank3;
            set
            {
                _bank3 = value;
                Audio = value != null && value.Length > 0
                    ? new Darkwing.Audio.AudioSequencer(value, _bank7)
                    : null;
            }
        }
        private byte[] _bank3;
        private byte[] _bank7;

        /// <summary>Late-bound bank 7 (world/music data) for the sequencer.</summary>
        public byte[] Bank7 { get => _bank7; set { _bank7 = value; } }

        /// <summary>The loaded mission's placement data (spawner source).</summary>
        public GameData.LevelDef CurrentLevel { get; set; }

        /// <summary>World tile map; StreamColumns runs after the player moves.</summary>
        public LevelTileMap TileMap
        {
            get => _tileMap;
            set { _tileMap = value; if (Enemies != null) Enemies.Tiles = value; }
        }
        private LevelTileMap _tileMap;

        /// <summary>Wire the player's level-table feeds ($32/$34 readers, $E7C6 row
        /// redraw) and seed the camera cell index ($22 = $FB*$2E + $FD).</summary>
        public void WireWorldFeed()
        {
            if (TileMap == null) return;
            var p = Player;
            p.GridAt = TileMap.GridByte;
            p.FlagAt = TileMap.FlagByte;
            p.RowStride = (byte)TileMap.GridWidth;   // $2E
            // $2D ($DF41) is the down-scroll limit $DC7C compares $FB against — it is a
            // descriptor value (world 0 = $00, worlds 2/4/5/6 = $03), not the row count.
            p.BottomRow = TileMap.World.TopRow;
            p.ReloadRow = () =>
            {
                TileMap.LoadRow(p.ScrollYRow);
                TileMap.StreamColumns(p.ScrollX);
            };
            p.CellIndex = (byte)(p.ScrollYRow * p.RowStride + (p.ScrollX >> 8)); // $22
        }

        private SectionTable _sections;

        private RoomDecoder _worldDecoder;                  // $DD57 arguments, kept for the death reload
        private byte _worldId;

        /// <summary>Records the $DD57 arguments so the death resolve ($D437) can re-run the
        /// world load. The scene normally reaches this through <see cref="LoadWorld"/>.</summary>
        public void SetWorldSource(RoomDecoder decoder, byte worldId)
        {
            _worldDecoder = decoder;
            _worldId = worldId;
        }

        /// <summary>
        /// $DD57 world load with $31 = world id (0-6 = missions 1-7, reached from the city
        /// map bubbles; ids 7+ are dialog scenes). Builds the tile map from the descriptor,
        /// seeds the player through <see cref="PlayerCore.ApplyWorldLoad"/>, and applies the
        /// per-world palettes ($C2F9 ← $DF04, $C319 ← $DF22) and CHR requests
        /// ($1A ← $DEE6, $1B ← $DEF5; the NMI maps a request to bank = value &amp; $7F, so
        /// world 0 is CHR0=14/CHR1=16 exactly as captured in probe_m1_enter).
        /// </summary>
        public void LoadWorld(RoomDecoder decoder, byte worldId)
        {
            _worldDecoder = decoder; _worldId = worldId;    // $D437 re-enters $DD57 with the same $31
            WorldPrelude();
            Player.StageSectionToEnd();     // $DE86-$DE91: the inline entry has no frame machine
            WorldSpawn();
            // $C2F9/$C319 never flag $18 — the PPU only sees a new table through a fade step's
            // $C2A2 STY $18. Inside the staged loader that is the $DE06/$C270 fade-in; this
            // entry point is the host's inline first load, so push the table once here.
            _palDirty = 0xFF;
            _sectionStage = 0;                              // $DD57 finishes inline (DE06 JMP $C254)
            SectionUsesInternalEngine = false;
            MissionCleared = false;
            WorldRunning = true;
        }

        /// <summary>$DD5F-$DDC8 plus the $DE24 render-off/staging restart — the half of
        /// $DD57 that runs before its $DDCB yield.</summary>
        private void WorldPrelude()
        {
            var w = _worldDecoder.GetWorld(_worldId);
            _world = w;
            Objects?.ClearForSectionLoad();                 // $DD57 → $C248 → $C235
            TileMap = new LevelTileMap(_worldDecoder, Ppu, w);
            Player.TileMap = TileMap;
            _gridByte = null; _flagByte = null;             // ($32)/($34) now point at this world
            RenderOff();                                    // $DE24 → $C14E: blank while it streams
            Player.ApplyWorldPrelude(w);
            if (Bank7 != null)
                Player.CardReleaseChr = RoomDecoder.DescByte(Bank7, 0xD589, _worldId);  // $D562 ($1A on $9600 release)
            WireWorldFeed();
            if (_data != null && _data.Levels != null && _worldId < _data.Levels.Length)
                CurrentLevel = _data.Levels[_worldId];
            // $DE93-$DEA1 runs only after the $DE91 staging exit, so it belongs to WorldSpawn.

            var t = Sections;
            if (t != null)
            {
                var bg = new byte[16];
                var sp = new byte[16];
                t.ReadBgPaletteByIndex(w.BgPalIdx, bg);     // $DDB8 → $C2F9
                t.ReadSpritePaletteByIndex(w.SpPalIdx, sp); // $DDC2 → $C319
                SetPaletteSource(bg, 0);
                SetPaletteSource(sp, 16);
            }
            Ppu.Chr0Bank = (byte)(w.ChrReq0 & 0x7F);        // $FF21 applies the $1A request
            Ppu.Chr1Bank = (byte)(w.ChrReq1 & 0x7F);        // $FF3B applies the $1B request
            Player.ChrLatch0 = 0; Player.ChrLatch1 = 0;
            // $DDA0-$DDAA: the ring event is only re-queued when $D9 was still non-zero.
            if (Player.SectionRingEvent != 0) PlaySfx(Player.SectionRingEvent);
        }

        /// <summary>$DE93-$DEA1 tail plus $DDCE-$DDFE + $DE00: the placement anchor, the CHR
        /// switch, the slot-0 seed, the entry pose and rendering back on. Only the state-$09
        /// release makes the player visible, one $FF06-yield after the prelude
        /// (gold_m1_longroute: prelude f814, spawn f824).</summary>
        private void WorldSpawn()
        {
            AnchorAndSpawnPlacements();                     // $DE93-$DEA1, after the $DE91 exit
            Player.PalStep3E = 0; Player.PalIndex3F = 0;    // $DDCE-$DDD2
            Player.ApplyWorldSpawn(_world);
            RenderOn();                                     // $DE00 JSR $C158
        }
        private RoomDecoder.World _world;

        /// <summary>$E388 section/door descriptor tables, read from bank 7.</summary>
        public SectionTable Sections
        {
            get { if (_sections == null && Bank7 != null) _sections = new SectionTable(Bank7); return _sections; }
        }

        /// <summary>$E478 engine fork: ids {4,6,8,$A,$C,$E} keep running the PRG=4
        /// internal engine ($9800); the rest return through the PRG=5 one ($8000).</summary>
        public bool SectionUsesInternalEngine { get; private set; }

        /// <summary>0 = not loading, 1 = $DE24 staging loop, 2 = the $E3ED yield frame,
        /// 3 = the $C270-$C2BF palette fade service.</summary>
        private int _sectionStage;
        /// <summary>Death-reload staging ($CB01 → $D41B → $C248/$C276 fade-out, then $DD57).
        /// 0 = idle, 1 = $C276 fade-out ($0F=$10 step $10, exit at $0F==$50), 2 = $DE24 render-off
        /// prelude, 3 = the $DE86-$DE91 streaming window (one $StageSectionPass batch per frame),
        /// 6 = the $DDCB yield's tail, 4 = the $C270 fade-in, 5 = the $D450 $FF06 yield that spends
        /// the fade-exit frame.</summary>
        private int _reloadStage;
        /// <summary>Frames left in the current <see cref="_reloadStage"/> hold (so far only the
        /// $C24B/$C251 tail after the fade-out; the streaming window is driven by the $DE91
        /// cursor exit, not a count).</summary>
        private int _reloadHold;
        private bool _chrPending;
        private byte _fadeLevel;    // $0F fade level ($30 → $20 → $10 → $00)
        private byte _fadeDelta;    // $0D per-step delta ($F0 = fade in from black)
        private byte _fadeFrame;    // frames served inside the $C270 loop ($0E = 4 per step)
        /// <summary>True while the fade was entered through $C276 ($0D=$10): $C2B7 tests
        /// <code>CMP #$50</code> and RTS-es there, whereas the $C270 fade-in only leaves through
        /// $C2BB <code>BPL</code> once $0F goes negative.</summary>
        private bool _fadeExitAt50;
        private readonly byte[] _paletteSrc = new byte[32]; // $0320 un-faded palette
        private readonly byte[] _palMirror = new byte[32];  // $0300 faded palette mirror
        private byte _palDirty;     // $18: set by $C2A2 STY $18, consumed by the NMI $C059 upload
        public bool SectionLoading => _sectionStage != 0;

        /// <summary>$C2F9/$C319: a palette table load writes BOTH $0300 and $0320 and does not
        /// flag $18, so the PPU keeps showing whatever the last fade step uploaded.</summary>
        private void SetPaletteSource(byte[] src, int offset)
        {
            for (int i = 0; i < 16; i++)
            {
                _paletteSrc[offset + i] = src[i];
                _palMirror[offset + i] = src[i];
            }
        }

        /// <summary>$C059-$C077: the NMI uploads $0300→$3F00 only when $18 is non-zero, which is
        /// why a fade step's ramp is visible on the NEXT frame (gold_m1_longroute: $0F=$10 is
        /// stored on f796 while $3F00 still holds the un-faded table).</summary>
        private void NmiPaletteUpload()
        {
            if (_palDirty == 0) return;
            for (int i = 0; i < 32; i++) Ppu.PaletteRam[i] = _palMirror[i];
            _palDirty = 0;
        }

        /// <summary>$C14E: <code>INC $F0 / LDA #$00 / STA $FE / STA $2001</code>. The port keeps
        /// the mask only in the PPU register, so the black window comes from
        /// <see cref="Ppu.Render"/> filling with $3F00 while bit3-4 of $2001 are clear.</summary>
        private void RenderOff() => Ppu.WriteRegister(0x2001, 0x00);

        /// <summary>$C158: <code>DEC $F0 / LDA #$1E / STA $FE / STA $2001</code>.</summary>
        private void RenderOn() => Ppu.WriteRegister(0x2001, 0x1E);

        /// <summary>Host/harness boot palette: the ROM keeps THREE palette states — the un-faded
        /// source $0320, the faded working copy $0300 and the PPU mirror $3F00 that only the NMI
        /// $C059 uploads — and they are legitimately out of step (a $C2F9 table load rewrites
        /// $0300/$0320 but leaves $3F00 until the next fade step or $EFBE patch flags $18). So the
        /// seed takes $0300/$0320 from the captured RAM and $3F00 from the captured PPU window
        /// instead of copying one onto the others.</summary>
        public void SeedPaletteTables(byte[] ram2048, byte[] ppu32)
        {
            for (int i = 0; i < 32; i++)
            {
                _palMirror[i] = ram2048[0x300 + i];
                _paletteSrc[i] = ram2048[0x320 + i];
                Ppu.PaletteRam[i] = ppu32[i];
            }
            // $18 is part of the palette state: a capture seeded while it is $FF still owes the
            // NMI $C059 upload, which is why gold_m1_longroute f0 shows $3F00[13]=$05 but f1
            // already shows the $0300 value $0F. Without it the port keeps displaying f0's
            // mirror for the whole run.
            _palDirty = ram2048[0x18];
        }

        /// <summary>Golden-comparison probe: the three palette states live independently in the
        /// ROM, so a $3F00 divergence has exactly two causes — wrong $0300 content or wrong $18
        /// upload timing. Exporting both lets a harness tell them apart instead of guessing.</summary>
        public void GetPaletteModel(byte[] into0300, byte[] into0320)
        {
            System.Array.Copy(_palMirror, into0300, 32);
            System.Array.Copy(_paletteSrc, into0320, 32);
        }

        /// <summary>$18, the pending NMI $C059 upload flag.</summary>
        public byte PaletteUploadFlag => _palDirty;

        /// <summary>$E388 with X = section id ($CA6E calls it from the $5B door
        /// request, $D461 for the respawn after death). The loader does not return
        /// for ~11 frames, so only the descriptor tables are written here; <see cref="Step"/>
        /// finishes the staging and the $E3F0-$E406 tail.</summary>
        public void LoadSection(byte sectionId)
        {
            var t = Sections;
            if (t == null) return;
            Objects?.ClearForSectionLoad();               // $C248 → $C235
            Player.ApplySectionLoad(t, sectionId);        // $E38F-$E3D3 + $DE24
            if (TileMap != null)
            {
                TileMap.ApplyTilesetBank(Player.TilesetBank);
                TileMap.LoadRow(Player.ScrollYRow);       // $E698 row rebuild
                TileMap.StreamColumns(Player.ScrollX);
            }
            var bg = new byte[16];
            var sp = new byte[16];
            t.ReadBgPalette(sectionId, bg);               // $C2F9 → $0300/$0320
            t.ReadSpritePalette(sectionId, sp);           // $C319 → $0310/$0330
            SetPaletteSource(bg, 0);
            SetPaletteSource(sp, 16);
            // $E454-$E476: checkpoint ids $02,$04,$06,$08,$0A,$0C,$0E set the $3B intro
            // hold, which redirects the state-$12 exit to $50=$0F instead of $50=$00.
            Player.IntroHoldFlag =
                (Player.CheckpointId >= 0x02 && Player.CheckpointId <= 0x0E
                 && (Player.CheckpointId & 1) == 0) ? (byte)0x78 : (byte)0x00;
            SectionUsesInternalEngine = SectionTable.UsesInternalEngine(sectionId);
            _sectionStage = 1;
        }

        // ==================================================================
        // M7 flow hooks: the city map owns the screen between missions
        // ==================================================================

        /// <summary>City-map VRAM exported from the ROM ($2000-$23FF, map_nametable.bytes)
        /// and its 32-entry palette ($map_palette.bin, verified against a live PPU read).</summary>
        public byte[] MapNametable { get; set; }
        public byte[] MapPalette { get; set; }
        /// <summary>PRG bank 2 mapped at $8000-$BFFF: the GAME OVER card's nametable records
        /// and palette (see WorldState.GameOver* for the addresses and their evidence).</summary>
        public byte[] Bank2 { get; set; }

        /// <summary>The bank-2 dialog coroutine ($AA00) — streams $0E/$0F are the intro lines
        /// Darkwing speaks over the night scene ($C606/$C617). Built on first use because the
        /// host wires <see cref="Bank2"/> after construction; null while bank 2 is missing.</summary>
        public TextEngine Text
        {
            get { if (Bank2 != null && _text == null) _text = new TextEngine(Ppu, Bank2); return _text; }
        }
        private TextEngine _text;

        /// <summary>CHR banks the map screen selects. $C8B5 drives them through the same
        /// $1A/$1B request bytes as LoadWorld, so these are set from the bank-7 descriptor
        /// for world 31 rather than hardcoded — see <see cref="LoadMapScreen"/>.</summary>
        public byte MapChrBank0 { get; set; }
        public byte MapChrBank1 { get; set; }

        /// <summary>False while the title/map/briefing phase renders instead of the world
        /// engine. <see cref="Step"/> still advances the frame counters and re-renders so
        /// the host keeps presenting the nametable it was given.</summary>
        public bool WorldRunning = true;

        /// <summary>Raised by $B315 (<see cref="BossBehaviors.BossDefeat"/>) when the world's
        /// boss dies — the end-of-mission event that <see cref="GameFlow"/> watches.</summary>
        public bool MissionCleared;

        /// <summary>Loads the city-map screen (the $C8B5 world-31 path: nametable + palette +
        /// CHR from the descriptor, rendering re-enabled, scroll zeroed, world objects cleared).
        /// The HELP-bubble/cursor sprites are composed by the bank-7 map task, still unported
        /// (GAPS G27), so this paints the background layer only.</summary>
        public void LoadMapScreen()
        {
            if (MapNametable != null)
                System.Array.Copy(MapNametable, 0, Ppu.NametableRam, 0,
                    System.Math.Min(MapNametable.Length, 0x800));
            if (MapPalette != null)
                for (int i = 0; i < 32; i++) Ppu.PaletteRam[i] = MapPalette[i];
            Ppu.Chr0Bank = MapChrBank0;
            Ppu.Chr1Bank = MapChrBank1;
            Ppu.ScrollX = 0; Ppu.ScrollY = 0;
            // The map's own sprites (HELP bubbles + cursor, composed by the bank-7 map task)
            // are not ported yet, so park OAM at the NES hidden line instead of leaving the
            // last world's objects on screen.
            for (int i = 0; i < 256; i += 4) Ppu.Oam[i] = 0xFF;
            Ppu.WriteRegister(0x2001, 0x1E);    // $C158: rendering back on for the map screen
            Objects?.ClearForSectionLoad();
            MissionCleared = false;
            WorldRunning = false;
        }

        /// <summary>
        /// $D46C-$D4A9, the GAME OVER card the ROM draws after $D41D sees a non-zero
        /// death counter: $C838 clears object RAM, $C1BA ($2000=&amp;$FE) clears the nametable
        /// and attributes, bank 2 task 3 streams its screen records and $8D00 stages the
        /// palette. Verified against gold_gameover/frame_000300.png + vram_frames.bin f300.
        /// </summary>
        public void LoadGameOverScreen()
        {
            Ppu.WriteRegister(0x2000, 0x90);   // $C1CA: NMI off, BG pattern table $1000
            for (int i = 0; i < 0x400; i++) Ppu.NametableRam[i] = 0;   // $C1BA tiles + attrs
            Ppu.Chr0Bank = WorldState.GameOverChrBank0;
            Ppu.Chr1Bank = WorldState.GameOverChrBank1;
            for (int i = 0; i < 16; i++)
                Ppu.PaletteRam[i] = Bank2[WorldState.GameOverPalette - 0x8000 + i];
            int p = WorldState.GameOverRecords - 0x8000;
            while ((Bank2[p] & 0x80) == 0)
            {
                int addr = ((Bank2[p] << 8) | Bank2[p + 1]) & 0x0FFF;
                int len = Bank2[p + 2];
                for (int i = 0; i < len; i++) Ppu.NametableRam[addr + i] = Bank2[p + 3 + i];
                p += 3 + len;
            }
            for (int i = 0; i < 256; i += 4) Ppu.Oam[i] = 0xFF;   // the card has no sprites
            Ppu.ScrollX = 0; Ppu.ScrollY = 0;
            Ppu.WriteRegister(0x2001, 0x1E);   // ppu_writes.csv f300: $2001=30 every frame
            Objects?.ClearForSectionLoad();    // $D47C JSR $C838
            MissionCleared = false;
            WorldRunning = false;
        }

        /// <summary>Sound triggers mirroring the original's $F84E/$F850 enqueue sites.</summary>
        public const int SfxJump = 0x21;        public const int SfxLand = 0x10;
        public const int SfxHurt = 0x15;
        public const int SfxSelect = 0x1B;
        public const int SfxPause = 0x1C;
        public const int SfxDeath = 0x25;
        public const int SfxTextBlip = 0x3F;

        public void PlaySfx(int id) => Audio?.PlaySfx(id);
        public void PlayMusic(int id) => Audio?.PlayMusic(id);

        /// <summary>The generic $F84E enqueue: the ROM passes the raw id through, so it covers
        /// sound-table entries ($10-$3F), music-table ids ($00-$0F) and the special engine
        /// tasks ($F0-$F7) — e.g. $D4BE's #$F0 stop and $D4A7's #$0C.</summary>
        public void PlaySound(int id) => Audio?.Task(id);

        /// <summary>Load a room's nametable directly (test fixture path; the level
        /// loader normally streams it via the VRAM queue).</summary>
        public void LoadNametableRoom(byte[] nametableTilesAndAttrs)
        {
            System.Array.Copy(nametableTilesAndAttrs, Ppu.NametableRam, nametableTilesAndAttrs.Length);
        }

        /// <summary>Reset to power-on state (equivalent of RESET vector path).</summary>
        public void Reset()
        {
            Frame = 0;
            FrameParity = 0;
        }

        private byte _prevHeld;
        private System.Func<int, byte> _gridByte, _flagByte;   // cached ($32)/($34) lookups for $DA70
        public bool Paused; // Start toggles in gameplay ($CA33 flow: SFX $1C, freeze, status screen)

        /// <summary>Advance the deterministic simulation by exactly one frame.</summary>
        public void Step(in NesButtons pad1, in NesButtons pad2 = default)
        {
            NmiPaletteUpload();            // $C059: $18-flagged $0300 → $3F00, every frame
            // $DE03 JSR $FF06 + $DE06 JMP $C254 → $C270: the world loader stays in its
            // palette fade for 17 NMIs, so neither the input read nor $CAFC/$CB41 runs
            // (gold_w00: $50=$09 from f328, first dispatch tick f345, $95 parked at 01).
            // The parity still has to catch up once before the first dispatched frame:
            // the ROM ticked $8000 on the fade's first NMI (gold_w03 $95 $00→$01 on f329,
            // then frozen f330-f344, $02 on f345). Nothing observable depends on the value
            // during the residency (no engine, so no anim tick), only on its phase after
            // the release, so the catch-up tick is applied on the last frozen frame.
            if (Player.EntryFreezeFrames > 0)
            {
                if (--Player.EntryFreezeFrames == 0) FrameParity++;
                return;
            }
            // $95 is NOT an NMI counter: its two writers are the frame engines themselves
            // (bank 5 $8000 for the world, bank 4 $9800 for the title/map phase), so it only
            // advances on a frame that actually reaches one of them — see RunWorldServices.
            // gold_m1_longroute proves it: $95 sits at $94 from f796 through the death reload
            // and the streaming window (f800-f836), and gold_w00 parks it at $01 through the
            // entry fade. Because bank5 $80EB gates the player's animation tick on $95&$04
            // while $7C is non-zero, a counter that advances during those frozen windows puts
            // the whole anim clock one frame out of phase with the ROM.
            // $CA6E called $E388 on a previous frame: the frame is spent inside the
            // loader — the staging loop yields through $E7A8/$C115 and the fade through
            // $C2A7/$FF06 — so no input, player or anim update happens until it returns.
            if (_sectionStage != 0)
            {
                // The NMI of this frame consumes any latch set on a previous frame; do it
                // before the stage advances so the tail frame keeps $1A/$1B = $80/$90.
                ConsumeChrLatch();
                if (!AdvanceSectionStage())
                {
                    Frame++;
                    return;
                }
                // $E388 RTS-ed into $CA75: the player dispatcher ($CAFC → $CB41) already
                // ran its pass of this frame, only the world services are left.
                RunWorldServices();
                Frame++;
                return;
            }
            // $D41B → $C248/$C276 → $DD57: the death reload is resident across the fade-out,
            // the black streaming window and the fade-in, so these frames take no input and
            // run no dispatcher either ($FF06 ProcessYield only).
            if (_reloadStage != 0)
            {
                ConsumeChrLatch();
                bool returned = AdvanceReloadStage();
                if (returned) RunWorldServices();
                Frame++;
                return;
            }
            byte held = pad1.ToByte();
            if (!WorldRunning)
            {
                // The title/map/briefing phase is bank 7's own loop ($C792/$C9AC): no player
                // dispatcher, no bank-6 task — just the NMI frame counter and the nametable
                // the phase handler wrote into PPU memory.
                FrameParity++;
                Ppu.Render(Framebuffer);
                Frame++;
                return;
            }
            byte pressed = (byte)(held & ~_prevHeld); // edge = held & ~previous (original $C193 semantics)
            _prevHeld = held;
            if (Paused)
            {
                if ((pressed & 0x10) != 0) Paused = false; // Start resumes
                return; // world frozen during pause
            }
            if ((pressed & 0x10) != 0 && Player.LevelActive >= 0)
            {
                Paused = true; // TODO(M7): SFX $1C + status screen via VRAM queue
                return;
            }
            // $CA40 JSR $CAFC: the state table $CB41 runs only while slot 0's $04D0 bit 7 is
            // set. Once the death payload clears it ($807A from the cull, $D42F from the
            // resolve), the frame belongs to the death service $CB01 → resolve $D41B, whose
            // tail is JMP $FF06 — it never RTS-es back to $CA43, so the scroll service,
            // spawner, door check and animation engine are all skipped for that frame.
            if ((Objects.Slots[0].StateLow & 0x80) != 0)
            {
                Player.Step(held, pressed);
                // $CE39 is called from inside the player's state machine and writes the
                // projectile slot there, so the bank-6 dispatch finds it live in the SAME
                // frame: gold_b_attack f30 has $04D0 = $81 and $0540 = $04 (the $82F0 init
                // ran) plus X = $3B + one 4 px step. Committing after RunWorldServices
                // would put every bullet one frame behind.
                if (Player.HasPendingFire)
                {
                    Player.HasPendingFire = false;
                    Enemies?.SpawnWeapon(Player.PendingFire);
                }
            }
            else
            {
                // $DD57 does not return inline: the death service drains $05E6 once, tail-calls
                // $D41B → $DD57, and the loader then stays resident through $FF06 yields until
                // its fade-in RTS-es back into $D43A.
                Player.DeathService();                      // $CB01-$CB3E, tail-calls $D41B
                if (Player.WorldReloadRequest && _worldDecoder != null)
                {
                    // $D437 JSR $DD57 is reached through $CB01's JSR $C248, whose first action is
                    // the $C276 fade-out ($0F=$10, $0D=$10, exit at $0F==$50). gold_m1_longroute:
                    // resolve on f795, $0F=$10 stored on f796, $50 on f812.
                    Player.WorldReloadRequest = false;
                    _reloadStage = 1;
                    _fadeLevel = 0x00; _fadeDelta = 0x10; _fadeFrame = 0; _fadeExitAt50 = true;
                    StepFade();     // $C276 STA $0F falls straight into $C285: the first ramp is
                                    // applied in the frame that enters the fade (f796 reads $10).
                }
                else if (Player.SectionReloadRequest != 0xFF)
                {
                    byte id = Player.SectionReloadRequest;  // $D466 JSR $E388
                    Player.SectionReloadRequest = 0xFF;
                    LoadSection(id);
                }
                Frame++;
                return;
            }
            // Hook/peg column service ($DAFD) is the fallthrough of the scroll service
            // ($CA59 → $DA70), so both run AFTER the player state machine: the grab frame
            // leaves $0460/$0510 untouched (gold_hook_ride2 f1) and the ride's row wrap only
            // releases $50 on the NEXT frame (f80 wrap → f81 release).
            // $9630/$FEFF: the intro card's release wait spins pure $FF06 yields inside
            // $CAFC, so nothing past $CA43 runs — scroll service, spawner, bank-6/1 tasks and
            // the bank-5 frame engine included. gold_m1_longroute parks $95 at $02 for exactly
            // the f951..f1010 window (only the audio RAM moves), and gold_w00 parks it at $01
            // over f453..f512; a port that ticks the engine there puts the whole anim clock
            // 60 frames out of phase for the rest of the run.
            if (Player.YieldOnlyFrame)
            {
                ConsumeChrLatch();
                Frame++;
                return;
            }
            if (TileMap != null)
            {
                if (_gridByte == null) { _gridByte = TileMap.GridByte; _flagByte = TileMap.FlagByte; }
                // $DA70-$DC6D owns $FC/$FD, $22, $23, $2B and $2F, and reaches the
                // column-stream driver ($E63C) through $DC14/$DC67 on scrolled frames only.
                Player.HorizontalScroll(_gridByte, _flagByte);
                Player.CellGridByte = TileMap.GridByte(Player.CellIndex);   // ($32),$22
                Player.CellScriptByte = TileMap.FlagByte(Player.CellIndex); // ($34),$22
                Player.HookGrabCheck();
            }
            // $CA6E: LDX $5B / JSR $E388 — consume the door request raised by the
            // probe tail ($EBAA/$EBC8) in the same frame.
            if (Player.PendingEvent != 0) LoadSection(Player.PendingEvent);
            if (_sectionStage != 0)
            {
                AdvanceSectionStage();
                Frame++;
                return;
            }
            ConsumeChrLatch();
            RunWorldServices();
            Frame++;
        }

        private void ConsumeChrLatch()
        {
            if (!_chrPending) return;
            // NMI $FF21/$FF3B consumes the CHR request latches once rendering is on
            // again (gold_door_id1: $1A/$1B = $80/$90 through the tail frame, 0 after —
            // and rendering is already on during the fade frames).
            Player.ChrLatch0 = 0;
            Player.ChrLatch1 = 0;
            _chrPending = false;
        }

        /// <summary>
        /// The tail of the main loop, in ROM order ($CA59 scroll → $CA6B spawner → $CA6E door →
        /// $CA84 bank-6 behaviours → $CABB bank-5 frame engine → render). The bank-5 engine
        /// ($8000-$81F7) composes the $0200 sprite shadow, which the NMI of the NEXT frame
        /// publishes: the captured OAM[f] therefore holds the object records of the walk in frame
        /// f-1 while its HUD placement follows RAM[f].$95. PublishToOam + the HUD below model that
        /// pairing (Temp/oam_lag.js: byte-exact for 622/622 and 631/631 object records over
        /// gold_contact/gold_jump; every other pairing scores 0).
        /// </summary>
        private void RunWorldServices()
        {
            if (Oam == null) return;
            var s0 = Objects.Slots[0];
            // $CAFC gates on $04D0 bit 7 and the loader re-arms it, so the slot must mirror
            // the real flag byte. Force-arming it every frame kept the player composited
            // through the death freeze (gold_w05 f656 has $04D0 = $00).
            s0.Active = (s0.StateLow & 0x80) != 0;
            s0.Flags = (byte)(0x90 | (Player.FacingRight != 0 ? 0x40 : 0) | 0x10);
            s0.X = Player.X;
            s0.Y = Player.Y;
            // Spawner + cull ($8090/$802F): placements activate as their cell scrolls in.
            TileMap?.StreamColumns(Player.ScrollX);
            if (Objects?.Placements != null)
            {
                // Spawner ($8090); culling happens per-slot in OamDispatcher
                // ($802F semantics: ±16 screens, onscreen bit at high==0).
                Objects.SpawnFromPlacements(Player.ScrollX,
                    (ushort)((Player.ScrollYRow << 8) | Player.ScrollY), Player.ScrollMode);
            }
            // $CA75/$CA79: LDA $66 / BMI $CA7C — the zone service is skipped while a door
            // has flagged $66 negative, but $CA7C JSR $EFBE is the FALL-THROUGH of that same
            // block, so the palette-patch service runs on every world frame either way.
            // Exception: both are the tail of $CAFC, and while the $9605 intro-card loop owns
            // the process it never RTS-es back to $CA43 — the engine tick still happens (each
            // iteration ends inside $F89A), only the tail is lost. That is why gold_m1_longroute
            // parks $3D/$3E/$3F for the f872..f1010 phase while $95 keeps counting, and why m2
            // and m7 — the only captures with no such phase — are the only palette MATCHes.
            if (!Player.IntroCardPhase)
            {
                if ((sbyte)Player.PendingMissionFlag >= 0) RunZoneService();
                RunPaletteScript();
            }
            Enemies?.RunFrame();
            // $8000 is the engine's own entry: INC $95 is its first instruction, so the value
            // the blink/iteration-order tests read is the one this frame's pass produced.
            FrameParity++;                                     // $8000-$8002
            // NMI DMA: the buffer this frame displays is the shadow of the previous walk.
            Oam.PublishToOam();
            // The HUD is object slot 1 ($C850/$C887), so the walk composes its 8 records at
            // whatever cursor position the parity order reaches slot 1 in — no separate writer
            // and no hard-coded entry (GAPS G44.1 closed).
            Hud.Refresh(Player);
            Oam.RunFrame(FrameParity);
            Ppu.Render(Framebuffer);
        }

        // ==================================================================
        // G25: the per-mission zone service ($CA79 JSR $EDF1, bank 7 $EDF1-$EFBD)
        // ==================================================================

        /// <summary>$EDF1: <code>LDA $31 / CMP #$08 / BCS $EE15</code> then the two vector tables
        /// $EE07 (low) / $EE0E (high) indexed by <code>$31 &amp; $0F</code> through
        /// <code>JMP ($0000)</code>. Seven handlers exist (missions 1-7); ids 8 and up are steered
        /// straight into the $EE15 handler. Every handler reads the current cell id
        /// <code>($32),Y</code> with <code>Y = $22</code>, the scroll row <code>$FA</code> and the
        /// scroll low byte <code>$FC</code>, and ends either in a plain RTS or through
        /// <code>$EE5C: LDA #$FF / STA $18</code> (flag the palette table for the NMI upload).
        /// Two handlers also relocate the spawner entry cursors <code>$70/$71</code>, which is how
        /// a zone jump re-enters the placement list mid-level.</summary>
        private void RunZoneService()
        {
            if (Sections == null || TileMap == null || Objects == null) return;
            byte cell = Player.CellGridByte;          // ($32),$22
            byte yLo = (byte)(Player.ScrollY & 0xFF);  // $FA
            byte xLo = (byte)(Player.ScrollX & 0xFF);  // $FC
            switch (_worldId < 8 ? _worldId : (byte)0)
            {
                case 0: ZoneEE15(cell, yLo, xLo); break;
                case 1: ZoneEE2D(cell, yLo, xLo); break;
                case 2: ZoneEE71(cell, yLo, xLo); break;
                case 3: ZoneEEB8(cell, yLo, xLo); break;
                case 4: ZoneEEEA(cell, yLo, xLo); break;
                case 5: ZoneEF5C(cell, yLo, xLo); break;
                default: ZoneEF76(cell, yLo, xLo); break;
            }
        }

        /// <summary>$CA7C/$EFBE: the palette-patch player driven by the section byte $3D.
        /// Bit7 arms it (BPL $F007 otherwise); $3E counts frames until the script's period
        /// byte, and on that frame $3F's record group is applied to the <em>faded</em> $0300
        /// mirror only (never $0320), then the $FF terminator is stored into $18 so the next
        /// NMI $C059 uploads it. <code>INC $3E</code>/<code>INC $3F</code> are memory-only
        /// opcodes, so the CMP that follows still sees the pre-increment byte — which is what
        /// makes the record index <code>base + $3F + 2</code> and the wrap fire on
        /// <code>$3F == steps</code>.</summary>
        private void RunPaletteScript()
        {
            byte s = Player.SectionFlag3D;                    // $EFBE LDA $3D
            if ((sbyte)s >= 0 || Sections == null) return;    // $EFBE BPL $F007
            int baseIdx = s & 0x7F;                           // $EFC2-$EFC6
            byte step = Player.PalStep3E;                     // $EFCB LDA $3E
            if (step != Sections.PaletteScriptPeriod(baseIdx)) // $EFCD-$EFD2
            {
                Player.PalStep3E = (byte)(step + 1);          // $EFCD INC $3E
                return;
            }
            Player.PalStep3E = 0;                             // $EFD6 STX $3E
            byte idx = Player.PalIndex3F;                     // $EFD8 LDA $3F
            Player.PalIndex3F = (byte)(idx + 1);              // $EFDA INC $3F
            if (idx == Sections.PaletteScriptSteps(baseIdx))
                Player.PalIndex3F = 0;                        // $EFDF-$EFE1 STX $3F
            _palDirty = (byte)Sections.ApplyPaletteScript(baseIdx, idx, _palMirror);  // $EFEC-$F005
        }

        /// <summary>$EE15 (mission 1): cells $0F..$13 switch to sprite palette 1 and CHR request
        /// $81; $14 and everything below $0F leave the tables alone.</summary>
        private void ZoneEE15(byte cell, byte yLo, byte xLo)
        {
            if (cell < 0x0F || cell == 0x14) return;      // $EE19/$EE1B, $EE1D/$EE1F
            ZoneSpritePalette(1);                         // $EE21-$EE23
            Player.ChrLatch0 = 0x81;                      // $EE26/$EE28 STA $1A
            ZoneFlagPalette();                            // $EE2A JMP $EE5C
        }

        /// <summary>$EE2D (mission 2): bg palettes 1/2/3 by cell band and the $FC &gt;= $E9 test,
        /// then CHR $82 + sprite palette 2 from cell $2A up ($34 excluded).</summary>
        private void ZoneEE2D(byte cell, byte yLo, byte xLo)
        {
            if (cell < 0x25)                              // $EE31/$EE33 BCC $EE35
            {
                ZoneBgPalette(1);                         // $EE35/$EE37
                ZoneFlagPalette();                        // $EE3A JMP $EE5C
                return;
            }
            if (cell >= 0x2A)                             // $EE55 BCS $EE61
            {
                if (cell == 0x34) return;                 // $EE61/$EE63 RTS without $18
                Player.ChrLatch0 = 0x82;                  // $EE65/$EE67
                ZoneSpritePalette(2);                     // $EE69/$EE6B
                ZoneFlagPalette();                        // $EE6E JMP $EE5C
                return;
            }
            // $EE3D/$EE51: cells $25..$27 re-enter the $FC test, $28/$29 fall into $EE57.
            if (cell < 0x28 && xLo < 0xE9)                // $EE41/$EE43/$EE45
            {
                ZoneBgPalette(2);                         // $EE47/$EE49
                ZoneFlagPalette();                        // $EE4C JMP $EE5C
                return;
            }
            ZoneBgPalette(3);                             // $EE57/$EE59
            ZoneFlagPalette();                            // $EE5C
        }

        /// <summary>$EE71 (mission 3): cell $47 at scroll X 0 sets $70/$71 = $15/$14 plus bg $11
        /// and CHR $83 + sprite 3; cells $43 and $4B past row $58 take the shared $EEA4 tail
        /// (CHR $85, bg 4, sprite 5, and for $4B also the $6D checkpoint $05).</summary>
        private void ZoneEE71(byte cell, byte yLo, byte xLo)
        {
            if (cell == 0x43)                             // $EE75/$EE77 → $EEB1
            {
                if (yLo >= 0x58) ZoneEEA4();               // $EEB3/$EEB5
                return;                                   // $EEB7 RTS
            }
            if (cell == 0x47)
            {
                if (xLo != 0) return;                     // $EE7D/$EE7F BNE $EE60
                Objects.CursorFwd = 0x15; Objects.CursorRev = 0x14;    // $EE81-$EE87 STA $70/$71
                ZoneBgPalette(0x11);                      // $EE89/$EE8B
                Player.ChrLatch0 = 0x83;                  // $EE8E/$EE90
                ZoneSpritePalette(3);                     // $EE92/$EE94 BNE $EE6B
                ZoneFlagPalette();                        // $EE6E JMP $EE5C
                return;
            }
            if (cell != 0x4B) return;                     // $EE96/$EE98
            if (yLo < 0x58) return;                       // $EE9A-$EE9E
            Player.CheckpointId = 0x05;                   // $EEA0/$EEA2 STA $6D
            ZoneEEA4();                                   // $EEA4 fallthrough
        }

        /// <summary>$EEA4-$EEB0: CHR request $85, bg palette 4, sprite palette 5, then $EE5C.
        /// Reached from both the $43 and the $4B branch of the mission-3 handler.</summary>
        private void ZoneEEA4()
        {
            Player.ChrLatch0 = 0x85;                      // $EEA4/$EEA6
            ZoneBgPalette(4);                             // $EEA8/$EEAA
            ZoneSpritePalette(5);                         // $EEAD/$EAAF → $EE6B
            ZoneFlagPalette();                            // $EE6E JMP $EE5C
        }

        /// <summary>$EEB8 (mission 4): bg 5 below cell $69, bg 6 for $69/$6A, and from $6B up only
        /// past row $58 the sprite palette 2 + CHR $82 + checkpoint $6D = $07.</summary>
        private void ZoneEEB8(byte cell, byte yLo, byte xLo)
        {
            if (cell < 0x69)                              // $EEBC/$EEBE
            {
                ZoneBgPalette(5);                         // $EEC0/$EEC2
                ZoneFlagPalette();                        // $EEC5 JMP $EE5C
                return;
            }
            if (cell < 0x6B)                              // $EEC8/$EECA
            {
                ZoneBgPalette(6);                         // $EECC/$EECE
                ZoneFlagPalette();                        // $EED1 JMP $EE5C
                return;
            }
            if (yLo < 0x58) return;                       // $EED4-$EED8 RTS
            Player.CheckpointId = 0x07;                   // $EEDA/$EEDC STA $6D
            ZoneSpritePalette(2);                         // $EEDE/$EEE0
            Player.ChrLatch0 = 0x82;                      // $EEE3/$EEE5
            ZoneFlagPalette();                            // $EEE7 JMP $EE5C
        }

        /// <summary>$EF3F: the cell-$8A zone patch shared by the mission-5 and mission-6
        /// handlers — three BG palette entries at $030D/$030E/$030F, only at $FC == $9C.</summary>
        private bool ZoneEF3F(byte cell, byte xLo)
        {
            if (cell != 0x8A) return false;               // $EF3F/$EF41
            if (xLo != 0x9C) return false;                // $EF43/$EF45/$EF47
            ZoneBgPaletteOverride(0x30, 0x2C, 0x21);      // $EF49-$EF56
            ZoneFlagPalette();                            // $EF58 JMP $EE5C
            return true;
        }

        /// <summary>$EEEA (mission 5): bg 7 below cell $88, the $8F/$93/$94/$8A ladder above it.</summary>
        private void ZoneEEEA(byte cell, byte yLo, byte xLo)
        {
            if (cell < 0x88)                              // $EEEE/$EEF0
            {
                ZoneBgPalette(7);                         // $EEF2/$EEF4 → $EECE JSR $C2F9
                ZoneFlagPalette();                        // $EED1 JMP $EE5C
                return;
            }
            if (cell == 0x8F && yLo >= 0x58)              // $EEF6-$EEFE
            {
                Player.ChrLatch0 = 0x80;                  // $EF00/$EF02
                ZoneBgPaletteOverride(0x30, 0x2C, 0x21);  // $EF04-$EF12
                ZoneSpritePalette(0);                     // $EF13/$EF15
                ZoneFlagPalette();                        // $EF18 JMP $EE5C
                return;
            }
            if (cell == 0x93 && xLo == 0)                 // $EF1B-$EF21
            {
                Objects.CursorFwd = 0x43; Objects.CursorRev = 0x42;    // $EF23-$EF29 STA $70/$71
                Player.ChrLatch0 = 0x87;                  // $EF2B/$EF2D
                ZoneSpritePalette(7);                     // $EF2F/$EF31 → $EF15 JSR $C319
                ZoneFlagPalette();                        // $EF18 JMP $EE5C
                return;
            }
            if (cell == 0x94)                             // $EF33/$EF35
            {
                ZoneBgPalette(7);                         // $EF37/$EF39
                ZoneFlagPalette();                        // $EF3C JMP $EE5C
                return;
            }
            ZoneEF3F(cell, xLo);                          // $EF3F/$EF5B
        }

        /// <summary>$EF5C (mission 6): cell $A4 past row $58 gives CHR $88 + sprite palette 8,
        /// otherwise the handler shares the mission-5 $EF3F tail.</summary>
        private void ZoneEF5C(byte cell, byte yLo, byte xLo)
        {
            if (cell == 0xA4 && yLo >= 0x58)              // $EF60-$EF68
            {
                Player.ChrLatch0 = 0x88;                  // $EF6A/$EF6C
                ZoneSpritePalette(8);                     // $EF6E/$EF70
                ZoneFlagPalette();                        // $EF73 JMP $EE5C
                return;
            }
            ZoneEF3F(cell, xLo);                          // $EF3F
        }

        /// <summary>$EF76 (mission 7): cells $05/$04 gate on $FA &gt;= $5A and $FC &gt;= $A0,
        /// $0B..$97 give sprite palette 7 + CHR $87, cell $98 does nothing, and the
        /// $0B-and-above path also sets the $6D checkpoint $0D.</summary>
        private void ZoneEF76(byte cell, byte yLo, byte xLo)
        {
            if (cell == 0x05)                             // $EF7A/$EF7C → $EFA4
            {
                if (yLo >= 0x5A) ZoneEF87();              // $EFA6/$EFA8
                return;                                   // $EFAA RTS
            }
            if (cell == 0x04)                             // $EF7E/$EF80 → $EFAB
            {
                ZoneBgPalette(xLo >= 0xA0 ? (byte)0x17 : (byte)9);   // $EFAD-$EFBB
                ZoneFlagPalette();                        // $EFB6 JMP $EE5C
                return;
            }
            if (cell < 0x0B) return;                      // $EF82/$EF86 RTS
            if (cell == 0x98) return;                     // $EF93/$EF95 → $EFBD RTS
            Player.CheckpointId = 0x0D;                   // $EF97/$EF99 STA $6D
            ZoneSpritePalette(9);                         // $EF9B/$EF9D
            Player.ChrLatch0 = 0x89;                      // $EFA0/$EFA2 → $EF8E STA $1A
            ZoneFlagPalette();                            // $EF90 JMP $EE5C
        }

        /// <summary>$EF87-$EF90: sprite palette 7 then CHR request $87 (the mission-7 cell $05
        /// branch at $FA &gt;= $5A).</summary>
        private void ZoneEF87()
        {
            ZoneSpritePalette(7);                         // $EF87/$EF89
            Player.ChrLatch0 = 0x87;                      // $EF8C/$EF8E
            ZoneFlagPalette();                            // $EF90 JMP $EE5C
        }

        /// <summary>$C2F9: reload the 16 BG entries into $0300 AND $0320.</summary>
        private void ZoneBgPalette(byte index)
        {
            var bg = new byte[16];
            Sections.ReadBgPaletteByIndex(index, bg);
            SetPaletteSource(bg, 0);
        }

        /// <summary>$C319: reload the 16 sprite entries into $0310 AND $0330.</summary>
        private void ZoneSpritePalette(byte index)
        {
            var sp = new byte[16];
            Sections.ReadSpritePaletteByIndex(index, sp);
            SetPaletteSource(sp, 16);
        }

        /// <summary>$EF04-$EF12 / $EF49-$EF56: patch $030D-$030F (the faded mirror only — these
        /// writes deliberately skip $032D-$032F, so a later fade step overwrites them).</summary>
        private void ZoneBgPaletteOverride(byte a, byte b, byte c)
        {
            _palMirror[13] = a; _palMirror[14] = b; _palMirror[15] = c;
        }

        /// <summary>$EE5C: <code>LDA #$FF / STA $18</code> — the NMI $C059 upload flag.</summary>
        private void ZoneFlagPalette() => _palDirty = 0xFF;

        /// <summary>
        /// One frame inside $E388: a batch of $DE86/$E698 staging passes ($DE8A) until the
        /// cursor hits $0F, then the $E3ED yield frame that runs the $E3F0-$E44E tail, then
        /// the $C270-$C2BF palette fade the tail tail-calls through $E478/$C254.
        /// Returns true on the frame where $E388 finally returns into the main loop: the
        /// fade's last step does not yield, it RTS-es to $CA75, so that frame runs the
        /// object/enemy/OAM services but NOT the player dispatcher ($CAFC ran earlier).
        /// </summary>
        private bool AdvanceSectionStage()
        {
            switch (_sectionStage)
            {
                case 1:
                    if (Player.StageSectionPass())
                    {
                        _sectionStage = 2;
                        AnchorAndSpawnPlacements();        // $DE93-$DEA1
                    }
                    return false;
                case 2:
                    Player.FinishSectionLoad();          // $E3F0-$E406
                    _chrPending = true;                  // $E44E JSR $C158 re-enables rendering
                    // $C270 LDA #$30/$C272 LDX #$F0: $0F is decremented by $0D on the
                    // FIRST frame of each 4-yield step ($C2A4 loop + $C2B0), so prime one
                    // step above the first ramped level and let the cadence apply $30.
                    _fadeLevel = 0x40;
                    _fadeDelta = 0xF0;
                    _fadeFrame = 0;                      // $C283 LDY #$04 / STY $0E
                    _fadeExitAt50 = false;               // $C270 enters through $C2BB BPL
                    _sectionStage = 3;
                    return false;
                default:
                    if (!StepFade()) return false;
                    _sectionStage = 0;
                    return true;
            }
        }

        /// <summary>One frame of the shared $C285-$C2BF fade loop. Each step applies its ramp and
        /// then yields four NMIs ($C2A4-$C2AE with $0E=$04); on the frame the fourth yield returns,
        /// $C2B0-$C2B5 adds $0D to $0F and either RTS-es ($C2B7 CMP #$50 for a $C276 fade-out) or
        /// loops back to $C285 ($C2BB BPL, i.e. until $0F goes negative for a $C270 fade-in). The
        /// exit level is never applied. Returns true on the frame the fade RTS-es.</summary>
        private bool StepFade()
        {
            if ((_fadeFrame & 3) == 0)
            {
                _fadeLevel = (byte)(_fadeLevel + _fadeDelta);           // $C2B0-$C2B5
                if (_fadeExitAt50 ? _fadeLevel == 0x50                  // $C2B7/$C2B9
                                  : (_fadeLevel & 0x80) != 0)           // $C2BB BPL $C285
                    return true;
                ApplyFadeRamp();                                        // $C285-$C2A0
            }
            _fadeFrame++;
            return false;
        }

        /// <summary>
        /// One frame of the death reload: $CB01 → $C248/$C276 fade-out, the $C24B/$C24E/$C251 tail,
        /// $D437 JSR $DD57's render-off prelude plus its black streaming window, the $DDD6-$DDFE
        /// spawn with $DE00 render-on, and the $DE06/$C254/$C270 fade-in. Timeline measured on
        /// gold_m1_longroute: fade-out f796-f812, prelude f814, window f815-f823, spawn f824,
        /// fade-in f825-f841. Returns true on the frame $C2BF RTS-es back into $D43A, where only
        /// the world services run (the player dispatcher is still inside the loader).
        /// </summary>
        private bool AdvanceReloadStage()
        {
            switch (_reloadStage)
            {
                case 1:                                     // $C276 fade-out, $10/$20/$30/$40→$50
                    if (StepFade())
                    {
                        _reloadStage = 2;
                        _reloadHold = 1;                   // $C24B JSR $C235 + the $C251 $FF06 yield
                    }
                    return false;
                case 2:
                    if (_reloadHold > 0) { _reloadHold--; return false; }
                    WorldPrelude();                         // $DD5F-$DDCB, $DE24 render-off
                    _reloadStage = 3;
                    // G37: the $DE86-$DE91 loop streams one $E698 pass per iteration and only
                    // leaves when $25 hits $0F ($DE8F CMP $0F), so the window length is derived
                    // from the cursor distance the prelude seeds at $DE84 ($0F | $70) — a world
                    // load streams 144 passes and $StageSectionPass delivers 15 per frame.
                    return false;
                case 3:
                    if (!Player.StageSectionPass()) return false;
                    // $DDCB JSR $FF06: the frame that ends the stream at $0F is spent in that
                    // yield, so the spawn is the NEXT frame (gold f823 exit → f824 spawn).
                    _reloadStage = 6;
                    return false;
                case 6:
                    WorldSpawn();                           // $DE93-$DDFE + $DE00 render-on
                    // $DE03 JSR $FF06 spends the spawn frame, so $DE06/$C270 primes one frame
                    // later (f825 holds $0F=$30, f824 still the prelude's $88).
                    Player.EntryFreezeFrames = 0;           // stage 4 supplies the frozen frames
                    _fadeLevel = 0x40; _fadeDelta = 0xF0; _fadeFrame = 0; _fadeExitAt50 = false;
                    _reloadStage = 4;
                    return false;
                case 4:                                    // $C270 fade-in, $30/$20/$10/$00
                    // G43: the fade-in's first NMI is the first frame where bank5's $8000
                    // engine is resident again, and its very first instruction is `INC $95`
                    // (gold f825: $94→$95). The engine then stays gated off for the rest of
                    // the fade (f826-f841 frozen at $95), so exactly one tick lands here —
                    // missing it leaves the port's parity a constant golden-1 and shifts the
                    // $80EB anim-tick phase, which is what makes f1824 X/frac diverge.
                    if (_fadeFrame == 0) FrameParity++;
                    if (!StepFade()) return false;
                    // $C2BF RTS → $D43A/$D450 JMP $FF06: the frame that leaves the fade is
                    // spent inside the scheduler, so it serves no service at all
                    // (gold f841 changes only $0F; $430/$97 tick on f842, $0460/$0550 on f843).
                    _reloadStage = 5;
                    return false;
                default:                                   // the yield unwinds back into $CA43
                    _reloadStage = 0;
                    // Only the frame tail runs here; the player dispatcher takes the NEXT
                    // frame (gold f842 = anim tick + $97 clear, first fall tick f843).
                    return true;
            }
        }

        /// <summary>$DE93-$DEA1: once the $DE24 staging loop is done (and only for the
        /// mission worlds, $31 &lt; 7), bank 4 re-anchors the spawner cursors to the camera
        /// and runs one spawn pass, so the incoming screen's objects activate on the load
        /// frame rather than at the first scroll.</summary>
        private void AnchorAndSpawnPlacements()
        {
            if (Objects == null || Objects.Placements == null || Player.WorldId >= 7) return;
            Objects.AnchorPlacementCursors(Player.ScrollX, Player.ScrollYRow);
            Objects.SpawnFromPlacements(Player.ScrollX,
                (ushort)((Player.ScrollYRow << 8) | Player.ScrollY), Player.ScrollMode);
        }

        /// <summary>$C285-$C2A2: $0320 → $0300, then $0300[i] = $0300[i] − $0F with the
        /// <em>borrow</em> as the only clamp (<code>$C295 SEC / $C296 SBC $0F / $C298 BPL $C29C /
        /// $C29A LDA #$0F</code>): a result of $00..$0E is stored as-is, $0F replaces only a
        /// negative one. Clamping at &lt; $0F instead would darken the whole ramp one step early —
        /// gold_m1_longroute f796 keeps $0300[3]=$07 ($17−$10) and gold_m3_route f32 keeps
        /// $0300[2]=$00 ($10−$10). Then <code>STY $18</code> (Y=$FF after the loop's DEY) flags the
        /// table for the next NMI upload, which is why a fade step shows one frame later.</summary>
        private void ApplyFadeRamp()
        {
            for (int i = 0; i < 32; i++)
            {
                int v = _paletteSrc[i] - _fadeLevel;
                _palMirror[i] = (byte)(v < 0 ? 0x0F : v);
            }
            _palDirty = 0xFF;
        }

        /// <summary>Serialize all future-affecting state. Version-tagged.</summary>
        public byte[] SaveState()
        {
            using var ms = new System.IO.MemoryStream();
            using var w = new System.IO.BinaryWriter(ms);
            w.Write((int)3); // schema version
            w.Write(Frame);
            w.Write(FrameParity);
            w.Write(Player.HitLadder);
            w.Write(Player.InvulnFrames);
            w.Write(Player.GasEnergyHi);
            w.Write(Player.GasEnergyLo);
            w.Write(Player.ActiveGasEnergyHi);
            w.Write(Player.ActiveGasEnergyLo);
            w.Write(Player.WeaponActive);
            w.Write(Player.WeaponSelected);
            foreach (var s in Objects.Slots)
            {
                w.Write(s.Active);
                w.Write(s.Flags); w.Write(s.AnimNumber); w.Write(s.AnimFrame); w.Write(s.AnimTimer);
                w.Write(s.StateLow); w.Write(s.Behavior); w.Write(s.BehaviorParam);
                w.Write(s.HitFlash); w.Write(s.Health); w.Write(s.Direction); w.Write(s.PlacementIndex);
                w.Write(s.X); w.Write(s.Y);
                w.Write(s.XFrac); w.Write(s.YFrac);
                w.Write(s.XVelInt); w.Write(s.XVelFrac);
                w.Write((byte)s.YVelInt); w.Write(s.YVelFrac);
            }
            Enemies?.SaveState(w);
            return ms.ToArray();
        }

        public void LoadState(byte[] data)
        {
            using var ms = new System.IO.MemoryStream(data);
            using var r = new System.IO.BinaryReader(ms);
            int version = r.ReadInt32();
            if (version != 3) throw new System.InvalidOperationException($"State schema {version} unsupported");
            Frame = r.ReadInt64();
            FrameParity = r.ReadByte();
            Player.HitLadder = r.ReadByte();
            Player.InvulnFrames = r.ReadByte();
            Player.GasEnergyHi = r.ReadByte();
            Player.GasEnergyLo = r.ReadByte();
            Player.ActiveGasEnergyHi = r.ReadByte();
            Player.ActiveGasEnergyLo = r.ReadByte();
            Player.WeaponActive = r.ReadByte();
            Player.WeaponSelected = r.ReadByte();
            foreach (var s in Objects.Slots)
            {
                s.Active = r.ReadBoolean();
                s.Flags = r.ReadByte(); s.AnimNumber = r.ReadByte(); s.AnimFrame = r.ReadByte(); s.AnimTimer = r.ReadByte();
                s.StateLow = r.ReadByte(); s.Behavior = r.ReadByte(); s.BehaviorParam = r.ReadByte();
                s.HitFlash = r.ReadByte(); s.Health = r.ReadByte(); s.Direction = r.ReadByte(); s.PlacementIndex = r.ReadByte();
                s.X = r.ReadUInt16(); s.Y = r.ReadUInt16();
                s.XFrac = r.ReadByte(); s.YFrac = r.ReadByte();
                s.XVelInt = r.ReadByte(); s.XVelFrac = r.ReadByte();
                s.YVelInt = (sbyte)r.ReadByte(); s.YVelFrac = r.ReadByte();
            }
            Enemies?.LoadState(r);
        }
    }
}
