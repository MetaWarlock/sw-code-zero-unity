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
            // C2BF returns to C601: LDX #1E, JSR FEFF and its first FF06.
            if(!again)return new BodyWork(cycles+6+2+6+6+49,true,-1,0xC2AA,0,0xB9);
            return new BodyWork(cycles+Fade(palette,level)+3+3+6+49,true,-1,0xC2AA,0,0xB8);
        }

        public static BodyWork MeasureIntroHold(int remaining, int stream = 0x0E)
        {
            if(remaining<1||remaining>30)throw new ArgumentOutOfRangeException(nameof(remaining));
            IntroGate(stream);
            // FF02 DEX / FF03 BNE FEFF crosses the FF/FE page when taken.
            if(remaining>1)return new BodyWork(2+Branch(0xFF03,0xFEFF,true)+6+49,true,-1,0xFF02,0,0xB9);
            // The first dialog also switches PRG to2; the second already owns that bank.
            int entry = stream == 0x0E ? 2+3+2+6+55+6 : 2+3+6;
            return new BodyWork(2+Branch(0xFF03,0xFEFF,false)+6+entry+6+TextClear(stream)+49,
                true,-1,0xFF02,0,0xB9);
        }

        private static int IntroGate(int stream)
        {
            if(stream!=0x0E&&stream!=0x0F)throw new NotSupportedException("Clocked text requires an auto-advance intro stream");
            return stream==0x0E ? 3+2+3 : 3+2+2+2+3;
        }

        // Bank2 AADD..AB54: marker, descriptor headers, 29 iterations filling
        // all three lines, terminator and JMP FF06. Both filler gates are executed.
        private static int TextClear(int stream) => 18+IntroGate(stream)+2+54+IntroGate(stream)+2+29*22-1+12;

        /// <summary>AA03 or F8B1 through the next text F89A/FF06. The caller adds
        /// the actual native OAM walk after executing this body; there is no fixture price.</summary>
        public static BodyWork MeasureIntroText(TextEngine text, byte[] bank2, byte frame, bool audioSlotFree)
        {
            if(text==null||!text.Running||bank2==null||bank2.Length!=0x4000)
                throw new ArgumentException("Running native text and bank2 required");
            int stream=text.StreamId, gate=IntroGate(stream);
            bool first=text.Yielding;
            int cycles=first ? 22+10+gate : 62+((frame&3)!=0 ? 30 : 37+SoundEnqueue(audioSlotFree)-3);
            ushort pc=(ushort)(first ? 0xAA03 : 0xF8B1);
            int pointer=bank2[0x2BAA+stream]|bank2[0x2BBD+stream]<<8;
            int index=text.ByteIndex,line=text.Line;
            for(int step=0;step<3;step++)
            {
                if((uint)(pointer+index-0x8000)>=(uint)bank2.Length)
                    throw new NotSupportedException("Text stream byte outside bank2");
                int page=((pointer&255)+index)>255 ? 1 : 0;
                byte value=bank2[pointer+index-0x8000];index++;
                if(value==0xFF)
                {
                    // AAA4..AAD0 seeds the intro-flight object for stream0F.
                    cycles+=48+page+(stream==0x0F ? 94 : 8)+3+11;
                    return new BodyWork(cycles+TextWalkWithoutOam,true,-1,pc,0,0xB6);
                }
                if(value==0xFE)
                {
                    cycles+=52+page+5+3+2;
                    line++;
                    if(line>=3)throw new NotSupportedException("Intro text page-flip continuation is not priced");
                    cycles+=Branch(0xAA83,0xAA17,true)+5+gate;
                    continue;
                }
                cycles+=87+page;
                return new BodyWork(cycles+TextWalkWithoutOam,true,-1,pc,0,0xB4);
            }
            throw new NotSupportedException("Intro text lacks a next glyph/terminator");
        }

        // F89A: save bank, bracket, C21E wipe(cursor4), switch bank5, JSR8000;
        // F8AA..FF19 clears the bracket and commits the one-frame yield.
        private static int TextWalkWithoutOam => 3+3+5+6+Wipe(4)+2+6+55+6+2+3+6+49;

        public static BodyWork IncludeOam(in BodyWork work, int walkCycles)
        {
            if(walkCycles<0)throw new NotSupportedException("Intro text OAM walk is unpriced");
            return new BodyWork(work.Cycles+walkCycles,work.Commits,work.CopyrightSetupCycles,
                work.BodyPc,work.BodyPcOffset,work.YieldStackPointer,work.StopPc,work.RenderOnAtCycles);
        }

        public static BodyWork MeasureIntroTextReturn(int stream)
        {
            int gate=IntroGate(stream);
            // F8B1 bank return, AB5E JSR AB72, AB61 stream gate and RTS;
            // then C612's first FEFF yield or C61E's plain FF06 yield.
            int cycles=62+6+gate+6+gate+6+(stream==0x0E ? 2+6+6+49 : 6+49);
            return new BodyWork(cycles,true,-1,0xF8B1,0,(ushort)(stream==0x0E ? 0xB9 : 0xBB));
        }

        /// <summary>C621: F5A4 radix-240 subtraction, F5DA acceleration/clamp,
        /// bank5 OAM and the height test. The caller adds this first OAM's price,
        /// even when the legacy presentation starts a second walk in the same pass.</summary>
        public static BodyWork MeasureIntroFlight(ObjectSystem.Slot player, byte gravity)
        {
            if(player==null)throw new ArgumentNullException(nameof(player));
            int fraction=player.YFrac-player.YVelFrac;
            int low=((player.Y&255)-(player.YVelInt&255)-(fraction<0 ? 1 : 0))&255;
            int integrate=39;
            if(low>=240) { low-=240;integrate=56; }
            byte velocityHigh=(byte)(((sbyte)player.YVelInt*256+player.YVelFrac-gravity)>>8);
            int acceleration=velocityHigh<0x80 ? 34 : velocityHigh>=0xF9 ? 38 : 51;
            int cycles=2+6+integrate+6+acceleration+2+6+55+2+3+6;
            bool flying=low<0x5C;
            cycles+=4+2+Branch(0xC63A,0xC61E,flying);
            if(flying)return new BodyWork(cycles+6+49,true,-1,0xC621,0,0xBB);
            // Terminal flight falls through C63C and immediately enters the
            // world-7 intro card.  The fixed C63C..8000 prefix plus F8AA..FF19
            // tail is 1682 + 11 + 49; both OAM walks are added from the live
            // dispatcher after the body has executed.
            const int cardFixed = 1682 + 11 + 49;
            return new BodyWork(cycles+cardFixed,true,-1,0xC621,0,0xB6,0xC63C);
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
