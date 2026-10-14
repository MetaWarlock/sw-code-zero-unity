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
        public byte ScrollYRow = 3;     // $FB (world grid row of the camera window)
        public byte ScrollY;            // $FA (intra-row vertical scroll, 0..$EF, 3 px/frame steps)
        public byte CellIndex;          // $22 (linear cell index = ScrollYRow*GridWidth + camera column)
        public byte CellIndexNext;      // $23 (incoming cell during horizontal scroll)
        public byte RowStride = 17;     // $2E (linear index step for one grid row)
        public byte BottomRow;          // $2D (lowest row the camera may scroll down into)
        public byte ScrollStep;         // $10 (px/frame handed to $DC6E/$DCC4)
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
        public byte BonusDigits;        // $05C4-$05CC pickup cascade (mod-10 accumulator)
        public byte TilesetBank;        // $36 section metatile-set bank ($E38F)
        public byte SectionFlag3D;      // $3D section table byte — semantics unknown (G16)
        public byte SectionRingEvent;   // $D9 section ring/event byte ($E3BA → JSR $F84E)
        public byte ChrLatch0;          // $1A CHR request ($E3AB, applied by NMI $FF21)
        public byte ChrLatch1;          // $1B CHR request ($E3B0, $FF3B)

        private ITileMap _tiles;
        public ITileMap TileMap { get => _tiles; set => _tiles = value; }
        public ObjectSystem Objects;  // slot 0 shares the player's anim state (original RAM model)
        public System.Action<int> Sfx; // wired to DarkwingGame.PlaySfx ($F84E/$F850 sites)
        private readonly byte[] _probeX = new byte[64];  // $06 = last sampled X ($EB52 uses $06/$07)
        private readonly byte[] _probeY = new byte[64];

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

        /// <summary>Integrate Y by the up-positive velocity: Y -= v ($F5A4 semantics).</summary>
        private void IntegrateY()
        {
            int v = (YVelInt << 8) | YVelFrac;          // signed 8.8, up-positive
            int acc = (Y << 8) | YFrac;
            acc -= v;                                   // original subtracts velocity
            Y = (ushort)((acc >> 8) & 0xFFFF);
            YFrac = (byte)(acc & 0xFF);
        }

        /// <summary>Gravity $F5DA: velocity -= $99/256 (0.25), signed; clamp fall to 8.00.</summary>
        public void ApplyGravity()
        {
            int v = VelToInt(YVelFrac, YVelInt);
            v -= GravityFrac; // $99 = $40 = 0.25 px/frame^2
            // terminal fall $F5EB: velocity >= 8.00 (i.e. v <= -0x0800) -> clamp
            if (v < -0x0800) v = -0x0800;
            YVelInt = (sbyte)((v >> 8) & 0xFF);
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

        public byte ProbeFloor()  // $40=6 → points 6-8
        {
            byte result = 0;
            for (int i = 0; i < 3; i++)
            {
                int px = (short)(X + FloorXOff[i]);
                int py = (short)(Y + 12);
                result = Math.Max(result, TileTypeAt(px, py));
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
                int py = (short)(Y - 12);
                result = Math.Max(result, TileTypeAt(px, py));
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
                int py = (short)(Y + SideYOff[i]);
                result = Math.Max(result, TileTypeAt(px, py));
            }
            HandleProbeType(result);   // $EBAA tail
            return result;
        }

        private byte TileTypeAt(int wx, int wy)
        {
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
        /// $E38F-$E406 plus the $DE24 reset list: put the camera and the player at
        /// the section start and enter the spawn state $50=$12. $E388 does NOT touch
        /// $32/$34/$2E, so the world grid and row stride stay as loaded — only the
        /// cell/row the camera points at changes. The original also zeroes
        /// $28/$29/$70/$71/$57/$5E/$63/$9B/$3B/$9D/$F8 and rebuilds the VRAM queue
        /// through $E698; the caller's row reload is that queue's equivalent here.
        /// $5D (continue count) is 0 in a normal door load, so the $C850 seed-array
        /// branch at $E408 is not taken.
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
            ChrLatch0 = t.ChrRequest0(id);                  // $1A ($E3AB)
            ChrLatch1 = t.ChrRequest1(id);                  // $1B ($E3B0)
            SectionFlag3D = t.Flag3D(id);                   // $3D ($E3B5)
            SectionRingEvent = t.RingEvent(id);             // $D9 ($E3BA)
            ScrollY = 0;                                    // $FA ($DE3C)
            DirBits = 1;                                    // $52 ($DE2C)
            ScrollState = 1;                                // $2B ($DE30)
            ScrollMode = 1;                                 // $2F ($DE32)
            StateTimer = 0;                                 // $51 ($DE5A clears $50)
            AirSubState = 0;                                // $56
            SavedState = 0;                                 // $58
            HopTimer = 0;                                   // $5A
            LandedFlag = 0;                                 // $5C
            GasActive = 0;                                  // $5F
            DeathFlag = 0;                                  // $64
            LevelActive = -1;                               // $65
            GasToggle = 0;                                  // $98
            WallBlocked = 0;                                // $9E
            InvulnFrames = 0;                               // $7C ($80E9 path)
            CellGridByte = 0;                               // ($32),$22 refreshed next frame
            CellScriptByte = 0;                             // ($34),$22
            Flags = 0xD0;                                   // $0400 ($E3FA)
            if (Objects != null)
            {
                var s0 = Objects.Slots[0];
                s0.AnimNumber = 0x2C;                       // $0420 ($FA78, A=$2C)
                s0.AnimTimer = 0;                           // $0430
                s0.AnimFrame = 0;                           // $0410
                s0.Active = true;                           // $04D0=$80
            }
            CutVelocity();                                  // $F5FC → FF,AB ($E401)
            State = 0x12;                                   // $50 ($E406)
            PendingEvent = 0;                               // $5B ($E3E8)
        }

        // ---------- Camera ($DA70/$DC19/$DBCE) ----------
        /// <summary>
        /// Horizontal dead-zone follow. On player move: if screenX passes the far
        /// edge ($88 moving left / $89 moving right) the scroll chases 1 px/frame
        /// until the player re-enters; then the player is pushed to 16 px inside
        /// the near edge. Verified against the captured gameplay trace; exact
        /// per-frame stepping re-verified in M3 golden comparisons.
        /// </summary>
        public void UpdateCamera(bool movedLeft, bool movedRight)
        {
            // $DA70/$DBCE/$DC19: the scroll delta = the player's move delta clamped
            // to [1,6] px/frame ($DBDF clamp), applied when the player is past the
            // dead-zone edge; the cell index rides the scroll high byte.
            int delta = (short)(X - PrevX);
            if (delta == 0) return;
            int amt = delta < 0 ? -delta : delta;
            if (amt > 6) amt = 6;
            if (amt < 1) amt = 1;
            int screenX = (short)(X - ScrollX);
            if (movedRight && screenX >= 0x89)
            {
                ScrollX = (ushort)(ScrollX + amt);
            }
            else if (movedLeft && screenX >= 0x88)
            {
                // chase left ($DC19 mirror): the window mirrors at the left edge
                ScrollX = (ushort)(ScrollX - amt);
            }
            // push-to-dead-zone after scroll ($DABD/$DAEE: 16px inside the edge)
            screenX = (short)(X - ScrollX);
            if (screenX < 0x10) ScrollX = (ushort)(X - 0x10);
        }

        // ---------- Main state dispatch ($CB41) ----------
        /// <summary>One frame of player logic. padBits: 7=A,6=B,5=Sel,4=Start,3=Up,2=Down,1=Left,0=Right.</summary>
        public void Step(byte held, byte pressed)
        {
            OnHookTileFlag = false;
            if (InvulnFrames > 0) InvulnFrames--;

            switch (State & 0x1F)
            {
                case 0x00: GroundState(held, pressed); break;
                case 0x01: AirState(held, pressed); break;
                case 0x02: CrouchState(held, pressed); break;
                case 0x03: LookUpState(held, pressed); break;
                case 0x06: HurtGroundState(held, pressed); break;
                case 0x0B: case 0x10: AirborneFullState(held, pressed); break;
                case 0x0C: HangState(held, pressed); break;
                case 0x0A: ForcedHopState(); break;
                case 0x07: DeathThroesState(); break;
                case 0x08: DeathResolveState(); break;
                default: break; // remaining states wired during M4/M7
            }
        }

        // ---------- State 7: death throes ($D353) ----------
        // Launch up 2.0 px/frame for $0F frames, pause $3C (SFX $25 at $1E), then fall.
        private byte _deathPhase; // 0 rise, 1 pause, 2 fall
        private byte _deathTimer;

        public void StartDeath()
        {
            State = 7;
            _deathPhase = 0;
            _deathTimer = 0x0F;
            YVelInt = 2; YVelFrac = 0;
            DeathFlag = 1;
        }

        private void DeathThroesState()
        {
            switch (_deathPhase)
            {
                case 0:
                    IntegrateY();
                    if (--_deathTimer == 0) { _deathPhase = 1; _deathTimer = 0x3C; }
                    break;
                case 1:
                    if (--_deathTimer == 0) { _deathPhase = 2; _deathTimer = 0; }
                    break;
                case 2:
                    IntegrateY();
                    ApplyGravity();
                    break;
            }
            if (_deathPhase == 2 && _deathTimer < 0xFF) _deathTimer++;
            if (_deathPhase == 2 && _deathTimer >= 2) State = 8;
        }

        // ---------- State 8: death resolve ($D41B) ----------
        private void DeathResolveState()
        {
            // Checkpoint respawn (doc §6): reload section at $6D, spawn pose at
            // X=$30, Y=$06, state $12 walk-out. Game-over path ($64) is M7.
            RespawnAtCheckpoint();
        }

        public void RespawnAtCheckpoint()
        {
            X = 0x30; XFrac = 0;
            Y = 0x06; YFrac = 0;
            YVelInt = 0; YVelFrac = 0;
            Flags = 0xD0;
            State = 0x12; // walk-out transition
            DeathFlag = 0;
            InvulnFrames = 0;
        }

        // ---------- State 0: ground ($CB8B) ----------
        private void GroundState(byte held, byte pressed)
        {
            // Re-arm walk velocity every frame ($CB43): 1.5 px/frame
            XVelInt = 1; XVelFrac = 0x80;

            // Shared vertical physics first ($CD0E via $CB8B): gravity + landing
            bool landed = VerticalPhysics(held, pressed);
            if (State != 0) return; // jumped inside VerticalPhysics

            // Damage from spike/contact probes is applied by the world (M4).

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
                // Walk anim + footstep dust ($CC53/$CC84/$CCAA): anim 3, cycle 1-4,
                // frame 0 with timer<4 = dust (velocity 0.28), fresh start dusts once.
                var s0 = Objects != null ? Objects.Slots[0] : null;
                bool fresh = s0 == null || s0.AnimNumber != 3;
                if (fresh)
                {
                    if (s0 != null) { s0.AnimNumber = 3; s0.AnimFrame = 0; s0.AnimTimer = 0; }
                }
                else if (s0.AnimFrame == 0 && s0.AnimTimer == 0)
                {
                    s0.AnimFrame = 1; // $CC84 cycle wrap skips the dust frame
                }
                if (s0.AnimFrame == 0 && s0.AnimTimer < 4)
                {
                    XVelInt = 0; XVelFrac = 0x48; // footstep dust: 0.28 px/frame ($CCAA)
                }
                bool right = (held & 0x01) != 0;
                FacingRight = right ? (byte)0x40 : (byte)0x00;
                Flags = (byte)((Flags & ~0x40) | FacingRight);
                DirBits = (byte)(held & 0x03);
                int before = X;
                MoveHorizontal(right);
                bool moved = X != before;
                UpdateCamera(!right, right && moved);
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

            if ((held & 0x0C) == 0x08) // Up only → look up ($CBEF)
            {
                State = 3;
                return;
            }
            if ((held & 0x0C) == 0x04) // Down → crouch ($CC44)
            {
                State = 2;
                return;
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

        private void SnapX(bool right)
        {
            int px = X;
            if (right)
            {
                // pushed right into wall: snap left edge of the wall tile: X -= X&0F + recheck
                int tile = (px + 12) >> 4; // wall column
                px = (tile << 4) - 13;
            }
            else
            {
                int tile = (px - 12) >> 4;
                px = ((tile + 1) << 4) + 12;
            }
            X = (ushort)(px & 0xFFFF);
        }

        // ---------- Shared vertical physics ($CD0E/$F2D7) ----------
        /// <returns>true if landed this frame</returns>
        private bool VerticalPhysics(byte held, byte pressed)
        {
            bool falling = YVelInt < 0;
            _wasAirborne = State is 1 or 0x0B or 0x10;
            if (falling)
            {
                IntegrateY();               // down
                ApplyGravity();
                byte floor = ProbeFloor();
                bool slowFall = YVelInt >= -3; // vel >= $FD (-3) per $F4EC
                bool oneWayLand = floor == 1 && slowFall && ((Y + 12) & 0x0F) >= 8;
                if (floor == 2 || floor == 3 || floor >= 8 || oneWayLand)
                {
                    // land ($F360): snap onto tile top ($EDB3)
                    int py = Y + 12;
                    py = py - (py & 0x0F) - 12;  // top of the 16px tile under the probe
                    Y = (ushort)(py & 0xFFFF);
                    CutVelocity(); // landing leaves the $F607 value (-0.33), capture-verified
                    LandedFlag = 1;
                    if (_wasAirborne) Sfx?.Invoke(0x10); // landing SFX ($CE08: SFX $10)
                    return true;
                }
                return false;
            }
            else
            {
                // rising ($F3C4): variable jump cut FIRST ($CD47), then integrate.
                if (((held & 0x80) == 0) && YVelInt < 5)
                {
                    CutVelocity();
                }
                IntegrateY();
                ApplyGravity();
                byte ceil = ProbeCeiling();
                if (ceil >= 3)
                {
                    CutVelocity(); // head bonk: cancel ascent ($F607)
                    int py = Y - 12;
                    py = py + (0x0F - (py & 0x0F)) + 1 + 12;
                    Y = (ushort)(py & 0xFFFF);
                }
                return false;
            }
        }

        // ---------- State 0B/10: full airborne control ($D626) ----------
        private void AirborneFullState(byte held, byte pressed)
        {
            AirborneControl(held);
            bool landed = VerticalPhysics(held, pressed);
            if (landed) { State = 0; AirSubState = 0; }
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
            if (YVelInt >= 0 && YVelInt < 5 && (held & 0x80) == 0) CutVelocity();
            bool landed = VerticalPhysics(held, pressed); // $CD0E: vertical first
            AirborneControl(held);                        // then air steering ($CD18)
            if (landed)
            {
                State = 0;
                AirSubState = 0;
            }
        }

        /// <summary>Full air steering at walk speed ($CD18/$D78D).</summary>
        private void AirborneControl(byte held)
        {
            if ((held & 0x03) != 0)
            {
                bool right = (held & 0x01) != 0;
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
            if ((held & 0x04) == 0) { State = 0; return; } // Down released → stand

            if ((pressed & 0x40) != 0)
            {
                // crouch fire ($CFFB): spawns via weapon system (M4)
            }
            if ((pressed & 0x80) != 0) // A in crouch ($D00F)
            {
                byte below = ProbeFloor();
                if (below == 1 || below == 2)
                {
                    State = 0x0A; // forced hop ($D06F)
                    CutVelocity();
                    HopTimer = 0;
                }
                else if (below == 8) OnHookTileFlag = true;
            }
            // Down pressed edge while crouched ($D0B2 → $D11C): drop-through
            if ((pressed & 0x04) != 0)
            {
                byte below = ProbeFloor();
                if (below == 2)
                {
                    // drop-through ($D166): state 1 with downward velocity
                    State = 1;
                    CutVelocity();
                    Y += 1; // begin descent below the platform surface
                }
            }
        }

        // ---------- State 3: look up ($D16E) ----------
        private void LookUpState(byte held, byte pressed)
        {
            if ((held & 0x08) == 0) { State = 0; return; }
            if ((pressed & 0x80) != 0) // normal jump impulse ($D18B)
            {
                YVelInt = 4; YVelFrac = 0xE5;
                AirSubState = 1; // reach pose
                State = 1;
            }
        }

        // ---------- State 6: hurt ground/knockback ($D2C3) ----------
        private void HurtGroundState(byte held, byte pressed)
        {
            // knockback drift 0.5 px/frame, inverted steering ($D2C3)
            XVelInt = 0; XVelFrac = 0x80;
            bool steerRight = (held & 0x02) != 0; // Left held → drift right, etc.
            // inverted: held Right (bit0) pushes left; held Left pushes right
            if ((held & 0x03) != 0)
            {
                bool invertRight = (held & 0x01) != 0; // Right held → move left
                MoveHorizontal(!invertRight);
            }
            VerticalPhysics(held, pressed);
            StateTimer--;
            if (StateTimer == 0) State = 0;
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
            // The incoming-cell pointer follows the same step in the row-advance frame
            // (gold_hook_ride2 f80: $22 $1C→$2D while $23 $2D→$3E).
            CellIndexNext = (byte)(idx + RowStride);
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

        // ---------- Gas gun (doc §7, $CF3A/$CE39) ----------
        public byte ActiveGasEnergyHi;  // $05BE
        public byte ActiveGasEnergyLo;  // $05C0
        public byte GasToggleFlag;      // $98
        public int ProjectileCount;     // active projectile slots (set by world)

        private static readonly byte[] WeaponMaxActive = { 3, 1, 2, 1 };       // $CFD3
        private static readonly byte[] WeaponSlotPref = { 4, 2, 3, 2 };        // $CF29
        private static readonly byte[] WeaponBehavior = { 1, 2, 3, 4 };        // $CF2E
        private static readonly byte[] WeaponTemplate = { 0x13, 0x16, 0x14, 0x14 }; // $CF32
        private static readonly byte[] WeaponSfx = { 0x11, 0x13, 0x11, 0x14 }; // $CF36

        /// <summary>True when the current weapon may fire ($CF3A gate: max active).</summary>
        public bool CanFire(int activeProjectiles) => activeProjectiles < WeaponMaxActive[WeaponActive & 3];

        /// <summary>Fire request returns spawn parameters ($CE39); the world performs the spawn.</summary>
        public bool TryGetFireSpawn(out FireSpawn spawn)
        {
            spawn = default;
            if (!CanFire(ProjectileCount)) return false;
            bool left = (DirBits & 2) != 0 || ((DirBits & 1) == 0 && FacingRight == 0);
            int w = WeaponActive & 3;
            int off = w is 0 or 1 ? 15 : 22;
            spawn = new FireSpawn
            {
                OffsetX = left ? -off : off,
                OffsetY = -3,
                Behavior = WeaponBehavior[w],
                Template = WeaponTemplate[w],
                SfxId = WeaponSfx[w],
                Direction = DirBits,
            };
            return true;
        }

        public struct FireSpawn
        {
            public int OffsetX, OffsetY, Behavior, Template, SfxId, Direction;
        }

        // ---------- Damage ($F9C2) ----------
        /// <summary>
        /// Fresh enemy/spike hit: 4-hit ladder hidden in the $05B2-$05B8 template.
        /// hitFlash 1..4; the 4th hit is death.
        /// </summary>
        public byte HitLadder; // 0-3 survived hits
        private bool _wasAirborne;

        public void TakeHit()
        {
            if (InvulnFrames > 0 || State is 7 or 8) return;
            HitLadder++;
            InvulnFrames = 0x3C; // 60 frames
            Sfx?.Invoke(0x15); // hurt ($CB9D: SFX $15)
            if (HitLadder >= 4)
            {
                StartDeath();
            }
            else
            {
                SavedState = State;
                State = 6;
                StateTimer = 0x0F;
                CutVelocity();
            }
        }

        // ---------- Enemy/pickup meter hooks (fixed-bank $890B/$8982/$88FF sites) ----------

        /// <summary>$890B add/subtract the saved gas meter ($60/$61, codes $70-$7A = 0-10).
        /// A with bit7 set subtracts (~A & $7F) units; otherwise adds A units.</summary>
        public void AddSavedMeter(byte a)
        {
            if ((a & 0x80) != 0)
            {
                int amount = (~a) & 0x7F;
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

        /// <summary>$8982 add/subtract the active gas meter ($05BE/$05C0).</summary>
        public void DrainActiveMeter(byte a) => AddActiveMeterRaw(a, ActiveGasEnergyHi, ActiveGasEnergyLo,
            (hi, lo) => { ActiveGasEnergyHi = hi; ActiveGasEnergyLo = lo; });

        private static void AddActiveMeterRaw(byte a, byte hi, byte lo, System.Action<byte, byte> set)
        {
            int v;
            if ((a & 0x80) != 0)
            {
                v = (hi - 0x70) * 10 + (lo - 0x70) - ((~a) & 0x7F);
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
        public void AddEnergyDigit(int n) => BonusDigits = (byte)((BonusDigits + n) % 100);

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
            IntegrateY();
            ApplyGravity();
            byte floor = ProbeFloor();
            if (floor == 2 || floor == 3)
            {
                int py = Y + 12;
                Y = (ushort)((py - (py & 0x0F) - 12) & 0xFFFF);
                YVelInt = 0; YVelFrac = 0;
            }
            HopTimer++;
            if (HopTimer >= 0x12) { State = 1; HopTimer = 0; }
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
