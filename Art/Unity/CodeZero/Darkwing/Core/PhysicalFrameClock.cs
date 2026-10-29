using System;

namespace Darkwing.Core
{
    /// <summary>The PPU side of the physical clock: one vblank edge per input poll, from the
    /// cold power-on profile (<see cref="PowerOnContinuation"/>) on. A frame is one PPU dot short
    /// when its pre-render line is odd and rendering is enabled at dot 339. The NMI edge reaches
    /// pre-render 339 about 2386 CPU cycles later; the $C00F publication writes $2001 well before
    /// that and every body write comes after the NMI service, so the mask left by the NMI decides
    /// the frame's skip. CPU cycles are counted on the same axis as the power-on continuation.</summary>
    public sealed class PhysicalFrameClock
    {
        public PhysicalFrameClock(long firstEdgeMaster, int cpuOffset)
        {
            if ((uint)cpuOffset >= PowerOnContinuation.CpuMasterTicks) throw new ArgumentOutOfRangeException(nameof(cpuOffset));
            EdgeMaster = firstEdgeMaster;
            CpuOffset = cpuOffset;
            Ordinal = 1;
        }

        public int CpuOffset { get; }
        /// <summary>Master tick of the vblank edge that starts the current poll's frame.</summary>
        public long EdgeMaster { get; private set; }
        /// <summary>1-based poll ordinal of the current frame.</summary>
        public long Ordinal { get; private set; }
        /// <summary>Poll ordinal of the frame closed by the last <see cref="CompleteFrame"/>.</summary>
        public long PreviousOrdinal => Ordinal - 1;
        public bool LastSkipped { get; private set; }
        /// <summary>NMI threshold of the frame closed by the last <see cref="CompleteFrame"/>.</summary>
        public long PreviousNmiThreshold { get; private set; } = -1;

        /// <summary>First CPU instruction boundary at which this frame's NMI can be accepted.</summary>
        public long NmiThreshold => Threshold(EdgeMaster);
        /// <summary>The same boundary for the next frame if this frame skips its dot or not.</summary>
        public long NextNmiThreshold(bool renderingAt339) => Threshold(NextEdge(renderingAt339));

        /// <summary>Measured on the original (2066 idle-scan accepts of the city route, edges from
        /// the power-on profile): the first accepting boundary is the CPU cycle that contains the
        /// edge, T = ⌈(E − CpuOffset − 10)/12⌉. Edge residues mod 12 are only 1/5/9 (frame lengths
        /// are multiples of four ticks), where every offset in the measured window −12…−9 agrees.</summary>
        public long Threshold(long edgeMaster) => (edgeMaster - CpuOffset + 1) / PowerOnContinuation.CpuMasterTicks;

        /// <summary>Close the current frame once the NMI publication has left its mask.</summary>
        public void CompleteFrame(bool renderingAt339)
        {
            long next = NextEdge(renderingAt339);
            PreviousNmiThreshold = NmiThreshold;
            LastSkipped = next != EdgeMaster + PowerOnContinuation.FullFrameMaster;
            EdgeMaster = next;
            Ordinal++;
        }

        private long NextEdge(bool renderingAt339)
        {
            bool oddPreRender = ((Ordinal + 1) & 1) != 0;
            return EdgeMaster + PowerOnContinuation.FullFrameMaster
                - (renderingAt339 && oddPreRender ? PowerOnContinuation.PpuMasterTicks : 0);
        }
    }
}
