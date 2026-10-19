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
        public TextAsset Bank4Data;
        public TextAsset ObjectTypesData;
        public TextAsset MapNametableData, MapPaletteData;

        private DarkwingGame _game;
        private GameFlow _flow;
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
            if (Bank4Data != null) _game.Bank4 = Bank4Data.bytes;
            _game.ObjectTypeTable = ObjectTypesData.bytes;
            _game.MapNametable = MapNametableData != null ? MapNametableData.bytes : null;
            _game.MapPalette = MapPaletteData != null ? MapPaletteData.bytes : null;
            // Bank 2 $8E18/$8E1B request separate sprite/background banks ($9F/$9A).
            _game.MapChrBank0 = WorldState.MapSpriteChrBank;
            _game.MapChrBank1 = WorldState.MapChrBank;
            _audioOut = gameObject.AddComponent<AudioOutFeed>();
            _game.Ppu.WriteRegister(0x2000, 0x90);
            _game.Ppu.WriteRegister(0x2001, 0x1E);
            if (UseFlow)
                _flow = new GameFlow(_game, decoder);
            else
            {
                _game.LoadWorld(decoder, (byte)WorldId);
                if (WorldId == 0) _game.PlayMusic(1);
            }
            _screen = new Texture2D(DarkwingGame.ScreenWidth, DarkwingGame.ScreenHeight,
                TextureFormat.RGBA32, false)
            {
                filterMode = FilterMode.Point,
                wrapMode = TextureWrapMode.Clamp,
            };
            GetComponent<Renderer>().material.mainTexture = _screen;
            _last = System.DateTime.UtcNow;
#if UNITY_EDITOR || DEVELOPMENT_BUILD
#if UNITY_EDITOR
            var editorReplay = UnityEditor.SessionState.GetString("DarkwingReplay", "");
            if (editorReplay.Length > 0)
            {
                UnityEditor.SessionState.EraseString("DarkwingReplay");
                BeginReplay(editorReplay);
            }
#endif
            var args = System.Environment.GetCommandLineArgs();
            int replayArg = System.Array.IndexOf(args, "-darkwing-replay");
            if (replayArg >= 0 && replayArg + 1 < args.Length) BeginReplay(args[replayArg + 1]);
#endif
        }

        private AudioOutFeed _audioOut;

        private void Update()
        {
            if (_audioOut != null && _game.Audio != null) _audioOut.Source = _game.Audio;
#if UNITY_EDITOR || DEVELOPMENT_BUILD
            if (_replay != null) { for (int i = 0; i < 8; i++) ReplayFrame(); return; }
#endif
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
                AdvanceFrame(in pad);
                // One audio frame per simulation frame: the sequencer's tempo is the 60 Hz clock
                // above, never the render rate of Update().

            }
        }

        // One public frame boundary for the host and deterministic input replay. A replay
        // supplies buttons only; it cannot seed RAM or select a flow phase.
        public void AdvanceFrame(in NesButtons pad)
        {
            if (_flow != null) _flow.Step(in pad);
            _game.Step(in pad);
            if (_audioOut != null) _audioOut.PushFrame();
        }

        public DarkwingGame Game => _game;
        public GameFlow Flow => _flow;

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

#if UNITY_EDITOR || DEVELOPMENT_BUILD
        [System.Serializable] public sealed class ReplayInput { public int first, last, buttons; }
        [System.Serializable] public sealed class ReplaySpec
        {
            public int frames;
            public ReplayInput[] inputs;
            public int[] captures;
            public string output;
            public bool captureAtStart; // poll boundary used by stand.py; default preserves older after-step captures
        }
        [System.Serializable] public sealed class ReplayState
        {
            public int frame, gameFrame, phaseFrames, world, x, y, state, scrollX, scrollY, chr0, chr1, ctrl, mask;
            public int completed, maskCompleted, scoreUnits, livesLeft, livesRight, bossDamage;
            public string phase, framebuffer, nametable, palette, oam, slots;
        }
        [System.Serializable] public sealed class ReplayResult
        {
            public string scope = "Scene launcher; buttons only from Awake. No RAM seed or direct phase entry.";
            public bool editor;
            public ReplaySpec input;
            public ReplayState[] states;
            public ReplayState final; // after every recorded input byte has actually been consumed
        }
        private ReplaySpec _replay;
        private int _replayFrame;
        private readonly System.Collections.Generic.List<ReplayState> _replayStates = new System.Collections.Generic.List<ReplayState>();
        private string _lastPhase;
        public bool ReplayFinished => _replay != null && _replayFrame >= _replay.frames;

        public void BeginReplay(string path)
        {
            if (_game.Frame != 0) throw new System.InvalidOperationException("Replay must begin before the first game frame");
            _replay = JsonUtility.FromJson<ReplaySpec>(File.ReadAllText(path));
            if (_replay.frames <= 0 || _replay.inputs == null || _replay.captures == null)
                throw new System.IO.InvalidDataException("Invalid button replay");
            Directory.CreateDirectory(_replay.output);
            File.Delete(Path.Combine(_replay.output, "route.json"));
        }

        private string ReplaySlots()
        {
            var s = new System.Text.StringBuilder();
            for (int i = 0; i < _game.Objects.Slots.Length; i++)
            {
                var slot = _game.Objects.Slots[i];
                if (slot.Active) s.AppendFormat("{0:X2}:{1:X2}/{2:X2}@{3:X4},{4:X4};", i, slot.AnimNumber, slot.Behavior, slot.X, slot.Y);
            }
            return s.ToString();
        }

        private void ReplayFrame()
        {
            if (ReplayFinished) return;
            int f = ++_replayFrame, bits = 0;
            foreach (var entry in _replay.inputs)
                if (f >= entry.first && f <= entry.last) bits |= entry.buttons;
            var pad = NesButtons.FromByte((byte)bits);
            if (_replay.captureAtStart) CaptureReplayState(f);
            AdvanceFrame(in pad);
            if (!_replay.captureAtStart) CaptureReplayState(f);
            if (ReplayFinished)
                File.WriteAllText(Path.Combine(_replay.output, "route.json"), JsonUtility.ToJson(new ReplayResult {
                    editor=Application.isEditor, input=_replay, states=_replayStates.ToArray(), final=ReadReplayState(f)
                }));
        }

        private ReplayState ReadReplayState(int f)
        {
            var p = _game.Player; var ppu = _game.Ppu;
            return new ReplayState {
                frame=f, gameFrame=(int)_game.Frame, phase=_flow == null ? "DirectWorld" : _flow.Stage.ToString(),
                phaseFrames=_flow == null ? 0 : _flow.PhaseFrames,
                world=_flow == null ? WorldId : _flow.World.CurrentWorld,
                completed=_flow == null ? 0 : _flow.World.CompletedCount,
                maskCompleted=_flow == null ? 0 : _flow.World.CompletedMask,
                scoreUnits=p.ScoreUnits, livesLeft=p.EnergyColLeft, livesRight=p.EnergyColRight,
                bossDamage=BossBehaviors.DamageAccum,
                x=p.X, y=p.Y, state=p.State, scrollX=p.ScrollX, scrollY=(p.ScrollYRow << 8) | p.ScrollY,
                chr0=ppu.Chr0Bank, chr1=ppu.Chr1Bank, ctrl=ppu.Ctrl, mask=ppu.Mask, slots=ReplaySlots(),
                framebuffer=System.Convert.ToBase64String(_game.Framebuffer),
                nametable=System.Convert.ToBase64String(ppu.NametableRam),
                palette=System.Convert.ToBase64String(ppu.PaletteRam), oam=System.Convert.ToBase64String(ppu.Oam)
            };
        }

        private void CaptureReplayState(int f)
        {
            string phase = _flow == null ? "DirectWorld" : _flow.Stage.ToString();
            bool capture = System.Array.IndexOf(_replay.captures, f) >= 0;
            if (capture || phase != _lastPhase)
            {
                _replayStates.Add(ReadReplayState(f));
                _lastPhase = phase;
                if (capture) DumpFramebuffer(Path.Combine(_replay.output, "frame_" + f + ".png"));
            }
        }
#endif

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
