using System;

namespace Darkwing.Core
{
    public sealed partial class PlayerCore
    {
        public bool TryMeasureAirborne0BCycles(byte held, byte pressed, bool? audioQueueAccepts,
            out int cycles, out string unsupported)
        {
            cycles = -1;
            unsupported = null;
            try
            {
                if (State != 0x0B) throw new NotSupportedException("D626 state outside 0B slice");
                if (Objects == null || !Objects.Slots[0].Active) throw new NotSupportedException("CAFC inactive player");
                if ((Flags & 0x20) != 0) throw new NotSupportedException("EB52 pre-hook flag");
                if ((pressed & 0x48) != 0) throw new NotSupportedException("D63E reach/shot");
                if (Objects.Slots[0].AnimNumber == 0x0D) throw new NotSupportedException("D756 reach pose");
                bool rising = YVelInt >= 0;
                int fractionY = YFrac - YVelFrac;
                int lowY = (byte)((byte)Y - (byte)YVelInt - (fractionY < 0 ? 1 : 0));
                int rowY = Y >> 8;
                int integrate = 4 + 2 + 4 + 5 + 4 + 4 + 5;
                if (rising)
                {
                    bool borrow = (byte)Y - (byte)YVelInt - (fractionY < 0 ? 1 : 0) < 0;
                    integrate += ScrollBranch(0xF594, 0xF5A3, !borrow);
                    if (borrow) { lowY = (byte)(lowY - 0x10); rowY = (byte)(rowY - 1); integrate += 2 + 5 + 4 + 2 + 5; }
                }
                else
                {
                    integrate += 2 + ScrollBranch(0xF5B9, 0xF5C8, lowY < 240);
                    if (lowY >= 240) { lowY = (byte)(lowY + 0x10); rowY = (byte)(rowY + 1); integrate += 2 + 5 + 4 + 2 + 5; }
                }
                integrate += 6;
                int nextY = rowY << 8 | lowY;
                int velocity = (((byte)YVelInt << 8 | YVelFrac) - GravityFrac) & 65535;
                bool afterRising = (velocity & 0x8000) == 0;
                int gravity = 4 + 2 + 3 + 5 + 4 + 2 + 5 + ScrollBranch(0xF5EB, 0xF5FB, afterRising);
                if (!afterRising)
                {
                    gravity += 2 + ScrollBranch(0xF5EF, 0xF5FB, (velocity >> 8) >= 0xF9);
                    if ((velocity >> 8) < 0xF9) { velocity = 0xF800; gravity += 2 + 5 + 2 + 5; }
                }
                gravity += 6;
                int vertical = 4 + ScrollBranch(0xF5CC, 0xF5D4, rising) + 6 + integrate + 3 + gravity;
                int dispatch = 4 + ScrollBranch(0xCAFF, 0xCB41, true);
                dispatch += 2 + 2 + 5 + 2 + 5 + 3 + 2 + 2 + 4 + 3 + 4 + 3 + 5;
                dispatch += 4 + ScrollBranch(0xD629, 0xD636, !rising);
                dispatch += rising ? 2 + 3 + 2 + 3 + 3 : 2 + 3 + 2 + 3;
                dispatch += 3 + 2 + ScrollBranch(0xD642, 0xD650, true) + 6 + 6;
                int probeCost = ExpandedProbeSet(X, nextY, rising ? 9 : 6, out int probe, out int lastProbeY);
                dispatch += 3 + 2 + ScrollBranch(0xD65A, 0xD65F, probe != 3);
                if (probe >= 3) throw new NotSupportedException("D65C solid/hazard ceiling");
                dispatch += 2 + ScrollBranch(0xD661, 0xD666, true);
                dispatch += 4 + ScrollBranch(0xD669, 0xD671, afterRising);
                if (!afterRising)
                {
                    dispatch += 3 + 2 + ScrollBranch(0xD66F, 0xD6EE, probe == 1);
                    if (probe == 1)
                    {
                        int integerVelocity = velocity >> 8;
                        dispatch += 4 + 2 + ScrollBranch(0xD6F3, 0xD6FB, integerVelocity == 0xFD);
                        if (integerVelocity != 0xFD)
                        {
                            dispatch += 2 + ScrollBranch(0xD6F7, 0xD6FB, integerVelocity == 0xFE);
                            if (integerVelocity != 0xFE)
                            {
                                dispatch += ScrollBranch(0xD6F9, 0xD725, integerVelocity >= 0xFE);
                                if (integerVelocity >= 0xFE) dispatch += 3 + 2 + ScrollBranch(0xD729, 0xD6FB, true);
                            }
                        }
                        if (!audioQueueAccepts.HasValue) throw new NotSupportedException("F850 native audio queue owner unavailable");
                        dispatch += 2 + 6 + ExpandedQueueCycles(audioQueueAccepts.Value) + 2 + 3 + 3 + 3;
                        int floorNibble = lastProbeY & 15;
                        bool floorBorrow = lowY < floorNibble;
                        dispatch += 3 + 2 + 3 + 4 + 2 + 3 + 5 + ScrollBranch(0xEDE1, 0xEDF0, !floorBorrow);
                        if (floorBorrow) dispatch += 2 + 5 + 4 + 2 + 5;
                        dispatch += 6;
                        cycles = dispatch + vertical + probeCost;
                        return true;
                    }
                }
                dispatch += 4 + ScrollBranch(0xD674, 0xD679, !afterRising);
                int pair = 0;
                if (afterRising) dispatch += 3;
                else
                {
                    byte animation = Objects.Slots[0].AnimNumber;
                    if (animation is 0x0E or 0x0F) throw new NotSupportedException("D69E shot-pose countdown");
                    dispatch += 4 + 2 + ScrollBranch(0xD67E, 0xD69E, false) + 2 + ScrollBranch(0xD682, 0xD69E, false);
                    dispatch += 2 + ScrollBranch(0xD686, 0xD68A, false) + ScrollBranch(0xD688, 0xD6AC, true);
                    dispatch += 2 + 6 + 4 + 2 + 4 + 4 + 6;
                    pair = ExpandedRejectedVerticalPair(X, nextY);
                    dispatch += 6 + ScrollBranch(0xD6B4, 0xD709, false) + 3 + 2 + ScrollBranch(0xD6BA, 0xD6C7, true);
                    if (!(_tiles is LevelTileMap map)) throw new NotSupportedException("D6C9 grid pointer unavailable");
                    dispatch += 3 + 5 + (((map.World.GridAddress & 255) + CellIndex > 255) ? 1 : 0) + 2;
                    dispatch += ScrollBranch(0xD6CD, 0xD6D6, CellGridByte != 0x90);
                    if (CellGridByte == 0x90) throw new NotSupportedException("D6CF world-cell90 landing");
                    dispatch += 3 + 2 + ScrollBranch(0xD6DA, 0xD711, false) + 2 + ScrollBranch(0xD6DE, 0xD6EB, false);
                    dispatch += 2 + ScrollBranch(0xD6E2, 0xD6FB, false) + 2 + ScrollBranch(0xD6E6, 0xD73C, false) + 3;
                }
                dispatch += 4 + 2 + ScrollBranch(0xD75B, 0xD76E, true);
                int direction = held & 3;
                dispatch += 2 + 3 + 2 + ScrollBranch(0xD774, 0xD79A, direction == 0);
                int horizontal = 0;
                if (direction != 0)
                {
                    if (direction != 2) throw new NotSupportedException("D77C right steering");
                    dispatch += 3 + 2 + ScrollBranch(0xD77A, 0xD795, true) + 2 + 6;
                    int fractionX = XFrac - 0x80;
                    int lowX = (byte)X - 1 - (fractionX < 0 ? 1 : 0);
                    int nextX = (ushort)(X - 1 - (fractionX < 0 ? 1 : 0));
                    int integrateX = 4 + 2 + 4 + 5 + 4 + 4 + 5 + ScrollBranch(0xF52E, 0xF538, lowX >= 0);
                    if (lowX < 0) integrateX += 4 + 2 + 5;
                    integrateX += 6;
                    int horizontalPair = TimingRejectedHorizontalPair(nextX, nextY);
                    int side = ExpandedProbeSet(nextX, nextY, 2, out int sideClass, out _);
                    if (sideClass >= 3) throw new NotSupportedException("F2A3 solid side snap");
                    ExpandedPoint(nextX, nextY, 5, out _, out int extraClass, out _);
                    if (extraClass >= 3) throw new NotSupportedException("F254 native extra side snap");
                    horizontal = 3 + 3 + 6 + integrateX + 2 + ScrollBranch(0xF25D, 0xF29A, false) + 6;
                    horizontal += horizontalPair + ScrollBranch(0xF262, 0xF29A, true) + 6 + side;
                    horizontal += 3 + 2 + ScrollBranch(0xF2A1, 0xF2A6, true) + 4 + 2 + 5 + 6;
                }
                dispatch += 3 + 2 + ScrollBranch(0xD79E, 0xD7B6, true) + 6;
                cycles = dispatch + vertical + probeCost + pair + horizontal;
                return true;
            }
            catch (NotSupportedException exception)
            {
                unsupported = exception.Message;
                return false;
            }
        }

        /// <summary>$D517 with $04D0&amp;$0F = 0: the world-entry free fall. $D51E keeps the plain
        /// $F5A4/$F5DA pair while the fall byte is still $FA..$FF; below $FA the $D525 slice runs
        /// $F4D7, which integrates, applies gravity, probes the floor through $EB52 ($40=$06,
        /// $41=$02) and then either leaves at $F4FB or snaps ($EDD2), cuts ($F5FC) and lands
        /// ($D530/$D532/$D535). Anything outside those two branches is refused, not estimated.</summary>
        public bool TryMeasureWorldEntryFall09Cycles(bool? audioQueueAccepts, out int cycles, out string unsupported)
        {
            cycles = -1;
            unsupported = null;
            try
            {
                if (State != 0x09) throw new NotSupportedException("D517 state outside 09 slice");
                if (Objects == null || !Objects.Slots[0].Active) throw new NotSupportedException("CAFC inactive player");
                if ((Objects.Slots[0].StateLow & 0x0F) != 0) throw new NotSupportedException("D53F state09 intro-card phase");
                if ((Flags & 0x20) != 0) throw new NotSupportedException("EB52 pre-hook flag");

                int dispatch = 4 + ScrollBranch(0xCAFF, 0xCB41, true);
                dispatch += 2 + 2 + 5 + 2 + 5 + 3 + 2 + 2 + 4 + 3 + 4 + 3 + 5;
                bool plain = (byte)YVelInt >= 0xFA;                                     // $D51E/$D523
                dispatch += 4 + 2 + 2 + 4 + 2 + ScrollBranch(0xD523, 0xD539, plain);   // $D517-$D523

                int fraction = YFrac - YVelFrac;                                        // $F5A4-$F5AB
                int row = (Y >> 8) & 255, low = (Y & 255) - (YVelInt & 255) - (fraction < 0 ? 1 : 0);
                int newLow = low & 255;
                bool wrap = newLow >= 0xF0;                                             // $F5B7/$F5B9
                int integrate = 4 + 2 + 4 + 5 + 4 + 4 + 5 + 2 + ScrollBranch(0xF5B9, 0xF5C8, !wrap);
                if (wrap)
                {
                    integrate += 2 + 5 + 4 + 2 + 5;                                     // $F5BB-$F5C5
                    if (newLow + 0x10 > 255) row = (row + 1) & 255;
                    newLow = (newLow + 0x10) & 255;
                }
                integrate += 6;
                int nextY = row << 8 | newLow;

                int velocity = (((byte)YVelInt << 8 | YVelFrac) - GravityFrac) & 65535; // $F5DA-$F5E8
                bool afterRising = (velocity & 0x8000) == 0;
                int gravity = 4 + 2 + 3 + 5 + 4 + 2 + 5 + ScrollBranch(0xF5EB, 0xF5FB, afterRising);
                int fallByte = (velocity >> 8) & 255;
                if (!afterRising)
                {
                    gravity += 2 + ScrollBranch(0xF5EF, 0xF5FB, fallByte >= 0xF9);
                    if (fallByte < 0xF9) { gravity += 2 + 5 + 2 + 5; fallByte = 0xF8; }  // $F5F1-$F5F8
                }
                gravity += 6;

                if (plain) { cycles = dispatch + 6 + integrate + 3 + gravity; return true; }   // $D539/$D53C

                dispatch += 2 + 2 + 6 + 3 + 3;                                          // $D525-$D529, $F4D7/$F4D9
                dispatch += 6 + integrate + 6 + gravity;                                // $F4DB/$F4DE
                int probe = ExpandedProbeSet(X, nextY, 6, out int hit, out int lastProbeY, 3);
                dispatch += 6 + probe + 3 + 2 + ScrollBranch(0xF4E8, 0xF4FB, hit == 0);  // $F4E1, $F4E4/$F4E8
                bool lands = false;
                if (hit != 0)
                {
                    dispatch += ScrollBranch(0xF4EA, 0xF4F3, hit >= 2);                // $F4EA
                    if (hit == 1)
                    {
                        dispatch += 4 + 2 + ScrollBranch(0xF4F1, 0xF4FB, fallByte >= 0xFC);   // $F4EC
                        lands = fallByte < 0xFC;
                    }
                    else lands = true;
                }
                if (lands)
                {
                    int snapped = nextY;
                    dispatch += 6 + PlayerSnapUp(ref snapped, lastProbeY & 15);          // $F4F3 JSR $EDD2
                    dispatch += 6 + 2 + 5 + 2 + 5 + 6;                                  // $F4F6 JSR $F5FC
                    dispatch += 2 + 6;                                                   // $F4F9 SEC / RTS
                }
                else dispatch += 2 + 6;                                                  // $F4FB CLC / RTS
                dispatch += 3 + ScrollBranch(0xD52E, 0xD538, hit == 0);                  // $D52C/$D52E
                if (hit != 0)
                {
                    if (!audioQueueAccepts.HasValue) throw new NotSupportedException("F850 native audio queue owner unavailable");
                    dispatch += 2 + 6 + ExpandedQueueCycles(audioQueueAccepts.Value) + 6;   // $D530/$D532, $D535
                }
                cycles = dispatch + 6;                                                   // $D538 RTS
                return true;
            }
            catch (NotSupportedException exception)
            {
                unsupported = exception.Message;
                return false;
            }
        }

        public bool TryMeasureState04Cycles(byte held, byte pressed, out int cycles, out string unsupported)
        {
            cycles = -1;
            unsupported = null;
            try
            {
                if (State != 4) throw new NotSupportedException("D1A4 state outside04 slice");
                if (Objects == null || !Objects.Slots[0].Active) throw new NotSupportedException("CAFC inactive player");
                if ((pressed & 0x4C) != 0) throw new NotSupportedException("D1F4 shot/down/reach outside04 slice");
                if ((held & 0x08) != 0) throw new NotSupportedException("D243 held-up jump pose outside04 slice");
                if (Objects.Slots[0].AnimNumber == 0x0C) throw new NotSupportedException("D1C2 shot-pose countdown");
                int probe = ExpandedProbeSet(X, Y, 6, out _, out _, 3);
                int dispatch = 4 + ScrollBranch(0xCAFF, 0xCB41, true);
                dispatch += 2 + 2 + 5 + 2 + 5 + 3 + 2 + 2 + 4 + 3 + 4 + 3 + 5;
                dispatch += 3 + 2 + ScrollBranch(0xD1A8, 0xD1B8, true) + 6;
                dispatch += 2 + 3 + 2 + 3 + 6 + probe + 3 + 2 + ScrollBranch(0xD262, 0xD252, true) + 6;
                dispatch += 4 + 2 + ScrollBranch(0xD1C0, 0xD1D0, true) + 2 + 6 + 4 + 2 + 4 + 4 + 6;
                int direction = held & 3;
                dispatch += 3 + 2 + ScrollBranch(0xD1D9, 0xD1F4, direction == 0);
                if (direction != 0)
                {
                    bool left = (direction & 1) == 0;
                    dispatch += 3 + 2 + ScrollBranch(0xD1DF, 0xD1EC, left) + 4 + 2 + 4;
                    if (!left) dispatch += 3;
                }
                dispatch += 3 + 2 + ScrollBranch(0xD1F8, 0xD20E, true);
                dispatch += 3 + 2 + ScrollBranch(0xD212, 0xD21E, true);
                bool jump = (pressed & 0x80) != 0;
                dispatch += 3 + 2 + ScrollBranch(0xD222, 0xD252, !jump);
                if (jump)
                {
                    dispatch += 2 + 4 + 2 + 4 + 3 + 2 + ScrollBranch(0xD232, 0xD23F, false);
                    dispatch += 2 + 4 + 2 + 3 + ScrollBranch(0xD23D, 0xD243, true);
                    dispatch += 3 + 2 + ScrollBranch(0xD247, 0xD24D, true) + 2 + 6 + 4 + 2 + 4 + 4 + 6;
                }
                cycles = dispatch + 6;
                return true;
            }
            catch (NotSupportedException exception)
            {
                unsupported = exception.Message;
                return false;
            }
        }

        public bool TryMeasureGround00Cycles(byte held, byte pressed, out int cycles, out string unsupported)
        {
            if ((held & 12) != 0 || (pressed & 0xC0) != 0)
            {
                cycles = -1;
                unsupported = "CBB7 ground shot/jump/up/down";
                return false;
            }
            return TryMeasureGround00WithQueueCycles(held, pressed, null, out cycles, out unsupported);
        }

        public bool TryMeasureGround00WithQueueCycles(byte held, byte pressed, bool? audioQueueAccepts,
            out int cycles, out string unsupported)
        {
            cycles = -1;
            unsupported = null;
            try
            {
                if (State != 0) throw new NotSupportedException("CB8B state outside00 slice");
                if (Objects == null || !Objects.Slots[0].Active) throw new NotSupportedException("CAFC inactive player");
                // $CBDF compares $16&0F with #04, so only an exact Down without a horizontal bit
                // reaches $CC44. Up (#08 at $CBD7), Up+Down and Down with a direction walk other
                // branches whose price is not derived here; a fresh $14&80 belongs to the jump wrapper.
                bool down = (held & 0x0F) == 0x04 && (held & 3) == 0;
                if ((pressed & 0x80) != 0 || ((held & 0x0C) != 0 && !down))
                    throw new NotSupportedException("CBB7 ground jump/up/down");
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
                dispatch += 3 + 2 + ScrollBranch(0xCBBB, 0xCBCE, true);
                int direction = held & 3;
                dispatch += 3 + 2 + ScrollBranch(0xCBD2, 0xCBD7, direction == 0);
                var slot = Objects.Slots[0];
                int frame = slot.AnimFrame, timer = slot.AnimTimer;
                if (direction == 0)
                {
                    dispatch += 3 + 2 + 2 + ScrollBranch(0xCBDD, 0xCC3A, false) + 2 + ScrollBranch(0xCBE1, 0xCBE6, !down);
                    if (down)
                    {
                        // $CBE3 JMP $CC44 → $CC52 RTS: LDA #$06 / JSR $FA78, $50=2, $0490=0. Reached
                        // before $CBE6, so $14&40 (B) is never tested on this path.
                        cycles = dispatch + 3 + 2 + 6 + 20 + 2 + 3 + 2 + 4 + 6;
                        return true;
                    }
                    bool shot = (pressed & 0x40) != 0;
                    dispatch += 3 + 2 + ScrollBranch(0xCBEA, 0xCBEF, !shot);
                    if (shot)
                    {
                        dispatch += 3 + 3 + 2 + ScrollBranch(0xCC1A, 0xCC52, false) + 4 + 2;
                        dispatch += ScrollBranch(0xCC21, 0xCC52, slot.AnimNumber == 4);
                        if (slot.AnimNumber == 4) { cycles = dispatch + 6; return true; }
                        int budget = ExpandedBulletBudget(out bool allowed);
                        dispatch += 6 + budget + ScrollBranch(0xCC26, 0xCC52, !allowed);
                        if (allowed)
                        {
                            dispatch += 4 + 2 + ScrollBranch(0xCC2D, 0xCC34, slot.AnimNumber == 5);
                            if (slot.AnimNumber != 5) dispatch += 2 + 6 + 4 + 2 + 4 + 4 + 6;
                            dispatch += 6 + ExpandedNormalShot(audioQueueAccepts, 5) + 3;
                        }
                        cycles = dispatch + 6;
                        return true;
                    }
                    dispatch += 4 + 2 + ScrollBranch(0xCBF4, 0xCC04, slot.AnimNumber != 5);
                    cycles = dispatch + ExpandedGroundIdleTail(slot.AnimNumber, frame, timer, slot.AnimNumber == 5);
                    return true;
                }
                bool movingShot = (pressed & 0x40) != 0;
                dispatch += 3 + 3 + 2 + ScrollBranch(0xCC57, 0xCC6E, !movingShot);
                if (movingShot)
                {
                    int budget = ExpandedBulletBudget(out bool allowed);
                    dispatch += 6 + budget + ScrollBranch(0xCC5C, 0xCC6E, !allowed);
                    if (allowed)
                    {
                        dispatch += 4 + 2 + ScrollBranch(0xCC63, 0xCC6A, slot.AnimNumber == 5);
                        if (slot.AnimNumber != 5) dispatch += 2 + 6 + 4 + 2 + 4 + 4 + 6;
                        cycles = dispatch + 6 + ExpandedNormalShot(audioQueueAccepts, 5) + 6;
                        return true;
                    }
                }
                dispatch += 4 + 2 + ScrollBranch(0xCC73, 0xCC78, slot.AnimNumber != 5);
                if (slot.AnimNumber == 5)
                {
                    cycles = dispatch + 3 + ExpandedGroundIdleTail(slot.AnimNumber, frame, timer, true);
                    return true;
                }
                dispatch += 4 + 2 + ScrollBranch(0xCC7D, 0xCC84, slot.AnimNumber == 3);
                if (slot.AnimNumber != 3)
                {
                    dispatch += 2 + 6 + 4 + 2 + 4 + 4 + 6;
                    frame = timer = 0;
                }
                dispatch += 4 + 2 + ScrollBranch(0xCC89, 0xCC9C, frame != 4);
                if (frame == 4)
                {
                    dispatch += 4 + 2 + ScrollBranch(0xCC90, 0xCC9C, timer != 6);
                    if (timer == 6) { dispatch += 2 + 4 + 2 + 4; frame = 1; timer = 0; }
                }
                dispatch += 4 + 2 + ScrollBranch(0xCCA1, 0xCCB4, frame != 0);
                byte velocityFraction = 0x80, velocityInteger = 1;
                if (frame == 0)
                {
                    dispatch += 4 + 2 + ScrollBranch(0xCCA8, 0xCCB4, timer >= 4);
                    if (timer < 4) { dispatch += 2 + 4 + 2 + 4; velocityFraction = 0x48; velocityInteger = 0; }
                }
                bool right = (direction & 1) != 0;
                dispatch += 2 + 3 + 2 + 3 + 2 + ScrollBranch(0xCCBE, 0xCCDF, !right);
                dispatch += 3 + ScrollBranch(right ? 0xCCC2 : 0xCCE1, 0xCD0D, (WallBlocked & 0x80) != 0);
                if ((WallBlocked & 0x80) != 0) { cycles = dispatch + 6; return true; }
                if (right)
                {
                    dispatch += 3 + 2 + ScrollBranch(0xCCC8, 0xCCD5, CheckpointId != 0x0E);
                    if (CheckpointId == 0x0E) throw new NotSupportedException("CCCA checkpoint0E facing veto");
                }
                dispatch += 2 + 2 + 6;
                dispatch += ExpandedHorizontalSimple(X, nextY, right, velocityFraction, velocityInteger, right ? 0 : 3);
                if (right) dispatch += 3;
                dispatch += 3 + 2 + ScrollBranch(0xCCEE, 0xCD0D, true) + 6;
                cycles = dispatch;
                return true;
            }
            catch (NotSupportedException exception)
            {
                unsupported = exception.Message;
                return false;
            }
        }

        private int ExpandedGroundIdleTail(int animation, int frame, int timer, bool checkFirePose)
        {
            int cycles = 0;
            bool normalize = true;
            if (checkFirePose)
            {
                cycles += 4 + 2 + ScrollBranch(0xCBFB, 0xCC16, frame != 2);
                normalize = frame == 2;
                if (normalize)
                {
                    cycles += 4 + 2 + ScrollBranch(0xCC02, 0xCC16, timer != 4);
                    normalize = timer == 4;
                }
            }
            if (normalize)
            {
                cycles += 4 + 2 + ScrollBranch(0xCC09, 0xCC16, animation == 1);
                if (animation != 1) cycles += 2 + 6 + 4 + 2 + 4 + 4 + 6 + 2 + 3 + 3;
            }
            return cycles + 3 + 2 + ScrollBranch(0xCC1A, 0xCC52, true) + 6;
        }

        private int ExpandedBulletBudget(out bool allowed)
        {
            if (WeaponActive != 0) throw new NotSupportedException("CF3A non-normal weapon budget");
            int cycles = 2 + 3 + 3 + 4 + 3 + 2;
            int count = 0;
            for (int index = 4; index >= 2; index--)
            {
                var slot = Objects.Slots[index];
                cycles += 4 + ScrollBranch(0xCF4A, 0xCF60, slot.Active);
                if (slot.Active)
                {
                    cycles += 3 + 2 + ScrollBranch(0xCF64, 0xCF7A, false) + 2 + ScrollBranch(0xCF68, 0xCF9B, false);
                    cycles += 2 + ScrollBranch(0xCF6C, 0xCFB5, false) + 3 + 4;
                    bool different = slot.AnimNumber != WeaponCountedAnims[0][0];
                    cycles += ScrollBranch(0xCF73, 0xCF4C, different);
                    if (!different) { count++; cycles += 5 + 3; }
                }
                cycles += 2 + 2 + ScrollBranch(0xCF4F, 0xCF47, index != 2);
            }
            allowed = count < WeaponMaxActive[0];
            return cycles + 3 + 3 + 4 + ScrollBranch(0xCF58, 0xCF5D, !allowed) + 2 + 6;
        }

        /// <summary>$CE39 normal shot; <paramref name="pose"/> is $0420 at $CEA1 (crouch 7: Y+8,
        /// air shots $0E/$0F: same Y, otherwise Y−3).</summary>
        private int ExpandedNormalShot(bool? audioQueueAccepts, byte pose)
        {
            if (WeaponActive != 0) throw new NotSupportedException("CE3D non-normal projectile");
            int cycles = 3 + ScrollBranch(0xCE3B, 0xCE40, true) + 4 + 2 + ScrollBranch(0xCE45, 0xCE38, false) + 2;
            int free = -1;
            for (int index = WeaponSlotPref[0]; index >= 2; index--)
            {
                bool inactive = !Objects.Slots[index].Active;
                cycles += 4 + ScrollBranch(0xCE4B, 0xCE55, inactive);
                if (inactive) { free = index; break; }
                cycles += 2 + 2 + ScrollBranch(0xCE50, 0xCE48, index != 2);
            }
            if (free < 0) return cycles + 2 + 6;
            if (!audioQueueAccepts.HasValue) throw new NotSupportedException("F850 native audio queue owner unavailable");
            cycles += 2 + 5 + 4 + 5 + 3 + 2 + ScrollBranch(0xCE64, 0xCE87, false) + 2 + ScrollBranch(0xCE68, 0xCE87, false);
            cycles += 3 + 2 + 2 + 4 + 2 + 4 + 3 + 4 + 4 + 5 + 4 + 5 + 3;
            cycles += 4 + 2;
            if (pose == 7)
            {
                bool zero = (byte)(Y + 8) == 0;
                cycles += ScrollBranch(0xCEA6, 0xCEBC, true) + 4 + 2 + 2 + 5 + (zero ? 2 + 4 + 5 : 3);
            }
            else
            {
                cycles += ScrollBranch(0xCEA6, 0xCEBC, false) + 2;
                if (pose == 0x0E) cycles += ScrollBranch(0xCEAA, 0xCEC7, true) + 4 + 5;
                else
                {
                    cycles += ScrollBranch(0xCEAA, 0xCEC7, false) + 2;
                    if (pose == 0x0F) cycles += ScrollBranch(0xCEAE, 0xCEC7, true) + 4 + 5;
                    else cycles += ScrollBranch(0xCEAE, 0xCEC7, false) + 4 + 2 + 2 + 5 + 3;
                }
            }
            cycles += 4 + 5 + 3 + 5 + 3 + 4 + 5 + 4 + 6 + 5 + 2 + 5 + 5 + 6;
            cycles += 4 + 6 + ExpandedQueueCycles(audioQueueAccepts.Value) + 2 + 5 + 6;
            return cycles;
        }

        private int ExpandedGroundVertical(out int nextY, out int maximum)
        {
            if (YVelInt >= 0 || _hookLatch != 0) throw new NotSupportedException("F2D7 ground rise/hook path");
            int fraction = YFrac - YVelFrac;
            int lowY = (byte)((byte)Y - (byte)YVelInt - (fraction < 0 ? 1 : 0));
            int rowY = Y >> 8;
            int integrate = 4 + 2 + 4 + 5 + 4 + 4 + 5 + 2 + ScrollBranch(0xF5B9, 0xF5C8, lowY < 240);
            if (lowY >= 240) { lowY = (byte)(lowY + 0x10); rowY = (byte)(rowY + 1); integrate += 2 + 5 + 4 + 2 + 5; }
            integrate += 6;
            nextY = rowY << 8 | lowY;
            int velocity = (((byte)YVelInt << 8 | YVelFrac) - GravityFrac) & 65535;
            if ((velocity >> 8) < 0xF9) throw new NotSupportedException("F5F1 ground gravity clamp");
            int gravity = 4 + 2 + 3 + 5 + 4 + 2 + 5 + ScrollBranch(0xF5EB, 0xF5FB, false) + 2 + ScrollBranch(0xF5EF, 0xF5FB, true) + 6;
            int pair = ExpandedVerticalPair(X, nextY, out bool standing, out int push, out int scratch);
            if (standing)
            {
                int landY = nextY;
                int head = 3 + 3 + 4 + 3 + 4 + 3 + 4 + ScrollBranch(0xF2E8, 0xF2ED, true) + 6 + integrate;
                head += 2 + ScrollBranch(0xF2F2, 0xF31F, false) + 6 + pair + ScrollBranch(0xF2F7, 0xF31F, false);
                head += PlayerActorLandCycles(X, ref landY, scratch, push, out maximum);
                nextY = landY;
                return head;
            }
            int probe = ExpandedProbeSet(X, nextY, 6, out maximum, out int lastY, 3);
            int nibble = lastY & 15;
            if (maximum == 0 || maximum == 2 || (maximum == 1 && nibble >= 8)) throw new NotSupportedException("F360 ground unlatch/hook dance");
            int cycles = 3 + 3 + 4 + 3 + 4 + 3 + 4 + ScrollBranch(0xF2E8, 0xF2ED, true) + 6 + integrate;
            cycles += 2 + ScrollBranch(0xF2F2, 0xF31F, false) + 6 + pair + ScrollBranch(0xF2F7, 0xF31F, true);
            cycles += 6 + gravity + 3 + ScrollBranch(0xF324, 0xF366, false) + 6 + probe;
            cycles += 2 + 3 + 2 + ScrollBranch(0xF32E, 0xF34B, false) + ScrollBranch(0xF330, 0xF33A, maximum != 1);
            if (maximum == 1) cycles += 3 + 2 + 2 + ScrollBranch(0xF338, 0xF360, false);
            cycles += 2 + ScrollBranch(0xF33C, 0xF344, false) + 3 + 2 + ScrollBranch(0xF342, 0xF360, false);
            bool borrow = lowY < nibble;
            int snap = 3 + 2 + 3 + 4 + 2 + 3 + 5 + ScrollBranch(0xEDE1, 0xEDF0, !borrow);
            if (borrow) { snap += 2 + 5 + 4 + 2 + 5; rowY = (byte)(rowY - 1); }
            nextY = rowY << 8 | (byte)(lowY - nibble - (borrow ? 0x10 : 0));
            snap += 6;
            return cycles + 6 + snap + 6 + 2 + 5 + 2 + 5 + 6 + 2 + 6;
        }

        private int ExpandedHorizontalSimple(int coordinateX, int coordinateY, bool right, byte velocityFraction, byte velocityInteger, int first)
        {
            int fraction = right ? XFrac + velocityFraction : XFrac - velocityFraction;
            int lowX = right ? (byte)coordinateX + velocityInteger + (fraction > 255 ? 1 : 0) : (byte)coordinateX - velocityInteger - (fraction < 0 ? 1 : 0);
            int nextX = (ushort)(right ? coordinateX + velocityInteger + (fraction > 255 ? 1 : 0) : coordinateX - velocityInteger - (fraction < 0 ? 1 : 0));
            int integrate = 4 + 2 + 4 + 5 + 4 + 4 + 5;
            integrate += right ? ScrollBranch(0xF510, 0xF51A, lowX <= 255) : ScrollBranch(0xF52E, 0xF538, lowX >= 0);
            if (right ? lowX > 255 : lowX < 0) integrate += 4 + 2 + 5;
            integrate += 6;
            int pair = TimingRejectedHorizontalPair(nextX, coordinateY);
            int probe = ExpandedProbeSet(nextX, coordinateY, first, out int maximum, out _, 3);
            int cycles = 3 + 3 + 6 + integrate + 2 + ScrollBranch(right ? 0xF209 : 0xF25D, right ? 0xF23F : 0xF29A, false) + 6 + pair;
            cycles += ScrollBranch(right ? 0xF20E : 0xF262, right ? 0xF23F : 0xF29A, true) + 6 + probe;
            cycles += 3 + 2 + ScrollBranch(right ? 0xF246 : 0xF2A1, right ? 0xF24B : 0xF2A6, maximum < 3);
            if (maximum >= 3) cycles += 6 + (right ? 3 + 2 + 3 + 4 + 2 + 3 + 5 + 4 + 2 + 5 + 6 : 3 + 2 + 2 + 2 + 4 + 5 + 4 + 2 + 5 + 6);
            return cycles + 4 + 2 + 5 + 6;
        }

        private int ExpandedProbeSet(int coordinateX, int coordinateY, int first, out int maximum, out int lastY, int allowedMaximum = 1)
        {
            int cycles = 3 + 2 + ScrollBranch(0xEB56, 0xEB60, false) + 4 + 2 + 5 + 2 + 3;
            maximum = 0;
            lastY = 0;
            for (int point = 0; point < 3; point++)
            {
                int pointCycles = ExpandedPoint(coordinateX, coordinateY, first + point, out int pointRow, out int rawClass, out lastY);
                if (rawClass > allowedMaximum) throw new NotSupportedException("EB52 class outside verified slice");
                bool lower = rawClass < maximum;
                cycles += 3 + 6 + pointCycles + 3 + 5 + 3 + ScrollBranch(0xEB70, 0xEB9A, lower);
                if (!lower)
                {
                    maximum = rawClass;
                    cycles += 3 + 2 + ScrollBranch(0xEB76, 0xEB80, false) + 3 + 3 + 3 + 3;
                    cycles += 3 + 2 + 3 + 2 + 3 + 3 + 3 + 2 + 3;
                    int rowDifference = pointRow - ScrollYRow;
                    cycles += ScrollBranch(0xEB8F, 0xEB9A, rowDifference < 0);
                    if (rowDifference >= 0)
                    {
                        cycles += ScrollBranch(0xEB91, 0xEB9A, rowDifference == 0);
                        if (rowDifference != 0) cycles += 3 + 2 + 3 + 3;
                    }
                }
                cycles += 2 + ScrollBranch(0xEB9C, 0xEBA4, true) + 5 + 5 + ScrollBranch(0xEBA8, 0xEB64, point != 2);
            }
            return cycles + 2 + ScrollBranch(0xEBAC, 0xEBBD, false) + 3 + 2 + ScrollBranch(0xEBB2, 0xEBC8, false)
                + 2 + ScrollBranch(0xEBB6, 0xEBE3, false) + 2 + ScrollBranch(0xEBBA, 0xEBDB, false) + 6;
        }

        /// <summary>$F64A for the player at (x, y): the slot-1 pair test, then the $F784 scan of
        /// slots 2..15. A solid actor ($0400/$0570 bit 7) runs the $F7A8 geometry; a hit leaves
        /// through $F7DE with SEC, which the callers do not price yet.</summary>
        private int ExpandedRejectedVerticalPair(int x, int y)
        {
            int cycles = ExpandedVerticalPair(x, y, out bool hit, out _);
            if (hit) throw new NotSupportedException("F79E stand-on-actor contact");
            return cycles;
        }

        /// <summary>$F64A for the player; on a $F7A8 hit it leaves through $F7DE with SEC and
        /// <paramref name="push"/> = $11 (class height − |dy|).</summary>
        private int ExpandedVerticalPair(int x, int y, out bool hit, out int push) => ExpandedVerticalPair(x, y, out hit, out push, out _);

        /// <summary>As above; <paramref name="scratch"/> is $02/$03 after a scan hit (class | |dY|·256).</summary>
        private int ExpandedVerticalPair(int x, int y, out bool hit, out int push, out int scratch)
        {
            hit = false;
            push = 0;
            scratch = -1;
            var partner = Objects.Slots[1];
            bool rejected = !partner.Active;
            int cycles = 2 + 2 + 2 + 4 + ScrollBranch(0xF651, 0xF695, rejected);
            if (!rejected) { rejected = (Flags & 0x80) == 0; cycles += 4 + ScrollBranch(0xF656, 0xF695, rejected); }
            if (!rejected) { rejected = (partner.Flags & 0x80) == 0; cycles += 4 + ScrollBranch(0xF65B, 0xF695, rejected); }
            if (!rejected) { rejected = partner.Behavior == 0; cycles += 4 + ScrollBranch(0xF660, 0xF695, rejected); }
            if (!rejected) throw new NotSupportedException("F662 vertical pair geometry");
            cycles += 2 + 3 + 2;
            for (int slot = 2; slot < 16; slot++)
            {
                var state = Objects.Slots[slot];
                bool skip = !state.Active;
                cycles += 4 + ScrollBranch(0xF789, 0xF7A1, skip);
                if (!skip) { skip = (state.Flags & 0x80) == 0; cycles += 4 + ScrollBranch(0xF78E, 0xF7A1, skip); }
                if (!skip) { skip = (state.BehaviorParam & 0x80) == 0; cycles += 4 + ScrollBranch(0xF793, 0xF7A1, skip); }
                if (!skip)
                {
                    cycles += 3 + 6 + TimingStandGeometry(x, y, state, out hit, out push) + 3;
                    if (hit)
                    {
                        int touchClass = state.BehaviorParam & 31;
                        scratch = touchClass | (EnemyBehaviors.TouchYHalf[touchClass] - push) << 8;
                        return cycles + 2 + 3 + 2 + 6;                   // BCC not taken, JMP $F7DE: SEC / RTS
                    }
                    cycles += ScrollBranch(0xF79C, 0xF7A1, true);
                }
                cycles += 2 + 2 + ScrollBranch(0xF7A4, 0xF786, slot != 15);
            }
            return cycles + 2 + 6;
        }

        /// <summary>$F699 with X = the player: $0400 bit 4 selects the 16-bit world distance.</summary>
        private int TimingDistanceX(int x, ObjectSystem.Slot actor, out int low, out int high)
        {
            if ((Objects.Slots[0].Flags & 0x10) == 0) throw new NotSupportedException("F6C6 screen-relative actor distance");
            int delta = (x - actor.X) & 0xFFFF;
            int cycles = 4 + 2 + 2 + 4 + 2 + 4 + 3 + 4 + 4 + 3;
            bool negative = x < actor.X;
            if (negative) { delta = (-delta) & 0xFFFF; cycles += 2 + 3 + 2 + 2 + 3 + 3 + 2 + 2 + 3; }
            else cycles += 3;
            low = delta & 255; high = delta >> 8;
            return cycles + 3 + 6;
        }

        /// <summary>$F6DF: player Y minus the actor Y moved by ±$10 across a 240-row page; the
        /// result is folded by the sign of its LOW byte.</summary>
        private static int TimingDistanceY(int y, ObjectSystem.Slot actor, out byte value)
        {
            int actorY = actor.Y;
            int cycles = 4 + 3 + 4 + 3 + 4;
            int actorPage = actorY >> 8, playerPage = (y >> 8) & 255;
            if (actorPage == playerPage) cycles += 4;                    // BEQ $F70B crosses $F6/$F7
            else if (actorPage > playerPage) { cycles += 2 + 2 + 3 + 2 + 3 + 3 + 2 + 3 + 3; actorY = (actorY - 0x10) & 0xFFFF; }
            else { cycles += 2 + 3 + 3 + 2 + 3 + 3 + 2 + 3; actorY = (actorY + 0x10) & 0xFFFF; }
            byte low = (byte)((y & 255) - (actorY & 255));
            cycles += 4 + 2 + 3 + 3 + 4 + 3 + 3 + 3;
            if (low < 0x80) cycles += 3;
            else { cycles += 2 + 2 + 2 + 2; low = (byte)(-low); }
            value = low;
            return cycles + 4 + 6;
        }

        /// <summary>$F7A8 stand test: |dx| below the class width and |dy| at most the class height.</summary>
        private int TimingStandGeometry(int x, int y, ObjectSystem.Slot actor, out bool hit, out int push)
        {
            hit = false;
            push = 0;
            int cls = actor.BehaviorParam & 31;
            int cycles = 4 + 2 + 3 + 6 + TimingDistanceX(x, actor, out int low, out int high) + 3;
            if (high != 0) return cycles + 3 + 2 + 6;
            cycles += 2 + 3 + 3 + 4;
            if (low >= EnemyBehaviors.TouchXHalf[cls]) return cycles + 3 + 2 + 6;
            cycles += 2 + 3 + 6 + TimingDistanceY(y, actor, out byte dy) + 3 + 3 + 4 + (0xE9 + cls > 255 ? 1 : 0);
            int height = EnemyBehaviors.TouchYHalf[cls];
            if (dy == height) cycles += 3;
            else
            {
                cycles += 2;
                if (dy > height) return cycles + 3 + 2 + 6;
                cycles += 2;
            }
            hit = true;
            push = (byte)(height - dy);
            return cycles + 3 + 3 + 4 + (0xE9 + cls > 255 ? 1 : 0) + 2 + 3 + 3 + 6;
        }

        /// <summary>$F2F9…$F319 then $F49C and $F347 after a stand-on-actor hit: lift by $11 (at most 4),
        /// probe above ($09) when the lift moved the player above the pre-move Y, else below ($06);
        /// a solid ceiling (≥3) snaps down, a floor (≥2) snaps up. Always returns SEC.</summary>
        private int PlayerActorLandCycles(int x, ref int y, int previousY, int push, out int probeClass)
        {
            int cycles = 2 + 3;
            if (push > 4) { cycles += 2 + 3; push = 4; }
            else cycles += 4;                                              // BCS $F301 crosses $F2/$F3
            int low = (y & 255) - push, page = y >> 8;
            cycles += 4 + 2 + 3 + 5;
            if (low >= 0) cycles += 3;
            else { cycles += 2 + 2 + 5 + 4 + 2 + 5; low = (low - 0x10) & 255; page--; }
            y = ((page & 255) << 8) | (low & 255);
            cycles += 6 + 4 + 2 + 3 + 4 + 3;                               // JSR $F49C, Y − ($02,$03)
            bool up = y < previousY;
            cycles += ScrollBranch(0xF4A7, 0xF4BE, !up) + 2 + 3 + 6;
            int probe = ExpandedProbeSet(x, y, up ? 9 : 6, out probeClass, out int lastY, 5);
            cycles += probe + 3 + 2;
            if (up)
            {
                if (probeClass < 3) cycles += 3 + 6;
                else
                {
                    cycles += 2 + 6 + PlayerSnapDown(ref y, lastY & 15) + 6 + 20 + 2 + 6;
                }
            }
            else
            {
                if (probeClass < 2) cycles += 3 + 2 + 6;
                else cycles += 2 + (probeClass != 2 ? 3 : 2) + 6 + PlayerSnapUp(ref y, lastY & 15) + 6 + 20 + 2 + 6;
            }
            return cycles + 3 + 6 + 20 + 2 + 6;                           // JMP $F347: $F5FC, SEC, RTS
        }

        private static int PlayerSnapUp(ref int y, int nibble)
        {
            int low = (y & 255) - nibble, page = y >> 8;
            bool borrow = low < 0;
            if (borrow) { low = (low - 0x10) & 255; page--; }
            y = ((page & 255) << 8) | (low & 255);
            return 3 + 2 + 3 + 4 + 2 + 3 + 5 + (borrow ? 2 + 2 + 5 + 4 + 2 + 5 : 3) + 6;
        }

        private static int PlayerSnapDown(ref int y, int nibble)
        {
            int low = ((y & 255) + ((~nibble) & 15) + 1) & 255, page = y >> 8;
            bool wrap = low >= 0xF0;
            if (wrap) { low = (low + 0x10) & 255; page++; }
            y = ((page & 255) << 8) | low;
            return 3 + 2 + 2 + 2 + 4 + 5 + 2 + (wrap ? 2 + 2 + 5 + 4 + 2 + 5 : 3) + 6;
        }

        /// <summary>$F74E side test: |dx| at most the class width and |dy| below the class height.</summary>
        private int TimingSideGeometry(int x, int y, ObjectSystem.Slot actor, out bool hit)
        {
            hit = false;
            int cls = actor.BehaviorParam & 31;
            int cycles = 4 + 2 + 3 + 6 + TimingDistanceX(x, actor, out int low, out int high) + 3;
            if (high != 0) return cycles + 3 + 2 + 6;
            cycles += 2 + 3 + 3 + 4;
            int width = EnemyBehaviors.TouchXHalf[cls];
            if (low == width) cycles += 3;
            else
            {
                cycles += 2;
                if (low > width) return cycles + 3 + 2 + 6;
                cycles += 2;
            }
            cycles += 3 + 3 + 6 + TimingDistanceY(y, actor, out byte dy) + 3 + 3 + 4 + (0xE9 + cls > 255 ? 1 : 0);
            if (dy >= EnemyBehaviors.TouchYHalf[cls]) return cycles + 3 + 2 + 6;
            hit = true;
            return cycles + 2 + 3 + 4 + 2 + 3 + 3 + 6;
        }

        private static int ExpandedQueueCycles(bool accepts)
        {
            int cycles = 3 + 3 + 3 + 4 + 2 + ScrollBranch(0xF85A, 0xF866, !accepts);
            if (accepts) cycles += 3 + 4 + 2 + 2 + 2 + 3;
            return cycles + 3 + 6;
        }

        private int ExpandedPoint(int coordinateX, int coordinateY, int point, out int pointRow, out int rawClass, out int lowY)
        {
            int offsetX = TimingPointX[point], offsetY = TimingPointY[point];
            int readX = 4 + (((0x19 + point) > 255) ? 1 : 0);
            int readY = 4 + (((0xAD + point) > 255) ? 1 : 0);
            int cycles = 2 + 3 + readX + ScrollBranch(0xEBEF, 0xEBF3, offsetX >= 0);
            if (offsetX < 0) cycles += 5;
            int pointX = coordinateX + offsetX;
            cycles += 4 + 2 + readX + 3 + 4 + 3 + 3 + ScrollBranch(0xEC03, 0xEC4D, (pointX & 0x8000) != 0);
            if ((pointX & 0x8000) != 0) throw new NotSupportedException("EC03 negative X probe scratch residency");
            cycles += 30 + 4 + 3 + readY + ScrollBranch(0xEC1E, 0xEC36, offsetY < 0);
            lowY = (byte)coordinateY + offsetY;
            pointRow = coordinateY >> 8;
            if (offsetY < 0)
            {
                cycles += readY + 2 + 3 + 5 + 4 + 2 + 3 + ScrollBranch(0xEC45, 0xEC4B, lowY >= 0);
                if (lowY < 0) { lowY = (byte)(lowY - 0x10); pointRow--; cycles += 2 + 5; }
                cycles += 3;
            }
            else
            {
                cycles += 4 + 2 + readY + ScrollBranch(0xEC27, 0xEC2D, lowY > 255);
                if (lowY <= 255) cycles += 2 + ScrollBranch(0xEC2B, 0xEC31, lowY < 240);
                if (lowY >= 240) { lowY = (byte)(lowY + 0x10); pointRow++; cycles += 2 + 5; }
                cycles += 3 + 3;
            }
            lowY &= 255;
            int pointY = (pointRow << 8) | lowY;
            bool sameCameraPage = (pointY - ((ScrollYRow << 8) | ScrollY) & 0xFF00) == 0;
            cycles += 3 + 2 + 3 + 3 + 3 + ScrollBranch(0xEC56, 0xEC6D, sameCameraPage);
            if (!sameCameraPage)
            {
                bool belowCamera = pointY >= ((ScrollYRow << 8) | ScrollY);
                cycles += ScrollBranch(0xEC58, 0xEC62, belowCamera);
                if (!belowCamera)
                {
                    cycles += 2 + ScrollBranch(0xEC5C, 0xEC62, false) + 2 + ScrollBranch(0xEC60, 0xEC65, point < 6);
                    if (point < 6) throw new NotSupportedException("EC65 camera-clamped side probe");
                }
                rawClass = 0;
                return cycles + 2 + 6;
            }
            rawClass = _tiles.TypeAt(pointX, pointY);
            cycles += 71;
            bool highNibble = (lowY & 0x10) != 0;
            cycles += ScrollBranch(0xEC9B, 0xECA0, highNibble);
            cycles += highNibble ? 3 + 8 + 6 : 3 + 6;
            return cycles;
        }
    }
}
