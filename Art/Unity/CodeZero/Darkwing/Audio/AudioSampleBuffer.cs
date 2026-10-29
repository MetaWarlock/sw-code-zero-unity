using System;
using System.Threading;

namespace Darkwing.Audio
{
    public sealed class AudioSampleBuffer
    {
        private readonly float[] _values;
        private readonly long[] _positions;
        private volatile int _write, _read;
        private volatile bool _started;
        private long _produced, _consumed;
        private long _late, _underrun, _overflow;

        public AudioSampleBuffer(int capacity)
        {
            if (capacity < 2) throw new ArgumentOutOfRangeException(nameof(capacity));
            _values = new float[capacity];
            _positions = new long[capacity];
        }

        public int BufferedSamples => (_write - _read + _values.Length) % _values.Length;
        public long LateSamples => Interlocked.Read(ref _late);
        public long UnderrunSamples => Interlocked.Read(ref _underrun);
        public long OverflowSamples => Interlocked.Read(ref _overflow);

        public void Push(float[] samples, int count)
        {
            if (count < 0 || count > samples.Length) throw new ArgumentOutOfRangeException(nameof(count));
            long first = _produced;
            _produced += count;
            for (int index = 0; index < count; index++)
            {
                long position = first + index;
                if (position < Volatile.Read(ref _consumed))
                {
                    Interlocked.Increment(ref _late);
                    continue;
                }
                int write = _write;
                int next = (write + 1) % _values.Length;
                if (next == _read)
                {
                    Interlocked.Increment(ref _overflow);
                    continue;
                }
                _values[write] = samples[index];
                _positions[write] = position;
                _write = next;
            }
            if (_write != _read) _started = true;
        }

        public void CopyTo(float[] output, int channels)
        {
            if (channels < 1 || output.Length % channels != 0) throw new ArgumentOutOfRangeException(nameof(channels));
            if (!_started) { Array.Clear(output, 0, output.Length); return; }
            long position = Volatile.Read(ref _consumed);
            for (int index = 0; index < output.Length; index += channels)
            {
                while (_read != _write && _positions[_read] < position)
                {
                    _read = (_read + 1) % _values.Length;
                    Interlocked.Increment(ref _late);
                }
                float value = 0;
                if (_read != _write && _positions[_read] == position)
                {
                    value = _values[_read];
                    _read = (_read + 1) % _values.Length;
                }
                else Interlocked.Increment(ref _underrun);
                for (int channel = 0; channel < channels; channel++) output[index + channel] = value;
                position++;
            }
            Volatile.Write(ref _consumed, position);
        }
    }
}
