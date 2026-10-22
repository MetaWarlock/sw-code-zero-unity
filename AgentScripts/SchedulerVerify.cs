using System;
using System.IO;
using System.Text;
using Darkwing.Core;

public static class SchedulerVerify
{
    public static string Run(string relativePath = "Temp/city_idle_phase.csv")
    {
        if (UnityEditor.EditorApplication.isCompiling || UnityEditor.EditorUtility.scriptCompilationFailed)
            throw new InvalidOperationException("Refuse a stale Core assembly while compilation is incomplete");

        string path = Path.GetFullPath(Path.Combine("D:/Art/SW Code Zero Unity/", relativePath));
        string[] lines = File.ReadAllLines(path);
        if (lines.Length < 2 || lines[0] != "poll,cycle,event,last_idle,phase,accept_phase,scans,s0,s1,s2,s3,sp,e4")
            throw new InvalidDataException("Unknown idle-phase trace schema");

        long lastCommit = -1;
        int pollChecks = 0, nmiChecks = 0, mismatches = 0;
        var detail = new StringBuilder();

        for (int i = 1; i < lines.Length; i++)
        {
            string[] f = lines[i].Split(',');
            if (f.Length != 13) throw new InvalidDataException("Bad row " + i);
            string ev = f[2];
            long cycle = long.Parse(f[1]);

            if (ev == "commit")
            {
                lastCommit = cycle;
                continue;
            }
            if (lastCommit < 0) continue;

            long anchor = lastCommit + FrameScheduler.CommitToIdleCycles;
            if (ev == "poll" && f[3].Length != 0)
            {
                if (cycle < anchor)
                {
                    mismatches++;
                    if (mismatches <= 8) detail.AppendLine($"poll {f[0]}: poll before anchor");
                    continue;
                }
                int phase = FrameScheduler.PhaseAt(cycle, anchor);
                int loggedPhase = int.Parse(f[4]);
                long loggedLast = long.Parse(f[3]);
                long expectedLast = anchor + ((cycle - 1 - anchor) / FrameScheduler.IdleScanCycles) * FrameScheduler.IdleScanCycles;
                if (phase != loggedPhase || loggedLast != expectedLast)
                {
                    mismatches++;
                    if (mismatches <= 8)
                        detail.AppendLine($"poll {f[0]}: anchor={anchor} phase={loggedPhase}/{phase} last={loggedLast}/{expectedLast}");
                }
                pollChecks++;
            }
            else if (ev == "nmi_idle")
            {
                int accept = FrameScheduler.AcceptPhaseAt(cycle, anchor);
                int loggedAccept = int.Parse(f[5]);
                if (accept != loggedAccept || !FrameScheduler.IsInstructionStart(accept))
                {
                    mismatches++;
                    if (mismatches <= 8)
                        detail.AppendLine($"nmi {f[0]}: anchor={anchor} accept={loggedAccept}/{accept}");
                }
                nmiChecks++;
            }
        }

        CheckSpecific(190135568, 190146111, 34, 190146120, 36, "f6385", ref mismatches, detail);
        CheckSpecific(190195329, 190205672, 20, 190205680, 21, "f6387", ref mismatches, detail);
        CheckSpecific(190238972, 190265233, 35, 190265241, 36, "f6389", ref mismatches, detail);

        var scheduler = new FrameScheduler();
        scheduler.AnchorAfterCommit(1000);
        scheduler.CommitWait(0, 2000, 0x1FF);
        if (scheduler.IdleStart != 2017 || scheduler.State[0] != FrameScheduler.Waiting || scheduler.Timer[0] != 1)
            throw new InvalidOperationException("waiting state/anchor mismatch");
        scheduler.Nmi();
        if (scheduler.State[0] != FrameScheduler.Ready || scheduler.NmiCounter != 1 || !scheduler.FrameGate)
            throw new InvalidOperationException("NMI timer/gate mismatch");
        if (scheduler.TryTakeReady(out _))
            throw new InvalidOperationException("ready slot taken through frame gate");
        scheduler.AdvanceScans(2018);
        if (scheduler.FrameGate || !scheduler.TryTakeReady(out int slot) || slot != 0)
            throw new InvalidOperationException("scan did not release ready slot");
        scheduler.Launch(0);
        if (scheduler.State[0] != FrameScheduler.Running)
            throw new InvalidOperationException("launch mismatch");

        if (mismatches != 0)
            throw new InvalidOperationException($"phase mismatches={mismatches}\n{detail}");
        return $"RESULT PASS polls={pollChecks} nmi={nmiChecks} mismatches=0 specific=f6385,f6387,f6389 slot=pass";
    }

    private static void CheckSpecific(long anchor, long poll, int pollPhase, long c000, int accept, string name,
        ref int mismatches, StringBuilder detail)
    {
        int p = FrameScheduler.PhaseAt(poll, anchor);
        int a = FrameScheduler.AcceptPhaseAt(c000, anchor);
        if (p == pollPhase && a == accept && FrameScheduler.IsInstructionStart(a)) return;
        mismatches++;
        detail.AppendLine($"{name}: phase={pollPhase}/{p} accept={accept}/{a} anchor={anchor}");
    }
}
