using System;
using System.Collections.Generic;

namespace Darkwing.Core
{
    /// <summary>
    /// Scheduler time of the ordinary mission pass ($CAE2 body → $CABB engine → RNG → $FF17 yield).
    /// The clock keeps every admissible physical history as one candidate: the CPU cycle of its last
    /// committed yield ($FF19 boundary) on the cycle axis of <see cref="PhysicalFrameClock"/>. At a
    /// semantic boundary (the first ordinary pass after an untimed loader/resident stretch) the set is
    /// the whole idle inventory: every commit position whose next NMI still finds slot 0 Waiting.
    /// Native prices then move each candidate through the NMI service, the $FE92 scan and the body;
    /// the scan's $90 rescan and the OAM DMA parity merge candidates, so the set collapses without a
    /// seed. A pass whose next NMI arrives before its $FF17 store is Running: the NMI publishes
    /// nothing ($9A=1), the same pass resumes after RTI and no new body or input poll happens.
    /// Whenever two candidates disagree on that outcome or on the interrupted instruction, or a price
    /// is unknown, the clock stops visibly instead of choosing one; the caller keeps its untimed path.
    /// </summary>
    public sealed class MissionClock
    {
        public enum Phase { Off, Waiting, Nmi, Body, Running, Unsupported }

        /// <summary>FF19..FE91 instruction starts before the first $FE92 scan.</summary>
        private static readonly int[] StartupStarts = { 0, 2, 4, 6, 10, 13, 15 };
        public const int StartupCycles = 17;
        public const int FeA8ToFecfCycles = 42;
        public const int EntryCycles = FrameScheduler.NmiEntryCycles;
        public const int BlockedPublicationCycles = 27;
        public const int BankGateCycles = 36;

        private List<long> _commits = new List<long>();
        private readonly List<long> _bodyStarts = new List<long>();
        private readonly List<long> _tailStarts = new List<long>();
        private int _pubEven, _pubOdd;
        private long _threshold;
        private long _interruptedTailCycles;

        public Phase Stage { get; private set; } = Phase.Off;
        public string Unsupported { get; private set; }
        public long UnsupportedPoll { get; private set; } = -1;
        public int Candidates => Stage == Phase.Running || Stage == Phase.Body
            ? (Stage == Phase.Running ? _tailStarts.Count : _bodyStarts.Count) : _commits.Count;
        public int Anchors { get; private set; }
        public int TimedPasses { get; private set; }
        public int RunningPasses { get; private set; }
        public long Poll { get; private set; }
        public long MinDeadline { get; private set; }
        public long MaxDeadline { get; private set; }
        public long FirstCommit => _commits.Count == 0 ? -1 : _commits[0];
        public long LastCommit => _commits.Count == 0 ? -1 : _commits[_commits.Count - 1];
        public long InterruptedTailCycles => _interruptedTailCycles;

        /// <summary>An untimed stretch began (loader, resident, pause, death…): forget the timeline;
        /// the next ordinary pass anchors with the full idle inventory again.</summary>
        public void Drop()
        {
            if (Stage == Phase.Running) throw new InvalidOperationException("A Running pass cannot be dropped");
            if (Stage == Phase.Unsupported) return;
            Stage = Phase.Off;
            _commits.Clear(); _bodyStarts.Clear(); _tailStarts.Clear();
        }

        /// <summary>Leave a recorded refusal; the next ordinary pass anchors again.</summary>
        public void ClearRefusal()
        {
            if (Stage != Phase.Unsupported) return;
            Stage = Phase.Off;
        }

        private bool Refuse(string reason)
        {
            Unsupported = reason;
            UnsupportedPoll = Poll;
            Stage = Phase.Unsupported;
            _commits.Clear(); _bodyStarts.Clear(); _tailStarts.Clear();
            return false;
        }

        /// <summary>NMI of a poll whose previous pass committed. Without a timeline the full idle
        /// inventory is admitted: commits c with c−4 &lt; T (the $FF17 store done) down to one whole
        /// startup+scan period before T; every earlier commit reaches one of these scan positions.</summary>
        public bool BeginNmi(long poll, long threshold, int publicationEven, int publicationOdd)
        {
            Poll = poll;
            if (Stage == Phase.Unsupported) return false;
            if (Stage != Phase.Off && Stage != Phase.Waiting) throw new InvalidOperationException("Mission clock NMI out of order: " + Stage);
            if (publicationEven < 0 || publicationOdd < 0) return Refuse("NMI publication price unsupported");
            _threshold = threshold;
            _pubEven = publicationEven; _pubOdd = publicationOdd;
            if (Stage == Phase.Off)
            {
                _commits.Clear();
                for (long commit = threshold - StartupCycles - FrameScheduler.IdleScanCycles - 8; commit <= threshold + 3; commit++)
                    _commits.Add(commit);
                Anchors++;
            }
            foreach (long commit in _commits)
                if (commit - 4 >= threshold) return Refuse("Pass candidate still Running at its NMI");
            Stage = Phase.Nmi;
            return true;
        }

        /// <summary>After the drain/tick: each candidate's service ($C000…RTI) and its $FE92 dispatch.
        /// The DMA parity follows each candidate's own handler cycle.</summary>
        public bool CompleteNmi(int timerCycles, int audioCycles)
        {
            if (Stage == Phase.Unsupported) return false;
            if (Stage != Phase.Nmi) throw new InvalidOperationException("No mission NMI entered");
            if (audioCycles < 0) return Refuse("NMI audio price unsupported");
            _bodyStarts.Clear();
            foreach (long commit in _commits)
            {
                long accept = Accept(commit, _threshold, out int position);
                long handler = accept + EntryCycles;
                int publication = (handler & 1) == 0 ? _pubEven : _pubOdd;
                long resume = handler + publication + timerCycles + BankGateCycles + audioCycles;
                _bodyStarts.Add(resume + DispatchCycles(position) + FeA8ToFecfCycles);
            }
            Stage = Phase.Body;
            return true;
        }

        /// <summary>$FECF → $CABB is executed atomically, so every candidate must reach the engine
        /// before the next NMI. Returns the earliest engine deadline (cycles from $CABB).</summary>
        public bool BeginTail(int fecfToCabb, long nextThreshold, out long deadline)
        {
            deadline = long.MaxValue;
            if (Stage == Phase.Unsupported) return false;
            if (Stage != Phase.Body) throw new InvalidOperationException("No dispatched mission body");
            if (fecfToCabb < 0) return Refuse("Body prefix price unsupported");
            _tailStarts.Clear();
            long minDeadline = long.MaxValue, maxDeadline = long.MinValue;
            foreach (long start in _bodyStarts)
            {
                long cabb = start + fecfToCabb;
                if (cabb > nextThreshold) return Refuse("NMI inside the non-resumable body prefix");
                _tailStarts.Add(cabb);
                minDeadline = Math.Min(minDeadline, nextThreshold - cabb);
                maxDeadline = Math.Max(maxDeadline, nextThreshold - cabb);
            }
            _threshold = nextThreshold;
            MinDeadline = minDeadline;
            MaxDeadline = maxDeadline;
            deadline = minDeadline;
            return true;
        }

        /// <summary>The engine/RNG/yield tail ran to the earliest deadline. If it committed there,
        /// every later deadline commits as well. Otherwise all candidates must share that deadline:
        /// a later one could have reached a different instruction or the store itself.</summary>
        public bool CompleteTail(bool committed, long cycles)
        {
            if (Stage == Phase.Unsupported) return false;
            if (Stage != Phase.Body) throw new InvalidOperationException("No mission tail");
            if (committed)
            {
                Commit(_tailStarts, cycles);
                return true;
            }
            if (MinDeadline != MaxDeadline) return Refuse("Candidates disagree on the interrupted engine instruction");
            _interruptedTailCycles = cycles;
            Stage = Phase.Running;
            RunningPasses++;
            return true;
        }

        /// <summary>The NMI accepted inside the Running pass: blocked $C0A1 path (timers, bank gate,
        /// audio, no publication). Returns the deadline of the resumed tail against the next NMI.</summary>
        public bool ContinueNmi(long poll, int timerCycles, int audioCycles, long nextThreshold, out long deadline)
        {
            Poll = poll;
            deadline = long.MaxValue;
            if (Stage == Phase.Unsupported) return false;
            if (Stage != Phase.Running) throw new InvalidOperationException("No Running mission pass");
            if (audioCycles < 0) return Refuse("NMI audio price unsupported");
            long service = EntryCycles + BlockedPublicationCycles + timerCycles + BankGateCycles + audioCycles;
            for (int index = 0; index < _tailStarts.Count; index++)
            {
                _tailStarts[index] += service;
                deadline = Math.Min(deadline, nextThreshold - _tailStarts[index]);
            }
            _threshold = nextThreshold;
            Stage = Phase.Body;
            MinDeadline = MaxDeadline = deadline;
            return true;
        }

        private void Commit(List<long> tailStarts, long cycles)
        {
            var next = new SortedSet<long>();
            foreach (long start in tailStarts) next.Add(start + cycles);
            _commits = new List<long>(next);
            _bodyStarts.Clear(); _tailStarts.Clear();
            Stage = Phase.Waiting;
            TimedPasses++;
        }

        /// <summary>First instruction start ≥ <paramref name="threshold"/> after a yield committed at
        /// <paramref name="commit"/>: the FF19..FE91 startup, then the 93-cycle $FE92 scan.</summary>
        public static long Accept(long commit, long threshold, out int position)
        {
            foreach (int start in StartupStarts)
                if (commit + start >= threshold) { position = -1 - start; return commit + start; }
            long scan = commit + StartupCycles;
            if (threshold > scan) scan += (threshold - scan) / FrameScheduler.IdleScanCycles * FrameScheduler.IdleScanCycles;
            while (true)
            {
                foreach (int start in FrameScheduler.ScanInstructionStarts)
                    if (scan + start >= threshold) { position = start; return scan + start; }
                scan += FrameScheduler.IdleScanCycles;
            }
        }

        /// <summary>Resume point → the $FEA8 that launches slot 0 with $90 cleared: a startup accept
        /// reaches $FE92 first; a scan accept before $FE94 keeps the pass, $FE96/$FE98 see the NMI's
        /// $90 at $FEAA and rescan, later positions finish the scan and wrap.</summary>
        public static int DispatchCycles(int position)
        {
            if (position < 0) return StartupCycles - (-1 - position) + 16;
            if (position == 0 || position == 2) return 16 - position;
            if (position == 5 || position == 7) return 38 - position;
            return 109 - position;
        }
    }
}
