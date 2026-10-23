using System;

namespace Darkwing.Core
{
    public sealed partial class PlayerCore
    {
        public int LastPlayerDispatchCycles { get; private set; } = -1;
        public string PlayerDispatchTimingUnsupported { get; private set; }
        public Func<bool> TimingAudioQueueAccepts;
        public Func<bool> TimingRenderingBlanked;
        internal byte HookLatchForTiming => _hookLatch;

        private void BeginPlayerDispatchTiming(byte held, byte pressed)
        {
            int cycles;
            string reason;
            bool supported;
            if (State == 0)
                supported = TryMeasureGround00IncludingJumpCycles(held, pressed, TimingAudioQueueAccepts?.Invoke(), out cycles, out reason);
            else if (State == 1)
                supported = TryMeasureAir01Cycles(held, pressed, TimingAudioQueueAccepts?.Invoke(), out cycles, out reason);
            else if (State == 4)
                supported = TryMeasureState04Cycles(held, pressed, out cycles, out reason);
            else if (State == 0x0B)
                supported = TryMeasureAirborne0BCycles(held, pressed, TimingAudioQueueAccepts?.Invoke(), out cycles, out reason);
            else
            {
                supported = false;
                cycles = -1;
                reason = "CB41 state outside reached dispatch timing";
            }
            if (supported)
            {
                LastPlayerDispatchCycles = cycles;
                PlayerDispatchTimingUnsupported = null;
            }
            else
            {
                LastPlayerDispatchCycles = -1;
                PlayerDispatchTimingUnsupported = reason;
            }
        }

        public readonly struct PlayerDispatchCost
        {
            public readonly int Wrapper, State, Vertical, Ceiling, Horizontal, PairScan, Side, Total;
            public readonly int[] Points;
            public PlayerDispatchCost(int wrapper, int state, int vertical, int ceiling, int horizontal, int pairScan, int side, int[] points)
            {
                Wrapper = wrapper; State = state; Vertical = vertical; Ceiling = ceiling;
                Horizontal = horizontal; PairScan = pairScan; Side = side; Points = points;
                Total = wrapper + state + vertical + ceiling + horizontal + pairScan + side;
            }
        }

        private static readonly sbyte[] TimingPointX = {12,12,12,-12,-12,-12,-11,0,11,-11,0,11};
        private static readonly sbyte[] TimingPointY = {-11,0,11,-11,0,11,12,12,12,-12,-12,-12};

        public bool TryMeasureRisingAirborneCycles(byte held, byte pressed,
            out PlayerDispatchCost result, out string unsupported)
        {
            result = default;
            unsupported = null;
            try
            {
                if (State != 0x0B) throw new NotSupportedException("CB41 state outside rising 0B slice");
                if (Objects == null || !Objects.Slots[0].Active) throw new NotSupportedException("CAFC inactive player");
                if ((Flags & 0x20) != 0) throw new NotSupportedException("EB52 native hook-flag clear mismatch");
                if (YVelInt < 0 || (pressed & 0x48) != 0) throw new NotSupportedException("D626 falling, reach or shot");
                if (Objects.Slots[0].AnimNumber == 0x0D) throw new NotSupportedException("D756 reach animation pinning");
                if ((held & 3) != 2) throw new NotSupportedException("D76E not left steering");
                int velocity = ((YVelInt & 255) << 8) | YVelFrac;
                int afterVelocity = (velocity - GravityFrac) & 0xFFFF;
                if ((afterVelocity & 0x8000) != 0) throw new NotSupportedException("D666 gravity sign change");
                int fraction = YFrac - YVelFrac;
                int yLow = (byte)Y - (YVelInt & 255) - (fraction < 0 ? 1 : 0);
                int nextY = (Y & 0xFF00) | (byte)yLow;
                int integrateY = 4 + 2 + 4 + 5 + 4 + 4 + 5 + ScrollBranch(0xF594, 0xF5A3, yLow >= 0);
                if (yLow < 0)
                {
                    nextY = (((Y >> 8) - 1) & 255) << 8 | (byte)(yLow - 0x10);
                    integrateY += 2 + 5 + 4 + 2 + 5;
                }
                integrateY += 6;
                int gravity = 4 + 2 + 3 + 5 + 4 + 2 + 5 + ScrollBranch(0xF5EB, 0xF5FB, true) + 6;
                int vertical = 4 + ScrollBranch(0xF5CC, 0xF5D4, true) + 6 + integrateY + 3 + gravity;
                var points = new int[6];
                int ceiling = TimingEmptyProbeSet(X, nextY, 9, points, 0);
                int fractionX = XFrac - 0x80;
                int lowX = (byte)X - 1 - (fractionX < 0 ? 1 : 0);
                int nextX = (ushort)(X - 1 - (fractionX < 0 ? 1 : 0));
                int integrateX = 4 + 2 + 4 + 5 + 4 + 4 + 5 + ScrollBranch(0xF52E, 0xF538, lowX >= 0);
                if (lowX < 0) integrateX += 4 + 2 + 5;
                integrateX += 6;
                int pair = TimingRejectedHorizontalPair();
                int side = TimingEmptyProbeSet(nextX, nextY, 2, points, 3);
                int wrapper = 0;
                int dispatch = 4 + ScrollBranch(0xCAFF, 0xCB41, true);
                dispatch += 2 + 2 + 5 + 2 + 5 + 3 + 2 + 2 + 4 + 3 + 4 + 3 + 5;
                dispatch += 4 + ScrollBranch(0xD629, 0xD636, false) + 2 + 3 + 2 + 3 + 3;
                dispatch += 3 + 2 + ScrollBranch(0xD642, 0xD650, true) + 6 + 6;
                dispatch += 3 + 2 + ScrollBranch(0xD65A, 0xD65F, true) + 2 + ScrollBranch(0xD661, 0xD666, true);
                dispatch += 4 + ScrollBranch(0xD669, 0xD671, true) + 4 + ScrollBranch(0xD674, 0xD679, false) + 3;
                dispatch += 4 + 2 + ScrollBranch(0xD75B, 0xD76E, true);
                dispatch += 2 + 3 + 2 + ScrollBranch(0xD774, 0xD79A, false) + 3 + 2 + ScrollBranch(0xD77A, 0xD795, true);
                dispatch += 2 + 6 + 3 + 2 + ScrollBranch(0xD79E, 0xD7B6, true) + 6;
                int horizontal = 3 + 3 + 6 + integrateX + 2 + ScrollBranch(0xF25D, 0xF29A, false) + 6;
                horizontal += ScrollBranch(0xF262, 0xF29A, true) + 6;
                horizontal += 3 + 2 + ScrollBranch(0xF2A1, 0xF2A6, true) + 4 + 2 + 5 + 6;
                result = new PlayerDispatchCost(wrapper, dispatch, vertical, ceiling, horizontal, pair, side, points);
                TimingEmptyProbePoint(nextX, nextY, 5, out _, true);
                return true;
            }
            catch (NotSupportedException exception)
            {
                unsupported = exception.Message;
                return false;
            }
        }

        private int TimingRejectedHorizontalPair()
        {
            var partner = Objects.Slots[1];
            int cycles = 2 + 2 + 2 + 4 + ScrollBranch(0xF619, 0xF646, !partner.Active);
            bool rejected = !partner.Active;
            if (!rejected)
            {
                rejected = (Flags & 0x80) == 0;
                cycles += 4 + ScrollBranch(0xF61E, 0xF646, rejected);
            }
            if (!rejected)
            {
                rejected = (partner.Flags & 0x80) == 0;
                cycles += 4 + ScrollBranch(0xF623, 0xF646, rejected);
            }
            if (!rejected)
            {
                rejected = partner.Behavior == 0;
                cycles += 4 + ScrollBranch(0xF628, 0xF646, rejected);
            }
            if (!rejected) throw new NotSupportedException("F62A partner geometry");
            cycles += 2 + 3 + 2;
            for (int slot = 2; slot < 16; slot++)
            {
                var state = Objects.Slots[slot];
                bool skip = !state.Active;
                cycles += 4 + ScrollBranch(0xF729, 0xF747, skip);
                if (!skip)
                {
                    skip = (state.Flags & 0x80) == 0;
                    cycles += 4 + ScrollBranch(0xF72E, 0xF747, skip);
                }
                if (!skip)
                {
                    skip = (state.BehaviorParam & 0x80) == 0;
                    cycles += 4 + ScrollBranch(0xF733, 0xF747, skip);
                }
                if (!skip) cycles += 3 + 2 + ScrollBranch(0xF739, 0xF747, State == 0x0B);
                cycles += 2 + 2 + ScrollBranch(0xF74A, 0xF726, slot != 15);
            }
            return cycles + 2 + 6;
        }

        private int TimingEmptyProbeSet(int x, int y, int first, int[] points, int destination)
        {
            int cycles = 3 + 2 + ScrollBranch(0xEB56, 0xEB60, false) + 4 + 2 + 5 + 2 + 3;
            for (int point = 0; point < 3; point++)
            {
                int kind = first + point;
                points[destination + point] = TimingEmptyProbePoint(x, y, kind, out int pointRow);
                cycles += 3 + 6 + points[destination + point] + 3 + 5 + 3 + ScrollBranch(0xEB70, 0xEB9A, false);
                cycles += 3 + 2 + ScrollBranch(0xEB76, 0xEB80, false) + 3 + 3 + 3 + 3;
                cycles += 3 + 2 + 3 + 2 + 3 + 3 + 3 + 2 + 3;
                int rowDifference = pointRow - ScrollYRow;
                cycles += ScrollBranch(0xEB8F, 0xEB9A, rowDifference < 0);
                if (rowDifference >= 0)
                {
                    cycles += ScrollBranch(0xEB91, 0xEB9A, rowDifference == 0);
                    if (rowDifference != 0) cycles += 3 + 2 + 3 + 3;
                }
                cycles += 2 + ScrollBranch(0xEB9C, 0xEBA4, true) + 5 + 5 + ScrollBranch(0xEBA8, 0xEB64, point != 2);
            }
            return cycles + 2 + ScrollBranch(0xEBAC, 0xEBBD, false) + 3 + 2 + ScrollBranch(0xEBB2, 0xEBC8, false)
                + 2 + ScrollBranch(0xEBB6, 0xEBE3, false) + 2 + ScrollBranch(0xEBBA, 0xEBDB, false) + 6;
        }

        private int TimingEmptyProbePoint(int x, int y, int point, out int pointRow, bool inertNativeExtra = false)
        {
            int offsetX = TimingPointX[point], offsetY = TimingPointY[point];
            int readX = 4 + (((0x19 + point) > 255) ? 1 : 0);
            int readY = 4 + (((0xAD + point) > 255) ? 1 : 0);
            int cycles = 2 + 3 + readX + ScrollBranch(0xEBEF, 0xEBF3, offsetX >= 0);
            if (offsetX < 0) cycles += 5;
            int pointX = x + offsetX;
            cycles += 4 + 2 + readX + 3 + 4 + 3 + 3 + ScrollBranch(0xEC03, 0xEC4D, (pointX & 0x8000) != 0);
            if ((pointX & 0x8000) != 0) throw new NotSupportedException("EC03 negative X probe scratch residency");
            cycles += 30 + 4 + 3 + readY + ScrollBranch(0xEC1E, 0xEC36, offsetY < 0);
            int lowY = (byte)y + offsetY;
            pointRow = y >> 8;
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
            if (!sameCameraPage) throw new NotSupportedException("EC58 out-of-camera/clamped probe");
            byte rawClass = _tiles.TypeAt(pointX, pointY);
            if (rawClass != 0 && (!inertNativeExtra || rawClass >= 3))
                throw new NotSupportedException("EB52 nonzero raw collision class " + rawClass + " point=" + point + " x=" + pointX.ToString("X4") + " y=" + pointY.ToString("X4"));
            cycles += 71;
            bool highNibble = (lowY & 0x10) != 0;
            cycles += ScrollBranch(0xEC9B, 0xECA0, highNibble);
            cycles += highNibble ? 3 + 8 + 6 : 3 + 6;
            return cycles;
        }
    }
}
