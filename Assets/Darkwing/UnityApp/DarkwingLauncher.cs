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
        public TextAsset Bank0Data, Bank1Data, Bank7Data;

        private DarkwingGame _game;
        private Darkwing.Core.LevelTileMap _map;
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
            var decoder = new Darkwing.Core.RoomDecoder(
                Bank0Data.bytes, Bank1Data.bytes, Bank7Data.bytes, LevelSetsData.bytes);
            _game = new DarkwingGame(data);
            _map = new Darkwing.Core.LevelTileMap(decoder, _game.Ppu);
            _game.Player.TileMap = _map;
            var world = decoder.GetWorld(0);
            _map.LoadRoom(world, 3, 0); // mission-1 start cell (row 3, col 0) = room 0x01
            System.Array.Copy(Mission1Palette, _game.Ppu.PaletteRam, 32);
            _game.Ppu.Chr1Bank = 16;    // bg pattern table (verified for set 0)
            _game.Ppu.Chr0Bank = 14;    // sprite pattern table
            _game.Ppu.WriteRegister(0x2000, 0x16); // NMI off phase, bg $1000, sprites $0000
            _game.Ppu.WriteRegister(0x2001, 0x1E); // rendering on
            _game.Ppu.ScrollX = 0;
            _game.Ppu.ScrollY = 0;
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
            for (int y = 0; y < DarkwingGame.ScreenHeight; y++)
            {
                int src = y * DarkwingGame.ScreenWidth;
                int dst = (DarkwingGame.ScreenHeight - 1 - y) * DarkwingGame.ScreenWidth; // texture is bottom-up
                for (int x = 0; x < DarkwingGame.ScreenWidth; x++)
                    _pixels[dst + x] = Palette[fb[src + x] & 0x3F];
            }
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
            for (int y = 0; y < DarkwingGame.ScreenHeight; y++)
            {
                int src = y * DarkwingGame.ScreenWidth;
                int dst = (DarkwingGame.ScreenHeight - 1 - y) * DarkwingGame.ScreenWidth;
                for (int x = 0; x < DarkwingGame.ScreenWidth; x++)
                    px[dst + x] = Palette[fb[src + x] & 0x3F];
            }
            tex.SetPixels32(px);
            tex.Apply(false);
            System.IO.File.WriteAllBytes(path, ImageConversion.EncodeToPNG(tex));
            Object.Destroy(tex);
            return path;
        }
    }
}
