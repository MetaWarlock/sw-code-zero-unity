"""Run the existing live-Editor harness and retain CLI output; no second Editor.

--suite gates on every STRICT verdict: 0 pass, 1 behavioral divergence, 2 invalid/error.
Other entries report execution only; inspect their saved comparisons for acceptance.
"""
from __future__ import annotations
import argparse
import json
from pathlib import Path
import shutil
import subprocess
import time

ROOT = Path(__file__).resolve().parents[2]
EXPECTED_CASES = {"walk", "jump", "b_attack", "hook", "traverse", "contact", "m1route",
                  "hurt", "door", "worldseeds", "longroute", *[f"w{i:02X}_fall" for i in range(7)],
                  *[f"m{i}" for i in range(2, 8)]}


def evaluate_suite(report):
    rows = report.get("results", [])
    if (report.get("schema") != 1 or len(rows) != 24
            or {r.get("name") for r in rows} != EXPECTED_CASES or report.get("cases") != 24):
        raise ValueError("Incomplete/unknown suite schema or missing/duplicate scenarios")
    for r in rows:
        if r.get("result") not in ("PASS", "FAIL", "ERROR") or r.get("strict") not in ("PASS", "FAIL", "ERROR"):
            raise ValueError("Unknown verdict")
    passed = sum(r["result"] == "PASS" for r in rows)
    strict = sum(r["strict"] == "PASS" for r in rows)
    errors = sum("ERROR" in (r["result"], r["strict"]) for r in rows)
    verdict = "ERROR" if errors else "PASS" if strict == 24 else "FAIL"
    if (report.get("resultPassed"), report.get("strictPassed"), report.get("errors"), report.get("verdict")) != (passed, strict, errors, verdict):
        raise ValueError("Inconsistent suite totals")
    return 2 if errors else 0 if verdict == "PASS" else 1


def main():
    ap = argparse.ArgumentParser(__doc__)
    ap.add_argument("--suite", action="store_true")
    ap.add_argument("--file", default="AgentScripts/PhysCompare.cs")
    ap.add_argument("--entry", default="PhysCompare.Suite")
    ap.add_argument("--args")
    ap.add_argument("--out", type=Path, required=True)
    a = ap.parse_args()
    a.out.mkdir(parents=True, exist_ok=True)
    cmd = [shutil.which("unity") or str(Path.home()/"AppData/Local/Unity/bin/unity.exe"),
           "command", "run_script", "--file", a.file, "--entry", a.entry,
           "--project-path", str(ROOT), "--caller", "plugin", "--skill", "unity-cli",
           "--timeout", "270", "--timeout_ms", "240000", "--format", "json"]
    if a.args is not None:
        cmd += ["--args", a.args]
    record = {"command": cmd, "scope": "execution only", "startedUtcEpoch": time.time()}
    code = 2
    try:
        run = subprocess.run(cmd, cwd=ROOT, capture_output=True, timeout=300)
        (a.out/"stdout.json").write_bytes(run.stdout)
        (a.out/"stderr.txt").write_bytes(run.stderr)
        record["cliExitCode"] = run.returncode
        response = json.loads(run.stdout.decode("utf-8-sig"))
        inner = (response.get("data") or {}).get("result") or {}
        if run.returncode or response.get("success") is not True or inner.get("success") is not True:
            raise RuntimeError("CLI/compile/execution failure; see stdout.json and stderr.txt")
        (a.out/"returned.txt").write_text(str(inner.get("result", "")), encoding="utf-8")
        code = 0
        record["execution"] = "EXECUTED"
        if a.suite:
            if a.entry != "PhysCompare.Suite" or a.file != "AgentScripts/PhysCompare.cs":
                raise ValueError("--suite requires the standard Suite entry")
            report_path = ROOT/"Temp/suite_result.json"
            if report_path.stat().st_mtime < record["startedUtcEpoch"]:
                raise ValueError("Stale suite_result.json")
            report = json.loads(report_path.read_text(encoding="utf-8-sig"))
            code = evaluate_suite(report)
            record.update(scope="selected STRICT fields", verdict=report["verdict"],
                          resultPassed=report["resultPassed"], strictPassed=report["strictPassed"], cases=24)
            shutil.copy2(report_path, a.out/"suite_result.json")
            shutil.copy2(ROOT/"Temp/suite_summary.txt", a.out/"suite_summary.txt")
            for row in report["results"]:
                src = ROOT/row["details"]
                if not src.is_file() or src.stat().st_mtime < record["startedUtcEpoch"]:
                    raise ValueError("Missing/stale case details: " + str(src))
                shutil.copy2(src, a.out/src.name)
    except Exception as e:
        record["error"] = f"{type(e).__name__}: {e}"
        code = 2
    record.update(exitCode=code, elapsedSeconds=round(time.time()-record["startedUtcEpoch"], 3))
    (a.out/"execution.json").write_text(json.dumps(record, ensure_ascii=False, indent=2), encoding="utf-8")
    print(json.dumps(record, ensure_ascii=False))
    return code


if __name__ == "__main__":
    raise SystemExit(main())
