"""Flexible original-ROM probe: runtime_capture.lua.in + input schedules.

Read-only instrumentation. Output directory must be new.
Examples:
  python probe_run.py --out Research/DarkwingDuck/reference/boot --frames 300
  python probe_run.py --out ... --frames 900 --input 0:start@30-33 --input 0:a@120-123
  python probe_run.py --out ... --frames 600 --load-state path/frame.mss --input 0:right@10-60
"""
from __future__ import annotations
import argparse
import hashlib
import json
from pathlib import Path
import subprocess
import wave

ROOT = Path(__file__).resolve().parents[2]
EXPECTED_SHA256 = "1271dce8678ffc261e2c05cc785ae53e29ac1f9ecf6265a4265bed825abb0a00"


def main():
    p = argparse.ArgumentParser(description=__doc__)
    p.add_argument("--out", type=Path, required=True)
    p.add_argument("--frames", type=int, required=True)
    p.add_argument("--load-state", type=Path)
    p.add_argument("--input", action="append", default=[])
    p.add_argument("--screenshot-every", type=int, default=0)
    p.add_argument("--vram-every", type=int, default=60)
    p.add_argument("--timeout", type=int, default=90)
    p.add_argument("--extra", action="append", default=[], help="extra raw CLI args for mesen-agent")
    args = p.parse_args()
    if args.frames < 1:
        p.error("frames must be positive")
    rom = ROOT / "Art/Darkwing Duck (USA).nes"
    assert hashlib.sha256(rom.read_bytes()).hexdigest() == EXPECTED_SHA256
    out = args.out.resolve()
    out.mkdir(parents=True, exist_ok=False)
    template = Path(__file__).with_name("runtime_capture.lua.in").read_text(encoding="utf-8")
    lua = out / "capture.lua"
    lua.write_text(template
        .replace("__OUTPUT_DIR__", json.dumps(out.as_posix(), ensure_ascii=False))
        .replace("__FRAME_COUNT__", str(args.frames))
        .replace("__VRAM_EVERY__", str(args.vram_every))
        .replace("__SNAPSHOT_EVERY__", str(args.screenshot_every if args.screenshot_every > 0 else 10 ** 9)), encoding="utf-8")
    exe = ROOT / "Tools/DarkwingResearch/vendor/mesen-agent/mesen-agent.exe"
    argv = [str(exe), str(rom), "--home", str(out / "home"), "--frames", str(args.frames),
            "--timeout", str(args.timeout), "--lua", str(lua), "--lua-allow-io"]
    if args.screenshot_every > 0:
        argv += ["--screenshot", str(out / "final.png")]
    if args.load_state:
        argv += ["--load-state", str(args.load_state.resolve())]
    for entry in args.input:
        argv += ["--input", entry]
    for extra in args.extra:
        argv += extra.split()
    (out / "request.json").write_text(json.dumps({"rom_sha256": EXPECTED_SHA256, "argv": argv,
        "binary_sha256": hashlib.sha256(exe.read_bytes()).hexdigest()}, indent=2), encoding="utf-8")
    result = subprocess.run(argv, cwd=ROOT, capture_output=True, text=True, encoding="utf-8",
                            errors="replace", timeout=args.timeout + 30)
    (out / "events.jsonl").write_text(result.stdout, encoding="utf-8")
    (out / "stderr.txt").write_text(result.stderr, encoding="utf-8")
    events = [json.loads(line) for line in result.stdout.splitlines() if line.strip()]
    summaries = [v for v in events if v.get("event") == "summary"]
    ok = result.returncode == 0 and bool(summaries) and summaries[-1].get("ok") is True
    print(json.dumps({"out": str(out), "ok": ok, "summary": summaries[-1] if summaries else None},
                     indent=2))
    raise SystemExit(0 if ok else 1)


if __name__ == "__main__":
    main()
