using Darkwing.Core;
using UnityEngine;

namespace Darkwing.UnityApp
{
    /// <summary>
    /// Application shell: drives the deterministic Core from an NTSC-frame
    /// accumulator so window FPS never changes simulation behavior,
    /// blits the Core framebuffer, and maps host input to NES buttons.
    /// </summary>
    public sealed class DarkwingLauncher : MonoBehaviour
    {
        [Tooltip("Integer pixel scale for the 256x240 picture (0 = fit window).")]
        public int PixelScale = 3;

        private DarkwingGame _game;
        private Texture2D _screen;
        private double _accumulator;
        private System.DateTime _last;
        private Color32[] _pixels = new Color32[DarkwingGame.ScreenWidth * DarkwingGame.ScreenHeight];

        // 2C02 palette entry lookup is Presentation's job later; the Core
        // framebuffer stores NES indices and we convert with the fixed table.
        private static readonly Color32[] Palette = NesPalette.Table;

        private void Awake()
        {
            Application.runInBackground = true;
            QualitySettings.vSyncCount = 0;
            Time.captureDeltaTime = 0; // we pace ourselves
            _game = new DarkwingGame();
            _game.Reset();
            _screen = new Texture2D(DarkwingGame.ScreenWidth, DarkwingGame.ScreenHeight,
                TextureFormat.RGBA32, false)
            {
                filterMode = FilterMode.Point,
                wrapMode = TextureWrapMode.Clamp,
            };
            GetComponent<Renderer>().material.mainTexture = _screen;
            _last = System.DateTime.UtcNow;
        }

        private void Update()
        {
            var now = System.DateTime.UtcNow;
            _accumulator += (now - _last).TotalSeconds;
            _last = now;
            if (_accumulator > 0.25) _accumulator = 0.25; // clamp after stalls
            double frame = 1.0 / DarkwingGame.NtscFramesPerSecond;
            while (_accumulator >= frame)
            {
                _accumulator -= frame;
                _game.Step(ReadPad1());
            }
        }

        private void LateUpdate()
        {
            var fb = _game.Framebuffer;
            for (int i = 0; i < fb.Length; i++)
                _pixels[i] = Palette[fb[i] & 0x3F];
            _screen.SetPixels32(_pixels);
            _screen.Apply(false);
        }

        private static NesButtons ReadPad1()
        {
            var k = UnityEngine.InputSystem.Keyboard.current;
            if (k == null) return default;
            return new NesButtons
            {
                A = k.zKey.isPressed,
                B = k.xKey.isPressed,
                Select = k.leftShiftKey.isPressed,
                Start = k.enterKey.isPressed,
                Up = k.upArrowKey.isPressed,
                Down = k.downArrowKey.isPressed,
                Left = k.leftArrowKey.isPressed,
                Right = k.rightArrowKey.isPressed,
            };
        }
    }
}
