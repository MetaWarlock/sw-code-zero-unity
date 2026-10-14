namespace Darkwing.Core
{
    /// <summary>
    /// Object slot machinery mirroring the original: 16 slots, stride-$10 field sets
    /// (RAM $0400-$059F), update order alternating by frame parity ($95: even frames
    /// slots 0→15, odd frames 15→0), camera culling ($802F: absolute X/Y vs scroll
    /// with a 16px window; persistent anim ids $13,$14,$17,$18,$19,$29 exempt),
    /// and the placement spawner (bank 4 $8090: 5 parallel arrays, activates into
    /// free slots 15..4 when the object's screen scrolls into view).
    /// Behavior execution is delegated to EnemyBehaviors (bank 6) / BossBehaviors
    /// (bank 1) through EnemyDispatcher.
    /// </summary>
    public sealed class ObjectSystem
    {
        public const int SlotCount = 16;

        public sealed class Slot
        {
            public bool Active;          // $04D0 bit7
            public byte Flags;           // $0400 (bit7 on-screen, bit6 h-flip, bit4 overlap ref, bit3 compose off, bit2 pass-through)
            public byte AnimNumber;      // $0420
            public byte AnimTimer;       // $0430
            public byte AnimFrame;       // $0410
            public byte YFrac;           // $0460 Y subpixel accumulator
            public byte XFrac;           // $0470 X subpixel accumulator
            public byte T480;            // $0480 behavior-local timer
            public byte T490;            // $0490 behavior-local timer/counter
            public byte T4A0;            // $04A0 behavior-local counter (often HP latch)
            public byte T4B0;            // $04B0 behavior-local counter/phase
            public byte T4AE;            // $04AE hop counter (Bushroot) / lit-post index (Moliarty)
            public byte T4AC;            // $04AC (Moliarty scratch)
            public byte T4AF;            // $04AF live-child counter (Wolfduck/Bushroot)
            public byte T4BC;            // $04BC flamethrower round-robin period
            public byte T4BD;            // $04BD Moliarty post index
            public byte StateLow;        // $04D0 low bits
            public byte Behavior;        // $04E0
            public ushort X;             // $04F0/$0500 absolute
            public ushort Y;             // $0510/$0520 absolute
            public byte XVelFrac;        // $0530
            public byte XVelInt;         // $0540
            public sbyte YVelInt;        // $0560 (up-positive)
            public byte YVelFrac;        // $0550
            public byte Direction;       // $0580
            public byte Health;          // $05A0
            public byte PlacementIndex;  // $0590 (FF = none)
            public byte ScreenX;         // $0450,X relative to scroll
            public byte ScreenY;         // $0440,X relative to scroll
            public byte BehaviorParam;   // $0570,X (low 5 bits = touch class)
            public byte HitFlash;        // $04C0,X hit-flash timer
        }

        public readonly Slot[] Slots = new Slot[SlotCount];
        private readonly GameData _data;
        private byte _parity; // $95
        private readonly byte[] _spawnedMask = new byte[32]; // $0340 already-spawned bitmask

        public ObjectSystem(GameData data)
        {
            _data = data;
            for (int i = 0; i < SlotCount; i++) Slots[i] = new Slot();
            Slots[0].Active = true; // player slot
        }

        public int Parity => _parity;

        /// <summary>Iterate slots in the original's alternating order.</summary>
        public System.Collections.Generic.IEnumerable<(int index, Slot slot)> UpdateOrder()
        {
            if ((_parity & 1) == 0)
            {
                for (int i = 0; i < SlotCount; i++) yield return (i, Slots[i]);
            }
            else
            {
                for (int i = SlotCount - 1; i >= 0; i--) yield return (i, Slots[i]);
            }
        }

        /// <summary>Camera cull ($802F-$80C6): deactivate offscreen objects.</summary>
        public void CullAgainstCamera(ushort scrollX, ushort scrollY, PlayerCore player)
        {
            for (int i = 1; i < SlotCount; i++)
            {
                var s = Slots[i];
                if (!s.Active) continue;
                int anim = s.AnimNumber;
                bool persistent = anim is 0x13 or 0x14 or 0x17 or 0x18 or 0x19 or 0x29;
                if (persistent) continue;
                int dx = (short)(s.X - scrollX);
                if (dx < -16 || dx > 256 + 16)
                {
                    s.Active = false;
                    s.StateLow = 0;
                    s.PlacementIndex = 0xFF;
                }
            }
        }

        /// <summary>$FC29 mark a placement index in the $0340 bitmask.</summary>
        public void MarkSpawned(byte placementIndex)
        {
            _spawnedMask[placementIndex >> 3] |= (byte)(1 << (placementIndex & 7));
        }

        public bool IsPlacementSpawned(byte placementIndex)
        {
            return (_spawnedMask[placementIndex >> 3] & (1 << (placementIndex & 7))) != 0;
        }

        /// <summary>Raw $0340-mask read (behavior 29's $0301-class checks use indexes).</summary>
        public byte SpawnedBitmask(int cell) => _spawnedMask[cell & 31];

        /// <summary>
        /// Spawn objects whose placement record scrolled into view ($8090).
        /// placement arrays come from GameData.Levels[level].Objects (5-byte records:
        /// screenX, localX, screenY, localY, type). Spawned objects load
        /// Flags/Behavior/AnimNumber/BehaviorParam/Health from the bank-4 type
        /// tables ($8AB3 = [flags, behavior, anim, param] × 105, $8C57 = HP × 105)
        /// via EnemyBehaviors.TryGetType.
        /// </summary>
        public void SpawnFromPlacements(GameData.LevelDef level, ushort scrollX, ushort scrollY)
        {
            foreach (var o in level.Objects)
            {
                // $80DA: the placement is live when BOTH page differences are exactly 0,
                // i.e. the 16-bit (placement − scroll) difference is in [0x0000, 0x00FF]
                // for X and the same window for Y ($FA/$FB, with the $80F2/$80F4 borrow
                // that costs one extra row). So an object spawns when it enters the
                // screen-width window ahead of the camera, one cell BEFORE $FD advances.
                ushort wx = (ushort)((o.ScreenX << 8) | o.X);
                ushort wy = (ushort)((o.ScreenY << 8) | o.Y);
                if (!InPlacementWindow(wx, wy, scrollX, scrollY)) continue;
                if (IsSpawned(o.Index)) continue;                 // $8101/$8110: collected, not consumed-by-spawn
                // $8112-$8124: a placement already held by a live slot is not re-spawned.
                bool held = false;
                for (int i = SlotCount - 1; i >= 4; i--)
                    if (Slots[i].PlacementIndex == (byte)o.Index && Slots[i].Active)
                    { held = true; break; }
                if (held) continue;
                int slot = FindFreeSlot();
                if (slot < 0) return; // pool exhausted ($8132 RTS)
                var s = Slots[slot];
                s.Active = true;
                s.X = wx;
                s.Y = wy;
                s.PlacementIndex = (byte)o.Index;
                // $8090 sets NO $0340 bit: the mask is written by the pickup behaviors
                // (bank6 $883C/$8A1F → $FC29) when an item is collected, so an enemy that
                // is culled or killed releases $0590 and re-spawns when the window returns.
                // $F7EE: face the player — $01 when the player is to the right, $02 left.
                s.Direction = s.X >= Slots[0].X ? (byte)0x02 : (byte)0x01;
                if (EnemyBehaviors.TryGetType(o.Type, out byte flags, out byte behavior,
                                              out byte anim, out byte param, out byte hp))
                {
                    s.Flags = flags;             // $0400 ← $8AB3+0
                    s.Behavior = behavior;       // $04E0 ← $8AB3+1
                    s.AnimNumber = anim;         // $0420 ← $8AB3+2
                    s.BehaviorParam = param;     // $0570 ← $8AB3+3
                    s.Health = hp;               // $05A0 ← $8C57+type
                }
                else
                {
                    s.Behavior = (byte)o.Type; // fallback when no table is loaded
                    s.Health = 1;
                }
                s.StateLow = 0;
                s.T480 = 0; s.T490 = 0; s.T4A0 = 0; s.T4B0 = 0;
                s.AnimFrame = 0; s.AnimTimer = 0;
                s.XFrac = 0; s.YFrac = 0; s.HitFlash = 0;      // $81D5-$81E7
            }
        }

        /// <summary>$80DA: the placement/scroll page differences the spawner tests.
        /// Both must be zero, with the low-byte borrow feeding the high-byte SBC and
        /// the $80F2/$80F4 pair costing one extra row when the Y low difference borrows.</summary>
        private static bool InPlacementWindow(ushort px, ushort py, ushort sx, ushort sy)
        {
            int dx = (px & 0xFF) - (sx & 0xFF);
            if (((px >> 8) - (sx >> 8) - (dx < 0 ? 1 : 0)) != 0) return false;
            int dy = (py & 0xFF) - (sy & 0xFF);
            return ((py >> 8) - (sy >> 8) - (dy < 0 ? 1 : 0)) == 0;
        }

        /// <summary>Restore the $0340 already-spawned mask from a captured frame so a
        /// seeded golden run does not re-spawn placements the original consumed.</summary>
        public void SeedSpawnMask(byte[] ram)
        {
            System.Array.Copy(ram, 0x340, _spawnedMask, 0, 32);
        }

        /// <summary>$DB99-$DBB1: hanging on a hook clears object slots 15..2
        /// (slot 1 is left alive, slot 0 is the player).</summary>
        public void ClearForHookRide()
        {
            for (int i = SlotCount - 1; i >= 2; i--)
            {
                var s = Slots[i];
                s.Active = false;   // $04D0
                s.Flags = 0;        // $0400
                s.BehaviorParam = 0; // $0570
                s.HitFlash = 0;     // $04C0
                s.PlacementIndex = 0xFF; // $0590
            }
        }

        /// <summary>$C235 (JSR'd by $C248 from the section loader and $DE24): writes
        /// $0400/$04D0=0 and $0590=$FF for slots 15..1. DEX/BNE exits at X=0, so the
        /// player slot is never cleared — $E3F5/$E3FA then re-seeds it explicitly.</summary>
        public void ClearForSectionLoad()
        {
            for (int i = SlotCount - 1; i >= 1; i--)
            {
                var s = Slots[i];
                s.Flags = 0;           // $0400
                s.Active = false;      // $04D0
                s.PlacementIndex = 0xFF; // $0590
            }
        }

        private bool IsSpawned(int placementIndex)
        {
            for (int i = 1; i < SlotCount; i++)
                if (Slots[i].Active && Slots[i].PlacementIndex == placementIndex) return true;
            return false;
        }

        private int FindFreeSlot()
        {
            for (int i = SlotCount - 1; i >= 4; i--)
                if (!Slots[i].Active) return i;
            return -1;
        }

        public void EndFrame() => _parity++;
    }
}
