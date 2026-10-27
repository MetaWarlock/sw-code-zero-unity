p = r'D:\Art\SW Code Zero Unity\Assets\Darkwing\Core\ActorBank6Timing.cs'
s = open(p, encoding='utf-8').read()
old = '''            if (target.Behavior == 0x08 || target.Behavior == 0x09)
            {
                // $8837 / $8A1A pickups: JSR $FB7B, BCS to an RTS; a touch runs the pickup table.
                int touch = TouchNoHit(player.Objects, player, target, out bool hit);
                if (hit) throw new NotSupportedException((target.Behavior == 8 ? "883C" : "8A1F") + " pickup collected");
                return 6 + touch + 3 + 6;
            }'''
new = '''            if (target.Behavior == 0x08 || target.Behavior == 0x09)
                return PickupTest(target, slotIndex, player, events, target.Behavior == 0x09);'''
assert old in s
s = s.replace(old, new)
old = '''            cycles += 4 + 2 + Branch(0x8BD2, 0x8BD7, target.AnimNumber < 0x22) + 3;
            int touch = TouchNoHit(player.Objects, player, target, out bool hit);
            if (hit)
            {
                if (target.AnimNumber != 0x23 || events == null) throw new NotSupportedException("8BCD pickup variant or queue owner");
                int repair = player.HitLadder >= 3 ? 4 + 2 + 2 + 2 + 4 + 6
                    : player.HitLadder >= 2 ? 4 + 2 + 4 + 4 + 2 + 2 + 2 + 4 + 6
                    : 4 + 2 + 4 + 4 + 2 + 3 + 4 + 2 + (player.HitLadder == 1 ? 2 + 2 + 4 : 3) + 6;
                return cycles + 6 + touch + 2 + 6 + 48 + 6 + ClearSlotCycles + 29 + 2 + 6 + events.Enqueue(0x17) + 4 + 2 + 3 + repair;
            }
            return cycles + 6 + touch + 3 + 6;
        }'''
new = '''            cycles += 4 + 2 + Branch(0x8BD2, 0x8BD7, target.AnimNumber < 0x22) + 3;
            return cycles + PickupTest(target, slotIndex, player, events, target.AnimNumber < 0x22);
        }

        /// <summary>$8837 (anims $22+) / $8A1A (anims $1C+) from JSR $FB7B: a miss returns; a touch
        /// marks the placement ($FC29), clears the slot ($FA9A) and jumps through the anim table.</summary>
        private static int PickupTest(ObjectSystem.Slot target, int slotIndex, PlayerCore player, ActorEventTiming events, bool scoreTable)
        {
            int touch = TouchNoHit(player.Objects, player, target, out bool hit);
            if (!hit) return 6 + touch + 3 + 6;
            if (events == null) throw new NotSupportedException("8837 pickup queue owner absent");
            int cycles = 6 + touch + 2 + 6 + 48 + 6 + ClearSlotCycles + 29;
            byte anim = target.AnimNumber;
            if (scoreTable)
            {
                if (anim != 0x1C && anim != 0x1D) throw new NotSupportedException("8A36 pickup anim " + anim.ToString("X2"));
                // $8A3F / $8A4D: SFX $17, the rising icon, then add 1 / 5 to the bonus digits.
                cycles += 2 + 6 + events.Enqueue(0x17) + 6 + PickupIcon(target, slotIndex, player.Objects) + 2 + 3;
                if (anim == 0x1C) cycles += 3;                          // BNE $8A59
                return cycles + BonusDigitsAdd(player.ScoreDigits, anim == 0x1C ? 1 : 5);
            }
            if (anim == 0x22 || anim == 0x23)
            {
                if (anim != 0x23) throw new NotSupportedException("89D6 full repair pickup");
                int repair = player.HitLadder >= 3 ? 4 + 2 + 2 + 2 + 4 + 6
                    : player.HitLadder >= 2 ? 4 + 2 + 4 + 4 + 2 + 2 + 2 + 4 + 6
                    : 4 + 2 + 4 + 4 + 2 + 3 + 4 + 2 + (player.HitLadder == 1 ? 2 + 2 + 4 : 3) + 6;
                return cycles + 2 + 6 + events.Enqueue(0x17) + 4 + 2 + 3 + repair;
            }
            if (anim >= 0x26 && anim <= 0x28)
            {
                // $8865/$8889/$88AD: SFX $18, icon/bracket into $05BA/$05BC, $54, +10 saved meter
                // ($88FF), then $05C0 gates the $53 promotion and the $88CE copy ($88AD falls into it).
                cycles += 2 + 6 + events.Enqueue(0x18) + 2 + 4 + 2 + 4 + 2 + 3 + 2 + 6
                    + SavedMeterAddTen(player.GasEnergyHi, player.GasEnergyLo) + 4;
                if (player.ActiveGasEnergyLo == 0) return cycles + 3 + 6;
                return cycles + 2 + 2 + 3 + (anim == 0x28 ? 0 : 3) + 3 + 4 + 3 + 4 + 6;
            }
            throw new NotSupportedException("8853 pickup anim " + anim.ToString("X2"));
        }

        /// <summary>$88FF with A=$0A: $61=0 starts the meter at $70/$70; ten units keep the low digit
        /// and raise $60, capped at $79/$79.</summary>
        private static int SavedMeterAddTen(byte high, byte low)
        {
            int cycles = 3 + (low != 0 ? 3 : 2 + 2 + 3 + 3);
            if (low == 0) high = 0x70;
            cycles += 2 + 2 + 2 + 2 + 2 + 2 + 3 + 2 + 2 + 2 + 3 + 2;
            return cycles + (high == 0x79 ? 3 + 2 + 3 + 3 + 6 : 2 + 5 + 3 + 6);
        }

        /// <summary>$8A59: unless every digit is 9, add <paramref name="units"/> to $05CC and carry
        /// single units up through $05CA…$05C4 (a carry out of $05C4 is dropped).</summary>
        private static int BonusDigitsAdd(byte[] digits, int units)
        {
            int cycles = 0;
            for (int index = 0; index < 5; index++)
            {
                cycles += 4 + 2;
                if (digits[index] != 0x79) { cycles += 3; break; }
                cycles += 2;
                if (index == 4) return cycles + 6;
            }
            int value = (digits[4] & 15) + units;
            cycles += 4 + 2 + 2 + 3 + 2;
            if (value < 10) return cycles + 2 + 2 + 2 + 4 + 6;
            cycles += 3 + 2 + 2 + 2 + 2 + 4;
            for (int index = 3; index >= 0; index--)
            {
                cycles += 4 + 2 + 2 + 2 + 2;
                if ((digits[index] & 15) + 1 < 10) return cycles + 2 + 2 + 2 + 4 + 6;
                if (index == 0) return cycles + 3 + 6;
                cycles += 3 + 2 + 4;
            }
            return cycles;
        }

        /// <summary>$8B34 rising pickup icon. The pickup's own slot was just cleared by $FA9A, so the
        /// $FA4C scan may reuse it.</summary>
        private static int PickupIcon(ObjectSystem.Slot target, int ownSlot, ObjectSystem objects)
        {
            int cycles = 6 + FindFreeSlot(objects, out int slot, ownSlot) + Branch(0x8B37, 0x8B7C, slot < 0);
            if (slot < 0) return cycles + 6;
            return cycles + 3 + 4 * (4 + 5) + 4 + 2 + 2 + 2 + 4 + 3 + 6 + AnimSeedSlotYCycles
                + 2 + 5 + 2 + 5 + 2 + 5 + 5 + 5 + 2 + 5 + 6;
        }

'''
assert old in s
s = s.replace(old, new)
old = '''        private static int FindFreeSlot(ObjectSystem objects, out int slot)
        {
            int cycles = 2;
            for (slot = 15; slot >= 5; slot--)
            {
                cycles += 4;
                if (!objects.Slots[slot].Active) return cycles + 3 + 2 + 6;'''
new = '''        private static int FindFreeSlot(ObjectSystem objects, out int slot, int alsoFree = -1)
        {
            int cycles = 2;
            for (slot = 15; slot >= 5; slot--)
            {
                cycles += 4;
                if (!objects.Slots[slot].Active || slot == alsoFree) return cycles + 3 + 2 + 6;'''
assert old in s
s = s.replace(old, new)
s = s.replace('if (target.Behavior == 0x0A) return Behavior0A(target, player, tiles, events);',
              'if (target.Behavior == 0x0A) return Behavior0A(target, slotIndex, player, tiles, events);')
s = s.replace('private static int Behavior0A(ObjectSystem.Slot target, PlayerCore player, ITileMap tiles, ActorEventTiming events)',
              'private static int Behavior0A(ObjectSystem.Slot target, int slotIndex, PlayerCore player, ITileMap tiles, ActorEventTiming events)')
open(p, 'w', encoding='utf-8').write(s)
print('ok')
