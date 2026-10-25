using System;

namespace Darkwing.Core
{
    public sealed partial class GameFlow
    {
        public PowerOnContinuation PowerOn { get; private set; }
        public ResetProcessClock ResetClock { get; private set; }
        public bool PowerOnActive => PowerOn != null && PowerOn.Active;
        // A Running inline pass resumes after RTI without another FECF/C162 hook.
        // Once the timed region ends, the existing untimed resident keeps ownership.
        internal bool ResidentInputHookReady => !PowerOnActive &&
            (ResetClock == null || !ResetClock.Active || ResetClock.Stage == ResetProcessClock.Phase.Body);
        public long PowerOnBoundaryMaster { get; private set; }
        private ResetResidentTiming.BodyWork _resetBodyWork;
        private ResetResidentTiming.BodyWork _loaderCompletion;
        private bool _resetNmiPending, _resetBodyPending;
        private bool _resetFadeStart;
        private bool _resetIntro, _resetTextWalk, _resetIntroFlight, _resetIntroCard;
        private int _resetFirstFlightWalk, _resetCardWalk;
        private byte[] _resetFadePalette;

        /// <summary>The in-line $DE86/$E698 loop currently streams inside the frame budget;
        /// the resident stays in its native $DD5D body until the loop reaches $0F.</summary>
        private bool LoaderStreaming { get; set; }

        private void BeginPowerOnTiming()
        {
            PowerOn = new PowerOnContinuation(blockReached: ApplyPowerOnCheckpoint);
            PowerOnBoundaryMaster = PowerOn.NextNmiEdgeMaster;
            PowerOn.AdvanceToMaster(PowerOnBoundaryMaster);
        }

        private void StepPowerOnTiming()
        {
            PowerOnBoundaryMaster += PowerOnContinuation.FullFrameMaster;
            PowerOn.AdvanceToMaster(PowerOnBoundaryMaster);
            if (!PowerOn.Active)
            {
                if (PowerOn.ProcessState != FrameScheduler.Waiting || PowerOn.ProcessTimer != 1
                    || PowerOn.CommitMaster >= PowerOn.FirstEnabledNmiEdgeMaster)
                    throw new InvalidOperationException("Native reset did not reach Waiting before its first NMI");
                _resetStage = 2;
                ResetClock = new ResetProcessClock(PowerOn);
            }
        }

        internal void BeginResetNmiTiming(byte fadeFrame, byte fadeLevel, byte[] palette)
        {
            _resetNmiPending = _resetBodyPending = false;
            if (ResetClock == null || !ResetClock.Active) return;
            _resetIntro=Stage==Phase.Intro&&_introStage<=5;
            _resetTextWalk=false;
            _resetIntroFlight=false;
            _resetIntroCard=false;
            _resetFirstFlightWalk=-1;
            _resetCardWalk=-1;
            bool staging = Stage==Phase.Reset&&_resetStage == 9;
            bool streaming = Stage==Phase.Reset&&_resetStage == 8 && LoaderStreaming;
            bool loaded = Stage==Phase.Reset&&_resetStage == 8 && !LoaderStreaming;
            _resetFadeStart=false;
            if (!_resetIntro&&!staging&&!streaming&&!loaded&&(Stage!=Phase.Reset||_resetStage<2||_resetStage>7))
            { ResetClock.Refuse("Resident continuation outside the priced region");return; }
            if(_resetIntro) { } // Text reads $92 and the audio ring after this NMI.
            else if(loaded)
            {
                _resetFadeStart=_game.SceneLoadStage==1;
                _resetFadePalette=palette;
                if(_game.SceneLoadStage==3)_resetBodyWork=ResetResidentTiming.MeasureLoaderSpawn(World.CurrentWorld);
                else if(!_resetFadeStart)_resetBodyWork=ResetResidentTiming.MeasureLoaderFadeResume(fadeFrame,fadeLevel,palette);
            }
            else if (!staging && !streaming)
            {
                try
                {
                    _resetBodyWork = ResetResidentTiming.Measure(_resetStage, fadeFrame, fadeLevel,
                        _game.Player.StateTimer, palette, _game.Bank2, (byte)_game.Oam.OamCursorBytes);
                }
                catch (NotSupportedException exception) { ResetClock.Refuse(exception.Message); return; }
            }
            try
            {
                _resetNmiPending = ResetClock.BeginNmi(handler =>
                {
                    if (!_game.TryMeasureNativeNmiPublication(handler, 0, 0, 0, out int cycles, out string reason))
                        throw new NotSupportedException(reason);
                    return cycles;
                },_game.LoaderBankSwitchDepth,_game.LoaderDeferredAudio);
            }
            catch (NotSupportedException exception) { ResetClock.Refuse(exception.Message); }
        }

        /// <summary>$DD5D-$DE86 reads the ring and $D9 after the NMI service, so its body is
        /// priced at the resume, not from the pre-drain state inside BeginNmi.</summary>
        internal void MeasureResetStaging()
        {
            if (ResetClock == null || ResetClock.Stage != ResetProcessClock.Phase.Body) return;
            try
            {
                _resetBodyWork = ResetResidentTiming.MeasureStaging(_decoder.GetWorld(World.CurrentWorld),
                    _game.AudioLastId != 0, _game.AudioQueueCanEnqueue);
            }
            catch (NotSupportedException exception)
            {
                _resetBodyPending = false;
                ResetClock.Refuse(exception.Message);
            }
        }

        internal void CompleteResetNmiTiming()
        {
            if (!_resetNmiPending) return;
            _resetNmiPending = false;
            _resetBodyPending = ResetClock.CompleteNmi(_game.NmiAudioTimingSupported ? _game.LastNmiAudioServiceCycles : -1);
        }

        private void CompleteResetBodyTiming()
        {
            if (!_resetBodyPending) return;
            _resetBodyPending = false;
            try
            {
                if(_resetFadeStart)_resetBodyWork=ResetResidentTiming.MeasureLoaderFadeStart(_resetFadePalette,_game.Oam.WalkCycles);
                if(_resetTextWalk)_resetBodyWork=ResetResidentTiming.IncludeOam(_resetBodyWork,_game.Oam.WalkCycles);
                if(_resetIntroFlight)_resetBodyWork=ResetResidentTiming.IncludeOam(_resetBodyWork,_resetFirstFlightWalk);
                if(_resetIntroCard)_resetBodyWork=ResetResidentTiming.IncludeOam(_resetBodyWork,_resetCardWalk);
                ResetClock.CommitBody(_resetBodyWork, _game.LastInputHookCycles, _resetBodyWork.YieldStackPointer);
                if(_resetIntroCard) ResetClock.StopAfterCommit(0xF8B1);
            }
            catch(NotSupportedException exception) { ResetClock.Refuse(exception.Message); }
        }

        private void RecordResetIntroFlightWalk()
        {
            if(_resetBodyPending&&_resetIntroFlight)_resetFirstFlightWalk=_game.Oam.WalkCycles;
        }

        private void RecordResetIntroCardWalk()
        {
            if(_resetBodyPending&&_resetIntroCard)_resetCardWalk=_game.Oam.WalkCycles;
        }

        private void MeasureResetIntroTiming()
        {
            if(!_resetBodyPending||!_resetIntro)return;
            try
            {
                switch(_introStage)
                {
                    case 0: _resetBodyWork=ResetResidentTiming.MeasureIntroHold(_introDelay); break;
                    case 1:
                    case 3:
                        _resetBodyWork=ResetResidentTiming.MeasureIntroText(_game.Text,_game.Bank2,_game.NmiCounter,_game.AudioQueueCanEnqueue);
                        _resetTextWalk=true;
                        break;
                    case 2:
                        _resetBodyWork=_introDelay==31 ? ResetResidentTiming.MeasureIntroTextReturn(0x0E)
                            : ResetResidentTiming.MeasureIntroHold(_introDelay,0x0F);
                        break;
                    case 4: _resetBodyWork=ResetResidentTiming.MeasureIntroTextReturn(0x0F); break;
                    case 5:
                        _resetBodyWork=ResetResidentTiming.MeasureIntroFlight(_game.Objects.Slots[0],_game.Player.GravityFrac);
                        _resetIntroFlight=true;
                        _resetIntroCard=_resetBodyWork.StopPc==0xC63C;
                        break;
                }
            }
            catch(NotSupportedException exception)
            { _resetBodyPending=false;ResetClock.Refuse(exception.Message); }
        }

        /// <summary>$DE93/$DEA5/$DEA9 RTS back to $DDCB and its JSR $FF06. The loader exits
        /// to $DE00 rather than $DE99, so the priced $DEA5 fall-through owns this pass.</summary>
        private bool BeginResetStreamingTiming()
        {
            _resetBodyPending = false;
            if (ResetClock == null || ResetClock.Stage != ResetProcessClock.Phase.Body) return false;
            try
            {
                _loaderCompletion = ResetResidentTiming.MeasureLoaderCompletion(World.CurrentWorld);
            }
            catch (NotSupportedException exception)
            {
                ResetClock.Refuse(exception.Message);
                return false;
            }
            if (!ResetClock.BeginStreaming(_resetBodyWork, _game.LastInputHookCycles, _resetBodyWork.YieldStackPointer))
                return false;
            LoaderStreaming = true;
            return true;
        }

        /// <summary>$DE86/$DE91 for one frame, then the $DE93 tail and the $DDCB JSR $FF06.
        /// The loader never yields inside the loop, so slot 0 stays Running across the NMI.</summary>
        private void RunLoaderStreamFrame()
        {
            if (!LoaderStreaming) return;
            long budget = ResetClock.StreamBudgetCycles;
            if (budget > int.MaxValue) budget = int.MaxValue;
            int used = _game.StreamSceneLoad((int)budget);
            ResetClock.AdvanceStream(used,_game.LoaderNextInstructionPc);
            if (!_game.LoaderExited) return;
            // The NMI interrupts an unfinished $DE93 tail; RTI resumes it in the next frame.
            if (ResetClock.StreamBudgetCycles <= _loaderCompletion.Cycles) return;
            LoaderStreaming = false;
            if (!ResetClock.CompleteStream(_loaderCompletion, _loaderCompletion.YieldStackPointer))
            {
                _game.FinishSceneLoadStream();
                return;
            }
            _game.FinishSceneLoadStream();
        }

        private void ApplyPowerOnCheckpoint(PowerOnContinuation.Checkpoint checkpoint)
        {
            switch (checkpoint.Pc)
            {
                case 0xFE41: _game.ResetPowerOnMemory(); break;
                case 0xFE7E: _game.ResetPowerOnVideo(); break;
                case 0xC59D: _game.Ppu.WriteRegister(0x2000, 0x90); break;
                case 0xFF19: _game.PlaySound(0xF0); break;
            }
        }
    }
}
