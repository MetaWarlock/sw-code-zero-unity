using System;

namespace Darkwing.Core
{
    public sealed partial class PlayerCore
    {
        public bool TryMeasureGround00IncludingJumpCycles(byte held, byte pressed, bool? audioQueueAccepts,
            out int cycles, out string unsupported)
        {
            if ((pressed & 0x80) == 0)
                return TryMeasureGround00WithQueueCycles(held, pressed, audioQueueAccepts, out cycles, out unsupported);
            cycles = -1;
            unsupported = null;
            try
            {
                if (State != 0) throw new NotSupportedException("CB8B state outside00 slice");
                if (Objects == null || !Objects.Slots[0].Active) throw new NotSupportedException("CAFC inactive player");
                if ((held & 12) != 0 || (pressed & 0x40) != 0) throw new NotSupportedException("CBB7 simultaneous jump/shot/up/down");
                if (Objects.Slots[0].AnimNumber is 0x0E or 0x0F) throw new NotSupportedException("CDD5 ground shot-air pose");
                int vertical = ExpandedGroundVertical(out int nextY, out int floor);
                int airDispatch = 3 + ScrollBranch(0xCD10, 0xCD3E, true);
                airDispatch += 4 + ScrollBranch(0xCD41, 0xCD57, true) + 4 + 2 + 3 + 2 + 2 + 2 + 6;
                airDispatch += 4 + 3 + 3 + 4 + ScrollBranch(0xCD6D, 0xCD82, false);
                airDispatch += 3 + 2 + ScrollBranch(0xCD73, 0xCD92, floor < 2);
                if (floor >= 2) airDispatch += 4 + 2 + 3 + 4 + 3;
                airDispatch += 3 + ScrollBranch(0xCD94, 0xCDD5, true);
                airDispatch += 4 + 2 + ScrollBranch(0xCDDA, 0xCDE3, false) + 2 + ScrollBranch(0xCDDE, 0xCDE3, false) + 3;
                airDispatch += 4 + 3 + ScrollBranch(0xCDF8, 0xCE06, true) + ScrollBranch(0xCE06, 0xCE22, false);
                airDispatch += 3 + ScrollBranch(0xCE0A, 0xCE21, true) + 6;
                int dispatch = 4 + ScrollBranch(0xCAFF, 0xCB41, true);
                dispatch += 2 + 2 + 5 + 2 + 5 + 3 + 2 + 2 + 4 + 3 + 4 + 3 + 5;
                dispatch += 6 + vertical + airDispatch;
                dispatch += 3 + ScrollBranch(0xCB90, 0xCB93, true) + 3 + 2 + ScrollBranch(0xCB97, 0xCBB7, true);
                dispatch += 3 + 2 + ScrollBranch(0xCBBB, 0xCBCE, false) + 2 + 4 + 2 + 4 + 2 + 3 + 3;
                cycles = dispatch + Air01Service(X, nextY, (byte)(YFrac - YVelFrac), 0x04E5, held, pressed, audioQueueAccepts);
                return true;
            }
            catch (NotSupportedException exception)
            {
                unsupported = exception.Message;
                return false;
            }
        }

        public bool TryMeasureAir01Cycles(byte held, byte pressed, bool? audioQueueAccepts,
            out int cycles, out string unsupported)
        {
            cycles = -1;
            unsupported = null;
            try
            {
                if (State != 1) throw new NotSupportedException("CD0E state outside01 slice");
                if (Objects == null || !Objects.Slots[0].Active) throw new NotSupportedException("CAFC inactive player");
                int dispatch = 4 + ScrollBranch(0xCAFF, 0xCB41, true);
                dispatch += 2 + 2 + 5 + 2 + 5 + 3 + 2 + 2 + 4 + 3 + 4 + 3 + 5;
                cycles = dispatch + Air01Service(X, Y, YFrac, (byte)YVelInt << 8 | YVelFrac, held, pressed, audioQueueAccepts);
                return true;
            }
            catch (NotSupportedException exception)
            {
                unsupported = exception.Message;
                return false;
            }
        }

        private int Air01Service(int coordinateX, int coordinateY, int fractionY, int velocity, byte held, byte pressed, bool? audioQueueAccepts)
        {
            if ((held & 8) != 0 || AirSubState == 1) throw new NotSupportedException("CD1F up/reach pose");
            int state = 1, substate = AirSubState;
            var player = Objects.Slots[0];
            int animation = player.AnimNumber, frame = player.AnimFrame, timer = player.AnimTimer;
            if (animation == 0x0D) throw new NotSupportedException("CD2A reach animation");
            int cycles = 3 + ScrollBranch(0xCD10, 0xCD3E, false);
            int direction = held & 3;
            cycles += 3 + 2 + ScrollBranch(0xCD16, 0xCD1B, direction == 0);
            TimingParts[0] = direction != 0 ? Air01Horizontal(ref coordinateX, coordinateY, direction) : 0;
            if (direction != 0) cycles += 6 + TimingParts[0];
            cycles += 3 + 2 + ScrollBranch(0xCD1F, 0xCD25, true);
            cycles += 4 + 2 + ScrollBranch(0xCD2A, 0xCD3E, false);
            bool freshShot = (pressed & 0x40) != 0;
            cycles += 3 + 2 + ScrollBranch(0xCD30, 0xCD3E, !freshShot);
            int newSlot = -1;
            if (freshShot)
            {
                cycles += 6 + ExpandedBulletBudget(out bool allowed) + ScrollBranch(0xCD35, 0xCD3E, !allowed);
                if (allowed)
                {
                    substate = 2;
                    for (int index = WeaponSlotPref[0]; index >= 2; index--)
                        if (!Objects.Slots[index].Active) { newSlot = index; break; }
                    cycles += 2 + 3 + 6 + ExpandedNormalShot(audioQueueAccepts, (byte)animation);
                }
            }
            bool falling = (velocity & 0x8000) != 0;
            cycles += 4 + ScrollBranch(0xCD41, 0xCD57, falling);
            if (!falling)
            {
                cycles += 2 + 3 + 3 + 2 + ScrollBranch(0xCD4B, 0xCD57, (held & 0x80) != 0);
                if ((held & 0x80) == 0)
                {
                    cycles += 4 + 2 + ScrollBranch(0xCD52, 0xCD57, (velocity >> 8) >= 5);
                    if ((velocity >> 8) < 5)
                    {
                        cycles += 6 + 2 + 5 + 2 + 5 + 6;
                        if (newSlot < 0) velocity = 0xFFAB;
                    }
                }
            }
            cycles += 4 + 2 + 3 + 2 + 2 + 2 + 6;
            TimingParts[1] = Air01Vertical(coordinateX, ref coordinateY, fractionY, ref velocity, ref state, newSlot,
                audioQueueAccepts, out int maximum, out bool landed, out bool verticalSound);
            cycles += TimingParts[1];
            if (newSlot >= 0 && verticalSound) throw new NotSupportedException("F850 multiple enqueue owner progression");
            cycles += 4 + 3 + 3 + 4 + ScrollBranch(0xCD6D, 0xCD82, (velocity & 0x8000) == 0);
            bool selectAnimation;
            int normalAnimation, shotAnimation;
            if ((velocity & 0x8000) != 0)
            {
                cycles += 3 + 2 + ScrollBranch(0xCD73, 0xCD92, maximum < 2);
                if (maximum >= 2) cycles += 4 + 2 + 3 + 4 + 3;
                cycles += 3 + ScrollBranch(0xCD94, 0xCDD5, state == 0);
                selectAnimation = state != 0;
                normalAnimation = 0x12;
                shotAnimation = 0x0F;
                if (selectAnimation) cycles += 2 + 3 + 2 + 3;
            }
            else
            {
                cycles += 3 + 2 + ScrollBranch(0xCD86, 0xCD75, maximum == 2);
                if (maximum == 2) throw new NotSupportedException("CD86 class2 air animation");
                cycles += 2 + 3 + 2 + 3 + ScrollBranch(0xCD90, 0xCD9E, true);
                selectAnimation = true;
                normalAnimation = 0x11;
                shotAnimation = 0x0E;
            }
            if (selectAnimation)
            {
                cycles += 3 + 2 + ScrollBranch(0xCDA2, 0xCDB5, false) + 2 + ScrollBranch(0xCDA6, 0xCDAC, substate == 2);
                bool setAnimation;
                int requested;
                if (substate != 2)
                {
                    cycles += 3 + ScrollBranch(0xCDAA, 0xCDBE, true);
                    setAnimation = true;
                    requested = normalAnimation;
                }
                else
                {
                    cycles += 3 + 4 + ScrollBranch(0xCDB1, 0xCDD5, animation == shotAnimation);
                    setAnimation = animation != shotAnimation;
                    requested = shotAnimation;
                    if (setAnimation) cycles += ScrollBranch(0xCDB3, 0xCDBE, true);
                }
                if (setAnimation)
                {
                    cycles += 6 + 4 + 2 + 4 + 4 + 6 + 3;
                    animation = requested;
                    frame = timer = 0;
                }
            }
            cycles += 4 + 2 + ScrollBranch(0xCDDA, 0xCDE3, animation == 0x0E);
            if (animation != 0x0E) cycles += 2 + ScrollBranch(0xCDDE, 0xCDE3, animation == 0x0F);
            if (animation is 0x0E or 0x0F)
            {
                cycles += 4 + 2 + ScrollBranch(0xCDE8, 0xCDF5, frame != 1);
                if (frame == 1)
                {
                    cycles += 4 + 2 + ScrollBranch(0xCDEF, 0xCDF5, timer != 4);
                    if (timer == 4) cycles += 2 + 3;
                }
            }
            else cycles += 3;
            cycles += 4 + 3 + ScrollBranch(0xCDF8, 0xCE06, true) + ScrollBranch(0xCE06, 0xCE22, !landed);
            if (landed)
            {
                cycles += 3 + ScrollBranch(0xCE0A, 0xCE21, state == 0);
                if (state != 0)
                {
                    cycles += 3 + 2 + ScrollBranch(0xCE10, 0xCE21, maximum == 2);
                    if (maximum != 2)
                    {
                        if (!audioQueueAccepts.HasValue) throw new NotSupportedException("F850 native audio queue owner unavailable");
                        if (newSlot >= 0) throw new NotSupportedException("F850 multiple enqueue owner progression");
                        cycles += 2 + 6 + ExpandedQueueCycles(audioQueueAccepts.Value) + 2 + 3 + 3 + 3 + 3;
                    }
                }
                return cycles + 6;
            }
            cycles += 3 + 2 + ScrollBranch(0xCE26, 0xCE21, state == 4);
            if (state == 4) return cycles + 6;
            cycles += 2 + ScrollBranch(0xCE2A, 0xCE21, state == 0x0E);
            if (state == 0x0E) return cycles + 6;
            cycles += 2 + ScrollBranch(0xCE2E, 0xCE21, state == 0x0D);
            if (state == 0x0D) return cycles + 6;
            cycles += 2 + ScrollBranch(0xCE32, 0xCE21, state == 7);
            if (state == 7) return cycles + 6;
            return cycles + 2 + 3 + 6;
        }

        /// <summary>$F366…$F3C1 for the player: probe the head band ($3A…$3C). A class 1/2 there
        /// catches the hook ($50=$0D, SFX $12) unless already hanging; an empty band re-probes the
        /// feet ($06): a class-1 floor lands only at fall speed $FA or faster than $F9, ≥3 lands.</summary>
        private int HookDanceCycles(int coordinateX, ref int coordinateY, int velocity, ref int state, bool? audioQueueAccepts,
            out int maximum, out bool landed, out bool sound)
        {
            landed = sound = false;
            int cycles = 2 + 2 + 2 + 3 + 2 + 3 + 6;
            cycles += ExpandedProbeSet(coordinateX, coordinateY, 0x3A, out int head, out _, 5) + 2 + 3 + 2;
            if (head >= 1)
            {
                cycles += 2 + 2;
                if (head != 1)
                {
                    cycles += 2 + 2;
                    if (head != 2) { maximum = head; return cycles + 2 + 3 + 6; }
                }
                cycles += 3 + 3 + 2;
                maximum = head;
                if (state == 0x0D)
                {
                    landed = true;
                    return cycles + 3 + 6 + 20 + 2 + 6;
                }
                if (!audioQueueAccepts.HasValue) throw new NotSupportedException("F850 native audio queue owner unavailable");
                state = 0x0D;
                sound = true;
                landed = head == 2;
                return cycles + 2 + 2 + 3 + 2 + 6 + ExpandedQueueCycles(audioQueueAccepts.Value) + 2 + 3 + 3 + 2
                    + (head != 2 ? 3 : 2) + 6 + 20 + 6;
            }
            cycles += 3 + 2 + 3 + 2 + 3 + 6;
            cycles += ExpandedProbeSet(coordinateX, coordinateY, 6, out maximum, out int lastY, 5) + 2 + 3 + 2;
            if (maximum == 0) return cycles + 3 + 6;
            cycles += 2 + 2;
            if (maximum == 1)
            {
                cycles += 3 + 4 + 2;
                int high = velocity >> 8;
                if (high != 0xFA)
                {
                    cycles += 2 + 2;
                    if (high >= 0xF9) return cycles + 2 + 2 + 6;
                    cycles += 3;
                }
                else cycles += 3;
                cycles += 2 + 3 + 3;
            }
            else
            {
                cycles += 2 + 2;
                if (maximum == 2) return cycles + 3 + 6;
                cycles += 2 + 2 + 3 + 3;
            }
            landed = true;
            return cycles + 6 + PlayerSnapUp(ref coordinateY, lastY & 15) + 6 + 20 + 2 + 6;
        }

        private int Air01Horizontal(ref int coordinateX, int coordinateY, int direction)
        {
            bool right = (direction & 1) != 0;
            int cycles = 3 + 2 + ScrollBranch(0xCCBE, 0xCCDF, !right);
            cycles += 3 + ScrollBranch(right ? 0xCCC2 : 0xCCE1, 0xCD0D, (WallBlocked & 0x80) != 0);
            if ((WallBlocked & 0x80) != 0) return cycles + 6;
            if (right)
            {
                cycles += 3 + 2 + ScrollBranch(0xCCC8, 0xCCD5, CheckpointId != 0x0E);
                if (CheckpointId == 0x0E) throw new NotSupportedException("CCCA checkpoint0E facing veto");
            }
            int first = right ? 0 : 3;
            int fraction = right ? XFrac + 0x80 : XFrac - 0x80;
            int nextX = (ushort)(right ? coordinateX + 1 + (fraction > 255 ? 1 : 0) : coordinateX - 1 - (fraction < 0 ? 1 : 0));
            ExpandedProbeSet(nextX, coordinateY, first, out int maximum, out _, 3);
            if (maximum != 0) throw new NotSupportedException("CCEA nonempty air horizontal side");
            cycles += 2 + 2 + 6 + ExpandedHorizontalSimple(coordinateX, coordinateY, right, 0x80, 1, first);
            coordinateX = nextX;
            if (right) cycles += 3;
            return cycles + 3 + 2 + ScrollBranch(0xCCEE, 0xCD0D, true) + 6;
        }

        private int Air01Vertical(int coordinateX, ref int coordinateY, int fractionY, ref int velocity, ref int state, int newSlot,
            bool? audioQueueAccepts, out int maximum, out bool landed, out bool sound)
        {
            landed = sound = false;
            bool falling = (velocity & 0x8000) != 0;
            int fraction = fractionY - (velocity & 255);
            int rawLow = (byte)coordinateY - (velocity >> 8) - (fraction < 0 ? 1 : 0);
            int low = (byte)rawLow, row = coordinateY >> 8;
            int integrate = 4 + 2 + 4 + 5 + 4 + 4 + 5;
            if (falling)
            {
                integrate += 2 + ScrollBranch(0xF5B9, 0xF5C8, low < 240);
                if (low >= 240) { low = (byte)(low + 0x10); row = (byte)(row + 1); integrate += 2 + 5 + 4 + 2 + 5; }
            }
            else
            {
                integrate += ScrollBranch(0xF594, 0xF5A3, rawLow >= 0);
                if (rawLow < 0) { low = (byte)(low - 0x10); row = (byte)(row - 1); integrate += 2 + 5 + 4 + 2 + 5; }
            }
            integrate += 6;
            coordinateY = row << 8 | low;
            velocity = (velocity - GravityFrac) & 65535;
            bool afterRising = (velocity & 0x8000) == 0;
            int gravity = 4 + 2 + 3 + 5 + 4 + 2 + 5 + ScrollBranch(0xF5EB, 0xF5FB, afterRising);
            if (!afterRising)
            {
                gravity += 2 + ScrollBranch(0xF5EF, 0xF5FB, (velocity >> 8) >= 0xF9);
                if ((velocity >> 8) < 0xF9) { velocity = 0xF800; gravity += 2 + 5 + 2 + 5; }
            }
            gravity += 6;
            int pair = ExpandedVerticalPair(coordinateX, coordinateY, out bool standing, out int push, out int scratch);
            if (standing)
            {
                if (!falling) throw new NotSupportedException("F3D7 rising actor push");
                if (newSlot >= 0) throw new NotSupportedException("F850 multiple enqueue owner progression");
                int previousY = scratch;
                int head = 3 + 3 + 4 + 3 + 4 + 3 + 4 + ScrollBranch(0xF2E8, 0xF2ED, true);
                head += 6 + integrate + 2 + ScrollBranch(0xF2F2, 0xF31F, false) + 6 + pair + ScrollBranch(0xF2F7, 0xF31F, false);
                head += PlayerActorLandCycles(coordinateX, ref coordinateY, previousY, push, out maximum);
                velocity = 0xFFAB;
                landed = true;
                return head;
            }
            if (newSlot >= 0)
            {
                pair += (Flags & 0x80) != 0 ? 12 : 6;
            }
            int probe = ExpandedProbeSet(coordinateX, coordinateY, falling ? 6 : 9, out maximum, out int lastY, 3);
            ExpandedPoint(coordinateX, coordinateY, falling ? 7 : 10, out _, out int middle, out _);
            int cycles = 3 + 3 + 4 + 3 + 4 + 3 + 4 + ScrollBranch(0xF2E8, 0xF2ED, falling);
            if (falling)
            {
                cycles += 6 + integrate + 2 + ScrollBranch(0xF2F2, 0xF31F, false) + 6 + pair + ScrollBranch(0xF2F7, 0xF31F, true);
                if (_hookLatch != 0)
                {
                    // $F324 BNE $F366: a latched fall skips the $06 probe and goes straight to the dance.
                    cycles += 6 + gravity + 3 + ScrollBranch(0xF324, 0xF366, true);
                    return cycles + HookDanceCycles(coordinateX, ref coordinateY, velocity, ref state, audioQueueAccepts,
                        out maximum, out landed, out sound);
                }
                cycles += 6 + gravity + 3 + ScrollBranch(0xF324, 0xF366, false) + 6 + probe;
                cycles += 2 + 3 + 2 + ScrollBranch(0xF32E, 0xF34B, maximum < 1);
                if (maximum == 0) return cycles + 6;
                cycles += ScrollBranch(0xF330, 0xF33A, maximum != 1);
                bool dance = false;
                if (maximum == 1)
                {
                    int nibble = lastY & 15;
                    cycles += 3 + 2 + 2 + ScrollBranch(0xF338, 0xF360, nibble >= 8);
                    dance = nibble >= 8;
                }
                if (!dance)
                {
                    cycles += 2 + ScrollBranch(0xF33C, 0xF344, false) + 3 + 2 + ScrollBranch(0xF342, 0xF360, maximum == 2);
                    dance = maximum == 2;
                }
                if (dance)
                {
                    // $F360: CPX #1 / BCS not taken for the player, INC $5C, then $F366.
                    cycles += 2 + 2 + 5;
                    return cycles + HookDanceCycles(coordinateX, ref coordinateY, velocity, ref state, audioQueueAccepts,
                        out maximum, out landed, out sound);
                }
                int snap = 3 + 2 + 3 + 4 + 2 + 3 + 5 + ScrollBranch(0xEDE1, 0xEDF0, low >= (lastY & 15));
                if (low < (lastY & 15)) { snap += 2 + 5 + 4 + 2 + 5; row = (byte)(row - 1); }
                coordinateY = row << 8 | (byte)(low - (lastY & 15) - (low < (lastY & 15) ? 0x10 : 0));
                velocity = 0xFFAB;
                landed = true;
                return cycles + 6 + snap + 6 + 6 + 2 + 5 + 2 + 5 + 6 + 2 + 6;
            }
            cycles += 3 + 3 + 2 + 3 + 3 + 6 + integrate;
            cycles += 2 + ScrollBranch(0xF3D0, 0xF42A, false) + 6 + pair + ScrollBranch(0xF3D5, 0xF42A, true);
            cycles += 6 + gravity + 6 + probe + 3 + 2 + ScrollBranch(0xF434, 0xF474, maximum < 1);
            if (maximum == 0) return cycles + 2 + 6;
            cycles += 2 + ScrollBranch(0xF438, 0xF46E, false) + 2 + ScrollBranch(0xF43C, 0xF45B, maximum == 1);
            if (maximum != 1) throw new NotSupportedException("F43E non-class1 rising air contact");
            cycles += 3 + 2 + ScrollBranch(0xF45F, 0xF471, state == 4);
            if (state == 4) throw new NotSupportedException("F45F already caught state");
            cycles += 3 + ScrollBranch(0xF463, 0xF474, middle == 0);
            if (middle == 0) return cycles + 2 + 6;
            if (!audioQueueAccepts.HasValue) throw new NotSupportedException("F850 native audio queue owner unavailable");
            cycles += 2 + 3 + 2 + 6 + ExpandedQueueCycles(audioQueueAccepts.Value);
            int snappedLow = low + 16 - (lastY & 15);
            int headSnap = 3 + 2 + 2 + 2 + 4 + 5 + 2 + ScrollBranch(0xEDC2, 0xEDD1, snappedLow < 240);
            if (snappedLow >= 240) { snappedLow = (byte)(snappedLow + 16); row = (byte)(row + 1); headSnap += 2 + 5 + 4 + 2 + 5; }
            coordinateY = row << 8 | (byte)snappedLow;
            state = 4;
            velocity = 0xFFAB;
            sound = true;
            return cycles + 6 + headSnap + 6 + 6 + 2 + 5 + 2 + 5 + 6 + 2 + 6;
        }
    }
}
