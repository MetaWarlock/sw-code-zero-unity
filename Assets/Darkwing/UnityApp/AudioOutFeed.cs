using UnityEngine;

namespace Darkwing.UnityApp
{
    /// <summary>
    /// Streams the deterministic AudioSequencer output into Unity's audio pipeline.
    /// Update() renders the frame's samples into a ring buffer; OnAudioFilterRead
    /// drains it. The simulation clock is unaffected (audio is a consumer).
    /// </summary>
    [RequireComponent(typeof(AudioSource))]
    public sealed class AudioOutFeed : MonoBehaviour
    {
        public Darkwing.Audio.AudioSequencer Source;

        private readonly float[] _ring = new float[48000 * 2]; // ~2 s at 48 kHz mono
        private int _write, _read;
        private int _carry; // leftover samples from partial frame renders
        private float[] _scratch = new float[4800];

        private void Start()
        {
            var src = GetComponent<AudioSource>();
            src.clip = AudioClip.Create("dw", 48000 * 4, 1, 48000, false);
            src.loop = true;
            src.Play();
        }

        private void Update()
        {
            if (Source == null) return;
            int n = Source.RenderFrame(_scratch, 0);
            for (int i = 0; i < n; i++)
            {
                _ring[_write] = _scratch[i];
                _write = (_write + 1) % _ring.Length;
            }
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
