"""Capture a bounded original-ROM experiment; no game RAM modifications."""
import argparse
import hashlib
import json
from pathlib import Path
import subprocess
import wave
from extract_rom import ROOT, EXPECTED_SHA256


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--out", type=Path, required=True, help="A new directory, never overwritten")
    parser.add_argument("--frames", type=int, required=True)
    parser.add_argument("--load-state", type=Path)
    parser.add_argument("--input", action="append", default=[])
    parser.add_argument("--screenshots-every", type=int, default=0)
    parser.add_argument("--timeout", type=int, default=60)
    args = parser.parse_args()
    if args.frames < 1 or args.screenshots_every < 0:
        parser.error("frames must be positive; screenshot interval cannot be negative")
    rom = ROOT / "Art/Darkwing Duck (USA).nes"
    assert hashlib.sha256(rom.read_bytes()).hexdigest() == EXPECTED_SHA256
    out = args.out.resolve()
    out.mkdir(parents=True, exist_ok=False)
    template = Path(__file__).with_name("oracle_capture.lua.in").read_text(encoding="utf-8")
    # Forward slashes and JSON quoting yield a valid Lua string for these paths.
    lua_path = out / "capture.lua"
    lua_path.write_text(template.replace("__OUTPUT_DIR__", json.dumps(out.as_posix(), ensure_ascii=False))
        .replace("__FRAME_COUNT__",str(args.frames)).replace("__SCREENSHOT_EVERY__",str(args.screenshots_every)),encoding="utf-8")
    exe = ROOT / "Art/Tools/DarkwingResearch/vendor/mesen-agent/mesen-agent.exe"
    argv = [str(exe), str(rom), "--home", str(out/"home"), "--frames", str(args.frames),
        "--timeout", str(args.timeout), "--lua", str(lua_path), "--lua-allow-io",
        "--screenshot", str(out/"final.png"), "--audio", str(out/"audio.wav"),
        "--save-state", str(out/"final.mss"), "--state-json",str(out/"state.json"),
        "--save-cdl", "NesPrgRom="+str(out/"coverage.cdl")]
    if args.load_state:
        argv += ["--load-state",str(args.load_state.resolve())]
    for entry in args.input:
        argv += ["--input",entry]
    metadata = {"rom_sha256": EXPECTED_SHA256, "argv": argv,
                "binary_sha256": hashlib.sha256(exe.read_bytes()).hexdigest(),
                "core_sha256": hashlib.sha256(exe.with_name("MesenCore.dll").read_bytes()).hexdigest(),
                "lua_sha256":hashlib.sha256(lua_path.read_bytes()).hexdigest(),
                "load_state_sha256":hashlib.sha256(args.load_state.read_bytes()).hexdigest() if args.load_state else None}
    (out/"request.json").write_text(json.dumps(metadata,indent=2),encoding="utf-8")
    result = subprocess.run(argv,cwd=ROOT,capture_output=True,text=True,encoding="utf-8",errors="replace",timeout=args.timeout+20)
    (out/"events.jsonl").write_text(result.stdout,encoding="utf-8")
    (out/"stderr.txt").write_text(result.stderr,encoding="utf-8")
    events = [json.loads(line) for line in result.stdout.splitlines() if line.strip()]
    summaries = [v for v in events if v.get("event")=="summary"]
    ok = result.returncode==0 and bool(summaries) and summaries[-1].get("ok") is True
    ok = ok and summaries[-1].get("frames_executed")==args.frames and summaries[-1].get("reason")=="frame_limit"
    ok = ok and all(v.get("error_count",0)==0 for v in events if v.get("event")=="lua_log")
    hashes = {}
    for name,size in [("ram_frames.bin",2048),("palette_frames.bin",32),("oam_frames.bin",256)]:
        f = out/name
        ok = ok and f.exists() and f.stat().st_size == size*args.frames
    for f in out.iterdir():
        if f.is_file() and f.name not in ["request.json","events.jsonl","stderr.txt","capture.lua"]:
            hashes[f.name] = hashlib.sha256(f.read_bytes()).hexdigest()
    audio = None
    if (out/"audio.wav").exists():
        with wave.open(str(out/"audio.wav"),"rb") as w:
            audio = {"rate":w.getframerate(),"channels":w.getnchannels(),"sample_width":w.getsampwidth(),"frames":w.getnframes()}
    report = {"ok":ok,"summary":summaries[-1] if summaries else None,"audio":audio,"sha256":hashes,
              "scope":"Instrumentation smoke test. Not proof of game/port equivalence."}
    (out/"verification.json").write_text(json.dumps(report,indent=2),encoding="utf-8")
    print(json.dumps({"out":str(out),**report},indent=2))
    raise SystemExit(0 if ok else 1)


if __name__ == "__main__":
    main()
