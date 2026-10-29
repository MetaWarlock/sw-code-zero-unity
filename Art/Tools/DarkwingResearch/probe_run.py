"""Flexible original-ROM probe: runtime_capture.lua.in + input schedules.

Read-only instrumentation. Output directory must be new.
Examples:
  python probe_run.py --out Art/Research/DarkwingDuck/reference/boot --frames 300
  python probe_run.py --out ... --frames 900 --input 0:start@30-33 --input 0:a@120-123
  python probe_run.py --out ... --frames 600 --load-state path/frame.mss --input 0:right@10-60
"""
from __future__ import annotations
import argparse
import hashlib
import json
from pathlib import Path
import subprocess
import sys
import wave

ROOT = next(p for p in Path(__file__).resolve().parents if (p / 'ProjectSettings').is_dir())
EXPECTED_SHA256 = "1271dce8678ffc261e2c05cc785ae53e29ac1f9ecf6265a4265bed825abb0a00"


def world_collision_grid(wid: int):
    """Metatile collision nibbles of a whole world, one char per 16x16 block.

    Character = chr(64 + type): 0 air, 1 one-way, 2 solid, 3 wall, 4 spike,
    5 door, 7/8 grapple-ledge (Art/Research/DarkwingDuck/docs/level_format.xhtml).
    Grid slots without a room decode as air. Source of truth is decode_room.py
    (verified 960/960 tiles and 64/64 attributes against probe_m1_enter), so the
    autopilot reacts to original level data rather than guessed timings.
    World pixel (X, Y) -> metatile (X // 16, Y // 16) with X = col*256 + localX
    and Y = row*256 + localY.
    """
    sys.path.insert(0, str(Path(__file__).resolve().parent))
    import decode_room as dr
    w = dr.World(wid)
    rows = len(w.grid) // w.row_width
    bands = [[] for _ in range(16 * rows)]
    hist = {}
    for row in range(rows):
        for col in range(w.row_width):
            cell = w.grid[row * w.row_width + col]
            typ = dr.decode_room(w, cell)[3] if cell else [[0] * 16 for _ in range(16)]
            for mr in range(16):
                band = bands[row * 16 + mr]
                for mc in range(16):
                    t = typ[mr][mc]
                    band.append(t)
                    hist[str(t)] = hist.get(str(t), 0) + 1
    grid = "".join("".join(chr(64 + t) for t in band) for band in bands)
    return grid, w.row_width * 16, 16 * rows, w.row_width, hist


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
    p.add_argument("--compact", action="store_true", help="Omit verbose PPU/all-write logs; retain frame state and bank latches")
    p.add_argument("--patch", action="append", default=[],
                   help="seed emulated RAM once: ADDR=VALUE (hex, NES $0000-$07FF). "
                        "Test fixture only — the ROM and the acceptance goldens stay untouched.")
    p.add_argument("--patch-frame", type=int, default=1, help="frame at which --patch applies")
    p.add_argument("--save-state", type=Path,
                   help="write a .mss after the run so a later --load-state trial continues from it")
    p.add_argument("--bot", default="", help="controller-only autopilot policy, e.g. "
                        "'world=0, startFrame=90, right=1, attack=1, jumpHold=26, stallAfter=60, feet=12, ahead=1'")
    args = p.parse_args()
    if args.frames < 1:
        p.error("frames must be positive")
    rom = ROOT / "Art/Darkwing Duck (USA).nes"
    assert hashlib.sha256(rom.read_bytes()).hexdigest() == EXPECTED_SHA256
    out = args.out.resolve()
    out.mkdir(parents=True, exist_ok=False)
    template = Path(__file__).with_name("runtime_capture.lua.in").read_text(encoding="utf-8")
    lua = out / "capture.lua"
    patchItems = []
    for entry in args.patch:
        addrText, _, valueText = entry.partition("=")
        addr, value = int(addrText, 16), int(valueText, 16)
        if not (0 <= addr <= 0x7FF and 0 <= value <= 0xFF):
            p.error(f"patch out of range (RAM $0000-$07FF, byte value): {entry}")
        patchItems.append(f"{{addr=0x{addr:03X}, value=0x{value:02X}}}")
    lua.write_text(template
        .replace("__OUTPUT_DIR__", json.dumps(out.as_posix(), ensure_ascii=False))
        .replace("__FRAME_COUNT__", str(args.frames))
        .replace("__TRACE__", "false" if args.compact else "true")
        .replace("__VRAM_EVERY__", str(args.vram_every))
        .replace("__PATCHES__", "{" + ",".join(patchItems) + "}")
        .replace("__PATCH_FRAME__", str(args.patch_frame))
        .replace("__SNAPSHOT_EVERY__", str(args.screenshot_every if args.screenshot_every > 0 else 10 ** 9)), encoding="utf-8")
    exe = ROOT / "Art/Tools/DarkwingResearch/vendor/mesen-agent/mesen-agent.exe"
    argv = [str(exe), str(rom), "--home", str(out / "home"), "--frames", str(args.frames),
            "--timeout", str(args.timeout), "--lua", str(lua), "--lua-allow-io"]
    if args.bot:
        policy = {"startFrame": 1, "right": 1, "attack": 1, "select": 0,
                  "stallAfter": 60, "jumpHold": 26, "feet": 12, "ahead": 1, "cool": 12,
                  "threat": 40, "holdMax": 150, "world": -1, "vgap": 24, "route": "",
                  "range": 60, "rise": 50, "land": 12}
        for entry in args.bot.split(","):
            key, _, value = entry.partition("=")
            key = key.strip()
            if key not in policy:
                p.error(f"unknown --bot key: {key}")
            try:
                policy[key] = int(value.strip(), 0)
            except ValueError:
                policy[key] = value.strip()
        bot = out / "bot.lua"
        grid, cols, rows, width, hist = "", 0, 0, 0, {}
        if policy["world"] >= 0:
            grid, cols, rows, width, hist = world_collision_grid(policy["world"])
            print(json.dumps({"world": policy["world"], "metatile_cols": cols,
                              "metatile_rows": rows, "cells_across": width,
                              "type_histogram": hist}))
        text = Path(__file__).with_name("autopilot.lua.in").read_text(encoding="utf-8")
        for token, value in (("__OUTPUT_DIR__", json.dumps(out.as_posix(), ensure_ascii=False)),
                             ("__COLL__", '"' + grid + '"'), ("__COLS__", str(cols)),
                             ("__ROWS__", str(rows)), ("__WCOLS__", str(width)),
                             ("__TYPE_BASE__", "64"),
                             ("__BOT__", "{" + ",".join(
                                 f"{k}={json.dumps(v) if isinstance(v, str) else v}"
                                 for k, v in policy.items()) + "}")):
            text = text.replace(token, value)
        bot.write_text(text, encoding="utf-8")
        argv += ["--lua", str(bot)]
        print(json.dumps({"bot_policy": policy}))
    if args.screenshot_every > 0:
        argv += ["--screenshot", str(out / "final.png")]
    if args.load_state:
        argv += ["--load-state", str(args.load_state.resolve())]
    if args.save_state:
        argv += ["--save-state", str(args.save_state.resolve())]
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
