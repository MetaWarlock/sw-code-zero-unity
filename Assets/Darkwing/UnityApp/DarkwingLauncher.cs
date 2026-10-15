using Darkwing.Core;
using UnityEngine;
using System.IO;

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

        [Tooltip("$31 world id: 0-6 = missions 1-7. All per-world data comes from the $DD57 tables.")]
        public int WorldId = 0;

        [Tooltip("Run the ROM's boot flow (title → city map → briefing → mission, M7). " +
                  "Off = jump straight into WorldId, the path the golden-comparison harnesses use.")]
        public bool UseFlow = true;

        public TextAsset ChrData, LevelSetsData, ScreensData, LevelsData, AnimsData;
        public TextAsset Bank0Data, Bank1Data, Bank7Data, Bank5Data, Bank3Data, Bank6Data, Bank2Data;
        public TextAsset ObjectTypesData;
        public TextAsset MapNametableData, MapPaletteData;

        private DarkwingGame _game;
        private GameFlow _flow;
        private Darkwing.Core.LevelTileMap _map;
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
            var data = Darkwing.Core.DataLoader.Load(
                ChrData.bytes, LevelSetsData.bytes, ScreensData.bytes,
                LevelsData.bytes, AnimsData.bytes);
            var decoder = new Darkwing.Core.RoomDecoder(
                Bank0Data.bytes, Bank1Data.bytes, Bank7Data.bytes, LevelSetsData.bytes);
            _game = new DarkwingGame(data);
            _game.Bank5 = Bank5Data.bytes;
            if (Bank7Data != null) _game.Bank7 = Bank7Data.bytes;
            if (Bank3Data != null) _game.Bank3 = Bank3Data.bytes;
            if (Bank6Data != null) _game.Bank6 = Bank6Data.bytes;
            if (Bank2Data != null) _game.Bank2 = Bank2Data.bytes;
            _game.ObjectTypeTable = ObjectTypesData.bytes;
            _game.MapNametable = MapNametableData != null ? MapNametableData.bytes : null;
            _game.MapPalette = MapPaletteData != null ? MapPaletteData.bytes : null;
            // Map CHR: probe_map logged no MMC1 CHR writes at all (the screen inherits the
            // banks latched by the previous phase), so the value comes from probe_flow's
            // chr($1000)=$26 latch plus the MapChrSearch screenshot score — see WorldState.
            _game.MapChrBank0 = WorldState.MapChrBank;
            _game.MapChrBank1 = WorldState.MapChrBank;
            _audioOut = gameObject.AddComponent<AudioOutFeed>();
            // $DD57: the whole per-world setup (descriptor, grid, palettes, CHR, player seed)
            // comes from the ROM tables, so WorldId 0-6 selects missions 1-7 with no hardcoded
            // palette/bank here. World 0 is what the goldens were captured on.
            _game.LoadWorld(decoder, (byte)WorldId);
            _map = _game.TileMap;
            _map.StreamColumns(0);
            if (WorldId == 0) _game.PlayMusic(1);   // song 1 = mission-1 gameplay (audio_engine.md)
            _game.Ppu.WriteRegister(0x2000, 0x16); // NMI off phase, bg $1000, sprites $0000
            _game.Ppu.WriteRegister(0x2001, 0x1E); // rendering on
            _game.Ppu.ScrollX = 0;
            _game.Ppu.ScrollY = 0;
            if (UseFlow)
            {
                // M7: boot at the title screen ($C792), then the map/briefing/mission phases.
                _flow = new GameFlow(_game, decoder);
            }
            _screen = new Texture2D(DarkwingGame.ScreenWidth, DarkwingGame.ScreenHeight,
                TextureFormat.RGBA32, false)
            {
                filterMode = FilterMode.Point,
                wrapMode = TextureWrapMode.Clamp,
            };
            GetComponent<Renderer>().material.mainTexture = _screen;
            _last = System.DateTime.UtcNow;
        }

        private AudioOutFeed _audioOut;

        private void Update()
        {
            if (_audioOut != null && _game.Audio != null) _audioOut.Source = _game.Audio;
            var now = System.DateTime.UtcNow;
            _accumulator += (now - _last).TotalSeconds;
            _last = now;
            if (_accumulator > 0.25) _accumulator = 0.25; // clamp after stalls
            double frame = 1.0 / DarkwingGame.NtscFramesPerSecond;
            while (_accumulator >= frame)
            {
                _accumulator -= frame;
                var pad = ReadPad1();
                // $C595 master loop: the bank-7 phase runs first and decides whether the
                // world engine gets this frame (WorldRunning) before the core steps.
                if (_flow != null) _flow.Step(in pad);
                _game.Step(in pad);
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
