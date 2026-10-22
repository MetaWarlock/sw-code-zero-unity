namespace Darkwing.Audio
{
    /// <summary>
    /// NES 2A03 APU (no DMC — Darkwing Duck never enables it; verified: no $4010-$4013
    /// writes anywhere, $4015 = $0F only). Register-level model per nesdev APU docs.
    /// M5 status: correct register semantics; frame-counter scheduling is the
    /// approximate 7457/14915-division model — to be verified against recorded WAV.
    /// </summary>
    public sealed class Apu
    {
        public const int CpuHz = 1789773;

        private struct Pulse
        {
            public bool Enabled, Halt, ConstantVolume;
            public int Duty;                  // 0-3
            public int VolumeOrEnvelope;      // envelope period 0-31 or volume 0-15
            public bool SweepEnabled, SweepNegate;
            public int SweepPeriod, SweepShift;
            public int Period;                // 11-bit cached period
            public int Timer;                 // countdown
            public int DutyStep;
            public int LengthCounter;
            public int EnvelopeDivider, EnvelopeDecay;
            public bool EnvelopeRestart;
            public int SweepCounter;
        }

        private struct Triangle
        {
            public bool Enabled, ControlFlag;
            public int LinearReload, LinearCounter;
            public int Period, Timer;
            public int SeqStep;               // 0-31
            public int LengthCounter;
        }

        private struct Noise
        {
            public bool Enabled, Halt, ConstantVolume;
            public bool ShortMode;
            public int VolumeOrEnvelope, EnvelopeDivider, EnvelopeDecay;
            public bool EnvelopeRestart;
            public int PeriodIndex;
            public int Timer;
            public ushort Shift;
            public int LengthCounter;
        }

        private Pulse _p1, _p2;
        private Triangle _tri;
        private Noise _noise;
        private int _frameStep;          // CPU steps within the 4/5-step sequence
        private bool _fiveStepMode;

        public Apu()
        {
            _noise.Shift = 1;
            _p1.SweepCounter = 1;
            _p2.SweepCounter = 1;
        }

        private static readonly int[] DutyWaveform = { 0b01000000, 0b01100000, 0b01111000, 0b10011111 };
        private static readonly int[] NoisePeriods =
            { 4, 8, 16, 32, 64, 96, 128, 160, 202, 254, 380, 508, 762, 1016, 2034, 4068 };
        private static readonly byte[] LengthLoads =
            { 10, 254, 20, 2, 40, 4, 80, 6, 160, 8, 60, 10, 14, 12, 26, 14 };
        private static readonly float[] PulseTable = BuildPulseTable();
        private static readonly float[] TndTable = BuildTndTable();

        private static float[] BuildPulseTable()
        {
            var t = new float[32];
            for (int i = 0; i < 32; i++) t[i] = 95.88f / (8128f / i + 100);
            return t;
        }

        private static float[] BuildTndTable()
        {
            var t = new float[361];
            for (int i = 0; i < 361; i++) t[i] = 163.67f / (24329f / i + 100);
            return t;
        }

        public void Write(int addr, byte value)
        {
            switch (addr)
            {
                case 0x4000:
                    _p1.Halt = (value & 0x20) != 0;
                    _p1.ConstantVolume = (value & 0x10) != 0;
                    _p1.VolumeOrEnvelope = value & 0x0F;
                    _p1.Duty = (value >> 6) & 3;
                    break;
                case 0x4001:
                    _p1.SweepEnabled = (value & 0x80) != 0;
                    _p1.SweepPeriod = (value >> 4) & 7;
                    _p1.SweepNegate = (value & 0x08) != 0;
                    _p1.SweepShift = value & 7;
                    _p1.SweepCounter = 1;
                    break;
                case 0x4002: _p1.Period = (_p1.Period & 0x700) | value; break;
                case 0x4003:
                    _p1.Period = (_p1.Period & 0xFF) | ((value & 7) << 8);
                    if (_p1.Enabled) _p1.LengthCounter = LengthLoads[value >> 3];
                    _p1.EnvelopeRestart = true;
                    _p1.DutyStep = 0;
                    _p1.Timer = _p1.Period;
                    break;
                case 0x4004:
                    _p2.Halt = (value & 0x20) != 0;
                    _p2.ConstantVolume = (value & 0x10) != 0;
                    _p2.VolumeOrEnvelope = value & 0x0F;
                    _p2.Duty = (value >> 6) & 3;
                    break;
                case 0x4005:
                    _p2.SweepEnabled = (value & 0x80) != 0;
                    _p2.SweepPeriod = (value >> 4) & 7;
                    _p2.SweepNegate = (value & 0x08) != 0;
                    _p2.SweepShift = value & 7;
                    _p2.SweepCounter = 1;
                    break;
                case 0x4006: _p2.Period = (_p2.Period & 0x700) | value; break;
                case 0x4007:
                    _p2.Period = (_p2.Period & 0xFF) | ((value & 7) << 8);
                    if (_p2.Enabled) _p2.LengthCounter = LengthLoads[value >> 3];
                    _p2.EnvelopeRestart = true;
                    _p2.DutyStep = 0;
                    _p2.Timer = _p2.Period;
                    break;
                case 0x4008:
                    _tri.ControlFlag = (value & 0x80) != 0;
                    _tri.LinearReload = value & 0x7F;
                    break;
                case 0x400A: _tri.Period = (_tri.Period & 0x700) | value; break;
                case 0x400B:
                    _tri.Period = (_tri.Period & 0xFF) | ((value & 7) << 8);
                    if (_tri.Enabled) _tri.LengthCounter = LengthLoads[value >> 3];
                    _tri.LinearCounter = _tri.LinearReload;
                    break;
                case 0x400C:
                    _noise.Halt = (value & 0x20) != 0;
                    _noise.ConstantVolume = (value & 0x10) != 0;
                    _noise.VolumeOrEnvelope = value & 0x0F;
                    break;
                case 0x400E:
                    _noise.PeriodIndex = value & 0x0F;
                    _noise.ShortMode = (value & 0x80) != 0;
                    break;
                case 0x400F:
                    if (_noise.Enabled) _noise.LengthCounter = LengthLoads[value >> 3];
                    _noise.EnvelopeRestart = true;
                    break;
                case 0x4015:
                    _p1.Enabled = (value & 1) != 0;
                    _p2.Enabled = (value & 2) != 0;
                    _tri.Enabled = (value & 4) != 0;
                    _noise.Enabled = (value & 8) != 0;
                    if (!_p1.Enabled) _p1.LengthCounter = 0;
                    if (!_p2.Enabled) _p2.LengthCounter = 0;
                    if (!_tri.Enabled) _tri.LengthCounter = 0;
                    if (!_noise.Enabled) _noise.LengthCounter = 0;
                    break;
                case 0x4017:
                    _fiveStepMode = (value & 0x80) != 0;
                    _frameStep = 0;
                    break;
            }
        }

        /// <summary>Advance one CPU cycle.</summary>
        public void StepCpu()
        {
            // Waveform timers run on CPU/2.
            if ((_frameStep & 1) == 0)
            {
                TickPulseTimer(ref _p1);
                TickPulseTimer(ref _p2);
            }
            if (_tri.Timer > 0) _tri.Timer--;
            else
            {
                _tri.Timer = _tri.Period;
                if (_tri.LinearCounter > 0 && _tri.LengthCounter > 0)
                    _tri.SeqStep = (_tri.SeqStep + 1) & 31;
            }
            if (_noise.Timer > 0) _noise.Timer--;
            else
            {
                _noise.Timer = NoisePeriods[_noise.PeriodIndex] - 1;
                int feedback = (_noise.Shift ^ (_noise.Shift >> (_noise.ShortMode ? 6 : 1))) & 1;
                _noise.Shift = (ushort)((_noise.Shift >> 1) | (feedback << 14));
            }

            // Frame counter: 4-step sequence at 3729/7457/11186/14915; 5-step .../18641.
            _frameStep++;
            if (!_fiveStepMode)
            {
                if (_frameStep == 7457) HalfFrame();
                else if (_frameStep == 14915) { QuarterFrame(); _frameStep = 0; }
                else if (_frameStep % 3729 == 0) QuarterFrame();
            }
            else
            {
                if (_frameStep == 18641) { HalfFrame(); _frameStep = 0; }
                else if (_frameStep % 3729 == 0) QuarterFrame();
            }
        }

        private static void TickPulseTimer(ref Pulse p)
        {
            if (p.Timer > 0) p.Timer--;
            else
            {
                p.Timer = p.Period;
                p.DutyStep = (p.DutyStep + 1) & 7;
            }
        }

        private void QuarterFrame()
        {
            TickEnvelope(ref _p1);
            TickEnvelope(ref _p2);
            TickNoiseEnvelope();
            if (_tri.LinearCounter > 0) _tri.LinearCounter--;
            if (_tri.ControlFlag) _tri.LinearCounter = _tri.LinearReload;
        }

        private void HalfFrame()
        {
            TickLength(ref _p1);
            TickLength(ref _p2);
            if (!_tri.ControlFlag && _tri.LengthCounter > 0) _tri.LengthCounter--;
            if (!_noise.Halt && _noise.LengthCounter > 0) _noise.LengthCounter--;
            TickSweep(ref _p1, true);
            TickSweep(ref _p2, false);
        }

        private static void TickEnvelope(ref Pulse p)
        {
            if (p.EnvelopeRestart)
            {
                p.EnvelopeRestart = false;
                p.EnvelopeDecay = 15;
                p.EnvelopeDivider = p.VolumeOrEnvelope;
            }
            else if (p.EnvelopeDivider > 0) p.EnvelopeDivider--;
            else
            {
                p.EnvelopeDivider = p.VolumeOrEnvelope;
                if (p.EnvelopeDecay > 0) p.EnvelopeDecay--;
                else if (p.Halt) p.EnvelopeDecay = 15;
            }
        }

        private void TickNoiseEnvelope()
        {
            if (_noise.EnvelopeRestart)
            {
                _noise.EnvelopeRestart = false;
                _noise.EnvelopeDecay = 15;
                _noise.EnvelopeDivider = _noise.VolumeOrEnvelope;
            }
            else if (_noise.EnvelopeDivider > 0) _noise.EnvelopeDivider--;
            else
            {
                _noise.EnvelopeDivider = _noise.VolumeOrEnvelope;
                if (_noise.EnvelopeDecay > 0) _noise.EnvelopeDecay--;
                else if (_noise.Halt) _noise.EnvelopeDecay = 15;
            }
        }

        private static void TickLength(ref Pulse p)
        {
            if (!p.Halt && p.LengthCounter > 0) p.LengthCounter--;
        }

        private static void TickSweep(ref Pulse p, bool oneComplement)
        {
            if (p.SweepCounter > 0) p.SweepCounter--;
            bool mute = p.Period < 8;
            if (p.SweepCounter == 0 && p.SweepEnabled && p.SweepShift > 0 && !mute)
            {
                int delta = p.Period >> p.SweepShift;
                int target = p.SweepNegate ? p.Period - delta - (oneComplement ? 1 : 0) : p.Period + delta;
                if (target < 0x800) { p.Period = target; p.Timer = p.Period; }
            }
            if (p.SweepCounter == 0) p.SweepCounter = p.SweepPeriod + 1;
        }

        /// <summary>Mixed output sample; caller steps the CPU and resamples to the output rate.</summary>
        public float CurrentOutput()
        {
            int p1 = 0, p2 = 0;
            if (_p1.Enabled && _p1.LengthCounter > 0 && _p1.Period >= 8)
                p1 = ((DutyWaveform[_p1.Duty] >> (7 - _p1.DutyStep)) & 1) != 0 ? Volume(_p1) : 0;
            if (_p2.Enabled && _p2.LengthCounter > 0 && _p2.Period >= 8)
                p2 = ((DutyWaveform[_p2.Duty] >> (7 - _p2.DutyStep)) & 1) != 0 ? Volume(_p2) : 0;
            int tri = (_tri.Enabled && _tri.LengthCounter > 0 && _tri.LinearCounter > 0)
                ? TriSequence(_tri.SeqStep) : 0;
            int noise = 0;
            if (_noise.Enabled && _noise.LengthCounter > 0 && (_noise.Shift & 1) == 0)
                noise = _noise.ConstantVolume ? _noise.VolumeOrEnvelope : _noise.EnvelopeDecay;
            return PulseTable[p1 + p2] + TndTable[3 * tri + 2 * noise];
        }

        private static int Volume(Pulse p) => p.ConstantVolume ? p.VolumeOrEnvelope : p.EnvelopeDecay;

        private static int TriSequence(int step)
        {
            return step < 16 ? 15 - step : step - 16;
        }
    }
}
