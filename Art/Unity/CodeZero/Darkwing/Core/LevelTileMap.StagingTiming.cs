using System;
using System.Collections.Generic;

namespace Darkwing.Core
{
    public sealed partial class LevelTileMap
    {
        // Fixed bank7 instruction boundaries of the native loader only. These tables
        // describe execution cost; they neither decode nor execute ROM instructions.
        public readonly struct StagingTimingStep
        {
            public readonly ushort Pc;
            public readonly int Cycles;
            public StagingTimingStep(ushort pc, int cycles) { Pc = pc; Cycles = cycles; }
        }
        private static readonly StagingTimingStep[] StagingBandHead = {
            new StagingTimingStep(0xE698,2), new StagingTimingStep(0xE69A,3), new StagingTimingStep(0xE69C,3),
            new StagingTimingStep(0xE69E,3), new StagingTimingStep(0xE69F,2), new StagingTimingStep(0xE6A1,2),
            new StagingTimingStep(0xE6A2,2), new StagingTimingStep(0xE6A3,2), new StagingTimingStep(0xE6A4,2),
            new StagingTimingStep(0xE6A5,3), new StagingTimingStep(0xE6A7,4), new StagingTimingStep(0xE6A8,2),
            new StagingTimingStep(0xE6AA,2), new StagingTimingStep(0xE6AB,2), new StagingTimingStep(0xE6AC,3),
            new StagingTimingStep(0xE6AD,2), new StagingTimingStep(0xE6AE,2), new StagingTimingStep(0xE6AF,3),
            new StagingTimingStep(0xE6B1,3), new StagingTimingStep(0xE6B3,2), new StagingTimingStep(0xE6B5,5),
            new StagingTimingStep(0xE6B8,4), new StagingTimingStep(0xE6B9,4), new StagingTimingStep(0xE6BC,5),
            new StagingTimingStep(0xE6BF,2), new StagingTimingStep(0xE6C0,2), new StagingTimingStep(0xE6C2,5),
            new StagingTimingStep(0xE6C5,2), new StagingTimingStep(0xE6C7,5), new StagingTimingStep(0xE6CA,2),
            new StagingTimingStep(0xE6CC,5), new StagingTimingStep(0xE6CF,3), new StagingTimingStep(0xE6D1,2),
            new StagingTimingStep(0xE6D2,2), new StagingTimingStep(0xE6D3,2), new StagingTimingStep(0xE6D4,2),
            new StagingTimingStep(0xE6D5,2), new StagingTimingStep(0xE6D7,4), new StagingTimingStep(0xE6DA,5),
            new StagingTimingStep(0xE6DD,5), new StagingTimingStep(0xE6E0,5), new StagingTimingStep(0xE6E3,5),
            new StagingTimingStep(0xE6E6,2), new StagingTimingStep(0xE6E8,5), new StagingTimingStep(0xE6EB,2),
            new StagingTimingStep(0xE6ED,5), new StagingTimingStep(0xE6F0,5), new StagingTimingStep(0xE6F3,5),
            new StagingTimingStep(0xE6F6,5), new StagingTimingStep(0xE6F9,2), new StagingTimingStep(0xE6FB,5),
            new StagingTimingStep(0xE6FE,2), new StagingTimingStep(0xE6FF,2), new StagingTimingStep(0xE700,2),
            new StagingTimingStep(0xE701,3), new StagingTimingStep(0xE703,3)
        };
        private static readonly StagingTimingStep[] StagingRoomPointer = {
            new StagingTimingStep(0xEB27,2), new StagingTimingStep(0xEB29,3), new StagingTimingStep(0xEB2B,5),
            new StagingTimingStep(0xEB2D,2), new StagingTimingStep(0xEB2E,5), new StagingTimingStep(0xEB30,2),
            new StagingTimingStep(0xEB31,5), new StagingTimingStep(0xEB33,2), new StagingTimingStep(0xEB34,5),
            new StagingTimingStep(0xEB36,2), new StagingTimingStep(0xEB37,5), new StagingTimingStep(0xEB39,2),
            new StagingTimingStep(0xEB3A,5), new StagingTimingStep(0xEB3C,2), new StagingTimingStep(0xEB3D,5),
            new StagingTimingStep(0xEB3F,3), new StagingTimingStep(0xEB41,3), new StagingTimingStep(0xEB43,3),
            new StagingTimingStep(0xEB45,3), new StagingTimingStep(0xEB47,2), new StagingTimingStep(0xEB49,3),
            new StagingTimingStep(0xEB4B,3), new StagingTimingStep(0xEB4D,2), new StagingTimingStep(0xEB4F,3),
            new StagingTimingStep(0xEB51,6)
        };
        private static readonly StagingTimingStep[] StagingMacroBankHead = {
            new StagingTimingStep(0xEAD7,2), new StagingTimingStep(0xEAD9,3), new StagingTimingStep(0xEADB,2),
            new StagingTimingStep(0xEADD,2)
        };
        private static readonly StagingTimingStep[] StagingMacroPointer = {
            new StagingTimingStep(0xEAE4,2), new StagingTimingStep(0xEAE6,3), new StagingTimingStep(0xEAE8,3),
            new StagingTimingStep(0xEAEA,2), new StagingTimingStep(0xEAEC,2), new StagingTimingStep(0xEAED,3),
            new StagingTimingStep(0xEAEF,5), new StagingTimingStep(0xEAF1,2), new StagingTimingStep(0xEAF2,5),
            new StagingTimingStep(0xEAF4,2), new StagingTimingStep(0xEAF5,5), new StagingTimingStep(0xEAF7,2),
            new StagingTimingStep(0xEAF8,2), new StagingTimingStep(0xEAFA,3), new StagingTimingStep(0xEAFC,3),
            new StagingTimingStep(0xEAFE,2), new StagingTimingStep(0xEB00,3), new StagingTimingStep(0xEB02,6)
        };
        private static readonly StagingTimingStep[] StagingSwitchHead = {
            new StagingTimingStep(0xFF55,3), new StagingTimingStep(0xFF57,3), new StagingTimingStep(0xFF59,5),
            new StagingTimingStep(0xFF5B,4), new StagingTimingStep(0xFF5E,2), new StagingTimingStep(0xFF5F,4),
            new StagingTimingStep(0xFF62,2), new StagingTimingStep(0xFF63,4), new StagingTimingStep(0xFF66,2),
            new StagingTimingStep(0xFF67,4), new StagingTimingStep(0xFF6A,2), new StagingTimingStep(0xFF6B,4),
            new StagingTimingStep(0xFF6E,2), new StagingTimingStep(0xFF70,3), new StagingTimingStep(0xFF72,3)
        };
        private static readonly StagingTimingStep[] StagingSwitchDeferredHead = {
            new StagingTimingStep(0xFF77,2), new StagingTimingStep(0xFF79,4), new StagingTimingStep(0xFF7C,2),
            new StagingTimingStep(0xFF7D,4), new StagingTimingStep(0xFF80,2), new StagingTimingStep(0xFF81,4),
            new StagingTimingStep(0xFF84,2), new StagingTimingStep(0xFF85,4), new StagingTimingStep(0xFF88,2),
            new StagingTimingStep(0xFF89,4)
        };
        private static readonly StagingTimingStep[] StagingMacroSetup = {
            new StagingTimingStep(0xEA4E,3), new StagingTimingStep(0xEA50,6)
        };
        private static readonly StagingTimingStep[] StagingMacroInit = {
            new StagingTimingStep(0xEA53,2), new StagingTimingStep(0xEA55,3), new StagingTimingStep(0xEA57,2),
            new StagingTimingStep(0xEA59,3), new StagingTimingStep(0xEA5B,3), new StagingTimingStep(0xEA5D,3)
        };
        private static readonly StagingTimingStep[] StagingQuadrantHead = {
            new StagingTimingStep(0xEA5F,3), new StagingTimingStep(0xEA61,4), new StagingTimingStep(0xEA64,3),
            new StagingTimingStep(0xEA66,5), new StagingTimingStep(0xEA68,2), new StagingTimingStep(0xEA69,4),
            new StagingTimingStep(0xEA6C,3), new StagingTimingStep(0xEA6E,3), new StagingTimingStep(0xEA6F,2),
            new StagingTimingStep(0xEA70,3), new StagingTimingStep(0xEA71,4), new StagingTimingStep(0xEA74,2),
            new StagingTimingStep(0xEA75,2)
        };
        private static readonly StagingTimingStep[] StagingQuadrantHigh = {
            new StagingTimingStep(0xEA79,3), new StagingTimingStep(0xEA7B,2), new StagingTimingStep(0xEA7D,3)
        };
        private static readonly StagingTimingStep[] StagingQuadrantLow = {
            new StagingTimingStep(0xEA80,3), new StagingTimingStep(0xEA82,2), new StagingTimingStep(0xEA83,2),
            new StagingTimingStep(0xEA84,2), new StagingTimingStep(0xEA85,2)
        };
        private static readonly StagingTimingStep[] StagingQuadrantMerge = {
            new StagingTimingStep(0xEA86,3), new StagingTimingStep(0xEA88,4), new StagingTimingStep(0xEA8A,4),
            new StagingTimingStep(0xEA8D,3), new StagingTimingStep(0xEA8F,4), new StagingTimingStep(0xEA91,4),
            new StagingTimingStep(0xEA92,2), new StagingTimingStep(0xEA93,4), new StagingTimingStep(0xEA94,2),
            new StagingTimingStep(0xEA96,2)
        };
        private static readonly StagingTimingStep[] StagingQuadrantTiles = {
            new StagingTimingStep(0xEA9F,3), new StagingTimingStep(0xEAA1,3), new StagingTimingStep(0xEAA3,3),
            new StagingTimingStep(0xEAA5,4), new StagingTimingStep(0xEAA8,5), new StagingTimingStep(0xEAAB,4),
            new StagingTimingStep(0xEAAE,5), new StagingTimingStep(0xEAB1,4), new StagingTimingStep(0xEAB4,5),
            new StagingTimingStep(0xEAB7,4), new StagingTimingStep(0xEABA,5), new StagingTimingStep(0xEABD,5)
        };
        private static readonly StagingTimingStep[] StagingMacroTail = {
            new StagingTimingStep(0xEAC4,3), new StagingTimingStep(0xEAC6,2)
        };
        private static readonly StagingTimingStep[] StagingMacroTrim = {
            new StagingTimingStep(0xEACA,3), new StagingTimingStep(0xEACC,2), new StagingTimingStep(0xEACE,3),
            new StagingTimingStep(0xEAD0,3), new StagingTimingStep(0xEAD2,2), new StagingTimingStep(0xEAD4,3)
        };
        private static readonly StagingTimingStep[] StagingBandPixelsHead = {
            new StagingTimingStep(0xE70B,3), new StagingTimingStep(0xE70D,4), new StagingTimingStep(0xE710,2),
            new StagingTimingStep(0xE712,2)
        };
        private static readonly StagingTimingStep[] StagingBandPixelRow = {
            new StagingTimingStep(0xE713,4), new StagingTimingStep(0xE716,5), new StagingTimingStep(0xE719,4),
            new StagingTimingStep(0xE71C,5), new StagingTimingStep(0xE71F,2), new StagingTimingStep(0xE720,2),
            new StagingTimingStep(0xE721,2), new StagingTimingStep(0xE722,2), new StagingTimingStep(0xE723,2),
            new StagingTimingStep(0xE724,2), new StagingTimingStep(0xE725,2), new StagingTimingStep(0xE726,2),
            new StagingTimingStep(0xE727,2), new StagingTimingStep(0xE728,2)
        };
        private static readonly StagingTimingStep[] StagingBandTrimHead = {
            new StagingTimingStep(0xE72C,3), new StagingTimingStep(0xE72E,2)
        };
        private static readonly StagingTimingStep[] StagingBandTrimSetup = {
            new StagingTimingStep(0xE732,2), new StagingTimingStep(0xE733,2), new StagingTimingStep(0xE735,2),
            new StagingTimingStep(0xE736,2)
        };
        private static readonly StagingTimingStep[] StagingBandTrimRow = {
            new StagingTimingStep(0xE738,4), new StagingTimingStep(0xE73B,5), new StagingTimingStep(0xE73E,2),
            new StagingTimingStep(0xE73F,2)
        };
        private static readonly StagingTimingStep[] StagingBandAttributes = {
            new StagingTimingStep(0xE742,3), new StagingTimingStep(0xE744,3), new StagingTimingStep(0xE746,2),
            new StagingTimingStep(0xE747,2), new StagingTimingStep(0xE748,2), new StagingTimingStep(0xE749,2),
            new StagingTimingStep(0xE74B,2), new StagingTimingStep(0xE74C,3), new StagingTimingStep(0xE74E,2),
            new StagingTimingStep(0xE750,2), new StagingTimingStep(0xE751,3), new StagingTimingStep(0xE753,2),
            new StagingTimingStep(0xE754,3), new StagingTimingStep(0xE756,4), new StagingTimingStep(0xE759,3),
            new StagingTimingStep(0xE75B,4), new StagingTimingStep(0xE75E,4), new StagingTimingStep(0xE761,3),
            new StagingTimingStep(0xE763,5), new StagingTimingStep(0xE766,3), new StagingTimingStep(0xE768,5),
            new StagingTimingStep(0xE76B,2), new StagingTimingStep(0xE76C,2), new StagingTimingStep(0xE76E,5),
            new StagingTimingStep(0xE771,2), new StagingTimingStep(0xE772,2), new StagingTimingStep(0xE773,2),
            new StagingTimingStep(0xE774,3), new StagingTimingStep(0xE776,2)
        };
        private static readonly StagingTimingStep[] StagingBandCursor = {
            new StagingTimingStep(0xE782,5), new StagingTimingStep(0xE785,3), new StagingTimingStep(0xE787,2),
            new StagingTimingStep(0xE789,2), new StagingTimingStep(0xE78A,3), new StagingTimingStep(0xE78C,2),
            new StagingTimingStep(0xE78D,4), new StagingTimingStep(0xE790,3)
        };
        private static readonly StagingTimingStep[] StagingBandCell = {
            new StagingTimingStep(0xE799,3), new StagingTimingStep(0xE79B,2), new StagingTimingStep(0xE79C,4),
            new StagingTimingStep(0xE79F,3)
        };
        private static readonly StagingTimingStep[] StagingFlushHead = {
            new StagingTimingStep(0xC115,2), new StagingTimingStep(0xC117,3)
        };
        private static readonly StagingTimingStep[] StagingFlushHeader = {
            new StagingTimingStep(0xC119,4), new StagingTimingStep(0xC11C,2), new StagingTimingStep(0xC11E,4),
            new StagingTimingStep(0xC121,4), new StagingTimingStep(0xC124,4), new StagingTimingStep(0xC127,4)
        };
        private static readonly StagingTimingStep[] StagingFlushByte = {
            new StagingTimingStep(0xC12A,4), new StagingTimingStep(0xC12D,4), new StagingTimingStep(0xC130,2),
            new StagingTimingStep(0xC131,2)
        };
        private static readonly StagingTimingStep[] StagingFlushNext = {
            new StagingTimingStep(0xC134,2), new StagingTimingStep(0xC135,2), new StagingTimingStep(0xC136,2)
        };

        private static StagingTimingStep S(int pc, int cycles) => new StagingTimingStep((ushort)pc, cycles);

        /// <summary>$FF55 timing and live F5/F6 ownership. The deferred callback owns
        /// the existing native audio task/tick costs, excluding the NMI-only return.</summary>
        private static IEnumerable<StagingTimingStep> StagingSwitch(Func<bool> deferred)
        {
            foreach (var op in StagingSwitchHead) yield return op;
            bool pending = deferred();
            yield return S(0xFF74, pending ? 3 : 2);
            if (!pending) { yield return S(0xFF76,6); yield break; }
            foreach (var op in StagingSwitchDeferredHead) yield return op;
            // Audio is serviced here, after RTI, not in the NMI that interrupted F5.
            yield return S(0xFF8C,-1); // native audio service is priced/executed at entry
            yield return S(0xFFA9,2); yield return S(0xFFAB,3);
            yield return S(0xFFAD,3); yield return S(0xFFAF,3);
            foreach (var op in StagingSwitchHead) yield return op;
            yield return S(0xFF74,2); yield return S(0xFF76,6);
        }

        public IEnumerable<StagingTimingStep> StagingPassInstructions(byte cursor, byte gridIndex, sbyte step,
            byte boundary, bool last, Func<bool> deferred)
        {
            if (_world.Id == 6) throw new NotSupportedException("Loader world6 checkpoint tile bank");
            yield return S(0xDE86,2); yield return S(0xDE88,3); yield return S(0xDE8A,6);
            foreach (var op in StagingBandHead) yield return op;
            yield return S(0xE705,6);
            foreach (var op in StagingRoomPointer)
                yield return op.Pc == 0xEB2B ? S(op.Pc,GridReadCycles(gridIndex)) : op;
            yield return S(0xE708,6);
            yield return S(0xEA36,3);
            bool room = GridByte(gridIndex) != 0;
            yield return S(0xEA38,room ? 3 : 2);
            int macro = ((cursor & 7) << 3) | ((cursor & 0x78) >> 4);
            if (!room) yield return S(0xEA3A,6);
            else
            {
                if (!TryGetRoom((gridIndex % _world.RowWidth)*32,gridIndex / _world.RowWidth,out _,out byte[] meta,out _))
                    throw new NotSupportedException("Loader macro room unavailable");
                yield return S(0xEA3B,6);
                foreach (var op in StagingMacroBankHead)
                    yield return op.Pc == 0xEADD ? S(op.Pc,_world.SetBank < 6 ? 3 : 2) : op;
                if (_world.SetBank >= 6) yield return S(0xEADF,2);
                yield return S(0xEAE1,6);
                foreach (var op in StagingSwitch(deferred)) yield return op;
                foreach (var op in StagingMacroPointer) yield return op;
                yield return S(0xEA3E,3); yield return S(0xEA40,2); yield return S(0xEA42,3);
                foreach (var op in StagingMacroSetup) yield return op;
                foreach (var op in StagingSwitch(deferred)) yield return op;
                foreach (var op in StagingMacroInit) yield return op;
                for (int q=3;q>=0;q--)
                {
                    int tile = meta[((macro>>3)*2+(q>>1))*16+(macro&7)*2+(q&1)];
                    foreach (var op in StagingQuadrantHead)
                        yield return op.Pc == 0xEA69 ? S(op.Pc,4+(tile>=32 ? 1 : 0)) : op;
                    yield return S(0xEA77,(q&2)==0 ? 3 : 2);
                    foreach (var op in (q&2)==0 ? StagingQuadrantLow : StagingQuadrantHigh) yield return op;
                    foreach (var op in StagingQuadrantMerge) yield return op;
                    yield return S(0xEA98,q==0 ? 3 : 2);
                    for(int count=q;count>0;count--)
                    {
                        yield return S(0xEA9A,2); yield return S(0xEA9B,2); yield return S(0xEA9C,2);
                        yield return S(0xEA9D,count>1 ? 3 : 2);
                    }
                    foreach(var op in StagingQuadrantTiles)
                        yield return op.Pc == 0xEAA5 || op.Pc == 0xEAAB || op.Pc == 0xEAB1 || op.Pc == 0xEAB7
                            ? S(op.Pc,4+(tile>=32 ? 1 : 0)) : op;
                    yield return S(0xEABF,q==0 ? 3 : 2);
                    if(q!=0) yield return S(0xEAC1,3);
                }
                foreach(var op in StagingMacroTail) yield return op;
                yield return S(0xEAC8,macro<0x38 ? 3 : 2);
                if(macro>=0x38) foreach(var op in StagingMacroTrim) yield return op;
                yield return S(0xEAD6,6);
            }
            foreach(var op in StagingBandPixelsHead) yield return op;
            for(int row=0;row<4;row++)
            {
                foreach(var op in StagingBandPixelRow) yield return op;
                yield return S(0xE72A,row<3 ? 3 : 2);
            }
            foreach(var op in StagingBandTrimHead) yield return op;
            yield return S(0xE730,macro<0x38 ? 3 : 2);
            if(macro>=0x38)
            {
                foreach(var op in StagingBandTrimSetup) yield return op;
                for(int copy=0;copy<3;copy++)
                {
                    foreach(var op in StagingBandTrimRow) yield return op;
                    yield return S(0xE740,copy<2 ? 3 : 2);
                }
            }
            foreach(var op in StagingBandAttributes) yield return op;
            yield return S(0xE778,(cursor&8)!=0 ? 3 : 2);
            if((cursor&8)==0) { yield return S(0xE77A,3); yield return S(0xE77C,3); }
            else { yield return S(0xE77F,3); yield return S(0xE781,2); }
            foreach(var op in StagingBandCursor) yield return op;
            yield return S(0xE792,2); yield return S(0xE794,4);
            bool crossed = ((byte)(cursor+step)&0x7F)==boundary;
            yield return S(0xE797,crossed ? 2 : 3);
            if(crossed) foreach(var op in StagingBandCell) yield return op;
            yield return S(0xE7A1,5); yield return S(0xE7A3,3);
            yield return S(0xE7A8,3); yield return S(0xE7AA,2); yield return S(0xE7AC,3);
            foreach(var op in StagingFlushHead) yield return op;
            int rows=(cursor&7)==7 ? 2 : 4;
            for(int record=0;record<=rows;record++)
            {
                foreach(var op in StagingFlushHeader)
                {
                    if(op.Pc==0xC11C) yield return S(op.Pc,2);
                    else yield return op;
                }
                int length=record==rows ? 1 : 2;
                for(int index=0;index<length;index++)
                {
                    foreach(var op in StagingFlushByte) yield return op;
                    yield return S(0xC132,index+1<length ? 3 : 2);
                }
                foreach(var op in StagingFlushNext) yield return op;
                yield return S(0xC137,3);
            }
            yield return S(0xC119,4); yield return S(0xC11C,3); yield return S(0xC139,6);
            yield return S(0xDE8D,3); yield return S(0xDE8F,3); yield return S(0xDE91,last ? 2 : 3);
        }
    }
}
