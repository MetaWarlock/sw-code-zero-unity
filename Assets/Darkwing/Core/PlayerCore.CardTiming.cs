using System;

namespace Darkwing.Core
{
    public sealed partial class PlayerCore
    {
        public int LastCardContinuationCycles { get; private set; } = -1;
        public bool LastCardContinuationReleased { get; private set; }
        public string CardTimingUnsupported { get; private set; }
        public byte CardWaitRemaining => _cardWait;

        private void BeginCardContinuationTiming()
        {
            LastCardContinuationCycles = -1;
            LastCardContinuationReleased = false;
            CardTimingUnsupported = null;
            if (State != 9 || !_cardPhase || _cardWait == 0) return;
            if (_cardWait > 1)
            {
                LastCardContinuationCycles = 2 + 4 + 6 + 49;
                return;
            }
            if (WorldId > 6 || WeaponSelected > 3)
            {
                CardTimingUnsupported = "D554 mission release context";
                return;
            }
            int cycles = 2 + 2 + 6 + 6;
            cycles += 2 + 3 + 2 + 6 + (4 + 2 + 4 + 4 + 6);
            cycles += 6 + MeasureCardHudSeedCycles(WeaponSelected);
            cycles += 3 + 4 + ((0x89 + WorldId > 255) ? 1 : 0) + 3 + 6;
            LastCardContinuationCycles = cycles;
            LastCardContinuationReleased = true;
        }

        public static int MeasureCardHudSeedCycles(byte weapon)
        {
            if (weapon > 3) throw new NotSupportedException("C850 weapon selection");
            int cycles = 2;
            for (int index = 17; index >= 0; index--)
                cycles += 4 + 5 + 2 + (index == 0 ? 2 : 3);
            cycles += 3;
            for (int selection = 0; selection <= weapon; selection++)
                cycles += 2 + (selection == weapon ? 3 : 2);
            if (weapon == 1 || weapon == 2) cycles += 2 + 4 + 3;
            else if (weapon == 3) cycles += 2 + 4;
            if (weapon != 0) cycles += 2 + 4;
            cycles += 2 + 2 + 5 + 5 + 2 + 6 + (5 + 2 + 5 + 5 + 6)
                + 2 + 5 + 5 + 2 + 5 + 2 + 5 + 4 + 4 + 4 + 4 + 6;
            return cycles;
        }
    }
}
