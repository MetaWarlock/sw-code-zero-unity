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

        public TextAsset ChrData, LevelSetsData, ScreensData, LevelsData, AnimsData;

        private DarkwingGame _game;
        private BackgroundComposer _composer;
        private Texture2D _screen;
        private double _accumulator;
        private System.DateTime _last;
        private Color32[] _pixels = new Color32[DarkwingGame.ScreenWidth * DarkwingGame.ScreenHeight];
        private static readonly byte[] Mission1Palette =
        {
            // Captured from original mission-1 gameplay (reference/probe_m1_enter palette_frames frame 700).
            0x0F,0x20,0x27,0x17, 0x0F,0x37,0x07,0x01, 0x0F,0x20,0x31,0x22, 0x0F,0x0F,0x11,0x01,
            0x0F,0x0F,0x30,0x27, 0x0F,0x0F,0x27,0x13, 0x0F,0x0F,0x27,0x15, 0x0F,0x0F,0x37,0x07,
        };

        // 2C02 palette entry lookup is Presentation's job later; the Core
        // framebuffer stores NES indices and we convert with the fixed table.
        private static readonly Color32[] Palette = NesPalette.Table;

        private void Awake()
        {
            Application.runInBackground = true;
            QualitySettings.vSyncCount = 0;
            Time.captureDeltaTime = 0; // we pace ourselves
            var data = Darkwing.Core.DataLoader.Load(
                ChrData.bytes, LevelSetsData.bytes, ScreensData.bytes,
                LevelsData.bytes, AnimsData.bytes);
            _composer = new BackgroundComposer(data);
            _game = new DarkwingGame();
            _game.Reset();
            // M3 smoke test: compose mission-1 first screen at scroll 0.
            var lv = data.Levels[0];
            _composer.Compose(lv.TileSet, lv.FirstScreen, 0, 0, Mission1Palette, _game.Framebuffer);
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

        /// <summary>Dev/test helper: dump the exact 256x240 framebuffer as PNG.</summary>
        public string DumpFramebuffer(string path)
        {
            var tex = new Texture2D(DarkwingGame.ScreenWidth, DarkwingGame.ScreenHeight,
                TextureFormat.RGBA32, false);
            var px = new Color32[_pixels.Length];
            var fb = _game.Framebuffer;
            for (int i = 0; i < fb.Length; i++)
                px[i] = Palette[fb[i] & 0x3F];
            tex.SetPixels32(px);
            tex.Apply(false);
            System.IO.File.WriteAllBytes(path, ImageConversion.EncodeToPNG(tex));
            Object.Destroy(tex);
            return path;
        }
    }
}
