using System;

namespace Darkwing.Core
{
    public sealed class ActorEventTiming
    {
        public Func<int, int> PeekSfxEnqueueCycles;
        public Func<byte> ReadHookLatch;

        public int Enqueue(int sound)
        {
            if (PeekSfxEnqueueCycles == null) throw new NotSupportedException("F850 actual queue owner absent");
            int cycles = PeekSfxEnqueueCycles(sound);
            if (cycles != 42 && cycles != 27) throw new NotSupportedException("F850 queue owner unsupported");
            return cycles;
        }

        public int ProjectileDamageTail(ObjectSystem objects, PlayerCore player, ObjectSystem.Slot target, int projectileIndex)
        {
            var projectile = objects.Slots[projectileIndex];
            if (projectile.AnimNumber != 0x13 || projectile.Behavior != 1 || target.Behavior is 0x1E or 0x34 or 5 || target.Health >= 128)
                throw new NotSupportedException("806B projectile damage variant");
            int cycles = 2 + 4 + 2 + 2 + 2 + 2 + 3 + 4 + 3 + 4;
            if (target.Health != 0)
            {
                cycles += 2 + 2 + 6 + Enqueue(0x1D) + 3 + 4 + 2 + 3 + 7 + 4 + 2
                    + 2 + 5 + 3 + 4 + 2 + 2 + 4 + 2 + 2 + 2 + 2 + 6 + 63 + 3;
                return cycles;
            }
            cycles += 3;
            cycles += 4 + 2 + 2 + 3 + 4 + 2 + 2 + 4 + 2 + 2 + 2 + 2 + 6 + 63;
            cycles += 2 + 5 + 2 + 5 + 5 + 2 + 5 + 2 + 5 + 2 + 5 + 2 + 5 + 2 + 5 + 2 + 6 + Enqueue(0x1E);
            var dead = new ObjectSystem.Slot { Active = true, Flags = target.Flags, Behavior = 5, BehaviorParam = 0 };
            return cycles + ContactTail(objects, player, dead);
        }

        public static int ContactTail(ObjectSystem objects, PlayerCore player, ObjectSystem.Slot target)
        {
            int cycles = 3;
            if (player.InvulnFrames != 0) return cycles + 3 + 3;
            cycles += 2 + 6 + ActorBank6Timing.TouchNoHit(objects, player, target, out bool hit);
            if (!hit) return cycles + 3 + 3;
            byte animation = objects.Slots[0].AnimNumber;
            if (animation == 4 || animation == 0x0D || target.AnimNumber >= 0x3F)
                throw new NotSupportedException("8144 damage or armored contact");
            return cycles + 2 + 4 + 2 + 2 + 2 + 2 + 4 + 2 + 3 + 3;
        }

        public static int DeathDebris(ObjectSystem objects, ObjectSystem.Slot target)
        {
            int free = -1;
            int scan = 2;
            for (int slot = 15; slot >= 5; slot--)
            {
                scan += 4;
                if (!objects.Slots[slot].Active) { scan += 3 + 2 + 6; free = slot; break; }
                scan += 2 + 2 + 2 + (slot == 5 ? 2 : 3);
            }
            if (free < 0) return 6 + scan + 2 + 6 + 3 + 6;
            if (target.AnimNumber == 0x38) throw new NotSupportedException("8741 debris alternate table");
            int cycles = 6 + scan + 2 + 3 + 4 * (4 + 5) + 2 + 5 + 2 + 5 + 4 + 2 + 3;
            int next = (EnemyBehaviors.RngE5 + EnemyBehaviors.RngE6 + (target.AnimNumber >= 0x38 ? 1 : 0)) & 255;
            cycles += 3 + 3 + 3 + 2;
            if (next < 0x7F) return cycles + 3 + 6;
            cycles += 2 + 2 + 2 + 3 + 4 + 3 + 6 + 37 + 2 + 5 + 2 + 5 + 2 + 5;
            int second = (EnemyBehaviors.RngE7 + next + 1) & 255;
            cycles += 3 + 3 + 3;
            foreach (int match in new[] { 0x30, 0x50, 0x80, 0xA0 })
            {
                cycles += 2;
                if (second == match) return cycles + 3 + 2 + 6 + 37 + 6;
                cycles += 2;
            }
            return cycles + 6;
        }
    }
}
