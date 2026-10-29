namespace Darkwing.Core
{
    /// <summary>
    /// One NES controller snapshot. Bit order matches the original shift register:
    /// A, B, Select, Start, Up, Down, Left, Right.
    /// </summary>
    public struct NesButtons
    {
        public bool A, B, Select, Start, Up, Down, Left, Right;

        public static NesButtons None => default;

        public byte ToByte()
        {
            return (byte)((A ? 0x80 : 0) | (B ? 0x40 : 0) | (Select ? 0x20 : 0) |
                          (Start ? 0x10 : 0) | (Up ? 0x08 : 0) | (Down ? 0x04 : 0) |
                          (Left ? 0x02 : 0) | (Right ? 0x01 : 0));
        }

        public static NesButtons FromByte(byte b)
        {
            return new NesButtons
            {
                A = (b & 0x80) != 0,
                B = (b & 0x40) != 0,
                Select = (b & 0x20) != 0,
                Start = (b & 0x10) != 0,
                Up = (b & 0x08) != 0,
                Down = (b & 0x04) != 0,
                Left = (b & 0x02) != 0,
                Right = (b & 0x01) != 0,
            };
        }
    }
}
