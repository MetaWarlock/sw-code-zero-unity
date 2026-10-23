namespace Darkwing.Core
{
    public static class InputTiming
    {
        public const int ResumeCallCycles = 6;
        public const int ResumeEpilogueCycles = 18;

        public readonly struct PollResult
        {
            public readonly byte PressedP1;
            public readonly byte PressedP2;
            public readonly byte HeldP1;
            public readonly byte HeldP2;
            public readonly int HookCycles;

            public PollResult(byte pressedP1, byte pressedP2, byte heldP1, byte heldP2, int hookCycles)
            {
                PressedP1 = pressedP1;
                PressedP2 = pressedP2;
                HeldP1 = heldP1;
                HeldP2 = heldP2;
                HookCycles = hookCycles;
            }
        }

        public static PollResult Poll(byte rawP1, byte rawP2, byte previousP1, byte previousP2)
        {
            int cycles = 14;
            for (int index = 0; index < 8; index++) cycles += index == 7 ? 40 : 41;
            cycles += 18 + 2;
            byte pressedP2 = (byte)(rawP2 & ~previousP2);
            cycles += 27;
            byte pressedP1 = (byte)(rawP1 & ~previousP1);
            cycles += 26 + 2;
            byte heldP2 = Sanitize(rawP2, false, ref cycles);
            byte heldP1 = Sanitize(rawP1, false, ref cycles);
            pressedP2 = Sanitize(pressedP2, false, ref cycles);
            pressedP1 = Sanitize(pressedP1, true, ref cycles);
            cycles += 6;
            return new PollResult(pressedP1, pressedP2, heldP1, heldP2, cycles);
        }

        private static byte Sanitize(byte buttons, bool last, ref int cycles)
        {
            cycles += 4 + 2 + 2;
            if ((buttons & 0x0C) == 0x0C)
            {
                cycles += 3 + 4 + 2 + 4;
                buttons &= 0xF0;
            }
            else
            {
                cycles += 2 + 4 + 2 + 2;
                if ((buttons & 0x03) == 0x03)
                {
                    cycles += 2 + 4 + 2 + 4;
                    buttons &= 0xF0;
                }
                else cycles += 3;
            }
            cycles += 2 + (last ? 2 : 3);
            return buttons;
        }
    }
}
