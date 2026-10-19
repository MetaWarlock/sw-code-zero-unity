using System;

namespace Darkwing.Core
{
    /// <summary>
    /// Faithful transcription of the original player physics (fixed bank routines
    /// documented in Research/DarkwingDuck/docs/player_mechanics.md).
    /// Byte-level semantics preserved: 16.8 positions, signed 8.8 velocities,
    /// 6502-style signed compares and wraps. Vertical velocity is up-positive
    /// (integration subtracts it from Y, mirroring $F5A4/$F5DA).
    /// </summary>
    public sealed class PlayerCore
    {
        // --- RAM-equivalent state (original addresses in comments) ---
        public byte State = 0;          // $50
        public byte StateTimer;         // $51
        public byte DirBits;            // $52 (latched $16&3)
        public byte WeaponActive;       // $53
        public byte PressedThisFrame { get; private set; } // $14, read by carried-object release
        public byte WeaponSelected;     // $54
        public byte AirSubState;        // $56
        public byte SavedState;         // $58
        public byte HopTimer;           // $5A
        public byte PendingEvent;       // $5B
        public byte LandedFlag;         // $5C
        public byte ContinueCount;      // $5D
        public byte GasActive;          // $5F
        public byte GasEnergyHi, GasEnergyLo; // $60/$61 saved
        public byte DeathFlag;          // $64
        public sbyte LevelActive = -1;  // $65
        public byte CheckpointId;       // $6D
        public byte InvulnFrames;       // $7C
        public byte GasToggle;          // $98
        public byte GravityFrac = 0x40; // $99
        public byte WallBlocked;        // $9E
        public bool OnHookTileFlag;     // $0400 bit5

        public ushort X = 0x2C;         // $0500/$04F0
        public byte XFrac;              // $0470
        public ushort Y = 0x35F;        // $0510/$0520
        public byte YFrac;              // $0460
        public sbyte YVelInt;           // $0560 (up-positive)
        public byte YVelFrac;           // $0550
        public byte XVelInt = 1;        // $0540
        public byte XVelFrac = 0x80;    // $0530

        public ushort PrevX;            // $6E/$6F
        public ushort ScrollX;          // $FC/$FD
        public ushort PrevPlayerX;      // $6E/$6F (player X as of the last scroll service)
        public byte ScrollYRow = 3;     // $FB (world grid row of the camera window)
        public byte ScrollY;            // $FA (intra-row vertical scroll, 0..$EF, 3 px/frame steps)
        public byte CellIndex;          // $22 (linear cell index = ScrollYRow*GridWidth + camera column)
        public byte CellIndexNext;      // $23 (incoming cell during horizontal scroll)
        public byte RowStride = 17;     // $2E (linear index step for one grid row)
        public byte BottomRow;          // $2D (lowest row the camera may scroll down into)
        /// <summary>$10 — ONE shared scratch byte with several owners, so its value only
        /// means something at the place that reads it: the bank-7 scroll services write the
        /// px/frame here ($DB2F/$DB41/$DB4E/$DB64/$DBC5/$DBE7/$DC39) and read it back in the
        /// same call ($DBF1/$DC3E/$DC8B/$DCE0), the column streamer masks it into the
        /// attribute byte ($E754/$E759 AND $E7C2,Y), $E388 parks the section-table index in
        /// it across JSR $C248, and the bank-5/4 object walk rewrites it as the flip bit of
        /// the last slot it advanced ($80F4-$80F9 = $0400,X AND #$40, mirrored at $98F4).
        /// The walk runs last in the frame ($CABB), so a golden shows $10 ∈ {00,40} at frame
        /// end, and RunFrame now reproduces that write. Measured by PhysCompare.Suite: clean in
        /// b_attack/contact/m1route/m2/m7, 59/119 walk, 149/298 hook, 547/599 traverse,
        /// 199-303/371 falls, 977/1499 m6 — the leftovers are frames where the port advanced
        /// no slot at all while the ROM advanced one with bit 6 set, so the field is now a
        /// slot-table-agreement proxy rather than a scroll-step bug (see GAPS G61).</summary>
        public byte ScrollStep;
        public byte ScrollMode;         // $2F (0 none, 4 down ride, 8 up ride)
        public byte ScrollState;        // $2B (bits 0-1 horizontal scroll from $20, bits 2-3 vertical armed)

        public byte FacingRight = 1;    // $0400 bit6
        public byte Flags = 0xD0;       // $0400 (active|facing)

        // --- RAM-equivalent state used by the enemy/boss port (addresses in comments) ---
        public byte AreaId;             // $31 (world/area index)
        public byte PendingWeaponIcon;  // $05BA HUD icon code from weapon pickups
        public byte PendingScreenEffect;// $1A screen-effect trigger ($8A/$8B/$88/$FF)
        public byte PendingSpawnType;   // $042E boss drop request (Bushroot vines $52)
        public byte PendingMissionFlag; // $66 door mission flag ($FF/$F0)
        public readonly byte[] ScoreDigits = { 0x70,0x70,0x70,0x70,0x70 }; // $05C4/$C6/$C8/$CA/$CC
        public int ScoreUnits
        {
            get { int value=0; foreach(byte d in ScoreDigits) value=value*10+(d&15); return value; }
            set { int v=Math.Max(0,Math.Min(99999,value)); for(int i=4;i>=0;i--){ScoreDigits[i]=(byte)(0x70+v%10);v/=10;} }
        }
        public byte BonusDigits { get => (byte)(ScoreUnits%100); set => ScoreUnits=value; }
        public byte ScoreLifeState; // $6B
        public byte TilesetBank;        // $36 section metatile-set bank ($E38F)
        public byte SectionFlag3D;      // $3D palette-patch script id, bit7 = active (was G16)
        public byte PalStep3E;          // $3E frame counter inside the current $EFBE script step
        public byte PalIndex3F;         // $3F step index inside the current $EFBE script
        public byte IntroHoldFlag;      // $3B intro hold ($E476 sets $78 for even checkpoint ids)
        public byte SectionRingEvent;   // $D9 section ring/event byte ($E3BA → JSR $F84E)
        public byte ChrLatch0;          // $1A CHR request ($E3AB, applied by NMI $FF21)
        public byte ChrLatch1;          // $1B CHR request ($E3B0, $FF3B)

        private ITileMap _tiles;
        public ITileMap TileMap { get => _tiles; set => _tiles = value; }
        public ObjectSystem Objects;  // slot 0 shares the player's anim state (original RAM model)
        public System.Action<int> Sfx; // wired to DarkwingGame.PlaySfx ($F84E/$F850 sites)
        public System.Action<int> Sound; // generic $F84E, including victory music
        /// <summary>$9605: the mission intro card owns the OAM page while it is on screen, so the
        /// card's blit ($961D JSR $9636) and the cursor prime ($9607 STY $97 / JSR $F89A) both act
        /// on the dispatcher. Null until the host wires it.</summary>
        public OamDispatcher Oam;
        public System.Action ResetHud;
        public System.Action RestoreHud;
        private byte _dropSupportTimer; // $9D in $D590-$D625
        /// <summary>Raw PRG bank 2, mapped at $8000. <see cref="OamDispatcher.BlitMissionCard"/> reads
        /// the card's table quartets from it, so a missing bank disables the blit rather than faulting.</summary>
        public byte[] PrgBank2;
        private readonly byte[] _probeX = new byte[64];  // $06 = last sampled X ($EB52 uses $06/$07)
        private readonly byte[] _probeY = new byte[64];
        // $43 = the class of the MIDDLE probe point of the last $EB52 scan ($EB6B
        // STA $0042,Y with $41 counting 2,1,0, so $44/$43/$42 = probes $40/$40+1/$40+2).
        // The rising branch's $F44C/$F461 gates read it; every probe service rewrites it.
        private byte _probe43;
        // $5C — the $F322 latch set by $F360 when a fall passes into a type-1/type-2 tile.
        // While it is non-zero every falling frame enters at $F366 (hook check) instead of
        // the plain $F326 floor probe, so the pass-through is not re-caught next frame.
        private byte _hookLatch;

        public PlayerCore(ITileMap tiles) { _tiles = tiles; }

        // ---------- 8.8 signed helpers ----------
        private static int VelToInt(byte frac, sbyte integ) => (integ << 8) | frac;

        /// <summary>Integrate X by the signed 8.8 velocity ($F4FD add / $F51B subtract).</summary>
        private void IntegrateX(int sign)
        {
            int v = (XVelInt << 8) | XVelFrac;          // 1/256 px units
            int acc = (X << 8) | XFrac;                 // position as 16.8
            acc += sign * v;
            X = (ushort)((acc >> 8) & 0xFFFF);
            XFrac = (byte)(acc & 0xFF);
        }

        /// <summary>
        /// $F5A4 byte-exact: $0460 -= $0550, then $0510 -= $0560 WITH the fraction borrow, and
        /// the borrow OUT of $0510 is discarded — the row byte $0520 only ever changes through
        /// the 240 px wrap ($F5B7 CMP #$F0, $F5BB ADC #$0F with C=1 i.e. +$10, $F5C3 ADC #$00
        /// carries that overflow into $0520). A plain 16.8 accumulator loses the wrap: at
        /// gold_w05 f656 the ROM goes $00EE → $0105 (+$17, not +$7) and that extra row is what
        /// makes $8057 see a row difference of 1 and kill the player on that exact frame.
        /// </summary>
        private void IntegrateY()
        {
            int frac = YFrac - YVelFrac;                            // $F5A4-$F5AB
            bool borrow = frac < 0;
            YFrac = (byte)frac;
            int lo = ((Y & 0xFF) - (YVelInt & 0xFF) - (borrow ? 1 : 0)) & 0xFF;  // $F5AE-$F5B4
            int hi = Y & 0xFF00;
            if (lo >= 0xF0)
            {
                int wrapped = lo + 0x0F + 1;                        // $F5BB (C=1 from the CMP)
                if ((wrapped & 0x100) != 0) hi = (hi + 0x100) & 0xFFFF;   // $F5C0-$F5C5
                lo = wrapped & 0xFF;
            }
            Y = (ushort)(hi | lo);
        }

        /// <summary>Gravity $F5DA: velocity -= $99 (0.25 px/frame), then the fall saturates.</summary>
        public void ApplyGravity()
        {
            int v = VelToInt(YVelFrac, YVelInt) - GravityFrac;   // $F5DA-$F5E8 (byte SBC + borrow)
            byte hi = (byte)((v >> 8) & 0xFF);
            // $F5EB-$F5F8: a negative high byte is left alone only while it is >= $F9; any
            // value in $80-$F8 is rewritten to exactly $F8,$00. So the cap is 8.00 px/frame
            // and it lands on a clean $F8:$00 — a 16-bit "<= -$0800" test would let $F8:$EB
            // through, which is what desynchronised the world-entry fall at f371.
            if (hi >= 0x80 && hi < 0xF9) { YVelInt = -8; YVelFrac = 0x00; return; }
            YVelInt = (sbyte)hi;
            YVelFrac = (byte)(v & 0xFF);
        }

        /// <summary>Jump cut $F5FC/$F607: velocity = -0.332 (down).</summary>
        public void CutVelocity()
        {
            YVelInt = -1;      // $FF
            YVelFrac = 0xAB;
        }

        /// <summary>Terminal clamp while rising is none; cap handled in gravity.</summary>
        private static bool Rising(int vel) => vel > 0;

        // ---------- Probes ($EB52/$EBE8) ----------
        // Probe sets from the doc: sides ±12 at Y -11/0/+11; floor +12 at X -11/0/+11;
        // ceiling -12 at X -11/0/+11; hook set $3A above head.
        private static readonly sbyte[] FloorXOff = { -11, 0, 11 };
        private static readonly sbyte[] SideYOff = { -11, 0, 11 };
        private static readonly sbyte[] HookXOff = { -1, 0, 1 };   // $ED19[58..60]

        private int ProbeY(int offset)
        {
            // $EC16-$EC4B: each page holds 240 pixels, including the probe offset.
            // Feet at $01E4 + 12 therefore sample $0200, not the unused $01F0 strip.
            int lo = (Y & 0xFF) + offset, hi = Y & 0xFF00;
            if (lo < 0) { lo = (lo - 0x10) & 0xFF; hi = (hi - 0x100) & 0xFFFF; }
            else if (lo >= 0xF0) { lo -= 0xF0; hi = (hi + 0x100) & 0xFFFF; }
            return hi | lo;
        }

        public byte ProbeFloor()  // $40=6 → points 6-8
        {
            byte result = 0;
            for (int i = 0; i < 3; i++)
            {
                int px = (short)(X + FloorXOff[i]);
                int py = ProbeY(12);
                byte t = TileTypeAt(px, py);
                if (i == 1) _probe43 = t;         // $EB6B STA $0042,Y — средняя точка = $43
                result = Math.Max(result, t);
            }
            HandleProbeType(result);   // $EBAA tail
            return result;
        }

        public byte ProbeCeiling()
        {
            byte result = 0;
            for (int i = 0; i < 3; i++)
            {
                int px = (short)(X + FloorXOff[i]);
                int py = ProbeY(-12);
                byte t = TileTypeAt(px, py);
                if (i == 1) _probe43 = t;         // $EB6B STA $0042,Y — средняя точка = $43
                result = Math.Max(result, t);
            }
            HandleProbeType(result);   // $EBAA tail
            return result;
        }

        /// <summary>$40=$3A, $41=2 → table points 58/59/60 ($ED19/$ECAD): X-1/X/X+1 at Y-20,
        /// i.e. the row 8 px above the head. Used by the $F366 hook check and the ceiling
        /// dispatch's $F44C gate.</summary>
        public byte ProbeHook()
        {
            byte result = 0;
            for (int i = 0; i < 3; i++)
            {
                int px = (short)(X + HookXOff[i]);
                int py = ProbeY(-20);
                byte t = TileTypeAt(px, py);
                if (i == 1) _probe43 = t;         // point 59 is the middle sample
                result = Math.Max(result, t);
            }
            HandleProbeType(result);   // $EBAA tail
            return result;
        }

        public byte ProbeSide(bool right)
        {
            byte result = 0;
            int dx = right ? 12 : -12;
            for (int i = 0; i < 3; i++)
            {
                int px = (short)(X + dx);
                int py = ProbeY(SideYOff[i]);
                byte t = TileTypeAt(px, py);
                if (i == 1) _probe43 = t;         // $EB6B STA $0042,Y — средняя точка = $43
                result = Math.Max(result, t);
            }
            HandleProbeType(result);   // $EBAA tail
            return result;
        }

        private byte TileTypeAt(int wx, int wy)
        {
            // $EC4D-$EC62: the $0600 window is indexed by the Y LOW byte, so the service
            // first requires hi(probeY - $FB/$FA) == 0 and returns type 0 for any point
            // outside the camera's own 256 px page. Every observed player probe set is
            // $40=$06 (floor/ceiling) or $40=$3A (hook), i.e. index >= 6, so the $EC65
            // "clamp to the scroll row" branch is unreachable for the player and both
            // out-of-page directions read 0. Without this bound gold_m3_route f31 lands the
            // falling player on a real room tile at Y=$0111 (window byte = 3) instead of
            // letting the bank-5 cull kill him on that page crossing.
            if ((((wy - (((ScrollYRow << 8) | ScrollY))) & 0xFF00)) != 0) return 0;
            byte t = _tiles.TypeAt(wx, wy);
            if (t == 6) return 0; // $EBE3 clears 6
            return t;
        }

        /// <summary>
        /// $EBAA-$EBDA tail of the shared probe service, slot 0 branch: the maximum
        /// metatile type over the probe set is the event. Type 5 = "exit here":
        /// $5B = $6D = script byte of the cell the player is standing over
        /// ($EBC8: $0500 - $FD + $22, then ($34),Y AND #$1F). $5B is consumed by
        /// $CA6E (JSR $E388); $6D is the sticky id used for the engine fork ($E478)
        /// and the respawn after death ($D461).
        /// Type 7 sets $0400 bit5 in the original ($EBDB); the port currently raises
        /// that flag from type 8 in the hook path — see GAPS G16, left alone so the
        /// hook golden stays byte-exact.
        /// </summary>
        private void HandleProbeType(byte maxType)
        {
            if (maxType != 5) return;
            int idx = ((X >> 8) - (ScrollX >> 8) + CellIndex) & 0xFF;
            byte script = FlagAt(idx);
            PendingEvent = (byte)(script & 0x1F);   // $5B
            CheckpointId = PendingEvent;            // $6D
        }

        /// <summary>
        /// $E38F-$E3D3 + $DE24, i.e. the frame $E388 spends on the descriptor tables.
        /// $E388 does NOT touch $32/$34/$2E, so the world grid and row stride stay as
        /// loaded — only the cell/row the camera points at changes. Everything the
        /// loader writes after the $DE24 staging loop ($FA78 anim seed, $0400, the
        /// $F5FC velocity cut, $50=$12) belongs to <see cref="FinishSectionLoad"/>:
        /// gold_door_id1 shows those bytes landing 11 frames later, because $DE24 only
        /// leaves when the $E698 cursor reaches $0F and $E3ED then yields one more NMI.
        /// </summary>
        public void ApplySectionLoad(SectionTable t, int id)
        {
            TilesetBank = t.TilesetBank(id);                // $36 ($E38F)
            CellIndex = CellIndexNext = t.StartCell(id);    // $22/$23 ($E394)
            byte row = t.StartRow(id);                      // $FB/$0520 ($E39B)
            byte col = t.StartColumn(id);                   // $FD/$0500 ($E3A3)
            ScrollYRow = row;
            ScrollX = (ushort)(col << 8);                   // $FC=0 ($DE3A) + $FD
            X = (ushort)((col << 8) | 0x30);                // $0500/$04F0=$30 ($E3D1)
            Y = (ushort)((row << 8) | 0x06);                // $0520/$0510=$06 ($E3CC)
            if (Objects != null) { Objects.Slots[0].X=X; Objects.Slots[0].Y=Y; }
            ChrLatch0 = t.ChrRequest0(id);                  // $1A ($E3AB)
            ChrLatch1 = t.ChrRequest1(id);                  // $1B ($E3B0)
            SectionFlag3D = t.Flag3D(id);                   // $3D ($E3B5)
            SectionRingEvent = t.RingEvent(id);             // $D9 ($E3BA)
            PalStep3E = 0; PalIndex3F = 0;                  // $E3C2-$E3C6
            ResetLoaderState(col);
        }

        /// <summary>
        /// $DE24, the reset both loaders call ($E3E8 from $E388, DDC8 from $DD57):
        /// render off, slots cleared, the scroll/camera fields, the zeroed flags and
        /// the $DE6C-$DE78 staging-cursor seed. Shared by section and world loads
        /// because the ROM shares the routine.
        /// </summary>
        private void ResetLoaderState(int col)
        {
            ScrollY = 0;                                    // $FA ($DE3C)
            DirBits = 1;                                    // $52 ($DE2C)
            ScrollState = 1;                                // $2B ($DE30)
            ScrollMode = 1;                                 // $2F ($DE32)
            PrevScrollMode = 1;                             // $20 ($DE2E, LDA #$01 — keeps the
                                                            // $E670 flip block out of the first
                                                            // scroll pass after a reload)
            LastRedrawScroll = 0;                           // $28 ($DE3E) — without this a reload
            LastVerticalRedraw = 0; VerticalRedrawRow = 0; PrevVerticalMode = 4; // $29/$2A/$21
                                                            // keeps the death frame's redraw mark,
                                                            // inflating the first $E650 pass count
                                                            // and arming $23 early (G42)
            State = 0;                                      // $DE5A clears $50, preserves $51
            AirSubState = 0;                                // $56
            SavedState = 0;                                 // $58
            HopTimer = 0;                                   // $5A
            _dropSupportTimer = 0;                          // $9D
            LandedFlag = 0;                                 // $5C
            GasActive = 0;                                  // $5F
            DeathFlag = 0;                                  // $64
            LevelActive = 0;                                // $65 ($DE5E, A=$00 — never negative after a load)
            IntroHoldFlag = 0;                              // $3B ($DE62; $E476 may re-set it)
            GasToggle = 0;                                  // $98
            WallBlocked = 0;                                // $9E
            InvulnFrames = 0;                               // $7C ($80E9 path)
            CellGridByte = 0;                               // ($32),$22 refreshed next frame
            CellScriptByte = 0;                             // ($34),$22
            PendingEvent = 0;                               // $5B ($E3E8)
            State = 0;                                      // $50 ($DE5A, held until the tail)
            Objects?.ResetPlacementCursors();               // $DE42/$DE44

            // $DE6C-$DE78: a non-zero $23 decrements it and restarts the cursor at $0F|$70.
            bool flipped = (col & 1) != 0;
            StageCursor = (byte)(flipped ? 0x80 : 0x00);
            StageTarget = (byte)(flipped ? 0x08 : 0x88);
            if (CellIndexNext != 0)
            {
                CellIndexNext--;
                StageCursor = (byte)(StageTarget | 0x70);
            }
            // $E785 selects the (step, boundary) pair $E7BE/$E7BF indexed by $2F&2.
            bool reverse = (ScrollMode & 2) != 0;
            StageStep = (sbyte)(reverse ? 0xFF : 0x01);
            StageBoundary = (byte)(reverse ? 0x7F : 0x00);
        }

        /// <summary>
        /// $DD57 with $31 = world id: the mission descriptor ($32/$34 grid pointers and
        /// $2E row width live in the tile map, not here), $DE24, then the slot-0 seed.
        /// Unlike $E388 the world loader finishes inline — DDD4-$DDFE run in the same
        /// call — and leaves the player in state $09 (not $12) with $04D0/$0400 written
        /// only for mission worlds ($31 &lt; 7; ids 7+ are dialog scenes that keep
        /// $0400=$D8 and skip the object seed).
        /// </summary>
        public void ApplyWorldLoad(RoomDecoder.World w)
        {
            ApplyWorldPrelude(w);       // $DD5F-$DDC8, ends with the $DDCB yield
            StageSectionToEnd();        // $DE86-$DE91 drained: no frame machine here
            ApplyWorldSpawn(w);         // $DDCE-$DDFE + $DE00 render-on
        }

        /// <summary>$DD5F-$DDC8: descriptor reads, palettes, CHR requests and the $DE24
        /// render-off/streamer restart — everything $DD57 does BEFORE its $DDCB yield. The
        /// position is only half re-anchored here: the $DE24 block zeroes $0500 (X high) and
        /// $DEC8 writes $0520 (Y high), so a reload frame keeps the death frame's low bytes
        /// (gold_m1_longroute f814: X=$0094/Y=$0305 out of X=$0394/Y=$0405).</summary>
        public void ApplyWorldPrelude(RoomDecoder.World w)
        {
            TilesetBank = w.SetBank;                        // $36 ($DD76)
            CellIndex = CellIndexNext = w.StartCell;        // $22/$23 ($DD7B)
            ScrollYRow = w.StartRow;                        // $FB ($DD82)
            ScrollX = 0;                                    // $FC/$FD = 0 ($DD84/$DD89)
            X = (ushort)(X & 0xFF);                         // $0500 = 0 ($DE24 zero block)
            Y = (ushort)((w.StartRow << 8) | (Y & 0xFF));   // $0520 ($DEC8,X), $0510 still old
            ChrLatch0 = w.ChrReq0;                          // $1A ($DD99)
            ChrLatch1 = w.ChrReq1;                          // $1B ($DD9E)
            SectionFlag3D = w.Flag3D;                       // $3D ($DDB6)
            // $DDA0-$DDAA: $D9 is only rewritten (and $F84E called) when it was already
            // non-zero, so the ring event stays with the caller — see DarkwingGame.LoadWorld.
            RowStride = (byte)w.RowWidth;                   // $2E ($DD94)
            BottomRow = w.TopRow;                           // $2D ($DDB1)
            WorldId = w.Id;                                 // $31 (the $D589/$9636 index)
            State = 0;                                      // $50 ($DE24 zero block)
            ResetLoaderState(0);                            // $DDC8 JSR $DE24, $FD = 0

            // $DE86-$DE91: the loader walks its own $25 cursor to $0F and steps $23 with
            // it. One JSR $E698 = one pass ($05 = 1 at $DE86-$DE88), and $DE91 branches back
            // until $25 == $0F. The loop is INLINE inside $DE24 and never yields — $E7A8's
            // $F0 test is the render-off nesting counter, so the pass leaves through the
            // $C115 queue flush, not through a frame wait. The frame split that the reload
            // captures show is CPU-time only (9-17 passes/frame), which $StageSectionPass
            // approximates with a constant; see StagePassesPerFrame and G62.
            StageSectionPass();
        }

        /// <summary>Drains the $DE86-$DE91 staging loop in one call — the ROM-faithful shape,
        /// since the loop is inline in $DE24 with no yield. The host's inline first load uses
        /// this; the death reload keeps per-frame batches only to match frame-stamped captures
        /// (G62).</summary>
        public void StageSectionToEnd()
        {
            for (int guard = 0; guard < 256 && !StageSectionPass(); guard++) { }
        }

        /// <summary>$DDCE-$DDFE (after the $DDCB yield): the slot-0 seed, the entry pose and
        /// the state-$09 release. $31 &gt;= 7 (dialog scenes) jumps to $DE09 and keeps
        /// $0400=$D8 without arming the object.</summary>
        public void ApplyWorldSpawn(RoomDecoder.World w)
        {
            X = 0x2C;                                       // $04F0 ($DDDB LDA #$2C)
            Y = (ushort)((ScrollYRow << 8) | 0x06);         // $0510 ($DDE3 LDA #$06)
            if (Objects != null)
            {
                var s0 = Objects.Slots[0];
                s0.Behavior = 0;                            // $04E0 ($DDD6, X=$00)
                s0.AnimNumber = 0x2C;                       // $0420 ($DDDE JSR $FA78, A=$2C)
                s0.AnimTimer = 0;                           // $0430
                s0.AnimFrame = 0;                           // $0410
                s0.X = X; s0.Y = Y;                         // shared $04F0/$0510 coordinate storage
                if (w.Id >= 7)
                {
                    if (w.Id == 8 || w.Id == 10) Flags = s0.Flags = 0xD8; // $DE09-$DE1B
                    return;
                }
                s0.StateLow = 0x80;                         // $04D0 ($DDF1, $31 < 7)
                s0.Active = true;
                s0.Flags = 0xD0;
            }
            Flags = 0xD0;                                   // $0400 ($DDF6, $31 < 7)
            FacingRight = 0x40;
            CutVelocity();                                  // $F5FC → FF,AB ($DDF9)
            State = 0x09;                                   // $50 ($DDFE) — map entry, not the $12 exit
            _matLanded = 0;                                 // $04D0 & $0F ($DDF1 seeds $80)
            _cardWait = 0;                                  // $9600 has not run yet
            _cardPhase = false;
            // $DE03 JSR $FF06 (1 frame) + $DE06 JMP $C254 → $C270 palette fade: $0F=$30
            // stepped by $0D=$F0 with $0E=$04 yields per round ($C2A4-$C2B0) = 30/20/10/00,
            // exiting when $0F goes negative (it reads $F0 from the release frame on).
            // $CAFC/$CB41 never runs inside that window: gold_w00..w06 all show $50=$09 at
            // f328, $0530/$0540 (written every dispatch by $CB43/$CB4A) first appear at f345.
            EntryFreezeFrames = 17;
        }

        /// <summary>
        /// Frames the main loop must skip after a $DD57 world load: $DE03/$C270 spends
        /// 17 NMIs in the loader's tail, so no input, player or world service runs.
        /// </summary>
        public int EntryFreezeFrames;

        /// <summary>$31 — the world id the current $DD57 load came from (missions 1-7 = 0-6).</summary>
        public byte WorldId;

        /// <summary>$D589[$31] — the CHR0 request ($1A) $D560 writes when the intro card releases.</summary>
        public byte CardReleaseChr;

        public byte StageCursor;                            // $25
        public byte StageTarget;                            // $0F
        public byte StageBoundary;                          // $E7BF,X
        public sbyte StageStep;                             // $E7BE,X

        /// <summary>
        /// $E698 passes per frame — a CYCLE-RATE APPROXIMATION, not a ROM mechanism (G62).
        /// $DE24 opens with `JSR $C14E` (INC $F0, $2001=0) and contains the whole
        /// $DE86…$DE91 loop, so the staging window is ONE inline block that never yields:
        /// $E698's tail (`$E7A1 DEC $05` / `BEQ $E7A8` / `$E7A8 LDA $F0` / `BEQ $E7AF` /
        /// `JMP $C115`) always takes the JMP while $F0 is non-zero, so every staging pass
        /// leaves through the VRAM-queue driver $C115, which RTS-es at $C139 and thereby
        /// skips `INC $19` and the $E7B1/$E7B3 `STA $28`. $F0 is the render-off nesting
        /// counter ($C14E INC / $C158 DEC), NOT a CPU-time flag — the previous note here
        /// said the loop yields at $E7AC "when $F0 says the frame's CPU time is gone", and
        /// that is wrong. The frame split is only how many passes fit in a frame's CPU time,
        /// and a pass's cost is dominated by the $C115 flush length, which depends on the
        /// column data just staged (gold: 9-17 passes/frame, and only the window's first
        /// frame is short). 15 is fitted to gold_door_id1's frame count; the residual cursor
        /// phase is tracked as G62.
        /// </summary>
        public const int StagePassesPerFrame = 15;

        /// <summary>
        /// One frame of the $DE86/$DE8A/$DE8D loop: $E698 rebuilds the next four VRAM
        /// row records and advances $25 by $E7BE,X ($E78A-$E790); every time $25&$7F
        /// hits $E7BF,X it also steps $23 ($E792-$E79F). Returns true when $25 reaches
        /// $0F, which is what lets $E388 continue to the $FF06 yield.
        /// </summary>
        public bool StageSectionPass()
        {
            for (int i = 0; i < StagePassesPerFrame; i++)
            {
                StageCursor = (byte)(StageCursor + StageStep);
                if ((StageCursor & 0x7F) == StageBoundary)
                    CellIndexNext = (byte)(CellIndexNext + StageStep);
                if (StageCursor == StageTarget) return true;
            }
            return false;
        }

        public byte LastRedrawScroll;    // $28 (scroll X at the end of the last redraw)
        public byte ColsToStream;        // $1F (column passes queued by the driver)
        public byte PrevScrollMode;      // $20 ($2F as of the last queued pass)
        public byte LastVerticalRedraw;  // $29
        public byte VerticalRedrawRow;   // $2A (tile row cursor, 0..29)
        public byte PrevVerticalMode = 4;// $21

        /// <summary>$E7C6/$E936: vertical stream cursors, including incoming-room changes.
        /// $E936 can run twice on a direction change at row 0, before any row is drawn.</summary>
        public void VerticalStreamPass()
        {
            int d = (sbyte)(byte)(ScrollY - LastVerticalRedraw);
            int passes = (Math.Abs(d) & 15) >> 3;
            if (passes == 0) return;
            bool down = (ScrollMode & 4) != 0;
            int step = down ? 1 : -1;
            void NextRoom() => CellIndexNext = (byte)(CellIndexNext + ((ScrollMode & 8) != 0 ? -RowStride : RowStride));
            if (ScrollMode != PrevVerticalMode)
            {
                int row = (byte)(VerticalRedrawRow + step);
                if (row >= 30) { row = (byte)(row + (down ? 2 : -2)) & 31; NextRoom(); }
                VerticalRedrawRow = (byte)row;
                NextRoom();
            }
            PrevVerticalMode = ScrollMode;
            while (passes-- > 0)
            {
                int row = (byte)(VerticalRedrawRow + step);
                if (row >= 30) { NextRoom(); row = down ? 0 : 29; }
                VerticalRedrawRow = (byte)row;
                int last = (byte)(LastVerticalRedraw + (down ? 8 : -8));
                if (last >= 240) last = (byte)(last + (down ? 16 : -16));
                LastVerticalRedraw = (byte)last;
            }
        }

        /// <summary>$E7BA — the $23 step used when the direction byte changes, indexed by
        /// ($2F&amp;2) plus one more when the $25 bit7 flips.</summary>
        private static readonly byte[] CellStepOnFlip = { 0x02, 0x01, 0xFE, 0xFF };

        /// <summary>
        /// $E63A-$E698: the column-stream driver, JSR'd by the horizontal scroll service
        /// ($DC14/$DC67) every frame the camera moves. It converts this frame's X delta
        /// into a number of $E698 passes ($1F = (|$FC−$28| + $28 bit0) &gt;&gt; 1, so one
        /// pass = 2 px of scroll), then runs the $E785 per-pass step: $25 counts passes
        /// and $23 takes a step whenever $25&amp;$7F hits $E7BF,X — 128 passes = 256 px =
        /// exactly one cell, which is why $23 (the incoming cell) is armed one screen
        /// before the scroll reaches it (gold_m1_fulljump: $25 $FF→$00 at f243 moves
        /// $23 $34→$35 while $22 is still $33; $22 only follows at the page crossing f253).
        /// </summary>
        public void ColumnStreamPass()
        {
            int fc = ScrollX & 0xFF;
            int delta = (fc - LastRedrawScroll) & 0xFF;              // $E63C-$E640 (SEC/SBC)
            if (delta >= 0x80) delta = (256 - delta) & 0xFF;         // $E643-$E646 two's complement
            int x = ScrollMode & 2;                                 // $E64A-$E64E
            int passes = x == 0
                ? (((LastRedrawScroll & 1)) + delta) >> 1           // $E650-$E657
                : (((LastRedrawScroll ^ 0xFF) & 1) + delta) >> 1;   // $E65D-$E666
            if (passes == 0) return;                                // $E65A/$E669
            ColsToStream = (byte)passes;                            // $E66C-$E66E
            if (ScrollMode != PrevScrollMode)                       // $E670-$E676
            {
                byte bit7 = (byte)(StageCursor & 0x80);             // $E67B-$E680
                StageCursor = (byte)(StageCursor + (x == 0 ? 0x91 : 0x6F)); // $E684 / $E7B6
                int k = ((StageCursor & 0x80) == bit7) ? x : x + 1; // $E689-$E68F
                CellIndexNext = (byte)(CellIndexNext + CellStepOnFlip[k]); // $E690-$E696
            }
            PrevScrollMode = ScrollMode;                            // $E674
            sbyte step = (sbyte)(x == 0 ? 0x01 : 0xFF);              // $E7BE,X
            byte boundary = (byte)(x == 0 ? 0x00 : 0x7F);           // $E7BF,X
            for (int i = 0; i < passes; i++)                        // $E698 → $E7A5
            {
                StageCursor = (byte)(StageCursor + step);           // $E78A-$E790
                if ((StageCursor & 0x7F) == boundary)
                    CellIndexNext = (byte)(CellIndexNext + step);   // $E792-$E79F
            }
            LastRedrawScroll = (byte)fc;                            // $E7B1-$E7B3
        }

        /// <summary>
        /// $E3F0-$E406, written on the frame after the staging loop exits: the $FA78
        /// anim seed ($0420=$2C, $0430/$0410=0), $04D0=$80, $0400=$D0, the $F5FC
        /// velocity cut and the spawn state $50=$12. $E408's $C850 seed-array branch is
        /// gated by $5D: $E40A BEQ $E43D skips $C850 when ContinueCount is 0 (a normal
        /// door load does NOT heal); when ContinueCount is non-zero the door load is a
        /// continue after game over and the ROM's $C850 full-heal runs. $54's sub-branch
        /// into $05BA/$05BC seeds is not yet derived, so only the heal is ported.
        /// </summary>
        public void FinishSectionLoad()
        {
            _matLanded = 0;                                 // $04D0 = $80 ($E3F5): low nibble clear
            _cardWait = 0;                                  // a $12 entry never runs $9600
            _cardPhase = false;
            Flags = 0xD0;                                   // $0400 ($E3FA)
            FacingRight = 0x40;
            if (Objects != null)
            {
                var s0 = Objects.Slots[0];
                s0.AnimNumber = 0x2C;                       // $0420 ($FA78, A=$2C)
                s0.AnimTimer = 0;                           // $0430
                s0.AnimFrame = 0;                           // $0410
                s0.StateLow = 0x80;                         // $04D0=$80 ($E3F5) — arms the $CAFC gate
                s0.Active = true;
                s0.X = X; s0.Y = Y; s0.Flags = Flags;
            }
            CutVelocity();                                  // $F5FC → FF,AB ($E401)
            State = 0x12;                                   // $50 ($E406)
            if (ContinueCount != 0) HitLadder = 0;          // $E40A BEQ $E43D / $E40C JSR $C850
            if (ContinueCount != 0) ResetHud?.Invoke();
            else RestoreHud?.Invoke();                    // $E43D JSR $C887, keeps the heart
            ContinueCount = WeaponActive = 0;             // $E448-$E44C
            ActiveGasEnergyHi = ActiveGasEnergyLo = 0;     // $E440/$E442/$E445
        }

        // ---------- Camera ($DA70/$DC19/$DBCE) ----------
        /// <summary>
        /// $DA70-$DC6D: the horizontal scroll service. Unlike a dead-zone follow, the
        /// step is the player's own displacement since the previous call ($CA5C/$CA64
        /// snapshots $0500/$04F0 into $6F/$6E), and $00 = X_lo − $FC_lo only gates
        /// entry: ≥ $89 opens the right scroll ($DBCE adds the step, forced to 1 px when
        /// it exceeds 6), ≤ $87 opens the left one ($DC19 subtracts it, capped at 6).
        /// The camera therefore tracks the player 1:1, which is why gold_m1_fulljump
        /// holds screen X at exactly $88 across walk, air and landing. $DBCE/$DC19 return
        /// SEC when the scroll happened and CLC when it was refused, and $DAE4/$DAB3
        /// <c>BCS $DAFD</c> leaves on SEC — so $2F is restored AND the $DAE8/$DAB7 player
        /// clamp runs only on the REFUSED path (gold_hurt_a41 f174: a +$6B displacement
        /// caps to 1 px, the scroll succeeds, and the ROM leaves X at $17F).
        /// </summary>
        public void HorizontalScroll(System.Func<int, byte> gridByte, System.Func<int, byte> flagByte)
        {
            if (State == 0x07 || State == 0x08 || State == 0x12)   // $DA70-$DA7D camera lock
            {
                PrevPlayerX = X;
                return;
            }
            byte ahead = (byte)(X - ScrollX);                       // $00 ($DA7F-$DA85)
            int moved = X - PrevPlayerX;                            // $04 + high byte ($DA87-$DA96)
            try
            {
            if (moved == 0) return;
            if (moved > 0)
            {
                if (ahead < 0x89) return;                           // $DAD1-$DAD3
                byte step = (byte)moved;                            // $DAD5-$DAD7
                byte saved = ScrollMode;
                ScrollMode = 1;                                     // $DADC-$DADE
                if (!ScrollRight(step, gridByte, flagByte))
                {
                    ScrollMode = saved;                             // $DAE3-$DAE6 (PLA + STA $2F)
                    if (ahead >= 0xF1) X = (ushort)(ScrollX + 0xF0); // $DAE8-$DAFA clamp: camera refused
                }                                                   // $DAE4 BCS $DAFD: a scroll that SUCCEEDS
            }                                                       // skips the clamp entirely
            else
            {
                if (ahead >= 0x88) return;                          // $DAA2-$DAA6
                byte step = (byte)(-(moved & 0xFF));                // $DA9A-$DAA0 two's complement
                byte saved = ScrollMode;
                ScrollMode = 2;                                     // $DAAB-$DAAD
                if (!ScrollLeft(step, gridByte, flagByte))
                {
                    ScrollMode = saved;                                 // $DAB2-$DAB5
                    if (ahead < 0x10) X = (ushort)(ScrollX + 0x10);     // $DAB7-$DAC9 clamp: camera refused
                }                                                       // $DAB3 BCS $DAFD: a scroll that SUCCEEDS
            }                                                           // skips the clamp entirely
            }
            finally
            {
                // $CA5C/$CA64 save the position AFTER $DA70 has clamped the player.
                // Saving the attempted position lets alternate frames escape the clamp.
                PrevPlayerX = X;
            }
        }

        /// <summary>$DC19-$DC6B: scroll left. Returns the carry; false = CLC (no scroll,
        /// $2F restored by the caller). On rejection at the level edge the ROM pins $FC to
        /// 0 but still returns SEC, so $2F stays latched at 2.</summary>
        private bool ScrollLeft(byte step, System.Func<int, byte> gridByte, System.Func<int, byte> flagByte)
        {
            if ((flagByte(CellIndex) & 0x40) == 0) return false;         // $DC1B-$DC1F
            if ((byte)ScrollX == 0)                                       // $DC21-$DC2C
            {
                if (gridByte(CellIndex - 1) == 0) return false;
                if ((byte)(ScrollX >> 8) == 0) return false;
            }
            if (!ArmHorizontal()) return false;                           // $DC2E-$DC31
            if (step > 6) step = 6;                                       // $DC33-$DC39
            int lo = (byte)ScrollX - step;                                // $DC3B-$DC40
            bool borrow = lo < 0;
            byte newHi = (byte)((byte)(ScrollX >> 8) - (borrow ? 1 : 0));  // $0E ($DC43-$DC47)
            byte newCell = (byte)(CellIndex - (borrow ? 1 : 0));           // $0F ($DC4A-$DC4E):
                                                                            // the borrow comes from
                                                                            // the LOW byte via PHP/PLP
            if (gridByte(newCell) != 0 && (newHi & 0x80) == 0)              // $DC50-$DC57
            {
                ScrollX = (ushort)((newHi << 8) | (byte)lo);
                CellIndex = newCell;
            }
            else
                ScrollX = (ushort)((ScrollX >> 8) << 8);                    // $DC59-$DC5B pin $FC = 0
            ColumnStreamPass();                                             // $DC67
            return true;
        }

        /// <summary>$DBCE-$DC18: scroll right, the mirror of ScrollLeft.</summary>
        private bool ScrollRight(byte step, System.Func<int, byte> gridByte, System.Func<int, byte> flagByte)
        {
            if ((flagByte(CellIndex + 1) & 0x40) == 0) return false;       // $DBCE-$DBD5
            if ((byte)ScrollX == 0 && gridByte(CellIndex + 1) == 0) return false;   // $DBD7-$DBDD
            if (step > 6) step = 1;                                         // $DBDF-$DBE7
            if (!ArmHorizontal()) return false;                             // $DBE9-$DBEC
            bool carry = (byte)ScrollX + step > 0xFF;                       // $DBEE-$DBF3
            ScrollX = (ushort)(ScrollX + step);
            if (carry) CellIndex = (byte)(CellIndex + 1);                   // $DBFD-$DC01
            if (gridByte(CellIndex + 1) != 0) { ColumnStreamPass(); return true; }   // $DC03-$DC09
            ScrollX = (ushort)((ScrollX >> 8) << 8);                        // $DC0B pin $FC = 0
            ColumnStreamPass();
            return true;
        }

        /// <summary>$DD17-$DD31: arm the incoming cell for a horizontal scroll. A vertical
        /// scroll in progress ($FA ≠ 0) blocks it outright; otherwise $2B takes the latched
        /// mode byte and $23 = $22 ± 1 through the $DD34 table indexed by $20 bit0
        /// (0 → −1 for a left ride, 1 → +1 for a right one).</summary>
        private bool ArmHorizontal()
        {
            if ((ScrollState & 3) != 0) return true;                        // $DD17-$DD1B
            if (ScrollY != 0) return false;                                 // $DD1D-$DD1F
            ScrollState = PrevScrollMode;                                   // $DD21-$DD23
            CellIndexNext = (byte)(CellIndex + ((PrevScrollMode & 1) == 0 ? -1 : 1));
            return true;                                                    // $DD30
        }

        // ---------- Main state dispatch ($CB41) ----------
        /// <summary>One frame of player logic. padBits: 7=A,6=B,5=Sel,4=Start,3=Up,2=Down,1=Left,0=Right.</summary>
        public void Step(byte held, byte pressed)
        {
            PressedThisFrame = pressed;
            OnHookTileFlag = false;
            PrevState = State;
            HurtOrigin = 0;
            // $CB41-$CB4A runs before every state handler and rewrites the player's X velocity
            // to +1.50 px/frame, so a state that moves sideways must set $0530/$0540 itself
            // (the death float's +0.0625 at $D39B is the clearest case).
            XVelFrac = 0x80;                                  // $CB43-$CB45
            XVelInt = 1;                                      // $CB48-$CB4A

            switch (State & 0x1F)
            {
                case 0x00: GroundState(held, pressed); break;
                case 0x01: AirState(held, pressed); break;
                case 0x02: CrouchState(held, pressed); break;
                case 0x03: LookUpState(held, pressed); break;
                case 0x06: HurtState(held, pressed); break;
                case 0x04: case 0x0D: case 0x0E: HurtAirState(held, pressed); break;
                case 0x0B: case 0x10: AirborneFullState(held, pressed); break;
                case 0x09: case 0x18: WorldEntryFallState(); break;
                case 0x0C: HangState(held, pressed); break;
                case 0x0A: ForcedHopState(); break;
                case 0x07: DeathThroesState(); break;
                case 0x08: ResolveDeath(); break;              // $08 → $D41B through the $CB5F/$CB75 table
                case 0x12: MaterializeState(); break;
                case 0x15: VictoryState(); break;              // $DA04, before $D838 owns the process
                default: break; // remaining states wired during M4/M7
            }

            // $DA70 (the scroll service) runs at $CA59, after the dispatcher returns, so it
            // sees this frame's X but is called by DarkwingGame.Step, not here.
        }

        // ---------- States 9/18: world-entry drop ($D517) ----------
        /// <summary>
        /// $D517, reached for $50 = $09 (the $DD57 mission seed) and $50 = $18 through the
        /// $CB5F/$CB75 state table. $04D0 &amp; $0F picks the phase: 0 = falling, where $D51E
        /// sends the $FA..$FF speed bytes straight to $F5A4/$F5DA and anything past $F9 runs
        /// the $D525 probe service ($F4D7 with $40=$06, $41=$02 — which integrates and applies
        /// gravity itself, so the $F800 terminal clamp still shows up in those frames). A hit
        /// requests SFX $10 and INC $04D0 ($D530-$D535). Phase 1 is $D53F: $97=$04, the audio
        /// service, bank 2, $9600 (the mission intro card, which holds $50 at $09 for ~140
        /// frames — gold_w00 releases at f513 with anim $01 and $1A=$D589[$31]=80), then
        /// $50=$00. $9600 is not ported yet (G29), so phase 1 keeps the player parked: the
        /// capture shows $0510/$0460/$0550 frozen from the landing frame through the release.
        /// </summary>
        private void WorldEntryFallState()
        {
            if (_matLanded != 0) { MissionIntroCard(); return; }   // $D51C → $D53F
            if ((byte)YVelInt < 0xFA)                       // $D51E/$D523
            {
                IntegrateY();                               // $F4DB
                ApplyGravity();                             // $F4DE
                byte hit = ProbeFloor();                    // $F4E1 → $10
                // $F4E4-$F4F6: $10 == 0 never lands; $10 >= 2 always does; a single-tile
                // hit lands only once the fall is fast enough ($0560 < $FC after gravity),
                // so a player that merely brushes a ledge keeps falling.
                if (hit >= 2 || (hit == 1 && (byte)YVelInt < 0xFC))
                {
                    int probeY = Y + 12;
                    Y = (ushort)((Y - (probeY & 0x0F)) & 0xFFFF);   // $EDD2 snap (low byte only)
                    CutVelocity();                          // $F4F6 (golden: $0550/$0560 back to FFAB)
                }
                if (hit == 0) return;                       // $D52C/$D52E
                _matLanded = 1;                             // $D535 INC $04D0
                if (Objects != null) Objects.Slots[0].StateLow++;
                Sfx?.Invoke(0x10);                          // $D530
                return;
            }
            IntegrateY();                                   // $D539
            ApplyGravity();                                 // $D53C
        }

        /// <summary>$FEFF counter used by the intro card: 0 = still spinning in the
        /// $9605 loop, $3C..1 = the release wait. Cleared on release.</summary>
        private byte _cardWait;

        /// <summary>True while $9630 spins the $FEFF yield loop: it never RTS-es back into
        /// $CA43, so the scroll service, spawner, bank-6/1 tasks and the bank-5 frame engine
        /// (and therefore $95) are skipped for the whole wait.</summary>
        public bool YieldOnlyFrame => _cardWait != 0 || _victoryWait != 0;

        private byte _victoryWait;
        private void VictoryState()
        {
            if (_victoryWait != 0)
            { if (--_victoryWait == 0) State = 0x11; return; }
            var s = Objects?.Slots[0]; if (s == null) return;
            if ((s.StateLow & 15) == 0) { SetAnimPlayer(0x12); s.StateLow++; }
            if (s.AnimNumber == 0x2D)
            {
                if (s.AnimFrame != 0x2B || s.AnimTimer != 4) return;
            }
            else if (s.AnimNumber != 0x11)
            {
                IntegrateY(); ApplyGravity();
                if (ProbeFloor() != 3) return;
                SnapFeetUpToRow(); ChrLatch0 = 0x8E; SetAnimPlayer(0x2D);
                Sound?.Invoke(2); Flags = 0xD0; FacingRight = 0x40;
                YVelFrac = 0; YVelInt = 4;
                return;
            }
            _victoryWait = 0x38; // $DA66 JSR $FEFF: 56 pure yields, no engine walk
        }

        /// <summary>True from the first $9600 visit until the $D554 release: the whole
        /// $9605-$9640 phase loop owns the process, so $CAFC never RTS-es back to the
        /// $CA75/$CA7C tail and the zone service ($EDF1) plus the palette script ($EFBE)
        /// are skipped. The engine tick still happens — each iteration ends inside $F89A —
        /// which is why this is NOT the same window as <see cref="YieldOnlyFrame"/>.</summary>
        public bool IntroCardPhase => _cardPhase;
        private bool _cardPhase;

        /// <summary>
        /// $D53F/$9600 — the mission intro card, a coroutine: one $9605 iteration per
        /// frame, each ending inside $F89A (audio service, bank 5, $8000 frame engine,
        /// then $FF06 ProcessYield), so the engine tick of frame f is what the check at
        /// the start of f+1 reads — the same order the port has (dispatcher before
        /// RunWorldServices). $960C releases on $0410 == $11, or on the animation's last
        /// frame $0410 == $0F once its timer reaches $04 ($9626); $9630 then spends
        /// 60 yield-only frames ($FEFF with X = $3C — no engine tick, which is why
        /// gold_w00 freezes $0410/$0430 at $0F/$04 for f453..f512) and $D554 releases
        /// $50 = 0 with anim $01 on the 61st (gold_w00 f513).
        /// </summary>
        private void MissionIntroCard()
        {
            var s0 = Objects != null ? Objects.Slots[0] : null;
            if (_cardWait == 0)
            {
                if (s0 == null) return;
                _cardPhase = true;                          // $9605 owns the process from here
                if (s0.AnimNumber != 0x2B)                  // $9600/$9602 first visit
                {
                    SetAnimPlayer(0x2B);
                    return;
                }
                if (s0.AnimFrame == 0x11 ||
                    (s0.AnimFrame == 0x0F && s0.AnimTimer == 0x04))
                {
                    _cardWait = 0x3C;   // $9632 JSR $FEFF
                    // $9605 primed $97 = $A0 and published, then $961D JSR $9636 filled the
                    // card's 40 OAM records below the cursor; the dispatcher holds that layout
                    // for the whole $3C-frame yield because its cursor starts at $A0.
                    if (Oam != null && PrgBank2 != null)
                    {
                        Oam.MissionCardHold = true;
                        Oam.BlitMissionCard(PrgBank2, WorldId);
                    }
                }
                return;
            }
            if (--_cardWait != 0) return;                   // $FEFF-$FF03
            _cardPhase = false;                             // $D554 RTS back to $CA43
            if (Oam != null) Oam.MissionCardHold = false;   // gameplay resumes the cursor=4 walk
            State = 0;                                      // $D556
            SetAnimPlayer(0x01);                            // $D558/$D55A
            ChrLatch0 = CardReleaseChr;                     // $D560-$D565 ($1A = $D589[$31])
            HitLadder = 0;                                  // $D55D JSR $C850
            ResetHud?.Invoke();
        }

        // ---------- State 12: materialize walk-out ($D7F9 → $F539) ----------
        private byte _matLanded; // $04D0 bits 0-3 (slot 0): floor contact marker

        /// <summary>
        /// $D517-$D588: the post-transition fall. While $04D0&amp;$0F is 0 the player only
        /// free-falls ($F5A4/$F5DA); $D51E takes the plain-physics branch while the fall
        /// speed is still in $FA..$FF, and $D525 runs the floor probe ($F4D7 with $40=$06,
        /// $41=$02) once it passes $F9 — that is why gold_door_id1 falls 24 frames from
        /// Y=$0106 before $D535 marks the landing. The next frame takes $D568: state 0,
        /// anim $01 ($FA78), velocity zeroed, or $50=$0F when $3B (the $E476 intro hold)
        /// is set.
        /// </summary>
        private void MaterializeState()
        {
            if (_matLanded == 0)
            {
                // $D51E tests the PRE-gravity speed byte, so $FA..$FF takes the plain
                // $D539 path and the floor probe only starts once the fall reaches $F9.
                if ((byte)YVelInt >= 0xFA)
                {
                    IntegrateY();                                   // $D539: JSR $F5A4
                    ApplyGravity();                                 // $D53C: JMP $F5DA
                    return;
                }
                IntegrateY();                                       // $F4DB
                ApplyGravity();                                     // $F4DE
                byte hit = ProbeFloor();                            // $F4E1 → $10
                // $F4EC: a type-1 platform is only landed on when the fall byte is < $FC.
                if (hit != 0 && (hit != 1 || (byte)YVelInt < 0xFC))
                {
                    int probeY = Y + 12;
                    Y = (ushort)((Y - (probeY & 0x0F)) & 0xFFFF);   // $EDD2 snap
                    CutVelocity();                                  // $F4F6
                }
                if (hit != 0)
                {
                    _matLanded = 1;                                 // $D535 INC $04D0
                    if (Objects != null) Objects.Slots[0].StateLow++;
                    Sfx?.Invoke(0x10);                              // $D530 SFX $10
                }
                return;
            }
            State = 0;                                          // $D571
            if (Objects != null)
            {
                var s = Objects.Slots[0];
                s.AnimNumber = 0x01;                            // $D575 JSR $FA78
                s.AnimFrame = 0;
                s.AnimTimer = 0;
            }
            YVelInt = 0; YVelFrac = 0;                           // $D57A/$D57D
            if (IntroHoldFlag != 0) State = 0x0F;               // $D580-$D586
        }

        // ---------- State 7: death throes ($D353) ----------
        // $D353 keeps its phase in slot 0's own object bytes, which is why it survives a
        // mid-mission reload and why a pit death (bit7 set, low nibble ≠ 0) skips the launch:
        //   $0490 ≠ 0 → Y -= vy only ($F55E), no gravity, no X          (15 frames after init)
        //   $0480 ≠ 0 → re-write vy=1.8E / vx=0.10 and NOTHING else     (60 frames, frozen)
        //   $0480 = 0 → $F5C9 (integrate + gravity) then $F4FD (X add), until vy reaches $FC,
        //               where $D3D1 keeps only $F5A4 → constant 3.83 px/frame fall, no X.
        // $D3B4's STA $9B (audio blip gate) is not modelled; $9B is on the open-gap list.
        public void StartDeath()
        {
            State = 7;
            // $64 is NOT set here: the ROM only increments it at $CB39/$D407, i.e. when the
            // spare energy column $05E4 is already empty. Setting it made every death take
            // the $D41D game-over branch.
        }

        private void DeathThroesState()
        {
            var s = Objects?.Slots[0];
            if (s == null) return;                            // $D353 reads $04D0/$0480/$0490
            if ((s.StateLow & 0x0F) == 0)                     // $D353-$D358
            {
                YVelFrac = 0;                                 // $D35A, A is the AND #$0F result
                YVelInt = 2;                                  // $D35D-$D35F
                s.T480 = 0x3C;                                // $D362-$D364
                s.T490 = 0x0F;                                // $D367-$D369
                Sfx?.Invoke(0x16);                            // $D36C JSR $F850 #$16
                s.StateLow++;                                 // $D371
                SetAnimPlayer(0x09);                          // $D374-$D376
                Flags &= 0xDF;                                // $D379-$D37E
            }
            if (s.T490 != 0)                                  // $D381
            {
                s.T490--;                                     // $D386
                IntegrateYRise();                             // $F55E
                return;                                       // $D389 JMP $F55E
            }
            if (s.T480 != 0)                                  // $D38C
            {
                YVelFrac = 0x8E; YVelInt = 1;                 // $D391-$D398
                XVelFrac = 0x10; XVelInt = 0;                 // $D39B-$D3A2
                s.T480--;                                     // $D3A5
                if (s.T480 == 0x1E) Sfx?.Invoke(0x25);         // $D3AB-$D3B1
                return;                                       // $D3B8: the float moves nothing
            }
            if (Flags == 0x50 || Flags == 0x10)               // $D3BC-$D3C2
            {
                DrainEnergyAndResolve();
                return;
            }
            if (YVelInt == unchecked((sbyte)0xFC))            // $D3C4
            {
                IntegrateY();                                 // $D3D1 JSR $F5A4 (no gravity/X)
                if ((s.StateLow & 0x80) != 0) return;         // $D3D4-$D3D7 BMI $D41A
                DrainEnergyAndResolve();                      // $D3D9
                return;
            }
            if (YVelInt >= 0) IntegrateYRise();               // $F5C9 → $F581
            else IntegrateY();                                // $F5C9 → $F5A4
            ApplyGravity();                                   // $F5DA
            IntegrateX(1);                                    // $D3CE JMP $F4FD
        }

        /// <summary>$D3D9-$D40E: one rung of the HUD energy ladder, then $50 = $08 with both
        /// death timers cleared. Same ladder as $CB0E-$CB3B (<see cref="DeathService"/>), but
        /// $D40E only arms state 8, so the resolve itself runs on the NEXT frame.</summary>
        private void DrainEnergyAndResolve()
        {
            if (EnergyColRight == 0x70)                       // $D3DF-$D3E1
                SecondEnergyColumn();                         // $D3F0
            else
            {
                EnergyColRight--;                             // $D3E3
                if (EnergyColRight == 0x6F) SecondEnergyColumn();  // $D3E6-$D3EB
            }
            State = 8;                                        // $D40E-$D410
            if (Objects != null) { Objects.Slots[0].T480 = 0; Objects.Slots[0].T490 = 0; } // $D412-$D417
        }

        /// <summary>$F55E/$F581: Y -= vy with the borrow handled the other way round from
        /// $F5A4 — a borrow out of $0510 takes a further $10 off $0510 and 1 (or 2, when that
        /// wrap borrows again) off $0520, so crossing below $00 lands on $EE/$F0 of the
        /// PREVIOUS page instead of $09 of the next one.</summary>
        private void IntegrateYRise()
        {
            int acc = YFrac - YVelFrac;                                    // $F55E-$F565
            bool borrow = acc < 0;
            YFrac = (byte)acc;
            int raw = (Y & 0xFF) - (YVelInt & 0xFF) - (borrow ? 1 : 0);    // $F568-$F56E
            if (raw >= 0)                                                  // $F571 BCS $F580
            {
                Y = (ushort)((Y & 0xFF00) | (raw & 0xFF));
                return;
            }
            int lo = (raw & 0xFF) - 0x0F - 1;                              // $F573 SBC #$0F, C=0
            int hi = (Y >> 8) - 1 - (lo >= 0 ? 0 : 1);                     // $F578 SBC #$01
            Y = (ushort)(((hi & 0xFF) << 8) | (lo & 0xFF));
        }

        // ---------- State 8 / death resolve ($D41B) ----------
        /// <summary>Set when $D41B asks for the whole world ($DD57); consumed by DarkwingGame.</summary>
        public bool WorldReloadRequest;
        /// <summary>Set when $D453 asks for the checkpoint section ($E388 with $6D &amp; $1F).</summary>
        public byte SectionReloadRequest = 0xFF;
        /// <summary>Set when $64 is non-zero, i.e. the $D46C game-over outro (M7).</summary>
        public bool GameOverRequest;

        /// <summary>
        /// $CB01-$CB3E, the service $CAFC runs while slot 0's $04D0 bit 7 is CLEAR. It drains
        /// the two HUD energy columns ($05E6, then $05E4 once $05E6 is empty; when $05E4 is
        /// empty too it increments the death counter $64) and then TAIL-CALLS the resolve
        /// through $CB3B/$CB3E. $D41B ends in JMP $FF06, never RTS-ing back to $CA43, so the
        /// rest of the world frame ($DA70 scroll, $8090 spawner, $E388 door, $8000 anim) is
        /// skipped — that, not any special meaning of state $07, is what freezes gold_w05
        /// f656-$04D0=00 → f674 while $50 stays $07 and only $05E6 counts down.
        /// ($CB03's $9B gate selects the SFX $25 variant; $9B is not modelled yet.)
        /// </summary>
        public void DeathService()
        {
            if ((sbyte)PendingMissionFlag >= 0)            // $CB0A: negative $66 skips the drain
            {
                if (EnergyColRight == 0x70)                 // $CB11
                    SecondEnergyColumn();
                else
                {
                    EnergyColRight--;                       // $CB15
                    if (EnergyColRight == 0x6F)             // $CB1B
                        SecondEnergyColumn();
                }
            }
            ResolveDeath();
        }

        private void SecondEnergyColumn()
        {
            if (EnergyColLeft == 0x70) DeathFlag++;                          // $CB27 → $CB39
            else { EnergyColLeft--; EnergyColRight = 0x79; }                 // $CB29/$CB32
        }

        /// <summary>
        /// $D41B: three-way fork on the death counter ($64) and the checkpoint id ($6D).
        /// w05 f656 has both zero, so it takes the full $DD57 world reload.
        /// </summary>
        public void ResolveDeath()
        {
            if (DeathFlag != 0)                              // $D41D
            {
                GameOverRequest = true;                      // $D46C outro is M7 (not ported)
                return;
            }
            if (CheckpointId != 0)                           // $D421 → $D453
            {
                if ((sbyte)PendingMissionFlag < 0)           // $D453-$D45B
                {
                    PendingMissionFlag = 0;
                    ContinueCount = 0;
                }
                else ContinueCount++;                        // $D45F
                SectionReloadRequest = (byte)(CheckpointId & 0x1F);   // $D461-$D466
                return;
            }
            Objects?.ClearForSectionLoad();                  // $D42A JSR $C235
            if (Objects != null)
            {
                Objects.Slots[0].StateLow = 0;               // $D42F STA $04D0
                Objects.Slots[0].Active = false;
            }
            Flags = 0;                                       // $D432 STA $0400
            WeaponActive = 0;                                // $D435 STA $53
            WorldReloadRequest = true;                       // $D437 JSR $DD57
        }

        // ---------- State 0: ground ($CB8B) ----------
        private void GroundState(byte held, byte pressed)
        {
            // Re-arm walk velocity every frame ($CB43): 1.5 px/frame
            XVelInt = 1; XVelFrac = 0x80;

            // Shared vertical physics first ($CD0E via $CB8B): gravity + landing
            if (YVelInt >= 0 && YVelInt < 5 && (held & 0x80) == 0) CutVelocity(); // $CD3E, X=0
            bool landed = VerticalPhysics(held, pressed);
            if (State != 0) return; // jumped inside VerticalPhysics

            // $CB93-$CBB6: the vertical service leaves $10 = the maximum metatile code it
            // sampled, and code 4 is the damage tile. Standing on one is the same fresh-hit
            // ladder as the contact sites ($F476): $50=6, $51=$0F, anim $09 via $FA78,
            // SFX $15, $7C=$3C, then JSR $F9C2. gold_w03 f583 is this path — the player
            // lands on the type-4 block at X=$0081 and hurts on the very next frame.
            if (_probeResult == 4 && InvulnFrames == 0)
            {
                State = 6;
                StateTimer = 0x0F;
                SetAnimPlayer(0x09);
                Sfx?.Invoke(0x15);
                InvulnFrames = 0x3C;
                TakeHit(Objects != null ? Objects.Slots[0].AnimNumber : 0); // $F9C2, X=0 → $0420
                HurtOrigin = 1;                                            // $CB93 tile site
                return;
            }

            // $CDF5-$CE38: when the vertical service's floor probe comes back without a
            // landing ($F2D7 returns carry clear) and $55 is 0, $CE34 force-sets $50=1.
            // $CB8E then re-tests $50 and RTS-es, so the walk move at $CCD9 never runs —
            // gold_m1_fulljump f208 keeps X=$14B and only gains the falling anim $12 on
            // f209, which is exactly the frame the pylon top ends.
            if (!landed && _vertFalling)
            {
                State = 1;
                return;
            }

            if ((pressed & 0x80) != 0) // A pressed → jump ($CBB7 → JMP $CD0E)
            {
                YVelInt = 4; YVelFrac = 0xE5;
                State = 1;
                Sfx?.Invoke(0x21); // jump ($F850 #$21)
                if (Objects != null)
                {
                    var sj = Objects.Slots[0];
                    sj.AnimNumber = (byte)(((held & 0x08) != 0) ? 0x0D : 0x11); // jump / reach-up
                    sj.AnimFrame = 0;
                    sj.AnimTimer = 0;
                }
                VerticalPhysics(held, pressed); // impulse integrates in the same frame
                AirborneControl(held);
                return;
            }

            if ((held & 0x03) != 0) // Left/Right held → walk ($CBB7/$CC53)
            {
                // $CC53-$CC6D: the B edge is read BEFORE the walk anim and the $CCB4 move,
                // so a shot frame latches the spawn and RTS-es without walking.
                var s0 = Objects != null ? Objects.Slots[0] : null;
                bool bEdge = (pressed & 0x40) != 0;
                if (bEdge && CanFire())
                {
                    if (s0 != null && s0.AnimNumber != 5) SetAnimPlayer(5);       // $CC5E-$CC67
                    TryLatchFire();                                               // $CC6A
                    return;                                                       // $CC6D
                }
                // $CC6E-$CC75 diverts EVERY walk frame whose pose is still the fire anim
                // $05 into the $CBF6 gate, not just the frame with the B edge. $CBF6/$CBFD
                // hold the pose (and therefore all horizontal movement) until anim frame 2
                // with timer 4 — three pose frames of five ticks — and $CC04 then spends one
                // frame on the idle anim $01 before $CC78 may start the walk again. A B edge
                // seen by $CC16 still fires, because $CC04 has already left the pose.
                if (s0 != null && s0.AnimNumber == 5)
                {
                    if (s0.AnimFrame != 2 || s0.AnimTimer != 4)
                    {
                        if (bEdge && CanFire()) { TryLatchFire(); }               // $CC1C-$CC34
                        return;                                                   // $CC37/$CC52
                    }
                    SetAnimPlayer(0x01);                                          // $CC04-$CC0D
                    State = 0;                                                    // $CC12
                    HopTimer = 0;                                                 // $CC14
                    if (bEdge && CanFire())
                    {
                        if (s0.AnimNumber != 5) SetAnimPlayer(5);                 // $CC1C-$CC2F
                        TryLatchFire();                                           // $CC34
                    }
                    return;                                                       // $CC37/$CC52
                }
                // Walk anim ($CC6E-$CCB1): anim 3 is set when it isn't already the anim,
                // the only manual frame edit here is the $CC84 wrap (frame 4 + timer 6 →
                // frame 1, timer 0). Frame 0 with timer < 4 is the footstep dust and pins
                // the speed to 0.$48 (0.28 px/frame); gold_m1_fulljump f354-f358 holds it
                // for five frames and only reaches 1.$80 when the timer passes 4.
                if (s0 != null)
                {
                    if (s0.AnimNumber != 3)
                    {
                        s0.AnimNumber = 3; s0.AnimFrame = 0; s0.AnimTimer = 0;   // $CC81 → $FA78
                    }
                    else if (s0.AnimFrame == 4 && s0.AnimTimer == 6)
                    {
                        s0.AnimFrame = 1; s0.AnimTimer = 0;                       // $CC92-$CC99
                    }
                    if (s0.AnimFrame == 0 && s0.AnimTimer < 4)
                    {
                        XVelInt = 0; XVelFrac = 0x48;                             // $CCAA-$CCB1
                    }
                }
                bool right = (held & 0x01) != 0;
                FacingRight = right ? (byte)0x40 : (byte)0x00;
                Flags = (byte)((Flags & ~0x40) | FacingRight);
                DirBits = (byte)(held & 0x03);
                int before = X;
                MoveHorizontal(right);
                bool moved = X != before;
                // wall block ($CCDF: blocked if $9E negative)
                byte side = ProbeSide(right);
                if (side >= 3)
                {
                    WallBlocked = 0xFF;
                    SnapX(right);
                }
                else WallBlocked = 0;
                return;
            }

            if ((held & 0x0C) == 0x08) // Up only → look up ($CBD7/$CC3A)
            {
                SetAnimPlayer(0x04);
                State = 3;
                return;
            }
            if ((held & 0x0C) == 0x04) // Down → crouch ($CBD7/$CC44)
            {
                SetAnimPlayer(0x06);
                State = 2;
                if (Objects != null) Objects.Slots[0].T490 = 0;   // $CC4F
                return;
            }

            // $CBE6: a B edge jumps straight into the fire block, skipping the idle
            // normalization; $CC1C vetoes the crouch anim $04 and $CC23 the $CF3A budget.
            var s = Objects?.Slots[0];
            if ((pressed & 0x40) != 0)
            {
                if (s != null && s.AnimNumber != 0x04 && CanFire())
                {
                    if (s.AnimNumber != 5) SetAnimPlayer(5);   // $CC2B/$CC2F
                    TryLatchFire();                            // $CC34
                }
                return;                                        // $CC37 → $CC52
            }

            // $CBEF-$CC14: with no direction held, the ground handler restores the idle
            // anim whenever it isn't already 1. The fire pose $05 is the exception while it
            // has not reached frame 2 / timer 4 ($CBF6-$CC02 only falls through to $CC04
            // once it does). This is what drops the hurt anim ($09) after state 6 exits.
            bool keepFirePose = s != null && s.AnimNumber == 5
                                && !(s.AnimFrame == 2 && s.AnimTimer == 4);
            if (s != null && !keepFirePose && s.AnimNumber != 1)
            {
                SetAnimPlayer(0x01);                     // $CC0B
                State = 0;                               // $CC12
                HopTimer = 0;                            // $CC14 ($5A)
            }
        }

        /// <summary>Horizontal move with probe+snap ($F200/$F254 flow).</summary>
        private void MoveHorizontal(bool right)
        {
            IntegrateX(right ? +1 : -1);
            byte side = ProbeSide(right);
            if (side >= 3)
            {
                // snap back to tile edge ($ED85/$ED9D)
                SnapX(right);
            }
        }

        /// <summary>
        /// $ED85 (right-block path, called at $F248) and $ED9D (left-block path, $F2A3).
        /// Both work on $06 = the last sampled probe point, so the snap aligns the SAMPLE
        /// to a tile edge, not the body: right → X -= ($06 & $0F), left → X += ((~($06 &
        /// $0F) & $0F) + 1) (SEC + ADC). With the player's side offset of +12/-12 that
        /// makes the room-0x02 pylon wall rest at X=$114 (sample $120, nibble 0 → no
        /// subtract), which gold_traverse_m1 shows from frame 159 on.
        /// </summary>
        private void SnapX(bool right)
        {
            if (right)
            {
                int sample = X + 12;                        // $06
                X = (ushort)((X - (sample & 0x0F)) & 0xFFFF);   // $ED85-$ED9C
            }
            else
            {
                int sample = X - 12;                        // $06
                X = (ushort)((X + (((~(sample & 0x0F)) & 0x0F) + 1)) & 0xFFFF); // $ED9D-$EDB2
            }
        }

        // ---------- $F344 landing / $F360 hook dance ----------
        /// <summary>$EDD2: pull the feet up onto the row plane the floor sample hit. Only
        /// ever moves the player UP, which is why the caller must reject a type-1 support
        /// once the feet are 8 px or more inside the tile.</summary>
        private void SnapFeetUpToRow()
        {
            // $07 is the last sampled point's Y low byte; the $EC2D/$EC47 +$10 row-wrap
            // correction cannot change its low nibble, so (Y+12)&$0F is exact.
            int off = (Y + 12) & 0x0F;
            int lo = (Y & 0xFF) - off;
            int hi = Y & 0xFF00;
            if (lo < 0)                       // $EDE1 BCS $EDF0, else $EDE3-$EDEC
            {
                hi = (hi - 0x100) & 0xFFFF;
                lo = (lo - 0x10) & 0xFF;
            }
            Y = (ushort)(hi | lo);
        }

        /// <summary>$F344 JSR $EDD2 / $F347 JSR $F5FC / SEC — "floor collision".</summary>
        private bool LandOnRow(bool cutOnLand)
        {
            SnapFeetUpToRow();
            _hookLatch = 0;                   // $F35B / $F397 / $F3BF clear $5C on every land
            // The $50=0B/10 landing ($D711 = $50→0, $56→0, then `LDA $0560 / BPL $D71F /
            // JMP $EDD2`) is snap only — the fall velocity survives into state 0 (m1_longroute
            // f441: ROM $0550 = $E5-$40 = $A5 while a cut there leaves $0460 six units low
            // for the rest of the run and costs the Y carry at f511). $CE08's landing SFX
            // belongs to the state-1 handler, not $D711.
            if (cutOnLand)
            {
                CutVelocity(); // landing leaves the $F607 value (-0.33), capture-verified
                if (_wasAirborne) Sfx?.Invoke(0x10); // landing SFX ($CE08: SFX $10)
            }
            LandedFlag = 1;
            return true;
        }

        /// <summary>$F360-$F3C1 for slot 0: the fall went into a type-1 (too deep) or type-2
        /// tile, so $5C is latched and the next probe uses the hook set ($3A) 8 px above the
        /// head. A type 1/2 there is the hang grab ($50=$0D + SFX $12, no snap); otherwise
        /// the floor set is probed a second time and only a type >= 3 — or a type 1 with
        /// $0560 exactly $FA — may land.</summary>
        private bool HookDance(bool cutOnLand)
        {
            if (_hookLatch == 0) _hookLatch = 1;                    // $F364 INC $5C
            byte above = ProbeHook();                               // $F372
            _probeResult = above;
            if (above == 1 || above == 2)                           // $F37E/$F382 BEQ $F386
            {
                if (State != 0x0D)                                  // $F386 BEQ $F347: cut only
                {
                    State = 0x0D;                                   // $F38E
                    Sfx?.Invoke(0x12);                              // $F392 JSR $F850
                }
                _hookLatch = 0;                                     // $F397
                CutVelocity();                                      // $F39F (no snap)
                return false;                                       // $F3A2, carry clear
            }
            if (above >= 3) return false;                           // $F384 BNE $F3A2
            byte re = ProbeFloor();                                 // $F3AB re-probe the floor set
            _probeResult = re;
            if (re == 0) return false;                              // $F3B3 BCC $F3A2
            if (re == 1)                                             // $F3B7 BEQ $F34C
                return (byte)YVelInt == 0xFA ? LandOnRow(cutOnLand) : false;   // $F34F/$F359
            if (re == 2) return false;                               // $F3BB BEQ $F3A2
            return LandOnRow(cutOnLand);                             // $F3BD/$F3C1
        }

        // ---------- Shared vertical physics ($CD0E/$F2D7) ----------
        /// <summary>The full service $F2D7 used by states 0/1/2/6 ($CD63/$D0C8/$D31C/$D9A3).
        /// The state $0B/$10 vertical service $F5C9 is a different routine with its own probe
        /// dispatch and lives in <see cref="AirborneD626"/>.</summary>
        /// <returns>true if landed this frame</returns>
        private bool VerticalPhysics(byte held, byte pressed, bool cutOnLand = true)
        {
            // $EB62: $10 is cleared on entry to every probe service, so a hazard code only
            // ever survives the frame whose $EB52 actually sampled it. Without the reset a
            // stale 4 from an earlier frame re-runs the $CB93 hurt ladder every frame (w01
            // f665 and w05 f635 hurt while the original keeps walking / keeps falling).
            _probeResult = 0;
            // $F2D7 never reads the pad. The caller's $CD3E block owns jump release.
            bool falling = YVelInt < 0;
            _vertFalling = falling;
            _wasAirborne = State is 1 or 0x0B or 0x10;
            if (falling)
            {
                ushort previousY = Y;                                // $F2DB-$F2E3
                IntegrateY();               // $F2ED JSR $F5A4 (down)
                if (PushPairOrScan(out byte supportLift))             // $F2F4/$F2F7
                {
                    LiftByObject(supportLift);                       // $F2F9-$F316
                    bool movedUp = Y < previousY;                    // $F49C-$F4A7
                    byte contact = movedUp ? ProbeCeiling() : ProbeFloor();
                    _probeResult = contact;
                    if (movedUp && contact >= 3) SnapBelowHead();
                    else if (!movedUp && contact >= 2) SnapFeetUpToRow();
                    CutVelocity();                                   // $F319 -> $F49C -> $F347
                    LandedFlag = 1;
                    return true;                                     // no gravity on this branch
                }
                // $F31F runs $F5DA unconditionally on the falling path ($F64A is a slot-pair
                // test, not a ground test): gravity applies every frame and the landing
                // branch re-cuts to $FF,AB afterwards. On the frame the floor disappears
                // there is no cut, so the fall keeps accelerating (gold_m1_fulljump f208:
                // $0560/$0550 FF,AB → FF,6B).
                ApplyGravity();
                // $F322: $5C latches "this fall has already gone through a pass-through tile,
                // so start the next frame at the hook check". It survives the frame it is set
                // on ($F3A2 returns without clearing it) and only a land clears it.
                if (_hookLatch != 0) return HookDance(cutOnLand);   // $F324 BNE $F366
                byte floor = ProbeFloor();                          // $F326 ($40=$06 floor set)
                _probeResult = floor;                               // $10 as left by $EB52
                if (floor == 0) return false;                       // $F32E BCC $F34B
                // $F332-$F338: a type-1 support is only caught while the feet sample sits in
                // the upper half of the tile — $EDD2 can only snap the feet UP to the row
                // plane, so once the feet are >= 8 px into the tile the one-way is passed
                // through. There is NO velocity gate here: $F4D7's `CMP #$FC` belongs to the
                // $D517 pit-fall handler. At m1_trench f277 the ROM lands with $0560 = $FE.
                if (floor == 1 && (((Y + 12) & 0x0F) >= 8)) return HookDance(cutOnLand);
                if (floor == 2) return HookDance(cutOnLand);        // $F33E-$F342 BEQ $F360
                return LandOnRow(cutOnLand);                        // $F344/$F347
            }
            else
            {
                // $F3CB JSR $F581 — the RISE integrate, not $F5A4. The two differ exactly on
                // a page crossing: $F5A4 wraps lo >= $F0 BACK UP by adding $100 (correct while
                // $0560 is negative), while $F581 borrows $10 off the page. Using $F5A4 here
                // turned the m1_trench f378 jump 0301→02EE into 0301→040E, which then made
                // the bank-15 $8062 offscreen-slot-0 test read a row difference of 1 and
                // start the death throes ($806E STA $50 = $07).
                IntegrateYRise();
                ApplyGravity();
                byte ceil = ProbeCeiling();
                _probeResult = ceil;                // $10 as left by $EB52
                // $F430-$F471: the rising branch dispatches on the CLASS found above the head,
                // not on "solid or not". Type 0 leaves the frame untouched; types 1 and 2 react
                // only when the middle sample ($43, directly over the head) is itself non-zero;
                // type 4 is the damage tile; 3/5/6/7 (and the hook type 8) just snap and fall.
                switch (ceil)
                {
                    case 0:                                        // $F434 BCC $F474
                        return false;
                    case 1:                                        // $F43C → $F45B
                        if (State == 4) { CutVelocity(); return false; }   // $F45F BEQ $F471
                        if (_probe43 == 0) return false;                   // $F463 BEQ $F474
                        State = 4;                                         // $F467
                        Sfx?.Invoke(0x12);                                 // $F46B
                        SnapBelowHead();                                   // $F46E ($EDB3)
                        CutVelocity();                                     // $F471 ($F5FC)
                        return false;
                    case 2:                                        // $F444 → $F446
                        if (State == 0x0D) { CutVelocity(); return false; }  // $F44A BEQ $F471
                        if (_probe43 == 0) return false;                   // $F44E BEQ $F474
                        Sfx?.Invoke(0x12);                                 // $F452
                        State = 0x0D;                                      // $F457
                        CutVelocity();                                     // $F459 → $F471, no snap
                        return false;
                    case 4:                                        // $F440 → $F476
                        if (InvulnFrames != 0)
                        {
                            SnapBelowHead();                                   // $F494 ($EDB3)
                            CutVelocity();                                     // $F497 ($F5FC)
                            return false;
                        }
                        State = 6;                                         // $F47C
                        StateTimer = 0x0F;                                 // $F480
                        SetAnimPlayer(0x09);                               // $F484 ($FA78)
                        Sfx?.Invoke(0x15);                                 // $F489
                        InvulnFrames = 0x3C;                               // $F48E
                        TakeHit(Objects != null ? Objects.Slots[0].AnimNumber : 0);  // $F490 ($F9C2)
                        return false;
                    default:                                       // 3, 5, 6, 7, 8 → $F46E
                        SnapBelowHead();
                        CutVelocity();
                        return false;
                }
            }
        }

        /// <summary>$EDB3 as the rising branch uses it: push the body down onto the top of
        /// the tile row the head sample ($07 = Y - 12) last touched, i.e. Y += 16 - (sample
        /// and $0F). Capture-verified: m1_longroute f1081 head bonk Y 034A → 034C.</summary>
        private void SnapBelowHead()
        {
            int py = Y - 12;
            py = py + (0x0F - (py & 0x0F)) + 1 + 12;
            Y = (ushort)(py & 0xFFFF);
        }

        // ---------- State 0B/10: full airborne control ($D626) ----------
        private void AirborneFullState(byte held, byte pressed)
        {
            // $D626 runs the vertical service FIRST ($D650 JSR $F5C9, $D653 JSR $EB52) and only
            // reaches the steering at $D756/$D76E when that probe did not land ($D656 CMP #$03 →
            // $D711 leaves without touching X). So the $F200 side probe samples the Y already
            // moved this frame. The port had the order reversed, which cost one frame of wall
            // stop: m1_longroute f438 Y=0374 → ROM X 01D4→01D6 (bottom point 037F leaves the
            // solid row 0380), port 01D4 (it still probed from 0375, point 0380).
            if (!AirborneD626(pressed)) return;
            AirborneControl(held);
            // $D79A-$D7B3: the airborne shot picks the pose from the POST-service velocity
            // sign ($0E rising, $0F falling) and calls $FA78 unconditionally before $CE39.
            if ((pressed & 0x40) != 0 && CanFire())
            {
                SetAnimPlayer(YVelInt >= 0 ? (byte)0x0E : (byte)0x0F);
                TryLatchFire();
            }
        }

        /// <summary>$D626-$D756 for slot 0: the $F5C9 vertical service followed by the $EB52
        /// class dispatch. Returns false when the frame leaves through a landing/park/hurt
        /// exit ($D6FB/$D711/$D725/$D73C/$D7C0) and must not reach the steering. $F5C9 has NO
        /// jump cut — the variable-height cut lives in $F2D7, so a state $0B/$10 arc is always
        /// the full impulse height.</summary>
        private bool AirborneD626(byte pressed)
        {
            // $D626-$D63C pick the probe set from the PRE-service velocity sign: $40 = $09
            // (ceiling set) while rising, $06 (floor set) while falling, $41 = 2 either way.
            bool rising = YVelInt >= 0;
            var s0 = Objects?.Slots[0];
            if ((pressed & 0x08) != 0 && s0 != null && s0.AnimNumber != 0x0D) SetAnimPlayer(0x0D);   // $D63E-$D64D
            if (rising) { IntegrateYRise(); ApplyGravity(); }               // $F5C9 BPL → $F581 + $F5DA
            else { IntegrateY(); ApplyGravity(); }                          // $F5C9 BMI → $F5A4 + $F5DA
            byte probe = rising ? ProbeCeiling() : ProbeFloor();            // $D653, $40 as set above
            _probeResult = probe;
            if (probe == 3) return AirD711();                         // $D656 BEQ $D711
            if (probe == 4) { AirD73C(); return false; }               // $D65F BEQ $D73C
            if (!rising && probe == 1) return AirD6EE();               // $D666 BPL / $D66F BEQ $D6EE
            if (YVelInt >= 0) return true;                             // $D671 BPL $D676 → $D756
            // $D679-$D6AC: falling onto a class the floor set cannot catch. Only the plain
            // falling poses are rewritten to $12; the shot poses keep their frames until
            // $0410 = 1 with $0430 = 2, and the reach pose $0D clears its counters.
            if (s0 != null)
            {
                if (s0.AnimNumber is 0x0E or 0x0F)
                {
                    if (s0.AnimFrame == 1 && s0.AnimTimer == 2) SetAnimPlayer(0x12);   // $D69E/$D6A5 → $D6AC
                }
                else if (s0.AnimNumber == 0x0D)
                {
                    if (s0.AnimFrame == 3) { s0.AnimFrame = 1; s0.AnimTimer = 0; }      // $D68A-$D698
                }
                else SetAnimPlayer(0x12);                                               // $D688 → $D6AC
            }
            // $D6B1/$D6B4: JSR $F64A — with carry set the frame leaves through $D709 and the
            // player is lifted off an armed object instead of reaching the steering.
            if (PushPairOrScan(out byte lift))
            {
                PushUpAndLand(lift);                                // $D709 JSR $D7D8, $50 = 0
                return false;                                      // $D710 RTS: no hook probe, steering or shot
            }
            if (State != 0x0B)                                          // $D6B6 CMP #$0B / BEQ $D6C7
            {
                probe = ProbeHook();                                    // $D6BC-$D6C4: $40 = $3A
                _probeResult = probe;
            }
            if (CellGridByte == 0x90 && (byte)YVelInt == 0xFA) return AirD6FB();   // $D6C7-$D6D4
            switch (probe)
            {
                case 1: return AirD711();                               // $D6DA BEQ $D711
                case 2: return AirD7C0();                               // $D6DE BEQ $D7C0
                case 3: return AirD6FB();                               // $D6E2 BEQ $D6FB
                case 4: AirD73C(); return false;                        // $D6E6 BEQ $D73C
                default: return true;                                   // $D6E8 JMP $D756
            }
        }

        // ---------- $F64A: the player/object push ----------
        // $00/$01 are the scratch the 6502 leaves behind inside $F699 for $F7A8 to re-read, so
        // they are fields, not locals.
        private byte _push00;
        private byte _push01;

        /// <summary>$F699: the horizontal gap between the player (slot 0) and slot Y. The
        /// player's $0400 is $D0, so bit4 is always set and the 16-bit branch is the live one;
        /// the $F6C6 screen-relative branch is kept for fidelity but its RTS at $F6DE leaves
        /// the accumulator at the LDA #$00, i.e. it always measures a gap of 0.</summary>
        private byte PushDx(byte y)
        {
            var o = Objects;
            if (o == null) return 0;
            int px = o.Slots[y].X;
            if ((Flags & 0x10) != 0)                                    // $F69C AND #$10
            {
                int d = X - px;
                if (d < 0) d = -d;                                      // $F6B1-$F6C1
                _push00 = (byte)(d & 0xFF);
                _push01 = (byte)((d >> 8) & 0xFF);
                return _push00;                                         // $F6C3 LDA $00
            }
            int s = (byte)px - (byte)ScrollX;                           // partner screen Xlo
            int r = s - (byte)X;
            _push00 = (byte)(r < 0 ? -r : r);                           // $F6D2-$F6D8
            _push01 = 0;
            return 0;
        }

        /// <summary>$F6DF: slot Y's Y is nudged one $10-row toward the player by which 256-block
        /// it occupies, then the 16-bit player-minus-partner difference is taken. A is the
        /// absolute value of the LOW byte only; carry (restored by $F718/$F722 PHP/PLP) is the
        /// no-borrow of the full subtraction, so set means the player is not above.</summary>
        private byte PushDy(byte y, out bool playerBelow)
        {
            var o = Objects;
            int other = o == null ? 0 : o.Slots[y].Y;
            int adjusted = (other >> 8) == (Y >> 8) ? other
                         : (other >> 8) < (Y >> 8) ? other + 0x10       // $F6FF-$F709
                         : other - 0x10;                                // $F6F0-$F6FA
            int d = Y - adjusted;                                       // $F70B-$F716
            playerBelow = d >= 0;
            int lo = d & 0xFF;
            if ((lo & 0x80) != 0) lo = (-lo) & 0xFF;                    // $F71D-$F720
            return (byte)lo;
        }

        /// <summary>$F7A8: measure slot Y and, when the player is inside its touch box, leave
        /// the vertical push in $11 = $FBE9[class] - |ΔY|. $F7CB accepts |ΔY| EQUAL to
        /// $FBE9[class] (the $F772 twin does not), and the SBC at $F7D6 is preceded by SEC so
        /// there is no off-by-one as in the $F679 pair path.</summary>
        private bool PushScanOne(byte y, out byte push)
        {
            push = 0;
            var o = Objects;
            if (o == null) return false;
            byte cls = (byte)(o.Slots[y].BehaviorParam & 0x1F);         // $F7AB AND #$1F
            PushDx(y);
            if (_push01 != 0) return false;                             // $F7B4 BNE $F7DC
            if (_push00 >= EnemyBehaviors.TouchXHalf[cls]) return false; // $F7BA BCS $F7DC
            byte dy = PushDy(y, out _);                                 // $F7C1
            if (dy > EnemyBehaviors.TouchYHalf[cls]) return false;      // $F7CB
            push = (byte)(EnemyBehaviors.TouchYHalf[cls] - dy);         // $F7D3 LDA $FBE9,Y; SEC; SBC $03
            return true;                                               // dy <= TouchYHalf, so SBC carries
        }

        /// <summary>$F784: the all-slot scan reached from every rejected $F64A pair test (its
        /// exit is JMP $F784, not RTS). Slots 2..15 with $04D0, $0400 AND $0570 all negative —
        /// bit7 of $0570 is armed at runtime only, e.g. the CeilingGrabber's mine goes $47 →
        /// $87 as it settles. The first hit wins and $F79E jumps to $F7DE to return SEC.</summary>
        private bool PushScanAll(out byte push)
        {
            push = 0;
            var o = Objects;
            if (o == null) return false;
            for (byte y = 2; y < 16; y++)                               // $F784/$F7A1-$F7A4
            {
                var s = o.Slots[y];
                if (!s.Active || (s.Flags & 0x80) == 0 || (s.BehaviorParam & 0x80) == 0) continue;
                if (PushScanOne(y, out push)) return true;              // $F79E JMP $F7DE
            }
            return false;                                               // $F7A6 CLC
        }

        /// <summary>$F64A: first the slot-pair test (X = 0 ⇒ Y = slot 1), then the $F784
        /// all-slot scan on every rejection. The pair path's two rewrites of $11 both subtract
        /// with C = 0 inherited from the CMP that gated them ($F66E CMP #$19 / $F684 CMP #$11),
        /// so each is constant - $11 - 1, and the RTS carries that SBC's no-borrow.</summary>
        private bool PushPairOrScan(out byte push)
        {
            push = 0;
            var o = Objects;
            if (o == null) return false;
            var s = o.Slots[1];                                         // $F64B EOR #$01
            if (!s.Active || (Flags & 0x80) == 0 || (s.Flags & 0x80) == 0 || s.Behavior == 0)
                return PushScanAll(out push);                           // $F651-$F660 → $F696
            if (PushDx(1) >= 0x10) return PushScanAll(out push);        // $F665/$F667
            byte dy = PushDy(1, out bool below);                        // $F669
            if (below) return PushScanAll(out push);                    // $F66C BCS
            if (dy >= 0x19) return PushScanAll(out push);               // $F66E/$F670
            push = dy;                                                  // $F672 STA $11
            if (s.AnimNumber != 0)                                      // $F674/$F677
            {
                int v = 0x19 - push - 1;                                // $F679/$F67B SBC $11, C=0
                push = (byte)v;
                return v >= 0;
            }
            if (push >= 0x11) return PushScanAll(out push);             // $F684/$F686
            int w = 0x11 - push - 1;                                    // $F688/$F68A SBC $11, C=0
            push = (byte)w;
            return w >= 0;
        }

        /// <summary>$D709/$D7D8: clamp $11 to 4, subtract it from $0510/$0520 with the same
        /// row-borrow wrap the Y integrator uses ($D7EB SBC #$0F then $D7F3 SBC #$01 — a 240 px
        /// row, not a 256 px byte borrow), and drop $50 to 0. That state write is what makes an
        /// armed object a stand: the airborne handler returns here without reaching steering.</summary>
        private void PushUpAndLand(byte push)
        {
            LiftByObject(push);
            State = 0;                                                  // $D70C-$D70E
        }

        private void LiftByObject(byte push)
        {
            if (push > 4) push = 4;                                     // $D7D8-$D7DE
            int yl = (Y & 0xFF) - push;                                 // $D7E0-$D7E6
            int yh = Y >> 8;
            if (yl < 0) { yl -= 0x10; yh -= 1; }                        // $D7EB-$D7F5
            Y = (ushort)(((yh & 0xFF) << 8) | (yl & 0xFF));
        }

        /// <summary>$D6EE: a type-1 (one-way) support under a falling $0B/$10. $FD and $FE are
        /// caught; $FF parks as state 4 only from state $10 with a solid middle sample $43;
        /// every faster fall passes through to the $D6FB land.</summary>
        private bool AirD6EE()
        {
            byte v = (byte)YVelInt;
            if (v == 0xFD || v == 0xFE) return AirD6FB();               // $D6F1/$D6F5 BEQ $D6FB
            if (v == 0xFF && State == 0x10 && _probe43 != 0)            // $D6F9 BCS $D725, $D727/$D72B
            {
                State = 4;                                              // $D731
                Sfx?.Invoke(0x12);                                      // $D735 JSR $F850
                CutVelocity();                                          // $D738 JSR $F607
                return false;
            }
            return AirD6FB();
        }

        /// <summary>$D6FB: land with the landing SFX — $50 = $56 = 0, feet snapped UP to the row
        /// plane ($EDD2); the fall velocity survives into state 0.</summary>
        private bool AirD6FB()
        {
            Sfx?.Invoke(0x10);                                          // $D6FD JSR $F850
            State = 0;                                                  // $D702
            AirSubState = 0;                                            // $D704
            SnapFeetUpToRow();                                          // $D706 JMP $EDD2
            return false;
        }

        /// <summary>$D711: exit to state 0 on a class-3 tile. Falling snaps the feet up ($EDD2);
        /// rising is a head bonk — $F607 commits the fall, then $EDB3 pushes the body down onto
        /// the ceiling row. Neither branch plays the landing SFX.</summary>
        private bool AirD711()
        {
            State = 0;                                                  // $D713
            AirSubState = 0;                                            // $D715
            if (YVelInt < 0) SnapFeetUpToRow();                          // $D717/$D71C JMP $EDD2
            else { CutVelocity(); SnapBelowHead(); }                     // $D71F JSR $F607, $D722 JMP $EDB3
            return false;
        }

        /// <summary>$D7C0: a class-2 tile under a falling $0B/$10. State $0B hands the frame to
        /// the jump service ($50 = $01 plus the feet snap); state $10 becomes the ceiling park
        /// $0D with pose $0B, SFX $12 and the $F607 velocity.</summary>
        private bool AirD7C0()
        {
            if (State != 0x10)
            {
                State = 1;                                              // $D7B9
                SnapFeetUpToRow();                                      // $D7BB JSR $EDD2
                return false;
            }
            Sfx?.Invoke(0x12);                                          // $D7C8 JSR $F850
            State = 0x0D;                                               // $D7CD
            SetAnimPlayer(0x0B);                                        // $D7D1 JSR $FA78
            CutVelocity();                                              // $D7D4 JSR $F607
            return false;
        }

        /// <summary>$D73C: the class-4 hazard tile met in the air — the same hurt ladder as the
        /// $F2D7 ceiling dispatch's $F476, but here with no invulnerability veto.</summary>
        private void AirD73C()
        {
            State = 6;                                                  // $D73E
            StateTimer = 0x0F;                                          // $D742 STA $51
            Sfx?.Invoke(0x15);                                          // $D746 JSR $F850
            SetAnimPlayer(0x09);                                        // $D74B JSR $FA78
            InvulnFrames = 0x3C;                                        // $D750 STA $7C
            TakeHit(Objects != null ? Objects.Slots[0].AnimNumber : 0); // $D752 JSR $F9C2
        }

        // ---------- State 1: air ($CD0E path) ----------
        private void AirState(byte held, byte pressed)
        {
            // $CD3E-$CD54 runs BEFORE the vertical service $F2D7: while the velocity
            // integer is 0..$04 and A is not held, $F5FC force-sets it to $FFAB. Only
            // then does $F2E5 test the sign, so the frame after a hook release (whose
            // ride velocity is $0080) already takes the FALLING branch — no ceiling
            // probe, and gravity still applies in the same frame (gold_hook_ride2 f82:
            // $0510 1F→20, $0460 D5→2A, velocity 00,80 → FF,AB → FF,6B).
            AirborneControl(held);                        // $CD18: steering runs FIRST
            if ((held & 0x08) != 0) AirSubState = 1;       // $CD1B-$CD23
            // $CD25-$CD3B: the reach pose $0D vetoes the shot; a firing frame arms $56 = 2
            // (no $FA78 here — $CDBE rewrites the air anim from the velocity sign anyway)
            // and latches $CE39 BEFORE the vertical service.
            var sa = Objects?.Slots[0];
            if (sa != null && sa.AnimNumber != 0x0D && (pressed & 0x40) != 0 && CanFire())
            {
                AirSubState = 2;
                TryLatchFire();
            }
            if (YVelInt >= 0 && YVelInt < 5 && (held & 0x80) == 0)
            {
                // $CE39 leaves X at the allocated projectile. $CD54's indexed $F5FC
                // therefore cuts that slot, until $CD5D explicitly restores X=0.
                int x = HasPendingFire ? PendingFire.SlotPref : 0;
                while (x >= 2 && Objects != null && Objects.Slots[x].Active) x--;
                if (x >= 2) { var shot = PendingFire; shot.CutYVelocity = true; PendingFire = shot; }
                else CutVelocity();
            }
            bool landed = VerticalPhysics(held, pressed); // $CD63: then the vertical service
            SelectAirAnim();                              // $CD82-$CDBE
            if (landed)
            {
                State = 0;
                AirSubState = 0;
            }
        }

        /// <summary>
        /// $CD6A-$CDBE: after the vertical service the air handler rewrites the anim from
        /// the post-gravity velocity sign — rising $11 (group $0E), falling $12 (group $0F),
        /// $56==1 keeps the reach pose $0D — and $CDBE always calls $FA78 with it. That is
        /// why gold_m1_fulljump switches to $12 on f209 and, once the fall ends, the ground
        /// handler sees anim != 3 at $CC7B and restarts $03 from frame 0, which triggers the
        /// footstep-dust 0.28 px/frame velocity at $CCAA and slows the walk-out (f223-f228).
        /// </summary>
        private void SelectAirAnim()
        {
            if (Objects == null) return;
            bool risingPose = YVelInt >= 0 && _probeResult != 2; // $CD82-$CD96: class 2 uses fall pose
            byte anim = AirSubState == 1 ? (byte)0x0D
                    : AirSubState == 2 ? (risingPose ? (byte)0x0E : (byte)0x0F)
                    : risingPose ? (byte)0x11 : (byte)0x12;
            var s = Objects.Slots[0];
            if (AirSubState == 2 && s.AnimNumber == anim)
            {
                if (s.AnimFrame == 1 && s.AnimTimer == 4) AirSubState = 0; // $CDD5-$CDF3
                return;
            }
            if (AirSubState == 1 && s.AnimNumber == 0x0D)  // $CDB5-$CDD2
            {
                if (s.AnimFrame == 3) { s.AnimFrame = 1; s.AnimTimer = 0; }
                return;
            }
            SetAnimPlayer(anim);
        }

        /// <summary>Full air steering at walk speed ($CD18/$D78D).</summary>
        private void AirborneControl(byte held)
        {
            if ((held & 0x03) != 0)
            {
                bool right = (held & 0x01) != 0;
                DirBits = (byte)(held & 0x03);              // $CCBA / $D776: the shot uses this latch
                FacingRight = right ? (byte)0x40 : (byte)0x00;
                Flags = (byte)((Flags & ~0x40) | FacingRight);
                MoveHorizontal(right);
                byte side = ProbeSide(right);
                if (side >= 3) SnapX(right);
            }
        }

        // ---------- State 2: crouch ($CFD7) ----------
        private void CrouchState(byte held, byte pressed)
        {
            if ((held & 4) == 0) { State = 0; return; }
            var c0 = Objects?.Slots[0];
            if (c0 != null && c0.AnimNumber == 7)
            {
                if (c0.AnimFrame == 2 && c0.AnimTimer == 4)
                { SetAnimPlayer(6); c0.AnimFrame = 1; return; } // $D08C-$D0AC
            }
            else if (c0 != null && c0.AnimFrame == 1)
            { c0.AnimFrame = 1; c0.AnimTimer = 0; }
            else goto buttons;
            if (pressed == 0x40 && CanFire())
            { SetAnimPlayer(7); TryLatchFire(); return; }
        buttons:
            if ((pressed & 0x80) != 0)
            {
                if (!PushPairOrScan(out _)) // $D018, before integration
                {
                    IntegrateY(); ApplyGravity();
                    byte floor = ProbeFloor();
                    if (floor == 0) return;
                    if (floor < 3) { BeginCrouchDrop(); return; }
                    if (floor == 8) { OnHookTileFlag = true; Flags |= 0x20; }
                    SnapFeetUpToRow(); CutVelocity(); return;
                }
                // $D04A-$D11B: the two special projectiles can support a crouch.
                for (int i = 3; i >= 2; i--)
                {
                    var t = Objects.Slots[i];
                    if (!t.Active || (t.AnimNumber != 0x17 && t.AnimNumber != 0x29)) continue;
                    int dx = System.Math.Abs(X - t.X) & 255;
                    int dy = System.Math.Abs((byte)Y - (byte)t.Y);
                    if (dx < 12) return;
                    if (dy < 8) break;
                    if (dy < 15) return;
                    if (i == 2) { BeginCrouchDrop(); return; }
                }
                for (int i = 15; i >= 5; i--)
                    if (Objects.Slots[i].Active && Objects.Slots[i].AnimNumber == 0x33)
                    { BeginCrouchDrop(); return; }
                return;
            }
            if ((pressed & 4) != 0 && !PushPairOrScan(out _))
            {
                if (c0 != null && c0.T490 == 0xFF) VerticalPhysics(held, pressed);
                return; // $D0BD-$D0CB
            }
            if (c0 != null) c0.T490 = 0xFF; // $D11C
            byte below = ProbeFloor();
            if (below == 0) { VerticalPhysics(held, pressed); return; }
            if (below == 2) { State = 1; CutVelocity(); return; }
            if (below == 4) { AirD73C(); return; }
            if (below == 8) { OnHookTileFlag = true; Flags |= 0x20; }
            SnapFeetUpToRow(); CutVelocity();
        }

        private void BeginCrouchDrop()
        {
            State = 0x0A; SetAnimPlayer(0x12); CutVelocity();
            if (Objects != null) Objects.Slots[0].T480 = 0;
            HopTimer = _dropSupportTimer = 0; // $D06F-$D082
        }

        // ---------- State 3: look up ($D16E) ----------
        private void LookUpState(byte held, byte pressed)
        {
            if ((held & 0x08) == 0) { State = 0; return; }
            var s = Objects?.Slots[0];
            if (s != null && s.AnimFrame == 2)                       // $D174-$D182
            {
                s.AnimFrame = 2; s.AnimTimer = 0;
                if ((pressed & 0x80) != 0)                          // $D185-$D199
                {
                    YVelInt = 4; YVelFrac = 0xE5;
                    AirSubState = 1;
                    State = 1;
                    AirState(held, pressed);                        // JMP $CD0E, same-frame service
                    return;
                }
            }
            byte floor = ProbeFloor();                             // $D1A1 -> $D121
            if (floor == 0 || floor == 2) { State = 1; CutVelocity(); return; }
            if (floor == 4) { AirD73C(); return; }
            if (floor == 8) OnHookTileFlag = true;
            SnapFeetUpToRow();                                      // $D15E
            CutVelocity();                                         // $D161
        }

        // ---------- State 6: $D29A gate, then hurt ground/knockback ($D2C3) ----------
        /// <summary>
        /// $D29A: the state-$06 table entry first looks at the player's own animation.
        /// While it is $0B or $0C the player is being held, so only the $51 timer counts
        /// down and, at zero, $58 is restored — nothing is moved and no velocity is
        /// touched. Every other animation falls through to the knockback body at $D2C3.
        /// Evidence: gold_m1_longroute f331-f346 keeps $0400-frame values X=$01C1,
        /// Y=$03A6, xv=$01,$80 and vy=$03,$A5 byte-identical while $50 = $06, then $50
        /// returns to $04 at f347 ($51 counted $0F down to 0).
        /// </summary>
        private void HurtState(byte held, byte pressed)
        {
            byte anim = Objects?.Slots[0].AnimNumber ?? 0;        // $D29A LDA $0420
            if (anim != 0x0B && anim != 0x0C) goto knockback;     // $D29F/$D2A3
            if (StateTimer != 0) { StateTimer--; return; }        // $D2AA/$D2C0
            State = State == 0x0E ? SavedState : (byte)1;          // $D2AC-$D2BD
            return;
        knockback:
            HurtGroundState(held, pressed);
        }

        private void HurtGroundState(byte held, byte pressed)
        {
            // $D2C3-$D2CA: the knockback velocity is a fixed 0.5 px/frame; its sign comes
            // from the latched pad byte $52, not from the live pad.
            XVelInt = 0; XVelFrac = 0x80;
            if (StateTimer == 0)                        // $D2CD tests BEFORE any decrement
            {
                State = 0;                              // $D2D3
                WallBlocked = 0;                        // $D2D5 ($9E)
                return;                                 // no probe/move/DEC: $0460 and $0430 hold
            }
            if (StateTimer == 0x0F)                        // $D2DA CMP #$0F
            {
                CutVelocity();                             // $D2DE JSR $F5FC
                SpawnHurtAfterimage();                     // $D2E1-$D313
            }
            VerticalPhysics(held, pressed);             // $D316: X=0, Y=2, A=6 → JSR $F2D7
            StateTimer--;                               // $D31F
            // $D323-$D33F: $52 bit 0 set → $F254 SUBTRACTS the velocity (drift left, i.e.
            // backwards from the direction the player was facing); clear → $F200 adds it.
            MoveHorizontal((DirBits & 1) == 0);
            var g5 = HurtGhost;                         // $D346-$D34F: the afterimage follows X
            if (g5 != null) { g5.X = X; }
        }

        /// <summary>$D2E1-$D313: the hurt afterimage is a copy of the player in slot 5
        /// ($04D5/$0405/$0425 anim $0A, 20 px higher, behavior and hitbox cleared).</summary>
        private ObjectSystem.Slot HurtGhost => Objects == null ? null : Objects.Slots[5];

        private void SpawnHurtAfterimage()
        {
            if (Objects == null) return;
            var g = Objects.Slots[5];
            g.StateLow = 0x80;                          // $04D5
            g.Active = true;
            g.Flags = Flags;                            // $0405 = $0400
            g.AnimNumber = 0x0A;                        // $FA6C with X=5
            g.AnimTimer = 0; g.AnimFrame = 0;
            g.X = X;                                    // $04F5/$0505
            // $D2FF/$D303: the low byte subtracts $14 with NO borrow into the high byte.
            g.Y = (ushort)(((Y & 0xFF00) | ((Y & 0xFF) - 0x14 & 0xFF)) & 0xFFFF);
            g.BehaviorParam = 0;                        // $0575
            g.Behavior = 0;                             // $04E5
        }

        // ---------- States 4 / $0D: hurt or carried in the air ($D1A4) ----------
        /// <summary>
        /// $D1A4, the handler the $CB5F/$CB75 state table points at for $50 = $04, $0D and
        /// $0E (low bytes $CB63/$CB6C/$CB6D with high byte $D1). Enemy contact sites that
        /// lift the player leave him in $04 with the carrier owning his position, so this
        /// handler moves nothing itself — it only runs the landing check, the facing latch
        /// and the three exits. Before it was ported the dispatcher's
        /// <c>default: break</c> parked the player forever at $50=$04 (live route probe,
        /// mission 1: X=$01A4 Y=$039E frozen from f2430 on).
        /// Pad reads are per-instruction, not "both from held": the write-monitored pad
        /// service ($C173/$C17C shift loop, then $C188/$C199/$C19B) leaves <c>$14</c> =
        /// freshly pressed this frame (<c>current &amp; ~previous</c>) and <c>$16</c> =
        /// held, so $D1F4/$D20E/$D21E read the edge byte and $D1D5/$D243 read the held byte.
        /// Evidence: gold_m1_longroute holds A from f290 but $14 = $80 only at f290, and the
        /// carried state $50=$04 survives the drag until f420 — the next press edge, where
        /// $D21E lets the player leave the carrier into $50=$0B.
        /// </summary>
        private void HurtAirState(byte held, byte pressed)
        {
            HurtAirLanding();                                       // $D1B8 JSR $D253
            var s0 = Objects?.Slots[0];
            // $D1BB-$D1D2: the struggle anim, kept only while the $0C hang pose is on its
            // 2nd frame / 4th timer tick (that frame is what the ROM leaves unpoked).
            if (s0 != null && (s0.AnimNumber != 0x0C || (s0.AnimFrame == 0x02 && s0.AnimTimer == 0x04)))
                SetAnimPlayer(0x0B);                                // $D1D0-$D1D2
            byte dir = (byte)(held & 0x03);                         // $D1D5-$D1D9 ($16)
            if (dir != 0)
            {
                DirBits = dir;                                      // $D1DB
                FacingRight = (dir & 1) != 0 ? (byte)0x40 : (byte)0x00;   // $D1DD-$D1F1
                Flags = (byte)((Flags & ~0x40) | FacingRight);
            }
            // $D1F4-$D20B: a B edge with the $CF3A budget open sets the $0C hang pose and
            // commits the $CE39 spawn in the same frame.
            if ((pressed & 0x40) != 0 && CanFire())
            {
                if (s0 != null && s0.AnimNumber != 0x0C) SetAnimPlayer(0x0C);
                TryLatchFire();
            }
            if ((pressed & 0x04) != 0)                              // $D20E-$D212: Down ($14)
            {
                State = 0;                                          // $D214-$D216
                AirSubState = 0;                                    // $D218 ($56)
                CutVelocity();                                      // $D21A JSR $F607
                return;                                             // $D21D
            }
            if ((pressed & 0x80) == 0) return;                      // $D21E-$D222: A not pressed ($14)
            // $D224-$D22B: the same impulse the ground jump uses, so breaking out of a
            // grab costs nothing but the button press.
            YVelFrac = 0xE5; YVelInt = 4;
            if (State == 0x0D) State = 0x10;                        // $D22E-$D232, $D23F-$D241
            else
            {
                if (Objects != null) Objects.Slots[1].T480 = 0;     // $D234-$D236 ($0481)
                State = 0x0B;                                       // $D239-$D23B
            }
            SetAnimPlayer((held & 0x08) != 0 ? (byte)0x0D : (byte)0x11);   // $D243-$D24F
        }

        /// <summary>$D253-$D281: while airborne-hurt, a floor probe ($40=$06, $41=$02
        /// through $EB52) that reports metatile type $04 with $7C clear lands the player
        /// into the ground-hurt state ($50=$06, $51=$0F) — SFX $15, anim $09, $7C=$3C and
        /// the $F9C2 ladder, i.e. the same fresh-hit sequence as the $CB93 damage tile.</summary>
        private void HurtAirLanding()
        {
            _probeResult = ProbeFloor();                            // $D253-$D25B
            if (_probeResult != 4 || InvulnFrames != 0) return;     // $D25E-$D267
            State = 6;                                              // $D268-$D26A
            StateTimer = 0x0F;                                      // $D26C-$D26E
            Sfx?.Invoke(0x15);                                      // $D270-$D272
            SetAnimPlayer(0x09);                                    // $D275-$D277
            InvulnFrames = 0x3C;                                    // $D27A-$D27C
            TakeHit(Objects != null ? Objects.Slots[0].AnimNumber : 0);   // $D27E, X=0 → $0420
        }

        // ---------- Level script hook ride ($DAFD) + vertical camera scroll ($DC6E/$DCC4) ----------
        /// <summary>
        /// $DAFD, the per-frame level-script check. Cell script byte ($34),$22 bits:
        /// 6 = the camera may cross this cell horizontally, 7 = the vertical scroll may
        /// not advance the grid row here, low nibble 1 = hook column (ride one row
        /// down), 2 = snap column (ride up). $6D gets script &amp; $CF when that value is
        /// >= $C1 and not one of the even $C2..$CE pairs.
        /// </summary>
        public void HookGrabCheck()
        {
            byte script = CellScriptByte;                     // ($34),$22
            byte section = (byte)(script & 0xCF);             // $DB01/$DB03
            if (section >= 0xC1 && !(section >= 0xC2 && section <= 0xCE && (section & 1) == 0))
                CheckpointId = section;                       // $DB05-$DB27
            if ((byte)(ScrollX & 0xFF) != 0) return;          // $DB29/$DB2B
            ScrollStep = 1;                                   // $DB2D/$DB2F
            switch (script & 0x0F)                            // $DB31-$DB3D
            {
                case 1: HookGrab(); break;                    // $DB39 → $DB6D
                case 2:                                        // $DB3F → $DB40
                    ScrollStep = 0;
                    int d = RowOffsetBelowScroll();           // $DB43-$DB4E
                    ScrollStep = (byte)d;
                    if (d >= 0x48) return;                    // $DB50-$DB56
                    int window = 0x48 - d;                    // $DB57-$DB5A
                    ScrollStep = (byte)(window < 9 ? window : 4); // $DB5E-$DB64
                    ScrollMode = 8;                           // $DB66/$DB68
                    VerticalScrollUp();                       // $DB6A JMP $DCC4
                    break;
            }
        }

        /// <summary>$DB43/$DB47/$DB4B: ($0510 - $FA), biased by -$10 when it borrows.</summary>
        private int RowOffsetBelowScroll()
        {
            int d = (Y & 0xFF) - ScrollY;
            if (d < 0) d -= 0x10;
            return d & 0xFF;
        }

        /// <summary>$DB6D: grab test, then $DB8B entry or the $DBB3 ride tail.</summary>
        private void HookGrab()
        {
            if (State == 0x0C) { RideTail(); return; }         // $DB6F/$DB71 already hanging
            if (ScrollYRow != (byte)(Y >> 8)) return;         // $DB73/$DB75 ($FB == $0520)
            if (((Y & 0xFF) - ScrollY) < 0) return;           // $DB7A-$DB7E borrow → $DB8A RTS
            if ((byte)((Y & 0xFF) - ScrollY) >= 0xE8) EnterHang(); // $DB80/$DB82
        }

        /// <summary>$DB8B: state $0C, +0.5 px/frame vertical drift, clear object slots 15..2.</summary>
        private void EnterHang()
        {
            State = 0x0C;                                     // $DB8B/$DB8D
            YVelFrac = 0x80;                                  // $DB8F → $0550
            YVelInt = 0;                                      // $DB94 → $0560
            Objects?.ClearForHookRide();                      // $DB99-$DBB1
            RideTail();
        }

        /// <summary>$DBB3/$DBC3: peg grid byte re-asserts the on-hook flag, then ride the camera.</summary>
        private void RideTail()
        {
            if (CellGridByte == 0x90) Flags |= 0x20;          // $DBB5-$DBC0
            ScrollStep = 3;                                   // $DBC3 → $10
            ScrollMode = 4;                                   // $DBC9 → $2F
            VerticalScrollDown();                             // $DBCB JMP $DC6E
        }

        /// <summary>$DD36: a vertical scroll may start only with no horizontal scroll in
        /// progress ($2B &amp; $0C == 0) and the camera on a cell boundary ($FC == 0); it
        /// then arms the incoming cell ($23) and marks $2B = 4.</summary>
        private bool BeginVerticalScroll()
        {
            if ((ScrollState & 0x0C) != 0) return true;       // $DD38/$DD3A → $DD53 SEC
            if ((byte)(ScrollX & 0xFF) != 0) return false;    // $DD3C-$DD3E → $DD55 CLC
            CellIndexNext = (byte)(CellIndex + RowStride);    // $DD40-$DD45 → $23
            ScrollState = 4;                                  // $DD4D-$DD51 → $2B
            return true;
        }

        /// <summary>$DC6E: ride the camera down $10 px/frame; on the 240 px wrap advance
        /// $FB/$22 to the cell below. Every path except the two boundary gates redraws.</summary>
        private void VerticalScrollDown()
        {
            if (ScrollY == 0)                                 // $DC6E/$DC70
            {
                if (GridAt(CellIndex + RowStride) == 0) return; // $DC72-$DC7A
                if (ScrollYRow == BottomRow) return;          // $DC7C-$DC80
            }
            if (!BeginVerticalScroll()) return;               // $DC83/$DC86
            int next = ScrollY + ScrollStep;                  // $DC88-$DC8D
            ScrollY = (byte)next;
            if (next < 0xF0) { ReloadRow(); return; }         // $DC8F-$DC91 → $DCC1
            ScrollY = (byte)(next + 0x10);                    // $DC93/$DC95 (CMP leaves C=1)
            if ((CellScriptByte & 0x80) != 0) { ScrollY = 0; ReloadRow(); return; } // $DC97-$DC9B
            int row = ScrollYRow + 1;                         // $DC9D-$DC9F (ADC #0, C=1)
            int idx = CellIndex + RowStride;                  // $DCA3-$DCA8
            if (GridAt(idx) == 0) { ScrollY = 0; ReloadRow(); return; }  // $DCAB-$DCAD
            if (row > 0x7F) { ScrollY = 0; ReloadRow(); return; }        // $DCAF/$DCB1 BPL
            ScrollYRow = (byte)row;                           // $DCB9-$DCBF
            CellIndex = (byte)idx;
            // $E936 advances $23 when the streamed tile row wraps; the camera's row
            // change itself only writes $22 ($DCBF).
            ReloadRow();                                      // $DCC1 JMP $E7C6
        }

        /// <summary>$DCC4: ride the camera up $10 px/frame; on the 240 px wrap step $FB/$22 back.</summary>
        private void VerticalScrollUp()
        {
            if (ScrollY == 0)                                 // $DCC4/$DCC6
            {
                if (GridAt(CellIndex - RowStride) == 0) return; // $DCC8-$DCD1
                if (ScrollYRow == 0) return;                  // $DCD3/$DCD5
            }
            if (!BeginVerticalScroll()) return;               // $DCD8/$DCDB
            int next = ScrollY - ScrollStep;                  // $DCDD-$DCE2
            if (next >= 0) { ScrollY = (byte)next; ReloadRow(); return; } // $DCE4 → $DD14
            ScrollY = (byte)(next - 0x10);                    // $DCE6/$DCE8 (borrow still set)
            if ((CellScriptByte & 0x80) != 0) { ScrollY = 0; ReloadRow(); return; } // $DCEA-$DCEE
            int row = ScrollYRow - 1;                         // $DCF0/$DCF2 (SBC #1, C=1)
            int idx = CellIndex - RowStride;                  // $DCF6-$DCFB
            if (GridAt(idx) == 0) { ScrollY = 0; ReloadRow(); return; }  // $DCFE-$DD00
            if (row < 0) { ScrollY = 0; ReloadRow(); return; }           // $DD02/$DD04 BPL
            ScrollYRow = (byte)row;                           // $DD0C-$DD12
            CellIndex = (byte)idx;
            ReloadRow();                                      // $DD14 JMP $E7C6
        }

        // World feed (set by DarkwingGame/LevelTileMap before the check runs).
        public byte CellGridByte;      // ($32),$22 — room id of the camera cell ($DBB5)
        public byte CellScriptByte;    // ($34),$22 — cell script byte ($DB33)
        public Func<int, byte> GridAt;          // ($32),Y reader
        public Func<int, byte> FlagAt;          // ($34),Y reader
        public Action ReloadRow;                // $E7C6 row redraw

        // ---------- Energy HUD drain ($CB0E-$CB3B / state 7 $D3D9-$D407) ----------
        public byte EnergyColRight = 0x79; // $05E6 (tile code $70-$79 = 9..0)
        public byte EnergyColLeft = 0x79;  // $05E4

        /// <summary>Per-frame energy drain; both columns empty → death ($64 → $D41B).</summary>
        public void DrainEnergy()
        {
            if (EnergyColRight > 0x70) EnergyColRight--;
            else if (EnergyColLeft > 0x70)
            {
                EnergyColLeft--;
                EnergyColRight = 0x79; // refill right column from left ($CB22)
            }
            if (EnergyColLeft == 0x70 && EnergyColRight == 0x70)
                DeathFlag = 1; // → death sequence (world handles state change)
        }

        // ---------- Gas gun: $CE39 spawn, $CF3A budget, $CEF2 meter gate ----------
        public byte ActiveGasEnergyHi;  // $05BE
        public byte ActiveGasEnergyLo;  // $05C0

        private static readonly byte[] WeaponMaxActive = { 3, 1, 2, 1 };              // $CFD3
        private static readonly byte[] WeaponSlotPref = { 4, 2, 3, 2, 0xFF };         // $CF29
        private static readonly byte[] WeaponBehavior = { 1, 2, 3, 4 };               // $CF2E
        private static readonly byte[] WeaponTemplate = { 0x13, 0x16, 0x14, 0x14 };   // $CF32
        private static readonly byte[] WeaponSfx = { 0x11, 0x13, 0x11, 0x14 };        // $CF36
        // 16-bit (lo,hi) X offsets, right at index 0 and left at index 2 — the side comes
        // from $52 bit 1 ($CE6C/$CE89). Kinds 2/3 take the far $CF25 pair ($CE62-$CE68).
        private static readonly byte[] WeaponOffsetNear = { 0x0F, 0x00, 0xF1, 0xFF }; // $CF21
        private static readonly byte[] WeaponOffsetFar = { 0x16, 0x00, 0xEA, 0xFF };  // $CF25
        // The $CF60 anim sets whose live slots consume the weapon's budget: $CFCF[0] for
        // kind 0, $CF7A/$CF9B/$CFB5 for kinds 1/2/3.
        private static readonly byte[][] WeaponCountedAnims =
        {
            new byte[] { 0x13 },
            new byte[] { 0x16, 0x18, 0x14 },
            new byte[] { 0x14, 0x19, 0x1A },
            new byte[] { 0x14, 0x17, 0x29 },
        };

        /// <summary>What $CE39 writes into the projectile slot: the $CF29 preference, the
        /// player-relative world position and the four table bytes.</summary>
        public struct FireSpawn
        {
            public int SlotPref;     // $CF29[$53]
            public ushort X, Y;      // $04F0/$0500 and $0510/$0520
            public byte Flags;       // $0400,X = $0400
            public byte Direction;   // $0580,X = $52
            public byte Behavior;    // $CF2E[$53]
            public byte Template;    // $CF32[$53] through $FA6C
            public byte SfxId;       // $CF36[$53] through $F850
            public bool CutYVelocity; // indexed $F5FC when $CE39 left X at this shot
        }

        public FireSpawn PendingFire;
        public bool HasPendingFire;

        /// <summary>$CF3A: the per-weapon live-projectile budget. Slots 4..2 are walked
        /// ($CF47, with $CF4D's <c>CPY #$01</c> excluding slot 1 entirely) and only anims
        /// from the weapon's own set count toward it.</summary>
        public bool CanFire()
        {
            int w = WeaponActive;
            if (w > 4) return false;
            int count = 0;
            if (Objects != null)
            {
                for (int y = 4; y >= 2; y--)
                {
                    var t = Objects.Slots[y];
                    if (!t.Active) continue;
                    foreach (byte a in WeaponCountedAnims[w])
                        if (t.AnimNumber == a) { count++; break; }
                }
            }
            return count < WeaponMaxActive[w];
        }

        /// <summary>$CEF2: the meter has reached its last unit. Kinds 1/3 fire down to
        /// $05C0 = $71, kind 2 to $72; anything but $70 in $05BE leaves the meter open.</summary>
        private bool GasMeterEmpty(int kind)
            => ActiveGasEnergyHi == 0x70 && ActiveGasEnergyLo < (kind == 2 ? 0x73 : 0x72);

        /// <summary>
        /// $CE39. Three exits leave every slot untouched: the $FF entry at $CE43, which
        /// $CEF2 reaches by re-arming Y = 4 when the meter is empty (kind 0 never calls it,
        /// $CE3B), and the $CE52 abort when slots 4..2 are all live (the downward scan
        /// $CE4D-$CE50 stops before testing slot 1).
        /// </summary>
        public bool TryGetFireSpawn(out FireSpawn spawn)
        {
            spawn = default;
            int w = WeaponActive;
            if (w > 4) return false;
            int idx = (w != 0 && GasMeterEmpty(w)) ? 4 : w;
            if (WeaponSlotPref[idx] == 0xFF) return false;

            int side = (DirBits & 2) == 0 ? 0 : 2;
            byte[] off = w is 2 or 3 ? WeaponOffsetFar : WeaponOffsetNear;
            ushort x = (ushort)(X + (short)(off[side] | (off[side + 1] << 8)));

            // $CEA1 branches on the PLAYER anim; only the low byte is adjusted, $0520 is
            // copied plain at $CECD (the $CEB4 subtract never borrows from the page).
            byte anim = Objects != null ? Objects.Slots[0].AnimNumber : (byte)0;
            int ylo = Y & 0xFF;
            if (anim == 0x07)
            {
                int raised = (ylo + 8) & 0xFF;
                // $CEC5 falls to $CEC7 on zero, which re-reads the PLAYER's $0510 and
                // stores it unchanged — the +8 is lost exactly on that wrap.
                ylo = raised == 0 ? ylo : raised;
            }
            else if (anim != 0x0E && anim != 0x0F) ylo = (ylo - 3) & 0xFF;

            spawn = new FireSpawn
            {
                SlotPref = WeaponSlotPref[idx],
                X = x,
                Y = (ushort)((Y & 0xFF00) | ylo),
                Flags = Flags,
                Direction = DirBits,
                Behavior = WeaponBehavior[w],
                Template = WeaponTemplate[w],
                SfxId = WeaponSfx[w],
            };
            return true;
        }

        /// <summary>
        /// The six $CE39 sites all sit in the player's state machine, and the original
        /// commits the slot there and then — before the bank-6 dispatch, in the same frame
        /// (gold_b_attack f30: $04D0 = $81 with $0540 = $04, so $82F0's init ran, and X is
        /// the $3B spawn plus exactly one 4 px move). The port latches here and
        /// DarkwingGame.Step commits immediately after Player.Step.
        /// </summary>
        private void TryLatchFire()
        {
            if (TryGetFireSpawn(out FireSpawn sp))
            {
                PendingFire = sp;
                HasPendingFire = true;
            }
        }

        // ---------- Damage ($F9C2) ----------
        /// <summary>
        /// Survived hits, 0..3 — the 4th kills. Physically this is the tile bytes of the
        /// player's RAM metasprite template: $F9C2 walks $05B4 ($80→$82), $05B8 and $05B6
        /// ($81→$83), then $05B2 ($80→$82) plus the death write; $C850 copies the 18-byte
        /// $C8CF template back over $05B0…$05C1, which is a FULL HEAL. Its two call sites
        /// are $D55D (mission intro-card release, so every mission starts at 3 hearts) and
        /// $E40C (door load with $5D ≠ 0 — $E40A branches to $E43D when $5D is 0, so a
        /// normal door load does NOT heal). Bank 6 $89D6 is the repair side: one rung per
        /// call, full reset when the object's $0420 is $22 (not ported yet).
        /// </summary>
        public byte HitLadder; // 0-3 survived hits
        private bool _wasAirborne;
        private bool _vertFalling;   // $CD6A sign test: the falling branch of $F2D7 ran
        private byte _probeResult;   // $10 as left by the last $EB52 inside $CD0E

        /// <summary>Diagnostics for the golden harness: the state the dispatcher entered with
        /// and which hurt site ($CB93 tile = 1, any other $F9C2 caller = 2) fired this frame.</summary>
        public byte PrevState;
        public byte HurtOrigin;
        public byte ProbeResultForTest => _probeResult;   // $10 after the last probe service

        /// <summary>
        /// $F9C2 hurt ladder. It runs ONLY for a caller that has just written
        /// $7C=$3C (all six sites do: $8160, $D138, $D268, $D73C, $F476, $8E60) —
        /// $F9C2 tests $7C and RTS's otherwise. It writes no velocity: the knockback
        /// comes from the state-6 handler on the NEXT frame ($D2C3 → $0530/$0540).
        /// </summary>
        /// <param name="hazardAnim">$0420 of the object that hurt the player ($FA29).</param>
        public void TakeHit(int hazardAnim = 0)
        {
            HurtOrigin = 2;                                     // $F9C2 reached from a slot site
            if (InvulnFrames != 0x3C) return;                   // $F9C3/$F9C6
            StateTimer = 0x0F;                              // $F9CA
            if (State != 0x04 && State != 0x0E && State != 0x0D)
                SetAnimPlayer(HurtAnim(hazardAnim));        // $FA29 → $FA78
            SavedState = State;                             // $F9E2
            State = 6;                                      // $F9E6
            if (HitLadder >= 3)                             // $FA11: the 4th hit dies
            {
                HitLadder = 4;
                InvulnFrames = 0;                           // $FA18
                StartDeath();                               // $FA1A: $50=$07
                if (Objects != null) Objects.Slots[0].StateLow = 0x80; // $FA1E
                SetAnimPlayer(0x09);                        // $FA23
                return;
            }
            HitLadder++;
        }

        /// <summary>$FA29 hurt-anim chooser: the two special forms only when the
        /// $6D checkpoint id and the hazard's anim match; otherwise the plain hurt $09.</summary>
        private byte HurtAnim(int hazardAnim)
        {
            if (CheckpointId == 0x08 && hazardAnim == 0x4D) return 0x1D;
            if (CheckpointId == 0x0C && hazardAnim == 0x5D) return 0x1C;
            return 0x09;
        }

        // ---------- Enemy/pickup meter hooks (fixed-bank $890B/$8982/$88FF sites) ----------

        /// <summary>$890B add/subtract the saved gas meter ($60/$61, codes $70-$7A = 0-10).
        /// A with bit7 set subtracts (~A & $7F) units; otherwise adds A units.</summary>
        public void AddSavedMeter(byte a)
        {
            if ((a & 0x80) != 0)
            {
                int amount = ((~a) & 0x7F) + 1; // $892F CLC / SBC: includes the extra borrow
                int v = (GasEnergyHi - 0x70) * 10 + (GasEnergyLo - 0x70) - amount;
                if (v < 0) v = 0;
                GasEnergyHi = (byte)(0x70 + v / 10);
                GasEnergyLo = (byte)(0x70 + v % 10);
                return;
            }
            if (GasEnergyLo == 0) { GasEnergyHi = 0x70; GasEnergyLo = 0x70; }
            int val = (GasEnergyHi - 0x70) * 10 + (GasEnergyLo - 0x70) + a;
            if (val > 99) val = 99;
            GasEnergyHi = (byte)(0x70 + val / 10);
            GasEnergyLo = (byte)(0x70 + val % 10);
        }

        /// <summary>$88CE: a weapon grant promotes the saved meter into the active one.</summary>
        public void CommitSavedGasMeter()
        {
            ActiveGasEnergyHi = GasEnergyHi;
            ActiveGasEnergyLo = GasEnergyLo;
        }

        /// <summary>$8982 add/subtract the active gas meter ($05BE/$05C0).</summary>
        public void DrainActiveMeter(byte a) => AddActiveMeterRaw(a, ActiveGasEnergyHi, ActiveGasEnergyLo,
            (hi, lo) => { ActiveGasEnergyHi = hi; ActiveGasEnergyLo = lo; });

        private static void AddActiveMeterRaw(byte a, byte hi, byte lo, System.Action<byte, byte> set)
        {
            int v;
            if ((a & 0x80) != 0)
            {
                v = (hi - 0x70) * 10 + (lo - 0x70) - (((~a) & 0x7F) + 1); // $89AC CLC / SBC
            }
            else
            {
                if (lo == 0) { hi = 0x70; lo = 0x70; }
                v = (hi - 0x70) * 10 + (lo - 0x70) + a;
            }
            if (v < 0) v = 0;
            if (v > 99) v = 99;
            set((byte)(0x70 + v / 10), (byte)(0x70 + v % 10));
        }

        /// <summary>Add plain units to the active meter (bonus pickups via $8957).</summary>
        public void AddActiveGasUnits(int units) => AddActiveMeterRaw((byte)units, ActiveGasEnergyHi, ActiveGasEnergyLo,
            (hi, lo) => { ActiveGasEnergyHi = hi; ActiveGasEnergyLo = lo; });

        /// <summary>$8A7D cascade: add 1/5 to the bonus BCD digits ($05C4-$05CC).</summary>
        public void AddEnergyDigit(int n) => ScoreUnits += n;

        /// <summary>$8AF4 add to the HUD energy column ($05E6, codes $70-$79).</summary>
        public void AddHudEnergy(int n)
        {
            int v = (EnergyColRight - 0x70) + n;
            if (v > 9) { v -= 10; if (EnergyColLeft < 0x79) EnergyColLeft++; EnergyColRight = (byte)(0x70 + v); }
            else EnergyColRight = (byte)(0x70 + v);
        }

        /// <summary>$FA78 player SetAnim: slot-0 anim + frame/timer reset.</summary>
        public void SetAnimPlayer(byte anim)
        {
            if (Objects == null) return;
            var s0 = Objects.Slots[0];
            s0.AnimNumber = anim;
            s0.AnimFrame = 0;
            s0.AnimTimer = 0;
        }

        private void ForcedHopState()
        {
            // $D590: falling through a one-way surface has two independent 17-frame
            // guards ($5A/$9D); $0480 is only the final 18-frame empty-space timeout.
            IntegrateY();
            if (_dropSupportTimer != 0 || PushPairOrScan(out _))
            {
                ApplyGravity(); _dropSupportTimer++;
                if (_dropSupportTimer == 0x11)
                {
                    State = 0x0E; SetAnimPlayer(0x0B);
                    HopTimer = _dropSupportTimer = 0; CutVelocity();
                }
                return;
            }
            ApplyGravity();
            byte floor = ProbeFloor();
            if (floor >= 2)
            {
                SnapFeetUpToRow();
                if (floor == 2 && (byte)YVelInt < 0xFC) { CutVelocity(); State = 0; return; }
                State = HopTimer = 0; return;
            }
            if (HopTimer != 0 || _probe43 != 0)
            {
                if (++HopTimer == 0x11)
                { State = 4; SetAnimPlayer(0x0B); Sfx?.Invoke(0x12); HopTimer = 0; CutVelocity(); }
                return;
            }
            var s0 = Objects?.Slots[0];
            if (s0 != null && ++s0.T480 >= 0x12) State = 1;
        }

        // ---------- State 0C: hang on hook ($D7F9) ----------
        /// <summary>
        /// $F539: add the 8.8 velocity into ($0520:$0510) where $0510 is the offset
        /// inside a 240 px row — the add's carry out of $0510 is DISCARDED (only
        /// $0510 is stored), and the row count in $0520 advances solely through the
        /// "$0510 >= $F0" wrap ($0510 += $0F with the CMP carry = +16, $0520 += 1).
        /// </summary>
        private void IntegrateYRide()
        {
            int frac = YFrac + YVelFrac;                    // $F53C CLC / ADC $0550
            YFrac = (byte)frac;
            int lo = (Y & 0xFF) + (YVelInt & 0xFF) + (frac >> 8); // $F546 ADC $0560
            Y = (ushort)(((Y & 0xFF00) | (lo & 0xFF)) & 0xFFFF);  // $F549 STA $0510 only
            if ((Y & 0xFF) >= 0xF0)                          // $F54C CMP #$F0
            {
                int wrapped = (Y & 0xFF) + 0x0F + 1;         // $F550 ADC #$0F (C=1 from CMP)
                Y = (ushort)((((Y + 0x100) & 0xFF00) | (wrapped & 0xFF)) & 0xFFFF); // $F552/$F55A
            }
        }

        /// <summary>$D7F9: released when the vertical scroll byte $FA returns to 0.</summary>
        private void HangState(byte held, byte pressed)
        {
            if (ScrollY == 0) { State = 1; return; }         // $D7F9-$D801
            if (CellGridByte == 0x90) Flags |= 0x20;         // $D802-$D80F
            IntegrateYRide();                                // $D812
            var s0 = Objects?.Slots[0];
            if (s0 == null) return;
            // $D815-$D824: only anims 6, $0D and 4 are pinned; others leave the
            // animation alone (the $D824 RTS path).
            if (s0.AnimNumber == 6 || s0.AnimNumber == 0x0D) s0.AnimFrame = 1; // $D825-$D827
            else if (s0.AnimNumber == 4) s0.AnimFrame = 2;                      // $D82C-$D82E
            else return;
            s0.AnimTimer = 0;                                                    // $D831-$D833
        }
    }
}
