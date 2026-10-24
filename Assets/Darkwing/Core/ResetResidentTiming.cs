using System;

namespace Darkwing.Core
{
    public static class ResetResidentTiming
    {
        public readonly struct BodyWork
        {
            public readonly int Cycles, CopyrightSetupCycles, BodyPcOffset;
            public readonly bool Commits;
            public readonly ushort BodyPc, YieldStackPointer;

            public BodyWork(int cycles, bool commits, int copyrightSetupCycles, ushort bodyPc, int bodyPcOffset, ushort yieldStackPointer)
            {
                Cycles = cycles; Commits = commits; CopyrightSetupCycles = copyrightSetupCycles;
                BodyPc = bodyPc; BodyPcOffset = bodyPcOffset; YieldStackPointer = yieldStackPointer;
            }
        }

        private static int Branch(int pc, int target, bool taken) => !taken ? 2 : 3 + (((pc + 2) & 0xFF00) != (target & 0xFF00) ? 1 : 0);

        public static BodyWork Measure(int stage, byte fadeFrame, byte fadeLevel, byte timer, byte[] palette0320, byte[] bank2, byte oamCursor)
        {
            const ushort masterStack = 0xBF;
            const ushort plainYieldStack = 0xBB;
            const ushort fadeYieldStack = 0xB9;
            const ushort loaderFadeYieldStack = 0xB4;
            if (stage == 2)
                return new BodyWork(2 + 3 + 2 + 3 + 6 + 20 + Fade(palette0320, 0x10) + 3 + 3 + 6 + 49, true, -1, 0xC5A5, 0, fadeYieldStack);
            if (stage == 3)
            {
                bool ramp = (fadeFrame & 3) == 0;
                int cycles = 4 + 2 + 2 + Branch(0xC2AE, 0xC2A6, !ramp);
                if (!ramp) return new BodyWork(cycles + 3 + 6 + 49, true, -1, 0xC2AA, 0, fadeYieldStack);
                byte level = (byte)(fadeLevel + 0x10);
                cycles += 3 + 2 + 3 + 3 + 2 + Branch(0xC2B9, 0xC2BF, level == 0x50);
                if (level == 0x50) return new BodyWork(cycles + 6 + 6 + 49, true, -1, 0xC2AA, 0, plainYieldStack);
                return new BodyWork(cycles + 3 + Branch(0xC2BD, 0xC285, true) + Fade(palette0320, level) + 3 + 3 + 6 + 49, true, -1, 0xC2AA, 0, fadeYieldStack);
            }
            if (stage == 4)
            {
                int setup = Copyright(bank2);
                return new BodyWork(setup + 3 + Branch(0xC5EA, 0xC5EF, true) + 5 + 6 + 49, true, setup, 0xC5B3, 0, plainYieldStack);
            }
            if (stage == 5)
            {
                int cycles = 3 + 3 + Branch(0xC5EA, 0xC5EF, timer != 0);
                if (timer != 0)
                    return new BodyWork(cycles + 5 + 6 + 49, true, -1, 0xC5E8, 3, plainYieldStack);
                // $C5EC JMP $C5F7 keeps the same pass in the loader:
                // $C95C, $31=7, $DD57 -> $C248 -> $C276 and the first ramp, then the $FF06 yield.
                return new BodyWork(cycles + 3 + FirstLoader(palette0320), true, -1, 0xC5F7, 0, loaderFadeYieldStack);
            }
            if (stage == 6)
            {
                bool ramp = (fadeFrame & 3) == 0;
                int cycles = 4 + 2 + 2 + Branch(0xC2AE, 0xC2A6, !ramp);
                if (!ramp) return new BodyWork(cycles + 3 + 6 + 49, true, -1, 0xC2AA, 0, loaderFadeYieldStack);
                byte level = (byte)(fadeLevel + 0x10);
                cycles += 3 + 2 + 3 + 3 + 2 + Branch(0xC2B9, 0xC2BF, level == 0x50);
                if (level == 0x50)
                    return new BodyWork(cycles + 6 + 6 + ClearSlots() + 6 + Wipe(oamCursor) + 3 + 49,
                        true, -1, 0xC2AA, 0, fadeYieldStack);
                return new BodyWork(cycles + 3 + Branch(0xC2BD, 0xC285, true) + Fade(palette0320, level) + 3 + 3 + 6 + 49,
                    true, -1, 0xC2AA, 0, loaderFadeYieldStack);
            }
            if (stage == 7)
                return new BodyWork(6 + 49, true, -1, 0xDD5A, 0, fadeYieldStack);
            throw new NotSupportedException("Reset resident reached the unpriced $DD5D continuation");
        }

        /// <summary>$C5F7 JSR $C95C, LDA #7/STA $31, JSR $DD57 -> $C248 -> $C276 and the
        /// first $C285 ramp, ending on the $C2A4/$C2A7 yield.</summary>
        private static int FirstLoader(byte[] palette)
        {
            int cycles = 6 + ClearScroll();
            cycles += 2 + 3 + 6;
            cycles += 6 + 6 + 20;
            cycles += Fade(palette, 0x10);
            return cycles + 3 + 3 + 6 + 49;
        }

        /// <summary>$C235: slots 15..1 only, DEX/BNE exits at X=0.</summary>
        private static int ClearSlots()
        {
            int cycles = 2;
            for (int x = 15; x >= 1; x--)
            {
                cycles += 2 + 5 + 5;
                cycles += 2 + 5;
                cycles += 2 + Branch(0xC245, 0xC237, x - 1 != 0);
            }
            return cycles + 6;
        }

        /// <summary>$C95C: $C235 then slot 0's $0400/$04D0 and the four scroll mirrors.</summary>
        private static int ClearScroll()
        {
            return 6 + ClearSlots() + 2 + 4 + 4 + 4 * 3 + 6;
        }

        /// <summary>$C21E at the live $97 cursor: the $C224 special case plus $0200,X..$02FF.</summary>
        private static int Wipe(int cursor)
        {
            if ((cursor & 3) != 0) throw new NotSupportedException("$C21E requires an aligned $97 cursor");
            int cycles = 3 + 2 + Branch(0xC222, 0xC229, cursor != 4);
            if (cursor == 4) cycles += 2 + 4;
            cycles += 2;
            while (true)
            {
                cursor = (cursor + 4) & 0xFF;
                cycles += 5 + 4 * 2 + Branch(0xC232, 0xC22B, cursor != 0);
                if (cursor == 0) return cycles + 6;
            }
        }

        private static int Fade(byte[] palette, byte level)
        {
            if (palette == null || palette.Length != 32) throw new ArgumentException("Owned0320 palette required");
            int cycles = 2;
            for (int index = 31; index >= 0; index--) cycles += 4 + 5 + 2 + Branch(0xC28E, 0xC287, index != 0);
            cycles += 2;
            for (int index = 31; index >= 0; index--)
            {
                bool positive = (byte)(palette[index] - level) < 128;
                cycles += 4 + 2 + 3 + Branch(0xC298, 0xC29C, positive) + (positive ? 0 : 2);
                cycles += 5 + 2 + Branch(0xC2A0, 0xC292, index != 0);
            }
            return cycles + 3;
        }

        private static int Copyright(byte[] bank2)
        {
            int wipe = 3 + 2 + 2 + 2 + 4 + 2;
            for (int cursor = 4; cursor <= 252; cursor += 4) wipe += 5 + 4 * 2 + Branch(0xC232, 0xC22B, cursor != 252);
            wipe += 6;
            int fill = 3 + 3 + 3 + 4 + 3 + 2 + 4 + 3 + 4 + 2 + 4 + 2 + 2 + 3 + 2 + 3;
            fill += 4 * (256 * (4 + 2 + 3) - 1) + 4 * (2 + 3) - 1;
            fill += 3 + 3 + 2 + 2 + 2 + 4 + 2 + 4 + 2 + 64 * (4 + 2 + 4) - 2 + 3 + 6;
            int render = 5 + 2 + 3 + 4 + 6;
            int clear = 2 + 3 + 2 + 3 + 2 + 2 + 2 + 2 * (256 * (6 + 2 + 3) - 1) + 5 + 2 + 3 + 5 + 2 + 2 + 6;
            int bank = 3 + 3 + 5 + 5 * 4 + 4 * 2 + 2 + 3 + 3 + 2 + 6;
            int cycles = 6 + render + 2 + 3 + 6 + wipe + 6 + clear;
            cycles += 2 + 5 * 3 + 2 + 2 + 2 + 6 + fill + 6 + wipe + 2 + 6 + bank + 2 + 6 + CopyrightTask(bank2);
            int palette = 2 + 16 * (4 + 5 + 5 + 2 + 3) - 1 + 2 + 3 + 3 + 6;
            return cycles + 6 + palette + 6 + render;
        }

        private static int CopyrightTask(byte[] bank2)
        {
            if (bank2 == null || bank2.Length != 0x4000) throw new ArgumentException("Bank2 required");
            int pointer = bank2[0x3D] | bank2[0x43] << 8, index = 0;
            int cycles = 4 + 3 + 4 + 3 + 2 + 4;
            byte Read()
            {
                if (pointer + index < 0x8000 || pointer + index >= 0xC000) throw new NotSupportedException("Copyright task outside bank2");
                cycles += 5 + ((pointer & 255) + index > 255 ? 1 : 0);
                return bank2[pointer + index - 0x8000];
            }
            void Advance()
            {
                index = (index + 1) & 255;
                cycles += 2 + Branch(0x8038, 0x803C, index != 0);
                if (index == 0) { pointer += 256; cycles += 5; }
                cycles += 6;
            }
            for (int command = 0; command < 256; command++)
            {
                byte value = Read();
                cycles += 2 + Branch(0x8013, 0x8036, value == 255);
                if (value == 255) return cycles + 6;
                cycles += 4 + 6; Advance();
                Read(); cycles += 4 + 6; Advance();
                byte count = Read();
                if (count == 0) throw new NotSupportedException("Copyright task zero count");
                cycles += 2 + 6; Advance();
                for (int item = 0; item < count; item++)
                {
                    Read(); cycles += 4 + 6; Advance();
                    cycles += 2 + Branch(0x8032, 0x8029, item + 1 != count);
                }
                cycles += Branch(0x8034, 0x800F, true);
            }
            throw new NotSupportedException("Copyright task terminator absent");
        }
    }
}
