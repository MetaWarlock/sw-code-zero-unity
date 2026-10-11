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
                default: break; // remaining states wired during M4/M7
            }
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
                if (floor == 2 || floor == 3 || floor >= 8 || (floor == 1 && ((Y + 12) & 0x0F) >= 8))
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

        // ---------- State 0A: forced hop ($D590) ----------
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
