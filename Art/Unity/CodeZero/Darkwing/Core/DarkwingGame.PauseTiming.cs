namespace Darkwing.Core
{
    public sealed partial class DarkwingGame
    {
        public int LastPausePrefixCycles { get; private set; } = -1;
        public int LastPauseCommitCycles { get; private set; } = -1;
        public string LastPauseTimingPath { get; private set; }
        public string PauseTimingUnsupported { get; private set; }
        private bool _pauseTimingEntering;

        private void ResetPauseTiming(string path)
        {
            LastPausePrefixCycles = LastPauseCommitCycles = -1;
            LastPauseTimingPath = path;
            PauseTimingUnsupported = Player.TimingDeferredBankAudio ? "FF55 deferred F6 pause path" : null;
        }

        private void BeginPauseEntryTiming()
        {
            ResetPauseTiming("enter");
            _pauseTimingEntering = true;
            if (PauseTimingUnsupported != null) return;
            if ((PressedP1 & 0x20) != 0 || Player.LevelActive < 0)
            {
                PauseTimingUnsupported = "C9B3 Select or inactive pause entry prefix";
                return;
            }
            LastPausePrefixCycles = PauseTiming.EntryPrefix(LastInputHookCycles, Player.State,
                Player.WeaponSelected, AudioQueueCanEnqueue, 0);
            if (LastPausePrefixCycles < 0) PauseTimingUnsupported = "CA1D guarded pause entry";
        }

        private void BeginPauseFrameTiming()
        {
            if (!_pauseTimingEntering) BeginPauseResumeTiming(false);
        }

        private void BeginPauseResumeTiming(bool leave)
        {
            ResetPauseTiming(leave ? "leave" : "stay");
            _pauseTimingEntering = false;
            if (PauseTimingUnsupported != null) return;
            int firstPlate = PauseTiming.Plate(Objects.Slots[5], false);
            int secondPlate = PauseTiming.Plate(Objects.Slots[4], true);
            LastPausePrefixCycles = PauseTiming.Resume(LastInputHookCycles, firstPlate, secondPlate, leave, 0);
            if (LastPausePrefixCycles < 0) PauseTimingUnsupported = "90E1 or 9148 pause plate state outside table";
        }

        private void FinishPauseFrameTiming()
        {
            _pauseTimingEntering = false;
            if (PauseTimingUnsupported != null || LastPausePrefixCycles < 0) return;
            int tail = PauseTiming.F89AToCommit(Oam.WalkCycles, Player.TimingDeferredBankAudio ? (byte)1 : (byte)0);
            if (tail < 0) PauseTimingUnsupported = "FF55 deferred F6 pause walk";
            else LastPauseCommitCycles = LastPausePrefixCycles + tail;
        }
    }

    public static class PauseTiming
    {
        public static int Plate(ObjectSystem.Slot plate, bool second)
        {
            int phase = plate.StateLow & 15;
            if (phase > 4) return -1;
            int dispatch = 4 + 2 + 2 + 4 + 3 + 4 + 3 + 5;
            if (phase == 4) return dispatch + 2 + 4 + 6;
            byte next = phase switch
            {
                0 => unchecked((byte)(plate.X + (second ? 4 : -4))),
                1 => unchecked((byte)(plate.Y + (second ? -4 : 4))),
                2 => unchecked((byte)(plate.X + (second ? -4 : 4))),
                _ => unchecked((byte)(plate.Y + (second ? 4 : -4)))
            };
            bool transition = phase switch
            {
                0 => second ? next >= 0xAC : next < 0x58,
                1 => second ? next < 0x30 : next >= 0xD8,
                2 => second ? next < 0x58 : next >= 0xAC,
                _ => second ? next >= 0xD8 : next < 0x30
            };
            return dispatch + 4 + 2 + 2 + 4 + 2 + (transition ? 2 + 6 : 3) + 6;
        }
    
        public static int WeaponName(byte weapon)
        {
            if (weapon > 3) return 3 + 4 * (2 + 2) + 6;
            int select = 3 + weapon * (2 + 2) + 2 + 3;
            return select + (weapon == 0 ? 2 + 7 * 4 : weapon == 3 ? 7 * (2 + 4) : 5 * (2 + 4) + 2 + 2 * 4) + 6;
        }
    
        public static int Enter(byte weapon)
        {
            int cycles = 3 + 3 + 3 + 4 + 3 + 4 + 6 + WeaponName(weapon) + 2;
            for (int slot = 5; slot >= 0; slot--)
            {
                cycles += 6 * (4 + 5) + 2 * (4 + 5) + 2;
                if (slot != 3) cycles += 3;
                else
                {
                    cycles += 2 + 3;
                    if (weapon == 0) cycles += 3;
                    else
                    {
                        cycles += 2;
                        if (weapon > 3) cycles += 3 * (2 + 2) + 3;
                        else cycles += (weapon - 1) * (2 + 2) + 2 + 3 + 2 + 3 + 5;
                    }
                }
                cycles += 2 + 3 * 5 + 2 * (4 + 5) + 2 + (slot == 0 ? 2 : 3);
            }
            cycles += 2 + 3;
            for (int index = 0; index < 10; index++) cycles += 4 + 5 + 2 + 5 + 2 + 2 + (index == 9 ? 2 : 3);
            cycles += 3 + 4 + 2;
            for (int index = 15; index >= 0; index--) cycles += 4 + 5 + 4 + 5 + 2 + (index == 0 ? 2 : 3);
            return cycles + 3 + 2 + 3 + 3 + 2 + 3;
        }
    
        public static int Restore()
        {
            int cycles = 2;
            for (int index = 5; index >= 0; index--) cycles += 6 * (4 + 5) + 2 + 5 + 2 + (index == 0 ? 2 : 3);
            cycles += 2;
            for (int index = 0; index < 10; index++) cycles += 4 + 5 + 2 + 2 + (index == 9 ? 2 : 4);
            cycles += 2;
            for (int index = 15; index >= 0; index--) cycles += 4 + 5 + 2 + (index == 0 ? 2 : 3);
            return cycles + 3 + 4 + 2 + 3 + 3 + 2 + 3 + 4 + 3 + 6;
        }
    
        public static int WipeFromFour()
        {
            int cycles = 3 + 2 + 2 + 2 + 4 + 2;
            for (int cursor = 4; cursor != 256; cursor += 4) cycles += 5 + 4 * 2 + (cursor == 252 ? 2 : 3);
            return cycles + 6;
        }
    
        public static int F89AToCommit(int bank5Cycles, byte deferredAudio)
        {
            if (deferredAudio != 0 || bank5Cycles < 0) return -1;
            return 3 + 3 + 5 + 6 + WipeFromFour() + 2 + 6 + 55 + 6 + bank5Cycles + 2 + 3 + 6 + 49;
        }
    
        public static int Resume(int inputHook, int firstPlate, int secondPlate, bool leave, byte deferredAudio)
        {
            if (deferredAudio != 0 || firstPlate < 0 || secondPlate < 0) return -1;
            int cycles = 6 + inputHook + 18 + 4 + 3 + 55 + 6 + firstPlate + 6 + secondPlate + 3 + 2;
            return leave ? cycles + 2 + 3 + Restore() : cycles + 3 + 2 + 3 + 6;
        }
    
        public static int EntryPrefix(int inputHook, byte state, byte weapon, bool audioAccepts, byte deferredAudio)
        {
            if (deferredAudio != 0 || state is 7 or 9 or 0x12 or 0x15) return -1;
            return 6 + inputHook + 18 + 5 + 3 + 3 + 3 + 3 + 2 + 4
                + 3 + 2 + 2 + 3 + 5 * (2 + 2) + 2 + 6 + (audioAccepts ? 42 : 27)
                + 2 + 6 + 55 + 6 + Enter(weapon) + 2 + 3 + 6;
        }
    }
    
    
}

