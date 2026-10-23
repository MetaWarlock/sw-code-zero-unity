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
                    pair = ExpandedRejectedVerticalPair();
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
                    int horizontalPair = TimingRejectedHorizontalPair();
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
                if ((held & 12) != 0 || (pressed & 0x80) != 0) throw new NotSupportedException("CBB7 ground jump/up/down");
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
                    dispatch += 3 + 2 + 2 + ScrollBranch(0xCBDD, 0xCC3A, false) + 2 + ScrollBranch(0xCBE1, 0xCBE6, true);
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
                            dispatch += 6 + ExpandedNormalShot(audioQueueAccepts) + 3;
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
                        cycles = dispatch + 6 + ExpandedNormalShot(audioQueueAccepts) + 6;
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

        private int ExpandedNormalShot(bool? audioQueueAccepts)
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
            cycles += 4 + 2 + ScrollBranch(0xCEA6, 0xCEBC, false) + 2 + ScrollBranch(0xCEAA, 0xCEC7, false);
            cycles += 2 + ScrollBranch(0xCEAE, 0xCEC7, false) + 4 + 2 + 2 + 5 + 3;
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
            int pair = ExpandedRejectedVerticalPair();
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
            foreach (var slot in Objects.Slots)
                if (slot.Active && (slot.Flags & slot.BehaviorParam & 0x80) != 0) throw new NotSupportedException("F73B armed horizontal geometry");
            int fraction = right ? XFrac + velocityFraction : XFrac - velocityFraction;
            int lowX = right ? (byte)coordinateX + velocityInteger + (fraction > 255 ? 1 : 0) : (byte)coordinateX - velocityInteger - (fraction < 0 ? 1 : 0);
            int nextX = (ushort)(right ? coordinateX + velocityInteger + (fraction > 255 ? 1 : 0) : coordinateX - velocityInteger - (fraction < 0 ? 1 : 0));
            int integrate = 4 + 2 + 4 + 5 + 4 + 4 + 5;
            integrate += right ? ScrollBranch(0xF510, 0xF51A, lowX <= 255) : ScrollBranch(0xF52E, 0xF538, lowX >= 0);
            if (right ? lowX > 255 : lowX < 0) integrate += 4 + 2 + 5;
            integrate += 6;
            int pair = TimingRejectedHorizontalPair();
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

        private int ExpandedRejectedVerticalPair()
        {
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
                if (!skip) throw new NotSupportedException("F797 armed vertical scan geometry");
                cycles += 2 + 2 + ScrollBranch(0xF7A4, 0xF786, slot != 15);
            }
            return cycles + 2 + 6;
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
