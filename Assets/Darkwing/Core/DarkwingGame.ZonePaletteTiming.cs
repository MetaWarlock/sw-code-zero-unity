using System;

namespace Darkwing.Core
{
    public sealed partial class DarkwingGame
    {
        public int LastZoneServiceCycles { get; private set; } = -1;
        public string ZoneTimingUnsupported { get; private set; }
        public int LastPaletteScriptCycles { get; private set; } = -1;
        public string PaletteTimingUnsupported { get; private set; }

        private void BeginZoneServiceTiming()
        {
            LastZoneServiceCycles = -1;
            ZoneTimingUnsupported = null;
            if (Sections == null || TileMap == null || Objects == null)
            {
                ZoneTimingUnsupported = "EDF1 world data unavailable";
                return;
            }
            if (_worldId > 1 || TileMap.World.Id != _worldId)
            {
                ZoneTimingUnsupported = "EDF1 world outside missions 1-2";
                return;
            }
            byte cell = Player.CellGridByte;
            // JSR $EDF1, LDA $31 / CMP / BCS / AND / TAY, the $EE07/$EE0E vector and JMP ($0000).
            int cycles = 6 + 3 + 2 + 2 + 2 + 2 + 4 + 3 + 4 + 3 + 5;
            // Both handlers start LDY $22 / LDA ($32),Y / CMP #imm.
            cycles += 3 + 5 + (((TileMap.World.GridAddress & 255) + Player.CellIndex > 255) ? 1 : 0) + 2;
            if (_worldId == 0)
            {
                if (cell < 0x0F) LastZoneServiceCycles = cycles + 3 + 6;
                else if (cell == 0x14) LastZoneServiceCycles = cycles + 2 + 2 + 3 + 6;
                else ZoneTimingUnsupported = "EE21 palette-writing zone";
                return;
            }
            LastZoneServiceCycles = ZoneEE2DCycles(cycles, cell, (byte)(Player.ScrollX & 0xFF));
        }

        /// <summary>$EE2D (mission 2) from its $EE31 compare on. Every exit except cell $34 reloads a
        /// palette ($C2F9 background index 1/2/3 or $C319 sprite index 2) and flags $18 at $EE5C.</summary>
        private static int ZoneEE2DCycles(int cycles, byte cell, byte scrollLow)
        {
            const int Flag = 3 + 2 + 3 + 6;                       // JMP $EE5C, LDA #$FF, STA $18, RTS
            if (cell < 0x25) return cycles + 2 + 2 + 6 + BgPaletteCycles(1) + Flag;
            cycles += 3 + 2;                                      // BCS $EE3D, CMP #$27
            bool scrollTest;
            if (cell < 0x27) { cycles += 2; scrollTest = true; }  // BCS $EE4F not taken
            else
            {
                cycles += 3 + 2;                                  // BCS $EE4F, CMP #$28
                if (cell == 0x27) { cycles += 3; scrollTest = true; } // BCC $EE41
                else
                {
                    cycles += 2 + 2;                              // BCC not taken, CMP #$2A
                    if (cell < 0x2A) return cycles + 2 + 2 + 6 + BgPaletteCycles(3) + 2 + 3 + 6; // $EE57 falls into $EE5C
                    cycles += 3 + 2;                              // BCS $EE61, CMP #$34
                    if (cell == 0x34) return cycles + 3 + 6;      // BEQ $EE60 → RTS
                    return cycles + 2 + 2 + 3 + 2 + 6 + SpritePaletteCycles(2, 1) + Flag;
                }
            }
            if (!scrollTest) throw new InvalidOperationException("EE2D path");
            cycles += 3 + 2;                                      // LDA $FC / CMP #$E9
            if (scrollLow < 0xE9) return cycles + 2 + 2 + 6 + BgPaletteCycles(2) + Flag;
            return cycles + 3 + 2 + 6 + BgPaletteCycles(3) + 2 + 3 + 6; // BCS $EE57, falls into $EE5C
        }

        /// <summary>$C2F9 with A = index: 16 bytes from $C35D + index·16 into $0300/$0320.</summary>
        private static int BgPaletteCycles(int index)
        {
            int y = (index << 4) & 0xFF;
            int cycles = 4 * 2 + 2 + 2 + 2 + 3 + 2 + 3 + 2;
            for (int entry = 0; entry < 16; entry++)
                cycles += 5 + (0x5D + y + entry > 255 ? 1 : 0) + 5 + 5 + 2 + 2 + 2 + (entry < 15 ? 3 : 2);
            return cycles + 6;
        }

        /// <summary>$C319 with A = index: $C4FD fixed entries, $C505 + index·8, then the $C33C world tail.</summary>
        private static int SpritePaletteCycles(int index, byte world)
        {
            int y = (index << 3) & 0xFF;
            int cycles = 3 * 2 + 2 + 2;
            for (int entry = 0; entry < 8; entry++)
                cycles += 4 + (0xFD + entry > 255 ? 1 : 0) + 5 + 5 + 2 + 2 + (entry < 7 ? 3 : 2);
            for (int entry = 0; entry < 8; entry++)
                cycles += 4 + (0x05 + y + entry > 255 ? 1 : 0) + 5 + 5 + 2 + 2 + 2 + (entry < 7 ? 3 : 2);
            const int Override = 3 + 2 + 4 + 4 + 6;              // BEQ taken, LDA #, STA $0310/$0330, RTS
            cycles += 3 + 2;                                      // LDA $31 / CMP #$08
            if (world == 8) return cycles + Override;
            cycles += 2 + 2;                                      // BEQ not taken, CMP #$09
            if (world == 9) return cycles + Override;
            cycles += 2 + 2;                                      // BEQ not taken, CMP #$0B
            if (world == 0x0B) return cycles + Override;
            return cycles + 2 + 6;
        }

        private void BeginPaletteScriptTiming()
        {
            LastPaletteScriptCycles = -1;
            PaletteTimingUnsupported = null;
            try
            {
                LastPaletteScriptCycles = MeasurePaletteScriptCycles();
            }
            catch (NotSupportedException exception)
            {
                PaletteTimingUnsupported = exception.Message;
            }
        }

        private int MeasurePaletteScriptCycles()
        {
            int cycles = 6 + 3;
            byte script = Player.SectionFlag3D;
            if (script < 0x80) return cycles + 4 + 6;
            if (Sections == null) throw new NotSupportedException("EFBE palette data unavailable");
            int basis = script & 0x7F;
            if (basis != 0) throw new NotSupportedException("EFC2 palette script outside base-0 slice");
            byte oldIndex = Player.PalIndex3F;
            if (oldIndex > Sections.PaletteScriptSteps(basis))
                throw new NotSupportedException("EFD8 palette index outside verified script");
            cycles += 2 + 2 + 3 + 2 + 3 + 2 + 3 + 5;
            cycles += 4 + ((0x4A + basis > 255) ? 1 : 0);
            if (Player.PalStep3E != Sections.PaletteScriptPeriod(basis)) return cycles + 4 + 6;
            cycles += 2 + 3 + 3 + 3 + 5;
            cycles += 4 + ((0x49 + basis > 255) ? 1 : 0);
            cycles += oldIndex == Sections.PaletteScriptSteps(basis) ? 2 + 3 : 3;
            int headerIndex = oldIndex + 2 + basis;
            if (headerIndex > 255) throw new NotSupportedException("EFEC palette header wrap");
            cycles += 2 + 2 + 3 + 2 + 4 + ((0x49 + headerIndex > 255) ? 1 : 0);
            int offset = Bank7[0xF049 - 0xC000 + headerIndex];
            while (true)
            {
                byte destination = Bank7[0xF008 - 0xC000 + offset];
                cycles += 4 + ((8 + offset > 255) ? 1 : 0);
                if (destination >= 0x80) return cycles + 4 + 3 + 6;
                if (destination > 29) throw new NotSupportedException("EFF7 palette destination outside mirror");
                if (offset > 251) throw new NotSupportedException("EFF5 palette record wrap");
                cycles += 2 + 2 + 2 + 2 + 3;
                offset++;
                for (int index = 0; index < 3; index++)
                {
                    cycles += 4 + ((8 + offset > 255) ? 1 : 0) + 5 + 2 + 2 + 5 + (index < 2 ? 4 : 2);
                    offset++;
                }
                cycles += 4;
            }
        }

        public bool TryMeasurePaletteServiceCycles(byte switchDepth, byte deferredAudio,
            out int cycles, out string unsupported)
        {
            cycles = -1;
            unsupported = PaletteTimingUnsupported;
            if (LastPaletteScriptCycles < 0)
            {
                if (unsupported == null) unsupported = "CA7C palette script has not executed";
                return false;
            }
            if (switchDepth != 0 || deferredAudio != 0)
            {
                unsupported = "CA81 FF55 deferred F5/F6 path";
                return false;
            }
            int switchCycles = 2 + 6 + 3 + 3 + 5 + 4 + 2 + 4 + 2 + 4 + 2 + 4 + 2 + 4 + 2 + 3 + 3 + 2 + 6;
            cycles = LastPaletteScriptCycles + switchCycles;
            return true;
        }
    }
}
