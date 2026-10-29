using System;

namespace Darkwing.Core
{
    /// <summary>Coordinates an actor mover reads and writes; prices walk a copy so a following probe
    /// reads the moved actor, never the state from before the behaviour.</summary>
    public struct ActorMotion
    {
        public int X, Y;
        public byte XFrac, YFrac, YVelFrac;
        public sbyte YVelInt;
        public byte ProbeClass;

        public static ActorMotion From(ObjectSystem.Slot slot) => new ActorMotion
        {
            X = slot.X, Y = slot.Y, XFrac = slot.XFrac, YFrac = slot.YFrac,
            YVelFrac = slot.YVelFrac, YVelInt = slot.YVelInt
        };
    }

    /// <summary>
    /// Prices of the shared bank-7 actor movers for actor slots (X ≥ 2): $F200/$F254 move X then probe
    /// for a wall, $F2D7 moves Y with gravity and probes for ground or ceiling. The player-only and
    /// slot-1 branches ($F612/$F64A platforms, the $5C hook latch) are skipped by CPX at entry.
    /// </summary>
    public static class ActorMotionTiming
    {
        private const int Facing = 4 + 2 + 5 + 6;            // $F24B / $F2A6
        private const int VelocityReset = 2 + 5 + 2 + 5 + 6; // $F5FC

        /// <summary>$F200 (right, A=first point) or $F254 (left). Leaves the probe class ($10).</summary>
        public static int MoveX(ref ActorMotion motion, byte xVelInt, byte xVelFrac, bool left, int first, int countMinusOne,
            PlayerCore player, ITileMap tiles)
        {
            int cycles = 3 + 3 + 6 + IntegrateX(ref motion, xVelInt, xVelFrac, left) + 2 + 3;
            var probe = new ActorProbeTiming(player, tiles);
            cycles += 6 + probe.MeasureProbe(motion.X, motion.Y, first, countMinusOne + 1, out byte cls);
            motion.ProbeClass = cls;
            cycles += 3 + 2;
            if (cls < 3) return cycles + 3 + Facing;
            int nibble = probe.LastPointX & 15;
            if (left)
            {
                // $ED9D: X += (~$06 & 15) + 1.
                int low = (motion.X & 0xFF) + ((~nibble) & 15) + 1;
                motion.X = ((motion.X & 0xFF00) + low) & 0xFFFF;
                return cycles + 2 + 6 + 3 + 2 + 2 + 2 + 4 + 5 + 4 + 2 + 5 + 6 + Facing;
            }
            // $ED85: X −= $06 & 15.
            motion.X = (motion.X - nibble) & 0xFFFF;
            return cycles + 2 + 6 + 3 + 2 + 3 + 4 + 2 + 3 + 5 + 4 + 2 + 5 + 6 + Facing;
        }

        /// <summary>$F2D7 for actors: Y integration, gravity, then a probe of the points below (falling)
        /// or above (rising: $40 += $41+1). A hit snaps onto the tile and resets the velocity to $FF.AB.
        /// Returns the carry: set when it landed.</summary>
        public static int MoveY(ref ActorMotion motion, byte gravity, int first, int countMinusOne, PlayerCore player, ITileMap tiles,
            out bool landed)
        {
            landed = false;
            int cycles = 3 + 3 + 4 + 3 + 4 + 3 + 4;
            var probe = new ActorProbeTiming(player, tiles);
            if (motion.YVelInt < 0)
            {
                cycles += 3 + 6 + IntegrateYDown(ref motion) + 2 + 4;   // BMI, JSR $F5A4, CPX/BCS crossing $F2/$F3
                // JSR $F5DA, LDA $5C / BNE: a set hook latch detours through $F366 CPX #1 / BCS $F326.
                cycles += 6 + Gravity(ref motion, gravity) + 3 + (player.HookLatchForTiming != 0 ? 3 + 2 + 3 : 2);
                cycles += 6 + probe.MeasureProbe(motion.X, motion.Y, first, countMinusOne + 1, out byte cls) + 2 + 3 + 2;
                motion.ProbeClass = cls;
                if (cls == 0) return cycles + 3 + 6;                      // BCC $F34B → RTS
                cycles += 2;                                               // BCC not taken
                if (cls == 1)
                {
                    cycles += 2 + 3 + 2 + 2;                               // BNE not taken, LDA $07 / AND / CMP #8
                    if ((probe.LastPointY & 15) >= 8) throw new NotSupportedException("F360 actor half-tile reprobe");
                    cycles += 2;
                }
                else cycles += 3;                                          // BNE $F33A
                cycles += 2 + 3;                                           // CPX #1 / BCS $F344
                cycles += 6 + SnapUp(ref motion, probe.LastPointY & 15);   // $EDD2
                motion.YVelFrac = 0xAB; motion.YVelInt = -1;
                landed = true;
                return cycles + 6 + VelocityReset + 2 + 6;
            }
            cycles += 2 + 3 + 3 + 2 + 3 + 3;                               // JMP $F3C4, $40 += $41 + 1
            first += countMinusOne + 1;
            cycles += 6 + IntegrateYUp(ref motion) + 2 + 4;               // JSR $F581, CPX/BCS crossing $F3/$F4
            cycles += 6 + Gravity(ref motion, gravity);
            cycles += 6 + probe.MeasureProbe(motion.X, motion.Y, first, countMinusOne + 1, out byte rise) + 3 + 2;
            motion.ProbeClass = rise;
            if (rise == 0) return cycles + 3 + 2 + 6;                      // BCC $F474: CLC / RTS
            cycles += 2 + 2 + 3;                                           // BCC not taken, CPX #1, BCS $F46E
            cycles += 6 + SnapDown(ref motion, probe.LastPointY & 15);     // $EDB3
            motion.YVelFrac = 0xAB; motion.YVelInt = -1;
            return cycles + 6 + VelocityReset + 2 + 6;
        }

        public static int IntegrateX(ref ActorMotion motion, byte velocity, byte velocityFraction, bool left)
        {
            int low;
            bool pageChange;
            if (left)
            {
                int fraction = motion.XFrac - velocityFraction;
                low = (motion.X & 0xFF) - velocity - (fraction < 0 ? 1 : 0);
                motion.XFrac = (byte)fraction;
                pageChange = low < 0;
            }
            else
            {
                int fraction = motion.XFrac + velocityFraction;
                low = (motion.X & 0xFF) + velocity + (fraction >> 8);
                motion.XFrac = (byte)fraction;
                pageChange = low > 255;
            }
            motion.X = ((motion.X & 0xFF00) + low) & 0xFFFF;
            return 4 + 2 + 4 + 5 + 4 + 4 + 5 + (pageChange ? 2 + 4 + 2 + 5 : 3) + 6;
        }

        /// <summary>$F5A4: Y −= velocity (a negative velocity moves down), skipping rows $F0..$FF.</summary>
        private static int IntegrateYDown(ref ActorMotion motion)
        {
            int fraction = motion.YFrac - motion.YVelFrac;
            int low = ((motion.Y & 0xFF) - (byte)motion.YVelInt - (fraction < 0 ? 1 : 0)) & 0xFF;
            motion.YFrac = (byte)fraction;
            int page = motion.Y >> 8;
            bool wrap = low >= 0xF0;
            if (wrap) { low = (low + 0x10) & 0xFF; page++; }
            motion.Y = ((page & 0xFF) << 8) | low;
            return 4 + 2 + 4 + 5 + 4 + 4 + 5 + 2 + (wrap ? 2 + 2 + 5 + 4 + 2 + 5 : 3) + 6;
        }

        /// <summary>$F581: Y −= velocity moving up; a borrow drops 16 rows and the page.</summary>
        private static int IntegrateYUp(ref ActorMotion motion)
        {
            int fraction = motion.YFrac - motion.YVelFrac;
            int low = (motion.Y & 0xFF) - (byte)motion.YVelInt - (fraction < 0 ? 1 : 0);
            motion.YFrac = (byte)fraction;
            int page = motion.Y >> 8;
            bool borrow = low < 0;
            if (borrow) { low = (low - 0x10) & 0xFF; page--; }
            motion.Y = ((page & 0xFF) << 8) | (low & 0xFF);
            return 4 + 2 + 4 + 5 + 4 + 4 + 5 + (borrow ? 2 + 2 + 5 + 4 + 2 + 5 : 3) + 6;
        }

        /// <summary>$F5DA: velocity −= $99, clamped at $F8.00 when falling faster.</summary>
        private static int Gravity(ref ActorMotion motion, byte gravity)
        {
            int fraction = motion.YVelFrac - gravity;
            byte stored = (byte)((byte)motion.YVelInt - (fraction < 0 ? 1 : 0));
            motion.YVelFrac = (byte)fraction;
            int cycles = 4 + 2 + 3 + 5 + 4 + 2 + 5;
            if (stored < 0x80) { motion.YVelInt = (sbyte)stored; return cycles + 3 + 6; }
            cycles += 2 + 2;
            if (stored >= 0xF9) { motion.YVelInt = (sbyte)stored; return cycles + 3 + 6; }
            motion.YVelInt = -8; motion.YVelFrac = 0;
            return cycles + 2 + 2 + 5 + 2 + 5 + 6;
        }

        /// <summary>$EDD2: Y −= $07 & 15 (onto the tile top).</summary>
        private static int SnapUp(ref ActorMotion motion, int nibble)
        {
            int low = (motion.Y & 0xFF) - nibble;
            int page = motion.Y >> 8;
            bool borrow = low < 0;
            if (borrow) { low = (low - 0x10) & 0xFF; page--; }
            motion.Y = ((page & 0xFF) << 8) | (low & 0xFF);
            return 3 + 2 + 3 + 4 + 2 + 3 + 5 + (borrow ? 2 + 2 + 5 + 4 + 2 + 5 : 3) + 6;
        }

        /// <summary>$EDB3: Y += (~$07 & 15) + 1 (below the ceiling tile).</summary>
        private static int SnapDown(ref ActorMotion motion, int nibble)
        {
            int low = ((motion.Y & 0xFF) + ((~nibble) & 15) + 1) & 0xFF;
            int page = motion.Y >> 8;
            bool wrap = low >= 0xF0;
            if (wrap) { low = (low + 0x10) & 0xFF; page++; }
            motion.Y = ((page & 0xFF) << 8) | low;
            return 3 + 2 + 2 + 2 + 4 + 5 + 2 + (wrap ? 2 + 2 + 5 + 4 + 2 + 5 : 3) + 6;
        }
    }
}
