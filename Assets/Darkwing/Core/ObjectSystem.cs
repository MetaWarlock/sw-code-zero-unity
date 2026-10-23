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
    public sealed partial class ObjectSystem
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
            InitPlacementLists(data?.Levels);
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
        public void ClearSpawnMask() => System.Array.Clear(_spawnedMask, 0, _spawnedMask.Length);

        /// <summary>$70/$71. The spawner never scans the whole list: it walks entry
        /// indexes from a cursor and stops at the FIRST entry outside the window, so
        /// out-of-order entries behind it are not considered this pass.</summary>
        public byte CursorFwd;
        public byte CursorRev;

        /// <summary>$DE42-$DE44, part of the $DE24 loader reset.</summary>
        public void ResetPlacementCursors() { CursorFwd = 0; CursorRev = 0; }

        /// <summary>
        /// $72-$7B. Bank-4's <c>$8000</c> (<c>ObjectPointerInit</c>) points the five
        /// parallel placement arrays at the lists of world <c>$31</c>, and the spawner
        /// only ever dereferences those pointers. It runs once per world load: the city
        /// capture holds $8374/$8338/$83EC/$83B0/$8428 ($31=$01) from f1897, the frame the
        /// mission starts, to the end of the route and nowhere earlier — the title and map
        /// frames keep the power-on 0000, so a later $31 change alone never re-points them.
        /// The world-load path in <c>DarkwingGame</c> is the port's only caller.
        /// </summary>
        public GameData.LevelDef Placements { get; private set; }

        /// <summary>The world index $8000 last used (LDA $81F3..$8232,X with X=$31).</summary>
        public int PlacementListIndex { get; private set; } = -1;

        private GameData.LevelDef[] _placementLists;

        /// <summary>Supplies the per-world lists $8000 selects between; index = $31.</summary>
        public void InitPlacementLists(GameData.LevelDef[] lists)
        {
            _placementLists = lists;
            SelectPlacementList(0);                         // the boot-time call with $31=$00
        }

        /// <summary>$8000-$8034: LDX $31, then the five pointer pairs.</summary>
        public void SelectPlacementList(int worldId)
        {
            if (_placementLists == null) return;
            if (worldId < 0 || worldId >= _placementLists.Length) return;
            PlacementListIndex = worldId;
            Placements = _placementLists[worldId];
        }

        /// <summary>The X-lo array pointers bank-4 keeps at $8201/$8208 (lo,hi per $31).</summary>
        private static readonly byte[] ListXloPtrLo = { 0x6C, 0x74, 0xA8, 0xEE, 0x16, 0x95, 0xCB };
        private static readonly byte[] ListXloPtrHi = { 0x82, 0x83, 0x84, 0x85, 0x87, 0x88, 0x89 };

        /// <summary>Selects the list from a $72/$73 pointer pair (used to seed a capture).</summary>
        public bool SelectPlacementListByPointer(byte lo, byte hi)
        {
            for (int i = 0; i < ListXloPtrLo.Length; i++)
                if (ListXloPtrLo[i] == lo && ListXloPtrHi[i] == hi)
                {
                    SelectPlacementList(i);
                    return true;
                }
            return false;
        }

        /// <summary>
        /// $8040, the re-anchor $DE93 runs after the staging loop when $31 &lt; 7.
        /// Pass 1 takes the first entry whose row equals $FB with X &gt;= scroll X
        /// ($804B/$804F SBC/SBC, found on BCS = no borrow); if none, pass 2 takes the
        /// first entry in that row with X &lt; scroll+0xFF ($8058-$8076). Neither
        /// happening, INY wraps Y to 0 and $8056/$807B store 0 into both cursors.
        /// </summary>
        public void AnchorPlacementCursors(ushort scrollX, byte scrollRow)
        {
            var level = Placements;
            if (level == null) return;
            int n = System.Math.Min(level.ObjectCount, 256);
            for (int y = 0; y < n; y++)
            {
                if (level.Objects[y].ScreenY != scrollRow) continue;
                if (PlacementX(level, y) >= scrollX) { CursorFwd = CursorRev = (byte)y; return; }
            }
            ushort ahead = (ushort)(scrollX + 0xFF);        // $8058-$8063: $FC + $FF with the carry into $FD
            for (int y = 0; y < n; y++)
            {
                if (level.Objects[y].ScreenY != scrollRow) continue;
                if (PlacementX(level, y) < ahead) { CursorFwd = CursorRev = (byte)y; return; }
            }
            CursorFwd = 0;
            CursorRev = 0;
        }

        /// <summary>
        /// $8090: the placement spawner. The scroll state byte $2F picks the pass —
        /// 0 = no pass at all (standing still spawns nothing), 1/4/8 = forward from $70
        /// (walk right, elevator down/up), 2 and any other value = reverse from $71.
        /// Entries come from the five parallel arrays $72/$74/$76/$78/$7A point at, so
        /// the walk reads <see cref="Placements"/> — the list $8000 selected, not the
        /// world currently loaded. The entry index is the placement index ($0590/$0340).
        /// </summary>
        public void SpawnFromPlacements(ushort scrollX, ushort scrollY, byte scrollState)
        {
            LastPlacementSpawnCycles = TryMeasurePlacementSpawnCycles(scrollX, scrollY, scrollState, out int cycles, out string reason) ? cycles : -1;
            PlacementSpawnTimingUnsupportedReason = reason;
            var level = Placements;
            if (level == null) return;
            switch (scrollState & 0x0F)                     // $8090/$8092
            {
                case 0: return;                             // $80BE RTS
                case 1: case 4: case 8:                     // $80BF forward
                    ForwardPass(level, scrollX, scrollY);
                    break;
                default:                                    // $80A6 reverse
                    ReversePass(level, scrollX, scrollY);
                    break;
            }
        }

        private void ForwardPass(GameData.LevelDef level, ushort sx, ushort sy)
        {
            int y = CursorFwd;
            while (y < level.ObjectCount && InWindow(level, y, sx, sy))
            {
                TryActivate(level, y, sx, sy);              // $80FC
                y++;                                        // $80C9 INC $70
                if (y == 256) break;                        // $80CB BNE exits on the wrap
            }
            CursorFwd = (byte)y;
            if (y == 0) { CursorRev = 0; return; }          // $80CD BEQ $80D7
            int r = y;
            for (int steps = 0; steps < 256; steps++)       // $80D1 DEY / $80D5 BEQ $80D1
            {
                r--;
                if (r < 0 || !InWindow(level, r, sx, sy)) break;
            }
            CursorRev = (byte)(r & 0xFF);                   // $80D7 STY $71
        }

        private void ReversePass(GameData.LevelDef level, ushort sx, ushort sy)
        {
            int y = CursorRev;
            while (y < level.ObjectCount && InWindow(level, y, sx, sy))
            {
                TryActivate(level, y, sx, sy);
                y--;                                        // $80B0 DEC $71
                if (y < 0) { y = 255; }                     // the ROM wraps and keeps walking
                if (y == 0) break;                          // $80B2 BNE exits when $71 lands on 0
                if (y >= level.ObjectCount) break;          // wrapped past the list
            }
            CursorRev = (byte)y;
            int f = y;                                      // $80B4 LDY $71 / $80B6 INY
            for (int steps = 0; steps < 256; steps++)
            {
                f++;
                if (f == 256) { f = 0; break; }
                if (!InWindow(level, f, sx, sy)) break;
            }
            CursorFwd = (byte)f;                            // $80BC STY $70
        }

        private ushort PlacementX(GameData.LevelDef level, int index)
            => (ushort)((level.Objects[index].ScreenX << 8) | level.Objects[index].X);

        /// <summary>$80DA: the entry is inside the window when both 16-bit
        /// (placement − scroll) differences lie in [0x0000, 0x00FF]. The Y low compare
        /// carries an extra dead <c>SBC #$0F</c> ($80F2) whose result is dropped by the
        /// unconditional CLC, so the borrow is what reaches the row compare.</summary>
        private bool InWindow(GameData.LevelDef level, int index, ushort sx, ushort sy)
        {
            if (index < 0 || index >= level.ObjectCount) return false;
            var o = level.Objects[index];
            return InPlacementWindow(PlacementX(level, index),
                                     (ushort)((o.ScreenY << 8) | o.Y), sx, sy);
        }

        /// <summary>$80FC: try to hand entry <paramref name="index"/> to a slot.</summary>
        private void TryActivate(GameData.LevelDef level, int index, ushort sx, ushort sy)
        {
            if ((_spawnedMask[index >> 3] & (1 << (index & 7))) != 0) return;   // $810B/$8110 collected
            for (int x = SlotCount - 1; x >= 5; x--)              // $8112: slots 15..5, never 4
                if (Slots[x].PlacementIndex == (byte)index)
                {
                    if (Slots[x].Active) return;                   // $8121 BMI $80FB
                    break;                                         // dead holder: allocate anyway
                }
            int slot = -1;
            for (int x = SlotCount - 1; x >= 5; x--)              // $8126 free-slot walk
                if (!Slots[x].Active) { slot = x; break; }
            if (slot < 0) return;                                  // $8132 RTS, pool exhausted
            Activate(level, index, slot);
        }

        /// <summary>$8194-$81C5: these animation ids are not facing-dependent, so the
        /// spawn does not run $F808 (they are the pickups/particles that keep $0400 bit6).</summary>
        private static readonly byte[] NoFlipAnims =
            { 0x1C, 0x1D, 0x1E, 0x22, 0x23, 0x24, 0x25, 0x26, 0x27, 0x28, 0x9B, 0x56 };

        /// <summary>
        /// $8133-$81EA: hand the entry to a slot. The four coordinate bytes the $80DA
        /// compare already read go to $04F0/$0500/$0510/$0520, $0590 gets the entry
        /// index, the bank-4 type record ($8AB3[type*4] = flags, behavior, anim, param,
        /// with HP at $8C57[type*4]) fills $0400/$04E0/$0420/$0570/$05A0 and $04D0=$80
        /// activates it. $F7EE faces the slot at the player, $F808 mirrors that facing
        /// into $0400 bit6, $F5FC cuts the vertical velocity to FF,$AB and the tail
        /// zeroes $0410/$0430/$0460-$04C0.
        /// </summary>
        private void Activate(GameData.LevelDef level, int index, int slot)
        {
            var o = level.Objects[index];
            var s = Slots[slot];
            s.PlacementIndex = (byte)index;                        // $8134
            s.X = PlacementX(level, index);                        // $8139/$813E
            s.Y = (ushort)((o.ScreenY << 8) | o.Y);                // $8143/$8148
            if (EnemyBehaviors.TryGetType(o.Type, out byte flags, out byte behavior,
                                          out byte anim, out byte param, out byte hp))
            {
                s.Flags = flags;
                s.Behavior = behavior;
                s.AnimNumber = anim;
                s.BehaviorParam = param;
                s.Health = hp;
            }
            else
            {
                s.Behavior = (byte)o.Type;      // fallback when no table is loaded
                s.Health = 1;
            }
            s.Active = true; s.StateLow = 0;                       // $818C LDA #$80 / STA $04D0
            // $F7EE: $01 when the slot sits left of the player, $02 at or right of it.
            s.Direction = s.X >= Slots[0].X ? (byte)0x02 : (byte)0x01;
            bool flip = true;
            for (int i = 0; i < NoFlipAnims.Length; i++)
                if (NoFlipAnims[i] == s.AnimNumber) { flip = false; break; }
            if (flip)                                              // $F808
                s.Flags = (byte)((s.Flags & 0xBF) | ((s.Direction & 1) << 6));
            EnemyBehaviors.CutVelocity(s);                         // $F5FC
            s.AnimTimer = 0; s.AnimFrame = 0;                      // $81CF/$81D2
            s.YFrac = 0; s.XFrac = 0;                              // $81D5/$81D8
            s.T480 = 0; s.T490 = 0; s.T4A0 = 0; s.T4B0 = 0;        // $81DB-$81E4
            s.HitFlash = 0;                                        // $81E7
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
        /// (slot 1 is left alive, slot 0 is the player). $04D0 is written as a whole byte,
        /// so the low state nibble is cleared with the active bit.</summary>
        public void ClearForHookRide()
        {
            for (int i = SlotCount - 1; i >= 2; i--)
            {
                var s = Slots[i];
                s.Active = false;   // $04D0
                s.StateLow = 0;     // $04D0 low nibble
                s.Flags = 0;        // $0400
                s.BehaviorParam = 0; // $0570
                s.HitFlash = 0;     // $04C0
                s.PlacementIndex = 0xFF; // $0590
            }
        }

        /// <summary>$C235 (JSR'd by $C248 from the section loader and $DE24): writes
        /// $0400/$04D0=0 and $0590=$FF for slots 15..1. DEX/BNE exits at X=0, so the
        /// player slot is never cleared — $E3F5/$E3FA then re-seeds it explicitly.
        /// $04D0 is a whole-byte write, so the low state nibble goes to 0 as well.</summary>
        public void ClearForSectionLoad()
        {
            for (int i = SlotCount - 1; i >= 1; i--)
            {
                var s = Slots[i];
                s.Flags = 0;           // $0400
                s.Active = false;      // $04D0
                s.StateLow = 0;        // $04D0 low nibble
                s.PlacementIndex = 0xFF; // $0590
            }
        }

        public void EndFrame() => _parity++;
    }
}
