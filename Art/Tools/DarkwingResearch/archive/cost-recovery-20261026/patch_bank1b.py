p = r'D:\Art\SW Code Zero Unity\Assets\Darkwing\Core\ActorTailTiming.cs'
s = open(p, encoding='utf-8').read()
a = s.index('        public static int MeasureBank1Script(ObjectSystem objects, out string unsupported, PlayerCore player')
b = s.index('        private const int AnimSet')
new = '''        public static int MeasureBank1Script(ObjectSystem objects, out string unsupported, PlayerCore player, ITileMap tiles,
            ActorEventTiming events, byte parity)
        {
            unsupported = null;
            if (objects == null) { unsupported = "objects"; return -1; }
            int cycles = Bank1HeaderCycles;
            for (int slotIndex = 1; slotIndex < ObjectSystem.SlotCount; slotIndex++)
            {
                int slot = MeasureBank1Slot(objects, slotIndex, player, tiles, events, parity, out unsupported);
                if (slot < 0) return -1;
                cycles += slot;
            }
            return cycles;
        }

        /// <summary>$8800 LDA #$40 / STA $99 / LDX #1 / STX $4D.</summary>
        public const int Bank1HeaderCycles = 2 + 3 + 2 + 3;

        /// <summary>One $8808…$8832 slot of the bank-1 pass, from the slot as the loop reaches it
        /// (an earlier slot may have spawned into it this pass).</summary>
        public static int MeasureBank1Slot(ObjectSystem objects, int slotIndex, PlayerCore player, ITileMap tiles,
            ActorEventTiming events, byte parity, out string unsupported)
        {
            unsupported = null;
            var slot = objects.Slots[slotIndex];
            int cycles = 4;
            if (!slot.Active) cycles += 3;
            else
            {
                cycles += 2 + 4 + 2;
                if (slot.Behavior < 0x47) cycles += 3;
                else if (slot.Behavior == 0x7E || slot.Behavior == 0x7F)
                {
                    cycles += 2 + 4 + PageCross(0x8836, slot.Behavior) + 3 + 4 + PageCross(0x88B6, slot.Behavior) + 3 + 2 + 3 + 2 + 3 + 5;
                    if (player == null || tiles == null || events == null) { unsupported = "bank1 7E/7F owners absent"; return -1; }
                    try
                    {
                        cycles += slot.Behavior == 0x7E ? FireShot(slot, player, tiles)
                            : FireSpitter(objects, slot, player, tiles, events, parity);
                    }
                    catch (NotSupportedException exception) { unsupported = exception.Message; return -1; }
                }
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
            return cycles + 5 + 3 + 2 + (slotIndex == 15 ? 2 + 3 + 6 : 3 + 3);
        }

'''
s = s[:a] + new + s[b:]
open(p, 'w', encoding='utf-8').write(s)

p = r'D:\Art\SW Code Zero Unity\Assets\Darkwing\Core\EnemyDispatcher.Timing.cs'
s = open(p, encoding='utf-8').read()
old = '''        private void BeginNativeBank1Timing()
        {
            LastBank1ScriptCycles = ActorTailTiming.MeasureBank1Script(Objects, out string reason, Player, Tiles,
                _nativeActorEvents, FrameParityForTiming?.Invoke() ?? 0);
            Bank1TimingUnsupported = reason;
        }'''
new = '''        private byte _bank1Parity;

        private void BeginNativeBank1Timing()
        {
            LastBank1ScriptCycles = ActorTailTiming.Bank1HeaderCycles;
            Bank1TimingUnsupported = null;
            _bank1Parity = FrameParityForTiming?.Invoke() ?? 0;
        }

        /// <summary>Price one bank-1 slot right before the loop reaches it.</summary>
        private void MeasureNativeBank1Slot(int slotIndex)
        {
            int cycles = ActorTailTiming.MeasureBank1Slot(Objects, slotIndex, Player, Tiles, _nativeActorEvents, _bank1Parity,
                out string reason);
            if (LastBank1ScriptCycles < 0) return;
            if (cycles < 0) { LastBank1ScriptCycles = -1; Bank1TimingUnsupported = reason; return; }
            LastBank1ScriptCycles += cycles;
        }'''
assert old in s
s = s.replace(old, new)
open(p, 'w', encoding='utf-8').write(s)

p = r'D:\Art\SW Code Zero Unity\Assets\Darkwing\Core\EnemyDispatcher.cs'
s = open(p, encoding='utf-8').read()
old = '''            BeginNativeBank1Timing();
            for (int slot = 1; slot < ObjectSystem.SlotCount; slot++)
            {
                var state = Objects.Slots[slot];
                if (state.Active && state.Behavior >= 0x47)'''
new = '''            BeginNativeBank1Timing();
            for (int slot = 1; slot < ObjectSystem.SlotCount; slot++)
            {
                MeasureNativeBank1Slot(slot);
                var state = Objects.Slots[slot];
                if (state.Active && state.Behavior >= 0x47)'''
assert old in s
s = s.replace(old, new)
open(p, 'w', encoding='utf-8').write(s)
print('ok')
