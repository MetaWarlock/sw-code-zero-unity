using System;

namespace Darkwing.Core
{
    public static class NmiTiming
    {
        public readonly struct Publication
        {
            public readonly byte BlankDepth, Bracket, Stream1C, Stream1D, Text19, Chr0, Chr1, Palette;
            public readonly Func<int, byte> ReadDescriptor;

            public Publication(byte blankDepth, byte bracket, byte stream1C, byte stream1D,
                byte text19, byte chr0, byte chr1, byte palette, Func<int, byte> readDescriptor)
            {
                BlankDepth = blankDepth; Bracket = bracket;
                Stream1C = stream1C; Stream1D = stream1D; Text19 = text19;
                Chr0 = chr0; Chr1 = chr1; Palette = palette; ReadDescriptor = readDescriptor;
            }
        }

        public static int PublicationCycles(in Publication work, long handlerCycle)
        {
            if ((work.BlankDepth | work.Bracket) != 0) return 27;
            int cycles = 50 + 4 + 513 + (int)((handlerCycle + 55) & 1);
            cycles += work.Stream1C == 0 ? 6 : 18 + DescriptorCycles(0x80, work.ReadDescriptor);
            cycles += work.Stream1D == 0 ? 6 : 18 + DescriptorCycles(0x58, work.ReadDescriptor);
            cycles += work.Text19 == 0 ? 6 : 16 + DescriptorCycles(0, work.ReadDescriptor);
            cycles += work.Chr0 == 0 ? 6 : 55;
            cycles += work.Chr1 == 0 ? 6 : 55;
            if (work.Palette == 0) cycles += 6;
            else
            {
                cycles += 3 + 2 + 2 + 3 + 4 + 2 + 4 + 4 + 2;
                cycles += 32 * (4 + 4 + 2 + 2) + 31 * 3 + 2;
                cycles += 2 + 4 + 4 + 4 + 4;
            }
            cycles += 4 + 3 + 4 + 3 + 4 + 3 + 4 + 3 + 2 + 3 + 4;
            return cycles;
        }

        public static int DescriptorCycles(byte cursor, Func<int, byte> read)
        {
            if (read == null) throw new ArgumentNullException(nameof(read));
            int cycles = 0;
            for (int command = 0; command < 256; command++)
            {
                cycles += 4 + PageCross(0x0360, cursor);
                if ((read(0x0360 + cursor) & 0x80) != 0) return cycles + 3 + 6;
                cycles += 2 + 4;
                cycles += 4 + PageCross(0x0361, cursor) + 4;
                cycles += 4 + PageCross(0x0362, cursor);
                byte remaining = read(0x0362 + cursor);
                do
                {
                    cycles += 4 + PageCross(0x0363, cursor) + 4 + 2 + 2;
                    cursor = unchecked((byte)(cursor + 1));
                    remaining = unchecked((byte)(remaining - 1));
                    cycles += remaining < 0x80 ? 3 : 2;
                } while (remaining < 0x80);
                cursor = unchecked((byte)(cursor + 3));
                cycles += 6;
                if (cursor == 0) return cycles + 2 + 6;
                cycles += 3;
            }
            throw new InvalidOperationException("VRAM descriptor chain does not terminate");
        }

        public static int ProcessTimerCycles(byte[] state, byte[] timer)
        {
            if (state == null || timer == null || state.Length != 4 || timer.Length != 4)
                throw new ArgumentException("Four scheduler slots are required");
            int cycles = 5 + 2 + 3 + 2 + 2;
            for (int slot = 0; slot < 4; slot++)
            {
                cycles += 4 + 2;
                if (state[slot] != FrameScheduler.Waiting) cycles += 3;
                else cycles += 2 + 6 + (timer[slot] == 1 ? 2 + 2 + 4 : 3);
                cycles += 4 * 2 + 2 + (slot == 3 ? 2 : 3);
            }
            return cycles;
        }

        public static int BankGateCycles(byte switchDepth, byte deferred, out bool audioRuns)
        {
            audioRuns = switchDepth == 0 || deferred == 0xFF;
            if (switchDepth == 0) return 36;
            return audioRuns ? 42 : 14; // $C0C5 BNE $C10E crosses C0/C1 page
        }

        public static int EmptyAudioQueueCycles => 12;
        public static int QueuedTaskOverheadCycles => 45;
        public static int TickCallCycles => 9;
        public static int RestoreBankAndReturnCycles => 57;
        public static int ReturnWithoutBankRestoreCycles => 26;

        private static int PageCross(int address, int index) => (address & 255) + index > 255 ? 1 : 0;
    }
}
