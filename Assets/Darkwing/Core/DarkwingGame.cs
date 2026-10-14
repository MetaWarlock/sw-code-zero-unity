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
                Enemies = value != null ? new EnemyDispatcher(value, Objects, Player) : null;
                if (Enemies != null) Enemies.Tiles = TileMap;   // $EB52 probe backend
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
            p.BottomRow = (byte)(TileMap.GridRows - 1); // $2D
            p.ReloadRow = () =>
            {
                TileMap.LoadRow(p.ScrollYRow);
                TileMap.StreamColumns(p.ScrollX);
            };
            p.CellIndex = (byte)(p.ScrollYRow * p.RowStride + (p.ScrollX >> 8)); // $22
        }

        private SectionTable _sections;

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
        private bool _chrPending;
        private byte _fadeLevel;    // $0F fade level ($30 → $20 → $10 → $00)
        private byte _fadeDelta;    // $0D per-step delta ($F0 = fade in from black)
        private byte _fadeFrame;    // frames served inside the $C270 loop ($0E = 4 per step)
        private readonly byte[] _paletteSrc = new byte[32]; // $0320/$0330 un-faded palette
        public bool SectionLoading => _sectionStage != 0;

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
            for (int i = 0; i < 16; i++) _paletteSrc[i] = bg[i];
            for (int i = 0; i < 16; i++) _paletteSrc[16 + i] = sp[i];
            for (int i = 0; i < 32; i++) Ppu.PaletteRam[i] = _paletteSrc[i];
            // $E454-$E476: checkpoint ids $02,$04,$06,$08,$0A,$0C,$0E set the $3B intro
            // hold, which redirects the state-$12 exit to $50=$0F instead of $50=$00.
            Player.IntroHoldFlag =
                (Player.CheckpointId >= 0x02 && Player.CheckpointId <= 0x0E
                 && (Player.CheckpointId & 1) == 0) ? (byte)0x78 : (byte)0x00;
            SectionUsesInternalEngine = SectionTable.UsesInternalEngine(sectionId);
            _sectionStage = 1;
        }

        /// <summary>Sound triggers mirroring the original's $F84E/$F850 enqueue sites.</summary>
        public const int SfxJump = 0x21;
        public const int SfxLand = 0x10;
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
        public bool Paused; // Start toggles in gameplay ($CA33 flow: SFX $1C, freeze, status screen)

        /// <summary>Advance the deterministic simulation by exactly one frame.</summary>
        public void Step(in NesButtons pad1, in NesButtons pad2 = default)
        {
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
            byte held = pad1.ToByte();
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
            Player.Step(held, pressed);
            // Hook/peg column service ($DAFD) runs AFTER the player state machine:
            // the grab frame leaves $0460/$0510 untouched (gold_hook_ride2 f1) and the
            // ride's row wrap only releases $50 on the NEXT frame (f80 wrap → f81 release).
            if (TileMap != null)
            {
                // $22 = $FB*$2E + the camera's cell-within-row ($FD): recomputed every
                // frame, because the scroll crossing a 256-px boundary is what arms the
                // new cell's script byte and its placements (gold_m1_fulljump f253:
                // camera $FC/$FD $FF→$0100 moves $22 $33→$34, walker slot $0C appears).
                byte cell = (byte)(Player.ScrollYRow * Player.RowStride + (Player.ScrollX >> 8));
                int step = cell - Player.CellIndex;
                if (step == 1 || step == -1)
                    Player.CellIndexNext = (byte)(cell + step);   // $DD2E/$DD34 ±1 table
                Player.CellIndex = cell;
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
            s0.Active = true;
            s0.Flags = (byte)(0x90 | (Player.FacingRight != 0 ? 0x40 : 0) | 0x10);
            s0.X = Player.X;
            s0.Y = Player.Y;
            Hud.Compose(3);
            // Spawner + cull ($8090/$802F): placements activate as their cell scrolls in.
            TileMap?.StreamColumns(Player.ScrollX);
            if (CurrentLevel != null)
            {
                // Spawner ($8090); culling happens per-slot in OamDispatcher
                // ($802F semantics: ±16 screens, onscreen bit at high==0).
                Objects.SpawnFromPlacements(CurrentLevel, Player.ScrollX,
                    (ushort)((Player.ScrollYRow << 8) | Player.ScrollY));
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
                    if (Player.StageSectionPass()) _sectionStage = 2;
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
                    _sectionStage = 3;
                    return false;
                default:
                    if ((_fadeFrame & 3) == 0)
                    {
                        _fadeLevel = (byte)(_fadeLevel + _fadeDelta);   // $C2B0-$C2B5
                        if ((_fadeLevel & 0x80) != 0)                   // $C2BB BPL $C285
                        {
                            _sectionStage = 0;
                            return true;
                        }
                        ApplyFadeRamp();                                // $C285-$C2A0
                    }
                    _fadeFrame++;
                    return false;
            }
        }

        /// <summary>$C285-$C2A0: palette = $0320 source − fade level, clamped to $0F.</summary>
        private void ApplyFadeRamp()
        {
            for (int i = 0; i < 32; i++)
            {
                int v = _paletteSrc[i] - _fadeLevel;
                Ppu.PaletteRam[i] = (byte)(v < 0x0F ? 0x0F : v);
            }
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
