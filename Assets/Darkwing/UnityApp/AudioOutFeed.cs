using UnityEngine;

namespace Darkwing.UnityApp
{
    /// <summary>
    /// Streams the deterministic AudioSequencer output into Unity's audio pipeline.
    /// The launcher pushes exactly one audio frame per stepped NES frame, so the song
    /// tempo follows the 60 Hz simulation clock instead of the render rate; a 120 Hz
    /// display used to advance the sequencer twice as fast and overrun this ring.
    /// OnAudioFilterRead only drains it (producer = main thread, consumer = audio thread).
    /// </summary>
    [RequireComponent(typeof(AudioSource))]
    public sealed class AudioOutFeed : MonoBehaviour
    {
        public Darkwing.Audio.AudioSequencer Source;

        private readonly float[] _ring = new float[48000 * 2]; // ~2 s at 48 kHz mono
        private int _write, _read;
        private readonly float[] _scratch = new float[800];    // RenderFrame: ~798.68 samples/frame

        /// <summary>Render one 60 Hz frame of APU samples into the ring. The sequencer tick is
        /// driven by the Core NMI, so this is samples-only. If the consumer has not freed a
        /// full frame the samples are advanced and dropped, keeping APU time aligned with the
        /// simulation instead of letting musical time run ahead of what was output.</summary>
        public void PushFrame()
        {
            if (Source == null) return;
            int n = Source.RenderSamples(_scratch, 0);
            int free = (_read - _write - 1 + _ring.Length) % _ring.Length;
            if (free < n) return;
            for (int i = 0; i < n; i++)
            {
                _ring[_write] = _scratch[i];
                _write = (_write + 1) % _ring.Length;
            }
        }

        private void Start()
        {
            var src = GetComponent<AudioSource>();
            src.clip = AudioClip.Create("dw", 48000 * 4, 1, 48000, false);
            src.loop = true;
            src.Play();
        }

        private void OnAudioFilterRead(float[] data, int channels)
        {
            if (Source == null) return;
            for (int i = 0; i < data.Length; i += channels)
            {
                float v = 0f;
                if (_read != _write)
                {
                    v = _ring[_read];
                    _read = (_read + 1) % _ring.Length;
                }
                for (int c = 0; c < channels; c++) data[i + c] = v;
            }
        }
    }
}
