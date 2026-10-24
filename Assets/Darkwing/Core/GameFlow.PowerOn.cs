using System;

namespace Darkwing.Core
{
    public sealed partial class GameFlow
    {
        public PowerOnContinuation PowerOn { get; private set; }
        public bool PowerOnActive => PowerOn != null && PowerOn.Active;
        public long PowerOnBoundaryMaster { get; private set; }

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
            }
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
