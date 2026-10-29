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

        private readonly Darkwing.Audio.AudioSampleBuffer _samples = new Darkwing.Audio.AudioSampleBuffer(48000 * 2);
        private readonly float[] _scratch = new float[800];    // RenderFrame: ~798.68 samples/frame
        private volatile bool _muted;

        public bool Muted => _muted;
        public int BufferedSamples => _samples.BufferedSamples;
        public long LateSamples => _samples.LateSamples;
        public long UnderrunSamples => _samples.UnderrunSamples;
        public long OverflowSamples => _samples.OverflowSamples;

        public void ToggleMute()
        {
            _muted = !_muted;
            Debug.Log(_muted ? "Darkwing audio: muted (F5)" : "Darkwing audio: enabled (F5)");
        }

        /// <summary>Render one 60 Hz frame of APU samples into the ring. The sequencer tick is
        /// driven by the Core NMI, so this is samples-only. The output sample timeline rejects
        /// samples whose device deadline was already replaced by underrun silence, rather than
        /// replaying them later and accumulating permanent audiovisual latency.</summary>
        public void PushFrame()
        {
            if (Source == null) return;
            int n = Source.RenderSamples(_scratch, 0);
            _samples.Push(_scratch, n);
        }

        private void Start()
        {
            var configuration = AudioSettings.GetConfiguration();
            if (configuration.dspBufferSize > 256)
            {
                configuration.dspBufferSize = 256;
                if (!AudioSettings.Reset(configuration)) Debug.LogWarning("Darkwing audio: low-latency DSP configuration was rejected.");
            }
            var src = GetComponent<AudioSource>();
            src.clip = AudioClip.Create("dw", 48000 * 4, 1, 48000, false);
            src.loop = true;
            src.Play();
        }

        private void OnAudioFilterRead(float[] data, int channels)
        {
            _samples.CopyTo(data, channels);
            if (_muted) System.Array.Clear(data, 0, data.Length);
        }
    }
}
