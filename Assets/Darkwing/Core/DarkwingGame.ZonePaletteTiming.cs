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
            if (_worldId != 0 || TileMap.World.Id != 0)
            {
                ZoneTimingUnsupported = "EDF1 world outside mission-1 slice";
                return;
            }
            byte cell = Player.CellGridByte;
            int cycles = 6 + 3 + 2 + 2 + 2 + 2 + 4 + 3 + 4 + 3 + 5;
            cycles += 3 + 5 + (((TileMap.World.GridAddress & 255) + Player.CellIndex > 255) ? 1 : 0) + 2;
            if (cell < 0x0F) LastZoneServiceCycles = cycles + 3 + 6;
            else if (cell == 0x14) LastZoneServiceCycles = cycles + 2 + 2 + 3 + 6;
            else ZoneTimingUnsupported = "EE21 palette-writing zone";
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
