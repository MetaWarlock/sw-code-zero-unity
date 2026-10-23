namespace Darkwing.Core
{
    public sealed partial class ObjectSystem
    {
        public int LastPlacementSpawnCycles { get; private set; } = -1;
        public string PlacementSpawnTimingUnsupportedReason { get; private set; }

        public bool TryMeasurePlacementSpawnCycles(ushort scrollX, ushort scrollY, byte scrollState, out int cycles, out string reason)
        {
            cycles = 0;
            reason = null;
            int mode = scrollState & 15;
            if (mode == 0) { cycles = 14; return true; }
            if (mode != 1 && mode != 4 && mode != 8) { reason = "8090 reverse placement walk"; return false; }
            if (Placements == null || PlacementListIndex < 0 || PlacementListIndex >= ListXloPtrLo.Length)
            { reason = "8090 placement pointer owner"; return false; }
            int listBase = ListXloPtrLo[PlacementListIndex] | ListXloPtrHi[PlacementListIndex] << 8;
            int count = Placements.ObjectCount;
            var active = new bool[SlotCount];
            var placements = new byte[SlotCount];
            for (int slotIndex = 0; slotIndex < SlotCount; slotIndex++)
            { active[slotIndex] = Slots[slotIndex].Active; placements[slotIndex] = Slots[slotIndex].PlacementIndex; }
            cycles = mode == 1 ? 12 : mode == 4 ? 20 : 24;
            int cursor = CursorFwd;
            for (int visits = 0; visits < 256; visits++)
            {
                cycles += 9;
                if (!TryMeasurePlacementWindow(cursor, listBase, count, scrollX, scrollY, out bool inside, out int window, out reason)) return false;
                cycles += window;
                if (!inside) { cycles += 3; break; }
                cycles += 8;
                if (!TryMeasurePlacementActivation(cursor, listBase, count, active, placements, out int activation, out reason)) return false;
                cycles += activation + 5;
                cursor = (cursor + 1) & 255;
                cycles += cursor == 0 ? 2 : 3;
                if (cursor == 0) break;
                if (visits == 255) { reason = "8090 forward placement wrap"; return false; }
            }
            cycles += 3;
            if (cursor == 0) { cycles += 12; return true; }
            cycles += 2;
            for (int visits = 0; visits < 256; visits++)
            {
                cursor = (cursor - 1) & 255;
                cycles += 8;
                if (!TryMeasurePlacementWindow(cursor, listBase, count, scrollX, scrollY, out bool inside, out int window, out reason)) return false;
                cycles += window + (inside ? 3 : 2);
                if (!inside) { cycles += 9; return true; }
            }
            reason = "8090 reverse-cursor adjustment wrap";
            return false;
        }

        private bool TryMeasurePlacementWindow(int index, int listBase, int count, ushort scrollX, ushort scrollY,
            out bool inside, out int cycles, out string reason)
        {
            inside = false;
            cycles = 0;
            reason = null;
            if (index >= count) { reason = "80DA placement outside native list"; return false; }
            var placement = Placements.Objects[index];
            int borrowX = placement.X < (scrollX & 255) ? 1 : 0;
            int pageX = placement.ScreenX - (scrollX >> 8) - borrowX;
            cycles = 33 + PlacementReadPageCross(listBase, index) + PlacementReadPageCross(listBase - count, index);
            if (pageX != 0) return true;
            bool borrowY = placement.Y < (scrollY & 255);
            cycles += 26 + (borrowY ? 3 : 0) + PlacementReadPageCross(listBase + count * 2, index)
                + PlacementReadPageCross(listBase + count, index);
            inside = placement.ScreenY - (scrollY >> 8) - (borrowY ? 1 : 0) == 0;
            return true;
        }

        private bool TryMeasurePlacementActivation(int index, int listBase, int count, bool[] active, byte[] placements,
            out int cycles, out string reason)
        {
            cycles = 37;
            reason = null;
            if (IsPlacementSpawned((byte)index)) { cycles += 8; return true; }
            cycles += 4;
            for (int slotIndex = SlotCount - 1; slotIndex >= 5; slotIndex--)
            {
                cycles += 4;
                if (placements[slotIndex] == index)
                {
                    cycles += 7;
                    if (active[slotIndex]) { cycles += 10; return true; }
                    cycles += 2;
                    break;
                }
                cycles += 6 + (slotIndex == 5 ? 5 : 3);
            }
            cycles += 2;
            for (int slotIndex = SlotCount - 1; slotIndex >= 5; slotIndex--)
            {
                cycles += 4;
                if (!active[slotIndex])
                {
                    cycles += 3;
                    var placement = Placements.Objects[index];
                    if (placement.Type >= 64 || !EnemyBehaviors.TryGetType(placement.Type, out _, out _, out byte animation, out _, out _))
                    { reason = "8133 placement type table"; return false; }
                    int record = placement.Type * 4;
                    cycles += 158 + 2 * PlacementReadPageCross(listBase + 3 * count, index);
                    cycles += PlacementReadPageCross(0x8AB3, record) + PlacementReadPageCross(0x8C57, record);
                    for (int field = 1; field <= 3; field++) cycles += PlacementReadPageCross(0x8AB3, record + field);
                    cycles += PlacementX(Placements, index) < Slots[0].X ? 40 : 46;
                    int noFlip = System.Array.IndexOf(NoFlipAnims, animation);
                    cycles += noFlip >= 0 ? 9 + noFlip * 4 : 93;
                    cycles += 79;
                    active[slotIndex] = true;
                    placements[slotIndex] = (byte)index;
                    return true;
                }
                cycles += 6 + (slotIndex == 5 ? 2 : 3);
            }
            cycles += 6;
            return true;
        }

        private static int PlacementReadPageCross(int pointer, int index) => ((pointer & 255) + index) > 255 ? 1 : 0;
    }
}
