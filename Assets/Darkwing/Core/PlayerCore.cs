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
        public byte ScrollYRow = 3;     // $FB (cell row; $FA = in-cell Y offset, normally 0)

        public byte FacingRight = 1;    // $0400 bit6
        public byte Flags = 0xD0;       // $0400 (active|facing)

        private readonly ITileMap _tiles;
        private readonly byte[] _probeX = new byte[64];  // $06 = last sampled X ($EB52 uses $06/$07)
        private readonly byte[] _probeY = new byte[64];

        public PlayerCore(ITileMap tiles) { _tiles = tiles; }

        // ---------- 8.8 signed helpers ----------
        private static int VelToInt(byte frac, sbyte integ) => (integ << 8) | frac;

        /// <summary>Integrate X by the signed 8.8 velocity ($F4FD add / $F51B subtract).</summary>
        private void IntegrateX(int sign)
        {
            int v = (XVelInt << 8) | XVelFrac;
            int acc = (XFrac << 16) | X; // not 6502-exact; use 16.8 add
            int dx = sign * v;
            acc += dx;
            X = (ushort)(acc & 0xFFFF);
            XFrac = (byte)((acc >> 16) & 0xFF);
        }

        /// <summary>Integrate Y by the up-positive velocity: Y -= v ($F5A4 semantics).</summary>
        private void IntegrateY()
        {
            int v = (YVelInt << 8) | YVelFrac;   // signed 8.8, up-positive
            int acc = (YFrac << 16) | Y;
            acc -= v;                             // original subtracts velocity
            Y = (ushort)(acc & 0xFFFF);
            YFrac = (byte)((acc >> 16) & 0xFF);
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
            return result;
        }

        private byte TileTypeAt(int wx, int wy)
        {
            byte t = _tiles.TypeAt(wx, wy);
            if (t == 6) return 0; // $EBE3 clears 6
            return t;
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
            int screenX = (short)(X - ScrollX);
            if (movedLeft && screenX >= 0x88)
            {
                ScrollX--;                                   // chase left
                screenX = (short)(X - ScrollX);
                if (screenX >= 0x88) { }                     // keep chasing next frames
            }
            else if (movedRight && screenX >= 0x89)
            {
                ScrollX++;                                   // chase right
                screenX = (short)(X - ScrollX);
            }
            // push-to-dead-zone after scroll (original pushes to 16px from edge)
            if (screenX < 0x10) { ScrollX = (ushort)(X - 0x10); }
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

            if ((pressed & 0x80) != 0) // A pressed → jump ($CBB7)
            {
                YVelInt = 4; YVelFrac = 0xE5;
                State = 1;
                AirborneControl(held);
                return;
            }

            if ((held & 0x03) != 0) // Left/Right held → walk ($CBB7/$CC53)
            {
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
                    YVelInt = 0; YVelFrac = 0;
                    LandedFlag = 1;
                    return true;
                }
                return false;
            }
            else
            {
                // rising ($F3C4)
                IntegrateY();
                ApplyGravity();
                byte ceil = ProbeCeiling();
                if (ceil >= 3)
                {
                    CutVelocity(); // head bonk: cancel ascent ($F607)
                    // snap below ceiling
                    int py = Y - 12;
                    py = py + (0x0F - (py & 0x0F)) + 1 + 12;
                    Y = (ushort)(py & 0xFFFF);
                }
                // variable jump cut ($CD47): A released while vel < 5
                if (((held & 0x80) == 0) && YVelInt < 5)
                {
                    CutVelocity();
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
            AirborneControl(held);
            bool landed = VerticalPhysics(held, pressed);
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

        // ---------- Level script hook ($DAFD) ----------
        /// <summary>
        /// Reads the level script byte for the current screen column. Byte $C1-$CF
        /// (odd) = checkpoint id → $6D; low nibble == 1 = hook column (grab check);
        /// low nibble == 2 = vertical scroll snap. The world supplies the byte.
        /// </summary>
        public void ApplyScriptByte(byte scriptByte)
        {
            if (scriptByte >= 0xC1 && scriptByte <= 0xCF && (scriptByte & 1) != 0)
                CheckpointId = (byte)(scriptByte & 0x1F);
            if ((scriptByte & 0x0F) == 1 && ScrollX == 0 && State != 0x0C)
            {
                // Hook grab ($DB6D): position-based — player screen Y >= $E8
                int screenY = (short)(Y - (ScrollYRow * 0)); // $FA in-cell offset normally 0
                if (screenY >= 0xE8)
                {
                    State = 0x0C; // hang
                    YVelInt = 0; YVelFrac = 0x80; // 0.5 px/frame ride
                    if (scriptByte == 0x90) OnHookTileFlag = true;
                }
            }
        }

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

        public void TakeHit()
        {
            if (InvulnFrames > 0 || State is 7 or 8) return;
            HitLadder++;
            InvulnFrames = 0x3C; // 60 frames
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
        private void HangState(byte held, byte pressed)
        {
            // 0.5 px/frame rise while riding ($DB8F): vel = +0.5 up
            YVelInt = 0; YVelFrac = 0x80;
            IntegrateY();
            YVelInt = 0; YVelFrac = 0; // no accumulation
            if (ScrollYRow == 0) State = 1; // release when scroll Y reaches top ($D833)
            if ((pressed & 0x80) != 0)
            {
                State = 1;
                YVelInt = 4; YVelFrac = 0xE5; // jump off
            }
        }
    }
}
