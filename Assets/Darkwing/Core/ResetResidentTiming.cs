using System;

namespace Darkwing.Core
{
    public static class ResetResidentTiming
    {
        public readonly struct BodyWork
        {
            public readonly int Cycles, CopyrightSetupCycles, BodyPcOffset, RenderOnAtCycles;
            public readonly bool Commits;
            public readonly ushort BodyPc, YieldStackPointer, StopPc;

            public BodyWork(int cycles, bool commits, int copyrightSetupCycles, ushort bodyPc, int bodyPcOffset, ushort yieldStackPointer, ushort stopPc = 0xC5F7, int renderOnAtCycles = -1)
            {
                Cycles = cycles; Commits = commits; CopyrightSetupCycles = copyrightSetupCycles;
                BodyPc = bodyPc; BodyPcOffset = bodyPcOffset; YieldStackPointer = yieldStackPointer;
                StopPc = stopPc;
                RenderOnAtCycles=renderOnAtCycles;
            }
        }

        private static int Branch(int pc, int target, bool taken) => !taken ? 2 : 3 + (((pc + 2) & 0xFF00) != (target & 0xFF00) ? 1 : 0);

        // $DDCB/$DE03 JSR $FF06 yields push their frame back to $B9.
        private const ushort LoaderYieldStack = 0xB9;

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
            throw new NotSupportedException("Reset resident has no priced body for stage " + stage);
        }

        /// <summary>$DD5D-$DE86: descriptors, palettes, CHR, render-off and the $C235 clear up to
        /// the $E698 stream loop head. The loop is inline in $DE24 and has no $FF06 of its own, so
        /// the honest endpoint is a stop at $DE86 rather than a fabricated Waiting.</summary>
        public static BodyWork MeasureStaging(RoomDecoder.World world, bool audioRingNonZero, bool audioSlotFree)
        {
            if (world == null) throw new ArgumentNullException(nameof(world));
            int cycles = 94;                                                   // $DD5D-$DDA4
            cycles += audioRingNonZero ? 20 + SoundEnqueue(audioSlotFree) : 8; // $DDA6-$DDAB/$F84E
            cycles += 27;                                                      // $DDAE-$DDBD
            cycles += BackgroundPalette(world.BgPalIdx);                       // $C2F9
            cycles += 13;                                                      // $DDC0-$DDC5
            cycles += SpritePalette(world.SpPalIdx, world.Id);                 // $C319
            cycles += 6 + 6 + 20 + 6 + ClearSlots();                           // $DDC8/$DE24/$C14E/$DE27/$C235
            cycles += 121;                                                     // $DE2A-$DE84 with $23 == 0
            if (world.StartCell != 0) cycles += 12;                            // $DE7C falls into $DE7E-$DE84
            return new BodyWork(cycles, false, -1, 0xDD5D, 0, 0xBB, 0xDE86);
        }

        /// <summary>$DDCB JSR $FF06 after the staging loop exits: the same 55-cycle yield
        /// shape as the $DD5A stage. The $DD5A yield resumed at $BB, and this JSR pushes
        /// its frame back to $B9 (the $DE03 yield after $DDCE shows the same owner).</summary>
        public static BodyWork MeasureLoaderYield()
        {
            return new BodyWork(6 + 49, true, -1, 0xDDCB, 0, LoaderYieldStack);
        }

        public static BodyWork MeasureLoaderSpawn(byte world)
        {
            if(world!=7)throw new NotSupportedException("Clocked first-loader spawn requires world7");
            // DDCE..DDDE, FA78, DDE1..DDEC, DE09..DE13; then C158 and the DE03 yield.
            int head=2+3+3+2+4+2+5+6+20+2+5+3+2+2+3+3+2+2+2+2+3;
            return new BodyWork(head+6+20+6+49,true,-1,0xDDCE,0,LoaderYieldStack,renderOnAtCycles:head+6+14);
        }

        public static BodyWork MeasureLoaderFadeStart(byte[] palette,int walkCycles)
        {
            if(walkCycles<0)throw new NotSupportedException("Clocked first-loader OAM walk");
            int prefix=3+2+6+55+6+walkCycles+3+23; // DE06, C254/FF55/8000/C25C/C270
            return new BodyWork(prefix+Fade(palette,0x30)+3+3+6+49,true,-1,0xDE06,0,0xB8);
        }

        public static BodyWork MeasureLoaderFadeResume(byte frame,byte level,byte[] palette)
        {
            bool ramp=(frame&3)==0;
            int cycles=4+2+2+Branch(0xC2AE,0xC2A6,!ramp);
            if(!ramp)return new BodyWork(cycles+3+6+49,true,-1,0xC2AA,0,0xB8);
            level=(byte)(level-0x10);
            cycles+=3+2+3+3+2+2+3; // C2B0..C2BB, BEQ50 not taken
            bool again=(level&0x80)==0;
            cycles+=Branch(0xC2BD,0xC285,again);
            if(!again)return new BodyWork(cycles+6,false,-1,0xC2AA,0,0xBD,0xC601);
            return new BodyWork(cycles+Fade(palette,level)+3+3+6+49,true,-1,0xC2AA,0,0xB8);
        }

        /// <summary>$DE93 LDA $31 / CMP #$7 / BCS $DEA5, then $DEA5 LDA $66 / BMI $DE99
        /// (not taken) / RTS, followed by the $DDCB JSR $FF06. Mission worlds ($31 &lt; 7)
        /// branch into the unpriced $DE99 placement anchor instead.</summary>
        public static BodyWork MeasureLoaderCompletion(byte worldId)
        {
            if (worldId < 7)
                throw new NotSupportedException("$DE99 placement anchor after the loader is unpriced");
            const int exit = 3 + 2 + 3 + 3 + 2 + 6;   // $DE93/$DE95/$DE97/$DEA5/$DEA7/$DEA9
            return new BodyWork(exit + 6 + 49, true, -1, 0xDE93, 0, LoaderYieldStack);
        }

        /// <summary>$F84E at a ring slot: 45 cycles when the slot is empty, 30 when the
        /// enqueue is dropped. $D9 is still overwritten on both paths.</summary>
        private static int SoundEnqueue(bool accepted)
        {
            int cycles = 3 + 3 + 3 + 3 + 4 + 2;          // STA $D9 .. CMP #$88
            if (!accepted) return cycles + 3 + 3 + 6;    // BNE $F866/LDX $00/RTS
            cycles += 2;                                 // BNE not taken
            cycles += 3 + 4 + 2 + 2 + 2 + 3;             // LDA $01 .. STA $DA
            return cycles + 3 + 6;                       // LDX $00/RTS
        }

        /// <summary>$C2F9: $C35D + (index&lt;&lt;4) copied to both $0300 planes.</summary>
        private static int BackgroundPalette(byte index)
        {
            int y = (index << 4) & 0xFF, cycles = 24;
            for (int offset = 0; offset < 16; offset++)
                if ((0x5D + ((y + offset) & 0xFF)) > 0xFF) cycles++;
            return cycles + 21 * 16 + 47 + 6;
        }

        /// <summary>$C319: the fixed $C4FD bytes plus eight bytes at $C505 + (index&lt;&lt;3),
        /// then the $C33C world override.</summary>
        private static int SpritePalette(byte index, byte world)
        {
            int y = (index << 3) & 0xFF, cycles = 10 + 167 + 5 + 183;
            for (int offset = 0; offset < 8; offset++)
                if ((0x05 + ((y + offset) & 0xFF)) > 0xFF) cycles++;
            if (world == 0x08) return cycles + 24;
            if (world == 0x09) return cycles + 28;
            if (world == 0x0B) return cycles + 32;
            return cycles + 21;
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
