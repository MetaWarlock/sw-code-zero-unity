using System;

namespace Darkwing.Core
{
    public sealed partial class DarkwingGame
    {
        public int LastMissionHeadCycles { get; private set; } = -1;
        public string MissionHeadTimingUnsupported { get; private set; }
        public int LastMissionResumePrefixCycles => LastMissionHeadCycles < 0 ? -1 : 6 + LastInputHookCycles + 18 + LastMissionHeadCycles;
        public int LastCardResumeCycles => Player.LastCardContinuationCycles < 0 ? -1 : 6 + LastInputHookCycles + 18 + Player.LastCardContinuationCycles;

        private void BeginMissionHeadTiming()
        {
            LastMissionHeadCycles = -1;
            MissionHeadTimingUnsupported = null;
            if (Player.IntroCardPhase || Player.CardWaitRemaining != 0)
            {
                MissionHeadTimingUnsupported = "FF02 resident Card continuation does not enter CAE2";
                return;
            }
            if (Player.LevelActive < 0)
            {
                MissionHeadTimingUnsupported = "C9B0 inactive mission head";
                return;
            }
            if (_pauseActive || (PressedP1 & 0x30) != 0)
            {
                MissionHeadTimingUnsupported = "C9B7 Select or CA1B pause path";
                return;
            }
            LastMissionHeadCycles = 5 + 3 + 3 + HeadBranch(0xC9AE, 0xC9B3, true)
                + 3 + 2 + HeadBranch(0xC9B7, 0xCA17, true)
                + 3 + 2 + HeadBranch(0xCA1B, 0xCA40, true);
        }

        private static int HeadBranch(int address, int target, bool taken)
        {
            return taken ? 3 + (((address + 2) & 0xFF00) != (target & 0xFF00) ? 1 : 0) : 2;
        }
    }
}
