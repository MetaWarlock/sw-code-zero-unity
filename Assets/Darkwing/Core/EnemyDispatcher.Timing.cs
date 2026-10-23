using System;
namespace Darkwing.Core
{
    public sealed partial class EnemyDispatcher
    {
        public ActorEventTiming ActorTimingEvents;
        public int LastBank6ScriptCycles { get; private set; } = -1;
        public int LastBank1ScriptCycles { get; private set; } = -1;
        public string Bank6TimingUnsupported { get; private set; }
        public string Bank1TimingUnsupported { get; private set; }
        private ActorEventTiming _nativeActorEvents;

        private void BeginNativeActorTiming()
        {
            _nativeActorEvents = ActorTimingEvents;
            LastBank6ScriptCycles = 2 + 3 + 2 + 3;
            Bank6TimingUnsupported = null;
            LastBank1ScriptCycles = -1;
            Bank1TimingUnsupported = "bank1 not reached";
        }

        private void MeasureNativePostProcess(ObjectSystem.Slot slot)
        {
            if (LastBank6ScriptCycles < 0) return;
            try { LastBank6ScriptCycles += ActorBank6Timing.PostProcessNoHit(Objects, Player, slot, _nativeActorEvents); }
            catch (NotSupportedException exception) { LastBank6ScriptCycles = -1; Bank6TimingUnsupported = exception.Message; }
        }

        private void MeasureNativeDispatch(ObjectSystem.Slot slot, int slotIndex)
        {
            if (LastBank6ScriptCycles < 0) return;
            LastBank6ScriptCycles += ActorBank6Timing.DispatchEnvelope(slot)
                + (slotIndex == 15 ? 5 + 3 + 2 + 2 + 3 + 6 : 5 + 3 + 2 + 3);
        }

        private void MeasureNativeBehavior(ObjectSystem.Slot slot, int slotIndex)
        {
            if (LastBank6ScriptCycles < 0) return;
            try { LastBank6ScriptCycles += ActorBank6Timing.Behavior(slot, slotIndex, Player, Tiles, _nativeActorEvents); }
            catch (NotSupportedException exception) { LastBank6ScriptCycles = -1; Bank6TimingUnsupported = exception.Message; }
        }

        private void BeginNativeBank1Timing()
        {
            LastBank1ScriptCycles = ActorTailTiming.MeasureBank1Script(Objects, out string reason);
            Bank1TimingUnsupported = reason;
        }
    }
}
