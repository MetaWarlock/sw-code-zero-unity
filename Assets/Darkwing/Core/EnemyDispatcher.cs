namespace Darkwing.Core
{
    /// <summary>
    /// Enemy behavior dispatch, transcription of bank 6 $8000: per active slot 1-15,
    /// behavior id ($04E0) &lt; $47 jumps through the $825F/$82A7 pointer table;
    /// then per-slot post-processing ($8032-$816C): hit-flash timer ($04C0 drives
    /// the compose-disable bit via $0400 bit3), placement bookkeeping.
    /// Behavior routine bodies live in EnemyBehaviors (ported per
    /// docs/enemies_bosses.md and object_catalog.csv); ids >= $47 route to the
    /// boss dispatcher (bank 1 $8800).
    /// </summary>
    public sealed class EnemyDispatcher
    {
        private readonly byte[] _bank6;
        private readonly ObjectSystem _objects;
        private readonly PlayerCore _player;
        private readonly Darkwing.Audio.AudioSequencer _audio;
        public System.Action<int> Sfx;

        public EnemyDispatcher(byte[] bank6, ObjectSystem objects, PlayerCore player,
                               Darkwing.Audio.AudioSequencer audio = null)
        {
            _bank6 = bank6;
            _objects = objects;
            _player = player;
            _audio = audio;
        }

        /// <summary>Per-frame pass (the master loop's bank-6 task).</summary>
        public void RunFrame()
        {
            _player.GravityFrac = 0x40; // $99 = $40 set by the bank6/bank1 dispatchers
            for (int slot = 1; slot < ObjectSystem.SlotCount; slot++)
            {
                var s = _objects.Slots[slot];
                if (!s.Active) continue;                     // $04D0 bit7
                byte behavior = s.Behavior;
                if (behavior >= 0x47) continue;              // bosses route via bank 1
                EnemyBehaviors.Dispatch(slot, s, behavior, this);
                PostProcess(s);
            }
        }

        /// <summary>$8032-$813B per-slot post-processing.</summary>
        private void PostProcess(ObjectSystem.Slot s)
        {
            if (!s.Active) return;
            // $8032: $0570,X bit6 gate for the flash/compose handling
            if ((s.BehaviorParam & 0x40) != 0)
            {
                if (s.HitFlash != 0)
                {
                    if ((s.HitFlash & 0x80) == 0) s.Flags |= 0x08;     // compose off
                    else s.Flags &= 0xF7;                              // compose on
                    s.HitFlash--;
                }
            }
        }
    }
}
