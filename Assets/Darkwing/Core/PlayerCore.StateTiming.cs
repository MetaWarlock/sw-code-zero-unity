using System;

namespace Darkwing.Core
{
    public sealed partial class PlayerCore
    {
        /// <summary>$CAFC → $CB41 table dispatch into a state handler (same for every state).</summary>
        private static int StateDispatchCycles()
            => 4 + ScrollBranch(0xCAFF, 0xCB41, true) + 2 + 2 + 5 + 2 + 5 + 3 + 2 + 2 + 4 + 3 + 4 + 3 + 5;

        /// <summary>$CFD7 crouch while Down is held: the crouch pose freezes on frame 1, then
        /// $D11C re-arms $0490 and falls into the $D121 floor test.</summary>
        public bool TryMeasureCrouch02Cycles(byte held, byte pressed, bool? audioQueueAccepts, out int cycles, out string unsupported)
        {
            cycles = -1;
            unsupported = null;
            try
            {
                if (State != 2) throw new NotSupportedException("CFD7 state outside02 slice");
                int c = StateDispatchCycles() + 3 + 2;
                if ((held & 4) == 0) { cycles = c + 2 + 3 + 2 + 3 + 6; return true; }   // JMP $D0AD: $50=0
                c += 3 + 4 + 2;
                var slot = Objects.Slots[0];
                if (slot.AnimNumber == 7)
                {
                    // $D08C crouch-shot pose: frame 2 timer 4 returns to the crouch pose; else $CFFB.
                    c += 2 + 3 + 4 + 2;
                    if (slot.AnimFrame != 2) c += 3 + 3;
                    else
                    {
                        c += 2 + 4 + 2;
                        if (slot.AnimTimer == 4) { cycles = c + 3 + 2 + 6 + 20 + 2 + 4 + 2 + 4 + 6; return true; }
                        c += 2 + 3;
                    }
                    c += 3 + 2;
                    if (pressed == 0x40)
                    {
                        // $D001 JSR $CF3A: a free bullet poses 7 ($FA78) and fires through $CE39.
                        c += 2 + 6 + ExpandedBulletBudget(out bool allowed);
                        if (allowed) { cycles = c + 2 + 2 + 26 + 6 + ExpandedNormalShot(audioQueueAccepts, 7) + 6; return true; }
                    }
                    c += 3;
                }
                else
                {
                    c += 3 + 4 + 2;
                    if (slot.AnimFrame != 1) c += 4;                                   // BNE $D00F crosses $CF/$D0
                    else
                    {
                        c += 2 + 2 + 4 + 2 + 4 + 3 + 2;
                        if (pressed == 0x40)
                        {
                            c += 2 + 6 + ExpandedBulletBudget(out bool allowed);
                            if (allowed) { cycles = c + 2 + 2 + 26 + 6 + ExpandedNormalShot(audioQueueAccepts, 7) + 6; return true; }
                        }
                        c += 3;
                    }
                }
                c += 3 + 2;
                if ((pressed & 0x80) != 0) throw new NotSupportedException("D018 crouch drop/jump");
                c += 2 + 3 + 3 + 2;
                if ((pressed & 4) != 0) throw new NotSupportedException("D0B8 crouch fresh down");
                c += 3 + 3 + 2 + 4;
                cycles = c + FloorTestTail(audioQueueAccepts);
                return true;
            }
            catch (NotSupportedException exception) { unsupported = exception.Message; return false; }
        }

        /// <summary>$D16E look-up while Up is held: frame 2 freezes the pose and A jumps through
        /// $CD0E; otherwise $D121 tests the floor.</summary>
        public bool TryMeasureLookUp03Cycles(byte held, byte pressed, bool? audioQueueAccepts, out int cycles, out string unsupported)
        {
            cycles = -1;
            unsupported = null;
            try
            {
                if (State != 3) throw new NotSupportedException("D16E state outside03 slice");
                int c = StateDispatchCycles() + 3 + 2;
                if ((held & 8) == 0) { cycles = c + 3 + 2 + 3 + 6; return true; }       // $D19C: $50=0
                c += 2 + 4 + 2;
                if (Objects.Slots[0].AnimFrame == 2)
                {
                    c += 2 + 2 + 4 + 2 + 4 + 3 + 2;
                    if ((pressed & 0x80) != 0) throw new NotSupportedException("D18B look-up jump");
                }
                c += 3 + 3;                                                            // BNE/BEQ $D1A1, JMP $D121
                cycles = c + FloorTestTail(audioQueueAccepts);
                return true;
            }
            catch (NotSupportedException exception) { unsupported = exception.Message; return false; }
        }

        /// <summary>$D994 carried by the flyer: pose 8 at $51=$73, the $F2D7 fall each frame, then
        /// $51 counts down to the release into state 0.</summary>
        public bool TryMeasureCarried13Cycles(byte held, byte pressed, bool? audioQueueAccepts, out int cycles, out string unsupported)
        {
            cycles = -1;
            unsupported = null;
            try
            {
                if (State != 0x13) throw new NotSupportedException("D994 state outside13 slice");
                int c = StateDispatchCycles() + 3 + 2;
                c += StateTimer != 0x73 ? 3 : 2 + 2 + 6 + 20;
                c += 2 + 2 + 6 + StateFall(audioQueueAccepts) + 3;
                cycles = c + (StateTimer != 0 ? 3 + 5 + 6 : 2 + 2 + 3 + 6);
                return true;
            }
            catch (NotSupportedException exception) { unsupported = exception.Message; return false; }
        }

        /// <summary>$D121: probe the feet ($06…$08) in place. Empty → $D0C4 $F2D7 fall; class 2 →
        /// state 1; class 4 → hurt; otherwise snap onto the tile (class 8 also sets $0400 bit 5).</summary>
        private int FloorTestTail(bool? audioQueueAccepts)
        {
            int c = 2 + 3 + 2 + 3 + 6 + ExpandedProbeSet(X, Y, 6, out int floor, out int lastY, 4) + 3;
            if (floor == 0) return c + 4 + 2 + 2 + 6 + StateFall(audioQueueAccepts) + 6;   // BEQ $D0C4 crosses $D1/$D0
            c += 2 + 2;
            if (floor == 2) return c + 3 + 2 + 3 + 6 + 18 + 6;
            c += 2 + 2;
            if (floor == 4) throw new NotSupportedException("D138 floor hurt");
            c += 3 + 2 + 3;                                                            // BNE $D152, CMP #8, BNE $D15E
            int y = Y;
            return c + 6 + PlayerSnapUp(ref y, lastY & 15) + 6 + 18 + 2 + 6;
        }

        /// <summary>JSR $F2D7 with A=6/Y=2 from a non-air state, priced by the shared mover.</summary>
        private int StateFall(bool? audioQueueAccepts)
        {
            int y = Y, velocity = (byte)YVelInt << 8 | YVelFrac, state = State;
            int cycles = Air01Vertical(X, ref y, YFrac, ref velocity, ref state, -1, audioQueueAccepts, out _, out _, out _);
            return cycles;
        }
    }
}
