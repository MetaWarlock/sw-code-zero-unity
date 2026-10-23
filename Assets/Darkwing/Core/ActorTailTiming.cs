using System;

namespace Darkwing.Core
{
    public static class ActorTailTiming
    {
        public static int MeasureBank1Script(ObjectSystem objects, out string unsupported)
        {
            unsupported = null;
            if (objects == null) { unsupported = "objects"; return -1; }
            int cycles = 2 + 3 + 2 + 3;
            for (int slotIndex = 1; slotIndex < ObjectSystem.SlotCount; slotIndex++)
            {
                var slot = objects.Slots[slotIndex];
                cycles += 4;
                if (!slot.Active) cycles += 3;
                else
                {
                    cycles += 2 + 4 + 2;
                    if (slot.Behavior < 0x47) cycles += 3;
                    else
                    {
                        if (slot.Behavior < 0x70 || slot.Behavior > 0x7D || (slot.StateLow & 15) != 0)
                        {
                            unsupported = "bank1 behavior/state " + slot.Behavior.ToString("X2") + "/" + slot.StateLow.ToString("X2");
                            return -1;
                        }
                        cycles += 2 + 4 + PageCross(0x8836, slot.Behavior) + 3
                            + 4 + PageCross(0x88B6, slot.Behavior) + 3 + 2 + 3 + 2 + 3 + 5;
                        int projectileCycles = ProjectileNoHit(objects, slot, out bool hit);
                        if (hit) { unsupported = "mission-door projectile hit"; return -1; }
                        cycles += 4 + 2 + 2 + 6 + projectileCycles + 3 + 6;
                    }
                }
                cycles += 5 + 3 + 2 + (slotIndex == 15 ? 2 + 3 + 6 : 3 + 3);
            }
            return cycles;
        }

        public static int MeasureBank1AndTail(ObjectSystem objects, byte cell, ushort gridAddress,
            byte cellIndex, byte deferredBank, out string unsupported)
        {
            int cycles = MeasureBank1Script(objects, out unsupported);
            if (cycles < 0) return -1;
            cycles += 6 + 3 + 5 + PageCross(gridAddress, cellIndex);
            byte[] cells = { 0x14, 0x34, 0x56, 0x76, 0x97, 0xB5, 0x98 };
            for (int cellTest = 0; cellTest < cells.Length; cellTest++)
            {
                cycles += 2;
                if (cell == cells[cellTest])
                {
                    if (cellTest != 0) { unsupported = "internal-engine cell " + cell.ToString("X2"); return -1; }
                    cycles += 3;
                    break;
                }
                cycles += 2;
            }
            if (deferredBank != 0) { unsupported = "deferred bank"; return -1; }
            cycles += 2 + 6 + 55 + 2 + 3 + 6 + ClearShadowCycles(4);
            return cycles;
        }

        public static int ClearShadowCycles(byte cursor)
        {
            if ((cursor & 3) != 0) throw new ArgumentException("Shadow cursor must be aligned", nameof(cursor));
            int records = cursor == 0 ? 64 : (256 - cursor) / 4;
            return 3 + 2 + (cursor == 4 ? 2 + 2 + 4 : 3) + 2
                + records * (5 + 2 + 2 + 2 + 2 + 3) - 1 + 6;
        }

        public static int ProjectileNoHit(ObjectSystem objects, ObjectSystem.Slot target, out bool hit)
        {
            return ProjectileNoHit(objects, target, out hit, out _);
        }

        public static int ProjectileNoHit(ObjectSystem objects, ObjectSystem.Slot target, out bool hit, out int projectileSlot)
        {
            hit = false;
            projectileSlot = -1;
            int cycles = 2 + 3;
            for (int slotIndex = 4; slotIndex >= 2; slotIndex--)
            {
                var projectile = objects.Slots[slotIndex];
                cycles += 3 + 4;
                if (!projectile.Active) cycles += 3;
                else
                {
                    cycles += 2 + 4;
                    if ((projectile.Flags & 0x80) == 0) cycles += 3;
                    else
                    {
                        cycles += 2 + 4 + 2;
                        if (projectile.Behavior == 0x1F) cycles += 3;
                        else
                        {
                            cycles += 2 + 4 + 2 + 4 + 2;
                            for (int behaviorTest = 1; behaviorTest <= 4; behaviorTest++) cycles += 2 + 2;
                            cycles += 4;
                            if ((target.Flags & 0x80) == 0) cycles += 3;
                            else
                            {
                                cycles += 2 + 2;
                                if ((target.Flags & 4) != 0) return cycles + 4 + 6;
                                cycles += 2 + 4 + 3 + 4 + 3 + 4 + 3 + 3 + 6;
                                int deltaX = projectile.X - target.X;
                                int magnitudeX = deltaX >= 0 ? (byte)deltaX : (byte)-deltaX;
                                int hitClass = target.BehaviorParam & 31;
                                cycles += 33 + (deltaX >= 0 ? 3 : 6) + 4;
                                if (magnitudeX >= EnemyBehaviors.ProjectileXSize[hitClass]) cycles += 3 + 6;
                                else
                                {
                                    int deltaY = (byte)projectile.Y - (byte)target.Y;
                                    int magnitudeY = deltaY >= 0 ? deltaY : -deltaY;
                                    hit = magnitudeY < EnemyBehaviors.ProjectileYSize[hitClass];
                                    cycles += 2 + 3 + 2 + 4 + (deltaY >= 0 ? 3 : 6) + 4 + (hit ? 3 : 2) + 6;
                                }
                                cycles += hit ? 4 : 2;
                                if (hit) { projectileSlot = slotIndex; return cycles + 6; }
                            }
                        }
                    }
                }
                cycles += 5 + 3 + 2 + (slotIndex == 2 ? 2 : 4);
            }
            return cycles + 2 + 6;
        }

        private static int PageCross(int address, int index) => ((address & 255) + index) > 255 ? 1 : 0;
    }
}
