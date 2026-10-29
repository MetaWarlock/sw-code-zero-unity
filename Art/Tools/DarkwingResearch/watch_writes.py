"""Who writes this RAM byte? Logs PC + value for every write to an address range.

Read-only instrumentation (ROM and goldens untouched). Same launch conventions as
probe_run.py: verified ROM hash, --load-state, --input "player:button@start-end".

Usage:
  python watch_writes.py --out Art/Temp/watch50 --frames 300 \\
      --load-state Art/Research/DarkwingDuck/reference/m1_standing.mss \\
      --input 0:right@2-290 --input 0:a@160-185 --input 0:a@290-315 \\
      --addr 0x50
Output: <out>/writes.csv = relative_frame,cpu_cycle,pc,addr,value
"""
from __future__ import annotations
import argparse
import hashlib
import json
from pathlib import Path
import subprocess

ROOT = next(p for p in Path(__file__).resolve().parents if (p / 'ProjectSettings').is_dir())
EXPECTED_SHA256 = "1271dce8678ffc261e2c05cc785ae53e29ac1f9ecf6265a4265bed825abb0a00"


def parse_range(text: str):
    lo, _, hi = text.partition("-")
    a = int(lo, 16) if lo.lower().startswith("0x") else int(lo, 16)
    b = int(hi, 16) if hi else a
    if not (0 <= a <= 0x7FF and a <= b <= 0x7FF):
        raise argparse.ArgumentTypeError("RAM ranges are NES $000-$7FF")
    return a, b


def main():
    p = argparse.ArgumentParser(description=__doc__)
    p.add_argument("--out", type=Path, required=True)
    p.add_argument("--frames", type=int, required=True)
    p.add_argument("--addr", action="append", type=parse_range, required=True,
                   help="NES RAM byte or range, e.g. 0x50 or 0x40-0x43")
    p.add_argument("--load-state", type=Path)
    p.add_argument("--input", action="append", default=[])
    p.add_argument("--timeout", type=int, default=120)
    args = p.parse_args()

    rom = ROOT / "Art/Darkwing Duck (USA).nes"
    assert hashlib.sha256(rom.read_bytes()).hexdigest() == EXPECTED_SHA256
    out = args.out.resolve()
    out.mkdir(parents=True, exist_ok=True)
    template = Path(__file__).with_name("zero_page_watch.lua.in").read_text(encoding="utf-8")
    ranges = ",".join(f"{{lo=0x{lo:03X}, hi=0x{hi:03X}}}" for lo, hi in args.addr)
    lua = out / "watch.lua"
    lua.write_text(template
        .replace("__OUTPUT_DIR__", json.dumps(out.as_posix(), ensure_ascii=False))
        .replace("__RANGES__", "{" + ranges + "}")
        .replace("__FRAME_COUNT__", str(args.frames)), encoding="utf-8")

    exe = ROOT / "Art/Tools/DarkwingResearch/vendor/mesen-agent/mesen-agent.exe"
    argv = [str(exe), str(rom), "--home", str(out / "home"), "--frames", str(args.frames),
            "--timeout", str(args.timeout), "--lua", str(lua), "--lua-allow-io"]
    if args.load_state:
        argv += ["--load-state", str(args.load_state.resolve())]
    for entry in args.input:
        argv += ["--input", entry]
    (out / "request.json").write_text(json.dumps({"rom_sha256": EXPECTED_SHA256, "argv": argv}, indent=2),
                                      encoding="utf-8")
    result = subprocess.run(argv, cwd=ROOT, capture_output=True, text=True,
                            encoding="utf-8", errors="replace", timeout=args.timeout + 30)
    (out / "events.jsonl").write_text(result.stdout, encoding="utf-8")
    (out / "stderr.txt").write_text(result.stderr, encoding="utf-8")
    events = [json.loads(line) for line in result.stdout.splitlines() if line.strip()]
    summaries = [v for v in events if v.get("event") == "summary"]
    ok = result.returncode == 0 and bool(summaries) and summaries[-1].get("ok") is True
    print(json.dumps({"out": str(out), "ok": ok, "summary": summaries[-1] if summaries else None}, indent=2))
    raise SystemExit(0 if ok else 1)


if __name__ == "__main__":
    main()
