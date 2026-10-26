using System;
namespace Darkwing.Core
{
    public sealed partial class EnemyDispatcher
    {
        public ActorEventTiming ActorTimingEvents;
        /// <summary>$95 as the actor passes read it (the bank-5 engine ticks it later in the pass).</summary>
        public Func<byte> FrameParityForTiming;
        public byte ReadFrameParity() => FrameParityForTiming?.Invoke() ?? 0;
        public int LastBank6ScriptCycles { get; private set; } = -1;
        public int LastBank1ScriptCycles { get; private set; } = -1;
        public string Bank6TimingUnsupported { get; private set; }
        public string Bank1TimingUnsupported { get; private set; }
        private ActorEventTiming _nativeActorEvents;

        /// <summary>Per-slot parts of the last bank-6 pass (diagnostic): $8032…$8008 post-processing,
        /// $8008…$8027 dispatch envelope and the handler up to its RTS. −1 = unsupported, −2 = not run.</summary>
        public readonly int[] Bank6PostCycles = new int[ObjectSystem.SlotCount];
        public readonly int[] Bank6EnvelopeCycles = new int[ObjectSystem.SlotCount];
        public readonly int[] Bank6BehaviorCycles = new int[ObjectSystem.SlotCount];
        public readonly string[] Bank6SlotUnsupported = new string[ObjectSystem.SlotCount];

        private void BeginNativeActorTiming()
        {
            _nativeActorEvents = ActorTimingEvents;
            LastBank6ScriptCycles = 2 + 3 + 2 + 3;
            Bank6TimingUnsupported = null;
            LastBank1ScriptCycles = -1;
            Bank1TimingUnsupported = "bank1 not reached";
            for (int index = 0; index < ObjectSystem.SlotCount; index++)
            {
                Bank6PostCycles[index] = Bank6EnvelopeCycles[index] = Bank6BehaviorCycles[index] = -2;
                Bank6SlotUnsupported[index] = null;
            }
        }

        private void AddBank6(int cycles, string reason, int slotIndex)
        {
            if (cycles < 0 && Bank6SlotUnsupported[slotIndex] == null) Bank6SlotUnsupported[slotIndex] = reason;
            if (LastBank6ScriptCycles < 0) return;
            if (cycles < 0) { LastBank6ScriptCycles = -1; Bank6TimingUnsupported = reason; return; }
            LastBank6ScriptCycles += cycles;
        }

        private void MeasureNativePostProcess(ObjectSystem.Slot slot, int slotIndex)
        {
            int cycles; string reason = null;
            try { cycles = ActorBank6Timing.PostProcessNoHit(Objects, Player, slot, _nativeActorEvents); }
            catch (NotSupportedException exception) { cycles = -1; reason = exception.Message; }
            Bank6PostCycles[slotIndex] = cycles;
            AddBank6(cycles, reason, slotIndex);
        }

        private void MeasureNativeDispatch(ObjectSystem.Slot slot, int slotIndex)
        {
            int envelope = ActorBank6Timing.DispatchEnvelope(slot);
            Bank6EnvelopeCycles[slotIndex] = envelope;
            Bank6BehaviorCycles[slotIndex] = 0;
            AddBank6(envelope + (slotIndex == 15 ? 5 + 3 + 2 + 2 + 3 + 6 : 5 + 3 + 2 + 3), null, slotIndex);
        }

        private void MeasureNativeBehavior(ObjectSystem.Slot slot, int slotIndex)
        {
            int cycles; string reason = null;
            try { cycles = ActorBank6Timing.Behavior(slot, slotIndex, Player, Tiles, _nativeActorEvents); }
            catch (NotSupportedException exception) { cycles = -1; reason = exception.Message; }
            Bank6BehaviorCycles[slotIndex] = cycles;
            AddBank6(cycles, reason, slotIndex);
        }

        private byte _bank1Parity;

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
        }
    }
}
