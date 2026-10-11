namespace Darkwing.Core
{
    /// <summary>
    /// Object slot machinery mirroring the original: 16 slots, stride-$10 field sets
    /// (RAM $0400-$059F), update order alternating by frame parity ($95: even frames
    /// slots 0→15, odd frames 15→0), camera culling ($802F: absolute X/Y vs scroll
    /// with a 16px window; persistent anim ids $13,$14,$17,$18,$19,$29 exempt),
    /// and the placement spawner (bank 4 $8090: 5 parallel arrays, activates into
    /// free slots 15..4 when the object's screen scrolls into view).
    /// Behavior execution itself is delegated to IObjectBehavior implementations (M6).
    /// </summary>
    public sealed class ObjectSystem
    {
        public const int SlotCount = 16;

        public sealed class Slot
        {
            public bool Active;          // $04D0 bit7
            public byte Flags;           // $0400
            public byte AnimNumber;      // $0420
            public byte AnimTimer;       // $0430
            public byte AnimFrame;       // $0410
            public byte StateLow;        // $04D0 low bits
            public byte Behavior;        // $04E0
            public ushort X;             // $04F0/$0500 absolute
            public ushort Y;             // $0510/$0520 absolute
            public sbyte YVelInt;        // $0560 (up-positive)
            public byte YVelFrac;        // $0550
            public byte Direction;       // $0580
            public byte Health;          // $05A0
            public byte PlacementIndex;  // $0590 (FF = none)
        }

        public readonly Slot[] Slots = new Slot[SlotCount];
        private readonly GameData _data;
        private byte _parity; // $95

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

        /// <summary>
        /// Spawn objects whose placement record scrolled into view ($8090).
        /// placement arrays come from GameData.Levels[level].Objects (5-byte records:
        /// screenX, localX, screenY, localY, type).
        /// </summary>
        public void SpawnFromPlacements(GameData.LevelDef level, ushort scrollX, ushort scrollY)
        {
            foreach (var o in level.Objects)
            {
                // Verified activation rule (level_format.md §7): spawn when the
                // object's cell column == scroll column ($FD) and its grid row == $FB.
                int cellX = o.ScreenX, rowY = o.ScreenY;
                int scrollCol = (scrollX >> 8) & 0xFFFF; // $FD = cell column
                int scrollRow = scrollY >> 8;
                if (cellX != scrollCol || rowY != scrollRow) continue;
                ushort wx = (ushort)((o.ScreenX << 8) | o.X);
                ushort wy = (ushort)((o.ScreenY << 8) | o.Y);
                if (IsSpawned(o.Index)) continue;
                int slot = FindFreeSlot();
                if (slot < 0) return; // pool exhausted ($8132 RTS)
                var s = Slots[slot];
                s.Active = true;
                s.X = wx;
                s.Y = wy;
                s.PlacementIndex = (byte)o.Index;
                s.Behavior = (byte)o.Type; // type→behavior mapping lands with M6 catalog
                s.Health = 1;
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
