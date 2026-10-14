using System;

namespace Darkwing.Core
{
    /// <summary>
    /// Enemy behavior dispatch — full transcription of bank 6 $8000-$81BF:
    /// per active slot 1-15, behavior id ($04E0) &lt; $47 jumps through the
    /// $825F/$82A7 pointer table (EnemyBehaviors), then the per-slot
    /// post-processing $8032-$816C runs BEFORE each behavior:
    ///   1. $803C hit-flash: $04C0 drives the $0400 bit3 compose-disable blink,
    ///      gated by $0570 bit6 (can-be-hit).
    ///   2. $8066 $FAAE/$FB08 player-projectile overlap → weapon damage by the
    ///      projectile's anim ($13/$14/$16/$19/$1A = 1, $18 = 2, $17/$29 = 3),
    ///      HP &lt;= 0 → $80F2 death (behavior 5 corpse pop, SFX $1E), otherwise
    ///      $04C0 = $0F flash + hit puff (the projectile slot retasks to anim
    ///      $14 unless it is behavior $07 / anim $17/$18); $81C0 special deaths
    ///      for ids {0F,17,1D,2A,30,32,34,42}: thunder/weapon-1 clears them,
    ///      other gas converts to behavior $1F.
    ///   3. $813B enemy→player contact: $FB7B touch (guards + $FBE9/$FC09
    ///      half-size tables) → SFX $15 + $7C = $3C + $F9C2; spiky armor anims
    ///      ($40/$4E/$9F/$7D/$62/$55/$A6) with a direction mismatch and |dx|
    ///      &gt;= 8 bounce instead (SFX $1A, retask to behavior $06).
    /// Ids &gt;= $47 route to BossBehaviors (bank 1 $8802). The boss slot still
    /// gets hit-flash and contact, but the generic weapon-damage branch is
    /// guarded by the HP $80 bit (gas-immune) — bosses take damage through
    /// BossBehaviors.TakeProjectileHit ($B39E, global $EE accumulator).
    /// </summary>
    public sealed class EnemyDispatcher
    {
        private readonly byte[] _bank6;
        public readonly ObjectSystem Objects;
        public readonly PlayerCore Player;
        public ITileMap Tiles;                          // $EB52 probe backend
        public System.Action<int> Sfx;                  // $F84E/$F850 sites

        /// <summary>$0F — the projectile slot found by the last $FAAE scan.</summary>
        public int ProjSlot { get; private set; } = -1;

        /// <summary>$10 — the last $EB52 probe result (max tile type).</summary>
        public int ProbeResult { get; set; }

        /// <summary>$06/$07 — the last probe point sampled; $ED85/$ED9D/$EDB3/$EDD2 align to it.</summary>
        public int ProbePointX;
        public int ProbePointY;

        public EnemyDispatcher(byte[] bank6, ObjectSystem objects, PlayerCore player,
                               Darkwing.Audio.AudioSequencer audio = null)
        {
            _bank6 = bank6;
            Objects = objects;
            Player = player;
            Sfx = audio != null ? id => audio.PlaySfx(id) : (System.Action<int>)null;
            EnemyBehaviors.ResetRng();
            BossBehaviors.Reset();
        }

        /// <summary>Per-frame pass (the master loop's bank-6 task; bank 7 $F8A7).</summary>
        public void RunFrame()
        {
            Player.GravityFrac = 0x40; // $99 = $40 set by the bank6/bank1 dispatchers
            ProjSlot = -1;
            // $8000 loop: slot 1's behavior runs first, then for slots 2..15 the
            // post-processing precedes the behavior (the RTS return address $8027
            // interleaves them exactly this way).
            DispatchSlot(1, postProcess: false);
            for (int slot = 2; slot < ObjectSystem.SlotCount; slot++)
                DispatchSlot(slot, postProcess: true);
        }

        private void DispatchSlot(int slot, bool postProcess)
        {
            var s = Objects.Slots[slot];
            if (postProcess) PostProcess(s);
            if (!s.Active) return;                      // $04D0 bit7
            byte behavior = s.Behavior;
            if (behavior >= 0x47)
            {
                BossBehaviors.Dispatch(slot, s, behavior, this); // bank 1 $8802
                return;
            }
            EnemyBehaviors.Dispatch(slot, s, behavior, this);
        }

        // ==================================================================
        // $8032-$816C per-slot post-processing
        // ==================================================================
        private void PostProcess(ObjectSystem.Slot s)
        {
            // $8032: $0570,X bit6 gate for the flash/projectile handling
            if ((s.BehaviorParam & 0x40) == 0)
            {
                ContactCheck(s); // $813B
                return;
            }
            // $803C hit-flash: toggle $0400 bit3 from bit3 of the timer
            if (s.HitFlash != 0)
            {
                if (((s.HitFlash >> 3) & 1) != 0) s.Flags &= 0xF7;   // compose on
                else s.Flags |= 0x08;                                 // compose off
                s.HitFlash--;
            }
            else
            {
                s.Flags &= 0xF7;
            }
            // $8066 projectile overlap
            if (!ProjectileOverlapHit(s))
            {
                ContactCheck(s); // $813B
                return;
            }
            // $8079 falling spikes ($1E/$34): the projectile must be below the
            // enemy top (custom $8079 check)
            if (s.Behavior is 0x1E or 0x34)
            {
                var proj = Objects.Slots[ProjSlot];
                int diff = (short)(proj.Y - s.Y);
                if (diff >= 0) { SpecialDeathCheck(s); return; }       // $8090
                if (-diff <= 4) { SpecialDeathCheck(s); return; }      // within 4 px above
            }
            // $808B: HP >= $80 (gas-immune / boss) → special-death check only
            if ((sbyte)s.Health < 0) { SpecialDeathCheck(s); return; }
            if (s.Health == 0) { Death(s); return; }
            // $8096 weapon damage by the projectile's anim
            Sfx?.Invoke(0x1D);
            s.Health = (byte)(s.Health - ProjectileDamage(Objects.Slots[ProjSlot].AnimNumber));
            if ((sbyte)s.Health < 0) { Death(s); return; }
            s.HitFlash = 0x0F;
            // $80D8: the projectile bursts (unless behavior $07 / anim $17/$18)
            var p = Objects.Slots[ProjSlot];
            if (p.Behavior != 0x07 && p.AnimNumber != 0x17 && p.AnimNumber != 0x18)
                SpawnHitPuff();
            ContactCheck(s);
        }

        /// <summary>Weapon damage by projectile anim ($80A0 table).</summary>
        public byte ProjectileDamage(byte projAnim)
        {
            switch (projAnim)
            {
                case 0x13: case 0x14: case 0x16: case 0x19: case 0x1A: return 1;
                case 0x18: return 2;
                case 0x17: case 0x29: return 3;
                default: return 1;
            }
        }

        /// <summary>$80F2 death: corpse pop (behavior 5) with rise velocity + SFX $1E.</summary>
        private void Death(ObjectSystem.Slot s)
        {
            if (s.Behavior == 0x05) return; // already dying ($816C guard)
            var p = Objects.Slots[ProjSlot];
            if (p.Behavior != 0x07 && p.AnimNumber != 0x17 && p.AnimNumber != 0x18)
                SpawnHitPuff();
            s.StateLow = 0;              // $04D0 = $80 (keep active, clear state)
            s.BehaviorParam = 0;
            s.XVelInt = 0;               // $0540
            s.PlacementIndex = 0xFF;
            s.Behavior = 0x05;
            s.YVelFrac = 0x44; s.YVelInt = 3;
            s.XVelFrac = 0x80;
            Sfx?.Invoke(0x1E);
        }

        /// <summary>$81C0 special deaths: ids {0F,17,1D,2A,30,32,34,42}.</summary>
        private void SpecialDeathCheck(ObjectSystem.Slot s)
        {
            switch (s.Behavior)
            {
                case 0x0F: case 0x17: case 0x1D: case 0x2A:
                case 0x30: case 0x32: case 0x34: case 0x42:
                    break;
                default:
                    return; // $81BF RTS
            }
            var p = Objects.Slots[ProjSlot];
            if (p.Behavior == 0x1F) return;              // $821C
            if (p.AnimNumber == 0x14 || p.AnimNumber == 0x16)
            {
                // $821D: clear the enemy outright
                s.Active = false; s.Flags = 0; s.Behavior = 0; s.PlacementIndex = 0xFF;
                return;
            }
            // $822E + $81FD: convert the projectile into a friendly pop (behavior $1F)
            p.Direction = p.Direction switch
            {
                0x01 or 0x09 or 0x05 => 0x0A,
                0x02 or 0x0A or 0x06 => 0x09,
                _ => p.Direction
            };
            p.XVelFrac = 0x80; p.YVelFrac = 0x80;
            p.XVelInt = 3; p.YVelInt = 3;
            p.Behavior = 0x1F;
            p.BehaviorParam = 0;
            Sfx?.Invoke(0x1A);
        }

        /// <summary>$813B enemy→player contact.</summary>
        private void ContactCheck(ObjectSystem.Slot s)
        {
            if (Player.InvulnFrames != 0) return;           // $7C
            if (!EnemyBehaviors.TouchTest(this, s)) return; // $FB7B (CS = not touching)
            byte playerAnim = Objects.Slots[0].AnimNumber;
            if (playerAnim == 0x04 || playerAnim == 0x0D)
            {
                // $816F spiky-armor bounce check
                switch (s.AnimNumber)
                {
                    case 0x40: case 0x4E: case 0x9F: case 0x7D:
                    case 0x62: case 0x55: case 0xA6:
                    {
                        int dir = s.AnimNumber == 0x4E ? s.Direction & 3 : s.Direction;
                        if (dir == Player.DirBits) break;          // same dir → hurt
                        if (EnemyBehaviors.DistX(this, s) < 8) break;
                        Sfx?.Invoke(0x1A);                          // bounce
                        if (s.Behavior != 0x06)
                        {
                            s.StateLow = (byte)0x80;
                            s.Behavior = 0x06;
                        }
                        return;
                    }
                }
            }
            if (s.AnimNumber < 0x3F) return;                // $814F: harmless classes
            if (Player.State == 0x07 || Player.State == 0x12) return;
            Sfx?.Invoke(0x15);                   // $8160
            Player.InvulnFrames = 0x3C;          // $8165 ($F9C2 only runs on $7C=$3C)
            Player.TakeHit(s.AnimNumber);        // $F9C2, with X still = this slot ($FA29)
        }

        // ==================================================================
        // Shared primitive surface (EnemyBehaviors / BossBehaviors call these)
        // ==================================================================

        /// <summary>$FAAE/$FB08 projectile→enemy overlap; sets ProjSlot ($0F) on hit.</summary>
        public bool ProjectileOverlapHit(ObjectSystem.Slot target)
        {
            for (int y = 4; y >= 2; y--) // $10 counts 4..2 ($FB06 loop)
            {
                var p = Objects.Slots[y];
                if (!p.Active || (p.Flags & 0x80) == 0) continue;
                if (p.Behavior == 0x1F) continue;
                if (!target.Active) continue;
                if (target.Behavior is 0x00 or 0x01 or 0x02 or 0x03 or 0x04) continue;
                if ((target.Flags & 0x80) == 0) continue;
                if ((target.Flags & 0x04) != 0) continue; // bit2: projectiles pass through
                int dx = Math.Abs((short)(p.X - target.X));
                int dy = Math.Abs((short)(p.Y - target.Y));
                int cls = target.BehaviorParam & 0x1F;
                if (dx < EnemyBehaviors.ProjectileXSize[cls] &&
                    dy < EnemyBehaviors.ProjectileYSize[cls])
                {
                    ProjSlot = y;
                    return true; // CC = hit
                }
            }
            return false;
        }

        /// <summary>$F872 retask the projectile slot into a hit puff (anim $14, behavior 0).</summary>
        public void SpawnHitPuff()
        {
            if (ProjSlot < 0) return;
            var p = Objects.Slots[ProjSlot];
            EnemyBehaviors.SetAnim(p, 0x14);
            p.Behavior = 0x00;
            p.BehaviorParam = 0x00;
        }

        /// <summary>$FA4C free object slot 15..4 (-1 = none).</summary>
        public int FindFreeSlot() => EnemyBehaviors.FindFreeSlot(this);

        public int FindSlotWithBehavior(byte behavior)
        {
            for (int i = 15; i >= 1; i--)
            {
                var t = Objects.Slots[i];
                if (t.Active && t.Behavior == behavior) return i;
            }
            return -1;
        }

        public int CountBehavior(byte behavior)
        {
            int n = 0;
            for (int i = 15; i >= 1; i--)
            {
                var t = Objects.Slots[i];
                if (t.Active && t.Behavior == behavior) n++;
            }
            return n;
        }

        /// <summary>$A3D8 scan slots $0C-$0E for the lit flamethrower (Moliarty).</summary>
        public int FindLitFlamethrower()
        {
            for (int i = 0x0C; i <= 0x0E; i++)
            {
                var t = Objects.Slots[i];
                if (t.Active && t.Behavior is 0x4C or 0x4D or 0x4E && t.T480 != 0 && t.T480 != 1)
                    return i;
            }
            return -1;
        }

        /// <summary>$EB52 probe (max tile type at the offset set); records ProbeResult ($10).</summary>
        public int Probe(ObjectSystem.Slot s, int setX, int setY)
        {
            ProbeResult = EnemyBehaviors.Probe(this, s, setX, setY);
            return ProbeResult;
        }

        /// <summary>$F200 move right with wall probe/snap (slot wrapper).</summary>
        public void WalkRight(ObjectSystem.Slot s, int probeSet, int probeY) =>
            EnemyBehaviors.WalkRight(this, s, probeSet, probeY);

        /// <summary>$F254 move left with wall probe/snap (slot wrapper).</summary>
        public void WalkLeft(ObjectSystem.Slot s, int probeSet, int probeY) =>
            EnemyBehaviors.WalkLeft(this, s, probeSet, probeY);

        /// <summary>$F2D7 fall-and-land (enemy path); also records the probe result.</summary>
        public bool FallLand(ObjectSystem.Slot s, int probeSet, int probeY)
        {
            bool landed = EnemyBehaviors.FallAndLand(this, s, probeSet, probeY);
            Probe(s, probeSet, probeY);
            return landed;
        }

        /// <summary>$FB7B enemy→player touch test.</summary>
        public bool TouchTest(ObjectSystem.Slot s) => EnemyBehaviors.TouchTest(this, s);

        /// <summary>$F880 restore the player idle anim per world (boss cinematic).</summary>
        public void RestorePlayerIdle()
        {
            Player.State = 0;
            Player.SetAnimPlayer(0x01);
            EnemyBehaviors.SetAnim(Objects.Slots[0], 0x01);
        }

        /// <summary>
        /// Serialize future-affecting dispatcher state (RNG counters $E4-$E7, boss
        /// globals, per-slot behavior timers).
        /// </summary>
        public void SaveState(System.IO.BinaryWriter w)
        {
            w.Write(EnemyBehaviors.RngE4);
            w.Write(EnemyBehaviors.RngE5);
            w.Write(EnemyBehaviors.RngE6);
            w.Write(EnemyBehaviors.RngE7);
            w.Write(BossBehaviors.DamageAccum);
            w.Write(BossBehaviors.InvulnTimer);
            w.Write(BossBehaviors.DefeatTimer);
            w.Write((byte)ProjSlot);
            foreach (var s in Objects.Slots)
            {
                w.Write(s.T480); w.Write(s.T490); w.Write(s.T4A0); w.Write(s.T4B0);
            }
        }

        public void LoadState(System.IO.BinaryReader r)
        {
            EnemyBehaviors.RngE4 = r.ReadByte();
            EnemyBehaviors.RngE5 = r.ReadByte();
            EnemyBehaviors.RngE6 = r.ReadByte();
            EnemyBehaviors.RngE7 = r.ReadByte();
            BossBehaviors.DamageAccum = r.ReadByte();
            BossBehaviors.InvulnTimer = r.ReadByte();
            BossBehaviors.DefeatTimer = r.ReadByte();
            ProjSlot = r.ReadByte();
            foreach (var s in Objects.Slots)
            {
                s.T480 = r.ReadByte(); s.T490 = r.ReadByte();
                s.T4A0 = r.ReadByte(); s.T4B0 = r.ReadByte();
            }
        }
    }
}
