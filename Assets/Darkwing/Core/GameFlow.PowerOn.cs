using System;

namespace Darkwing.Core
{
    public sealed partial class GameFlow
    {
        public PowerOnContinuation PowerOn { get; private set; }
        public ResetProcessClock ResetClock { get; private set; }
        public bool PowerOnActive => PowerOn != null && PowerOn.Active;
        public long PowerOnBoundaryMaster { get; private set; }
        private ResetResidentTiming.BodyWork _resetBodyWork;
        private bool _resetNmiPending, _resetBodyPending;

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
            bool staging = _resetStage == 9;
            if (!staging && (_resetStage < 2 || _resetStage > 7)) return;
            if (!staging)
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
                });
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
            ResetClock.CommitBody(_resetBodyWork, _game.LastInputHookCycles, _resetBodyWork.YieldStackPointer);
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
