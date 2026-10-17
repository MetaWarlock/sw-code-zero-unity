using System;

namespace Darkwing.Core
{
    /// <summary>
    /// Enemy behavior routines — transcription of PRG bank 6 ($82EF-$B5F3),
    /// dispatched through the $825F/$82A7 pointer table (ids $00-$46). Each
    /// handler mirrors the original's per-frame state machine for its slot:
    /// state var $04D0 low bits, timers $0480-$04B0, signed 8.8 velocities
    /// ($0530/$0540 X, $0550/$0560 Y up-positive) integrated exactly like the
    /// fixed-bank helpers ($F4FD/$F51B X, $F539/$F55E/$F5A4/$F581 Y), tile
    /// probes via the $ED19/$ECAD offset tables with the $ED85/$ED9D/$EDB3/
    /// $EDD2 snaps, RNG via the $E4-$E7 counters (doc §4), SFX via ctx.Sfx
    /// (the $F84E/$F850 sites), spawns through the slot pool ($FA4C/$FA84/
    /// $FA9A). Bank-1 boss ids (&gt;= $47) route to BossBehaviors.
    /// </summary>
    public static class EnemyBehaviors
    {
        // ==================================================================
        // RNG ($E4-$E7 zero-page counters; doc §4). Advanced in place at each
        // use site with `state[i] += state[i-1] + carry`; masked by the caller.
        // Deterministic; serialized via EnemyDispatcher.SaveState.
        // ==================================================================
        public static byte RngE4, RngE5, RngE6, RngE7;

        public static void ResetRng()
        {
            RngE4 = RngE5 = RngE6 = RngE7 = 0; // RAM init (doc §4: zero at power-on)
        }

        // E5 += E4 + C ($8D2A, $8D3F, $9648, $A153, $ADCA, $B405)
        private static byte RngE5PlusE4(bool carry) { int v = RngE5 + RngE4 + (carry ? 1 : 0); RngE5 = (byte)v; return RngE5; }
        // E6 += E4 + C ($94FA)
        private static byte RngE6PlusE4(bool carry) { int v = RngE6 + RngE4 + (carry ? 1 : 0); RngE6 = (byte)v; return RngE6; }
        // E6 += E7 + C ($8785 debris second-roll)
        private static byte RngE6PlusE7(bool carry) { int v = RngE6 + RngE7 + (carry ? 1 : 0); RngE6 = (byte)v; return RngE6; }
        // E6 += E5 + C ($8745/$875F debris)
        private static byte RngE6PlusE5(bool carry) { int v = RngE6 + RngE5 + (carry ? 1 : 0); RngE6 = (byte)v; return RngE6; }
        // E7 += E6 + C ($AA69-region / $8785 second E6 update uses E6 += E7)
        private static byte RngE7PlusE6(bool carry) { int v = RngE7 + RngE6 + (carry ? 1 : 0); RngE7 = (byte)v; return RngE7; }
        // E5 += E4 - $0490,X (perturbed, $A7AB Bushroot — implemented at its site)
        // E4 += E5, E4 EOR $04F0 → E4/E6 (orb spawner $9447 — implemented at its site)
        private static void RngIncE6E7() { RngE6++; RngE7++; } // cheap bump (bank 6 $BAD0 class)

        // ---- per-site wrappers (BossBehaviors consumes these) ----
        internal static int RngE5Mask7() => RngE5PlusE4(false) & 7;                 // $9959 ball
        internal static int RngE5Mask15() => RngE5PlusE4(false);                    // $AF74/$AAEC-class
        internal static int RngE7PlusE6Wrap() { RngE6 = RngE7PlusE6(false); return RngE6; } // E6 += E7
        internal static int RngE4PlusE6() { int v = RngE4 + RngE6; RngE4 = (byte)v; return RngE4; } // $AF0F-class
        internal static int RngBushroot(int hop)                                    // $A7AB E5 += E4 - $0490
        {
            int v = (RngE5 + RngE4 - hop) & 0xFF;
            RngE5 = (byte)v;
            return RngE5;
        }
        internal static int RngOrbVolley(ushort xPos)                               // $9447 E4 += E5 EOR X
        {
            int v = (RngE4 + RngE5) & 0xFF;
            v ^= xPos & 0xFF;
            RngE4 = (byte)v;
            RngE6 = (byte)v;
            return RngE4;
        }

        /// <summary>$B36A boss flash wrapper for BossBehaviors call sites.</summary>
        internal static void HitFlashShared(EnemyDispatcher ctx, ObjectSystem.Slot s) => BossBehaviors.HitFlash(ctx, s);

        // ---- aim tables exposed for the boss flamethrower ($A5B7-family sectors) ----
        internal static byte AimFracShared(int sector) => ThrowerFrac[sector & 0x0F];
        internal static byte AimIntShared(int sector) => ThrowerInt[sector & 0x0F];
        internal static byte AimYFracShared(int sector) => ThrowerYF2[sector & 0x0F];
        internal static sbyte AimYIntShared(int sector) => (sbyte)ThrowerYI2[sector & 0x0F];

        // ==================================================================
        // Hitbox tables (half-sizes, px; indexed by $0570 & $1F) — doc §5.4.
        // ==================================================================
        public static readonly byte[] ProjectileYSize = // $FB39
        {
            0x0F,0x0F,0x0D,0x0F,0x07,0x0B,0x0F,0x13,0x07,0x0B,0x04,0x09,0x13,0x07,0x07,0x07,
            0x0B,0x13,0x13,0x0D,0x0D,0x0F,0x04,0x13,0x03,0x03,0x03,0x03,0x03,0x03,0x03,0x03
        };
        public static readonly byte[] ProjectileXSize = // $FB59
        {
            0x0E,0x0E,0x0A,0x12,0x0E,0x12,0x0E,0x06,0x06,0x0E,0x04,0x0E,0x0E,0x0A,0x06,0x0A,
            0x0A,0x0A,0x12,0x0A,0x0C,0x08,0x0C,0x03,0x0A,0x02,0x02,0x02,0x02,0x02,0x02,0x02
        };
        public static readonly byte[] TouchYHalf = // $FBE9
        {
            0x16,0x0C,0x12,0x16,0x0E,0x14,0x16,0x1C,0x16,0x12,0x0C,0x10,0x16,0x0E,0x14,0x0E,
            0x12,0x1A,0x1A,0x12,0x14,0x16,0x16,0x16,0x0E,0x0A,0x0A,0x0A,0x0A,0x0A,0x0A,0x0A
        };
        public static readonly byte[] TouchXHalf = // $FC09
        {
            0x16,0x0C,0x12,0x1A,0x0F,0x12,0x16,0x10,0x0C,0x16,0x0C,0x10,0x1A,0x12,0x13,0x12,
            0x12,0x16,0x12,0x12,0x10,0x14,0x1A,0x16,0x10,0x0A,0x0A,0x0A,0x0A,0x0A,0x0A,0x0A
        };

        // Probe offset tables (fixed bank), 108 bytes each: $ECAD Y / $ED19 X.
        // One table index selects BOTH offsets, and $EB52 advances the index by 1 per
        // sample, so a set is a run of points, not a triple of X with one Y.
        private static readonly byte[] ProbeX =
        {
            0x0C,0x0C,0x0C, 0xF4,0xF4,0xF4, 0xF5,0x00,0x0B, 0xF5,0x00,0x0B,
            0x02,0x02,0x02, 0xFE,0xFE,0xFE, 0xFF,0x01,0x01, 0xFF,0x01,0x01,
            0x0C,0x0C,0x0C, 0xF4,0xF4,0xF4, 0xFF,0x00,0x01, 0xFF,0x00,0x01,
            0x09,0x09,0xF7, 0xF7,0xF8,0x08, 0xF8,0x08,0x14, 0x14,0xEC,0xEC,
            0xFF,0x01,0xFF, 0x01,0x04,0x04, 0x04,0xFC,0xFC, 0xFC,0xFF,0x00,
            0x01,0xFF,0x00, 0x01,0xFD,0x03, 0xFD,0x03,0x0C, 0x0C,0x0C,0xF4,
            0xF4,0xF4,0xF5, 0x00,0x0B,0xF5, 0x00,0x0B,0x10, 0x10,0x10,0xF0,
            0xF0,0xF0,0xF1, 0x00,0x0F,0xF1, 0x00,0x0F,0x10, 0x10,0x10,0xF0,
            0xF0,0xF0,0xF9, 0x00,0x07,0xF9, 0x00,0x07,0xF9, 0x07,0xFD,0x03
        };
        private static readonly byte[] ProbeY =
        {
            0xF5,0x00,0x0B, 0xF5,0x00,0x0B, 0x0C,0x0C,0x0C, 0xF4,0xF4,0xF4,
            0xFF,0x01,0x01, 0xFF,0x01,0x01, 0x02,0x02,0x02, 0xFE,0xFE,0xFE,
            0xFF,0x00,0x01, 0xFF,0x00,0x01, 0x02,0x02,0x02, 0xFE,0xFE,0xFE,
            0xF9,0x07,0xF9, 0x07,0x08,0x08, 0xF8,0xF8,0xFC, 0x04,0xFC,0x04,
            0x02,0x02,0xFE, 0xFE,0xFD,0x00, 0x03,0xFD,0x00, 0x03,0xEC,0xEC,
            0xEC,0xFD,0xFD, 0xFD,0x10,0x10, 0xF0,0xF0,0xFD, 0x00,0x03,0xFD,
            0x00,0x03,0x04, 0x04,0x04,0xFC, 0xFC,0xFC,0xF1, 0x00,0x0F,0xF1,
            0x00,0x0F,0x10, 0x10,0x10,0xF0, 0xF0,0xF0,0xF9, 0x00,0x07,0xF9,
            0x00,0x07,0x08, 0x08,0x08,0xF8, 0xF8,0xF8,0x08, 0x08,0xFC,0xFC
        };

        // 24-sector direction → direction code table ($F9A2, 32 entries).
        private static readonly byte[] SectorTable =
        {
            0x04,0x05,0x06,0x04, 0x08,0x07,0x06,0x04, 0x0C,0x0B,0x0A,0x04, 0x08,0x09,0x0A,0x04,
            0x04,0x03,0x02,0x04, 0x00,0x01,0x02,0x04, 0x0C,0x0D,0x0E,0x04, 0x00,0x0F,0x0E,0x04
        };
        // Sector code → direction bits ($9F3C; sectors >= $10 wrap — see note at SectorToPlayer).
        private static readonly byte[] SectorDir = { 0x08,0x09,0x09,0x09,0x01,0x05,0x05,0x05,0x04,0x06,0x06,0x06,0x02,0x0A,0x0A,0x0A };

        // RNG result tables.
        private static readonly byte[] WalkerDelay = { 0x1E,0x3C,0x1E,0x78,0x78,0x1E,0x3C,0x1E };   // $8D37
        private static readonly byte[] WalkerHopYF = { (byte)0xDC,(byte)0x8C,(byte)0xDC,(byte)0x8C }; // $8D55
        private static readonly byte[] WalkerHopYI = { 0x03,0x05,0x03,0x05 };                          // $8D59
        private static readonly byte[] HopperDelay = { 0x1E,0x3C,0x5A,0x1E };                          // $9507
        private static readonly byte[] GrabberDelay = { 0x78,0x1E,0x3C,0x78,0x3C,0x1E,0x1E,0x3C };    // $9655
        private static readonly byte[] SwimmerWait = { 0x1E,0x3C,0x0F,0x3C };                          // $A160
        private static readonly byte[] DrifterDelay = { 0x40,0x30,0x40,0x30 };                         // $ADE6 (timer) / $ADEA (frac) / $ADEE (int)
        private static readonly byte[] DrifterFrac  = { (byte)0x80,0x00,(byte)0x80,0x00 };
        private static readonly byte[] DrifterInt   = { 0x01,0x02,0x01,0x02 };
        private static readonly byte[] DebrisVelFrac = { 0x1C,0x23,0x22,0x24,0x1C,0x1D,0x25,0x23,0x24,0x1C,0x1D,0x22,0x1D,0x25,0x1D,0x22,0x24,0x23,0x1C,0x22,0x25,0x23,0x25,0x1D,0x1D,0x25,0x23,0x1C,0x23,0x1D,0x22,0x1D }; // $87A2
        private static readonly byte[] DebrisVelFrac2 = { 0x1C,0x23,0x25,0x24,0x1C,0x1D,0x25,0x1C,0x1C,0x1C,0x1D,0x25,0x24,0x25,0x1D,0x25,0x24,0x23,0x1C,0x25,0x1D,0x23,0x25,0x1D,0x1D,0x25,0x23,0x1C,0x23,0x1D,0x25,0x1C }; // $87C2
        private static readonly byte[] FlyerDiveFrac = { 0x00,(byte)0x87,(byte)0xD4,(byte)0xB2,0x00,(byte)0xB2,(byte)0xD4,(byte)0x87,0x00,(byte)0x87,(byte)0xD4,(byte)0xB2,0x00,(byte)0xB2,(byte)0xD4,(byte)0x87 }; // $9EFC
        private static readonly byte[] FlyerDiveInt  = { 0x00,0x01,0x02,0x03,0x04,0x03,0x02,0x01,0x00,0x01,0x02,0x03,0x04,0x03,0x02,0x01 }; // $9F0C
        private static readonly byte[] FlyerRiseFrac = { 0x00,(byte)0xB2,(byte)0xD4,(byte)0x87,0x00,(byte)0x87,(byte)0xD4,(byte)0xB2,0x00,(byte)0xB2,(byte)0xD4,(byte)0x87,0x00,(byte)0x87,(byte)0xD4,(byte)0xB2 }; // $9F1C
        private static readonly byte[] FlyerRiseInt  = { 0x04,0x03,0x02,0x01,0x00,0x01,0x02,0x03,0x04,0x03,0x02,0x01,0x00,0x01,0x02,0x03 }; // $9F2C
        private static readonly byte[] ThrowerFrac = { 0x00,0x24,0x1F,(byte)0xC5,0x00,(byte)0xC5,0x1F,0x24,0x00,0x24,0x1F,(byte)0xC5,0x00,(byte)0xC5,0x1F,0x24 }; // $A799
        private static readonly byte[] ThrowerInt  = { 0x00,0x01,0x02,0x02,0x03,0x02,0x02,0x01,0x00,0x01,0x02,0x02,0x03,0x02,0x02,0x01 }; // $A7A9
        private static readonly byte[] ThrowerYF2  = { 0x00,(byte)0xC5,0x1F,0x24,0x00,0x24,0x1F,(byte)0xC5,0x00,(byte)0xC5,0x1F,0x24,0x00,0x24,0x1F,(byte)0xC5 }; // $A7B9
        private static readonly byte[] ThrowerYI2  = { 0x03,0x02,0x02,0x01,0x00,0x01,0x02,0x02,0x03,0x02,0x02,0x01,0x00,0x01,0x02,0x02 }; // $A7C9
        private static readonly byte[] HeavyVelFrac = { 0x00,(byte)0xFF,0x14,(byte)0xFF,0x4B,(byte)0xFF,(byte)0x9F,(byte)0xFF,0x00,0x00,0x61,0x00,(byte)0xB5,0x00,(byte)0xEC,0x00,0x00,0x01,(byte)0xEC,0x00,(byte)0xB5,0x00,0x61,0x00,0x00,0x00,(byte)0x9F,(byte)0xFF,0x4B,(byte)0xFF,0x14,(byte)0xFF }; // $8E55 (8.8 frac,hi per entry)
        private static readonly byte[] HeavyVelInt  = { 0x00,0x00,0x61,0x00,(byte)0xB5,0x00,(byte)0xEC,0x00,0x00,0x01,(byte)0xEC,0x00,(byte)0xB5,0x00,0x61,0x00,0x00,0x00,(byte)0x9F,(byte)0xFF,0x4B,(byte)0xFF,0x14,(byte)0xFF,0x00,(byte)0xFF,0x14,(byte)0xFF,0x4B,(byte)0xFF,(byte)0x9F,(byte)0xFF }; // $8E75
        private static readonly byte[] HeavyPhase  = { 0x09,0x0A,0x0B,0x0C,0x0D,0x0E,0x0F,0x01,0x02,0x03,0x04,0x05,0x06,0x07,(byte)0xBD,(byte)0xD0 }; // $8E95
        private static readonly byte[] SwimmerWalkLen = { (byte)0x98,0x78,0x58,(byte)0xC8 }; // $9420
        private static readonly byte[] AimFrac  = { 0x00,0x61,(byte)0xB5,(byte)0xEC,0x00,(byte)0xEC,(byte)0xB5,0x61,0x00,0x61,(byte)0xB5,(byte)0xEC,0x00,(byte)0xEC,(byte)0xB5,0x61 }; // $B193
        private static readonly byte[] AimInt   = { 0x00,0x00,0x00,0x00,0x01,0x00,0x00,0x00,0x01,0x00,0x00,0x00,0x01,0x00,0x00,0x00 }; // $B1A3
        private static readonly byte[] AimYFrac = { 0x00,(byte)0xEC,(byte)0xB5,0x61,0x00,0x61,(byte)0xB5,(byte)0xEC,0x00,(byte)0xEC,(byte)0xB5,0x61,0x00,0x61,(byte)0xB5,(byte)0xEC }; // $B1B3
        private static readonly byte[] AimYInt  = { 0x01,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x01,0x00,0x00,0x00,0x00,0x00,0x00,0x00 }; // $B1C3
        private static readonly byte[] BombHopFrac = { (byte)0xDC,(byte)0xBE,(byte)0x80,0x03 }; // $B5EB
        private static readonly byte[] BombHopInt  = { 0x03,0x01,0x00,0x00 };                   // $B5EE
        private static readonly byte[] BallBounceFrac = { (byte)0xDC,(byte)0x8C,(byte)0xEA,(byte)0x8C }; // $B427
        private static readonly byte[] BallBounceInt  = { 0x03,0x05,0x07,0x05 };                          // $B42B

        // ==================================================================
        // Object type table (bank 4 $8AB3/$8C57; 105 records [flags, behavior,
        // anim, param] + 105 HP). Loaded from Assets/Darkwing/Data/object_types.bytes.
        // ==================================================================
        public static byte[] TypeFlags = new byte[0];
        public static byte[] TypeBehavior = new byte[0];
        public static byte[] TypeAnim = new byte[0];
        public static byte[] TypeParam = new byte[0];
        public static byte[] TypeHp = new byte[0];

        public static void LoadTypeTable(byte[] data)
        {
            if (data == null || data.Length < 525) return;
            TypeFlags = new byte[105]; TypeBehavior = new byte[105];
            TypeAnim = new byte[105]; TypeParam = new byte[105]; TypeHp = new byte[105];
            for (int t = 0; t < 105; t++)
            {
                TypeFlags[t] = data[t * 4];
                TypeBehavior[t] = data[t * 4 + 1];
                TypeAnim[t] = data[t * 4 + 2];
                TypeParam[t] = data[t * 4 + 3];
                TypeHp[t] = data[420 + t];
            }
        }

        /// <summary>Type table row for a placed type ($FF = terminator, never an object).</summary>
        public static bool TryGetType(int type, out byte flags, out byte behavior, out byte anim, out byte param, out byte hp)
        {
            flags = behavior = anim = param = hp = 0;
            if (type < 0 || type >= 105) return false;
            flags = TypeFlags.Length == 105 ? TypeFlags[type] : (byte)0xD0;
            behavior = TypeBehavior.Length == 105 ? TypeBehavior[type] : (byte)0;
            anim = TypeAnim.Length == 105 ? TypeAnim[type] : (byte)0;
            param = TypeParam.Length == 105 ? TypeParam[type] : (byte)0;
            hp = TypeHp.Length == 105 ? TypeHp[type] : (byte)1;
            return true;
        }

        // ==================================================================
        // Dispatch (bank 6 $8000 target table)
        // ==================================================================
        public static void Dispatch(int slot, ObjectSystem.Slot s, byte behavior, EnemyDispatcher ctx)
        {
            switch (behavior)
            {
                case 0x00: case 0x44: case 0x45: break;                    // $82EF RTS no-op
                case 0x01: GasCloud(s, ctx); break;                        // $82F0 normal gas cloud
                case 0x02: GasLob(s, ctx); break;                          // $8385 lobbed gas
                case 0x03: ThunderBolt(s, ctx); break;                     // $841C thunder bolt
                case 0x04: ArrowBomb(s, ctx); break;                       // $85BC arrow/bomb
                case 0x05: DeathPop(s, ctx); break;                        // $86A2 corpse pop
                case 0x06: BounceAway(s, ctx); break;                      // $87E2 spiky bounce
                case 0x07: LobSplitter(s, ctx); break;                     // $85FF 8-dir splitter
                case 0x08: LingeringGasOrBonus(s, ctx); break;             // $8837 lingering gas / bonus pickup
                case 0x09: Pickup(s, ctx); break;                          // $8A1A weapon pickup
                case 0x0A: Debris(s, ctx); break;                          // $8B80 death debris
                case 0x0B: EightDirMover(s, ctx); break;                   // $8BDA 8-direction mover
                case 0x0C: case 0x0D: ThugWalker(s, ctx); break;           // $8C34 thug walker
                case 0x0E: BayonetWalker(s, ctx); break;                   // $8DAF aggro walker
                case 0x0F: HeavyImmuneWalker(s, ctx); break;               // $8EA3 gas-immune heavy
                case 0x10: ClimberFrog(s, ctx); break;                     // $906A wall climber
                case 0x11: case 0x16: Carrier(s, ctx); break;              // $90D3 carried/grabbed enemy
                case 0x12: PipePopper(s, ctx); break;                      // $9265 sewer pipe popper
                case 0x13: case 0x14: case 0x19: case 0x1C: Swimmer(s, ctx); break; // $92E6 swimmer family
                case 0x15: case 0x38: Riser(s, ctx); break;                // $A41E riser bomb
                case 0x17: Hopper(s, ctx); break;                          // $9424 fish hopper
                case 0x18: SwayWick(s, ctx); break;                        // $950B sway swinger
                case 0x1A: SwayPendulum(s, ctx); break;                    // $954B pendulum sway
                case 0x1B: Grabber(s, ctx); break;                         // $95B8 grabber clock
                case 0x1D: HeavyCharger(s, ctx); break;                    // $98EE heavy ceiling charger
                case 0x1E: FallingSpike(s, ctx); break;                    // $9ABE falling spike
                case 0x1F: FriendlyPop(s, ctx); break;                     // $8812 converted friendly
                case 0x20: Patroller(s, ctx); break;                       // $9A03 patroller + throw
                case 0x21: case 0x22: VineSnapper(s, ctx); break;          // $9F4C forest vine
                case 0x23: Sinker(s, ctx); break;                          // $9BC9 sinking riser
                case 0x24: Diver(s, ctx); break;                           // $9C8A sewer diver
                case 0x25: BombFrog(s, ctx); break;                        // $A6B4 bombing frog
                case 0x26: Roamer(s, ctx); break;                          // $A09C wanderer
                case 0x27: Faller(s, ctx); break;                          // $A7D9 ceiling dropper
                case 0x28: Jumper(s, ctx); break;                          // $A8BB jumper
                case 0x29: Shooter(s, ctx); break;                         // $9E85 aimed shooter
                case 0x2A: Floater(s, ctx); break;                         // $9DBD floating mine
                case 0x2B: Chaser(s, ctx); break;                          // $A9BF warehouse chaser
                case 0x2C: Bouncer(s, ctx); break;                         // $AA82 accelerating bouncer
                case 0x2D: Dropper(s, ctx); break;                         // $A53E dropper spawner
                case 0x2E: Zipper(s, ctx); break;                          // $A00A fast zipper
                case 0x2F: Spitter(s, ctx); break;                         // $ABBE wall spitter
                case 0x30: Slider(s, ctx); break;                          // $AD5B sliding turret
                case 0x31: LingeringInit(s, ctx); break;                   // $883F lingering-gas body
                case 0x32: SkyBomber(s, ctx); break;                       // $B007 falling bomber
                case 0x33: Rainmaker(s, ctx); break;                       // $AAD1 waterfall spawner
                case 0x34: DiveBomber(s, ctx); break;                      // $ADF2 dive bomber
                case 0x35: Hopper2(s, ctx); break;                         // $AC2C hopping turret
                case 0x36: case 0x3E: Flinger(s, ctx); break;              // $A28F grab-and-throw
                case 0x37: QuackerjackCore6(s, ctx); break;                // $965D boss core (bank-6 copy)
                case 0x39: Roller(s, ctx); break;                          // $A164 steam roller
                case 0x3A: Flyer(s, ctx); break;                           // $9FCB swooping flyer
                case 0x3B: Jumper2(s, ctx); break;                         // $A631 jumper variant
                case 0x3C: Stalker(s, ctx); break;                         // $AEBC stalker bomber
                case 0x3D: MiniDoor(s, ctx); break;                        // $B1D3 spawned door prop
                case 0x3F: PickupGrant(s, ctx); break;                     // $8A22 pickup grant body
                case 0x40: case 0x41: HoverBomber(s, ctx); break;          // $B2FC hovering bomber
                case 0x42: Licker(s, ctx); break;                          // $B483 tower licker
                case 0x43: Lobber(s, ctx); break;                          // $B570 hop lobber
                case 0x46: GrabbedCarrier(s, ctx); break;                  // $9822 grab-carry controller
                default: break; // unreachable: table covers $00-$46
            }
        }

        // ==================================================================
        // Shared fixed-bank helper transcriptions
        // ==================================================================

        /// <summary>$FA6C SetAnim X: anim = A, frame/timer = 0.</summary>
        internal static void SetAnim(ObjectSystem.Slot s, byte anim)
        {
            s.AnimNumber = anim; s.AnimFrame = 0; s.AnimTimer = 0;
        }

        /// <summary>$FA9A kill slot X: LDA #$00 → $04D0,X / $0400,X / $04C0,X / $04E0,X,
        /// then #$FF → $0590,X. $04D0 is zeroed as a whole byte, so the low state nibble
        /// goes with it — clearing only bit7 would leave $80|state and keep the slot readable
        /// as state 1 by the $8000 dispatcher.</summary>
        internal static void KillSlot(ObjectSystem.Slot s)
        {
            s.Active = false; s.StateLow = 0; s.Flags = 0; s.HitFlash = 0;
            s.Behavior = 0; s.PlacementIndex = 0xFF;
        }

        /// <summary>$FA4C find free object slot 15..4 (returns -1 = none/CS).</summary>
        internal static int FindFreeSlot(EnemyDispatcher ctx)
        {
            for (int i = 15; i >= 4; i--)
                if (!ctx.Objects.Slots[i].Active) return i;
            return -1;
        }

        /// <summary>$FA5C find free projectile slot 4..1 (returns -1 = none/CS).</summary>
        internal static int FindFreeProjSlot(EnemyDispatcher ctx)
        {
            for (int i = 4; i >= 1; i--)
                if (!ctx.Objects.Slots[i].Active) return i;
            return -1;
        }

        /// <summary>$FA84 spawn-init slot Y: anim A, frame/timer 0, active, flags $90.</summary>
        internal static void SpawnInit(EnemyDispatcher ctx, int y, byte anim)
        {
            var t = ctx.Objects.Slots[y];
            SetAnim(t, anim);
            t.Active = true;
            t.Flags = 0x90;
        }

        /// <summary>$FC29 mark placement index in the $0340 already-spawned bitmask.</summary>
        internal static void MarkPlacement(EnemyDispatcher ctx, ObjectSystem.Slot s)
        {
            if (s.PlacementIndex == 0xFF) return;
            ctx.Objects.MarkSpawned(s.PlacementIndex);
        }

        // ---- 8.8 position/velocity integration ----

        /// <summary>$F4FD X += ($0540.$0530) unsigned 16.8.</summary>
        internal static void IntegrateXPos(ObjectSystem.Slot s)
        {
            int acc = (s.X << 8) | s.XFrac;
            acc += (s.XVelInt << 8) | s.XVelFrac;
            s.X = (ushort)((acc >> 8) & 0xFFFF);
            s.XFrac = (byte)acc;
        }

        /// <summary>$F51B X -= ($0540.$0530).</summary>
        internal static void IntegrateXNeg(ObjectSystem.Slot s)
        {
            int acc = (s.X << 8) | s.XFrac;
            acc -= (s.XVelInt << 8) | s.XVelFrac;
            s.X = (ushort)((acc >> 8) & 0xFFFF);
            s.XFrac = (byte)acc;
        }

        /// <summary>$F539 Y += ($0560.$0550) (down-positive mover convention).</summary>
        internal static void IntegrateYAdd(ObjectSystem.Slot s)
        {
            int acc = (s.Y << 8) | s.YFrac;
            acc += (s.YVelInt << 8) | s.YVelFrac;
            s.Y = (ushort)((acc >> 8) & 0xFFFF);
            s.YFrac = (byte)acc;
        }

        /// <summary>$F539 exactly, including the $F54C-$F55A screen radix: the byte written
        /// to $0510,X is kept under $F0, and the row byte $0520,X takes the carry out of
        /// <c>ADC #$0F</c> — which is always 1 here, because the CMP only passes at ≥ $F0.</summary>
        internal static void IntegrateYRideSlot(ObjectSystem.Slot s)
        {
            int frac = s.YFrac + s.YVelFrac;                                  // $F53C ADC $0550,X
            s.YFrac = (byte)frac;
            int lo = ((s.Y & 0xFF) + (s.YVelInt & 0xFF) + (frac >> 8)) & 0xFF; // $F546 ADC $0560,X
            s.Y = (ushort)((s.Y & 0xFF00) | lo);                              // $F549 STA $0510,X
            if (lo >= 0xF0)                                                   // $F54C CMP #$F0
                s.Y = (ushort)((((s.Y >> 8) + 1) << 8) | ((lo + 0x0F + 1) & 0xFF)); // $F550-$F55A
        }

        /// <summary>$F5C9: apply the 8.8 Y velocity through the branch that matches its sign
        /// (non-negative → $F581 at $F5D4, negative → $F5A4), then $F5DA decays the velocity by
        /// $0099 and clamps a fall at $F8.00. Both calls use JMP, so the decay always runs.</summary>
        internal static void ApplyYVelocityDecay(ObjectSystem.Slot s, byte gravityFrac)
        {
            if (s.YVelInt >= 0) IntegrateYRising(s);          // $F5CC BPL $F5D4
            else IntegrateYSub(s);                            // $F5CE JSR $F5A4
            ApplyGravity(s, gravityFrac);                     // $F5D1/$F5D7 JMP $F5DA
        }

        /// <summary>$F55E/$F581/$F5A4 Y -= ($0560.$0550) (up-positive velocity).</summary>
        internal static void IntegrateYSub(ObjectSystem.Slot s)
        {
            int f = s.YFrac - s.YVelFrac;                       // $0460 -= $0550
            s.YFrac = (byte)f;
            int low = (s.Y & 0xFF) - (s.YVelInt & 0xFF) - (f < 0 ? 1 : 0); // $0510 -= $0560
            int screen = s.Y >> 8;
            low &= 0xFF;
            if (low >= 0xF0) { low -= 240; screen++; }          // $F5BB screen radix
            s.Y = (ushort)(((screen & 0xFF) << 8) | low);
        }

        /// <summary>$F581 rising integrate: same subtraction, but a borrow dips into the
        /// previous screen and steps 16 px further so the pixel byte stays under 240.</summary>
        internal static void IntegrateYRising(ObjectSystem.Slot s)
        {
            int f = s.YFrac - s.YVelFrac;
            s.YFrac = (byte)f;
            int low = (s.Y & 0xFF) - (s.YVelInt & 0xFF) - (f < 0 ? 1 : 0);
            int screen = s.Y >> 8;
            if (low < 0) { low = (low - 16) & 0xFF; screen--; } // $F596
            s.Y = (ushort)(((screen & 0xFF) << 8) | (low & 0xFF));
        }

        /// <summary>$F5DA gravity: velocity -= $99/256, clamp fall to 8.00.</summary>
        internal static void ApplyGravity(ObjectSystem.Slot s, byte gravityFrac)
        {
            int v = (sbyte)s.YVelInt * 256 + s.YVelFrac;
            v -= gravityFrac;
            if (v < -0x0800) v = -0x0800; // $F5EB clamp ($F8.00)
            s.YVelInt = (sbyte)((v >> 8) & 0xFF);
            s.YVelFrac = (byte)v;
        }

        /// <summary>$F5FC cut velocity to -0.33 (landing).</summary>
        internal static void CutVelocity(ObjectSystem.Slot s)
        {
            s.YVelFrac = 0xAB; s.YVelInt = -1;
        }

        /// <summary>$F5C9 unconditional up-positive integrate (Y -= vel).</summary>
        internal static void MoveY(ObjectSystem.Slot s) => IntegrateYSub(s);

        // ---- Probes ($EB52) + snaps ($ED85/$ED9D/$EDB3/$EDD2) ----

        /// <summary>
        /// $EB52/$EBE8 probe. ONE table index selects the X offset ($ED19) and the Y
        /// offset ($ECAD) together and the sampling loop advances that index by one per
        /// point ($EBA4 INC $40 / DEC $41), so a set is setY+1 points at indices
        /// setX..setX+setY — a floor set is ±11 at +12, a side set is +12 at −11/0/+11.
        /// Returns the max tile type ($10): 0 air, 1 one-way, >= 3 solid; 6/7 zero it
        /// ($EBE3) and 7 also sets $0400 bit5 ($EBDB). The last point sampled stays in
        /// ctx.ProbePointX/Y ($06/$07) because the snaps align to it, not to the slot.
        /// </summary>
        internal static byte Probe(EnemyDispatcher ctx, ObjectSystem.Slot s, int setX, int setY)
        {
            if (ctx.Tiles == null) return 0;
            byte result = 0;
            for (int i = 0; i <= setY; i++)
            {
                int idx = setX + i;
                int px = (s.X + (sbyte)ProbeX[idx]) & 0xFFFF;   // $06/$03 (high = cell column)
                int py = ProbePointY(s, (sbyte)ProbeY[idx]);
                ctx.ProbePointX = px;
                ctx.ProbePointY = py;
                byte t = ctx.Tiles.TypeAt(px, py);
                if (t > result) result = t;
            }
            if (result == 7) s.Flags |= 0x20;                   // $EBDB
            if (result == 6 || result == 7) result = 0;         // $EBE3
            ctx.ProbeResult = result;                            // $10
            return result;
        }

        /// <summary>
        /// $EC1B-$EC4B: Y is mixed-radix — the low byte is a pixel inside a 240 px screen
        /// and the high byte counts screens, so a point landing in [$F0,$FF] normalizes by
        /// ±16 and carries into the screen byte ($F5BB/$EC47 do the same for positions).
        /// </summary>
        private static int ProbePointY(ObjectSystem.Slot s, int off)
        {
            int low = (s.Y & 0xFF) + off;
            int screen = s.Y >> 8;
            if (low >= 0xF0) { low = (low + 16) & 0xFF; screen++; }
            else if (low < 0) { low = (low - 16) & 0xFF; screen--; }
            return ((screen & 0xFF) << 8) | (low & 0xFF);
        }

        /// <summary>$ED85 snap left after pushing right into a wall (aligns the probe point).</summary>
        internal static void SnapXWallRight(EnemyDispatcher ctx, ObjectSystem.Slot s)
        {
            s.X = (ushort)((s.X - (ctx.ProbePointX & 0x0F)) & 0xFFFF);
        }

        /// <summary>$ED9D snap right after pushing left into a wall.</summary>
        internal static void SnapXWallLeft(EnemyDispatcher ctx, ObjectSystem.Slot s)
        {
            s.X = (ushort)((s.X + 16 - (ctx.ProbePointX & 0x0F)) & 0xFFFF);
        }

        /// <summary>$EDD2 snap up onto the floor tile top (aligns the probe point Y).</summary>
        internal static void SnapToFloor(EnemyDispatcher ctx, ObjectSystem.Slot s)
        {
            int low = (s.Y & 0xFF) - (ctx.ProbePointY & 0x0F);
            int screen = s.Y >> 8;
            if (low < 0) { low += 240; screen--; }
            s.Y = (ushort)(((screen & 0xFF) << 8) | low);
        }

        /// <summary>$EDB3 snap down below a ceiling tile.</summary>
        internal static void SnapToCeiling(EnemyDispatcher ctx, ObjectSystem.Slot s)
        {
            int low = (s.Y & 0xFF) + 16 - (ctx.ProbePointY & 0x0F);
            int screen = s.Y >> 8;
            if (low >= 240) { low -= 240; screen++; }
            s.Y = (ushort)(((screen & 0xFF) << 8) | low);
        }

        /// <summary>
        /// $F200 move right at own velocity, probe right wall (set A, y Y), snap, face right.
        /// Returns the probe code left in $10 (callers like $8CF4 read it after the move).
        /// </summary>
        internal static byte WalkRight(EnemyDispatcher ctx, ObjectSystem.Slot s, int probeSet, int probeY)
        {
            IntegrateXPos(s);
            byte code = Probe(ctx, s, probeSet, probeY);
            if (code >= 3) SnapXWallRight(ctx, s);
            s.Flags |= 0x40;
            return code;
        }

        /// <summary>$F254 move left at own velocity, probe left wall (set 3), snap, face left.</summary>
        internal static byte WalkLeft(EnemyDispatcher ctx, ObjectSystem.Slot s, int probeSet, int probeY)
        {
            IntegrateXNeg(s);
            byte code = Probe(ctx, s, probeSet, probeY);
            if (code >= 3) SnapXWallLeft(ctx, s);
            s.Flags = (byte)(s.Flags & ~0x40);
            return code;
        }

        /// <summary>
        /// $F2D7 enemy path: integrate vertical velocity, apply gravity, probe
        /// floor at (setX, setY); lands (returns true) → $EDD2 snap + $F5FC cut.
        /// Rising into a ceiling ($EDB3 snap + cut) returns false.
        /// </summary>
        internal static bool FallAndLand(EnemyDispatcher ctx, ObjectSystem.Slot s, int probeSet, int probeY)
        {
            if (s.YVelInt < 0) // falling ($F2E8 BMI → $F5A4)
            {
                IntegrateYSub(s);
                ApplyGravity(s, ctx.Player.GravityFrac);            // $F31F
                byte floor = Probe(ctx, s, probeSet, probeY);       // $F326
                if (floor == 0) return false;
                // $F32E-$F344 (enemy branch, $F33A CPX #$01): any solid type snaps and lands;
                // type 1 only while the probe point sits in the first half of its 16 px block
                // ($F332 LDA $07 / AND #$0F / CMP #$08). $EDD2 + $F5FC + SEC.
                if (floor >= 2 || (floor == 1 && (ctx.ProbePointY & 0x0F) < 8))
                {
                    SnapToFloor(ctx, s);
                    CutVelocity(s);
                    return true;
                }
                return false;
            }
            // rising ($F3C4): $F581 integrate, gravity, then the probe index widens by the
            // sample count ($40 = $40 + $41 + 1) so the points sit above the head.
            IntegrateYRising(s);
            ApplyGravity(s, ctx.Player.GravityFrac);                // $F42A
            // $F432/$F436: for an enemy ANY non-zero probe ($EDB3) snap plus ($F471) cut, and
            // $F474 always clears carry — hitting a ceiling is never a landing.
            if (Probe(ctx, s, probeSet + probeY + 1, probeY) >= 1)
            {
                SnapToCeiling(ctx, s);
                CutVelocity(s);
            }
            return false;
        }

        // ---- Direction/aim helpers ----

        /// <summary>$F928 |player.X - s.X| (16-bit, returned as the original's byte).</summary>
        internal static int DistX(EnemyDispatcher ctx, ObjectSystem.Slot s) =>
            Math.Abs((short)(ctx.Player.X - s.X)) & 0xFF;

        /// <summary>$F919 |player.Y - s.Y| (low byte).</summary>
        internal static int DistY(EnemyDispatcher ctx, ObjectSystem.Slot s) =>
            Math.Abs((short)(ctx.Player.Y - s.Y)) & 0xFF;

        /// <summary>$F93F 24-sector code to the player (0-$1F; table SectorTable).</summary>
        internal static int SectorToPlayer(EnemyDispatcher ctx, ObjectSystem.Slot s)
        {
            int dy = (short)(ctx.Player.Y - s.Y);
            int y = 0;
            int ady = dy;
            if (dy < 0) { ady = -dy; y = 4; }        // player above
            int dx = (short)(ctx.Player.X - s.X);
            int adx = dx;
            if (dx < 0) { adx = -dx; y += 2; }       // player left
            if (adx < ady) { (adx, ady) = (ady, adx); y += 1; } // dominant axis first
            int refine = 0;
            int q = adx >> 2;
            if (q < ady) refine = 1;
            if ((q << 1) < ady) refine = 2;
            return (y << 2) + refine;
        }

        /// <summary>Direction bits for the sector to the player ($9F3C via the sector code).</summary>
        internal static byte DirToSector(EnemyDispatcher ctx, ObjectSystem.Slot s) =>
            SectorDir[SectorTable[SectorToPlayer(ctx, s) & 31] & 0x0F];

        /// <summary>$F7EE face the player: dir 1 (right) unless the player is to the left.</summary>
        internal static void FacePlayer(EnemyDispatcher ctx, ObjectSystem.Slot s)
        {
            s.Direction = (short)(s.X - ctx.Player.X) >= 0 ? (byte)2 : (byte)1;
        }

        /// <summary>$F808 apply dir bit0 to the flags h-flip bit6.</summary>
        internal static void ApplyFacing(ObjectSystem.Slot s)
        {
            if ((s.Direction & 1) != 0) s.Flags |= 0x40;
            else s.Flags = (byte)(s.Flags & ~0x40);
        }

        /// <summary>$F81D face the player + apply facing.</summary>
        internal static void FacePlayerApply(EnemyDispatcher ctx, ObjectSystem.Slot s)
        {
            FacePlayer(ctx, s);
            ApplyFacing(s);
        }

        /// <summary>$F808-with-dir-3-rotate: some callers pass $0580 pre-xored.</summary>
        internal static void SetVel(ObjectSystem.Slot s, byte frac, byte xFrac, byte yFrac, sbyte yInt)
        {
            s.XVelFrac = xFrac; s.YVelFrac = yFrac; s.YVelInt = yInt;
        }

        /// <summary>$882C init a spawned child's X velocity ($0530=0, $0540=3 → 3.00 rightward base).</summary>
        internal static void InitChildVel(ObjectSystem.Slot s)
        {
            s.XVelFrac = 0; s.XVelInt = 3;
        }

        // ---- Shared spawners ----

        /// <summary>$8D61 walker gas throw: spawn a behavior-1 cloud offset ±$12 by dir bit1.</summary>
        internal static void SpawnWalkerGas(EnemyDispatcher ctx, ObjectSystem.Slot s)
        {
            int y = FindFreeSlot(ctx);
            if (y < 0) return;
            var t = ctx.Objects.Slots[y];
            t.Direction = s.Direction;
            int off = (s.Direction & 2) != 0 ? 0xFF12 : 0x0012; // $8DAB/$8DAC {12 00, EE FF}
            int px = (short)(s.X + off);
            t.X = (ushort)px;
            t.Y = (ushort)(s.Y - 2);
            SpawnInit(ctx, y, 0x40);
            t.Behavior = 0x01;
            t.BehaviorParam = 0x0A;
            t.StateLow = 0x01;
            InitChildVel(t);
        }

        /// <summary>$8634 arrow/bomb split: spawn 2 behavior-7 clouds (dirs from $869C).</summary>
        internal static void SpawnSplitClouds(EnemyDispatcher ctx, ObjectSystem.Slot s, byte[] dirTable)
        {
            int n = 1;
            while (n >= 0)
            {
                int y = FindFreeProjSlot(ctx);
                if (y < 0) return;
                var t = ctx.Objects.Slots[y];
                t.X = s.X; t.Y = s.Y;
                t.BehaviorParam = 0x01;
                t.YVelFrac = 0x2E; t.XVelFrac = 0x2E;
                t.YVelInt = 3; t.XVelInt = 3;
                t.Behavior = 0x07;
                SpawnInit(ctx, y, 0x2E);
                t.Direction = dirTable[n];
                n--;
            }
        }

        /// <summary>$8715 death debris: 1 (sometimes 2) behavior-0A chunks with RNG velocities.</summary>
        internal static void SpawnDeathDebris(EnemyDispatcher ctx, ObjectSystem.Slot s)
        {
            int y = FindFreeSlot(ctx);
            if (y < 0) return;
            var t = ctx.Objects.Slots[y];
            t.X = s.X; t.Y = s.Y;
            t.BehaviorParam = 0x06;
            t.Health = 0;
            byte debrisAnim;
            if (s.AnimNumber == 0x38)
            {
                byte r = RngE6PlusE5(false); // $8745 E6 += E5
                if (r < 0x50) return;
                debrisAnim = DebrisVelFrac2[r & 0x1F];
            }
            else
            {
                byte r = RngE6PlusE5(false); // $875F E6 += E5
                if (r < 0x7F) return;
                debrisAnim = DebrisVelFrac[r & 0x1F];
            }
            SpawnInit(ctx, y, debrisAnim);
            t.Behavior = 0x0A;
            t.YVelFrac = 0xAB; t.YVelInt = -1;
            byte r2 = RngE6PlusE7(false); // $8785 E6 += E7
            if (r2 == 0x30 || r2 == 0x50 || r2 == 0x80 || r2 == 0xA0)
                SpawnInit(ctx, y, 0x1E); // second-roll retask of the same slot
        }

        /// <summary>$8B34 flying pickup icon: behavior-0B mover rising at 0.5 px/f.</summary>
        internal static void SpawnPickupFlyer(EnemyDispatcher ctx, ObjectSystem.Slot s)
        {
            int y = FindFreeSlot(ctx);
            if (y < 0) return;
            var t = ctx.Objects.Slots[y];
            t.X = s.X; t.Y = s.Y;
            SpawnInit(ctx, y, (byte)((s.AnimNumber - 0x1C) + 0x1F)); // $8B7D table
            t.Behavior = 0x0B;
            t.Direction = 0x08;
            t.BehaviorParam = 0;
            t.Health = 0;
            t.YVelInt = 0; t.YVelFrac = 0x80;
        }

        /// <summary>$97E0 Quackerjack ball child: anim $14 puff child at Y-$18.</summary>
        internal static void SpawnBallChild(EnemyDispatcher ctx, ObjectSystem.Slot s)
        {
            int y = FindFreeSlot(ctx);
            if (y < 0) return;
            var t = ctx.Objects.Slots[y];
            t.X = s.X;
            t.Y = (ushort)(s.Y - 0x18);
            t.BehaviorParam = 0; t.XVelFrac = 0; t.XVelInt = 0;
            t.YVelFrac = 0; t.YVelInt = 0;
            t.Behavior = 0;
            SpawnInit(ctx, y, 0x14);
            t.Direction = (byte)(s.Direction ^ 3);
        }

        // ==================================================================
        // Projectile behaviors (player doc §7)
        // ==================================================================

        /// <summary>$82F0 behavior 1: normal gas cloud. Horizontal 4 px/f (6 once anim $18);
        /// screen-edge bursts into anim $14.</summary>
        private static void GasCloud(ObjectSystem.Slot s, EnemyDispatcher ctx)
        {
            if ((s.StateLow & 0x0F) == 0)
            {
                s.XVelFrac = 0;
                s.XVelInt = (byte)(s.AnimNumber == 0x18 ? 6 : 4);
                // $830A is INC $04D0,X, not a store: the $CE39 bullet arrives with bit 7 set
                // and must keep it (gold_b_attack f30 reads $04D0 = $81).
                s.StateLow++;
            }
            if (s.AnimNumber == 0x14) return; // burst: static
            bool right = (s.Direction & 1) != 0;
            if (ctx.Player.ScrollYRow == 4) // $2B == 4: vertical-scroll rooms clamp at edges
            {
                if (right && s.X < 0xF0) { IntegrateXPos(s); return; }
                if (!right && s.X >= 0x10) { IntegrateXNeg(s); return; }
            }
            int screenX = (s.X - ctx.Player.ScrollX) & 0xFF; // $832D unsigned compare
            if (right)
            {
                if (screenX >= 0xFC) { KillSlot(s); return; }  // $8337: >= $FC → kill
                WalkRight(ctx, s, 0x0C, 1);
            }
            else
            {
                if (screenX < 8)
                {
                    if (s.X >= 4) { KillSlot(s); return; }     // $8363: abs X >= 4 → kill
                }
                WalkLeft(ctx, s, 0x0F, 1);
            }
            // wall → burst ($836B: probe result 3..6 while moving)
            int hit = Probe(ctx, s, right ? 0x24 : 0x27, 1); // $0C sets: right/left walls
            if (hit >= 3 && hit != 7)
            {
                if (s.AnimNumber != 0x18) SetAnim(s, 0x14);
                else KillSlot(s);
            }
        }

        /// <summary>$8385 behavior 2: lobbed gas. X 0.5 px/f, Y 3.00 up + gravity arc;
        /// floor → anim $14 burst, 2 behavior-1 clouds (dirs 2,1), SFX $24.</summary>
        private static void GasLob(ObjectSystem.Slot s, EnemyDispatcher ctx)
        {
            if ((s.StateLow & 0x0F) == 0)
            {
                s.XVelInt = 0; s.YVelFrac = 0;
                ctx.Player.DrainActiveMeter(0xFE); // $8982 add -$02 (lob cost)
                ctx.Player.AddSavedMeter(0xFE);    // $890B
                s.StateLow = 1;
                s.XVelFrac = 0x80;
                s.YVelInt = 3; s.YVelFrac = 0;
            }
            if (s.AnimNumber == 0x14) return;
            if ((s.Direction & 1) != 0) IntegrateXPos(s); else IntegrateXNeg(s);
            bool landed = FallAndLand(ctx, s, 6, 1); // probe (set 6, y 1) — $F2D7($12,$01)
            if (landed)
            {
                SetAnim(s, 0x14);
                SpawnSplitClouds(ctx, s, new byte[] { 0x02, 0x01 }); // $841A table
                ctx.Sfx?.Invoke(0x24);
            }
        }

        /// <summary>$841C behavior 3: thunder bolt. Wait phase, then 3.6 px/f travel;
        /// wall stick ($0570=$84), player proximity release, timer $FF→$78 → anim $29
        /// lightning fall ($F5C9).</summary>
        private static void ThunderBolt(ObjectSystem.Slot s, EnemyDispatcher ctx)
        {
            if ((s.StateLow & 0x0F) == 0)
            {
                s.YVelInt = 0; s.XVelInt = 0; s.XVelFrac = 0;
                s.YVelFrac = 1;
                ctx.Player.DrainActiveMeter(0xFD);
                ctx.Player.AddSavedMeter(0xFD);
                s.StateLow = 1;
                s.T480 = 0xFF;
            }
            if (s.AnimNumber != 0x14)
            {
                // travel ($8443..): anim frame 4 → anim $17 stick
                if (s.AnimFrame == 4 && s.AnimNumber == 0x14)
                {
                    SetAnim(s, 0x17);
                    s.XVelFrac = 0x99; s.XVelInt = 3;
                }
                if ((s.StateLow & 0x02) == 0)
                {
                    bool right = (s.Direction & 1) != 0;
                    int screenX = (short)(s.X - ctx.Player.ScrollX);
                    if (ctx.Player.ScrollYRow == 4)
                    {
                        if (right) { if (s.X < 0xF0) IntegrateXPos(s); }
                        else if (s.X >= 0x10) IntegrateXNeg(s);
                    }
                    else if (right)
                    {
                        if (((s.X - ctx.Player.ScrollX) & 0xFF) >= 0xF4) { KillSlot(s); return; }
                        WalkRight(ctx, s, 0x18, 1);
                    }
                    else
                    {
                        if (screenX < 0x10 && s.X < 0x2A) { KillSlot(s); return; }
                        WalkLeft(ctx, s, 0x1B, 1);
                    }
                    // wall stick ($84DF: screen edge → param $84 + bit1 latch)
                    int screenX2 = (short)(s.X - ctx.Player.ScrollX);
                    if (screenX2 >= 3 && screenX2 != 7)
                    {
                        s.BehaviorParam = 0x84;
                        s.StateLow |= 0x02;
                    }
                }
                else
                {
                    StuckBolt(s, ctx);
                }
                return;
            }
            // stuck chain ($851E): proximity to the player → release into the fall
            if ((s.StateLow & 0x0F) == 3) { s.BehaviorParam = 0; MoveY(s); return; }
            if (DistX(ctx, s) < 0x0C && DistY(ctx, s) < 0x0E && ctx.Player.State == 0)
            {
                s.StateLow++;
                return;
            }
            if (s.T480 == 0) { s.StateLow++; return; }
            if (DistX(ctx, s) >= 0x10) return;
            if (s.Y >= 0x10 && ctx.Player.Y < 0xEC)
            {
                s.T480--;
                if (s.T480 == 0x78) SetAnim(s, 0x29);
            }
        }

        /// <summary>$84F2/$851E stuck bolt: travel + anim $17 release into the lightning fall.</summary>
        private static void StuckBolt(ObjectSystem.Slot s, EnemyDispatcher ctx)
        {
            if ((s.StateLow & 0x0F) == 3) { s.BehaviorParam = 0; MoveY(s); return; }
            bool right = (s.Direction & 1) != 0;
            if (right) WalkRight(ctx, s, 0x0C, 1); else WalkLeft(ctx, s, 0x0F, 1);
            int screenX = (short)(s.X - ctx.Player.ScrollX);
            if (screenX != 3) return;
            SetAnim(s, 0x17);
            s.BehaviorParam = 0;
            s.StateLow = 0x83;
        }

        /// <summary>$85BC behavior 4: arrow/bomb. No motion; anim frame 4 + timer 2 →
        /// spawn 2 behavior-7 clouds then die.</summary>
        private static void ArrowBomb(ObjectSystem.Slot s, EnemyDispatcher ctx)
        {
            if ((s.StateLow & 0x0F) == 0)
            {
                int free = 0;
                for (int i = 4; i >= 2; i--) if (!ctx.Objects.Slots[i].Active) free++;
                if (free == 2)
                {
                    ctx.Player.DrainActiveMeter(0xFE);
                    ctx.Player.AddSavedMeter(0xFE);
                    s.StateLow = 1;
                }
            }
            if (s.AnimFrame == 4 && s.AnimTimer == 2)
            {
                SpawnSplitClouds(ctx, s, new byte[] { 0x01, 0x02 }); // $869C table (dirs 6/A vs 5/9 by dir bit1)
                KillSlot(s);
            }
        }

        /// <summary>$85FF behavior 7: split cloud 8-dir mover (dirs 1/2/5/6/9/A).</summary>
        private static void LobSplitter(ObjectSystem.Slot s, EnemyDispatcher ctx)
        {
            switch (s.Direction)
            {
                case 0x09: IntegrateXPos(s); WalkFaceR(s); MoveYFall(ctx, s); break;   // + F55E
                case 0x05: IntegrateXPos(s); WalkFaceR(s); MoveYRise(ctx, s); break;   // + F539
                case 0x0A: IntegrateXNeg(s); WalkFaceL(s); MoveYFall(ctx, s); break;
                default:   IntegrateXNeg(s); WalkFaceL(s); MoveYFall(ctx, s); break;   // $860E falls through
            }
        }

        internal static void WalkFaceR(ObjectSystem.Slot s) => s.Flags |= 0x40;
        internal static void WalkFaceL(ObjectSystem.Slot s) => s.Flags = (byte)(s.Flags & ~0x40);
        private static void MoveYFall(EnemyDispatcher ctx, ObjectSystem.Slot s) { IntegrateYAdd(s); ApplyGravity(s, ctx.Player.GravityFrac); }
        private static void MoveYRise(EnemyDispatcher ctx, ObjectSystem.Slot s) { IntegrateYAdd(s); ApplyGravity(s, ctx.Player.GravityFrac); }

        // ==================================================================
        // Death / debris / pickups
        // ==================================================================

        /// <summary>$86A2 behavior 5: death pop. Rise 3.27 px/f, debris once, anim retask,
        /// falls off screen. Shared tail $87FE (X by dir + $F5C9).</summary>
        private static void DeathPop(ObjectSystem.Slot s, EnemyDispatcher ctx)
        {
            if ((s.StateLow & 0x0F) == 0)
            {
                s.StateLow = 1;
                if (s.AnimNumber != 0x96 && s.AnimNumber != 0x61 && s.AnimNumber != 0x45 &&
                    s.AnimNumber != 0x46 && s.AnimNumber != 0x56)
                    SpawnDeathDebris(ctx, s);
            }
            s.AnimFrame = 0; s.AnimTimer = 0; // $F869
            switch (s.AnimNumber) // $86C9 retask table
            {
                case 0xA0: SetAnim(s, 0xA2); break;
                case 0x7E: case 0x7F: SetAnim(s, 0x44); break;
                case 0x69: case 0x6B: case 0x6A: case 0x72: SetAnim(s, 0xBA); break;
            }
            if ((s.StateLow & 0x02) == 0)
            {
                if (s.AnimNumber == 0x5F) { s.Direction ^= 3; s.StateLow |= 0x02; }
                else s.StateLow |= 0x02;
            }
            BounceTail(s, ctx); // $87FE
        }

        /// <summary>$87E2 behavior 6: bounce-away (spiky armor retask): rise 1.43 px/f
        /// + horizontal 1 px/f.</summary>
        private static void BounceAway(ObjectSystem.Slot s, EnemyDispatcher ctx)
        {
            if ((s.StateLow & 0x0F) == 0)
            {
                s.XVelFrac = 0;
                s.YVelFrac = 0x6E; s.YVelInt = 1;
                s.XVelInt = 1;
                s.StateLow = 1;
            }
            BounceTail(s, ctx);
        }

        /// <summary>$87FE shared tail: X by dir bit0 (own velocity), $F5C9 integrate.</summary>
        private static void BounceTail(ObjectSystem.Slot s, EnemyDispatcher ctx)
        {
            if ((s.Direction & 1) != 0) IntegrateXPos(s); else IntegrateXNeg(s);
            MoveY(s);
        }

        /// <summary>$8837 behavior 8: lingering gas / bonus pickup. Touch → collect via
        /// the (anim-$22) jump table; lingering clouds drain the active meter ($8957).</summary>
        private static void LingeringGasOrBonus(ObjectSystem.Slot s, EnemyDispatcher ctx)
        {
            if (!TouchTest(ctx, s)) return; // BCS $8856: not touching → linger
            MarkPlacement(ctx, s);
            KillSlot(s);
            s.Behavior = 0x31; // grant runs via the LingeringInit body below
            LingeringInit(s, ctx);
            switch (s.AnimNumber) // $8857/$885E jump table
            {
                case 0x22: BonusEnergy(ctx, s, 5); break;                     // $89D6
                case 0x23: BonusPoints(ctx, s, 1); break;                     // $88D9
                case 0x24: BonusPoints(ctx, s, 10); break;                    // $88D9
                case 0x25: BonusPoints(ctx, s, 1); break;                     // $88D9
                case 0x26: WeaponPickup(ctx, s, 2, 0x7B); break;              // $8865
                case 0x27: WeaponPickup(ctx, s, 1, 0x7A); break;              // $8889
                case 0x28: WeaponPickup(ctx, s, 3, 0x7C); break;              // $89AD
                case 0x29: WeaponPickup(ctx, s, 1, 0x7A); break;              // $8989
            }
        }

        /// <summary>$883F behavior 31: the lingering-gas grant body (also its dispatch id).</summary>
        private static void LingeringInit(ObjectSystem.Slot s, EnemyDispatcher ctx) { /* grant handled by caller */ }

        private static void BonusEnergy(EnemyDispatcher ctx, ObjectSystem.Slot s, int units)
        {
            ctx.Sfx?.Invoke(0x17);
            ctx.Player.AddActiveGasUnits(units);
        }

        private static void BonusPoints(EnemyDispatcher ctx, ObjectSystem.Slot s, int units)
        {
            ctx.Sfx?.Invoke(0x17);
            if (ctx.Player.ActiveGasEnergyLo != 0)
                ctx.Player.AddActiveGasUnits(s.AnimNumber == 0x24 ? 10 : 1);
            if (ctx.Player.GasEnergyLo == 0) { ctx.Player.GasEnergyHi = 0x70; ctx.Player.GasEnergyLo = 0x70; }
            ctx.Player.AddSavedMeter((byte)(units == 10 ? 0x0A : 0x01));
        }

        private static void WeaponPickup(EnemyDispatcher ctx, ObjectSystem.Slot s, int weapon, byte icon)
        {
            ctx.Sfx?.Invoke(0x18);
            ctx.Player.PendingWeaponIcon = icon;
            ctx.Player.WeaponSelected = (byte)weapon;
            ctx.Player.AddSavedMeter(0x0A);
            if (ctx.Player.GasEnergyLo != 0) ctx.Player.WeaponActive = (byte)weapon;
        }

        /// <summary>$8A1A behavior 9: weapon pickup — touch → mark + kill + grant.</summary>
        private static void Pickup(ObjectSystem.Slot s, EnemyDispatcher ctx)
        {
            if (!TouchTest(ctx, s)) return; // $8A1D BCS: not touching → idle
            MarkPlacement(ctx, s);
            PickupGrant(s, ctx);
        }

        /// <summary>$8A22 pickup grant body: kill + (anim-$1C) jump table.</summary>
        private static void PickupGrant(ObjectSystem.Slot s, EnemyDispatcher ctx)
        {
            KillSlot(s);
            switch (s.AnimNumber)
            {
                case 0x1C: // $8A3F weapon 1 icon + energy digit +1
                    ctx.Sfx?.Invoke(0x17);
                    SpawnPickupFlyer(ctx, s);
                    ctx.Player.AddEnergyDigit(1);
                    break;
                case 0x1D: // $8A4D weapon 2 icon + energy digit +5
                    ctx.Sfx?.Invoke(0x17);
                    SpawnPickupFlyer(ctx, s);
                    ctx.Player.AddEnergyDigit(5);
                    break;
                case 0x1E: // $8AF4 bonus +1 HUD energy column
                    ctx.Sfx?.Invoke(0x19);
                    SpawnPickupFlyer(ctx, s);
                    ctx.Player.AddHudEnergy(1);
                    break;
                default: // $8A8A cascade entry (flying icons $1F-$21 land here)
                    ctx.Player.AddEnergyDigit(1);
                    break;
            }
        }

        /// <summary>$8B80 behavior 0A: death debris. Arc (F2D7 $28,2), land → blink
        /// $FF→0 frames, then anim >= $22 → lingering gas, else pickup grant.</summary>
        private static void Debris(ObjectSystem.Slot s, EnemyDispatcher ctx)
        {
            if ((s.StateLow & 0x0F) == 0)
            {
                s.T480 = 0xFF;
                s.StateLow = 1;
            }
            if ((s.StateLow & 0x02) == 0)
            {
                bool landed = FallAndLand(ctx, s, 0x28, 2);
                if (landed) s.StateLow |= 0x02;
                return;
            }
            if (s.T480 == 0) { KillSlot(s); return; }
            s.T480--;
            if (s.T480 < 0x3C)
            {
                if ((s.T480 & 1) != 0) s.Flags = (byte)(s.Flags & ~0x08);
                else s.Flags |= 0x08;
            }
            if (s.AnimNumber >= 0x22) LingeringGasOrBonus(s, ctx);
            else Pickup(s, ctx);
        }

        /// <summary>$8BDA behavior 0B: 8-direction mover (dir bits: 1 +x, 2 -x, 4 +Y, 8 -Y).</summary>
        private static void EightDirMover(ObjectSystem.Slot s, EnemyDispatcher ctx)
        {
            switch (s.Direction)
            {
                case 0x01: WalkFaceR(s); IntegrateXPos(s); break;                       // $8BFE
                case 0x02: WalkFaceL(s); IntegrateXNeg(s); break;                       // $8C04
                case 0x08: IntegrateYSub(s); break;                                     // $8C0A (F55E)
                case 0x04: IntegrateYAdd(s); break;                                     // $8C0D (F539)
                case 0x09: WalkFaceR(s); IntegrateXPos(s); IntegrateYSub(s); break;     // $8C10
                case 0x05: WalkFaceR(s); IntegrateXPos(s); IntegrateYAdd(s); break;     // $8C19
                case 0x0A: WalkFaceL(s); IntegrateXNeg(s); IntegrateYSub(s); break;     // $8C22
                case 0x06: WalkFaceL(s); IntegrateXNeg(s); IntegrateYAdd(s); break;     // $8C2B
            }
        }

        // ==================================================================
        // Walkers / ground enemies
        // ==================================================================

        /// <summary>$8C34 behaviors 0C/0D: thug walker. Walk 1 px/f on the $16/$32/$3C
        /// timer cadence, aggro within $32 px → chase + throw gas ($8D61) on the RNG
        /// delay; anim $43 wind-up; turn + hop on solid probe.</summary>
        private static void ThugWalker(ObjectSystem.Slot s, EnemyDispatcher ctx)
        {
            if ((s.StateLow & 0x0F) == 0)
            {
                s.XVelFrac = 0; s.XVelInt = 1;
                s.T480 = 0x16;
                s.T490 = WalkerDelay[RngE5PlusE4(false) & 0x07]; // $8D2A
                s.StateLow = 1;
            }
            if ((s.StateLow & 0x02) == 0)
            {
                // $8C58: the walk (move + $8C67 probes) only runs while T480 counts DOWN
                // in anim $41; the wind-up anim $43 freezes horizontally ($8CA2 goes
                // straight to the $8C7D distance gate).
                if (s.T480 != 0 && s.AnimNumber != 0x43)
                {
                    s.T480--;
                    MoveThug(s, ctx);
                    ThugDistanceGate(s, ctx);
                    return;
                }
                if (s.T480 == 0 && s.AnimNumber != 0x43)
                {
                    SetAnim(s, 0x43);                       // $8C97
                    s.T480 = 0x3C;                          // $8C9C
                    return;
                }
                ThugAttack(s, ctx);                         // $8CA2
                return;
            }
            // fall/chase state ($8CBD): $F2D7 integrates gravity and probes the floor.
            bool landed = FallAndLand(ctx, s, 6, 2); // $F2D7($06, $02)
            if (landed)
            {
                if (s.AnimNumber != 0x41) SetAnim(s, 0x41);
            }
            else SetAnim(s, 0x42); // $8CD6 airborne pose
            // $8CDB: the walk runs in both the landed and air frames, and $8CF4 reads
            // $10 LEFT BY THAT MOVE (the wall probe), not the floor probe result.
            byte code = MoveThug(s, ctx);
            if (landed && code >= 3)
            {
                s.Direction ^= 3;
                int i = RngE5PlusE4(false) & 0x03; // $8D3F
                s.YVelFrac = WalkerHopYF[i];
                s.YVelInt = (sbyte)WalkerHopYI[i];
            }
            // $8D05: behavior $0C exits before the gas block — only $0D spits.
            if (s.Behavior != 0x0C)
            {
                if (s.AnimNumber == 0x41 && s.T490 == 0)
                {
                    s.T490 = WalkerDelay[RngE5PlusE4(false) & 0x07];
                    ctx.Sfx?.Invoke(0x23);            // $8D1E LDA #$23 / JSR $F850
                    SpawnWalkerGas(ctx, s);
                }
                if (s.T490 != 0) s.T490--;
            }
        }

        /// <summary>$8C7D: while the player is further than $32 px (LOW BYTE ONLY —
        /// $F928 returns the 8-bit magnitude, so 0x130 reads as 0x30 and aggroes),
        /// stay in the walk state; otherwise face the player and enter the fall state.</summary>
        private static void ThugDistanceGate(ObjectSystem.Slot s, EnemyDispatcher ctx)
        {
            // $8C7D: health==0 skips the distance gate, so the INC always runs.
            if (s.Health != 0 && DistX(ctx, s) >= 0x32) return;   // $8C85 CMP #$32 / BCS $8C8F
            FacePlayerApply(ctx, s);                              // $8C89 JSR $F81D
            s.StateLow++;                                         // $8C8C INC $04D0,X
        }

        private static byte MoveThug(ObjectSystem.Slot s, EnemyDispatcher ctx)
        {
            if ((s.Direction & 1) != 0) return WalkRight(ctx, s, 0, 2);
            return WalkLeft(ctx, s, 3, 2);
        }

        /// <summary>$8CA2 wind-up window: timer counts down without moving; if it is
        /// still running the $8C7D distance gate decides whether to start falling;
        /// expiry → anim $41, timer $32, turn.</summary>
        private static void ThugAttack(ObjectSystem.Slot s, EnemyDispatcher ctx)
        {
            s.T480--;
            if (s.T480 != 0) { ThugDistanceGate(s, ctx); return; }
            SetAnim(s, 0x41);
            s.T480 = 0x32;
            s.Direction ^= 3;
        }

        /// <summary>$8DAF behavior 0E: bayonet walker. Aggro at |dy| < 8 → lunge anim $46
        /// param $49; otherwise paces through the $8E55 velocity phase table (sine bob).</summary>
        private static void BayonetWalker(ObjectSystem.Slot s, EnemyDispatcher ctx)
        {
            if ((s.StateLow & 0x0F) == 0)
            {
                s.StateLow = 1;
            }
            if ((s.StateLow & 0x02) == 0)
            {
                if (DistY(ctx, s) < 8)
                {
                    SetAnim(s, 0x46);
                    s.BehaviorParam = 0x49;
                    FacePlayerApply(ctx, s);
                    s.StateLow |= 0x02;
                    return;
                }
                BayonetPace(s, ctx);
                return;
            }
            // lunge ($8DCE): while T480 != 0 bob, else integrate + X move
            if (s.T480 == 0)
            {
                int i = (s.T490 & 0x0F) * 2; // $8E95 phase → *2 into the vel table
                byte f = HeavyVelFrac[i & 31];
                byte hi = HeavyVelFrac[(i + 1) & 31];
                byte vi = HeavyVelInt[i & 31];
                byte vhi = HeavyVelInt[(i + 1) & 31];
                s.YVelFrac = f;
                s.YVelInt = (sbyte)vi;
                s.XVelFrac = f; // $8E75/$8E76 pairs load $0530/$0540 from the same rows
                s.XVelInt = vi;
                if ((sbyte)hi < 0) { s.XVelFrac = (byte)(((~f) & 0xFF) + 1); s.XVelInt = (byte)(((~vi) & 0xFF) + 1); }
                s.T490 = (byte)((s.T490 + 1) & 0x0F);
                s.T480 = (byte)(AreaOr5(ctx) == 5 ? 0x0C : 0x06);
            }
            s.T480--;
            IntegrateYAdd(s);
            if ((s.Direction & 2) == 0) IntegrateXPos(s); else IntegrateXNeg(s);
        }

        private static void BayonetPace(ObjectSystem.Slot s, EnemyDispatcher ctx)
        {
            // $8E43 tail: vertical bob + X move by dir bit1 (mirror of the lunge tail)
            IntegrateYAdd(s);
            if ((s.Direction & 2) == 0) IntegrateXPos(s); else IntegrateXNeg(s);
        }

        /// <summary>$8EA3 behavior 0F: heavy gas-immune walker. Pace 4 px/f, anim $5C/$5D
        /// cadence; aggro within $48 px → charge anim $5E → jump arc ($5B). HP $80
        /// (gas-immune; the post-processing $81C0 path handles conversions).</summary>
        private static void HeavyImmuneWalker(ObjectSystem.Slot s, EnemyDispatcher ctx)
        {
            if ((s.StateLow & 0x0F) == 0)
            {
                s.XVelFrac = 0; s.XVelInt = 4;
                s.T480 = 0x3C;
                s.StateLow = 1;
            }
            if ((s.StateLow & 0x02) == 0)
            {
                if (s.T480 == 0)
                {
                    if (s.AnimNumber == 0x5C) { HeavyTurn(s, ctx); return; }      // $8EC6-$8ECC → $8EE7
                    if (s.AnimNumber == 0x5D) { HeavyStride(s, ctx); return; }    // $8ECD-$8ED0 → $8EFB
                    SetAnim(s, 0x5C);                                             // $8ED1-$8ED6
                    s.Health = 2;                                                 // $8ED8 STA $05A0,X
                    return;
                }
                s.T480--;                                    // $8EDC DEC $0480,X
                // $8EDF-$8EE4: JSR $F928 / CMP #$48 / BCC $8ED1. The test CLOSES the
                // pace anim when the player comes within $48 px; it is not a walk
                // command. The port ran HeavyStride when the distance was >= $48, which
                // both inverted the sense and moved the enemy, so m1_longroute slot 0E
                // walked from X=$02F8 to $02EC by f568 while the ROM froze it at $02F8,
                // and the ported player then died on f611 to an enemy the ROM keeps away.
                if (DistX(ctx, s) < 0x48)
                {
                    SetAnim(s, 0x5C);
                    s.Health = 2;
                }
                return;                                      // $8EE6 / $8EDB RTS
            }
            // charge state ($8F3F)
            if (s.AnimNumber == 0x5B) { HeavyChargeWalk(s, ctx); return; }   // $8F44 → $8F91
            if (s.AnimNumber == 0x5C) { HeavyChargeTurn(s, ctx); return; }   // $8F48 → $8FD4
            if (s.AnimNumber != 0x5D) { HeavyChargeJump(s, ctx); return; }   // $8F54
            HeavyChargeWind(s, ctx);                                          // $8F4E → $8FED
        }

        private static void HeavyStride(ObjectSystem.Slot s, EnemyDispatcher ctx)
        {
            HeavyWobble(s);                                        // $8EFB-$8F22
            if (s.AnimFrame == 3 && s.AnimTimer == 0x10)           // $8F25-$8F31
            {
                s.StateLow |= 0x02;                                // $8F33 INC $04D0,X
                SetAnim(s, 0x5E);                                  // $8F36-$8F38
                HeavySpawnPuff(ctx, s);                            // $8F3B JSR $9028
            }
        }

        /// <summary>$8EFB-$8F22 / $8FED-$9014: the heavy's pace wobble. It is a 4-frame
        /// cycle on $0490 that nets ZERO displacement and touches only the X LOW byte
        /// ($04F0), so a nudge at $02 never borrows out of $0500. The port kept $0490 at
        /// 0 after the nudge instead of INCing it, which turned the once-per-cycle
        /// -2 px into a continuous 2 px/frame walk.</summary>
        private static void HeavyWobble(ObjectSystem.Slot s)
        {
            if (s.T490 == 0)
            {
                NudgeXLo(s, -2);                 // $8F00-$8F08
                s.T490 = 1;                      // $8F22, via either the BNE or the $8F0B fallthrough
            }
            else if (s.T490 == 4)
            {
                s.T490 = 0;                      // $8F12-$8F14
                NudgeXLo(s, 2);                  // $8F17-$8F1D
                if ((s.X & 0xFF) == 0) s.T490 = 1;   // $8F20 BNE $8F25 → else falls into the INC
            }
            else s.T490++;                       // $8F22
        }

        private static void NudgeXLo(ObjectSystem.Slot s, int delta) =>
            s.X = (ushort)((s.X & 0xFF00) | ((s.X + delta) & 0xFF));

        /// <summary>$8EE7: the turn anim only hands over to the stride at frame 1/$10.</summary>
        private static void HeavyTurn(ObjectSystem.Slot s, EnemyDispatcher ctx)
        {
            if (s.AnimFrame == 1 && s.AnimTimer == 0x10) { SetAnim(s, 0x5D); s.T490 = 0; }
        }

        /// <summary>$8FD4: same hand-over as $8EE7 but the miss path burns $0490
        /// ($8FDB-$8FE0 → $8FD0 DEC $0490,X), which the non-charge branch does not do.</summary>
        private static void HeavyChargeTurn(ObjectSystem.Slot s, EnemyDispatcher ctx)
        {
            if (s.AnimFrame == 1 && s.AnimTimer == 0x10)
            {
                SetAnim(s, 0x5D);   // $8FE2-$8FE4
                s.T490 = 0;         // $8FE7-$8FE9
            }
            else s.T490--;          // $8FD0
        }

        private static void HeavyChargeWind(ObjectSystem.Slot s, EnemyDispatcher ctx)
        {
            HeavyWobble(s);                                       // $8FED-$9014
            if (s.AnimFrame == 3 && s.AnimTimer == 0x10)          // $9017-$9023
            {
                SetAnim(s, 0x5E);                                 // $8F36 via $9025
                HeavySpawnPuff(ctx, s);                           // $8F3B JSR $9028
            }
        }

        private static void HeavyChargeWalk(ObjectSystem.Slot s, EnemyDispatcher ctx)
        {
            // $8F91: $0490 is a one-shot hop counter. The enemy moves only while it is
            // nonzero, then DECs it and RTSes; at 0 the handler FREEZES (the $8FAB path
            // just RTSes unless the animation reaches frame $0A / timer $04).
            if (s.T490 != 0)
            {
                if ((s.Direction & 1) != 0) IntegrateXNeg(s); else IntegrateXPos(s);
                if (s.T490 != 0) { s.T490--; return; }            // $8FA6-$8FD0
            }
            if (s.AnimFrame == 0x0A && s.AnimTimer == 4)          // $8FAB-$8FB7
            {
                s.Direction ^= 3;                                 // $8FBC-$8FBE EOR #$03
                s.Health = s.T4A0;                                // $8FC1-$8FC4
                SetAnim(s, 0x5C);                                 // $8FC7-$8FC9
                ctx.Sfx?.Invoke(0x08);                            // $8FCC JSR $F808
            }
        }

        private static void HeavyChargeJump(ObjectSystem.Slot s, EnemyDispatcher ctx)
        {
            // $8F54: charge jump arc (F2D7 $28,1) + chase move
            bool landed = FallAndLand(ctx, s, 0x28, 1);
            if ((s.Direction & 1) != 0) WalkRight(ctx, s, 0, 1); else WalkLeft(ctx, s, 3, 1);
            if (landed)
            {
                s.T4A0 = s.Health;
                s.Health = 0x80; // gas-immune while airborne
                SetAnim(s, 0x5B);
                s.T490 = 1;
            }
        }

        private static void HeavySpawnPuff(EnemyDispatcher ctx, ObjectSystem.Slot s)
        {
            int y = FindFreeSlot(ctx);
            if (y < 0) return;
            var t = ctx.Objects.Slots[y];
            t.X = s.X; t.Y = (ushort)(s.Y + 2);
            t.BehaviorParam = 0;
            t.XVelFrac = 0;
            SpawnInit(ctx, y, 0x14);
            t.XVelInt = 1;
            t.Behavior = 0x0B;
            t.Direction = (byte)(s.Direction ^ 3);
        }

        /// <summary>$906A behavior 10: wall climber frog (state table $90CD: 0 init,
        /// 1 climb, 2 hop). Shares the swimmer/pipe family helpers.</summary>
        private static void ClimberFrog(ObjectSystem.Slot s, EnemyDispatcher ctx)
        {
            switch (s.StateLow & 0x0F)
            {
                case 0:
                    s.T490 = 0; s.XVelFrac = 0; s.XVelInt = 1;
                    s.T480 = 1; s.T4A0 = 6; s.Direction = 1;
                    s.StateLow = 1;
                    break;
                case 1: // $909D hop-walk (F200/F254 $2C/$2E)
                    PipeHopTail(s, ctx);
                    if ((s.Direction & 1) != 0) WalkRight(ctx, s, 0x2C, 0);
                    else WalkLeft(ctx, s, 0x2E, 0);
                    if (ctx.ProbeResult != 0) { ClimberTurn(s, ctx); return; }
                    break;
                case 2: // $906A's hop arc — falls through the swimmer $9341 code
                    SwimRise(s, ctx);
                    break;
            }
        }

        private static void ClimberTurn(ObjectSystem.Slot s, EnemyDispatcher ctx)
        {
            s.Direction ^= 3;
            ctx.Sfx?.Invoke(0x08);
            PipeHopTail(s, ctx);
        }

        /// <summary>$90D3 behaviors 11/16: the carried/grabbed enemy controller.
        /// Every gate below is transcribed from raw PRG bank 6 ($180D3-$18200), verified
        /// byte-for-byte against `extracted/raw/prg.bin`; the port splits $04D0 into
        /// `Active` (bit 7) + `StateLow`, so the ROM's `INC $04D0,X` is a StateLow
        /// increment and `STA $04D0,#$80` is Active + StateLow 0.</summary>
        private static void Carrier(ObjectSystem.Slot s, EnemyDispatcher ctx)
        {
            if ((s.StateLow & 0x0F) == 0) // $90D3-$90ED init
            {
                s.YVelInt = 0; s.YVelFrac = 0x80;
                s.T490 = 0x10;
                s.StateLow = (byte)(s.StateLow + 1);   // $90E7 INC $04D0,X ($80 -> $81)
                s.T4B0 = (byte)(s.Y & 0xFF);           // $90EA latch Y low
            }
            // $90F0 — falls through in the same frame as the init.
            if (ctx.Player.State == 7) return;                                // $90F3
            if (ctx.Player.State == 0x0B) { CarrierRiseOrRelease(ctx, s); return; }  // $90F9 JMP $917C
            if (s.T480 != 0) { CarrierDrag(ctx, s); return; }                 // $9101 BNE $9145
            // $9104 trigger gate.
            if (DistX(ctx, s) >= 0x0C) return;                                // $9108 BCS
            if (!PlayerAtOrBelowSlot(ctx, s)) return;                         // $910E BCC ($F919 carry)
            int dy = DistY(ctx, s);
            if (dy >= 0x14) return;                                           // $9112
            if (dy < 0x0C) return;                                            // $9116
            if (ctx.Player.YVelInt < 0) { CarrierRisingGate(ctx, s); return; } // $9118 BMI $9130
            CarrierGrab(ctx, s);                                              // $911D
        }

        /// <summary>$911D: the shared grab body — queues SFX $12, sets the carried state.</summary>
        private static void CarrierGrab(EnemyDispatcher ctx, ObjectSystem.Slot s)
        {
            if (ctx.Player.State == 4) return;                                // $9121
            ctx.Sfx?.Invoke(0x12);
            ctx.Player.State = 4;
            s.T480++;                                                         // $912C INC $0480,X
        }

        /// <summary>$9130: gate used while the player rises — wider Y band, tight X band.</summary>
        private static void CarrierRisingGate(EnemyDispatcher ctx, ObjectSystem.Slot s)
        {
            if (!PlayerAtOrBelowSlot(ctx, s)) return;                         // $9133 BCC
            int dy = DistY(ctx, s);
            if (dy < 0x0F) return;                                            // $9137
            if (dy >= 0x14) return;                                           // $913B
            if (DistX(ctx, s) >= 0x08) return;                                // $9141 BCS
            CarrierGrab(ctx, s);                                              // $9143 → $911D
        }

        /// <summary>$9145: while $0480,X is set the slot drags the player up half a pixel
        /// per frame and watches the player state for release.</summary>
        private static void CarrierDrag(EnemyDispatcher ctx, ObjectSystem.Slot s)
        {
            if ((s.StateLow & 0x02) == 0)                                     // $9145 AND #$02
            {
                if (s.T490 == 0) { CarrierStatusAdvance(ctx, s); return; }    // $914C BEQ $9192
                IntegrateYRideSlot(s);                                        // $9151 JSR $F539 (indexed: the SLOT, not the player)
                int frac = ctx.Player.YFrac + 0x80;                           // $9154-$9162 player += 0.5 px
                ctx.Player.YFrac = (byte)frac;
                ctx.Player.Y = (ushort)((ctx.Player.Y & 0xFF00) |
                    (byte)(((ctx.Player.Y & 0xFF) + (frac >> 8)) & 0xFF));
                s.T490--;                                                     // $9165 DEC $0490,X
            }
            byte st = ctx.Player.State;
            if (st == 0 || st == 1 || st == 2) { CarrierRelease(ctx, s); return; }  // $916A/$9172/$9176
            if (st == 4 || st == 6) return;                                   // $916E/$917A
            CarrierRiseOrRelease(ctx, s);                                     // $917C
        }

        /// <summary>$917C: rising keeps the grab window open; otherwise drop back.</summary>
        private static void CarrierRiseOrRelease(EnemyDispatcher ctx, ObjectSystem.Slot s)
        {
            if (ctx.Player.YVelInt < 0) { CarrierRisingGate(ctx, s); return; } // $917F BMI $9130
            CarrierRelease(ctx, s);                                            // $9181
        }

        /// <summary>$9192: the drag counter expired — advance the status and, once the
        /// $04A0 kind latch says so, release a behavior-$12 child at slot X + $18.</summary>
        private static void CarrierStatusAdvance(EnemyDispatcher ctx, ObjectSystem.Slot s)
        {
            s.StateLow = (byte)(s.StateLow + 1);                              // $9192 INC $04D0,X
            bool eleven = s.Behavior == 0x11;
            byte kind = eleven ? (byte)0x80 : (byte)0x81;                     // $9205 / $9237
            s.T4A0 = CountCarrierKind(ctx, kind) == 1 ? (byte)0xFF : (byte)0x00;
            if (s.T4A0 != 0) return;                                          // $91AA/$91A2 BNE $9191
            int y = FindFreeSlot(ctx);                                        // $91AF JSR $FA4C
            if (y < 0) return;                                                // $91B1 BCS
            var t = ctx.Objects.Slots[y];
            t.X = (ushort)((s.X + 0x18) & 0xFFFF);                            // $91B6-$91C8
            t.Y = (ushort)((s.Y & 0xFF00) | 0x01);                            // $91CD/$91D3
            SpawnInit(ctx, y, 0x5A);                                          // $91D8 JSR $FA84 #$5A
            t.Behavior = 0x12;                                                // $91DD
            t.BehaviorParam = 0x47;                                           // $91E2 $0570,Y
            t.PlacementIndex = kind;                                          // $91F2 $0590,Y
            t.YVelFrac = kind;                                                // $91F5 $0550,Y
            t.Health = kind;                                                  // $91F8 $05A0,Y
            t.YVelInt = 7;                                                    // $91FD $0560,Y
        }

        /// <summary>$9201/$9233: count active slots 15..5 whose $0590 equals the carrier kind
        /// (the loop exits when Y reaches 4, so slot 4 is never tested).</summary>
        private static int CountCarrierKind(EnemyDispatcher ctx, byte kind)
        {
            int count = 0;
            for (int y = 0x0F; y >= 0x05; y--)
            {
                var slot = ctx.Objects.Slots[y];
                if (slot.Active && slot.PlacementIndex == kind) count++;
            }
            return count;
        }

        /// <summary>$F919 carry out: player $0510 >= slot $0510 (unsigned byte compare).</summary>
        private static bool PlayerAtOrBelowSlot(EnemyDispatcher ctx, ObjectSystem.Slot s) =>
            (ctx.Player.Y & 0xFF) >= (s.Y & 0xFF);

        private static void CarrierRelease(EnemyDispatcher ctx, ObjectSystem.Slot s)
        {
            s.Y = (ushort)((s.Y & 0xFF00) | s.T4B0); // $9181 restore latched Y
            s.StateLow = 0x80; s.T480 = 0;
        }

        /// <summary>$9265 behavior 12: the object a carrier releases. It falls through
        /// $F2AF's own $F539 integration until the floor probe answers solid, then bursts
        /// (anim $2A/$33, $0570 $87/$8E, SFX $26) and spawns 2 behavior-0B droplets.</summary>
        private static void PipePopper(ObjectSystem.Slot s, EnemyDispatcher ctx)
        {
            if ((s.StateLow & 0x0F) != 0) return;                            // $926A BNE $92E3 (RTS)
            // $926C LDA #$40 / LDY #$01 / JSR $F2AF. The helper integrates BEFORE probing
            // ($F2B3 JSR $F539), so the fall is part of the call, not a separate step —
            // gold_m1_longroute slot 0E moves 0390→0397→039F at $07.$80 and $EDD2 writes
            // $051E = $B0 over $F54C's $B5 at f335 (Temp/watch0e/writes.csv).
            IntegrateYRideSlot(s);                                           // $F2B3 JSR $F539
            byte hit = Probe(ctx, s, 0x40, 1);                               // $F2B6 JSR $EB52
            if (hit >= 3) SnapToFloor(ctx, s);                               // $F2BD/$F2BF JSR $EDD2
            if (hit < 3) return;                                             // $9277 BCC $92E3
            if (s.AnimNumber == 0x33) { s.BehaviorParam = 0x8E; }
            else { SetAnim(s, 0x2A); s.BehaviorParam = 0x87; }
            ctx.Sfx?.Invoke(0x26);                                           // $9290 JSR $F850 #$26
            s.StateLow++;                                                    // $9293 INC $04D0,X
            int n = 1;
            while (n >= 0)
            {
                int y = FindFreeSlot(ctx);
                if (y < 0) return;
                var t = ctx.Objects.Slots[y];
                t.X = s.X; t.Y = (ushort)(s.Y + 0x10);
                t.BehaviorParam = 0; t.XVelFrac = 0;
                SpawnInit(ctx, y, 0x14);
                t.XVelInt = 1;
                t.Behavior = 0x0B;
                t.Direction = PipePopperDirs[n];
                n--;
            }
        }

        private static readonly byte[] PipePopperDirs = { 0x01, 0x02 }; // $92E4

        private static void PipeHopTail(ObjectSystem.Slot s, EnemyDispatcher ctx) { /* shared hop tail */ }

        private static void SwimRise(ObjectSystem.Slot s, EnemyDispatcher ctx)
        {
            IntegrateYSub(s);
        }

        /// <summary>$92E6 behaviors 13/14/19/1C: swimmer family (state table $9418:
        /// 0 init, 1 $F869+aggro ($9331), 2 rise ($9341), 3 rise-move ($9372),
        /// 4 grabbed ($9892)). Walk length per behavior from $9420.</summary>
        private static void Swimmer(ObjectSystem.Slot s, EnemyDispatcher ctx)
        {
            switch (s.StateLow & 0x0F)
            {
                case 0: // $92F9
                    s.T490 = 0; s.T4B0 = 0; s.XVelFrac = 0; s.XVelInt = 1;
                    s.T480 = 1; s.T4A0 = 6;
                    s.T4B0 = s.Behavior == 0x13 ? SwimmerWalkLen[0]
                           : s.Behavior == 0x14 ? SwimmerWalkLen[1]
                           : s.Behavior == 0x19 ? SwimmerWalkLen[2]
                           : SwimmerWalkLen[3];
                    s.StateLow = 1;
                    break;
                case 1: // $9331
                    s.AnimFrame = 0; s.AnimTimer = 0; // $F869
                    SwimmerAggro(s, ctx);
                    break;
                case 2: // $9341
                    SwimmerAggro(s, ctx);
                    IntegrateXPos(s);
                    s.T4B0--;
                    if (s.T4B0 == 0) s.StateLow = 3;
                    break;
                case 3: // $9372 rise + chase
                    MoveY(s);
                    IntegrateXPos(s);
                    SwimmerChase(s, ctx);
                    break;
                case 4: // $9892 grabbed-carrier states (shared with behavior 46)
                    GrabbedShared(s, ctx);
                    break;
            }
        }

        private static void SwimmerAggro(ObjectSystem.Slot s, EnemyDispatcher ctx)
        {
            s.T490 = 0; s.T4B0 = 0; s.XVelFrac = 0; s.XVelInt = 1;
            s.T480 = 1; s.T4A0 = 6;
            s.StateLow = 2;
            s.T4B0 = s.Behavior == 0x13 ? SwimmerWalkLen[0]
                   : s.Behavior == 0x14 ? SwimmerWalkLen[1]
                   : s.Behavior == 0x19 ? SwimmerWalkLen[2]
                   : SwimmerWalkLen[3];
        }

        private static void SwimmerChase(ObjectSystem.Slot s, EnemyDispatcher ctx)
        {
            // $9379: lunge at the player, carry into state 4 via the $50=4 path
            s.T480--;
            if (s.T480 != 0) return;
            if (s.T490 == 0)
            {
                if (s.X != ctx.Player.X) { s.T480 = 1; return; }
            }
            if (s.T490 == 0 && ctx.Player.YVelInt >= 0) { s.T480 = 1; return; }
            if (s.T490 != 0) { s.T490 = 0; if (ctx.Player.Y == 4) { s.T480 = 0x1E; s.T490 = 0; ctx.Player.State = 1; return; } }
            if (ctx.Player.State == 7) { s.T480 = 1; return; }
            if (DistX(ctx, s) >= 8) { s.T480 = 1; return; }
            int py = ctx.Player.Y;
            ctx.Player.Y = (ushort)(py - 0x12);
            if (DistY(ctx, s) < 5)
            {
                ctx.Player.Y = (ushort)(py - 0x12);
                ctx.Player.State = 4;
                ctx.Player.YVelInt = -1;
                s.T490 = 1;
                ctx.Player.X = s.X;
                ctx.Player.Y = (ushort)(s.Y + 0x16);
                s.T480 = 1;
            }
            else ctx.Player.Y = (ushort)py;
        }

        private static void GrabbedShared(ObjectSystem.Slot s, EnemyDispatcher ctx)
        {
            // $9892 (behavior 46 grab): pace the grabbed player between states
            if (ctx.Player.State == 0x0B) { s.StateLow = 0x80; return; }
            GrabbedWatch(ctx, s);
        }

        private static void GrabbedWatch(EnemyDispatcher ctx, ObjectSystem.Slot s)
        {
            if (ctx.Player.State == 4)
            {
                ctx.Player.State = 1;
                ctx.Sfx?.Invoke(0x2C);
                s.T480 = 0xFF;
            }
        }

        /// <summary>$A41E behaviors 15/38: riser bomb (state table $A538: 0 init,
        /// 1 rise $939-ish via $A44D, 2 fall+despawn $A488). Rises 1 px/f + Y bob;
        /// despawn at Y limits with SFX $2E cadence.</summary>
        private static void Riser(ObjectSystem.Slot s, EnemyDispatcher ctx)
        {
            switch (s.StateLow & 0x0F)
            {
                case 0: // $A431
                    s.T480 = s.T480; s.T4B0 = s.T4B0;
                    s.YVelFrac = s.YVelFrac;
                    s.YVelInt = 1; s.T4A0 = 1; s.T490 = 8;
                    s.StateLow = 1;
                    break;
                case 1: // $A44D rise
                    MoveY(s);
                    RiserWobble(s, ctx);
                    int limit = s.Behavior == 0x38 ? 0x68 : 0x88;
                    if (s.Y >= limit) { s.StateLow = 2; s.YVelFrac = (byte)0x80; s.YVelInt = 0; }
                    s.T490--;
                    if (s.T490 == 0) { ctx.Sfx?.Invoke(0x2E); s.T490 = 8; }
                    break;
                case 2: // $A488 fall
                    IntegrateYSub(s);
                    RiserWobble(s, ctx);
                    int floor = s.Behavior == 0x38 ? 0x48 : 0x18;
                    if (s.Y <= floor)
                    {
                        if (s.T480 != 0) { ctx.Player.State = 1; } // $F607 + $50=1
                        KillSlot(s);
                    }
                    s.T490--;
                    if (s.T490 == 0) { ctx.Sfx?.Invoke(0x2E); s.T490 = 0x10; }
                    break;
            }
        }

        private static void RiserWobble(ObjectSystem.Slot s, EnemyDispatcher ctx)
        {
            // $A4C4: proximity check vs player → carry the player up (state 4)
            s.T4A0--;
            if (s.T4A0 != 0) return;
            if (s.T480 == 0)
            {
                if (ctx.Player.YVelInt >= 0) { s.T4A0 = 1; return; }
                s.T480 = 0;
                if (ctx.Player.State == 7 || s.X != ctx.Player.X || DistX(ctx, s) >= 8)
                { s.T4A0 = 1; return; }
                s.T4B0 = (byte)ctx.Player.Y;
                if (DistY(ctx, s) < 0x11)
                {
                    ctx.Player.State = 4;
                    s.Y = (ushort)(s.Y + 0x10);
                    s.T480 = 1;
                    ctx.Player.YVelInt = -1;
                }
                else s.T4A0 = 1;
            }
            else s.T4A0 = 1;
        }

        /// <summary>$9424 behavior 17: fish hopper. Idle anim $4D on the RNG delay
        /// ($94FA table {1E,3C,5A,1E}); burst to anim $4C when frame 11/timer 4,
        /// HP toggles $80 bit, spawns a behavior-0B splash child ($94B1);
        /// rises while T490 counts, then re-arms.</summary>
        private static void Hopper(ObjectSystem.Slot s, EnemyDispatcher ctx)
        {
            if ((s.StateLow & 0x0F) == 0)
            {
                s.StateLow = 1;
                s.T480 = HopperDelay[RngE6PlusE4(false) & 0x03]; // $94FA: E6 += E4
            }
            if ((s.StateLow & 0x02) == 0)
            {
                if (s.T480 == 0)
                {
                    if (s.AnimNumber != 0x4D) { SetAnim(s, 0x4D); return; }
                    return;
                }
                FacePlayerApply(ctx, s); // $F81D
                s.T480--;
                return;
            }
            // burst state ($949F)
            if (s.T490 == 0)
            {
                s.StateLow = 1;
                s.T480 = HopperDelay[RngE6PlusE4(false) & 0x03];
                return;
            }
            s.T490--;
        }

        /// <summary>$950B behavior 18: sway swinger. Horizontal 1 px/f; timer $32 →
        /// reverse + SFX cue; timer $65 re-arm.</summary>
        private static void SwayWick(ObjectSystem.Slot s, EnemyDispatcher ctx)
        {
            if ((s.StateLow & 0x0F) == 0)
            {
                s.XVelInt = 0; s.XVelFrac = 0;
                s.T480 = 0x32;
                s.StateLow = 1;
            }
            if ((s.Direction & 1) != 0) IntegrateXPos(s); else IntegrateXNeg(s);
            if (s.T480 == 0)
            {
                s.Direction ^= 3;
                ctx.Sfx?.Invoke(0x08); // $F808 site cue
                s.T480 = 0x65;
            }
            s.T480--;
        }

        /// <summary>$954B behavior 1A: pendulum sway. Wait |dx| < $48 → drop phase;
        /// swings via F2D7($28,$01) arcs at 2.4/2.6 px/f, reverses on floor probe.</summary>
        private static void SwayPendulum(ObjectSystem.Slot s, EnemyDispatcher ctx)
        {
            if ((s.StateLow & 0x0F) == 0)
            {
                s.YVelFrac = 0; s.XVelInt = 0;
                s.XVelFrac = (byte)0x80;
                s.YVelInt = 3;
                if (DistX(ctx, s) >= 0x48) return;
                s.StateLow = 1;
                return;
            }
            if (s.AnimNumber != 0x67)
            {
                bool landed = FallAndLand(ctx, s, 0x28, 1); // $F2AF($28,$01) probe
                if (ctx.ProbeResult == 0) return;
                SetAnim(s, 0x67);
                FacePlayerApply(ctx, s);
                return;
            }
            bool landed2 = FallAndLand(ctx, s, 0x28, 1); // $F2D7($28,$01)
            if ((s.Direction & 1) != 0) WalkRight(ctx, s, 0x24, 1);
            else WalkLeft(ctx, s, 0x26, 1);
            if (landed2)
            {
                if (ctx.ProbeResult < 3) return;
                s.Direction ^= 3;
            }
        }

        /// <summary>$95B8 behavior 1B: grabber clock. RNG delay ($9655), tick SFX $32,
        /// spawns a behavior-0B hand (anim $9F, param 2) every cycle ($95FB).</summary>
        private static void Grabber(ObjectSystem.Slot s, EnemyDispatcher ctx)
        {
            if ((s.StateLow & 0x0F) == 0)
            {
                s.StateLow = 1;
                s.T480 = GrabberDelay[RngE5PlusE4(false) & 0x07]; // $9648: E5 += E4
            }
            if (s.T480 == 0)
            {
                if (s.Flags == 0x50 || s.Flags == 0x10) return;
                if (s.AnimFrame == 1 && s.AnimTimer == 0x10)
                {
                    s.T480 = GrabberDelay[RngE5PlusE4(false) & 0x07];
                    ctx.Sfx?.Invoke(0x32);
                    SpawnGrabberHand(ctx, s);
                    return;
                }
                return;
            }
            s.AnimFrame = 0; s.AnimTimer = 0; // $F869
            s.T480--;
        }

        private static void SpawnGrabberHand(EnemyDispatcher ctx, ObjectSystem.Slot s)
        {
            int y = FindFreeSlot(ctx);
            if (y < 0) return;
            var t = ctx.Objects.Slots[y];
            t.Direction = s.Direction;
            int off = (s.Direction & 2) != 0 ? unchecked((short)0xFFEC) : 0x14; // $9644 {14 00, EC FF}
            t.X = (ushort)((short)(s.X + off));
            t.Y = (ushort)(s.Y + 0x16);
            SpawnInit(ctx, y, 0x9F);
            t.Behavior = 0x0B;
            t.BehaviorParam = 0x02;
            InitChildVel(t);
        }

        /// <summary>$98EE behavior 1D: heavy ceiling charger. Hangs (anim $48), T480=$41
        /// rise window; aggro |dx| < $30 → anim $4A drop; frame 9 → SFX $2B + throw
        /// child ($99BC, behavior $20, param $0F); anim $4B → hang again ($999D).</summary>
        private static void HeavyCharger(ObjectSystem.Slot s, EnemyDispatcher ctx)
        {
            if ((s.StateLow & 0x0F) == 0)
            {
                s.XVelFrac = 0; s.XVelInt = 1;
                s.T480 = 0x41; s.T490 = 5; s.T4A0 = 0x30;
                s.StateLow = 1;
            }
            if (s.AnimNumber == 0x4A) { HeavyDrop(s, ctx); return; }
            if (s.AnimNumber == 0x4B) { HeavyHang(s, ctx); return; }
            if (AreaOr5(ctx) == 3)
            {
                if ((s.T4B0 & 1) != 0) { IntegrateXPos(s); WalkFaceR(s); }
                else { IntegrateXNeg(s); WalkFaceL(s); }
            }
            if (DistX(ctx, s) < 0x30)
            {
                FacePlayerApply(ctx, s);
                SetAnim(s, 0x4A);
            }
            if (s.T4A0 == 0)
            {
                s.T4B0 ^= 1;
                s.T4A0 = 0x30;
            }
            s.T4A0--;
        }

        private static void HeavyDrop(ObjectSystem.Slot s, EnemyDispatcher ctx)
        {
            if (s.Flags == 0x50 || s.Flags == 0x10) return;
            if (s.AnimFrame == 9 && s.AnimTimer == 8)
            {
                ctx.Sfx?.Invoke(0x2B);
                FacePlayerApply(ctx, s);
                SpawnHeavyChild(ctx, s);
                return;
            }
            if (s.AnimFrame == 0x0A && s.AnimTimer == 8)
            {
                SetAnim(s, 0x4B);
                s.Health = s.T490;
            }
        }

        private static void SpawnHeavyChild(EnemyDispatcher ctx, ObjectSystem.Slot s)
        {
            int y = FindFreeSlot(ctx);
            if (y < 0) return;
            var t = ctx.Objects.Slots[y];
            t.Direction = s.Direction;
            int off = (s.Direction & 2) != 0 ? unchecked((short)0xFFFF) : 0x01; // $99FF {01 00, FF FF}
            t.X = (ushort)((short)(s.X + off));
            t.Y = s.Y;
            SpawnInit(ctx, y, 0x49);
            t.Behavior = 0x20;
            t.BehaviorParam = 0x0F;
        }

        private static void HeavyHang(ObjectSystem.Slot s, EnemyDispatcher ctx)
        {
            if (s.T480 == 0)
            {
                SetAnim(s, 0x48);
                s.T490 = s.Health;
                s.Health = (byte)0x80;
                s.T480 = 0x41;
                return;
            }
            s.T480--;
        }

        /// <summary>$9ABE behavior 1E: falling spike. Shivers on the RNG delay ($8D2A),
        /// then drops ($0570=$4C touch); floor → anim $65 shatter + spike chunks
        /// ($9B6E behavior-1 spawn); projectile hits only below the top (post-processing).</summary>
        private static void FallingSpike(ObjectSystem.Slot s, EnemyDispatcher ctx)
        {
            if ((s.StateLow & 0x0F) == 0)
            {
                s.XVelFrac = 0; s.XVelInt = 1;
                s.T480 = 0x18;
                s.T490 = WalkerDelay[RngE5PlusE4(false) & 0x07]; // $8D2A
                s.StateLow = 1;
            }
            if ((s.StateLow & 0x02) == 0)
            {
                if ((s.Direction & 1) != 0) IntegrateXPos(s); else IntegrateXNeg(s);
                if (s.T480 == 0)
                {
                    s.T480 = 0x31;
                    s.Direction ^= 3;
                    ctx.Sfx?.Invoke(0x08); // $F808
                }
                s.T480--;
                if (s.Flags == 0x50 || s.Flags == 0x10) return;
                if (s.T490 == 0) { SpikeShatter(s, ctx); return; }
                s.T490--;
                if (ProjectileOverlap(ctx, s)) // $9B1D: CC (hit) → shatter anim
                {
                    SetAnim(s, 0x64);
                    s.StateLow |= 0x02;
                }
                return;
            }
            // shatter state ($9B2B)
            if (s.T4A0 == 0)
            {
                if (s.AnimFrame == 4 && s.AnimTimer == 8)
                {
                    s.BehaviorParam = 0x56;
                    s.T4A0++;
                }
                if (s.AnimNumber != 0x65) { SetAnim(s, 0x65); return; }
                if ((s.AnimFrame ^ s.AnimTimer) == 0)
                {
                    SetAnim(s, 0x63);
                    s.StateLow = (byte)(0x80 | 0);
                    s.BehaviorParam = 0x4C;
                    s.T4A0 = 0;
                }
                return;
            }
            SpikeChunks(ctx, s);
        }

        private static void SpikeShatter(ObjectSystem.Slot s, EnemyDispatcher ctx)
        {
            s.T490 = 0;
            SetAnim(s, 0x64);
            s.StateLow |= 0x02;
        }

        private static void SpikeChunks(EnemyDispatcher ctx, ObjectSystem.Slot s)
        {
            int y = FindFreeSlot(ctx);
            if (y < 0) return;
            var t = ctx.Objects.Slots[y];
            t.Direction = s.Direction;
            int off = (s.Direction & 2) != 0 ? unchecked((short)0xFF12) : 0x12; // $8DAB
            t.X = (ushort)((short)(s.X + off));
            t.Y = (ushort)(s.Y - 2);
            SpawnInit(ctx, y, 0x62);
            t.Behavior = 0x01;
            t.BehaviorParam = 0x0A;
            t.StateLow = 0x81;
            t.XVelFrac = 0; t.XVelInt = 2;
            s.T490 = WalkerDelay[RngE5PlusE4(false) & 0x07];
        }

        private static bool ProjectileOverlap(EnemyDispatcher ctx, ObjectSystem.Slot s) => ctx.ProjectileOverlapHit(s);

        /// <summary>$8812 behavior 1F: friendly pop (converted projectile): X ±1, rises
        /// via $F55E until culled.</summary>
        private static void FriendlyPop(ObjectSystem.Slot s, EnemyDispatcher ctx)
        {
            if (s.Direction == 0x0A) IntegrateXNeg(s);
            else IntegrateXPos(s);
            IntegrateYSub(s);
        }

        /// <summary>$9A03 behavior 20: patroller. Walks, turns at the timer; RNG check
        /// for a same-Y behavior-1D partner ($9A88) — throws via T490; state 2 counts
        /// T4A0 → kill; state 3 = turn hop.</summary>
        private static void Patroller(ObjectSystem.Slot s, EnemyDispatcher ctx)
        {
            if ((s.StateLow & 0x0F) == 0)
            {
                s.T490 = 0; s.XVelFrac = 0; s.XVelInt = 4;
                s.StateLow = 1;
                s.T480 = 0x1E; s.T4A0 = 0x10;
            }
            byte st = (byte)(s.StateLow & 0x0F);
            if (st == 2) { s.T4A0--; if (s.T4A0 == 0) s.StateLow = 1; return; }
            if (st == 3) { if ((s.Direction & 1) != 0) IntegrateXPos(s); else IntegrateXNeg(s); return; }
            if ((s.Direction & 1) != 0) IntegrateXPos(s); else IntegrateXNeg(s);
            if (s.T480 != 0) { s.T480--; return; }
            s.StateLow++;
            if (s.T490 != 0)
            {
                PatrollerThrow(ctx, s);
                return;
            }
            if (s.T490 == 0)
            {
                s.Direction ^= 3;
                s.T480 = 0x1F;
                s.T490++;
            }
        }

        private static void PatrollerThrow(EnemyDispatcher ctx, ObjectSystem.Slot s)
        {
            // $9A88: find a behavior-1D partner at the same Y → T4A0 = FF (skip)
            for (int i = 15; i >= 5; i--)
            {
                var t = ctx.Objects.Slots[i];
                if (!t.Active) continue;
                if (t.Behavior == 0x1D && t.Y == s.Y) { s.T4A0 = 0xFF; return; }
            }
            s.T4A0 = 0;
        }

        /// <summary>$9F4C behaviors 21/22: vine snapper. Aggro |dx| < $10, |dy| < $17
        /// → drop; bite probe ($F2D7 $06,$02) + walk 0/3 px/f; touch class $00/$95
        /// by proximity; behavior 22 mirrors directions.</summary>
        private static void VineSnapper(ObjectSystem.Slot s, EnemyDispatcher ctx)
        {
            if ((s.StateLow & 0x0F) == 0)
            {
                s.XVelFrac = 0; s.XVelInt = 1;
                if (DistX(ctx, s) < 0x10 && DistY(ctx, s) < 0x17)
                {
                    s.StateLow = 1;
                    s.Direction = 2;
                }
                s.AnimFrame = 0; s.AnimTimer = 0; // $F869
                return;
            }
            if (ctx.Player.State == 7) return;
            bool landed = FallAndLand(ctx, s, 6, 2); // $F2D7($06,$02)
            s.BehaviorParam = DistY(ctx, s) < 6 ? (byte)0x00 : (byte)0x95;
            if (s.Behavior == 0x22)
            {
                if ((s.Direction & 1) != 0) { VineMove(ctx, s, 0, 2); }
                else { VineMove(ctx, s, 3, 2); }
                return;
            }
            if ((s.Direction & 1) != 0) { VineMove(ctx, s, 0, 2); }
            else { VineMove(ctx, s, 3, 2); }
        }

        private static void VineMove(EnemyDispatcher ctx, ObjectSystem.Slot s, int probeSet, int probeY)
        {
            if ((s.Direction & 1) != 0) WalkRight(ctx, s, probeSet, probeY);
            else WalkLeft(ctx, s, probeSet, probeY);
            if (ctx.ProbeResult != 0) s.Direction ^= 3;
        }

        /// <summary>$9BC9 behavior 23: sinking riser — rises 1 px/f while close, sinks
        /// after the T480 cadence; grabbed player carried up (SFX site $93D6-class).</summary>
        private static void Sinker(ObjectSystem.Slot s, EnemyDispatcher ctx)
        {
            if ((s.StateLow & 0x0F) == 0)
            {
                s.YVelFrac = 0; s.YVelInt = 1; s.T480 = 5;
                s.StateLow = 1;
                s.T4A0 = (byte)0x9B;
            }
            if (s.T480 != 0)
            {
                if (DistX(ctx, s) < 0x10 && DistY(ctx, s) < 0x12)
                {
                    MoveY(s); // $F539 rise
                    s.T480--;
                    ctx.Player.Y = (ushort)(ctx.Player.Y + 1); // carry the player up
                }
                return;
            }
            if (s.T4B0 == 0)
            {
                bool found = SinkerScan(ctx, s);
                if (!found) { s.T4A0++; return; }
                s.T4A0 = (byte)0x9B;
            }
            s.T4B0++;
            if (DistX(ctx, s) >= 0x10)
            {
                if (s.T490 == 5) return;
                IntegrateYSub(s); // $F55E sink
                s.T490++;
                return;
            }
            if (ctx.Player.YVelInt >= 0) return;
            if (s.T490 != 5) return;
            s.T480 = 5; s.T490 = 0; s.T4B0 = 0;
            if (s.T4A0 >= (byte)0x9E) s.T4A0 = (byte)0x9B;
        }

        private static bool SinkerScan(EnemyDispatcher ctx, ObjectSystem.Slot s)
        {
            for (int i = 15; i >= 5; i--)
            {
                var t = ctx.Objects.Slots[i];
                if (!t.Active) continue;
                if (t.AnimNumber == 0x9B || t.AnimNumber == 0x9C || t.AnimNumber == 0x9D) return false;
            }
            return true;
        }

        /// <summary>$9C8A behavior 24: sewer diver (state table $9DB3: 0 init, 1 rise,
        /// 2 wait, 3 surface ($9D01), 4 submerge ($9D25)). Anim $9E surfacing burst.</summary>
        private static void Diver(ObjectSystem.Slot s, EnemyDispatcher ctx)
        {
            switch (s.StateLow & 0x0F)
            {
                case 0: // $9C9D: scan for a behavior-23 partner, then init
                    DiverScan(ctx, s);
                    break;
                case 1: // $9CDD rise
                    s.T490--;
                    if (s.T490 == 0) s.StateLow = 2;
                    else IntegrateYSub(s);
                    break;
                case 2: // $9CEA wait → surface
                    if (s.T480 == 0) { s.XVelFrac = 0; s.XVelInt = 1; s.StateLow = 3; return; }
                    DiverScan(ctx, s);
                    break;
                case 3: // $9D01 surface burst: probe up ($40=0,$41=2)
                    if (Probe(ctx, s, 0, 2) != 0)
                    {
                        s.YVelFrac = (byte)0x80; s.YVelInt = 0;
                        s.StateLow = 4;
                        return;
                    }
                    IntegrateXPos(s);
                    DiverTail(s, ctx);
                    break;
                case 4: // $9D25 submerge
                    if (s.T480 != 0)
                    {
                        ctx.Player.State = 1; // $F607 knockback path
                        s.T480 = 0;
                    }
                    IntegrateYSub(s);
                    break;
            }
        }

        private static void DiverScan(EnemyDispatcher ctx, ObjectSystem.Slot s)
        {
            for (int i = 15; i >= 5; i--)
            {
                var t = ctx.Objects.Slots[i];
                if (!t.Active) continue;
                if (t.Behavior != 0x23) continue;
                SetAnim(s, t.Health);
                return;
            }
        }

        private static void DiverTail(ObjectSystem.Slot s, EnemyDispatcher ctx)
        {
            s.T4A0--;
            if (s.T4A0 != 0) return;
            if (ctx.Player.YVelInt < 0)
            {
                s.T480 = 0;
                if (ctx.Player.State == 7) { s.T4A0 = 1; return; }
                if (DistX(ctx, s) < 8)
                {
                    s.T4B0 = (byte)ctx.Player.Y;
                    int py = ctx.Player.Y - 0x10;
                    ctx.Player.Y = (ushort)py;
                    if (DistY(ctx, s) < 0x11)
                    {
                        ctx.Player.State = 4;
                        ctx.Player.X = s.X;
                        ctx.Player.Y = (ushort)(s.Y + 0x10);
                        ctx.Player.YVelInt = -1;
                        s.T480 = 1;
                    }
                    else ctx.Player.Y = (ushort)s.T4B0;
                }
            }
            s.T4A0 = 1;
        }

        /// <summary>$9DBD behavior 2A: floating mine — wait for |dx| < $40 → arm;
        /// rise/fall bob ($9DDC..), frame 13 → HP latch; frame $16/$17 → SFX $29 +
        /// spawn behavior-0B shock (anim $A1, param 2, $9E3C); proximity disarm.</summary>
        private static void Floater(ObjectSystem.Slot s, EnemyDispatcher ctx)
        {
            if ((s.StateLow & 0x0F) == 0)
            {
                if (DistX(ctx, s) < 0x40)
                {
                    s.StateLow = 1;
                    s.T480 = 0x1E;
                    s.T490 = 1;
                }
                s.AnimFrame = 0; s.AnimTimer = 0; // $F869
                return;
            }
            if ((s.StateLow & 0x02) == 0)
            {
                FacePlayerApply(ctx, s); // $F81D
                s.Health = (byte)0x80;
                s.BehaviorParam = 0x50;
                if (s.AnimFrame == 0x0D)
                {
                    s.StateLow |= 0x02;
                    s.Health = s.T490;
                }
                return;
            }
            // armed bob ($9E01)
            s.T490 = s.Health;
            if (s.Flags == 0x50 || s.Flags == 0x10) return;
            if (s.AnimFrame == 0x16)
            {
                if (s.AnimTimer == 4)
                {
                    ctx.Sfx?.Invoke(0x29);
                    SpawnFloaterShock(ctx, s);
                }
                return;
            }
            if (s.AnimFrame >= 0x17)
            {
                s.Health = (byte)0x80;
                s.BehaviorParam = 0;
                s.StateLow = 1;
                return;
            }
        }

        private static void SpawnFloaterShock(EnemyDispatcher ctx, ObjectSystem.Slot s)
        {
            int y = FindFreeSlot(ctx);
            if (y < 0) return;
            var t = ctx.Objects.Slots[y];
            t.Direction = s.Direction;
            int off = (s.Direction & 2) != 0 ? unchecked((short)0xFF12) : 0x12;
            t.X = (ushort)((short)(s.X + off));
            t.Y = (ushort)(s.Y - 2);
            SpawnInit(ctx, y, 0xA1);
            t.Behavior = 0x0B;
            t.BehaviorParam = 0x02;
            InitChildVel(t);
        }

        /// <summary>$9E85 behavior 29: aimed shooter — anim $8C charge (T480 rise),
        /// fires 8-dir children via $F93F sectors ($9EFC tables), then $8BDA mover.</summary>
        private static void Shooter(ObjectSystem.Slot s, EnemyDispatcher ctx)
        {
            if ((s.StateLow & 0x0F) == 0)
            {
                s.YVelInt = 0; s.T490 = 0;
                s.YVelFrac = (byte)0x80;
                s.T480 = 0x3C;
                s.StateLow = 1;
            }
            if (s.AnimNumber == 0x8C)
            {
                if (s.T480 == 0) return;
                IntegrateYSub(s); // $F55E charge rise
                s.T480--;
                return;
            }
            if (s.AnimNumber == 0x8A)
            {
                if (s.T490 != 0) { EightDirMover(s, ctx); return; }
                s.Direction = DirToSector(ctx, s);
                s.XVelFrac = FlyerDiveFrac[SectorTable[SectorToPlayer(ctx, s) & 31] & 0x0F];
                s.XVelInt = FlyerDiveInt[SectorTable[SectorToPlayer(ctx, s) & 31] & 0x0F];
                s.YVelFrac = FlyerRiseFrac[SectorTable[SectorToPlayer(ctx, s) & 31] & 0x0F];
                s.YVelInt = (sbyte)FlyerRiseInt[SectorTable[SectorToPlayer(ctx, s) & 31] & 0x0F];
                ctx.Sfx?.Invoke(0x08); // $F808
                s.T490++;
                return;
            }
            if (DistX(ctx, s) >= 0x48) { SetAnim(s, 0x8C); return; }
            if (ctx.Objects.SpawnedBitmask(0x0301) != 0) return;
            s.AnimFrame = 0; s.AnimTimer = 0; // $F869
        }

        /// <summary>$9FCB behavior 3A: swooping flyer — descend $44/$03, on player
        /// touch → behavior 5 + player grab ($FA78 anim 9, $51=$78, $F607).</summary>
        private static void Flyer(ObjectSystem.Slot s, EnemyDispatcher ctx)
        {
            if ((s.StateLow & 0x0F) == 0)
            {
                s.XVelInt = 0;
                s.YVelFrac = 0x44; s.YVelInt = 3;
                s.XVelFrac = (byte)0x80;
                s.Direction ^= 3;
                s.StateLow = 1;
            }
            if (TouchTest(ctx, s))
            {
                s.Behavior = 0x05;
                ctx.Player.State = 0x13;
                ctx.Player.SetAnimPlayer(0x09);
                ctx.Player.StateTimer = 0x78;
                ctx.Player.YVelFrac = 0xAB; ctx.Player.YVelInt = -1; // $F607
                return;
            }
        }

        /// <summary>$A00A behavior 2E: fast zipper — dash phases ($82/$83 anims),
        /// reverses on timer $50, SFX $2F cue at parity windows.</summary>
        private static void Zipper(ObjectSystem.Slot s, EnemyDispatcher ctx)
        {
            if ((s.StateLow & 0x0F) == 0)
            {
                s.XVelFrac = 0; s.XVelInt = 2;
                s.StateLow = 1;
                s.T480 = 0x28; s.T490 = 0x3C;
            }
            if (s.AnimNumber != 0x82 && s.AnimNumber != 0x83)
            {
                if (s.T490 == 0) { SetAnim(s, 0x82); s.T490 = 0x3C; return; }
                s.T490--;
                return;
            }
            if ((s.StateLow & 0x02) == 0)
            {
                if ((s.Direction & 1) != 0) IntegrateXPos(s); else IntegrateXNeg(s);
                if (s.T480 == 0)
                {
                    s.Direction ^= 3;
                    s.T480 = 0x50;
                    ctx.Sfx?.Invoke(0x08); // $F808
                    s.StateLow |= 0x02;
                    return;
                }
                s.T480--;
                if (s.T480 == 0x10) SetAnim(s, 0x83);
                return;
            }
            if (s.T490 == 0)
            {
                s.T490 = 0x3C;
                SetAnim(s, 0x82);
                s.StateLow = 1;
                return;
            }
            s.T490--;
        }

        /// <summary>$A09C behavior 26: wanderer — RNG delay ($A153 table {1E,3C,0F,3C}),
        /// anim $7B/$7C cadence, spawns behavior-0B shots (anim $7D, param 2, $A10A).</summary>
        private static void Roamer(ObjectSystem.Slot s, EnemyDispatcher ctx)
        {
            if ((s.StateLow & 0x0F) == 0)
            {
                s.StateLow = 1;
                s.T480 = SwimmerWait[RngE5PlusE4(false) & 0x03]; // $A153
            }
            FacePlayerApply(ctx, s); // $F81D
            if (s.AnimNumber == 0x7B)
            {
                if (s.Flags != 0x50 && s.Flags != 0x10 && s.AnimFrame == 6 && s.AnimTimer == 4)
                {
                    SpawnRoamerShot(ctx, s);
                    return;
                }
                if (s.AnimFrame == 0x0A && s.AnimTimer == 4) { SetAnim(s, 0x7C); return; }
            }
            else if (s.AnimNumber == 0x7C)
            {
                if (s.AnimTimer == 0x10) { SetAnim(s, 0x7A); return; }
            }
            else
            {
                if (s.T480 == 0) { SetAnim(s, 0x7B); s.T480 = SwimmerWait[RngE5PlusE4(false) & 0x03]; return; }
                s.T480--;
            }
        }

        private static void SpawnRoamerShot(EnemyDispatcher ctx, ObjectSystem.Slot s)
        {
            int y = FindFreeSlot(ctx);
            if (y < 0) return;
            var t = ctx.Objects.Slots[y];
            t.Direction = s.Direction;
            int off = (s.Direction & 2) != 0 ? unchecked((short)0xFF12) : 0x12;
            t.X = (ushort)((short)(s.X + off));
            t.Y = (ushort)(s.Y - 2);
            SpawnInit(ctx, y, 0x7D);
            t.Behavior = 0x0B;
            t.BehaviorParam = 0x02;
            InitChildVel(t);
        }

        /// <summary>$A164 behavior 39: steam roller — charges 0x5C px/f (0x5F left),
        /// carries the player ($A26A), pushes through walls with $ED85/$ED9D snaps.</summary>
        private static void Roller(ObjectSystem.Slot s, EnemyDispatcher ctx)
        {
            if ((s.StateLow & 0x0F) == 0)
            {
                s.XVelInt = 0; s.XVelFrac = (byte)0x80;
                s.Direction = 1;
                s.StateLow = 1;
            }
            if ((s.Direction & 1) != 0)
            {
                WalkRight(ctx, s, 0x5C, 2);
                if (ctx.ProbeResult != 0) s.Direction ^= 3;
            }
            else
            {
                WalkLeft(ctx, s, 0x5F, 2);
                if (ctx.ProbeResult != 0) s.Direction ^= 3;
            }
            if (s.Y == ctx.Player.Y && DistY(ctx, s) < 0x15 && DistX(ctx, s) < 0x10)
                RollerCarry(ctx, s);
        }

        private static void RollerCarry(EnemyDispatcher ctx, ObjectSystem.Slot s)
        {
            if (ctx.Player.State == 0x0E) return;
            if (ctx.Player.State != 0 && ctx.Player.State != 2 && ctx.Player.State != 3) return;
            if (ctx.Player.State == 0x0E || ctx.Player.YVelInt < 0)
            {
                ctx.Player.State = 1;
                ctx.Player.YVelFrac = 0xAB; ctx.Player.YVelInt = -1; // $F607
                s.BehaviorParam = 0;
                return;
            }
            s.BehaviorParam = 0x8E;
        }

        /// <summary>$A28F behaviors 36/3E: grab-and-throw flinger (state machine
        /// $90D3-family states + children at X+$40/$20, Y $28/$48; behavior $15/$38
        /// children with flags $B0, param $47, HP $80, placement $80).</summary>
        private static void Flinger(ObjectSystem.Slot s, EnemyDispatcher ctx)
        {
            if ((s.StateLow & 0x0F) == 0)
            {
                s.YVelInt = 0; s.YVelFrac = (byte)0x80;
                s.T490 = 0x10;
                s.StateLow = 1;
                s.T4B0 = (byte)(s.Y & 0xFF);
                return;
            }
            if (ctx.Player.State == 7) return;
            if (ctx.Player.State == 0x0B) { FlingerJump(ctx, s); return; }
            if (s.T480 == 0)
            {
                if (DistX(ctx, s) < 0x0C) return;
                if (DistY(ctx, s) >= 0x14 || DistY(ctx, s) < 0x0C) return;
                if (ctx.Player.YVelInt < 0) { FlingerGrab(ctx, s); return; }
                if (ctx.Player.State == 4) return;
                ctx.Sfx?.Invoke(0x12);
                ctx.Player.State = 4;
                s.T480++;
                return;
            }
            FlingerThrow(ctx, s);
        }

        private static void FlingerGrab(EnemyDispatcher ctx, ObjectSystem.Slot s)
        {
            if (DistY(ctx, s) >= 0x0F || DistY(ctx, s) < 0x0C || DistX(ctx, s) >= 8) return;
            ctx.Player.State = 4;
        }

        private static void FlingerJump(EnemyDispatcher ctx, ObjectSystem.Slot s)
        {
            if (ctx.Player.YVelInt < 0) FlingerGrab(ctx, s);
        }

        private static void FlingerThrow(EnemyDispatcher ctx, ObjectSystem.Slot s)
        {
            if ((s.StateLow & 0x02) != 0)
            {
                if (s.T490 == 0) { CarrierRelease(ctx, s); return; }
                MoveY(s);
                s.T490--;
            }
            if (ctx.Player.State == 0 || ctx.Player.State == 1 || ctx.Player.State == 2 ||
                ctx.Player.State == 3 || ctx.Player.State == 6)
                CarrierRelease(ctx, s);
        }

        /// <summary>$965D behavior 37: Quackerjack core (bank-6 copy, state table
        /// $9670/$9677: 0 init $967E, 1 wait $96A6, 2 teleport/throw $96B6, 3 rise
        /// $973B, 4 jump $9771, 5 timer $9783, 6 ball walk $9796).</summary>
        private static void QuackerjackCore6(ObjectSystem.Slot s, EnemyDispatcher ctx)
        {
            switch (s.StateLow & 0x0F)
            {
                case 0: // $967E init
                    s.YVelFrac = 0; s.XVelFrac = 0;
                    s.YVelInt = 1; s.XVelInt = 1;
                    s.StateLow = 1;
                    s.T480 = 8; s.T490 = 0x0A;
                    if ((SectorToPlayer(ctx, s) & 8) != 0) s.T4A0++;
                    break;
                case 1: // $96A6 wait |dx| >= $60
                    if (DistX(ctx, s) >= 0x60)
                    {
                        s.Health = 8;
                        s.StateLow = 2;
                    }
                    break;
                case 2: // $96B6 teleport + throw
                    if (s.T480 == 0) { QuackerjackBallWalk(s, ctx); return; }
                    if (s.Health != 8) { QuackerjackThrowReady(s, ctx); return; }
                    if (s.Y >= 0x28) { QuackerjackRise(s, ctx); return; }
                    QuackerjackDrop(s, ctx);
                    break;
                case 3: // $973B rise
                    s.YVelFrac += 0x40;
                    if (s.Y < 0x28) { MoveY(s); return; }
                    if (Probe(ctx, s, 0x28, 1) != 0)
                    {
                        s.StateLow++;
                        s.YVelFrac = (byte)0xD1; s.YVelInt = 2;
                    }
                    break;
                case 4: // $9771 jump arc
                    if (!FallAndLand(ctx, s, 0x28, 1)) return;
                    s.T480 = 0x0A;
                    s.StateLow++;
                    break;
                case 5: // $9783 timer
                    s.T480--;
                    if (s.T480 == 0)
                    {
                        s.StateLow++;
                        s.XVelFrac = 0; s.XVelInt = 2;
                    }
                    break;
                case 6: // $9796 ball-throw walk
                    QuackerjackBallWalk(s, ctx);
                    break;
            }
        }

        private static void QuackerjackThrowReady(ObjectSystem.Slot s, EnemyDispatcher ctx)
        {
            FacePlayerApply(ctx, s); // $F81D
            s.BehaviorParam = 0x42;
            SetAnim(s, 0xA9);
            s.Health = 0;
            SpawnBallChild(ctx, s);
            s.StateLow++;
            ctx.Player.YVelFrac = 0xAB; ctx.Player.YVelInt = -1; // $F5FC
        }

        private static void QuackerjackRise(ObjectSystem.Slot s, EnemyDispatcher ctx)
        {
            MoveY(s); // $F539 down? rise: $F539 with $0560=1 → climbs
        }

        private static void QuackerjackDrop(ObjectSystem.Slot s, EnemyDispatcher ctx)
        {
            // $96C2: teleport to the player's X and re-arm the throw cadence
            s.X = ctx.Player.X;
            s.T490 = 0x0A;
            s.Health = 8;
            s.StateLow = 2;
            s.BehaviorParam = 0x42;
            SetAnim(s, 0xA8);
            FacePlayerApply(ctx, s);
            s.T4A0 = 2;
            s.Health = 2;
            FacePlayerApply(ctx, s);
        }

        private static void QuackerjackBallWalk(ObjectSystem.Slot s, EnemyDispatcher ctx)
        {
            s.T490--;
            if (s.T490 != 0) return;
            s.T480 = 0x10;
            s.T490 = 0x0A;
            s.T4A0 ^= 1;
        }

        /// <summary>$A631 behavior 3B: jumper — hop arcs (F2D7 $06,$02), anim $4F/$50
        /// by rise/fall, turn+hop on walls, $0480 count-3 cadence ($04A0/$04B0).</summary>
        private static void Jumper2(ObjectSystem.Slot s, EnemyDispatcher ctx)
        {
            if ((s.StateLow & 0x0F) == 0)
            {
                s.XVelFrac = 0; s.XVelInt = 1;
                s.StateLow = 1;
            }
            bool landed = FallAndLand(ctx, s, 6, 2); // $F2D7($06,$02)
            if (landed)
            {
                if (s.AnimNumber != 0x4F) SetAnim(s, 0x4F);
            }
            else
            {
                if (s.AnimNumber != 0x50) { SetAnim(s, 0x50); s.YVelFrac = 0; s.YVelInt = 4; }
            }
            if ((s.Direction & 1) != 0) WalkRight(ctx, s, 0, 2);
            else WalkLeft(ctx, s, 3, 2);
            if (landed && ctx.ProbeResult >= 3)
            {
                s.T480++;
                if (s.T480 == 3)
                {
                    s.Direction ^= 3;
                    s.T480 = 0;
                }
                else { s.YVelFrac = 0; s.YVelInt = 4; return; }
            }
            s.T480 = 0;
        }

        /// <summary>$A6B4 behavior 25: bombing frog — hop arcs, spawns behavior-0B
        /// bombs (anim $55, param $0A) at the apex via $F93F sectors ($A799 tables).</summary>
        private static void BombFrog(ObjectSystem.Slot s, EnemyDispatcher ctx)
        {
            if ((s.StateLow & 0x0F) == 0)
            {
                s.YVelFrac = 0; s.YVelInt = 4;
                s.StateLow = 1;
            }
            if (s.T480 == 0)
            {
                if (((ctx.Objects.Parity & 1) == 0))
                {
                    SetAnim(s, 0x52);
                    s.T480++;
                    s.T490 = 0;
                }
                FacePlayerApply(ctx, s); // $F81D
                return;
            }
            bool landed = FallAndLand(ctx, s, 0x28, 1); // $F2D7($28,$01)
            if (landed)
            {
                s.YVelFrac = 0; s.YVelInt = 4;
                ctx.Sfx?.Invoke(0x33);
                s.T480--;
                s.BehaviorParam = 0x51;
                return;
            }
            byte vi = (byte)s.YVelInt;
            if (((vi & 0xFE) == 0xFE)) { SetAnim(s, 0x54); if (s.T490 == 0) BombFrogSpawn(ctx, s); }
            else if (vi < 0) SetAnim(s, 0x53);
            else SetAnim(s, 0x52);
            if ((s.Direction & 1) != 0) WalkRight(ctx, s, 0x28, 1);
            else WalkLeft(ctx, s, 0x26, 1);
            FacePlayerApply(ctx, s);
        }

        private static void BombFrogSpawn(EnemyDispatcher ctx, ObjectSystem.Slot s)
        {
            int y = FindFreeSlot(ctx);
            if (y < 0) return;
            var t = ctx.Objects.Slots[y];
            t.Direction = s.Direction;
            int off = (s.Direction & 2) != 0 ? unchecked((short)0xFFF8) : 0x10; // $B2F8 {10 00, F8 FF}
            t.X = (ushort)((short)(s.X + off));
            t.Y = (ushort)(s.Y + 1);
            SpawnInit(ctx, y, 0x55);
            t.Behavior = 0x0B;
            t.BehaviorParam = 0x0A;
            int sector = SectorToPlayer(ctx, s);
            s.T4B0 = (byte)sector;
            t.XVelFrac = ThrowerFrac[sector & 0x0F];
            t.XVelInt = ThrowerInt[sector & 0x0F];
            t.YVelFrac = ThrowerYF2[sector & 0x0F];
            t.YVelInt = (sbyte)ThrowerYI2[sector & 0x0F];
            t.Direction = DirToSector(ctx, s);
        }

        /// <summary>$A7D9 behavior 27: ceiling dropper — hang, |dx| < $58 → drop;
        /// frame-2 flap ($0480 toggle, $04A0 count to 8 → anim $8E + $F5FC);
        /// then fly at the player with the $F93F sectors ($AA42 tables).</summary>
        private static void Faller(ObjectSystem.Slot s, EnemyDispatcher ctx)
        {
            if ((s.StateLow & 0x0F) == 0)
            {
                s.XVelFrac = 0; s.YVelFrac = 0;
                s.XVelInt = 2; s.YVelInt = 2;
                s.StateLow = 1;
                return;
            }
            byte st = (byte)(s.StateLow & 0x0F);
            if (st == 2) { FallerFly(s, ctx); return; }
            if (st == 3) { FallerFlap(s, ctx); return; }
            if (DistX(ctx, s) < 0x58)
            {
                s.StateLow = 2;
                return;
            }
        }

        private static void FallerFly(ObjectSystem.Slot s, EnemyDispatcher ctx)
        {
            if (s.T480 == 0) { IntegrateYSub(s); s.T490++; }
            else { IntegrateYAdd(s); }
            s.T490++;
            if (s.T490 != 2) return;
            s.T480 ^= 1;
            s.T490 = 0;
            s.T4A0++;
            if (s.T4A0 == 8)
            {
                SetAnim(s, 0x8E);
                s.StateLow = 3;
                ctx.Player.YVelFrac = 0xAB; ctx.Player.YVelInt = -1; // $F5FC
            }
        }

        private static void FallerFlap(ObjectSystem.Slot s, EnemyDispatcher ctx)
        {
            if (s.AnimNumber == 0x8F) return;
            if (s.AnimFrame != 0x13 || s.AnimTimer != 2) return;
            SetAnim(s, 0x8F);
        }

        /// <summary>$A8BB behavior 28: jumper — hang (anim $89→$87 flap), T480=$B4
        /// cadence, T4A0=$5A cycles; drop + $04A0/$04B0 phase; spawns behavior-29
        /// aimed bolts (anim $8A, param $10) on the $0480 toggle ($A975).</summary>
        private static void Jumper(ObjectSystem.Slot s, EnemyDispatcher ctx)
        {
            if ((s.StateLow & 0x0F) == 0)
            {
                s.YVelInt = 0; s.YVelFrac = (byte)0x80;
                s.StateLow = 1;
                s.T480 = (byte)0xB4;
                s.T4A0 = 0x5A;
                s.T4B0 = 4;
            }
            FacePlayerApply(ctx, s); // $F81D
            if ((s.StateLow & 0x02) == 0)
            {
                if (s.AnimNumber == 0x87)
                {
                    if (s.T490 == 0)
                    {
                        if (s.AnimFrame == 0) IntegrateYSub(s);
                        else return;
                    }
                    else if (s.AnimFrame == 0) { /* hold */ }
                    else IntegrateYAdd(s);
                    if (s.T480 == 0)
                    {
                        s.T490 ^= 1;
                        s.T480 = (byte)0xB5;
                    }
                    s.T480--;
                    if (s.Health == s.T4B0 && DistX(ctx, s) < 0x32) { JumperFire(ctx, s); return; }
                    s.T4B0 = s.Health;
                    if (s.Flags == 0x50 || s.Flags == 0x10) return;
                    JumperFire(ctx, s);
                    SetAnim(s, 0x88);
                    s.StateLow |= 0x02;
                    return;
                }
                if (s.AnimNumber != 0x89)
                {
                    SetAnim(s, 0x89);
                    return;
                }
                if (s.AnimFrame == 2 && s.AnimTimer == 8) { SetAnim(s, 0x87); return; }
                return;
            }
            // fired state ($A964)
            if (s.T4A0 == 0)
            {
                s.T4A0 = 0x5B;
                s.StateLow = 1;
                return;
            }
            s.T4A0--;
        }

        private static void JumperFire(EnemyDispatcher ctx, ObjectSystem.Slot s)
        {
            int y = FindFreeSlot(ctx);
            if (y < 0) return;
            var t = ctx.Objects.Slots[y];
            t.Direction = s.Direction;
            int off = (s.Direction & 2) != 0 ? unchecked((short)0xFF0A) : 0x0A; // $A9BB
            t.X = (ushort)((short)(s.X + off));
            t.Y = (ushort)(s.Y - 0x14);
            SpawnInit(ctx, y, 0x8A);
            t.Behavior = 0x29;
            t.BehaviorParam = 0x10;
        }

        /// <summary>$A9BF behavior 2B: warehouse chaser — anim $86 charge at |dx| < $50;
        /// rise ($0550=$80) while T480 counts ($0490 re-arm $1E), then chase via the
        /// $F93F sectors ($AA42 tables) into the $8BDA mover.</summary>
        private static void Chaser(ObjectSystem.Slot s, EnemyDispatcher ctx)
        {
            if ((s.StateLow & 0x0F) == 0)
            {
                s.YVelInt = 0; s.YVelFrac = (byte)0x80;
                s.T480 = 0x3C;
                s.StateLow = 1;
            }
            if (s.AnimNumber != 0x86)
            {
                if (DistX(ctx, s) >= 0x50) return;
                SetAnim(s, 0x86);
                return;
            }
            if (s.T490 == 0)
            {
                if (s.T480 == 0) { s.T490++; s.T480 = 0x1E; return; }
                IntegrateYSub(s); // $F55E rise
                FacePlayerApply(ctx, s); // $F81D
                s.T480--;
                return;
            }
            if (s.T480 != 0) { s.T480--; return; }
            if ((s.StateLow & 0x02) != 0) { EightDirMover(s, ctx); return; }
            s.Direction = DirToSector(ctx, s);
            int idx = SectorTable[SectorToPlayer(ctx, s) & 31] & 0x0F;
            s.XVelFrac = AimFrac[idx];
            s.XVelInt = AimInt[idx];
            s.YVelFrac = AimYFrac[idx];
            s.YVelInt = (sbyte)AimYInt[idx];
            ctx.Sfx?.Invoke(0x08); // $F808
            s.StateLow |= 0x02;
        }

        /// <summary>$AA82 behavior 2C: accelerating bouncer — 1 px/f walk + $0C/256
        /// accel while $0540 < 4; timer $40 → reverse.</summary>
        private static void Bouncer(ObjectSystem.Slot s, EnemyDispatcher ctx)
        {
            if ((s.StateLow & 0x0F) == 0)
            {
                s.XVelFrac = 0; s.XVelInt = 1;
                s.StateLow = 1;
                s.T480 = 0x40;
            }
            if ((s.Direction & 1) != 0) { IntegrateXPos(s); WalkFaceR(s); }
            else { IntegrateXNeg(s); WalkFaceL(s); }
            if (s.T480 == 0)
            {
                if (s.XVelInt < 4)
                {
                    int acc = (s.XVelInt << 8) | s.XVelFrac;
                    acc += 0x0C;
                    s.XVelInt = (byte)(acc >> 8);
                    s.XVelFrac = (byte)acc;
                }
                return;
            }
            s.T480--;
        }

        /// <summary>$AAD1 behavior 33: rainmaker — rises $40/$01 while T490 counts,
        /// then falls ($F55E) to Y >= $E8 → splash child (anim $3C, behavior $33,
        /// param $8E) + kill ($AB82); |dx| < $13 and |dy| < $19 carries the player.</summary>
        private static void Rainmaker(ObjectSystem.Slot s, EnemyDispatcher ctx)
        {
            if ((s.StateLow & 0x0F) == 0)
            {
                s.YVelInt = 0; s.T490 = 0;
                s.YVelFrac = 0x40;
                s.StateLow = 1;
                s.T480 = (byte)(s.Y & 0xFF);
            }
            if (s.T490 == 0)
            {
                IntegrateYSub(s); // $F55E rise
                if (s.Y < 0x17)
                {
                    s.T490++;
                    s.Flags = (byte)(s.Flags & ~0x20);
                    s.YVelFrac = (byte)0x80;
                }
                return;
            }
            if (DistY(ctx, s) < 0x19 && DistX(ctx, s) < 0x13)
            {
                if (ctx.Player.State == 3)
                {
                    ctx.Player.Y = (ushort)(ctx.Player.Y + 1); // $AB20 carry
                }
                else
                {
                    ctx.Player.Y = (ushort)(ctx.Player.Y + 1);
                }
            }
            IntegrateYAdd(s); // $F539 fall
            if (s.Y < 0xE8) return;
            if (ctx.Player.State == 3) ctx.Player.State = 0;
            SpawnRainSplash(ctx, s);
            KillSlot(s);
        }

        private static void SpawnRainSplash(EnemyDispatcher ctx, ObjectSystem.Slot s)
        {
            int y = FindFreeSlot(ctx);
            if (y < 0) return;
            var t = ctx.Objects.Slots[y];
            t.X = s.X;
            t.Y = 0x28;
            SpawnInit(ctx, y, 0x3C);
            t.Flags = 0xF0;
            t.Behavior = 0x33;
            t.PlacementIndex = s.PlacementIndex;
            t.BehaviorParam = 0x8E;
            t.Health = 0;
        }

        /// <summary>$ABBE behavior 2F: wall spitter — spits via the $030C/$032C VRAM
        /// palette scratch (cycle $04A0 0..9, $0490=2 cadence).</summary>
        private static void Spitter(ObjectSystem.Slot s, EnemyDispatcher ctx)
        {
            if ((s.StateLow & 0x0F) == 0)
            {
                s.StateLow = 1;
                s.T490 = 2;
            }
            if (s.T490 != 0) { s.T490--; return; }
            s.T490 = 2;
            s.T4A0 += 4;
            if (s.T4A0 >= 0x0A) s.T4A0 = 0;
        }

        /// <summary>$AC2C behavior 35: hopping turret — phase table $8E95/$8E55 bob
        /// (shared with 0E), T480=6 cadence; HP == 0 → anim $91 + SFX $30 + freeze.</summary>
        private static void Hopper2(ObjectSystem.Slot s, EnemyDispatcher ctx)
        {
            if ((s.StateLow & 0x0F) == 0)
            {
                s.T480 = 0; s.T490 = 0;
                s.StateLow = 1;
            }
            if (s.AnimNumber == 0x91) { Hopper2Frozen(s, ctx); return; }
            if (s.T480 == 0)
            {
                int i = (s.T490 & 0x0F) * 2;
                s.YVelFrac = HeavyVelFrac[i & 31];
                s.YVelInt = (sbyte)HeavyVelInt[i & 31];
                s.XVelFrac = HeavyVelFrac[(i + 1) & 31];
                s.XVelInt = HeavyVelInt[(i + 1) & 31];
                if ((sbyte)HeavyVelFrac[(i + 1) & 31] < 0) { s.XVelFrac++; s.XVelInt = (byte)~s.XVelInt; }
                s.T490 = (byte)((s.T490 + 1) & 0x0F);
                s.T480 = 6;
            }
            s.T480--;
            IntegrateYAdd(s);
            if ((s.Direction & 2) == 0) IntegrateXPos(s); else IntegrateXNeg(s);
            if (s.Health == 0)
            {
                SetAnim(s, 0x91);
                ctx.Sfx?.Invoke(0x30);
                s.XVelFrac = 0; s.YVelFrac = 0;
                s.XVelInt = 4; s.YVelInt = 4;
            }
        }

        private static void Hopper2Frozen(ObjectSystem.Slot s, EnemyDispatcher ctx)
        {
            if (s.AnimFrame != 1) return;
            s.AnimFrame = 1; s.AnimTimer = 0;
            byte st = (byte)(s.StateLow & 0x0F);
            if (st == 2) { Hopper2Walk(ctx, s); return; }
            if (st == 3) { Hopper2Dash(ctx, s); return; }
            if ((s.Direction & 1) != 0) WalkRight(ctx, s, 0x50, 2);
            else WalkLeft(ctx, s, 0x53, 2);
            if (ctx.ProbeResult != 0) s.StateLow |= 0x02;
        }

        private static void Hopper2Walk(EnemyDispatcher ctx, ObjectSystem.Slot s)
        {
            bool landed = FallAndLand(ctx, s, 0x28, 1); // $F2AF($28,$01)
            if ((s.Direction & 1) != 0) WalkRight(ctx, s, 0x24, 1);
            else WalkLeft(ctx, s, 0x26, 1);
            if (ctx.ProbeResult != 0) s.StateLow |= 0x02;
        }

        private static void Hopper2Dash(EnemyDispatcher ctx, ObjectSystem.Slot s)
        {
            if ((s.Direction & 1) != 0) IntegrateXPos(s); else IntegrateXNeg(s);
            IntegrateYSub(s); // $F55E
        }

        /// <summary>$AD5B behavior 30: sliding turret — $0490=$20 slide cadence, HP
        /// toggles $80 while active; reverses at |dx| windows ($ADC1) with RNG
        /// re-arms ($ADCA table).</summary>
        private static void Slider(ObjectSystem.Slot s, EnemyDispatcher ctx)
        {
            if ((s.StateLow & 0x0F) == 0)
            {
                s.T490 = 0x20;
                s.XVelFrac = (byte)0x80; s.XVelInt = 1;
                if (DistX(ctx, s) < 0) s.T480++;
                ctx.Sfx?.Invoke(0x08); // $F808
                s.StateLow = 1;
            }
            s.Health |= 0x80;
            if (s.T480 != 0)
            {
                IntegrateXPos(s);
                if (DistX(ctx, s) < 0) { SliderTurn(ctx, s); }
                return;
            }
            IntegrateXNeg(s);
            if (DistX(ctx, s) >= 0) { SliderTurn(ctx, s); return; }
            s.T490--;
            if (s.T490 != 0) return;
            SliderTurn(ctx, s);
        }

        private static void SliderTurn(EnemyDispatcher ctx, ObjectSystem.Slot s)
        {
            int i = RngE5PlusE4(false) & 0x03; // $ADCA
            s.XVelFrac = DrifterFrac[i];
            s.XVelInt = DrifterInt[i];
            s.T490 = DrifterDelay[i];
            s.T480 ^= 1;
            s.Direction ^= 3;
            ctx.Sfx?.Invoke(0x08); // $F808
        }

        /// <summary>$ADF2 behavior 34: dive bomber — hop arcs (F2D7 $56,$01) with
        /// T480=$3C/$3D cadence; HP == 0 → anim $95 + behavior-5 child ($AE62).</summary>
        private static void DiveBomber(ObjectSystem.Slot s, EnemyDispatcher ctx)
        {
            if ((s.StateLow & 0x0F) == 0)
            {
                s.XVelInt = 0; s.XVelFrac = (byte)0x80;
                s.T480 = 0x3C;
                ctx.Sfx?.Invoke(0x31);
                s.StateLow = 1;
            }
            bool landed = FallAndLand(ctx, s, 0x56, 1); // $F2D7($56,$01)
            if ((s.Direction & 1) != 0) WalkRight(ctx, s, 0, 1);
            else WalkLeft(ctx, s, 3, 1);
            if (landed && ctx.ProbeResult >= 3) s.Direction ^= 3;
            if (s.T480 == 0)
            {
                ctx.Sfx?.Invoke(0x3D); // $F7EE-adjacent hop cue
                s.T480 = 0x3D;
            }
            s.T480--;
            if (s.Health == 0)
            {
                if (s.AnimNumber == 0x95) return;
                SetAnim(s, 0x95);
                DiveBomberChild(ctx, s);
            }
        }

        private static void DiveBomberChild(EnemyDispatcher ctx, ObjectSystem.Slot s)
        {
            int y = FindFreeSlot(ctx);
            if (y < 0) return;
            var t = ctx.Objects.Slots[y];
            t.Direction = s.Direction;
            int off = (s.Direction & 2) != 0 ? unchecked((short)0xFFFF) : 0x01; // $99FF
            t.X = (ushort)((short)(s.X + off));
            t.Y = (ushort)(s.Y - 0x0C);
            SpawnInit(ctx, y, 0x96);
            t.Behavior = 0x05;
            t.BehaviorParam = 0x0A;
            t.YVelFrac = 0x44; t.YVelInt = 3;
            t.XVelFrac = (byte)0x80; t.XVelInt = 0;
        }

        /// <summary>$AEBC behavior 3C: stalker bomber (state table $AFFF/$B003:
        /// 0 wait $AECF, 1 descend $AEE8, 2 throw $AF5E, 3 fall $AEDA).</summary>
        private static void Stalker(ObjectSystem.Slot s, EnemyDispatcher ctx)
        {
            switch (s.StateLow & 0x0F)
            {
                case 0: // $AECF
                    s.XVelFrac = (byte)0x80; s.XVelInt = 1;
                    s.T480 = 0; s.YVelFrac = 0; s.YVelInt = 0;
                    s.StateLow = 1;
                    break;
                case 1: // $AEE8 descend to |dx| < $30
                    if (DistX(ctx, s) < 0x30)
                    {
                        s.XVelFrac = 0; s.XVelInt = 1;
                        s.YVelFrac = 0x40; s.YVelInt = 3;
                        SetAnim(s, 0x61);
                        s.T4A0 = 0x31;
                        s.T4B0++;
                        s.StateLow = 2;
                        return;
                    }
                    StalkerArc(ctx, s, 0x62, 2);
                    break;
                case 2: // $AF5E throw + hover
                    s.T4A0--;
                    if (s.T4A0 != 0) { StalkerArc(ctx, s, 0x62, 2); return; }
                    s.T490 = 0x1E;
                    SetAnim(s, 0x60);
                    s.YVelFrac = 0; s.YVelInt = 0;
                    s.T4B0 = 0;
                    s.StateLow = 3;
                    break;
                case 3: // $AEDA fall
                    s.T490--;
                    if (!FallAndLand(ctx, s, 0x62, 2)) return;
                    FacePlayerApply(ctx, s); // $F81D
                    if (s.T490 >= 0) return;
                    s.XVelFrac = (byte)0x80; s.XVelInt = 1;
                    s.StateLow = 1;
                    s.StateLow = (byte)(0x80 | 0);
                    break;
            }
        }

        private static void StalkerArc(EnemyDispatcher ctx, ObjectSystem.Slot s, byte a, byte y)
        {
            bool landed = FallAndLand(ctx, s, a, y);
            if ((s.Direction & 1) != 0) WalkRight(ctx, s, 0x5C, y);
            else WalkLeft(ctx, s, 0x5F, y);
            if (landed) { s.T480++; if (s.T480 == 3) { s.Direction ^= 3; s.T480 = 0; } else { s.YVelInt = 4; s.YVelFrac = 0; } }
        }

        /// <summary>$B007 behavior 32: sky bomber (state table $B30F/$B314: 0 wait,
        /// 1 rise $B319, 2 hover $B32C, 3 anim $B378, 4 walk+jump $B398).</summary>
        private static void SkyBomber(ObjectSystem.Slot s, EnemyDispatcher ctx)
        {
            switch (s.StateLow & 0x0F)
            {
                case 0: // $B019 wait |dx| < $40, |dy| < $30
                    if (DistX(ctx, s) < 0x40 && DistY(ctx, s) < 0x30)
                    {
                        s.StateLow = 1;
                        SetAnim(s, 0x69);
                        s.Health ^= (byte)0x80;
                        s.T480 = 0x2D;
                    }
                    s.AnimFrame = 0; s.AnimTimer = 0; // $F869
                    break;
                case 1: // $B035 hover/throw
                    FacePlayerApply(ctx, s); // $F81D
                    if (s.AnimNumber != 0x6B)
                    {
                        if ((s.StateLow & 0x02) != 0) { SkyBomberReturn(s, ctx); return; }
                        if (s.AnimFrame == 5 && s.AnimTimer == 8) { SetAnim(s, 0x6B); return; }
                        return;
                    }
                    if (s.Flags == 0x50 || s.Flags == 0x10) return;
                    if (s.AnimFrame == 1 && s.AnimTimer == 0)
                    {
                        SkyBomberBomb(ctx, s);
                        return;
                    }
                    if (s.AnimFrame == 1 && s.AnimTimer == 0x10)
                    {
                        SetAnim(s, 0x6A);
                        s.T490 = 0;
                        s.StateLow |= 0x02;
                        return;
                    }
                    break;
                case 2: // $B0A2 return
                    if (s.T480 != 0) { s.T480--; return; }
                    if (s.AnimNumber != 0x72)
                    {
                        if (DistX(ctx, s) >= 0x40) { SetAnim(s, 0x72); return; }
                        SetAnim(s, 0x6B);
                        s.StateLow = 1;
                        s.T480 = 0x2D;
                        return;
                    }
                    SkyBomberReturn(s, ctx);
                    break;
            }
        }

        private static void SkyBomberReturn(ObjectSystem.Slot s, EnemyDispatcher ctx)
        {
            if (s.AnimFrame == 5 && s.AnimTimer == 8)
            {
                s.Health |= 0x80;
                s.StateLow = (byte)0x80;
                SetAnim(s, 0x69);
            }
        }

        private static void SkyBomberBomb(EnemyDispatcher ctx, ObjectSystem.Slot s)
        {
            ctx.Sfx?.Invoke(0x29);
            SpawnRainSplash(ctx, s); // $B0EC: anim $6C child, behavior 0B, param $13
        }

        /// <summary>$B1D3 behavior 3D: mini door prop (spawned child): gravity fall;
        /// mission-scene gating via $22/$32/$2F/$FC reads (no-op outside the cutscene).</summary>
        private static void MiniDoor(ObjectSystem.Slot s, EnemyDispatcher ctx)
        {
            if ((s.StateLow & 0x0F) == 0)
            {
                s.YVelFrac = 0; s.YVelInt = 3;
                s.StateLow = 1;
                // cutscene gating ($B1E5): table $22/$2F/$FC reads — skip outside scenes
            }
            byte st = (byte)(s.StateLow & 0x0F);
            if (st == 2) { s.T480--; if (s.T480 == 0) s.StateLow++; return; }
            if (st == 3) { if (s.T490 == 0) { StalkerArc(ctx, s, 0x62, 2); } EightDirMover(s, ctx); return; }
            if (s.AnimNumber == 0x6E) return;
            MoveY(s); // $F539-style rise/fall: falls with gravity via $F55E
            if (s.Y >= 0x4C) { s.BehaviorParam = 0x6E; }
        }

        /// <summary>$B2FC behaviors 40/41: hover bomber — bob arcs, bombs on the
        /// $B427/$B42B RNG pairs, $04A0 HP latch, anim $71/$74/$77/$79 cadence.</summary>
        private static void HoverBomber(ObjectSystem.Slot s, EnemyDispatcher ctx)
        {
            if ((s.StateLow & 0x0F) == 0)
            {
                s.XVelFrac = 0; s.XVelInt = 1;
                s.StateLow = 1;
            }
            if (s.AnimNumber == 0x71) { HoverBomberBombing(s, ctx); return; }
            if (s.AnimNumber == 0x74) { HoverBomberHop(s, ctx); return; }
            if (DistX(ctx, s) < 0x48) { SetAnim(s, 0x71); return; }
        }

        private static void HoverBomberBombing(ObjectSystem.Slot s, EnemyDispatcher ctx)
        {
            if ((s.StateLow & 0x02) != 0) { HoverBomberLatch(s, ctx); return; }
            if (s.AnimFrame == 2 && s.AnimTimer == 4)
            {
                FacePlayerApply(ctx, s); // $F81D
                HoverBomberBomb(ctx, s);
                s.StateLow |= 0x02;
                return;
            }
        }

        private static void HoverBomberLatch(ObjectSystem.Slot s, EnemyDispatcher ctx)
        {
            s.Health = 1;
            SetAnim(s, 0x74);
        }

        private static void HoverBomberHop(ObjectSystem.Slot s, EnemyDispatcher ctx)
        {
            bool landed = FallAndLand(ctx, s, 6, 2); // $F2D7($06,$02)
            if ((s.Direction & 1) != 0) WalkRight(ctx, s, 0, 2);
            else WalkLeft(ctx, s, 3, 2);
            if (landed && ctx.ProbeResult >= 3)
            {
                s.T490++;
                if (s.T490 == 4) { s.Direction ^= 3; s.T480 = 1; }
                else { s.YVelFrac = (byte)0xDC; s.YVelInt = 3; }
            }
        }

        private static void HoverBomberBomb(EnemyDispatcher ctx, ObjectSystem.Slot s)
        {
            int y = FindFreeSlot(ctx);
            if (y < 0) return;
            var t = ctx.Objects.Slots[y];
            t.Direction = s.Direction;
            int off = (s.Direction & 2) != 0 ? unchecked((short)0xFFF4) : 0x0C; // $B47F
            t.X = (ushort)((short)(s.X + off));
            t.Y = (ushort)(s.Y + 2);
            SpawnInit(ctx, y, 0x6F);
            t.Behavior = 0x0B;
            t.BehaviorParam = 0x0A;
            t.XVelFrac = 0; t.XVelInt = 2;
        }

        /// <summary>$B483 behavior 42: tower licker — anim $71 tongue at |dx| < $48,
        /// frame 2 → $B521 child (anim $70, behavior $43, param $40); anim $74 hop
        /// cycle ($B4D7) with RNG hops; HP = 1 latch.</summary>
        private static void Licker(ObjectSystem.Slot s, EnemyDispatcher ctx)
        {
            if ((s.StateLow & 0x0F) == 0)
            {
                s.XVelFrac = 0; s.XVelInt = 1;
                s.StateLow = 1;
            }
            if (s.AnimNumber == 0x71) { LickerTongue(s, ctx); return; }
            if (s.AnimNumber == 0x74) { LickerHop(s, ctx); return; }
            if (DistX(ctx, s) < 0x48) { SetAnim(s, 0x71); return; }
        }

        private static void LickerTongue(ObjectSystem.Slot s, EnemyDispatcher ctx)
        {
            if ((s.StateLow & 0x02) != 0)
            {
                s.Health = 1;
                SetAnim(s, 0x74);
                return;
            }
            if (s.AnimFrame == 2 && s.AnimTimer == 4)
            {
                FacePlayerApply(ctx, s); // $F81D
                LickerChild(ctx, s);
                s.StateLow |= 0x02;
            }
        }

        private static void LickerChild(EnemyDispatcher ctx, ObjectSystem.Slot s)
        {
            int y = FindFreeSlot(ctx);
            if (y < 0) return;
            var t = ctx.Objects.Slots[y];
            t.Direction = s.Direction;
            int off = (s.Direction & 2) != 0 ? unchecked((short)0xFFEE) : 0x12; // $B56C
            t.X = (ushort)((short)(s.X + off));
            t.Y = (ushort)(s.Y - 0x14);
            SpawnInit(ctx, y, 0x70);
            t.Behavior = 0x43;
            t.BehaviorParam = 0x40;
            t.Health = 0;
        }

        private static void LickerHop(ObjectSystem.Slot s, EnemyDispatcher ctx)
        {
            bool landed = FallAndLand(ctx, s, 6, 2); // $F2D7($06,$02)
            if ((s.Direction & 1) != 0) WalkRight(ctx, s, 0, 2);
            else WalkLeft(ctx, s, 3, 2);
            if (landed && ctx.ProbeResult >= 3)
            {
                s.T490++;
                if (s.T490 == 4) { s.Direction ^= 3; s.T480 = 1; }
                else { s.YVelFrac = (byte)0xDC; s.YVelInt = 3; }
            }
        }

        /// <summary>$B570 behavior 43: hop lobber — hops (RNG pairs $B5EB/$B5EE),
        /// SFX $34 per hop, lob arcs ($F2D7 $06,$02); state 3 = $F5C9 fall.</summary>
        private static void Lobber(ObjectSystem.Slot s, EnemyDispatcher ctx)
        {
            if ((s.StateLow & 0x0F) == 0)
            {
                s.XVelFrac = 0; s.T480 = 0;
                s.XVelInt = 1;
                s.StateLow = 1;
            }
            if (s.T480 == 3)
            {
                if ((s.StateLow & 0x02) == 0)
                {
                    s.StateLow |= 0x02;
                    s.YVelFrac = 0x44; s.YVelInt = 3;
                    s.XVelFrac = (byte)0x80; s.XVelInt = 0;
                }
                if ((s.Direction & 1) != 0) IntegrateXPos(s); else IntegrateXNeg(s);
                MoveY(s); // $F5C9
                return;
            }
            bool landed = FallAndLand(ctx, s, 6, 2); // $F2D7($06,$02)
            if (landed)
            {
                int i = s.T480 & 3;
                s.YVelFrac = BombHopFrac[i];
                s.YVelInt = (sbyte)BombHopInt[i];
                ctx.Sfx?.Invoke(0x34);
                s.T480++;
            }
            if ((s.Direction & 1) != 0) IntegrateXPos(s); else IntegrateXNeg(s);
        }

        /// <summary>$9822 behavior 46: grab-carry controller (shared with the swimmer
        /// state 4): watches the grabbed player, $C2F9 cutscene hooks ($98B0).</summary>
        private static void GrabbedCarrier(ObjectSystem.Slot s, EnemyDispatcher ctx)
        {
            if ((s.StateLow & 0x0F) == 0)
            {
                s.T480 = 0;
                s.StateLow = 1;
            }
            if (ctx.Player.State == 7) return;
            if (ctx.Player.State == 0x0B) { GrabbedWatch(ctx, s); return; }
            if (s.T480 == 0)
            {
                if (DistX(ctx, s) < 0x0C) return;
                if (DistY(ctx, s) >= 0x18 || DistY(ctx, s) < 0x0C) return;
                if (ctx.Player.YVelInt < 0) { ctx.Player.State = 4; return; }
                if (ctx.Player.State == 4) return;
                ctx.Player.State = 4;
                ctx.Sfx?.Invoke(0x2C);
                s.T480++;
                return;
            }
            GrabbedWatch(ctx, s);
        }

        /// <summary>$31 area id gate used by a few routines (falls back to 5).</summary>
        internal static int AreaOr5(EnemyDispatcher ctx)
        {
            int a = ctx.Player.AreaId;
            return a == 0 ? 5 : a;
        }

        /// <summary>$A53E behavior 2D: dropper spawner — arms at |dx| < $30 (param
        /// $57) or retreats (flags $D8, param 0); armed: anim frame $23/timer 4 →
        /// behavior-6 child (anim $80) at X±2; frame $0A/timer 2 → anim $7E;
        /// frame $1C → SFX $2D.</summary>
        private static void Dropper(ObjectSystem.Slot s, EnemyDispatcher ctx)
        {
            if ((s.StateLow & 0x0F) == 0)
            {
                s.StateLow = 1;
                s.T480 = 0;
                s.T490 = 0;
            }
            if ((s.StateLow & 0x02) == 0)
            {
                if (DistX(ctx, s) < 0x30)
                {
                    s.StateLow |= 0x02;
                    s.BehaviorParam = 0x57;
                    return;
                }
                s.Flags = 0xD8;
                s.BehaviorParam = 0;
                return;
            }
            FacePlayerApply(ctx, s); // $F81D
            if (s.AnimFrame == 0x23 && s.AnimTimer == 4)
            {
                int y = FindFreeSlot(ctx);
                if (y >= 0)
                {
                    var t = ctx.Objects.Slots[y];
                    t.X = (ushort)(s.X - 2);
                    t.Y = s.Y;
                    SpawnInit(ctx, y, 0x80);
                    t.Behavior = 0x06;
                }
                SetAnim(s, 0x7F);
                return;
            }
            if (s.AnimFrame == 0x0A && s.AnimTimer == 2) { SetAnim(s, 0x7E); return; }
            if (s.AnimFrame == 0x1C) ctx.Sfx?.Invoke(0x2D);
        }

        // ==================================================================
        // Touch/overlap primitives (used by the dispatcher post-pass too)
        // ==================================================================

        /// <summary>$FB7B enemy→player touch test (CC = touching).</summary>
        internal static bool TouchTest(EnemyDispatcher ctx, ObjectSystem.Slot s)
        {
            var p = ctx.Player;
            if (p.State == 7 || p.State == 0x0F) return false;
            if ((p.Flags & 0x80) == 0) return false;   // player inactive
            if (!s.Active || (s.Flags & 0x80) == 0) return false;
            if (s.Behavior == 7) return false;
            // $FB9F tests the WHOLE $0570 byte for zero, then masks to 5 bits: an object
            // with $0570=$40 (bit6 flash-path gate, class 0) still has a hitbox.
            if (s.BehaviorParam == 0) return false;
            int cls = s.BehaviorParam & 0x1F;
            int yHalf = TouchYHalf[cls];
            int pAnim = ctx.Objects.Slots[0].AnimNumber;
            if (pAnim == 6 || pAnim == 7) yHalf -= 8; // climbing
            int xHalf = TouchXHalf[cls];
            int dx = Math.Abs((short)(p.X - s.X));
            int dy = Math.Abs((short)(p.Y - s.Y));
            return dx < xHalf && dy < yHalf;
        }
    }

    /// <summary>
    /// Boss dispatch (bank 1 $8802-$8835) + the seven boss state machines and
    /// their shared framework, ported per docs/enemies_bosses.md section 1.3/1.4/2.
    /// Ids >= $47 route here from EnemyDispatcher. Boss HP accumulates in the
    /// global $EE (universal 16 damage points); $ED is the 30-frame hit
    /// invulnerability; $3B is the death-cinematic timer.
    /// </summary>
    public static class BossBehaviors
    {
        public static byte DamageAccum;   // $EE (universal HP: >= $10 = defeated)
        public static byte InvulnTimer;   // $ED
        public static byte DefeatTimer;   // $3B death cinematic countdown
        public static int ProjectileSlot; // $EF (last projectile slot that hit)

        public static void Reset()
        {
            DamageAccum = InvulnTimer = DefeatTimer = 0;
            ProjectileSlot = -1;
        }

        /// <summary>$B39E BossTakeProjectileHit: same 1/2/3 damage rule as enemies,
        /// accumulated in $EE; SFX $1D, hit puff, 30-frame invulnerability ($ED).</summary>
        public static bool TakeProjectileHit(EnemyDispatcher ctx, ObjectSystem.Slot s)
        {
            if (InvulnTimer != 0) return false;
            if (!ctx.ProjectileOverlapHit(s)) return false;
            ProjectileSlot = ctx.ProjSlot;
            byte dmg = ctx.ProjectileDamage(ctx.Objects.Slots[ctx.ProjSlot].AnimNumber);
            ctx.Sfx?.Invoke(0x1D);
            DamageAccum += dmg;
            InvulnTimer = 0x1E;
            ctx.SpawnHitPuff();
            return true;
        }

        /// <summary>$B36A hit-flash toggle; >= 16 damage → $B315 BossDefeat.</summary>
        public static void HitFlash(EnemyDispatcher ctx, ObjectSystem.Slot s)
        {
            if (InvulnTimer == 0) return;
            if (((InvulnTimer >> 3) & 1) != 0) s.Flags &= 0xF7;
            else s.Flags |= 0x08;
            InvulnTimer--;
            if (DamageAccum >= 0x10) BossDefeat(ctx, s);
        }

        /// <summary>$B315 BossDefeat: sweep slots 15..1 (the $51/$54/$57 cores
        /// persist), retask to behavior $4F, knockback, player state $0F (unless 6/7).</summary>
        public static void BossDefeat(EnemyDispatcher ctx, ObjectSystem.Slot s)
        {
            for (int i = 15; i >= 1; i--)
            {
                var t = ctx.Objects.Slots[i];
                if (!t.Active) continue;
                if (t.Behavior is 0x51 or 0x54 or 0x57) continue;
                EnemyBehaviors.KillSlot(t);
            }
            s.Behavior = 0x4F;
            s.StateLow = 0; s.T480 = 0; s.T490 = 0; s.T4A0 = 0; s.T4B0 = 0;
            ctx.Player.YVelFrac = 0xAB; ctx.Player.YVelInt = -1; // $F607 knockback
            if (ctx.Player.State != 6 && ctx.Player.State != 7) ctx.Player.State = 0x0F;
            ctx.BossDefeated?.Invoke();
        }

        /// <summary>$B4BA child-object projectile check: weak projectiles (anim
        /// $14/$16) are destroyed; others convert to behavior $1F, SFX $1A.</summary>
        public static void ConvertWeakProjectile(EnemyDispatcher ctx)
        {
            int p = ctx.ProjSlot;
            if (p < 0) return;
            var t = ctx.Objects.Slots[p];
            if (t.AnimNumber is 0x14 or 0x16) { EnemyBehaviors.KillSlot(t); return; }
            t.Behavior = 0x1F;
            t.Direction = t.Direction switch { 1 => 0x0A, 9 => 0x0A, 5 => 0x0A, 2 => 0x09, 0x0A => 0x09, 6 => 0x09, _ => t.Direction };
            ctx.Sfx?.Invoke(0x1A);
        }

        /// <summary>$F893 (ROM bytes at file 0x1F8A3): the anim $F880 hands the boss slot on the
        /// frame $3B reaches 1, indexed by $31. Measured: pilot1 $31=00 → $9A (f165), and
        /// pilot6_wolfduck $31=01 → $6B with anim frame $0A → $00, which proves the $FA6C call.</summary>
        static readonly byte[] BossFightAnims = { 0x9A, 0x6B, 0x4C, 0x4A, 0x50, 0x6F, 0x61 };

        public static void Dispatch(int slot, ObjectSystem.Slot s, byte behavior, EnemyDispatcher ctx)
        {
            // $8836/$88B6: only these seven bodies read $3B ($984E/$99AE/$9DDA/$A166/$A626/
            // $A992/$AFB6), and six of them read it behind their own $04D0&$0F init gate, so
            // the frame that first activates a boss takes the init pass and no decrement.
            if (DefeatTimer != 0 && behavior is 0x49 or 0x4A or 0x51 or 0x54 or 0x57 or 0x5A or 0x5F or 0x60 &&
                (behavior is 0x5F or 0x60 || (s.StateLow & 0x0F) != 0))   // $AFB6 follows $AFA0 INC $04D0,X
            {
                if (DefeatTimer == 1)                                      // $F880: CMP #$01 branch only
                {
                    ctx.RestorePlayerIdle();                                // $F886 LDA #$00 / $F888 STA $50
                    EnemyBehaviors.SetAnim(s, BossFightAnims[ctx.Player.AreaId]);   // $F88A LDY $31 / $F88C / $F88F JSR $FA6C
                }
                DefeatTimer--;                                              // $9855 DEC $3B
                if (behavior is 0x51 or 0x57 or 0x5A or 0x5F or 0x60)
                { s.AnimFrame = 0; s.AnimTimer = 0; }                        // $F869: STA $0410,X / $0430,X
                return;
            }
            switch (behavior)
            {
                case 0x47: break;                                   // $8936 RTS
                case 0x48: case 0x6D: case 0x6E: case 0x6F: HopLift(s, ctx); break; // $8989
                case 0x49: Bushroot(s, ctx); break;                 // $A5F7
                case 0x4A: Moliarty(s, ctx); break;                 // $A12C
                case 0x4B: TeleportShooter(s, ctx); break;          // $A846
                case 0x4C: case 0x4D: case 0x4E: FlameThrower(s, ctx); break; // $A4BD/$A4B3
                case 0x4F: DefeatPop(s, ctx); break;                // $B3F8
                case 0x50: QuackerjackCore(s, ctx); break;          // $9736
                case 0x51: QuackerjackBall(s, ctx); break;          // $9836
                case 0x52: QuackerjackChild(s, ctx); break;         // $97E6
                case 0x53: WolfduckController(s, ctx); break;       // $9D4C
                case 0x54: Wolfduck(s, ctx); break;                 // $9971
                case 0x55: WolfduckChild(s, ctx); break;            // $9BEC
                case 0x56: case 0x65: case 0x67: Spawner(s, ctx); break; // $8CE6
                case 0x57: Liquidator(s, ctx); break;               // $9DA7
                case 0x58: case 0x59: Geyser(s, ctx); break;        // $9FDE/$A05A
                case 0x5A: Megavolt(s, ctx); break;                 // $A977
                case 0x5B: MegavoltShotA(s, ctx); break;            // $AC90
                case 0x5C: MegavoltShotB(s, ctx); break;            // $ACF2
                case 0x5D: OrbChild(s, ctx); break;                 // $94E2
                case 0x5E: OrbCore(s, ctx); break;                  // $9350
                case 0x5F: case 0x60: SteelbeakMinion(s, ctx); break; // $AF91
                case 0x61: SteelbeakMinionC(s, ctx); break;         // $B1D4
                case 0x62: Steelbeak(s, ctx); break;                // $AD6F
                case 0x63: OrbSpawner(s, ctx); break;               // $933D (bank 1)
                case 0x64: SteelbeakFlame(s, ctx); break;           // $AD23
                case 0x66: BallChildWalker(s, ctx); break;          // $8937
                case 0x68: Stalactite(s, ctx); break;               // $8E7A
                case 0x69: GrabberVerifier(s, ctx); break;          // $9694
                case 0x6A: case 0x6B: TowerRiser(s, ctx); break;    // $90C5
                case 0x6C: SwingerChild(s, ctx); break;             // $923E
                case 0x70: case 0x71: case 0x72: case 0x73:
                case 0x74: case 0x75: case 0x76: case 0x77:
                case 0x78: case 0x79: case 0x7A: case 0x7B:
                case 0x7C: case 0x7D: MissionDoor(s, ctx); break;   // $96C9
                case 0x7E: DoorChild(s, ctx); break;                // $9083
                case 0x7F: CageDroid(s, ctx); break;                // $8F71
                default: break;
            }
        }

        // ---- shared minor boss helpers ----

        /// <summary>$8989 hop-lift: grabs the player (SFX $12) and carries upward.</summary>
        private static void HopLift(ObjectSystem.Slot s, EnemyDispatcher ctx)
        {
            if (s.StateLow == 0)
            {
                if (ctx.TouchTest(s))
                {
                    ctx.Sfx?.Invoke(0x12);
                    ctx.Player.State = 0x12; // grabbed
                    s.StateLow = 1;
                }
                return;
            }
            ctx.Player.Y = (ushort)(ctx.Player.Y - 1); // lift
            ctx.Player.X = s.X;
            if (s.Y < 0x20) s.Active = false;
        }

        /// <summary>$B3F8 defeat pop: rise $44/$03, flags $D0, SFX $1E; at Y >= $E0 →
        /// player state $15.</summary>
        private static void DefeatPop(ObjectSystem.Slot s, EnemyDispatcher ctx)
        {
            s.Flags |= 0xD0;
            ctx.Sfx?.Invoke(0x1E);
            if (s.StateLow == 0)
            {
                s.YVelFrac = 0x44; s.YVelInt = 3;
                s.StateLow = 1;
            }
            EnemyBehaviors.IntegrateYSub(s);
            if ((s.Direction & 1) != 0) EnemyBehaviors.IntegrateXPos(s); else EnemyBehaviors.IntegrateXNeg(s);
            if (s.Y >= 0xE0) ctx.Player.State = 0x15;
        }

        /// <summary>$8CE6 spawner: timed child spawn (anim $33).</summary>
        private static void Spawner(ObjectSystem.Slot s, EnemyDispatcher ctx)
        {
            if (s.T480 == 0)
            {
                int y = ctx.FindFreeSlot();
                if (y >= 0)
                {
                    var t = ctx.Objects.Slots[y];
                    t.X = s.X; t.Y = s.Y;
                    EnemyBehaviors.SpawnInit(ctx, y, 0x33);
                    t.Behavior = 0x66;
                }
                s.T480 = 0x40;
                return;
            }
            s.T480--;
        }

        /// <summary>$8937 ball child walker: short walk then pop.</summary>
        private static void BallChildWalker(ObjectSystem.Slot s, EnemyDispatcher ctx)
        {
            if (s.StateLow == 0)
            {
                s.StateLow = 1;
                s.T480 = 0x20;
                s.Direction = 1;
            }
            if ((s.Direction & 1) != 0) ctx.WalkRight(s, 0, 2); else ctx.WalkLeft(s, 3, 2);
            if (--s.T480 == 0) EnemyBehaviors.KillSlot(s);
        }

        /// <summary>$8E7A stalactite drop: fall and shatter.</summary>
        private static void Stalactite(ObjectSystem.Slot s, EnemyDispatcher ctx)
        {
            if (s.StateLow == 0)
            {
                s.StateLow = 1;
                s.YVelFrac = 0; s.YVelInt = 0;
            }
            EnemyBehaviors.ApplyGravity(s, ctx.Player.GravityFrac);
            EnemyBehaviors.IntegrateYSub(s);
            if (ctx.Probe(s, 6, 2) >= 3)
            {
                EnemyBehaviors.SetAnim(s, 0x64);
                s.Behavior = 0x05;
                s.StateLow = 1;
            }
        }

        private static void GrabberVerifier(ObjectSystem.Slot s, EnemyDispatcher ctx)
        {
            // $9694 verifies the grabber hand chain; despawns when its parent dies
            int hand = ctx.FindSlotWithBehavior(0x1B);
            if (hand < 0) EnemyBehaviors.KillSlot(s);
        }

        private static void TowerRiser(ObjectSystem.Slot s, EnemyDispatcher ctx)
        {
            EnemyBehaviors.IntegrateYSub(s); // $F55E rise through the tower
            if (s.Y < 0x10) EnemyBehaviors.KillSlot(s);
        }

        private static void SwingerChild(ObjectSystem.Slot s, EnemyDispatcher ctx)
        {
            EnemyBehaviors.Dispatch(0, s, 0x0B, ctx); // shares the 8-dir mover
        }

        private static void DoorChild(ObjectSystem.Slot s, EnemyDispatcher ctx)
        {
            if (s.StateLow == 0)
            {
                s.StateLow = 1;
                s.YVelFrac = 0; s.YVelInt = 3;
            }
            EnemyBehaviors.IntegrateYSub(s);
        }

        /// <summary>$8F71 cage droid (city $27): rises from the cage and hovers.</summary>
        private static void CageDroid(ObjectSystem.Slot s, EnemyDispatcher ctx)
        {
            if (s.StateLow == 0)
            {
                s.StateLow = 1;
                s.YVelFrac = 0x80; s.YVelInt = 0;
            }
            EnemyBehaviors.IntegrateYSub(s);
            if (s.Y < 0x40) { s.YVelFrac = 0; s.YVelInt = 0; }
        }

        // ---- 2.1 Quackerjack (mission 0) ----

        /// <summary>$9736 Quackerjack core (bank 1): the init pass writes $0530=#$00 $0540=#$01
        /// $0480=#$2D $0490=#$78 and INC $04D0,X; every later frame is the shared $0490 countdown,
        /// which RTS-es while $0490 != 0. Only at $0490 == 0 does the core act: phase bit1 goes to
        /// $9789, otherwise $F928 (|player-core|) below #$0C throws the ball child ($9769/$97B0) and
        /// sets phase bit1, otherwise the core walks by $0580&1 through $F4FD/$F51B and $F808.</summary>
        private static void QuackerjackCore(ObjectSystem.Slot s, EnemyDispatcher ctx)
        {
            // $9736 LDA $04D0,X / AND #$0F / BNE $9752
            if ((s.StateLow & 0x0F) == 0)
            {
                s.XVelFrac = 0x00;   // $973D STA $0530,X (A is 0 here)
                s.XVelInt = 0x01;    // $9740 LDA #$01 / $9742 STA $0540,X
                s.T480 = 0x2D;       // $9745 LDA #$2D / $9747 STA $0480,X
                s.T490 = 0x78;       // $974A LDA #$78 / $974C STA $0490,X
                s.StateLow++;        // $974F INC $04D0,X
            }
            // $9752 LDA $0490,X / BEQ $975B / DEC $0490,X / RTS
            if (s.T490 != 0) { s.T490--; return; }
            // $975B LDA $04D0,X / AND #$02 / BNE $9789
            if ((s.StateLow & 0x02) != 0) { CoreThrowPhase(s, ctx); return; }
            // $9762 JSR $F928 / CMP #$0C / BCS $9775: within 12 px of the player the core throws.
            // Measured in tmp_boss_pilot1: the throw fires on f242 and the ROM activates slot 0D.
            if (EnemyBehaviors.DistX(ctx, s) < 0x0C)
            {
                EnemyBehaviors.SetAnim(s, 0x93);   // $9769 LDA #$93 / $976B JSR $FA6C
                ThrowBallChild(ctx, s);            // $976E JSR $97B0
                s.StateLow++;                      // $9771 INC $04D0,X → phase bit1
                return;                            // $9774 RTS
            }
            // $9775 LDA $0580,X / AND #$01: $F4FD adds ($0540.$0530), $F51B subtracts it.
            if ((s.Direction & 1) != 0) EnemyBehaviors.IntegrateXPos(s);
            else EnemyBehaviors.IntegrateXNeg(s);
            // $9785 JSR $F808: bit6 of $0400,X takes bit0 of $0580,X (the incoming carry never
            // reaches bit6 through AND #$40, so a single mask/merge reproduces it exactly).
            s.Flags = (byte)((s.Flags & ~0x40) | ((s.Direction & 1) << 6));
        }

        /// <summary>$9789 core phase 2: anim $93 with timer #$08 converts to $92, then $0480 counts
        /// a #$2E-frame cadence. On reaching 0 the core drops back to phase 1 ($97A6 DEC $04D0,X)
        /// and $F81D re-faces the player; $97AC then runs for every path, including that one, so
        /// the freshly written #$2E is decremented in the same frame.</summary>
        private static void CoreThrowPhase(ObjectSystem.Slot s, EnemyDispatcher ctx)
        {
            if (s.AnimNumber == 0x93 && s.AnimTimer == 0x08)   // $9789-$9795
                EnemyBehaviors.SetAnim(s, 0x92);               // $9797/$9799 JSR $FA6C
            if (s.T480 == 0)                                   // $979C/$979F BNE $97AC
            {
                s.T480 = 0x2E;                                 // $97A1/$97A3
                s.StateLow--;                                  // $97A6 DEC $04D0,X
                EnemyBehaviors.FacePlayerApply(ctx, s);                  // $97A9 JSR $F81D
            }
            s.T480--;                                          // $97AC DEC $0480,X
        }

        /// <summary>$97B0 the core's throw service: a free slot ($FA4C, carry = none → $97E5 RTS)
        /// takes the core's $0580/$04F0/$0500, its row minus $0C in the low byte only ($0520 is
        /// copied unchanged), anim $47 through $FA84 — which also writes $04D0=#$80 and
        /// $0400=#$90 — behaviour $52 and $0570=#$0A.</summary>
        private static void ThrowBallChild(EnemyDispatcher ctx, ObjectSystem.Slot s)
        {
            int y = EnemyBehaviors.FindFreeSlot(ctx);              // $97B0 JSR $FA4C / $97B3 BCS $97E5
            if (y < 0) return;
            var t = ctx.Objects.Slots[y];
            t.Direction = s.Direction;                             // $97B5/$97B8
            t.X = s.X;                                             // $97BB..$97C4
            t.Y = (ushort)((s.Y & 0xFF00) | (byte)((byte)s.Y - 0x0C)); // $97C7-$97CD, 8-bit only
            EnemyBehaviors.SpawnInit(ctx, y, 0x47);                // $97D6/$97D8 JSR $FA84
            t.Behavior = 0x52;                                     // $97DB/$97DD
            t.BehaviorParam = 0x0A;                                // $97E0/$97E2
        }

        /// <summary>$9836 Quackerjack ball: destroyable (HP 0); bounces with RNG
        /// pairs ($9959/$9961/$9969), jump $F2D7 vel $28, probe $56/2 + floor snap.</summary>
        private static void QuackerjackBall(ObjectSystem.Slot s, EnemyDispatcher ctx)
        {
            // $9836 LDA $04D0,X / AND #$0F / BNE $984E: on the frame that takes the slot from
            // $80 to $81 only the init pass runs and $984D RTS-es, so no body and no physics.
            // ($984A JSR $B454 zeroes the $0600 collision window, which the port probes directly.)
            if ((s.StateLow & 0x0F) == 0)
            {
                s.XVelFrac = 0x80;   // $983D LDA #$80 / $983F STA $0530,X  (frac: $F51B reads $0540.$0530)
                s.XVelInt = 0x01;    // $9842 LDA #$01 / $9844 STA $0540,X  (integer) -> 1.502 px/frame
                s.StateLow++;       // $9847 INC $04D0,X
                return;             // $984D RTS
            }
            // $985B JSR $B36A — the boss hit-flash service ($ED/$EE/$EF globals). In pilot1 they
            // stay 0 from f150 to f600, so the only effect the capture constrains is the $B395
            // bit3 clear, which BossBehaviors.HitFlash does not model yet (G85.1).
            EnemyBehaviors.HitFlashShared(ctx, s);
            if (s.Behavior == 0x4F) return;      // $985E CMP #$4F → $984D RTS (the defeated body)
            TakeProjectileHit(ctx, s);           // $9865 JSR $B39E — the ROM ignores the carry
            if ((s.StateLow & 0x02) != 0)        // $9868 AND #$02 / D0 → JMP $98F6
            {
                BallBounce(s, ctx);
                return;
            }

            // $9872 the patrol: $0580 bit0 is the direction, mirrored into $0400 bit6 by
            // $F24B/$F2A6, and the step is the 8.8 X velocity (1.$80 = 1.502 px/frame).
            // Measured slot 0E: X F0→EE→ED→EB… with $0470 alternating 00/80, turning at $16/$E8.
            if ((s.Direction & 1) != 0)
            {
                s.Flags |= 0x40;                            // $9879 JSR $F24B
                EnemyBehaviors.IntegrateXPos(s);            // $987C JSR $F4FD
                if ((byte)s.X >= 0xE8) s.Direction ^= 3;    // $9882 CMP #$E8 / BCC $989E, else $9896
            }
            else
            {
                s.Flags = (byte)(s.Flags & ~0x40);          // $9889 JSR $F2A6
                EnemyBehaviors.IntegrateXNeg(s);            // $988C JSR $F51B
                if ((byte)s.X < 0x18) s.Direction ^= 3;     // $9892 CMP #$18 / BCS $989E, else $9896
            }
            BallRowTrigger(s, ctx);                         // $989E, also reached by fall-through
        }

        // $9959/$9961/$9969 (prg_bank1 bytes at 0x1959/0x1961/0x1969): the three launch tables
        // indexed by ($E4+$E5)&$07 for a ball that meets the player off the $D0/$50 rows.
        static readonly byte[] BallKickFrac = { 0x8E, 0xAB, 0x8E, 0xAB, 0x8E, 0xAB, 0x8E, 0xAB };
        static readonly byte[] BallKickInt = { 0x06, 0xFF, 0x06, 0xFF, 0x06, 0xFF, 0x06, 0xFF };
        static readonly byte[] BallKickMode = { 0x00, 0xFF, 0x00, 0xFF, 0x00, 0xFF, 0x00, 0xFF };

        /// <summary>$989E: the ball leaves its patrol only when its own row + 4 equals the
        /// player's row; $04D0 then goes to phase 2, $04B0 counts 15 frames and anim $99 shows.
        /// The launch velocity is picked by row: $D0 → +6.$8E with $0480=0, $50 → the $F5FC cut
        /// with $0480=$FF, anything else → the three tables above. pilot1 never matches
        /// ($051E=$D0 vs $0510=$54), so this block is transcription and is not covered by the
        /// f150..f600 comparison.</summary>
        private static void BallRowTrigger(ObjectSystem.Slot s, EnemyDispatcher ctx)
        {
            if ((byte)((byte)s.Y + 4) != (byte)ctx.Player.Y) return;   // $989E-$98A7 BNE $98F5
            s.StateLow++;                                              // $98A9 INC $04D0,X
            s.T4B0 = 0x0F;                                             // $98AC STA $04B0,X
            EnemyBehaviors.SetAnim(s, 0x99);                           // $98B1 JSR $FA6C
            byte row = (byte)s.Y;                                      // $98B6 LDA $0510,X
            if (row == 0xD0)
            {
                s.YVelFrac = 0x8E; s.YVelInt = 0x06; s.T480 = 0x00;    // $98BD-$98C9
                return;
            }
            if (row == 0x50)
            {
                EnemyBehaviors.CutVelocity(s);                         // $98CD JSR $F5FC
                s.T480 = 0xFF;                                         // $98D6
                return;
            }
            int i = EnemyBehaviors.RngE5Mask7();                       // $98DA E5 += E4, AND #$07
            s.YVelFrac = BallKickFrac[i];                              // $98E3/$98E6 ← $9959
            s.YVelInt = (sbyte)BallKickInt[i];                         // $98E9/$98EC ← $9961
            s.T480 = BallKickMode[i];                                  // $98EF/$98F2 ← $9969
        }

        /// <summary>$98F6 phase 2: $04B0 counts down first, then anim $AB and the vertical arc.
        /// $0480 selects the branch: 0 → $F5C9 integrate+decay, landing once the velocity turns
        /// negative; non-zero → $F5A4+$F5DA until the integer part drops below $FC. Both tails
        /// probe $EB52 at ($56,$02), snap with $EDD2, show anim $9A and DEC $04D0 back into the
        /// patrol (only the $992C tail clears $0480). Unexecuted in pilot1.</summary>
        private static void BallBounce(ObjectSystem.Slot s, EnemyDispatcher ctx)
        {
            if (s.T4B0 != 0) { s.T4B0--; return; }                       // $98F6-$98FE
            EnemyBehaviors.SetAnim(s, 0xAB);                             // $98FF/$9901 JSR $FA6C
            if (s.T480 != 0)                                             // $9904 BNE $992C
            {
                EnemyBehaviors.IntegrateYSub(s);                         // $992C JSR $F5A4
                EnemyBehaviors.ApplyGravity(s, ctx.Player.GravityFrac);  // $992E JSR $F5DA
                if ((byte)s.YVelInt >= 0xFC) return;                     // $9935 CMP #$FC / BCS $98F5
                if (EnemyBehaviors.Probe(ctx, s, 0x56, 2) == 0) return;  // $9939-$9946 JSR $EB52
                EnemyBehaviors.SnapToFloor(ctx, s);                      // $9948 JSR $EDD2
                EnemyBehaviors.SetAnim(s, 0x9A);                         // $994B JSR $FA6C
                s.StateLow--;                                            // $9950 DEC $04D0,X
                s.T480 = 0;                                              // $9953 STA $0480,X
                return;
            }
            EnemyBehaviors.ApplyYVelocityDecay(s, ctx.Player.GravityFrac); // $9909 JSR $F5C9
            if (s.YVelInt >= 0) return;                                  // $990F BPL $98F5
            if (EnemyBehaviors.Probe(ctx, s, 0x56, 2) == 0) return;      // $9911-$991E JSR $EB52
            EnemyBehaviors.SnapToFloor(ctx, s);                          // $9920 JSR $EDD2
            EnemyBehaviors.SetAnim(s, 0x9A);                             // $9923 JSR $FA6C
            s.StateLow--;                                                // $9928 DEC $04D0,X
        }

        /// <summary>$97E6 Quackerjack ball child: on its first frame it takes the arc velocity
        /// ($0560.$0550 = $01.2E rising, $0540.$0530 = $01.00), then walks horizontally by
        /// $0580&1 through $F4FD/$F51B while $F5C9 integrates and decays the arc. Once the integer
        /// velocity turns negative and equals $FC it flips to phase bit1, which routes every later
        /// frame to $F539.</summary>
        private static void QuackerjackChild(ObjectSystem.Slot s, EnemyDispatcher ctx)
        {
            if ((s.StateLow & 0x0F) == 0)                        // $97E6-$97EB
            {
                s.XVelFrac = 0x00;                               // $97ED (A = 0 from the AND #$0F)
                s.YVelFrac = 0x2E;                               // $97F0/$97F2
                s.YVelInt = 0x01;                                // $97F5/$97F7
                s.XVelInt = 0x01;                                // $97FA/$97FC
                s.StateLow++;                                    // $97FF INC $04D0,X, falls through
            }
            if ((s.StateLow & 0x02) != 0)                        // $9802/$9807 BNE $9833
            {
                EnemyBehaviors.IntegrateYRideSlot(s);            // $9833 JMP $F539
                return;
            }
            if ((s.Direction & 1) != 0) EnemyBehaviors.IntegrateXPos(s); // $9809-$9810
            else EnemyBehaviors.IntegrateXNeg(s);                        // $9816 JSR $F51B
            EnemyBehaviors.ApplyYVelocityDecay(s, ctx.Player.GravityFrac); // $9819 JSR $F5C9
            if (s.YVelInt >= 0) return;                          // $981C BPL $9832
            if ((byte)s.YVelInt != 0xFC) return;                 // $9821 CMP #$FC / BNE $9832
            s.StateLow++;                                        // $9825 INC $04D0,X
            s.YVelFrac = 0x00;                                   // $9828/$982A
            s.YVelInt = 0x02;                                    // $982D/$982F
        }

        // ---- 2.2 Wolfduck (mission 1 arena + boss) ----

        /// <summary>$9A1F / $9A2F: the frame-$0D RNG picks throw ($82, anim $68) or
        /// charge ($84, anim $6C) with the same $82/$84 ↔ $68/$6C pairing.</summary>
        static readonly byte[] WolfNextState =   // $9A1F
        {
            0x82,0x82,0x84,0x84,0x82,0x82,0x84,0x84,
            0x82,0x82,0x84,0x84,0x82,0x82,0x84,0x84
        };
        static readonly byte[] WolfNextAnim =    // $9A2F
        {
            0x68,0x68,0x6C,0x6C,0x68,0x68,0x6C,0x6C,
            0x68,0x68,0x6C,0x6C,0x68,0x68,0x6C,0x6C
        };

        /// <summary>$9D4C Wolfduck controller: init ($0530=0 $0540=1 $0480=$B4 $0490=$78),
        /// then $0490 freezes the slot for 120 frames; state 1 slides the LOW byte of X down
        /// by $0540.$0530 (only $04F0 is written, $0500 never changes) until Xlo = $28,
        /// state 2 holds for $0480 and steps back to state 1 on the $B5 re-arm.</summary>
        private static void WolfduckController(ObjectSystem.Slot s, EnemyDispatcher ctx)
        {
            if ((s.StateLow & 0x0F) == 0)                              // $9D4E BNE $9D68
            {
                s.XVelFrac = 0x00; s.XVelInt = 0x01;                   // $9D53-$9D58
                s.T480 = 0xB4; s.T490 = 0x78;                          // $9D5B-$9D62
                s.StateLow++;                                          // $9D65 INC $04D0,X
            }
            if (s.T490 != 0) { s.T490--; return; }                     // $9D68-$9D70
            if ((s.StateLow & 0x02) == 0)                              // $9D71-$9D76
            {
                int f = s.XFrac - s.XVelFrac;                          // $9D78-$9D7F (SEC/SBC)
                s.XFrac = (byte)f;
                s.X = (ushort)((s.X & 0xFF00) | (byte)(s.X - s.XVelInt - (f < 0 ? 1 : 0))); // $9D82-$9D88
                if ((s.X & 0xFF) == 0x28) s.StateLow++;                // $9D8B-$9D92
                return;
            }
            if (s.T480 == 0) { s.T480 = 0xB5; s.StateLow--; }          // $9D96-$9DA0
            s.T480--;                                                  // $9DA3
        }

        /// <summary>$9971 Wolfduck body: $04D0&$0F dispatches to $998E/$99AE/$9A3F/$9AA2/$9B4C.
        /// States are entered by `INC $04D0,X` (init→1, throw→fly) or by storing $81/$82/$84,
        /// so the phase lives in the low nibble and bit7 marks the slot active.</summary>
        private static void Wolfduck(ObjectSystem.Slot s, EnemyDispatcher ctx)
        {
            switch (s.StateLow & 0x0F)                                 // $9971-$9981
            {
                case 0: WolfduckInit(s, ctx); break;                   // $998E
                case 1: WolfduckWait(s, ctx); break;                   // $99AE
                case 2: WolfduckThrow(s, ctx); break;                  // $9A3F
                case 3: WolfduckFly(s, ctx); break;                    // $9AA2
                case 4: WolfduckCharge(s, ctx); break;                 // $9B4C
            }
        }

        /// <summary>$998E init: speed 3.$E6, hop velocity 6.$8E up, $0480=$14, state→1.
        /// The $99AA tail JSR $B454 (clears the $0600 collision window) is not ported here.</summary>
        private static void WolfduckInit(ObjectSystem.Slot s, EnemyDispatcher ctx)
        {
            s.XVelFrac = 0xE6; s.XVelInt = 3;
            s.YVelFrac = 0x8E; s.YVelInt = 6;
            s.T480 = 0x14;
            s.StateLow++;                                              // $99A7
        }

        /// <summary>$99AE state 1 — stands still and waits. The $3B death cinematic is taken
        /// by BossBehaviors.Dispatch before the handler runs. The helper duck (slot 15) parks
        /// at Xlo=$28; while its low byte is under 4 the body is handed the $67 wind-up anim.</summary>
        private static void WolfduckWait(ObjectSystem.Slot s, EnemyDispatcher ctx)
        {
            HitFlash(ctx, s);                                          // $B36A
            ConvertWeakProjectile(ctx);                                // $B4BA
            if (s.Behavior == 0x4F) return;                            // $99C1
            if (s.AnimNumber != 0x67 && (ctx.Objects.Slots[15].X & 0xFF) < 4)
            {
                EnemyBehaviors.SetAnim(s, 0x67);                       // $99D3
                ctx.Sfx?.Invoke(0x35);                                 // $99DA
                return;
            }
            if (s.AnimFrame == 8)                                      // $99E1
            {
                ctx.Player.PendingScreenEffect = 0x8A;                 // $1A = $8A
                return;
            }
            if (s.AnimFrame != 0x0D) return;                           // $99EA
            int i = EnemyBehaviors.RngE7PlusE6Wrap() & 0x0F;           // $99EE-$99F6
            s.StateLow = WolfNextState[i];                             // $99F7
            EnemyBehaviors.SetAnim(s, WolfNextAnim[i]);                // $99FD
            if (s.AnimNumber != 0x6C) return;                          // $9A08
            s.XVelFrac = 0xE6; s.XVelInt = 3;                          // $9A0A-$9A1B
            s.YVelFrac = 0x8E; s.YVelInt = 6;
        }

        /// <summary>$9A3F state 2 (anim $68): the runner child leaves at frame 1 / timer 2,
        /// the anim becomes $6B at frame 2 / timer 8, and while it is $6B the slot freezes on
        /// frame 1 counting $0480 from $15; the re-arm with the helper still parked at $28
        /// advances $82→$83 and cues the $67 wind-up.</summary>
        private static void WolfduckThrow(ObjectSystem.Slot s, EnemyDispatcher ctx)
        {
            HitFlash(ctx, s);                                          // $9A3F
            ConvertWeakProjectile(ctx);                                // $9A42
            if (s.Behavior == 0x4F) return;                            // $9A48
            if (s.AnimNumber == 0x6B)                                  // $9A4F
            {
                s.AnimFrame = 1; s.AnimTimer = 0;                     // $9A7B-$9A82
                if (s.T480 == 0)
                {
                    s.T480 = 0x15;                                     // $9A8A
                    if ((ctx.Objects.Slots[15].X & 0xFF) == 0x28)
                    {
                        s.StateLow++;                                  // $9A96 $82→$83
                        EnemyBehaviors.SetAnim(s, 0x67);               // $9A99
                    }
                }
                s.T480--;                                              // $9A9E
                return;
            }
            if (s.AnimFrame == 1 && s.AnimTimer == 2)                  // $9A53-$9A5F
            {
                EnemyBehaviors.FacePlayerApply(ctx, s);                // $9A61
                WolfduckSpawnChild(ctx, s);                            // $9A64
            }
            if (s.AnimFrame == 2 && s.AnimTimer == 8)                  // $9A67-$9A73
                EnemyBehaviors.SetAnim(s, 0x6B);                       // $9A75
        }

        /// <summary>$9AA2 state 3: $67 frame $0D hands the slot the $6A flight anim with
        /// speed 2.$80 and the $F5FC cut velocity, then every frame runs $F2D7 (fall/land)
        /// plus the slide. Facing is bit0 of $0490 here, not $0580; the arena edges are
        /// Xlo $E8/$18 and the turn hands the slot back to state 1 with anim $6B.</summary>
        private static void WolfduckFly(ObjectSystem.Slot s, EnemyDispatcher ctx)
        {
            HitFlash(ctx, s);                                          // $9AA2
            if (s.Behavior == 0x4F) return;                            // $9AAA
            TakeProjectileHit(ctx, s);                                 // $9AAC (carry unused here)
            if (s.AnimNumber == 0x67)                                  // $9AB2
            {
                if (s.AnimFrame != 0x0D) return;                       // $9ABB
                ctx.Player.PendingScreenEffect = 0x8B;                 // $1A = $8B
                EnemyBehaviors.SetAnim(s, 0x6A);                       // $9AC1
                s.XVelFrac = 0x80; s.XVelInt = 2;                      // $9AC6-$9ACD
                EnemyBehaviors.CutVelocity(s);                         // $9AD0 = $F5FC
                return;
            }
            bool landed = ctx.FallLand(s, 0x56, 2);                    // $9AD8, kept by PHP at $9ADB
            if ((s.T490 & 1) != 0)                                     // $9AE1
            {
                ctx.WalkRight(s, 0, 2);                                // $9AE7 $F200
                if ((s.X & 0xFF) >= 0xE8)                              // $9AED BCC $9B0D
                {
                    s.T490 = (byte)(s.T490 ^ 1);                       // $9AF1-$9AF6
                    s.Flags &= 0xBF;                                   // $9AF9 $F2A6
                    if ((ctx.Objects.Slots[15].X & 0xFF) != 0x28)
                    {
                        EnemyBehaviors.SetAnim(s, 0x6B);               // $9B03
                        s.StateLow = 0x81;                             // $9B0A
                    }
                }
            }
            else
            {
                ctx.WalkLeft(s, 3, 2);                                 // $9B12 $F254
                if ((s.X & 0xFF) < 0x18)                               // $9B1A BCS $9B38
                {
                    s.Flags |= 0x40;                                   // $9B1C $F24B
                    s.T490 = (byte)(s.T490 ^ 1);                       // $9B1F-$9B24
                    if ((ctx.Objects.Slots[15].X & 0xFF) != 0x28)
                    {
                        EnemyBehaviors.SetAnim(s, 0x6B);               // $9B2E
                        s.StateLow = 0x81;                             // $9B35
                    }
                }
            }
            if (!landed || ctx.ProbeResult < 3) return;                // $9B39 PLP / $9B3B
            s.YVelFrac = 0x00; s.YVelInt = 4;                           // $9B41-$9B48 bounce
        }

        /// <summary>$9B4C state 4 (charge): slides by $F4FD/$F51B on bit0 of $0490, runs
        /// $F2D7 for gravity, and on a type>=$03 probe flips round onto anim $68 and state
        /// $82 — i.e. the charge always ends as a throw.</summary>
        private static void WolfduckCharge(ObjectSystem.Slot s, EnemyDispatcher ctx)
        {
            HitFlash(ctx, s);                                          // $9B4C
            ConvertWeakProjectile(ctx);                                // $9B4F
            if (s.Behavior == 0x4F) return;                            // $9B57
            if (s.AnimNumber == 0x68) return;                          // $9B5E
            if ((s.T490 & 1) != 0) EnemyBehaviors.IntegrateXPos(s);    // $9B67 $F4FD
            else EnemyBehaviors.IntegrateXNeg(s);                      // $9B6D $F51B
            ctx.FallLand(s, 0x56, 2);                                  // $9B74
            if (ctx.ProbeResult < 3) return;                           // $9B7B
            s.T490 = (byte)(s.T490 ^ 1);                               // $9B7D-$9B82
            EnemyBehaviors.SetAnim(s, 0x68);                           // $9B85
            EnemyBehaviors.FacePlayerApply(ctx, s);                    // $9B8A
            s.StateLow = 0x82;                                         // $9B8D
        }

        /// <summary>$9B93 throw a runner child into a free slot ($FA4C): facing copied from
        /// $0580, X = parent ± $0026 chosen by bit1 of $0580, Ylo pinned to $C8 with the
        /// parent's Yhi, anim $6E via $FA84 plus flags bit5, behavior $55 and velocity 3.$00.</summary>
        private static void WolfduckSpawnChild(EnemyDispatcher ctx, ObjectSystem.Slot s)
        {
            int y = ctx.FindFreeSlot();                                // $FA4C
            if (y < 0) return;                                         // $9B96 BCS
            var t = ctx.Objects.Slots[y];
            t.Direction = s.Direction;                                 // $9B9A-$9B9D
            int dx = (s.Direction & 0x02) == 0 ? 0x26 : -0x26;         // $9BE8/$9BE9
            t.X = (ushort)(s.X + dx);                                  // $9BA3-$9BB7
            t.Y = (ushort)((s.Y & 0xFF00) | 0xC8);                      // $9BB9-$9BC2
            EnemyBehaviors.SpawnInit(ctx, y, 0x6E);                    // $9BC5
            t.Flags |= 0x20;                                           // $9BCA-$9BCF
            t.Behavior = 0x55;                                         // $9BD2
            t.BehaviorParam = 0x00;                                    // $9BD7
            t.YVelFrac = 0x00;                                         // $9BDC
            t.Health = 0x00;                                           // $9BDF
            t.YVelInt = 3;                                             // $9BE4
        }

        /// <summary>$9BEC Wolfduck runner child: runs, hops $F2D7 vel $56, throws at
        /// frame 1/timer 2, turns at the X = $E8/$18 screen edges.</summary>
        private static void WolfduckChild(ObjectSystem.Slot s, EnemyDispatcher ctx)
        {
            if (s.StateLow == 0) { s.StateLow = 1; s.T490 = 1; s.Direction = 2; }
            if ((s.T490 & 1) != 0) { EnemyBehaviors.IntegrateXNeg(s); EnemyBehaviors.WalkFaceL(s); }
            else { EnemyBehaviors.IntegrateXPos(s); EnemyBehaviors.WalkFaceR(s); }
            int screenX = (short)(s.X - ctx.Player.ScrollX);
            if (screenX >= 0xE8 || screenX < 0x18)
            {
                s.T4AF--; // kill when it exits (doc: DEC $04AF)
                if (s.T4AF == 0) EnemyBehaviors.KillSlot(s);
            }
            if (s.AnimFrame == 1 && s.AnimTimer == 2)
            {
                EnemyBehaviors.FacePlayerApply(ctx, s);
                ctx.Sfx?.Invoke(0x1D); // $9B93 throw
            }
            ctx.FallLand(s, 0x56, 2);
        }

        // ---- 2.3 Liquidator (mission 2) ----

        /// <summary>$9DA7 Liquidator: init ($0530=0 $0540=1, $0480=$0F); walk cycle
        /// turns at arena marks X = $C0/$80/$40 → RNG walk-time {00,30...}, anim
        /// {41,42...}, timer {00,01...}; attack frames spawn geysers at the player
        /// column (anim $45/$46, behavior $59/$58, $0570=$44, HP 1).</summary>
        private static void Liquidator(ObjectSystem.Slot s, EnemyDispatcher ctx)
        {
            EnemyBehaviors.HitFlashShared(ctx, s);
            if (s.StateLow == 0)
            {
                s.XVelFrac = 0; s.XVelInt = 1;
                s.T480 = 0x0F;
                s.StateLow = 1;
                return;
            }
            if (s.T480 != 0) { s.T480--; return; }
            if ((s.Direction & 1) != 0) ctx.WalkRight(s, 0, 2); else ctx.WalkLeft(s, 3, 2);
            int sx = (short)(s.X - ctx.Player.ScrollX);
            if (sx == 0xC0 || sx == 0x80 || sx == 0x40)
            {
                int i = EnemyBehaviors.RngE7PlusE6Wrap() & 0x0F; // E6 += E7
                s.T480 = new byte[] { 0x00,0x30,0x00,0x30,0x00,0x30,0x00,0x30,0x00,0x30,0x00,0x30,0x00,0x30,0x00,0x30 }[i];
                EnemyBehaviors.SetAnim(s, new byte[] { 0x41,0x42,0x41,0x42,0x41,0x42,0x41,0x42,0x41,0x42,0x41,0x42,0x41,0x42,0x41,0x42 }[i]);
                s.T490 = new byte[] { 0x00,0x01,0x00,0x01,0x00,0x01,0x00,0x01,0x00,0x01,0x00,0x01,0x00,0x01,0x00,0x01 }[i];
            }
            if (s.AnimNumber == 0x41 || s.AnimNumber == 0x42)
            {
                if (s.AnimFrame == 2 && s.AnimTimer == 8)
                {
                    EnemyBehaviors.SetAnim(s, 0x44);
                    s.BehaviorParam = 6;
                }
                if (s.AnimFrame == 3 || s.AnimFrame == 4) ctx.Sfx?.Invoke(0x37);
                if (s.T4A0 == 0 && (s.AnimFrame is 5 or 8 or 0x0B) && s.AnimTimer == 4)
                    SpawnGeyser(ctx, s);
            }
        }

        private static void SpawnGeyser(EnemyDispatcher ctx, ObjectSystem.Slot s)
        {
            int y = ctx.FindFreeSlot();
            if (y < 0) return;
            var t = ctx.Objects.Slots[y];
            t.X = ctx.Player.X;                 // player column
            t.Y = 0xF0;
            EnemyBehaviors.SpawnInit(ctx, y, (byte)(s.T480 != 0 ? 0x46 : 0x45));
            t.Behavior = (byte)(s.T480 != 0 ? 0x59 : 0x58);
            t.BehaviorParam = 0x44;
            t.Health = 1;
        }

        /// <summary>$9FDE/$A05A water geyser: erupts from the floor and retracts.</summary>
        private static void Geyser(ObjectSystem.Slot s, EnemyDispatcher ctx)
        {
            if (s.StateLow == 0)
            {
                s.StateLow = 1;
                s.YVelFrac = 0x40; s.YVelInt = 4;
                s.T480 = 0x3C;
            }
            if (s.T480 != 0)
            {
                s.T480--;
                EnemyBehaviors.IntegrateYSub(s); // erupt upward
                return;
            }
            EnemyBehaviors.IntegrateYAdd(s); // retract
            if (s.Y >= 0xF0) EnemyBehaviors.KillSlot(s);
        }

        // ---- 2.4 Moliarty (mission 3) ----

        /// <summary>$A12C Moliarty: walks toward the lit flamethrower post (slots
        /// $0C-$0E scan), douses it on landing (SFX $2C), jump arcs ($F2D7 vel $06)
        /// between floors, throws behavior-1 bombs (anim $40, param $0A) on the
        /// $0490/$0480 match.</summary>
        private static void Moliarty(ObjectSystem.Slot s, EnemyDispatcher ctx)
        {
            EnemyBehaviors.HitFlashShared(ctx, s);
            TakeProjectileHit(ctx, s);
            if (s.StateLow == 0)
            {
                s.T4BC = 1;
                s.T4BD = 0x0E;
                s.T4AC = 8;
                s.StateLow = 1;
                return;
            }
            int target = ctx.FindLitFlamethrower();
            if (s.T490 == s.T480) // $A1AC bomb cadence
            {
                int y = ctx.FindFreeSlot();
                if (y >= 0)
                {
                    var t = ctx.Objects.Slots[y];
                    t.X = s.X; t.Y = s.Y;
                    EnemyBehaviors.SpawnInit(ctx, y, 0x40);
                    t.Behavior = 0x01;
                    t.BehaviorParam = 0x0A;
                    t.Direction = 1;
                }
                ctx.Sfx?.Invoke(0x39);
            }
            if (target >= 0)
            {
                var f = ctx.Objects.Slots[target];
                int dx = (short)(f.X - s.X);
                if (Math.Abs(dx) > 4)
                {
                    if (dx > 0) ctx.WalkRight(s, 0, 2); else ctx.WalkLeft(s, 3, 2);
                }
                else if (s.Y >= f.Y)
                {
                    s.YVelFrac = 0; s.YVelInt = 6; // jump arc $F2D7 vel $06
                    ctx.FallLand(s, 6, 2);
                    // douse the post ($A33E)
                    f.T480 = 0;
                    f.Health = 0x0D;
                    EnemyBehaviors.SetAnim(f, 0x56);
                    ctx.Sfx?.Invoke(0x2C);
                }
            }
        }

        /// <summary>$A4BD/$A4B3 flamethrowers: round-robin charge ($04BC, 100-frame
        /// period); shoots an 8-dir flame (anim $4D, behavior $0B, param $13, sector
        /// velocities, SFX $29). HP 13/9 from the type table; destroyed at HP < $0A
        /// ($A503: anim 0, $0480=1, HP=$80).</summary>
        private static void FlameThrower(ObjectSystem.Slot s, EnemyDispatcher ctx)
        {
            if (s.T480 == 1) return; // destroyed ($A503 latch)
            if (s.Health < 0x0A)
            {
                EnemyBehaviors.SetAnim(s, 0);
                s.T480 = 1;
                s.Health = 0x80;
                return;
            }
            s.T4BC++;
            if (s.T4BC < 100) return;
            s.T4BC = 0;
            int sector = EnemyBehaviors.SectorToPlayer(ctx, s) & 0x0F;
            int y = ctx.FindFreeSlot();
            if (y < 0) return;
            var t = ctx.Objects.Slots[y];
            t.X = s.X; t.Y = s.Y;
            EnemyBehaviors.SpawnInit(ctx, y, 0x4D);
            t.Behavior = 0x0B;
            t.BehaviorParam = 0x13;
            t.XVelFrac = EnemyBehaviors.AimFracShared(sector);
            t.XVelInt = EnemyBehaviors.AimIntShared(sector);
            t.YVelFrac = EnemyBehaviors.AimYFracShared(sector);
            t.YVelInt = EnemyBehaviors.AimYIntShared(sector);
            t.Direction = EnemyBehaviors.DirToSector(ctx, s);
            ctx.Sfx?.Invoke(0x29);
        }

        // ---- 2.5 Bushroot (mission 4) ----

        /// <summary>$A5F7 Bushroot: hit check sets $04B0=1 (unless $04A0 == 2); hop
        /// loop (anim $50, counter from 8, $F2D7 vel $56); on hops $0490 ∈ {6,4,2}
        /// → 50% ceiling hop (rise above the ceiling table, probe up; head-bonk →
        /// fall, DEC $04A0); at $04A0 == 2 → anim $51 + drop vines ($048E=$88,
        /// $042E=$52), then back to hopping.</summary>
        private static void Bushroot(ObjectSystem.Slot s, EnemyDispatcher ctx)
        {
            EnemyBehaviors.HitFlashShared(ctx, s);
            if (TakeProjectileHit(ctx, s) && s.T4A0 != 2) s.T4B0 = 1;
            if (s.T4B0 != 0) { s.T4B0--; return; }
            if (s.StateLow == 0)
            {
                s.T4AE = 8;
                s.StateLow = 1;
            }
            int sx = (short)(s.X - ctx.Player.ScrollX);
            if (sx >= 0xF0) s.Direction = 2;
            else if (sx <= 0x10) s.Direction = 1;
            if (s.StateLow == 2)
            {
                // ceiling hop ($A6BB/$A6E1): rise until above the ceiling table
                s.YVelFrac += 0x40;
                EnemyBehaviors.IntegrateYSub(s);
                if (s.Y < BushrootCeiling(s.T4A0))
                {
                    s.YVelFrac = 0; s.YVelInt = 0;
                    if (ctx.Probe(s, 0x56, 1) >= 3)
                    {
                        s.YVelInt = -1;
                        s.T4A0--;
                        s.StateLow = 1;
                    }
                }
                if (s.T4A0 == 2)
                {
                    s.StateLow = 3;
                    s.T4B0 = 0x80;
                    EnemyBehaviors.SetAnim(s, 0x51);
                    ctx.Player.PendingScreenEffect = 0x88; // $048E = $88 (drop vines)
                    ctx.Player.PendingSpawnType = 0x52;     // $042E = $52
                    return;
                }
                return;
            }
            if (s.StateLow == 3)
            {
                if (--s.T4B0 == 0) { s.StateLow = 1; s.T4AE = 8; }
                return;
            }
            // hop loop ($A630)
            EnemyBehaviors.SetAnim(s, 0x50);
            if (s.T4AE == 0) s.T4AE = 8;
            s.T4AE--;
            if (s.T4AE is 6 or 4 or 2)
            {
                int roll = EnemyBehaviors.RngBushroot(s.T4AE) & 3; // E5 += E4 - $0490
                if (new byte[] { 0, 1, 0, 1 }[roll] != 0) s.StateLow = 2;
            }
            s.YVelInt = 0x56;
            ctx.FallLand(s, 0x56, 2);
            if ((s.Direction & 1) != 0) ctx.WalkRight(s, 0, 2); else ctx.WalkLeft(s, 3, 2);
        }

        private static int BushrootCeiling(int phase) => phase switch { 0 => 0x38, 1 => 0x48, _ => 0x58 };

        /// <summary>$A846 teleport shooter: every $0480 expiry ($6F/$41/$15 re-arm)
        /// spawns a flyer (anim $4E, behavior $0B, param $0A, SFX $3A) aimed by the
        /// sector tables; otherwise teleports via the RNG wait/position tables.</summary>
        private static void TeleportShooter(ObjectSystem.Slot s, EnemyDispatcher ctx)
        {
            if (s.T480 == 0)
            {
                int y = ctx.FindFreeSlot();
                if (y >= 0)
                {
                    var t = ctx.Objects.Slots[y];
                    t.X = s.X; t.Y = s.Y;
                    EnemyBehaviors.SpawnInit(ctx, y, 0x4E);
                    t.Behavior = 0x0B;
                    t.BehaviorParam = 0x0A;
                    t.Direction = EnemyBehaviors.DirToSector(ctx, s);
                }
                ctx.Sfx?.Invoke(0x3A);
                s.T480 = s.T480 switch { 0x6F => 0x41, 0x41 => 0x15, _ => 0x6F };
                return;
            }
            s.T480--;
            int r = EnemyBehaviors.RngE5Mask15();
            if ((r & 0x0F) < 4) return;
            s.X = new ushort[] { 0x20, 0xE0, 0x50, 0x98 }[r & 3];
        }

        /// <summary>$933D (bank 1) orb spawner: |dx| < $18 and |dy| < $10 arms $9E;
        /// volleys E4 += E5 EOR X → mask 7 → table {00,01,02,03,04,05,01,04} (no
        /// repeat), spawns from 6 fixed points, child anim $3D behavior $5D param
        /// $10, max 5 alive ($04AF).</summary>
        private static void OrbSpawner(ObjectSystem.Slot s, EnemyDispatcher ctx)
        {
            if (Math.Abs((short)(ctx.Player.X - s.X)) >= 0x18 ||
                Math.Abs((short)(ctx.Player.Y - s.Y)) >= 0x10) return;
            ctx.Player.PendingScreenEffect = 0xFF; // $9E = $FF
            if (s.T480 != 0) { s.T480--; return; }
            if (ctx.CountBehavior(0x5D) >= 5) return;
            int i = EnemyBehaviors.RngOrbVolley(s.X) & 7;
            i = new byte[] { 0x00,0x01,0x02,0x03,0x04,0x05,0x01,0x04 }[i];
            int sx = i < 3 ? 0x18 : 0xE8;
            int sy = 0x28 + (i % 3) * 0x30;
            int y = ctx.FindFreeSlot();
            if (y < 0) return;
            var t = ctx.Objects.Slots[y];
            t.X = (ushort)(ctx.Player.ScrollX + sx);
            t.Y = (ushort)sy;
            EnemyBehaviors.SpawnInit(ctx, y, 0x3D);
            t.Behavior = 0x5D;
            t.BehaviorParam = 0x10;
            s.T480 = 0x10;
        }

        /// <summary>$94E2 orb child: homes toward the player.</summary>
        private static void OrbChild(ObjectSystem.Slot s, EnemyDispatcher ctx)
        {
            s.Direction = EnemyBehaviors.DirToSector(ctx, s);
            EnemyBehaviors.Dispatch(0, s, 0x0B, ctx);
        }

        /// <summary>$9350 orb-spawner core (forest): pulses between two points.</summary>
        private static void OrbCore(ObjectSystem.Slot s, EnemyDispatcher ctx)
        {
            if (s.T480 == 0) { s.T480 = 0x20; s.Direction ^= 3; }
            s.T480--;
            EnemyBehaviors.Dispatch(0, s, 0x0B, ctx);
        }

        // ---- 2.6 Megavolt (mission 5) ----

        /// <summary>$A977 Megavolt: init ($0490=$1E); walk between X $F0/$10 (speed
        /// 3); at X = $DC/$24 → jump ($0550=$8E/$0560=6); rise ($F5C9); fall + floor
        /// probe → snap + E6 += E7 mask $0F picks attack (SFX $3B) anim $58/$5A;
        /// attack: $0490=$1E, RNG pattern A (anim $5C) or B (anim $5B), SFX $3C.</summary>
        private static void Megavolt(ObjectSystem.Slot s, EnemyDispatcher ctx)
        {
            EnemyBehaviors.HitFlashShared(ctx, s);
            if (s.StateLow == 0)
            {
                s.T490 = 0x1E;
                s.StateLow = 1;
                return;
            }
            switch (s.StateLow)
            {
                case 1: // walk
                {
                    if ((s.Direction & 1) != 0) ctx.WalkRight(s, 0, 2); else ctx.WalkLeft(s, 3, 2);
                    int sx = (short)(s.X - ctx.Player.ScrollX);
                    if (sx >= 0xDC || sx <= 0x24)
                    {
                        s.StateLow = 2;
                        EnemyBehaviors.SetAnim(s, 0x58);
                        s.YVelFrac = 0x8E; s.YVelInt = 6;
                    }
                    break;
                }
                case 2: // rise + fall ($AA3E)
                {
                    EnemyBehaviors.MoveY(s); // $F5C9
                    if (s.YVelInt < 0)
                    {
                        bool landed = ctx.FallLand(s, 0x56, 2);
                        if (landed)
                        {
                            int i = EnemyBehaviors.RngE7PlusE6Wrap() & 0x0F;
                            bool attack = new byte[] { 0,0xFF,0,0xFF,0,0xFF,0,0xFF,0,0xFF,0,0xFF,0,0xFF,0,0xFF }[i] != 0;
                            if (attack) ctx.Sfx?.Invoke(0x3B);
                            EnemyBehaviors.SetAnim(s, (byte)(attack ? 0x5A : 0x58));
                            s.StateLow = 3;
                            s.T490 = 0x1E;
                        }
                    }
                    break;
                }
                case 3: // attack phase ($AAA6)
                {
                    if (s.T490 == 0)
                    {
                        int r = EnemyBehaviors.RngE5Mask15() & 3;
                        s.T4A0 = (byte)(r < 2 ? 3 : 2);
                        EnemyBehaviors.SetAnim(s, (byte)(r < 2 ? 0x5C : 0x5B));
                        ctx.Sfx?.Invoke(0x3C);
                        s.StateLow = 0x86;
                        return;
                    }
                    s.T490--;
                    break;
                }
                default: // $86 barrage
                {
                    s.T4A0--;
                    if (s.T4A0 == 0)
                    {
                        s.StateLow = 1;
                        EnemyBehaviors.SetAnim(s, 0x59);
                    }
                    else if (s.T4A0 % 4 == 0)
                    {
                        int y = ctx.FindFreeSlot();
                        if (y >= 0)
                        {
                            var t = ctx.Objects.Slots[y];
                            t.X = s.X; t.Y = (ushort)(s.Y - 0x14);
                            EnemyBehaviors.SpawnInit(ctx, y, 0x5D);
                            t.Behavior = 0x5C;
                            t.Flags = 0xD8;
                        }
                    }
                    break;
                }
            }
        }

        /// <summary>$AC90 Megavolt shot A: falls with $F55E; at Y = 0 copies the
        /// player's X and spawns an orb (anim $5D, behavior $5C, flags $D8).</summary>
        private static void MegavoltShotA(ObjectSystem.Slot s, EnemyDispatcher ctx)
        {
            EnemyBehaviors.IntegrateYSub(s); // $F55E fall
            if ((s.Y & 0xFF) == 0)
            {
                s.X = ctx.Player.X;
                int y = ctx.FindFreeSlot();
                if (y >= 0)
                {
                    var t = ctx.Objects.Slots[y];
                    t.X = s.X; t.Y = s.Y;
                    EnemyBehaviors.SpawnInit(ctx, y, 0x5D);
                    t.Behavior = 0x5C;
                    t.Flags = 0xD8;
                }
                EnemyBehaviors.KillSlot(s);
            }
        }

        /// <summary>$ACF2 Megavolt shot B: rises 7/frame for $1E frames, then param
        /// $0570=2, flags $D0, $F539 rise — the aimed energy orb.</summary>
        private static void MegavoltShotB(ObjectSystem.Slot s, EnemyDispatcher ctx)
        {
            if (s.T480 < 0x1E)
            {
                s.YVelFrac = 0; s.YVelInt = 7;
                EnemyBehaviors.IntegrateYSub(s);
                s.T480++;
                return;
            }
            s.BehaviorParam = 2;
            s.Flags = 0xD0;
            EnemyBehaviors.IntegrateYAdd(s); // $F539 rise
        }

        // ---- 2.7 Steelbeak (mission 6) ----

        /// <summary>$AD6F Steelbeak: descend ($0560=0 $0550=$70 $0530=$80 $0540=2),
        /// land Y=$B0 → anim $5F, screen flash $1A=$8B, $0490=$1E, $04A0=$3C; walk
        /// between X $E0/$20 with the RNG hop gate ($AF74); frame 8 → anim $63
        /// wind-up; waits $0490 → SFX $3D, anim $66, two flames ($AEC5: anim $64,
        /// behavior $0B, param $10, offset ±$12, RNG speed table $AF2D, SFX $5F);
        /// waits $04A0 (60) then returns to walk. HP via $B39E/$EE.</summary>
        private static void Steelbeak(ObjectSystem.Slot s, EnemyDispatcher ctx)
        {
            EnemyBehaviors.HitFlashShared(ctx, s);
            TakeProjectileHit(ctx, s);
            switch (s.StateLow)
            {
                case 0: // descend
                    s.YVelInt = 0; s.YVelFrac = 0x70;
                    s.XVelFrac = 0x80; s.XVelInt = 2;
                    s.StateLow = 1;
                    break;
                case 1: // fall to Y = $B0
                    EnemyBehaviors.IntegrateYSub(s);
                    if (s.Y <= 0x70) s.Flags &= unchecked((byte)~0x20);
                    if (s.Y <= 0xB0)
                    {
                        EnemyBehaviors.SetAnim(s, 0x5F);
                        ctx.Player.PendingScreenEffect = 0x8B; // $1A = $8B flash
                        s.T490 = 0x1E;
                        s.T4A0 = 0x3C;
                        s.StateLow = 2;
                    }
                    break;
                case 2: // walk + RNG hop gate ($AF74)
                {
                    if (s.AnimFrame == 8) EnemyBehaviors.SetAnim(s, 0x63);
                    if ((s.Direction & 1) != 0) ctx.WalkRight(s, 0, 2); else ctx.WalkLeft(s, 3, 2);
                    int sx = (short)(s.X - ctx.Player.ScrollX);
                    if (sx >= 0xE0 || sx <= 0x20)
                    {
                        int r = EnemyBehaviors.RngE5Mask15() & 0x0F;
                        if (new byte[] { 0,0xFF,0xFF,0,0,0xFF,0xFF,0,0,0xFF,0xFF,0,0,0xFF,0xFF,0 }[r] != 0)
                        {
                            s.StateLow = 3;
                            s.T490 = 0x1E;
                        }
                    }
                    if (s.Y >= 0xB4) s.StateLow = 3;
                    break;
                }
                case 3: // wind-up → flames
                    if (s.T490 == 0)
                    {
                        ctx.Sfx?.Invoke(0x3D);
                        EnemyBehaviors.SetAnim(s, 0x66);
                        SpawnSteelbeakFlames(ctx, s);
                        s.StateLow = 0x86;
                        s.T4A0 = 0x3C;
                    }
                    else s.T490--;
                    break;
                case 0x86: // recover
                    if (s.T4A0 == 0) { s.StateLow = 2; EnemyBehaviors.SetAnim(s, 0x60); }
                    else s.T4A0--;
                    break;
            }
        }

        private static void SpawnSteelbeakFlames(EnemyDispatcher ctx, ObjectSystem.Slot s)
        {
            for (int n = 0; n < 2; n++)
            {
                int y = ctx.FindFreeSlot();
                if (y < 0) return;
                var t = ctx.Objects.Slots[y];
                int off = n == 0 ? -0x12 : 0x12;
                t.X = (ushort)((short)(s.X + off));
                t.Y = s.Y;
                EnemyBehaviors.SpawnInit(ctx, y, 0x64);
                t.Behavior = 0x0B;
                t.BehaviorParam = 0x10;
                int r = EnemyBehaviors.RngE4PlusE6() & 0x0F;
                int spd = new byte[] { 0x02,0x03,0x07,0x04,0x02,0x02,0x06,0x03,0x02,0x03,0x07,0x04,0x02,0x02,0x06,0x03 }[r];
                t.XVelInt = (byte)spd;
                t.XVelFrac = 0;
                t.Direction = (byte)(off < 0 ? 2 : 1);
            }
            ctx.Sfx?.Invoke(0x5F);
        }

        /// <summary>$AF91 Steelbeak minions: simple patrollers that throw $B1D4
        /// children on the $0480 cadence.</summary>
        private static void SteelbeakMinion(ObjectSystem.Slot s, EnemyDispatcher ctx)
        {
            if ((s.Direction & 1) != 0) ctx.WalkRight(s, 0, 2); else ctx.WalkLeft(s, 3, 2);
            if (ctx.ProbeResult >= 3) s.Direction ^= 3;
            if (s.T480 != 0) { s.T480--; return; }
            int y = ctx.FindFreeSlot();
            if (y >= 0)
            {
                var t = ctx.Objects.Slots[y];
                t.X = s.X; t.Y = (ushort)(s.Y - 0x14);
                EnemyBehaviors.SpawnInit(ctx, y, 0x61);
                t.Behavior = 0x61;
            }
            s.T480 = 0x60;
        }

        /// <summary>$B1D4 Steelbeak minion C: arcs and lob throws.</summary>
        private static void SteelbeakMinionC(ObjectSystem.Slot s, EnemyDispatcher ctx)
        {
            if (s.StateLow == 0) { s.StateLow = 1; s.YVelFrac = 0; s.YVelInt = 3; }
            ctx.FallLand(s, 6, 2);
            if ((s.Direction & 1) != 0) EnemyBehaviors.IntegrateXPos(s); else EnemyBehaviors.IntegrateXNeg(s);
        }

        /// <summary>$AD23 Steelbeak flame: 8-dir drift then despawn.</summary>
        private static void SteelbeakFlame(ObjectSystem.Slot s, EnemyDispatcher ctx)
        {
            EnemyBehaviors.Dispatch(0, s, 0x0B, ctx);
            if (++s.T480 > 0x40) EnemyBehaviors.KillSlot(s);
        }

        // ---- 2.8 mission-complete doors ($70-$7D → $96C9) ----

        private static readonly byte[] DoorScript5B =
            { 0x0F,0x11,0x13,0x15,0x17,0x19,0x1B,0x1D,0x1F,0x21,0x23,0x25,0x27,0x29 }; // $970C
        private static readonly byte[] DoorScript9C =
            { 0x10,0x12,0x14,0x16,0x18,0x1A,0x1C,0x1E,0x20,0x22,0x24,0x26,0x28,0x2A }; // $971A
        private static readonly byte[] DoorFlag66 =
            { 0xFF,0xF0,0xFF,0xF0,0xFF,0xF0,0xFF,0xF0,0xFF,0xF0,0xFF,0xF0,0xFF,0xF0 }; // $9728

        /// <summary>$96C9 mission door: state 0 waits for a projectile touch (SFX
        /// $1C, flags $90); state 1 waits for the player walk-in ($FB7B) → mission
        /// complete (SFX $23, player state $14, script pointers by id & $0F), then
        /// $FC29 + kill.</summary>
        private static void MissionDoor(ObjectSystem.Slot s, EnemyDispatcher ctx)
        {
            if ((s.StateLow & 0x0F) == 0)
            {
                if (!ctx.ProjectileOverlapHit(s)) return;
                s.Flags = 0x90;
                ctx.Sfx?.Invoke(0x1C);
                s.StateLow = 1;
                return;
            }
            if (!ctx.TouchTest(s)) return;
            ctx.Sfx?.Invoke(0x23);
            ctx.Player.State = 0x14; // level complete
            int idx = s.Behavior & 0x0F;
            if (idx >= 14) idx = 13;
            ctx.Player.PendingEvent = DoorScript5B[idx];
            ctx.Player.CheckpointId = DoorScript9C[idx];
            ctx.Player.PendingMissionFlag = DoorFlag66[idx];
            EnemyBehaviors.MarkPlacement(ctx, s);
            EnemyBehaviors.KillSlot(s);
        }
    }
}
