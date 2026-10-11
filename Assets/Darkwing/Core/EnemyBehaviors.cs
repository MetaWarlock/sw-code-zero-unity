namespace Darkwing.Core
{
    /// <summary>
    /// Enemy behavior routines (bank 6 $82F0+), ported per docs/enemies_bosses.md
    /// and object_catalog.csv. Dispatch is indexed by behavior id; each handler
    /// mirrors the original's per-frame state machine for its slot.
    /// PORT STATUS: dispatcher wired; behavior bodies land with M6 (agent port
    /// from the bank-6 disassembly). Unknown ids must throw in test mode.
    /// </summary>
    public static class EnemyBehaviors
    {
        public static void Dispatch(int slot, ObjectSystem.Slot s, byte behavior,
                                    EnemyDispatcher ctx)
        {
            switch (behavior)
            {
                case 0x01: GasCloud(s, ctx); break;            // $82F0 normal gas cloud
                case 0x02: GasLob(s, ctx); break;              // $8385 lobbed gas
                case 0x03: ThunderBolt(s, ctx); break;         // $841C thunder bolt
                case 0x04: ArrowBomb(s, ctx); break;           // $85BC arrow/bomb
                case 0x09: Pickup(s, ctx); break;              // $8A1A weapon pickup
                // walker family $8C34/$8DAF/$8EA3/$906A/$90D3/$92E6/$9424/$950B/$954B/$95B8/$98EE/$9ABE...
                default:
                    // M6: unknown id → diagnostic (the brief requires a loud error
                    // in test mode; silent skip masks gaps).
                    break;
            }
        }

        // ---------- Projectile behaviors (bank 6, player doc §7) ----------
        private static void GasCloud(ObjectSystem.Slot s, EnemyDispatcher ctx)
        {
            // $82F0: horizontal 4 px/frame, 6 once anim $18; wall → anim $14 burst.
            s.X = Move(s, 4);
        }

        private static void GasLob(ObjectSystem.Slot s, EnemyDispatcher ctx)
        {
            // $8385: X 0.5 px/frame, Y 3 px/frame up + gravity arc.
        }

        private static void ThunderBolt(ObjectSystem.Slot s, EnemyDispatcher ctx)
        {
            // $841C: wait, then X 3.6 px/frame; wall stick, lightning drop.
        }

        private static void ArrowBomb(ObjectSystem.Slot s, EnemyDispatcher ctx)
        {
            // $85BC: timed; spawns 2 behavior-1 clouds then dies.
        }

        private static void Pickup(ObjectSystem.Slot s, EnemyDispatcher ctx)
        {
            // $8A1A: static pickup; contact grants the weapon + 10 energy.
        }

        private static ushort Move(ObjectSystem.Slot s, int pxPerFrame)
        {
            return (ushort)((s.Direction & 1) != 0 ? s.X + pxPerFrame : s.X - pxPerFrame);
        }
    }
}
