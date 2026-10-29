using System;

namespace Darkwing.Core
{
    public sealed partial class DarkwingGame
    {
        /// <summary>Scheduler time of the ordinary mission pass; see <see cref="MissionClock"/>.</summary>
        public MissionClock Clock { get; } = new MissionClock();

        /// <summary>Research seams only (null in every host): replace a native price that is still
        /// unsupported, to exercise the clock past it in a diagnostic run. (poll, native) → price.</summary>
        public Func<long, int, int> DiagnosticBodyPrefix, DiagnosticNmiAudio;
        /// <summary>Research seam: (poll, handler parity, native) → publication price.</summary>
        public Func<long, int, int, int> DiagnosticPublication;

        public int LastClockBodyPrefixCycles { get; private set; } = -1;
        public string LastClockBodyPrefixUnsupported { get; private set; }

        private bool _clockNmi;
        private bool _clockTail;

        /// <summary>The bank-5 world pass may own the frame: mission flow, no resident, rendering
        /// published by this NMI. Section/reload/entry stretches still return before the tail.</summary>
        private bool ClockEligible => PhysicalFrames != null && WorldRunning && ResidentProcess == null
            && Flow != null && Flow.Stage == GameFlow.Phase.Mission && (Ppu.Ctrl & 0x80) != 0
            && _blankDepth == 0 && Oam != null && Bank7 != null;

        /// <summary>Before the publication consumes its latches: both DMA parities of this NMI.</summary>
        private void BeginClockNmi()
        {
            _clockNmi = false;
            _clockTail = false;
            if (Clock.Stage == MissionClock.Phase.Unsupported) return;
            if (!ClockEligible) { Clock.Drop(); return; }
            if (!TryMeasureNativeNmiPublication(0, 0, 0, 0, out int even, out _)) even = -1;
            if (!TryMeasureNativeNmiPublication(1, 0, 0, 0, out int odd, out _)) odd = -1;
            if (DiagnosticPublication != null)
            {
                even = DiagnosticPublication(PhysicalFrames.Ordinal, 0, even);
                odd = DiagnosticPublication(PhysicalFrames.Ordinal, 1, odd);
            }
            _clockNmi = Clock.BeginNmi(PhysicalFrames.Ordinal, PhysicalFrames.NmiThreshold, even, odd);
        }

        private void CompleteClockNmi()
        {
            if (!_clockNmi) return;
            int audio = NmiAudioTimingSupported ? LastNmiAudioServiceCycles : -1;
            if (DiagnosticNmiAudio != null) audio = DiagnosticNmiAudio(PhysicalFrames.PreviousOrdinal, audio);
            Clock.CompleteNmi(NmiTiming.ProcessTimerCycles(WaitingSlot0, TimerOne), audio);
        }

        private static readonly byte[] WaitingSlot0 = { FrameScheduler.Waiting, 0, 0, 0 };
        private static readonly byte[] RunningSlot0 = { FrameScheduler.Running, 0, 0, 0 };
        private static readonly byte[] TimerOne = { 1, 0, 0, 0 };

        /// <summary>An NMI-dispatched frame that did not reach the ordinary tail (pause, death,
        /// door, card, loader…) is untimed: the next ordinary pass anchors again.</summary>
        private void EndClockFrame()
        {
            if (Clock.Stage == MissionClock.Phase.Nmi || Clock.Stage == MissionClock.Phase.Body) Clock.Drop();
        }

        /// <summary>$FECF → $CABB of the ordinary pass from the native prices of this frame.</summary>
        private int MeasureClockBodyPrefix(out string unsupported)
        {
            unsupported = null;
            // The frame on which $D53F releases the intro card ($9600 exit → $D554-$D567 → RTS from
            // $CAFC into $CA43) does not enter $CAE2: neither the mission head nor the $CB41 state
            // table runs, so the released CardPassContinuation owns their span. Only that owner is
            // taken here - a hold frame (_cardWait > 1) never reaches this method, because
            // Player.IntroCardPhase routes it to Oam.RunFrame, and it stays untimed.
            bool cardRelease = Player.LastCardContinuationReleased;
            int[] parts =
            {
                LastInputHookCycles, cardRelease ? Player.LastCardContinuationCycles : LastMissionHeadCycles,
                cardRelease ? 0 : Player.LastPlayerDispatchCycles,
                Player.LastScrollServiceCycles, Objects.LastPlacementSpawnCycles, LastZoneServiceCycles,
                Enemies?.LastBank6ScriptCycles ?? -1, Enemies?.LastBank1ScriptCycles ?? -1
            };
            string[] names = { "C162 input hook", cardRelease ? "D554 card continuation" : "CAE2 head", "CAFC player", "DA70 scroll", "8090 spawn", "EDF1 zone", "bank6 script", "bank1 script" };
            for (int index = 0; index < parts.Length; index++)
                if (parts[index] < 0) { unsupported = names[index]; return -1; }
            if (!TryMeasurePaletteServiceCycles(0, 0, out int palette, out unsupported)) return -1;
            int tail = TileMap == null ? -1 : ActorTailTiming.MeasureEnvironmentTail(Player.CellGridByte,
                TileMap.World.GridAddress, Player.CellIndex, 0, out unsupported);
            if (tail < 0) return -1;
            // FECF JSR → C162 hook → FED2..FED6 return, then CAE2…CABB in source order. The
            // common 6 + hook + 18 is counted once for either owner, so the card pass must not
            // reuse LastCardResumeCycles (that property already folds the common prefix in).
            return 6 + parts[0] + 18
                + parts[1] + (cardRelease ? 0 : 6 + parts[2]) + 3 + 3 + 3 + 3 + parts[3]
                + 6 + parts[4] + 3 + 3 + 3 + 2 + parts[5] + palette
                + 6 + parts[6] + 2 + 6 + 55 + parts[7] + tail;
        }

        /// <summary>$CABB onward. Returns true when the pass is left Running across the next NMI.</summary>
        private bool RunClockedTail()
        {
            _clockTail = true;
            if (!_clockNmi || Clock.Stage != MissionClock.Phase.Body) { MissionTail.Finish(); return false; }
            int prefix = MeasureClockBodyPrefix(out string reason);
            LastClockBodyPrefixCycles = prefix;
            LastClockBodyPrefixUnsupported = reason;
            if (DiagnosticBodyPrefix != null) prefix = DiagnosticBodyPrefix(PhysicalFrames.PreviousOrdinal, prefix);
            if (!Clock.BeginTail(prefix, PhysicalFrames.NmiThreshold, out long deadline))
            {
                MissionTail.Finish();
                return false;
            }
            MissionTail.AdvanceTo((int)Math.Min(deadline, int.MaxValue));
            bool committed = !MissionTail.Active;
            if (!Clock.CompleteTail(committed, MissionTail.Cycles))
            {
                MissionTail.Finish();
                return false;
            }
            return !committed;
        }

        /// <summary>The NMI found the pass Running: $C006 sees $9A=1 and takes $C0A1 (no OAM DMA,
        /// palette, CHR, VRAM or scroll publication), the same pass resumes after RTI, and nothing
        /// polls input, steps the flow or starts a new body in this frame.</summary>
        private void AdvanceRunningFrame()
        {
            BeginNmi(true);
            PhysicalFrames.CompleteFrame((Ppu.Mask & 0x18) != 0);
            int audio = NmiAudioTimingSupported ? LastNmiAudioServiceCycles : -1;
            if (DiagnosticNmiAudio != null) audio = DiagnosticNmiAudio(PhysicalFrames.PreviousOrdinal, audio);
            if (Clock.ContinueNmi(PhysicalFrames.PreviousOrdinal, NmiTiming.ProcessTimerCycles(RunningSlot0, TimerOne),
                audio, PhysicalFrames.NmiThreshold, out long deadline))
            {
                MissionTail.AdvanceTo((int)Math.Min(deadline, int.MaxValue));
                bool committed = !MissionTail.Active;
                if (!Clock.CompleteTail(committed, MissionTail.Cycles)) MissionTail.Finish();
            }
            else MissionTail.Finish();
            if (MissionTail.Active && Clock.Stage != MissionClock.Phase.Running) MissionTail.Finish();
            Player.Flags = Objects.Slots[0].Flags;
            Ppu.Render(Framebuffer);
            Frame++;
        }
    }
}
