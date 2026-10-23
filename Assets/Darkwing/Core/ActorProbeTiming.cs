using System;

namespace Darkwing.Core
{
    public sealed class ActorProbeTiming
    {
        private readonly PlayerCore _player;
        private readonly ITileMap _tiles;
        private static readonly sbyte[] PointX = { 12,12,12,-12,-12,-12,-11,0,11,-11,0,11,2,2,2,-2,-2,-2,-1,1,1,-1,1,1,12,12,12,-12,-12,-12,-1,0,1,-1,0,1,9,9,-9,-9,-8,8,-8,8 };
        private static readonly sbyte[] PointY = { -11,0,11,-11,0,11,12,12,12,-12,-12,-12,-1,1,1,-1,1,1,2,2,2,-2,-2,-2,-1,0,1,-1,0,1,2,2,2,-2,-2,-2,-7,7,-7,7,8,8,-8,-8 };
        public int LastPointY { get; private set; }

        public ActorProbeTiming(PlayerCore player, ITileMap tiles) { _player = player; _tiles = tiles; }

        public int MeasureEmptyProbe(int coordinateX, int coordinateY, int first, int count = 3)
        {
            int cycles = MeasureProbe(coordinateX, coordinateY, first, count, out byte maximum);
            if (maximum >= 2) throw new NotSupportedException("EB52 solid/special tile");
            return cycles;
        }

        public int MeasureProbe(int coordinateX, int coordinateY, int first, int count, out byte maximum)
        {
            int cycles = 3 + 2 + Branch(0xEB56, 0xEB60, _player.State == 0x0C)
                + (_player.State == 0x0C ? 0 : 4 + 2 + 5) + 2 + 3;
            maximum = 0;
            for (int pointIndex = 0; pointIndex < count; pointIndex++)
            {
                int pointCycles = MeasurePoint(coordinateX, coordinateY, first + pointIndex, out int pointRow, out byte tileClass);
                cycles += 3 + 6 + pointCycles + 3 + 5 + 3 + Branch(0xEB70, 0xEB9A, tileClass < maximum);
                if (tileClass >= maximum)
                {
                    maximum = tileClass;
                    if (maximum >= 6) throw new NotSupportedException("EB52 special tile");
                    cycles += 3 + 2 + Branch(0xEB76, 0xEB80, true);
                    cycles += 3 + 2 + 3 + 2 + 3 + 3 + 3 + 2 + 3;
                    int rowDifference = pointRow - _player.ScrollYRow;
                    cycles += Branch(0xEB8F, 0xEB9A, rowDifference < 0);
                    if (rowDifference >= 0)
                    {
                        cycles += Branch(0xEB91, 0xEB9A, rowDifference == 0);
                        if (rowDifference != 0) cycles += 3 + 2 + 3 + 3;
                    }
                }
                cycles += 2 + Branch(0xEB9C, 0xEBA4, false) + 3 + 2 + Branch(0xEBA2, 0xEBAA, maximum >= 2);
                if (maximum >= 2) break;
                cycles += 5 + 5 + Branch(0xEBA8, 0xEB64, pointIndex != count - 1);
            }
            return cycles + 2 + Branch(0xEBAC, 0xEBBD, true) + 3 + 2 + Branch(0xEBC1, 0xEBE3, false)
                + 2 + Branch(0xEBC5, 0xEBDB, false) + 6;
        }

        private int MeasurePoint(int coordinateX, int coordinateY, int point, out int pointRow, out byte tileClass)
        {
            int offsetX = PointX[point], offsetY = PointY[point];
            int readX = 4 + (0x19 + point > 255 ? 1 : 0);
            int readY = 4 + (0xAD + point > 255 ? 1 : 0);
            int cycles = 2 + 3 + readX + Branch(0xEBEF, 0xEBF3, offsetX >= 0);
            if (offsetX < 0) cycles += 5;
            int pointX = coordinateX + offsetX;
            cycles += 4 + 2 + readX + 3 + 4 + 3 + 3 + Branch(0xEC03, 0xEC4D, (pointX & 0x8000) != 0);
            if ((pointX & 0x8000) != 0) throw new NotSupportedException("EC03 negative X");
            cycles += 30 + 4 + 3 + readY + Branch(0xEC1E, 0xEC36, offsetY < 0);
            int lowY = (byte)coordinateY + offsetY;
            pointRow = coordinateY >> 8;
            if (offsetY < 0)
            {
                cycles += readY + 2 + 3 + 5 + 4 + 2 + 3 + Branch(0xEC45, 0xEC4B, lowY >= 0);
                if (lowY < 0) { lowY = (byte)(lowY - 0x10); pointRow--; cycles += 2 + 5; }
                cycles += 3;
            }
            else
            {
                cycles += 4 + 2 + readY + Branch(0xEC27, 0xEC2D, lowY > 255);
                if (lowY <= 255) cycles += 2 + Branch(0xEC2B, 0xEC31, lowY < 240);
                if (lowY >= 240) { lowY = (byte)(lowY + 0x10); pointRow++; cycles += 2 + 5; }
                cycles += 3 + 3;
            }
            lowY &= 255;
            int pointY = (pointRow << 8) | lowY;
            LastPointY = pointY;
            int cameraDifference = pointY - ((_player.ScrollYRow << 8) | _player.ScrollY);
            bool sameCameraPage = (cameraDifference & 0xFF00) == 0;
            cycles += 3 + 2 + 3 + 3 + 3 + Branch(0xEC56, 0xEC6D, sameCameraPage);
            if (!sameCameraPage)
            {
                tileClass = 0;
                return cycles + (cameraDifference >= 0 ? 3 : 2 + 2 + 3) + 2 + 6;
            }
            tileClass = _tiles.TypeAt(pointX, pointY);
            cycles += 71;
            bool highNibble = (lowY & 0x10) != 0;
            cycles += Branch(0xEC9B, 0xECA0, highNibble);
            cycles += highNibble ? 3 + 8 + 6 : 3 + 6;
            return cycles;
        }

        private static int Branch(int instruction, int target, bool taken)
            => taken ? 3 + (((instruction + 2) & 0xFF00) == (target & 0xFF00) ? 0 : 1) : 2;
    }
}
