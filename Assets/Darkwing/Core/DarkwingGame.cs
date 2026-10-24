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
    public sealed partial class DarkwingGame
    {
        public const int ScreenWidth = 256;
        public const int ScreenHeight = 240;
        public const double NtscFramesPerSecond = 60.098814; // 39375000 / (341*262*2) exact rational 39375000/178684

        public readonly byte[] Framebuffer = new byte[ScreenWidth * ScreenHeight]; // NES palette indices 0..63
        public long Frame;
        /// <summary>$95: the NMI frame counter. Bit 2 gates the player's invulnerability
        /// blink (bank5 $80EB) and bit 0 alternates the slot update order.</summary>
        public byte FrameParity;
        /// <summary>Resident bank-7 process; runs after NMI uploads and owns this frame.</summary>
        public System.Action<NesButtons> ResidentProcess;
        public readonly PlayerCore Player;
        public readonly Ppu Ppu;
        public readonly ObjectSystem Objects;
        public readonly OamDispatcher Oam;
        public readonly HudComposer Hud;
        public MissionPassContinuation MissionTail { get; private set; }
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
            Player.TimingAudioQueueAccepts = () => AudioQueueCanEnqueue;
            Player.TimingRenderingBlanked = () => _blankDepth != 0;
            Hud = new HudComposer();
            Oam = data != null
                ? new OamDispatcher(_bank5 ?? System.Array.Empty<byte>(), Objects, Ppu, Player)
                : null;
            Oam?.SetHud(Hud);
            Player.Oam = Oam;   // $9605 mission-card composition reaches the dispatcher
            Player.ResetHud = SeedHudSlot;
            Player.Sound = PlaySound;
            Player.RestoreHud = RestoreHudSlot;
            InitializeAudioQueue();
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
                    Enemies.Sfx = PlaySfx;
                    Enemies.ActorTimingEvents = new ActorEventTiming
                    {
                        PeekSfxEnqueueCycles = id => AudioQueueCanEnqueue ? 42 : 27,
                        ReadHookLatch = () => Player.HookLatchForTiming
                    };
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

        /// <summary>Late-bound bank 7 (world/music data) for the sequencer. Setting it also runs
        /// the boot-time $C92A copy, because the ROM seeds $05C2 long before any mission.</summary>
        public byte[] Bank7 { get => _bank7; set { _bank7 = value; EnsurePauseBlock(); BindNativeVramProducers(); } }

        /// <summary>
        /// Raw PRG bank 4 (ROM $8000–$BFFF window while gameplay is paused): the
        /// pause/status routine $8F00–$9025 and its tables. Index with <c>addr - $8000</c>.
        /// </summary>
        public byte[] Bank4 { get; set; }

        /// <summary>The loaded mission's placement data (spawner source).</summary>
        public GameData.LevelDef CurrentLevel { get; set; }

        /// <summary>World tile map; StreamColumns runs after the player moves.</summary>
        public LevelTileMap TileMap
        {
            get => _tileMap;
            set { _tileMap = value; if (Enemies != null) Enemies.Tiles = value; BindNativeVramProducers(); }
        }
        private LevelTileMap _tileMap;

        /// <summary>Wire the player's level-table feeds ($32/$34 readers, $E7C6 row
        /// redraw). $22 is the descriptor/streamer's index, not a derived camera position.</summary>
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
                p.VerticalStreamPass();
                TileMap.LoadRow(p.ScrollYRow);
                // $E7C6 never paints a page: $E7D4-$E7DB returns when the camera moved less
                // than 8 px, and the rest only queues rows of the room at $23 ($E821 LDY $23 /
                // JSR $EB27, records into $0360/$0383/$0384). The whole-window repaint here
                // overwrote the back page with the next grid row, which is empty at the bottom
                // of a world: on m1 f12248-13269 page $2400 sat at $00 for 1022 frames while
                // the original left it stale (10 name bytes changed across the section, max 8
                // per frame = one band). The $E698 band stream owns the nametable, and the
                // queued rows are published by RowService -> LevelTileMap.PaintRow.
                TileMap.CameraY = p.ScrollY;
                TileMap.SetScroll(p.ScrollX);
            };
            // $E698 paints one band per pass; the cursor sequence in PlayerCore is the ROM's,
            // so the nametable advances like the original instead of a per-frame repaint.
            // $E703 fetches the band's room from ($32),$23, i.e. from the loader's next cell.
            p.StageBand = u =>
            {
                TileMap.CameraY = p.ScrollY;
                TileMap.StreamBand(u, p.CellIndexNext);
            };
            // $E7C6 queues one full tile row (and its attribute run) from the incoming
            // room at $23; the page is $FD bit0. Writing it into the nametable here is
            // equivalent to the NMI draining the queue before the next frame's capture.
            p.RowService = (row, gridIndex) =>
                TileMap.PaintRow(row, gridIndex, p.ScrollX >> 8);
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
            TileMap.CameraY = Player.ScrollY;
            TileMap.SetScroll(Player.ScrollX);
            WorldSpawn();
            // $C2F9/$C319 never flag $18 — the PPU only sees a new table through a fade step's
            // $C2A2 STY $18. Inside the staged loader that is the $DE06/$C270 fade-in; this
            // entry point is the host's inline first load, so push the table once here.
            _palDirty = 0xFF;
            _sectionStage = 0;                              // $DD57 finishes inline (DE06 JMP $C254)
            SectionUsesInternalEngine = false;
            MissionCleared = false;
            WorldRunning = true;
            MapScreen = false;
            MapChooser = false;
            TitleScreen = EndingScreen = false;
            // Record 0 is below every walk's $97=#$04 start, so its tile/attr/X are the previous
            // screen's leftovers. Re-prime the value the world HUD captures show (F8 C0 00 10) after
            // any screen that seeded its own — see <see cref="LoadTitleScreen"/>.
            Oam?.SeedHiddenRecord(0, 0xC0, 0x00, 0x10);
            if (Oam != null) Oam.HudActive = true;   // $C887: slot 1 is the HUD block in a world
        }

        /// <summary>$DD5F-$DDC8 plus the $DE24 render-off/staging restart — the half of
        /// $DD57 that runs before its $DDCB yield.</summary>
        private void WorldPrelude()
        {
            var w = _worldDecoder.GetWorld(_worldId);
            _world = w;
            Objects?.SelectPlacementList(_worldId);         // $DD57 → $8000: LDX $31, re-point $72-$7B
            Objects?.ClearForSectionLoad();                 // $DD57 → $C248 → $C235
            TileMap = new LevelTileMap(_worldDecoder, Ppu, w, TileMap);
            Player.TileMap = TileMap;
            _gridByte = null; _flagByte = null;             // ($32)/($34) now point at this world
            RenderOff();                                    // $DE24 → $C14E: blank while it streams
            Player.ApplyWorldPrelude(w);
            BossBehaviors.DamageAccum = BossBehaviors.InvulnTimer = 0; // $DE54/$DE56
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
                t.ReadSpritePaletteByIndex(w.SpPalIdx, sp, _worldId); // $DDC2 → $C319
                SetPaletteSource(bg, 0);
                SetPaletteSource(sp, 16);
            }
            Ppu.Chr0Bank = (byte)(w.ChrReq0 & 0x7F);        // $FF21 applies the $1A request
            Ppu.Chr1Bank = (byte)(w.ChrReq1 & 0x7F);        // $FF3B applies the $1B request
            Player.ChrLatch0 = 0; Player.ChrLatch1 = 0;
            // $DDA0-$DDAA: the ring event is only re-queued when $D9 was still non-zero.
            if (_audioLastId != 0) PlaySound(w.RingEvent);
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
        /// <summary>$F0: the blank depth counted by $C14E/$C158. Non-zero makes the NMI take
        /// $C006 <code>LDA $F0 / ORA $9A / BEQ $C00F / JMP $C0A1</code>, which skips the whole
        /// publish path (OAM DMA, $1C/$1D and $19 VRAM uploads, CHR, palette, $2005/$2001/$2000)
        /// while the scheduler services still run. Verified on the city capture: $F0 is non-zero
        /// on 101 frames in 14 runs and the captured palette changes on none of them (97 of the
        /// other frames do), OAM on only the five run-first frames where $C14E ran after that
        /// frame's NMI.</summary>
        private int _blankDepth;
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
        /// stored on f796 while $3F00 still holds the un-faded table). The 32 bytes go out through
        /// $2007, so the $3F10/$14/$18/$1C mirrors land on $3F00/$04/$08/$0C — gold_title2 f919
        /// proves it: $0300[0]=$27 but $3F00[0]=$0F, the sprite table's own first entry.</summary>
        private void NmiPaletteUpload()
        {
            if (_palDirty == 0) return;
            for (int i = 0; i < 32; i++) Ppu.WriteVram(0x3F00 + i, _palMirror[i]);
            _palDirty = 0;
        }

        /// <summary>$C14E: <code>INC $F0 / LDA #$00 / STA $FE / STA $2001</code>. The port keeps
        /// the mask only in the PPU register, so the black window comes from
        /// <see cref="Ppu.Render"/> filling with $3F00 while bit3-4 of $2001 are clear. The
        /// depth is mirrored too, because it is what suppresses the NMI publish path.</summary>
        public void RenderOff()
        {
            _blankDepth++;                                      // $C14E INC $F0
            Ppu.WriteRegister(0x2001, 0x00);
        }

        /// <summary>$C158: <code>DEC $F0 / LDA #$1E / STA $FE / STA $2001</code>. DEC is
        /// unconditional in the ROM, so a mis-paired call leaves $F0 non-zero there as well.</summary>
        public void RenderOn()
        {
            _blankDepth--;                                      // $C158 DEC $F0
            _nmiMaskRequest = null;
            Ppu.WriteRegister(0x2001, 0x1E);
        }

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

        /// <summary>$F0, the blank depth that gates the whole NMI publish path. A stuck non-zero
        /// value suppresses palette, CHR, the VRAM queues and the OAM DMA at once, so a harness
        /// that sees a frozen picture has to be able to tell it apart from a wrong table.</summary>
        public int BlankDepth => _blankDepth;

        /// <summary>$E388 with X = section id ($CA6E calls it from the $5B door
        /// request, $D461 for the respawn after death). The loader does not return
        /// for ~11 frames, so only the descriptor tables are written here; <see cref="Step"/>
        /// finishes the staging and the $E3F0-$E406 tail.</summary>
        public void LoadSection(byte sectionId)
        {
            var t = Sections;
            if (t == null) return;
            RenderOff();                                    // $DE24, after the caller's $C248 fade
            Objects?.ClearForSectionLoad();               // $C248 → $C235
            Player.ApplySectionLoad(t, sectionId);        // $E38F-$E3D3 + $DE24
            BossBehaviors.DamageAccum = BossBehaviors.InvulnTimer = 0; // $E3C8/$E3CA
            if (TileMap != null)
            {
                TileMap.ApplyTilesetBank(Player.TilesetBank);
                TileMap.LoadRow(Player.ScrollYRow);       // $E698 row rebuild
                // The staging window's $E698 ring owns the nametable here too: with $25
                // starting at $00 (or $80 after $DE6C) and running to $0F|$70 it paints one
                // whole page plus ~9 bands of the other, so the second page keeps the
                // previous screen — measured at m1 f1847 (840 untouched name bytes). A
                // whole-window repaint also wrote page 1 and used scrollX/cell pairing rather
                // than $23, which is why the door transition diverged. Publish scroll only.
                TileMap.CameraY = Player.ScrollY;
                TileMap.SetScroll(Player.ScrollX);
            }
            var bg = new byte[16];
            var sp = new byte[16];
            t.ReadBgPalette(sectionId, bg);               // $C2F9 → $0300/$0320
            t.ReadSpritePalette(sectionId, sp, _worldId);           // $C319 → $0310/$0330
            SetPaletteSource(bg, 0);
            SetPaletteSource(sp, 16);
            BossBehaviors.DefeatTimer = 0; // $DE62 already cleared the shared $3B
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
        /// and palette (see WorldState.GameOver* for the addresses and their evidence), plus the
        /// mission intro card's OAM quartets read by <see cref="PlayerCore"/>'s $9636 blit.</summary>
        public byte[] Bank2
        {
            get => _bank2;
            set { _bank2 = value; Player.PrgBank2 = value; }
        }
        private byte[] _bank2;

        /// <summary>The bank-2 dialog coroutine ($AA00) — streams $0E/$0F are the intro lines
        /// Darkwing speaks over the night scene ($C606/$C617). Built on first use because the
        /// host wires <see cref="Bank2"/> after construction; null while bank 2 is missing.</summary>
        public TextEngine Text
        {
            get { if (Bank2 != null && _text == null) { _text = new TextEngine(Ppu, Bank2); BindNativeVramProducers(); } return _text; }
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

        /// <summary>True while the city-map screen owns the frame (set by
        /// <see cref="LoadMapScreen"/>, cleared by <see cref="LoadWorld"/> and the GAME OVER
        /// card). The map runs bank 2's own sprite service instead of the world engine.</summary>
        public bool MapScreen;

        /// <summary>True while the $A448 bubble chooser owns the frame. On confirm bank 2 drops
        /// into the $A402 idle loop, which still parks the caret but no longer re-seeds the
        /// bubbles, so <see cref="GameFlow"/> clears this flag when it leaves the chooser.</summary>
        public bool MapChooser;

        /// <summary>$6C: the bubble under the cursor — slot <c>2 + $6C</c> carries the $BB blink
        /// ($A4C8/$A51D/$A554).</summary>
        public byte MapCursorBubble;

        /// <summary>$38: completed-mission count. $A102 picks the bubble geometry from it, and
        /// $A475/$A492 freeze the cursor at 6.</summary>
        public byte MapTier;

        /// <summary>Legacy diagnostic hook. Natural victory runs $B315, $B3F8,
        /// $DA04 and state $11/$D838; the boss does not set this flag.</summary>
        public bool MissionCleared;

        /// <summary>$C7E5-$C804: clear VRAM, bank-2 task 2, $8E00 palette/CHR,
        /// then $A100 map sprites. GameFlow drives the $A300 text independently.</summary>
        public void LoadMapScreen(bool fixtureHiddenRecord = true)
        {
            // $C7E5/$C7FA: rebuild from bank-2 task 2. The old exported snapshot
            // included the preceding intro's dialog, which is not map initialization.
            FillVramPages(0x20, 0x00, 0x00);
            if (Bank2 != null) RunBank2Task(2);
            else if (MapNametable != null)
                System.Array.Copy(MapNametable, Ppu.NametableRam, System.Math.Min(MapNametable.Length, 0x800));
            if (MapPalette != null)
                for (int i = 0; i < 32; i++)
                    _paletteSrc[i] = _palMirror[i] = MapPalette[i];
            _palDirty = 0xFF;
            TitleScreen = EndingScreen = false;
            Ppu.Chr0Bank = MapChrBank0;
            Ppu.Chr1Bank = MapChrBank1;
            Ppu.SetScroll(0, 0);
            // $FA-$FD are $00 on every captured map frame. The walk culls against these RAM
            // mirrors, not the PPU latches, so leaving the last world's values here sends the
            // plate through the $8062 offscreen branch (bit 7 cleared) and every bubble
            // through a page difference.
            Player.ScrollX = 0; Player.ScrollY = 0; Player.ScrollYRow = 0;
            RenderOn();                       // $A1D7 JSR $F90C → $C158: DEC $F0 plus $2001=$1E.
            // Writing only the register left $F0 at 1 for the rest of the run, and a non-zero $F0
            // sends every later NMI to $C0A1, so the palette, CHR, VRAM queues and OAM DMA stopped
            // publishing entirely (city capture: $3F00 pinned to $0F from f1026 to f18489 while the
            // $18 upload flag stayed armed at $FF).
            Objects?.ClearForSectionLoad();
            if (fixtureHiddenRecord) Oam?.SeedHiddenRecord(0, 0xC0, 0x00, 0x10);
            MissionCleared = false;
            WorldRunning = false;
            MapScreen = true;
            MapChooser = true;
            MapCursorBubble = 0;                // $A193/$A1C8 LDA #0 / STA $6C
            if (Oam != null) Oam.HudActive = false;   // $C887 never claims slot 1 on the map
            SeedMapSlots();
        }

        /// <summary>Bank 2's map-init seeds: $8E21 puts the city plate (anim $36) in slot 0 and
        /// the dialog caret (anim $39) in slot 1, $A100 puts the three HELP bubbles in slots
        /// 2-4. All positions are absolute with the scroll parked at 0, which is what the four
        /// map captures measure ($FA-$FD = $00 on every map frame). At $38 == 6 only slot 2
        /// lives and starts as the $BB cursor ($A172); at $38 3-5 the bubbles sit on the upper
        /// row ($A13D).</summary>
        private void SeedMapSlots()
        {
            if (Objects == null) return;
            MapSlot(0, 0x36, 0x90, 0x00D0, 0x00B7);   // $8E21/$8E28/$8E38/$8E40
            MapSlot(1, 0x39, 0x90, 0x0010, 0x00B7);   // $8E2B/$8E3B/$8E55/$8E5A
            if (MapTier == 6) { MapSlot(2, 0xBB, 0x80, 0x00D0, 0x0050); return; }   // $A172
            if (MapTier >= 3)
            {
                MapSlot(2, 0x73, 0x80, 0x00A8, 0x0054);   // $A141/$A146/$A14B
                MapSlot(3, 0x73, 0x80, 0x0028, 0x0050);   // $A151/$A156/$A15B
                MapSlot(4, 0x73, 0x80, 0x0084, 0x0070);   // $A161/$A166/$A16B
                return;
            }
            MapSlot(2, 0x73, 0x80, 0x0030, 0x0088);       // $A10B/$A110/$A115
            MapSlot(3, 0x73, 0x80, 0x0098, 0x00A0);       // $A11B/$A120/$A125
            MapSlot(4, 0x73, 0x80, 0x00E0, 0x0088);       // $A12B/$A130/$A135
        }

        private void MapSlot(int index, byte anim, byte flags, ushort x, ushort y)
        {
            var s = Objects.Slots[index];
            s.Active = true;                     // $8E30/$A1A0 STA $04D0 = $80
            s.StateLow = 0x80;
            s.Flags = flags;                     // $8E28/$A1A9 STA $0400
            s.X = x; s.Y = y;                    // $04F0/$0500 and $0510/$0520 ($A19B/$A1B4)
            EnemyBehaviors.SetAnim(s, anim);     // $FA6C/$FA78: number, timer and frame to 0
        }

        /// <summary>One map frame: the chooser's per-frame seed ($A4AE/$A503/$A53A), then the
        /// bank-5 walk that ticks the clocks and composes the $0200 shadow — the service $F89A
        /// drives from inside both map loops.</summary>
        private void StepMapFrame(byte pressed)
        {
            // $A448 reads held $14: a frame spent on A($80, $A44E → $A43F) or on Right($01)/
            // Left($02) tails into JSR $FF06 / JMP $A448 and never reaches $A4F1, so NEITHER the
            // bubble seed NOR the caret parking runs — probe_map2 f602 samples $0431 = $02 from
            // exactly such a frame while every idle frame reads $01.
            bool chooserIdle = MapChooser && (pressed & 0x83) != 0;
            if (!chooserIdle)
            {
                var caret = Objects.Slots[1];
                caret.AnimTimer = 0;             // $A4FA/$A404: STA $0431 / STA $0411
                caret.AnimFrame = 0;
            }
            if (MapChooser && !chooserIdle)
            {
                // The two bubbles the cursor is NOT on are re-seeded to $73 every frame, which
                // is why their $0430 reads $01 (the seed's 0 plus the walk's INC) while the
                // plate and the cursor run their own clocks.
                int cursor = 2 + MapCursorBubble;
                for (int slot = 2; slot <= 4; slot++)
                    if (slot != cursor) EnemyBehaviors.SetAnim(Objects.Slots[slot], 0x73);
                // $A4C8/$A51D/$A554: the cursor bubble takes the $BB blink ONCE — a slot that
                // already holds $BB is left alone, so its blink keeps phase while the cursor
                // rests on it and restarts when the cursor comes back from a $73 re-seed.
                var cur = Objects.Slots[cursor];
                if (cur.AnimNumber != 0xBB) EnemyBehaviors.SetAnim(cur, 0xBB);
            }
            FrameParity++;                                     // $8000-$8002
            Oam.PublishToOam();
            Oam.RunFrame(FrameParity);                         // $A4F1 STA $97=#$04 + $F89A
            Ppu.Render(Framebuffer);
        }

        // ==================================================================
        // The $C792 title screen — bank 7's own loop + the $9500/$9200 bank-2 seeds
        // ==================================================================

        /// <summary>True while the title owns the frame. The ROM reaches $C792 from its attract
        /// loop ($C78C <code>LDA $14 / AND #$10 / BEQ $C721</code> — the title only appears on a
        /// Start press), seeds two sprite slots, then waits for a SECOND Start at $C7C9. The port
        /// has no attract mode, so its Phase.Title IS the title screen.</summary>
        public bool TitleScreen;

        /// <summary>$C797-$C7AA: <code>JSR $C970</code> (the resident fade-to-black, $C1BA clear
        /// and $C95C object-RAM clear), <code>LDA #$02 / JSR $FF55</code> + <code>LDX #$01 /
        /// JSR $8000</code> (bank-2 task 1 streams the silhouette/stripes picture), then $9500
        /// (palettes + CHR + the logo slot) and $9200 (the PRESS START slot), then $C158.
        /// The picture is therefore a bank-2 record stream, not an exported nametable — the
        /// "title art is missing from the port" note in GAPS was a false premise.</summary>
        public void LoadTitleScreen(bool fixtureHiddenRecords = true)
        {
            if (Bank2 == null || Objects == null) return;
            FillVramPages(0x20, 0x00, 0x00);                   // $C979-$C97F JSR $C1BA(#$20,$00,$00)
            ClearObjectRamAndScroll();                         // $C982 JSR $C95C
            RunBank2Task(1);                                   // $C79A-$C7A1 → $80A1 records
            var t = Sections;
            if (t != null)
            {
                var bg = new byte[16];
                t.ReadBgPaletteByIndex(0x0D, bg);               // $950E-$9510 JSR $C2F9(#$0D) → $C42D
                SetPaletteSource(bg, 0);
            }
            for (int i = 0; i < 16; i++)                        // $9502-$950C: literal → $0310 AND $0330
                _paletteSrc[16 + i] = _palMirror[16 + i] = Bank2[0x953C - 0x8000 + i];
            SetChrAndUpload(0x9F, 0x99, 0x99);                  // $9513-$951B: $1A/$1B requests + $18
            TitleSlot(0, 0xA3, 0x0028, 0x009B);                 // $951D-$953A: the logo
            TitleSlot(1, 0xBC, 0x0000, 0x00DC);                 // $9202-$9213: PRESS START
            Objects.Slots[1].Flags = 0xA4;                      // $920C-$920E STA $0400,X = #$A4
            Objects.Slots[1].XVelFrac = 0;                      // $921B-$9221 STA $0500/$0520/$0530,X
            Objects.Slots[1].XVelInt = 0x01;                    // $9224-$9226 STA $0540,X = #$01
            // The title walk starts at $97=#$04 and never reaches record 0, and $C21E only writes
            // its Y byte, so tile/attr/X keep what the previous screen composed. gold_title2 shows
            // F8 C0 00 64 on every title frame — the attract screen's last composition, which the
            // port does not have, so the captured value is seeded directly.
            if (fixtureHiddenRecords)
            {
                Oam?.SeedHiddenRecord(0, 0xC0, 0x00, 0x64);
                Oam?.SeedHiddenRecord(53, 0xFC, 0x01, 0x84);
                Oam?.SeedHiddenRecord(54, 0x30, 0x41, 0x86);
            }
            RenderOn();                                         // $C7AA JSR $C158
            TitleScreen = true;
            MapScreen = false;
            MapChooser = false;
            EndingScreen = false;
            WorldRunning = false;
            if (Oam != null) Oam.HudActive = false;
        }

        /// <summary>One title frame: $C7AD parks $97 at 4, $C21E publishes the $0200 shadow, the
        /// bank-5 <code>JSR $8000</code> walks the slots, then $C7BF-$C7C1 integrates slot 1's X
        /// through $F4FD (PRESS START scrolls right at 1 px/frame, $0541=$01) and $C7C4 re-arms
        /// its $0400. This is <see cref="StepMapFrame"/> minus the chooser, plus the mover.</summary>
        private void StepTitleFrame()
        {
            FrameParity++;                                      // $8000-$8002
            Oam.PublishToOam();                                 // $C7B1 JSR $C21E
            Oam.RunFrame(FrameParity);                          // $C7B9 JSR $8000 (bank 5)
            EnemyBehaviors.IntegrateXPos(Objects.Slots[1]);      // $C7BF LDX #$01 / JSR $F4FD
            Objects.Slots[1].Flags = 0x80;                      // $C7C4 LDA #$80 / STA $0401
            Ppu.Render(Framebuffer);
        }

        /// <summary>$9500/$9200 write $04D0,X=#$80 and the coordinate pair only — the flags byte
        /// $0400 is left to the walk's on-screen test (slot 0) or to the seed itself (slot 1).</summary>
        private void TitleSlot(int index, byte anim, ushort x, ushort y)
        {
            var s = Objects.Slots[index];
            s.Active = true; s.StateLow = 0x80;                 // $9526/$9209 STA $04D0,X = #$80
            s.Behavior = 0;
            s.X = x; s.Y = y;                                   // $04F0/$0500, $0510/$0520
            s.ScreenX = (byte)x; s.ScreenY = (byte)y;
            EnemyBehaviors.SetAnim(s, anim);                     // $951F/$9202 through $FA6C
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
            Ppu.SetScroll(0, 0);
            Ppu.WriteRegister(0x2001, 0x1E);   // ppu_writes.csv f300: $2001=30 every frame
            Objects?.ClearForSectionLoad();    // $D47C JSR $C838
            MissionCleared = false;
            WorldRunning = false;
            MapScreen = false;                 // $D46C replaces the map: no bubble seed, no walk
            MapChooser = false;
        }

        // ==================================================================
        // G67: the ending ($D841-$D8D0) — the bank-2 primitives its three blocks call
        // ==================================================================

        /// <summary>True while an ending scene owns the frame. $A900/$9E00/$9280 are bank-2
        /// resident routines that drive the bank-5 walk through $F89A, like the map chooser,
        /// but without its per-frame bubble seed ($A4AE/$A503/$A53A) or the $A4FA caret park —
        /// the ending's own sprites live in slots 1-5 and must keep their clocks.</summary>
        public bool EndingScreen;

        /// <summary>True while the ending is resident inside a $FEFF count (<code>JSR $FF06 /
        /// DEX / BNE</code> — the $D89D 30-frame gap and $9280's $F0=240-frame hold). Only the
        /// yield runs there, so <see cref="StepEndingFrame"/> is a no-op: no walk, no $95 tick.</summary>
        public bool EndingYieldOnly;

        /// <summary>$C1BA: NMI off (<code>LDA $FF / AND #$FE / STA $2000</code>), then 4 × 256
        /// bytes of X from the page high byte A, then 64 bytes of Y over the tail of that fill.
        /// $C1F1's <code>ADC #$02</code> runs with the carry still set from $C1ED's
        /// <code>CMP #$20</code>, so the attribute pass lands on pageHigh + 3, $C0 = the LAST 64
        /// bytes the fill wrote ($23C0 for A=$20) — it is a patch of the fill's own tail, not a
        /// second page. A &lt; $20 takes the CHR-fill path ($C1DA re-reads the page count from Y);
        /// the ending only ever calls it with A=$20.</summary>
        public void FillVramPages(byte pageHigh, byte tile, byte attr)
        {
            Ppu.WriteRegister(0x2000, (byte)(Ppu.Ctrl & 0xFE));       // $C1C0-$C1C7
            Ppu.WriteRegister(0x2006, pageHigh);                      // $C1CC
            Ppu.WriteRegister(0x2006, 0x00);                          // $C1D1 STY $2006 (Y=0)
            for (int i = 0; i < 4 * 256; i++)                         // $C1D4/$C1E0-$C1E7
                Ppu.WriteRegister(0x2007, tile);
            if (pageHigh < 0x20) return;                              // $C1EF BCC $C203
            Ppu.WriteRegister(0x2006, (byte)(pageHigh + 3));          // $C1F1 ADC #$02 with C=1
            Ppu.WriteRegister(0x2006, 0xC0);                          // $C1F6
            for (int i = 0; i < 64; i++)                              // $C1FB LDX #$40
                Ppu.WriteRegister(0x2007, attr);
        }

        /// <summary>$C95C: $C235 (slots 15…1 cleared, slot 0 left alone), then slot 0's $0400
        /// and $04D0 written by hand, then the four scroll mirrors $FC/$FA/$FD/$FB.</summary>
        public void ClearObjectRamAndScroll()
        {
            Objects?.ClearForSectionLoad();                           // $C95C → $C235
            if (Objects != null)
            {
                var s0 = Objects.Slots[0];
                s0.Flags = 0;                                          // $C961 STA $0400
                Player.Flags = 0;                                    // shared $0400
                s0.Active = false; s0.StateLow = 0;                    // $C964 STA $04D0
            }
            Player.ScrollX = 0; Player.ScrollY = 0;                   // $FC/$FA
            Player.ScrollYRow = 0;                                     // $FB
            Ppu.SetScroll(0, 0);
        }

        /// <summary>Bank 2's task entry $8000 with X = the task index. The pointer pair sits in
        /// two overlapping tables — low bytes at $803D, high bytes at $8043 — so index i reads
        /// $803D+i and $8043+i; the ROM's six tasks are $8049, $80A1, $83EC, $8789, $87C8, $89BE.
        /// Records are [addrHi][addrLo][count][data…], ended by an addrHi with bit 7 set
        /// ($8011 CMP #$FF), and count is the exact number of $2007 writes ($8031 DEX /
        /// $8032 BNE $8029) — one less than $C119's queue convention.</summary>
        public void RunBank2Task(int task)
        {
            int p = ((Bank2[0x43 + task] << 8) | Bank2[0x3D + task]) - 0x8000;   // $8000/$8005
            while ((Bank2[p] & 0x80) == 0)
            {
                Ppu.WriteRegister(0x2006, Bank2[p]);                  // $8015
                Ppu.WriteRegister(0x2006, Bank2[p + 1]);              // $801D
                int count = Bank2[p + 2];                             // $8023 TAX
                for (int i = 0; i < count; i++)
                    Ppu.WriteRegister(0x2007, Bank2[p + 3 + i]);      // $8029/$802B
                p += 3 + count;
            }
        }

        /// <summary>$9E02-$9E15 / $A902-$A915: 16 bytes → $0300 AND $0320, 16 bytes → $0310 AND
        /// $0330 — the same double write $C2F9/$C319 do, only from a literal bank-2 table.</summary>
        private void BlitBank2Palette(int bgTable, int spTable)
        {
            for (int i = 0; i < 16; i++)
            {
                _paletteSrc[i] = _palMirror[i] = Bank2[bgTable - 0x8000 + i];
                _paletteSrc[16 + i] = _palMirror[16 + i] = Bank2[spTable - 0x8000 + i];
            }
        }

        /// <summary>$1A/$1B are the CHR request bytes the NMI applies as bank = value &amp; $7F
        /// (see <see cref="WorldPrelude"/>), $18 is the $C059 palette-upload flag.</summary>
        private void SetChrAndUpload(byte req1A, byte req1B, byte flag18)
        {
            Ppu.Chr0Bank = (byte)(req1A & 0x7F);
            Ppu.Chr1Bank = (byte)(req1B & 0x7F);
            _palDirty = flag18;
        }

        /// <summary>$EE5C/$9322: <code>LDA #$FF / STA $18</code> — flag the $C059 upload without
        /// touching the $1A/$1B requests (the ending's third scene keeps the ones $DD57 loaded).</summary>
        private void SetPaletteUploadFlag(byte flag) => _palDirty = flag;

        /// <summary>One ending scene sprite: the $9E00/$A900/$9280 seeds write $04D0=$80 and
        /// $0400 with the high coordinate bytes already zeroed, so X/Y are the page-low pair.</summary>
        private void EndingSlot(int index, byte flags, ushort x, ushort y, byte anim)
        {
            var s = Objects.Slots[index];
            s.Active = true; s.StateLow = 0x80;      // $04D0,X = $80 (the MapSlot convention)
            s.Flags = flags;                         // $0400,X
            s.Behavior = 0;
            s.X = x; s.Y = y;                        // $04F0/$0500, $0510/$0520
            s.ScreenX = (byte)x; s.ScreenY = (byte)y;
            EnemyBehaviors.SetAnim(s, anim);         // $FA6C: number, timer and frame
        }

        /// <summary>$A900 — the ending's first scene ($D86C, with $62=$11 and task 5 already
        /// blitted): palette $A94C/$A95C, slot 1 = anim $56 at ($7C,$5B), $1B=$9E/$1A=$8C/
        /// $18=$8C, then $F90C (render on + yield) and $F89A (one walk frame). The $AA00 dialog
        /// is driven by <see cref="GameFlow"/> because the ROM stays resident inside it.</summary>
        public void EndingSceneA900()
        {
            BlitBank2Palette(0xA94C, 0xA95C);                        // $A902/$A90B
            EndingSlot(1, 0x80, 0x007C, 0x005B, 0x56);               // $A919/$A92E/$A933
            SetChrAndUpload(0x8C, 0x9E, 0x8C);                        // $A938-$A940
            RenderOn();                                               // $A942 JSR $F90C
            EndingScreen = true; MapScreen = false; WorldRunning = false;
        }

        /// <summary>$9E00 — the ending's second scene ($D89A, with $62=$09 and task 4 already
        /// blitted): palette $9E68/$9E78, slot 1 = anim $AF at ($8C,$67), slot 2 = anim $B0 at
        /// ($88,$87), $1B=$9B/$1A=$84/$18=$84, then $F90C and $F89A.</summary>
        public void EndingScene9E00()
        {
            BlitBank2Palette(0x9E68, 0x9E78);                         // $9E02/$9E0B
            EndingSlot(1, 0x80, 0x008C, 0x0067, 0xAF);                // $9E19/$9E40/$9E4A
            EndingSlot(2, 0x80, 0x0088, 0x0087, 0xB0);                // $9E1F/$9E45/$9E4F
            SetChrAndUpload(0x84, 0x9B, 0x84);                         // $9E54-$9E5C
            RenderOn();                                                // $9E5E JSR $F90C
            EndingScreen = true; MapScreen = false; WorldRunning = false;
        }

        /// <summary>$9280's seeds, run after its <code>LDX #$F0 / JSR $FEFF</code> 240-frame
        /// hold: four anim slots ($2F/$AE/$C6/$C5), the velocity bytes the scene's falling
        /// sprites need ($0530=$19, $0542=3, $0544=7), the sprite palette $93B8 → $0310/$0330,
        /// $18=$FF and $97=$04 + one $F89A walk. $9326's flight branch tests
        /// <code>LDA $04D0 / AND #$0F / CMP #$02</code>; after $C95C cleared slot 0 it is $0, so
        /// the $F55E/$F51B flight (still unported, task #6) is NOT taken and the routine RTS-es
        /// at $934B.</summary>
        public void EndingScene9280()
        {
            var o = Objects;
            EndingSlot(1, 0x90, 0x00FF, 0x00A4, 0x2F);                 // $92A7/$92AC/$92B1
            o.Slots[1].XVelFrac = 0x19;                                 // $92DF STA $0530,X
            EndingSlot(2, 0x80, 0x0000, 0x00AC, 0xAE);                 // $9294/$92F4/$92EF
            o.Slots[2].XVelInt = 0x03;                                  // $92E4 STA $0542
            EndingSlot(3, 0x00, 0x0000, 0x0000, 0x00);                  // $9297/$92BF/$92C8
            o.Slots[3].Active = true;
            EndingSlot(4, 0x80, 0x0000, 0x00B7, 0xC6);                 // $929A/$9300/$92FB
            o.Slots[4].XVelInt = 0x07;                                  // $92E9 STA $0544
            EndingSlot(5, 0xA0, 0x00A0, 0x00D0, 0xC5);                 // $92A2/$930C/$9311
            o.Slots[5].Active = true;
            for (int i = 0; i < 16; i++)                                // $9314-$9320
                _paletteSrc[16 + i] = _palMirror[16 + i] = Bank2[0x93B8 - 0x8000 + i];
            SetPaletteUploadFlag(0xFF);                                  // $9322 LDA #$FF / STA $18
            EndingScreen = true;
        }

        /// <summary>True while a bank-2 intro story scene owns the frame ($C66B <c>$8F00</c>,
        /// $C684 <c>$9100</c>, $C69D <c>$9000</c>, $C6B6 <c>$A000</c>). Like the ending scenes the
        /// ROM is resident inside the routine and drives the bank-5 walk itself through $F89A, so
        /// <see cref="StepEndingFrame"/> is the frame driver and the player dispatcher stays out.</summary>
        public bool IntroScene;

        /// <summary>$8F00-$8F5D, the first story panel ($C65F sets <c>$31=$09</c> and $DD57 loads it):
        /// slot 1 = anim $2F, flags $90, X <c>$00FF</c>, Y <c>$00A4</c>, velocity $0540=$00/$0530=$E9
        /// (−0.91 px/frame); slot 2 = anim $AD, flags $80, X <c>$0000</c>, Y <c>$00B7</c>, velocity
        /// $0542=$06 (+6 px/frame); sprite palette $9A9C → $0310 AND $0330 and $18=$FF.</summary>
        public void IntroPanel8F00Seed()
        {
            var o = Objects;
            EndingSlot(1, 0x90, 0x00FF, 0x00A4, 0x2F);            // $8F02/$8F14/$8F19/$8F1E/$8F04
            o.Slots[1].XVelInt = 0x00; o.Slots[1].XVelFrac = 0xE9;  // $8F21/$8F3A, $8F2C = 0
            EndingSlot(2, 0x80, 0x0000, 0x00B7, 0xAD);            // $8F42/$8F45/$8F4A/$8F35/$8F0F
            o.Slots[2].XVelInt = 0x06; o.Slots[2].XVelFrac = 0x00;  // $8F3D, $8F32 = 0
            for (int i = 0; i < 16; i++)                          // $8F4F-$8F59
                _paletteSrc[16 + i] = _palMirror[16 + i] = Bank2[0x9A9C - 0x8000 + i];
            SetPaletteUploadFlag(0xFF);                           // $8F5B/$8F5D
            IntroScene = true; MapScreen = false; WorldRunning = false;
        }

        /// <summary>One iteration of <c>$8F63…$8F79</c>: $F89A, the $14 exit gate ($8F66 → $8F7B),
        /// slot 1 <c>X -= $0541.$0531</c> through $F51B, slot 2 <c>X += $0542.$0532</c> through $F4FD,
        /// then <c>$04F1 &gt;= 4</c> re-arms the loop. False = the body reached $8F82 RTS.</summary>
        public bool IntroPanel8F00Step(byte justPressed)
        {
            StepEndingFrame();                                    // $8F63 JSR $F89A (+ compose)
            if (justPressed != 0) { IntroScene = false; return false; }   // $8F66 BNE $8F7B
            EnemyBehaviors.IntegrateXNeg(Objects.Slots[1]);       // $8F6C LDX #$01 / JSR $F51B
            EnemyBehaviors.IntegrateXPos(Objects.Slots[2]);       // $8F71 LDX #$02 / JSR $F4FD
            if ((Objects.Slots[1].X & 0xFF) >= 0x04) return true;  // $8F74/$8F77 BCS $8F5F
            IntroScene = false;
            return false;                                          // $8F7B…$8F82
        }

        /// <summary>$9100's sprite seeds (<c>$9100…$9153</c>): slot 1 = anim $2D, flags $E0, StateLow $80,
        /// X $0080, Y $005C; slot 2 = anim $B5, flags $A0, StateLow $80, X $0080, Y $0048; sprite palette
        /// $919E → $0310/$0330, $18=$FF. The single $F89A at $9155 and the $0550/$0560 scroll-settle
        /// stage are in <see cref="IntroPanel9100Step"/>, not here.</summary>
        public void IntroPanel9100Seed()
        {
            EndingSlot(1, 0xE0, 0x0080, 0x005C, 0x2D);            // $9100 head/$911B/$9121/$9127/$912D
            EndingSlot(2, 0xA0, 0x0080, 0x0048, 0xB5);            // $9133/$9139/$913F/$9145
            // Slot 0 is the settle's moving reference ($9169 JSR $F539) and $C95C clears only
            // $0400/$04D0/$0590, so $9100 inherits it. Neither $8F00 nor $9100 writes these bytes,
            // and gold_intro_panels measures the same values from f600 ($31=$07) through f1054, so
            // the port restates them here: Y $0006, $0460=$B7 (the $80+$B7 carry that makes the
            // first settle step +3), X $002C, velocity $F8.00.
            var s0 = Objects.Slots[0];
            s0.Y = 0x0006; s0.YFrac = 0xB7;
            s0.X = 0x002C; s0.YVelInt = -8; s0.YVelFrac = 0x00;
            for (int i = 0; i < 16; i++)                          // $919E table, $0310 AND $0330
                _paletteSrc[16 + i] = _palMirror[16 + i] = Bank2[0x919E - 0x8000 + i];
            SetPaletteUploadFlag(0xFF);
            _intro9100Stage = 0;
            IntroScene = true; MapScreen = false; WorldRunning = false;
        }

        private int _intro9100Stage;   // 0 = $9155 walk, 1 = $9162 settle, 2 = $9188 animation gate

        /// <summary>$F7E0 as the panel uses it: <c>LDA #$04 / STA $2F</c> then <c>JMP $DC6E</c>
        /// (the saved <c>LDA $F1 / PHA … PLA / JMP $FF55</c> bank push is a no-op — the panel is
        /// already in the bank $DC6E lives in). The $DC88-$DC8D step is the observable effect: $FA
        /// becomes the byte $916E-$9179 just computed from $0510, so the camera tracks slot 0's Y.</summary>
        private void IntroVerticalScroll()
        {
            Player.ScrollSceneDown(Player.ScrollStep);
        }

        /// <summary>The three stages of <c>$9100</c>: one <c>$F89A</c> at $9155, then the $9162…$9186
        /// settle (<c>JSR $FF06</c> + $F539 on slot 0 + $F7E0, NO walk — the golden freezes $0431=$01
        /// while $FA chases slot 0's $0510 up to $A4), then the $9188…$9194 gate that walks until
        /// <c>$0411 == $11</c>. The settle scroll and the gate's first walk are reported one frame
        /// apart because a settle frame captures at its $FF06 while the gate captures inside $F89A
        /// (gold f1117 $FA=$A1/$0431=$01, f1118 $FA=$A4/$0431=$02), so the walk is deferred by one
        /// step. 84 gate walks end with $0431/$0432 = $00/$04 at f1201. False = $9196…$919D.</summary>
        public bool IntroPanel9100Step(byte justPressed)
        {
            if (_intro9100Stage == 0)
            {
                StepEndingFrame();                                 // $9155 JSR $F89A
                Objects.Slots[0].YVelFrac = 0x80;                  // $9158/$915A STA $0550
                Objects.Slots[0].YVelInt = 0x02;                   // $915D/$915F STA $0560
                _intro9100Stage = 1;
                return true;
            }
            if (_intro9100Stage == 1)
            {
                if (justPressed != 0) { IntroScene = false; return false; }   // $9165/$9167 BNE $9196
                var s0 = Objects.Slots[0];
                EnemyBehaviors.IntegrateYRideSlot(s0);             // $9169 LDX #$00 / JSR $F539
                int d = (s0.Y & 0xFF) - Player.ScrollY;            // $916E SEC / SBC $FA
                if (d < 0) d -= 0x10;                              // $9176/$9178 SBC #$0F with C=0
                Player.ScrollStep = (byte)d;                       // $9179 STA $10
                IntroVerticalScroll();                             // $917B JSR $F7E0
                if (Player.ScrollYRow != 0 || Player.ScrollY != 0xA4) return true;   // $917E…$9186
                // The settle frame's own capture sits at its $FF06 (BEFORE $F539/$F7E0), so the
                // $FA=$A4 this step produced and the $918C walk that follows it in the same frame
                // are only both visible at the NEXT capture, which is inside $F89A (gold f1117 =
                // $FA $A1/$0431 $01, gold f1118 = $FA $A4/$0431 $02). Hence the walk is deferred.
                _intro9100Stage = 2;
                return true;
            }
            StepEndingFrame();                                    // $918C JSR $F89A
            if (Objects.Slots[1].AnimFrame != 0x11) return true;   // $918F/$9192/$9194 BNE $9188
            IntroScene = false;
            return false;                                          // $9196…$919D LDA #$04/STA $97/$C21E/RTS
        }

        /// <summary>$9000's sprite seeds, i.e. everything after its $FA/$FB scroll-settle stage and
        /// its <c>LDX #$0A / JSR $FEFF</c>: slot 1 = anim $3B, StateLow $80, flags $B0, X $00FF,
        /// Y $0170 (<c>$0520,X=#$01</c>), velocity $0541=$04/$0531=$04 (−4 px/frame); sprite palette
        /// $90A4 → $0310/$0330; $18=$FF.</summary>
        public void IntroPanel9000Seed()
        {
            EndingSlot(1, 0xB0, 0x00FF, 0x0170, 0x3B);            // $903A/$9040/$9046/$904C/$902E
            Objects.Slots[1].XVelInt = 0x04; Objects.Slots[1].XVelFrac = 0x04;   // $9052/$9058
            for (int i = 0; i < 16; i++)                          // $90A4 table
                _paletteSrc[16 + i] = _palMirror[16 + i] = Bank2[0x90A4 - 0x8000 + i];
            SetPaletteUploadFlag(0xFF);                            // $9075/$9077
            IntroScene = true; MapScreen = false; WorldRunning = false;
        }

        /// <summary>One iteration of <c>$9079…$909A</c>: $97=$04, $F89A, the $14 gate, slot 1
        /// <c>X -= $0541.$0531</c>, the mid-flight flag flip <c>$04F1 &lt; $70 → $0400,X=$90</c>
        /// ($9089…$9093) and the <c>$04F1 &gt;= 4</c> re-arm. False at $909C…$909F RTS.</summary>
        public bool IntroPanel9000Step(byte justPressed)
        {
            StepEndingFrame();                                    // $907D JSR $F89A
            if (justPressed != 0) { IntroScene = false; return false; }   // $9080 BNE $909C
            var s1 = Objects.Slots[1];
            EnemyBehaviors.IntegrateXNeg(s1);                      // $9084 LDX #$01 / JSR $F51B
            if ((s1.X & 0xFF) < 0x70) s1.Flags = 0x90;              // $9089…$9093
            if ((s1.X & 0xFF) >= 0x04) return true;                 // $9095/$9097 BCS $9079
            IntroScene = false;
            return false;
        }

        /// <summary>$A000 — the panel chain's last call ($C6B6) is not a panel but the scene-$0A
        /// entry: slot 1 = anim $B2, StateLow/flags $80, X $0000, Y $006C, and the PLAYER (slot 0)
        /// parked at X $0200 with velocity $0530/$0531=$99/$99 and $0540/$0541=$00 (0.6 px/frame),
        /// $FD=$02 and the camera page $6F=$02; sprite palette $A04C → $0310/$0330, $18=$00.</summary>
        public void IntroEntryA000()
        {
            var o = Objects;
            EndingSlot(1, 0x80, 0x0000, 0x006C, 0xB2);              // $A004/$A007/$A00C/$A011/$A014/$A016
            o.Slots[0].X = (ushort)((o.Slots[0].X & 0xFF) | (0x02 << 8));   // $A019/$A01B STA $0500
            o.Slots[1].X = 0x0200;                                  // $A01E STA $0501 ($04F1 = 0)
            o.Slots[0].Y = (ushort)(o.Slots[0].Y & 0xFF);            // $A025/$A027 STA $0520
            o.Slots[1].Y = 0x006C;                                  // $A02A STA $0521 ($0512 = $6C)
            o.Slots[0].XVelInt = 0x00; o.Slots[1].XVelInt = 0x00;   // $A02D/$A030 STA $0540/$0541
            o.Slots[0].XVelFrac = 0x99; o.Slots[1].XVelFrac = 0x99; // $A033/$A035/$A038 $0530/$0531
            Player.ScrollX = (ushort)((Player.ScrollX & 0xFF) | (0x02 << 8));   // $A021 STA $FD
            for (int i = 0; i < 16; i++)                            // $A03B…$A047
                _paletteSrc[16 + i] = _palMirror[16 + i] = Bank2[0xA04C - 0x8000 + i];
            SetPaletteUploadFlag(0x00);                             // $A049 STY $18 with Y = 0
            IntroScene = true; MapScreen = false; WorldRunning = false;
        }

        /// <summary>$C970 as the ending calls it: $C276 seeds the fade-out ($0F=$10, $0D=$10,
        /// exit at $0F==$50), $FF06 yields the one frame, then $C14E blanks, $C1BA($20,$00,$00)
        /// clears the nametable and $C95C empties object RAM. The ROM's fade ramp keeps running
        /// in the background service (modelled by <see cref="_fadeRampActive"/> in
        /// <see cref="StepEndingFrame"/>), so the seed+yield is one frame here, not sixteen —
        /// there is no ending capture that could prove any other residency.</summary>
        public void EndingFadeOutAndClear()
        {
            _fadeLevel = 0x00; _fadeDelta = 0x10; _fadeFrame = 0;      // $C276
            _fadeExitAt50 = true;
            _fadeRampActive = true;
            StepFade();                                                // $C276 falls into $C285
            RenderOff();                                               // $C976 JSR $C14E
            FillVramPages(0x20, 0x00, 0x00);                            // $C979-$C97F
            ClearObjectRamAndScroll();                                  // $C982 JSR $C95C
        }

        private bool _fadeRampActive;

        /// <summary>One ending frame: the background $C285-$C2BF ramp, then the bank-5 walk the
        /// scene routines drive through $F89A ($C21E park at $97=$04 + the compose), then the
        /// render. This is <see cref="StepMapFrame"/> without the chooser logic. Neither of them
        /// ticks <c>Frame</c>: <see cref="Step"/> already does it for every non-world phase, so
        /// one call to <c>Step</c> is exactly one NMI (the counter has no runtime reader — the
        /// probes only print it — but a doubled tick mislabelled every map-phase frame number
        /// in those reports).
        /// While the ending sits inside $FEFF/$9280's <code>LDX #$F0</code> the ROM is resident in
        /// the yield — ProcessYield only, no walk and no $95 tick — so <see cref="EndingYieldOnly"/>
        /// reduces the frame to a no-op.</summary>
        private void StepEndingFrame()
        {
            if (EndingYieldOnly) return;
            if (_fadeRampActive && StepFade()) _fadeRampActive = false;
            FrameParity++;                                              // $8000-$8002
            Oam.PublishToOam();
            Oam.RunFrame(FrameParity);                                  // $F89A
            Ppu.Render(Framebuffer);
        }

        /// <summary>Sound triggers mirroring the original's $F84E/$F850 enqueue sites.</summary>
        public const int SfxMapLaunch = 0x21;   public const int SfxLand = 0x10;
        public const int SfxHurt = 0x15;
        public const int SfxSelect = 0x1B;
        public const int SfxPause = 0x1C;
        public const int SfxDeath = 0x25;
        public const int SfxTextBlip = 0x3F;

        // $DA/$DB/$DC-$E3: the NMI-visible task ring. $88 is the empty marker; the
        // writer only advances when the slot is empty, so an enqueue during a full
        // ring is dropped exactly like $F84E. $D9 keeps the last id even when dropped.
        private readonly byte[] _audioQueue = new byte[8];
        public bool AudioQueueCanEnqueue => _audioQueue[_audioWrite] == AudioQueueEmpty;
        private int _audioWrite, _audioRead;
        private byte _audioLastId;
        private const byte AudioQueueEmpty = 0x88;

        private void InitializeAudioQueue()
        {
            for (int i = 0; i < _audioQueue.Length; i++) _audioQueue[i] = AudioQueueEmpty;
            _audioWrite = _audioRead = 0;
        }

        private void EnqueueAudio(byte id)
        {
            if (_audioQueue[_audioWrite] != AudioQueueEmpty) return;
            _audioQueue[_audioWrite] = id;
            _audioWrite = (_audioWrite + 1) & 7;
        }

        /// <summary>$C0DC/$FF8C: drain every queued task, then run the $8000 NMI tick.</summary>
        public int LastNmiAudioServiceCycles { get; private set; }
        public bool NmiAudioTimingSupported { get; private set; }

        private void DrainAudioQueue()
        {
            LastNmiAudioServiceCycles = 0;
            NmiAudioTimingSupported = Audio != null;
            while (_audioQueue[_audioRead] != AudioQueueEmpty)
            {
                byte id = _audioQueue[_audioRead];
                _audioQueue[_audioRead] = AudioQueueEmpty;
                _audioRead = (_audioRead + 1) & 7;
                Audio?.Task(id);            // $8003 Audio_Task_Queued
                if (Audio != null)
                {
                    LastNmiAudioServiceCycles += NmiTiming.QueuedTaskOverheadCycles + Audio.LastTaskCycles;
                    NmiAudioTimingSupported &= Audio.TimingSupported;
                }
            }
            Audio?.Tick();                  // $8000 Audio_Tick, once per NMI
            if (Audio != null)
            {
                LastNmiAudioServiceCycles += NmiTiming.EmptyAudioQueueCycles + NmiTiming.TickCallCycles
                    + Audio.LastTickCycles + NmiTiming.RestoreBankAndReturnCycles;
                NmiAudioTimingSupported &= Audio.TimingSupported;
            }
            if (!NmiAudioTimingSupported) LastNmiAudioServiceCycles = -1;
        }

        public void PlaySfx(int id)
        {
            if (id < 16 || id > 63) throw new System.ArgumentOutOfRangeException(nameof(id), "SFX ids are 16-63");
            EnqueueAudio((byte)id);
        }

        public void PlayMusic(int id)
        {
            if (id < 0 || id > 15) throw new System.ArgumentOutOfRangeException(nameof(id), "music ids are 0-15");
            PlaySound(id);
        }

        /// <summary>The generic $F84E enqueue: the ROM passes the raw id through, so it covers
        /// sound-table entries ($10-$3F), music-table ids ($00-$0F) and the special engine
        /// tasks ($F0-$F7) — e.g. $D4BE's #$F0 stop and $D4A7's #$0C.</summary>
        public void PlaySound(int id)
        {
            _audioLastId = (byte)id;
            EnqueueAudio((byte)id);
        }

        /// <summary>$D9 as the next $F84E call sees it.</summary>
        public byte AudioLastId => _audioLastId;

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
        /// <summary>$14: P1 press edge latched by the last input poll that ran.</summary>
        public byte PressedP1 { get; private set; }
        /// <summary>$16 as seen by the next poll: P1 held byte from that same poll. Frames that
        /// return before reaching a poll (loader, reload, entry freeze) leave both latched.</summary>
        public byte HeldP1 => _prevHeld;
        public int LastInputHookCycles { get; private set; }

        /// <summary>$C162–$C1B9: strobe the pads, latch held into $16 and its edge into $14.
        /// Bank 7's resident loop ($C9AC/$C792) polls at the head of its own iteration, so the
        /// title/dialog/map/mission-load frames poll too, not only the world dispatcher.</summary>
        private void PollInput(in NesButtons pad1)
        {
            InputTiming.PollResult input = InputTiming.Poll(pad1.ToByte(), 0, _prevHeld, 0);
            PressedP1 = input.PressedP1;
            _prevHeld = input.HeldP1;
            LastInputHookCycles = input.HookCycles;
        }
        private System.Func<int, byte> _gridByte, _flagByte;   // cached ($32)/($34) lookups for $DA70
        // The pause phase lives in DarkwingGame.Pause.cs (bank 4 $8F00…$9025).
        private bool _parityStepped;
        private bool _oamPublished;

        /// <summary>The NMI's <c>$0200→OAM</c> DMA: exactly once per frame. The resume frame
        /// reaches it twice (the pause loop's tail and the $CA43 tail), and a second copy would
        /// expose the pause-loop composition as the displayed frame instead of the pre-frame
        /// shadow (gold_pause_resume f45: 79/256 bytes).</summary>
        private void PublishToOamOnce()
        {
            if (_oamPublished || _blankDepth != 0 || Oam == null) return;
            _oamPublished = true;
            Oam.PublishToOam();
        }

        /// <summary>$8000 <c>INC $95</c> — once per frame, not once per walk. The resume frame
        /// reaches $F89A twice (the pause loop's iteration and the $CA43 tail), and a second
        /// increment leaves $95 one ahead of the ROM from that frame on and flips the walk
        /// direction for every later frame (gold_pause_resume: port 57 vs ROM 56 at f45,
        /// then 64/256 OAM bytes to the end of the capture).</summary>
        private void StepFrameParity()
        {
            if (_parityStepped) return;
            _parityStepped = true;
            FrameParity++;
        }

        /// <summary>The flow that owns the bank-7 phase of the frame. Set by GameFlow; a direct
        /// Core caller that supplies its own flow must use <see cref="AdvanceFrame"/> so the
        /// NMI still runs before the flow body.</summary>
        public GameFlow Flow { get; set; }

        /// <summary>Advance one complete frame in ROM order: NMI ($C0A1), the bank-7 flow phase,
        /// then the main-loop body. This is the host/replay entry point.</summary>
        public void AdvanceFrame(in NesButtons pad1, in NesButtons pad2 = default)
        {
            BeginNmi();
            BeginNativeVramBody();
            if (Flow != null) Flow.Step(in pad1);
            StepBody(in pad1, in pad2);
        }

        /// <summary>Advance the deterministic simulation by exactly one frame. Kept for direct
        /// Core tests that drive GameFlow themselves; hosts should call <see cref="AdvanceFrame"/>.</summary>
        public void Step(in NesButtons pad1, in NesButtons pad2 = default)
        {
            BeginNmi();
            BeginNativeVramBody();
            StepBody(in pad1, in pad2);
        }

        /// <summary>$C0A1 NMI: frame latches, CHR/palette/text uploads, then the main body.</summary>
        private void BeginNmi()
        {
            _parityStepped = false;
            _oamPublished = false;
            if ((Ppu.Ctrl & 0x80) == 0)
            {
                LastNmiAudioServiceCycles = 0;
                NmiAudioTimingSupported = true;
                return;
            }
            Flow?.BeginResetNmiTiming(_fadeFrame, _fadeLevel, _paletteSrc);
            PublishNativeVram(0, false);
            NmiCounter++;
            // $C006: LDA $F0 / ORA $9A / BEQ $C00F / JMP $C0A1 — a blanked frame keeps the last
            // latched picture, and the $18/$19/$1A/$1B/$1C/$1D latches stay armed for the next
            // NMI that is allowed to publish.
            if (_blankDepth == 0)
            {
                PublishToOamOnce();
                NmiChrUpload();            // $C047/$C050, before $C059 palette upload
                NmiPaletteUpload();        // $C059: $18-flagged $0300 → $3F00, every frame
                Text?.FlushNmiWrites();
                Ppu.SetScroll(Player.ScrollX, Player.ScrollY);
                if (_nmiMaskRequest.HasValue)
                {
                    Ppu.WriteRegister(0x2001, _nmiMaskRequest.Value);
                    _nmiMaskRequest = null;
                }
            }
            DrainAudioQueue();             // $C0DC/$C0F6: runs on blanked frames too
            Flow?.CompleteResetNmiTiming();
        }

        private void StepBody(in NesButtons pad1, in NesButtons pad2)
        {
            if (ResidentProcess != null)
            {
                if (Flow == null || !Flow.PowerOnActive) PollInput(in pad1);
                PublishToOamOnce();
                ResidentProcess(pad1);
                if (ResidentProcess != null || !WorldRunning)
                {
                    Ppu.Render(Framebuffer);
                    Frame++;
                    return;
                }
                // $C8B5/$DD57 RTS-es into $C820 and the SAME $C9AC iteration.
                // Resume after this NMI; neither discard the player pass nor run another NMI.
            }
            if (_sectionFadeStage != 0)
            {
                if (Ppu.Mask != 0) PublishToOamOnce();
                if (_sectionFadeStage == 1)
                {
                    if (TickResidentFade())
                    {
                        Objects.ClearForSectionLoad(); Oam.HideShadow(); // $C24B/$C24E, before the yield
                        _sectionFadeStage = 2;
                    }
                }
                else
                {
                    Oam.HideShadow();                         // $C248/$C21E after the fade
                    LoadSection(_sectionRequested);
                    _sectionFadeStage = 0;
                    AdvanceSectionStage();                    // $DE86 runs inline before its first yield
                }
                Ppu.Render(Framebuffer); Frame++;
                return;
            }
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
                if (Ppu.Mask != 0) PublishToOamOnce();
                // The NMI of this frame consumes any latch set on a previous frame; do it
                // before the stage advances so the tail frame keeps $1A/$1B = $80/$90.
                ConsumeChrLatch();
                if (!AdvanceSectionStage())
                {
                    Ppu.Render(Framebuffer);
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
                else Ppu.Render(Framebuffer);
                Frame++;
                return;
            }
            PollInput(in pad1);
            byte pressed = PressedP1;
            if (!WorldRunning)
            {
                // The title/map/briefing phase is bank 7's own loop ($C792/$C9AC): no player
                // dispatcher, no bank-6 task. On the city map the frame still belongs to bank 2
                // ($A448 chooser + the $F89A walk); the title runs its own $C7AD walk. The
                // GAME OVER card is the only pure-nametable screen left, so it just takes the
                // NMI counter and the renderer.
                if (EndingScreen && Oam != null) StepEndingFrame();
                else if (MapScreen && Oam != null) StepMapFrame(pressed);
                else if (TitleScreen && Oam != null) StepTitleFrame();
                else
                {
                    FrameParity++;
                    Ppu.Render(Framebuffer);
                }
                Frame++;
                return;
            }
            if (!_pauseActive && (pressed & 0x20) != 0 && Player.LevelActive >= 0 && PauseGuardAllows(Player.State)
                && Player.GasEnergyLo != 0) ToggleSpecialWeapon(); // $C9B3-$CA14
            // $CA17…$CA3D: a fresh Start outside the four guarded $50 states plays SFX $1C and
            // calls JSR $8F00, which holds the process. Its loop ($8FB7) re-reads $14 each frame
            // after passing through $F89A's JSR $FF06 yield, so the press that OPENED the screen
            // is consumed by the entry frame and the resume needs a new edge.
            if (_pauseActive)
            {
                // $8FCD is read at the head of the $8FB7 loop, BEFORE its JSR $F89A, so the frame
                // that leaves the pause screen composes no pause records at all. The hidden OAM
                // tail proves it: gold_pause_resume f46 keeps F8+61,02,6C … F8+6D,03,68 in entries
                // 41..53, the panel block of the last real pause walk (f45's publish already showed
                // it), where one more pause frame would have left the player+plate block
                // F8+7B,00,70 … F8+CA,03,50 instead.
                if ((pressed & 0x10) == 0) { PauseFrame(); return; }
                PauseRestore();                     // $8FCD…$9025, then RTS back to $CA40
            }
            else if ((pressed & 0x10) != 0 && PauseGuardAllows(Player.State))
            {
                PauseEnter();
                PauseFrame();                       // $8FB7 runs inside the entry frame too
                return;
            }
            // $CA40 JSR $CAFC: the state table $CB41 runs only while slot 0's $04D0 bit 7 is
            // set. Once the death payload clears it ($807A from the cull, $D42F from the
            // resolve), the frame belongs to the death service $CB01 → resolve $D41B, whose
            // tail is JMP $FF06 — it never RTS-es back to $CA43, so the scroll service,
            // spawner, door check and animation engine are all skipped for that frame.
            if ((Objects.Slots[0].StateLow & 0x80) != 0)
            {
                BeginMissionHeadTiming();
                Player.Step(HeldP1, pressed);
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
            if (Player.CardPass != null && Player.CardPass.FramePending)
            {
                StepFrameParity();
                PublishToOamOnce();
                Hud.Refresh(Player);
                Player.CardPass.ContinueFrame(FrameParity);
                Player.CardPass.Finish();
                Ppu.Render(Framebuffer);
                Frame++;
                return;
            }
            if (Player.YieldOnlyFrame)
            {
                ConsumeChrLatch();
                Ppu.Render(Framebuffer);
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
            if (Player.PendingEvent != 0)
            {
                BeginSectionTransition(Player.PendingEvent);
                Ppu.Render(Framebuffer); Frame++;
                return;
            }
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

        private void NmiChrUpload()
        {
            // Resident section/death loaders retain their existing request-residency
            // schedule. Ordinary requests (card release $D565, zone service $EE26…)
            // are consumed by the next NMI, never by the frame that writes them.
            if (_sectionStage != 0 || _reloadStage != 0 || _chrPending) return;
            if (Player.ChrLatch0 != 0)
            {
                Ppu.Chr0Bank = Player.ChrLatch0 & 0x1F;
                Player.ChrLatch0 = 0;
            }
            if (Player.ChrLatch1 != 0)
            {
                Ppu.Chr1Bank = Player.ChrLatch1 & 0x1F;
                Player.ChrLatch1 = 0;
            }
        }

        private void ConsumeChrLatch()
        {
            if (!_chrPending) return;
            if (Player.ChrLatch0 != 0) Ppu.Chr0Bank = Player.ChrLatch0 & 0x1F;
            if (Player.ChrLatch1 != 0) Ppu.Chr1Bank = Player.ChrLatch1 & 0x1F;
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
            // The nametable itself is only written by the $E698 band stream (Player.StageBand)
            // and by the redraw services; this frame updates the scroll registers.
            if (TileMap != null) { TileMap.CameraY = Player.ScrollY; TileMap.SetScroll(Player.ScrollX); }
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
            StepFrameParity();                                   // $8000-$8002
            // NMI DMA: the buffer this frame displays is the shadow of the previous walk.
            PublishToOamOnce();
            // The HUD is object slot 1 ($C850/$C887), so the walk composes its 8 records at
            // whatever cursor position the parity order reaches slot 1 in — no separate writer
            // and no hard-coded entry (GAPS G44.1 closed).
            Hud.Refresh(Player);
            if (Player.IntroCardPhase) Oam.RunFrame(FrameParity);
            else
            {
                if (MissionTail == null) MissionTail = new MissionPassContinuation(Oam, Player);
                MissionTail.Begin(FrameParity);
                MissionTail.Finish();
            }
            Player.Flags = Objects.Slots[0].Flags;
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
            BeginZoneServiceTiming();
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
            BeginPaletteScriptTiming();
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
            Sections.ReadSpritePaletteByIndex(index, sp, _worldId);
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
                    RenderOn();                         // $E44E JSR $C158
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
                    if (_fadeFrame == 0)
                    {
                        // $E451 yields before $E454-$E476 can arm the entrance countdown.
                        if (Player.CheckpointId >= 2 && Player.CheckpointId <= 0x0E && (Player.CheckpointId & 1) == 0)
                            Player.IntroHoldFlag = 0x78;
                        BossBehaviors.DefeatTimer = Player.IntroHoldFlag;
                        ResidentWalk(); // $E478/$C254 engine once before $C270
                    }
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
                    // load streams 144 passes and $StageSectionPass delivers a constant 15 per
                    // frame; the ROM splits the same window 10...19 (23 in the first window) by
                    // CPU time, which is the open half of G62.
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
                    if (_fadeFrame == 0)
                    {
                        PublishToOamOnce();
                        ResidentWalk();
                    }
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

        /// <summary>Restores the resident $C270-$C2BF fade schedule — the state the $E388 tail
        /// ($E492 JMP $C254 / $E495 JMP $C25F) parks the frame body in, which is what freezes $95,
        /// the object dispatcher and the boss dispatchers for 4 frames per $0F step. $0F/$0D are
        /// readable from RAM; the cadence index lives on the process stack inside the $C2A4-$C2AE
        /// yield loop, so the caller has to pass the phase the seed frame is in.</summary>
        public void SeedResidentFadeIn(byte level, byte delta, byte cadence)
        {
            _sectionStage = 3;
            _fadeLevel = level;
            _fadeDelta = delta;
            _fadeFrame = cadence;
            _fadeExitAt50 = false;
        }

        /// <summary>Serialize all future-affecting state. Version-tagged.</summary>
        public byte[] SaveState()
        {
            using var ms = new System.IO.MemoryStream();
            using var w = new System.IO.BinaryWriter(ms);
            w.Write((int)4); // schema version
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
            w.Write(Player.LastVerticalRedraw); w.Write(Player.VerticalRedrawRow); w.Write(Player.PrevVerticalMode);
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
            if (version != 4) throw new System.InvalidOperationException($"State schema {version} unsupported");
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
            Player.LastVerticalRedraw = r.ReadByte(); Player.VerticalRedrawRow = r.ReadByte(); Player.PrevVerticalMode = r.ReadByte();
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
