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
            Hud = new HudComposer(Ppu);
            Oam = data != null
                ? new OamDispatcher(_bank5 ?? System.Array.Empty<byte>(), Objects, Ppu, Player)
                : null;
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
            AnchorAndSpawnPlacements();                     // $DE93-$DEA1, tail of $DE24

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

        /// <summary>$DDCE-$DDFE + $DE00: the slot-0 seed, the entry pose and rendering
        /// back on. Only the state-$09 release makes the player visible, one
        /// $FF06-yield after the prelude (gold_m1_longroute: prelude f814, spawn f824).</summary>
        private void WorldSpawn()
        {
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
        /// prelude + the black streaming window, 3 = $DDD6-$DDFE spawn + $C158 render-on,
        /// 4 = the $C270 fade-in, 5 = the $D450 $FF06 yield that spends the fade-exit frame.</summary>
        private int _reloadStage;
        /// <summary>Frames left in the current <see cref="_reloadStage"/> hold (the $C248 tail and
        /// the black streaming window; see GAPS G37 for why the window is a measured constant).</summary>
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

        /// <summary>Host/harness boot palette: writes $0320, $0300 and $3F00 at once. A staged
        /// load never touches $3F00 directly (only the NMI $C059 upload does), but the very first
        /// table has no preceding fade, so the host has to place it itself.</summary>
        public void SeedPaletteTables(byte[] pal32)
        {
            for (int i = 0; i < 32; i++)
            {
                _paletteSrc[i] = pal32[i];
                _palMirror[i] = pal32[i];
                Ppu.PaletteRam[i] = pal32[i];
            }
        }

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

        /// <summary>Sound triggers mirroring the original's $F84E/$F850 enqueue sites.</summary>
        public const int SfxJump = 0x21;        public const int SfxLand = 0x10;
        public const int SfxHurt = 0x15;
        public const int SfxSelect = 0x1B;
        public const int SfxPause = 0x1C;
        public const int SfxDeath = 0x25;
        public const int SfxTextBlip = 0x3F;

        public void PlaySfx(int id) => Audio?.PlaySfx(id);
        public void PlayMusic(int id) => Audio?.PlayMusic(id);

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
            if (Player.EntryFreezeFrames > 0)
            {
                Player.EntryFreezeFrames--;
                return;
            }
            FrameParity++; // $95 is incremented by the NMI every frame, before any logic
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

        private void RunWorldServices()
        {
            // Audio advances via AudioSequencer.RenderFrame (Tick inside) once per
            // presented frame — SFX/music events queue at trigger time.
            if (Oam == null) return;
            // Slot 0 mirrors the player position/facing; the anim state is
            // player-state driven (walk/fire/... in PlayerCore) and advanced
            // by the dispatcher each frame.
            var s0 = Objects.Slots[0];
            // $CAFC gates on $04D0 bit 7 and the loader re-arms it, so the slot must mirror
            // the real flag byte. Force-arming it every frame kept the player composited
            // through the death freeze (gold_w05 f656 has $04D0 = $00).
            s0.Active = (s0.StateLow & 0x80) != 0;
            s0.Flags = (byte)(0x90 | (Player.FacingRight != 0 ? 0x40 : 0) | 0x10);
            s0.X = Player.X;
            s0.Y = Player.Y;
            Hud.Compose(3);
            // Spawner + cull ($8090/$802F): placements activate as their cell scrolls in.
            TileMap?.StreamColumns(Player.ScrollX);
            if (Objects?.Placements != null)
            {
                // Spawner ($8090); culling happens per-slot in OamDispatcher
                // ($802F semantics: ±16 screens, onscreen bit at high==0).
                Objects.SpawnFromPlacements(Player.ScrollX,
                    (ushort)((Player.ScrollYRow << 8) | Player.ScrollY), Player.ScrollMode);
            }
            Enemies?.RunFrame();
            Oam.RunFrame(FrameParity, 36); // 36 = hidden entry 0 + 8 HUD sprites
            Ppu.Render(Framebuffer);
        }

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
                    // G37: the ROM keeps streaming columns through $E63C until its $25 cursor
                    // reaches $0F; the per-frame pass budget is not derived yet, so the window
                    // f815-f823 is the measured constant (rendering is off, nothing is visible).
                    _reloadHold = 9;
                    return false;
                case 3:
                    if (_reloadHold > 0) { _reloadHold--; return false; }
                    WorldSpawn();                           // $DDD6-$DDFE + $DE00 render-on
                    // $DE03 JSR $FF06 spends the spawn frame, so $DE06/$C270 primes one frame
                    // later (f825 holds $0F=$30, f824 still the prelude's $88).
                    Player.EntryFreezeFrames = 0;           // stage 4 supplies the frozen frames
                    _fadeLevel = 0x40; _fadeDelta = 0xF0; _fadeFrame = 0; _fadeExitAt50 = false;
                    _reloadStage = 4;
                    return false;
                case 4:                                    // $C270 fade-in, $30/$20/$10/$00
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

        /// <summary>$C285-$C2A2: $0320 → $0300, then $0300[i] = max($0300[i] − $0F, $0F), then
        /// <code>STY $18</code> flags the table for the next NMI upload — which is why a fade
        /// step is only visible on the following frame. $0F is the port's <see cref="_fadeLevel"/>;
        /// the $0300/$0320 pair lives in <see cref="_palMirror"/>/<see cref="_paletteSrc"/>.</summary>
        private void ApplyFadeRamp()
        {
            for (int i = 0; i < 32; i++)
            {
                int v = _paletteSrc[i] - _fadeLevel;
                _palMirror[i] = (byte)(v < 0x0F ? 0x0F : v);
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
