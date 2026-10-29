namespace Darkwing.Audio
{
    public sealed partial class AudioSequencer
    {
        private int _executionCycles;

        public int LastTickCycles { get; private set; }
        public int LastTaskCycles { get; private set; }
        public bool TimingSupported { get; private set; } = true;
        /// <summary>Source line of the first unpriced branch since the last reset (diagnostic).</summary>
        public int TimingUnsupportedLine { get; private set; }

        private void Unpriced([System.Runtime.CompilerServices.CallerLineNumber] int line = 0)
        {
            if (TimingSupported) TimingUnsupportedLine = line;
            TimingSupported = false;
        }
        public System.Action<string, int> TimingObserver { get; set; }

        private void Cost(int cycles, [System.Runtime.CompilerServices.CallerMemberName] string routine = null)
        {
            _executionCycles = unchecked(_executionCycles + cycles);
            TimingObserver?.Invoke(routine, cycles);
        }

        private int InstrumentPageCross(int index)
        {
            return _c5 + index > 255 ? 1 : 0;
        }
    }
}
