"""ZERO-72: prepare and feed a pinned darkwing-duck-recomp NTSC probe."""
from __future__ import annotations

import argparse
import csv
import hashlib
import json
import os
from pathlib import Path
import subprocess
import time

ROOT = next(p for p in Path(__file__).resolve().parents if (p / "ProjectSettings").is_dir())
ROM_SHA256 = "1271dce8678ffc261e2c05cc785ae53e29ac1f9ecf6265a4265bed825abb0a00"
INPUT_SHA256 = "137190299728c9ea6285ebf057fa25b1efaf916b9aeb2ad6d21fa46efb7f6275"
EXPECTED_POLLS = 38886
ROUTE_INPUTS = {
    INPUT_SHA256: EXPECTED_POLLS,
    "ee97b1e9a30d3746b85d1cb573edb913ec6fe0e81f21e9a4148d82d6c08c73f6": 77688,
    "50718e87aebd5798f72720e1f84dbe2531a79e8b459cf4a7fbcb43598fe1eb70": 24261,
    "510925941e2397b6d86aae459cbd4dde291a6574963a01b104f5f7d3b88a9100": 29360,
    "fe01e37d28252b79ee80098c70c2f9a29a03f35a765558f5e0afc7d4f34b20e4": 73407,
    "ad2f4a1bc5e94718490e3b1acce69b478e7231b7fee7632164d6e1c7605b6fff": 12517,
    "d624d9e06cfbd740a8d703cbc3f35c0c542a609935ec177a95f749e5e0bb7283": 32283,
    "f4c6593d11b5c21de5b651b3719acfa6c6435cb97374a901998471cb65bf9f79": 38650,
}
ROUTE_BINARY_SHA256 = "2669e6885267b7de96bede8a455e5cd690a7ca3aa7c587bd7961e59a46c819dd"
OLD_ROOT = "local root = 'D:/Art/SW Code Zero Unity/'"


def replace_once(path: Path, old: str, new: str) -> None:
    text = path.read_text(encoding="utf-8")
    if new in text:
        return
    if text.count(old) != 1:
        raise RuntimeError(f"Expected one patch site in {path}: {old[:80]!r}")
    path.write_text(text.replace(old, new), encoding="utf-8")


def prepare(source: Path) -> None:
    source = source.resolve()
    replace_once(source / "CMakeLists.txt",
        'set(GAME_CONFIG "${CMAKE_SOURCE_DIR}/config/darkwing_duck.toml")',
        'set(GAME_CONFIG "${CMAKE_SOURCE_DIR}/config/darkwing_duck.toml" CACHE FILEPATH "Game recompilation config")')
    replace_once(source / "CMakeLists.txt",
        'target_compile_options(game_recomp PRIVATE -O1 -Wno-unused-label)',
        'if(MSVC)\n'
        '  target_compile_options(game_recomp PRIVATE /O1)\n'
        'else()\n'
        '  target_compile_options(game_recomp PRIVATE -O1 -Wno-unused-label)\n'
        'endif()')
    replace_once(source / "src/main.c",
        '        else if (!strcmp(a, "--overclock")) ui_config.remove_slowdown = true;',
        '        else if (!strcmp(a, "--no-overclock")) ui_config.remove_slowdown = false;\n'
        '        else if (!strcmp(a, "--overclock")) ui_config.remove_slowdown = true;')
    replace_once(source / "src/main.c",
        '            "  --overclock         remove slowdown (extra CPU time per frame)\\n"',
        '            "  --no-overclock      keep the original NTSC/PAL CPU budget per frame\\n"\n'
        '            "  --overclock         remove slowdown (extra CPU time per frame)\\n"')
    replace_once(source / "src/main.c",
        '    if (region) opt.region = !strcmp(region, "pal") ? REGION_PAL : REGION_NTSC;\n',
        '    if (region) opt.region = !strcmp(region, "pal") ? REGION_PAL : REGION_NTSC;\n'
        '    int enabled_cheats = 0;\n'
        '    for (int i = 0; i < nes_cheat_count; i++) enabled_cheats += nes_cheats[i] != 0;\n'
        '    fprintf(stderr, "execution_mode=%s region=%s remove_slowdown=%d no_flicker=%d frame_interp=%d enabled_cheats=%d prg_crc32=%08X\\n",\n'
        '            opt.interpret_only ? "interpreter" : "recompiled",\n'
        '            opt.region == REGION_PAL ? "pal" : "ntsc", ui_config.remove_slowdown,\n'
        '            ui_config.no_flicker, ui_config.frame_interp, enabled_cheats, nes_cart_state.prg_crc32);\n')
    replace_once(source / "src/main.c",
        '    fprintf(stderr, "exiting normally\\n");',
        '    fprintf(stderr, "exiting normally, overclock_paused_cycles=%llu\\n",\n'
        '            (unsigned long long)nes_ppu_paused_cycles());')

    replace_once(source / "runtime/include/nesrt/recomp.h",
        'int nes_poll(nes_cpu *c, uint16_t pc); /* 1 if control was redirected */',
        'int nes_poll(nes_cpu *c, uint16_t pc); /* 1 if control was redirected */\n'
        'void nes_probe_pc(nes_cpu *c, uint16_t pc);')
    cpu_source = source / "runtime/src/cpu.c"
    if "static FILE *probe_log;" in cpu_source.read_text(encoding="utf-8"):
        replace_once(cpu_source,
            '"%s,%llu,%llu,%04X,%02X,%02X,%02X,%02X,%02X,%02X,%02X,%02X,%02X,%02X,%02X\\n"',
            '"%s,%llu,%llu,%04X,%02X,%02X,%02X,%02X,%02X,%02X,%02X,%02X,%02X,%02X,%02X,%02X\\n"')
    else:
        replace_once(cpu_source,
            'uint32_t nes_marks;\n',
            '''uint32_t nes_marks;

static FILE *probe_log;

static void probe_row(const char *event, nes_cpu *c, uint16_t pc, uint64_t cycles) {
    if (!probe_log) return;
    int bank = pc >= 0x8000 ? nes_bus_state.prg_bank16[(pc >> 14) & 1] : -1;
    fprintf(probe_log,
            "%s,%llu,%llu,%04X,%02X,%02X,%02X,%02X,%02X,%02X,%02X,%02X,%02X,%02X,%02X,%02X\\n",
            event, (unsigned long long)nes_ppu_frame_count(), (unsigned long long)cycles,
            pc, bank, c->s, c->a, c->x, c->y, nes_get_p(c), RAM[0xE4], RAM[0x96],
            RAM[0x97], RAM[0x95], RAM[0x9A], RAM[0x043F]);
    fflush(probe_log);
}

void nes_probe_pc(nes_cpu *c, uint16_t pc) {
    if (pc == 0xC000 || pc == 0xC114 || pc == 0xC2DE || pc == 0xCAD8 ||
        pc == 0xCADF || pc == 0xCABB || pc == 0x8000 || pc == 0x81E5)
        probe_row("pc", c, pc, c->cycles);
}
''')
    replace_once(source / "runtime/src/cpu.c",
        '    if (c->nmi_pending) {\n        c->nmi_pending = false;\n        interrupt(c, pc, 0xFFFA);',
        '    if (c->nmi_pending) {\n'
        '        c->nmi_pending = false;\n'
        '        probe_row("nmi_accept", c, pc, c->cycles);\n'
        '        interrupt(c, pc, 0xFFFA);')
    replace_once(source / "runtime/src/cpu.c",
        'static int interp_step(nes_cpu *c) {\n',
        'static int interp_step(nes_cpu *c) {\n    nes_probe_pc(c, c->pc);\n')
    replace_once(source / "runtime/src/cpu.c",
        '    opts = *opt;\n    nes_cart_state.region = opt->region;',
        '    opts = *opt;\n'
        '    const char *probe_path = getenv("DWD_NMI_LOG");\n'
        '    if (probe_path) {\n'
        '        probe_log = fopen(probe_path, "w");\n'
        '        if (!probe_log) { fprintf(stderr, "cannot open DWD_NMI_LOG\\n"); exit(2); }\n'
        '        fprintf(probe_log, "event,frame,cycle,pc,bank,sp,a,x,y,p,e4,slot,cursor,parity,bracket,timer15\\n");\n'
        '    }\n'
        '    nes_cart_state.region = opt->region;')
    replace_once(source / "runtime/src/cpu.c",
        'void nes_shutdown(void) { trace_flush(); }',
        'void nes_shutdown(void) {\n'
        '    trace_flush();\n'
        '    if (probe_log) { fclose(probe_log); probe_log = NULL; }\n'
        '}')

    replace_once(source / "tools/nesrecomp/recompiler.py",
        '            out.append(f"{head} /* {a:04X}: {raw:<8}  {disasm(insn)} */")\n',
        '            out.append(f"{head} /* {a:04X}: {raw:<8}  {disasm(insn)} */")\n'
        '            if (win.bank, a) in {(7, 0xC000), (7, 0xC114), (7, 0xC2DE),\n'
        '                                 (7, 0xCAD8), (7, 0xCADF), (7, 0xCABB),\n'
        '                                 (5, 0x8000), (5, 0x81E5)}:\n'
        '                out.append(f"    nes_probe_pc(c, 0x{a:04X});")\n')

    config = source / "config/darkwing_duck.toml"
    text = config.read_text(encoding="utf-8")
    old = 'sha1 = "8c73e5f724ae8957570b0ea1b411e1ac5061bb24"'
    new = 'sha1 = "c7b81ee2ff158a58582bc1bf508ffb1213e7a53a"'
    if old not in text and new not in text:
        raise RuntimeError("Unexpected ROM identity in config")
    text = text.replace(old, new).replace(
        "# Recompilation config for Darkwing Duck (Europe), MMC1.",
        "# ZERO-72 experimental target: Darkwing Duck (USA), MMC1.")
    out = source / "config/zero72_usa.toml"
    out.write_text(text, encoding="utf-8")


def encode_input(track: bytes) -> str:
    names = ((0x80, "A"), (0x40, "B"), (0x20, "s"), (0x10, "S"),
             (0x08, "U"), (0x04, "D"), (0x02, "L"), (0x01, "R"))
    rows = []
    for frame, value in enumerate(track, 1):
        buttons = "".join(name for bit, name in names if value & bit) or "-"
        rows.append(f"{frame} {buttons}\n")
    return "".join(rows)


def convert_input(source: Path, output: Path) -> None:
    track = source.read_bytes()
    digest = hashlib.sha256(track).hexdigest()
    if digest not in ROUTE_INPUTS or len(track) != ROUTE_INPUTS[digest]:
        raise RuntimeError(f"Unexpected input identity: {len(track)} bytes, SHA256 {digest}")
    if output.exists():
        raise RuntimeError(f"Refuse to overwrite {output}")
    output.parent.mkdir(parents=True, exist_ok=True)
    output.write_text(encode_input(track), encoding="ascii", newline="\n")


def route_summary(data: bytes, frames: int) -> dict:
    """Post-frame RAM progression only; does not claim original-to-C equivalence."""
    if frames <= 0 or len(data) != frames * 2048:
        raise ValueError("Incomplete/extra RAM records")
    world_events, clears, progress, deaths = [], [], [], []
    previous = (None, 0, 0, None)
    pending = None
    union = 0
    for i in range(frames):
        b = i * 2048
        world, count, mask, player = (data[b + n] for n in (0x31, 0x38, 0x69, 0x50))
        poll = i + 1
        if world != previous[0]:
            world_events.append({"poll": poll, "world": world, "completed": count, "mask": mask})
        if (count, mask) != previous[1:3]:
            progress.append({"poll": poll, "world": world, "completed": count, "mask": mask})
        union |= mask & 0x7F
        if world < 7 and player == 0x11 and (world, player) != (previous[0], previous[3]):
            clears.append({"poll": poll, "world": world})
        if world < 7 and player == 7 and previous[3] != 7:
            pending = {"deathPoll": poll, "world": world, "reloadPoll": None}
            deaths.append(pending)
        elif pending and player == 9 and world == pending["world"]:
            pending["reloadPoll"] = poll
            pending = None
        previous = world, count, mask, player
    return {"frames": frames, "worldTransitions": world_events, "progression": progress,
            "stageClearCandidates": clears, "deathReloads": deaths,
            "worldsMarkedComplete": [w for w in range(7) if union & (1 << w)],
            "final": dict(zip(("world", "completed", "mask", "playerState"), previous))}


def busy_lags(data: bytes, stride: int, frames: int) -> dict:
    """Mission polls whose RAM record keeps RNG $E4-$E7 from the previous record.

    Records are pre-input RAM at offset 0 of each stride (2048 C ram-log, 4384 stand buffers).
    busy: $9A=1, the NMI found the pass Running (lag frame); hold: $9A=0, the pass ran without
    the RNG step (doors, transitions). States $00/$09 (loader/reload) are skipped."""
    if stride < 2048 or frames <= 0 or len(data) != frames * stride:
        raise ValueError("Incomplete/extra RAM records")
    busy, hold = [], []
    for i in range(1, frames):
        b, p = i * stride, (i - 1) * stride
        if data[b + 0x31] >= 7 or data[b + 0x50] in (0, 9) or data[b + 0xE4:b + 0xE8] != data[p + 0xE4:p + 0xE8]:
            continue
        (busy if data[b + 0x9A] == 1 else hold).append(i + 1)
    return {"frames": frames, "busy": busy, "hold": hold}


def lag_compare(reference: dict, port: dict, first: int, last: int) -> dict:
    def window(polls: list[int]) -> set[int]:
        return {p for p in polls if first <= p <= last}
    ref, act = window(reference["busy"]), window(port["busy"])
    return {"range": [first, last], "referenceBusy": len(ref), "portBusy": len(act),
            "missed": sorted(ref - act), "extra": sorted(act - ref)}


def run_route(source: Path, output: Path, binary: Path) -> None:
    source, output, binary = source.resolve(), output.resolve(), binary.resolve()
    if not output.is_relative_to(ROOT / "Art/Temp") or output.exists():
        raise ValueError("Route output must be a NEW folder in assigned Art/Temp")
    if digest(binary) != ROUTE_BINARY_SHA256:
        raise ValueError("Unexpected prepared USA binary")
    track = source.read_bytes()
    sha = hashlib.sha256(track).hexdigest()
    if ROUTE_INPUTS.get(sha) != len(track):
        raise ValueError("Unknown input identity")
    output.mkdir(parents=True)
    converted = output / "input-recomp.txt"
    convert_input(source, converted)
    command = [str(binary), "--headless", "--frames", str(len(track)), "--region", "ntsc",
               "--no-overclock", "--input", str(converted), "--ram-log", str(output / "ram.bin"),
               "--screenshot", str(output / "final.bmp"), str(ROOT / "Art/Darkwing Duck (USA).nes")]
    env = dict(os.environ)
    env.pop("DWD_NMI_LOG", None)  # route test does not collect instruction/timing probes
    started = time.monotonic()
    with (output / "stdout.txt").open("wb") as stdout, (output / "stderr.txt").open("wb") as stderr:
        result = subprocess.run(command, cwd=ROOT, env=env, stdout=stdout, stderr=stderr)
    log = (output / "stderr.txt").read_text(encoding="utf-8")
    mode_ok = all(word in log for word in ("execution_mode=recompiled", "region=ntsc",
                  "remove_slowdown=0", "enabled_cheats=0", "overclock_paused_cycles=0"))
    report = {"schema": 1, "scope": "external USA C route, not Unity/full equivalence",
              "sourceRevision": subprocess.check_output(["git", "rev-parse", "HEAD"], cwd=ROOT).decode().strip(),
              "toolSha256": digest(Path(__file__)),
              "binarySha256": digest(binary), "romSha256": ROM_SHA256,
              "input": {"path": str(source.relative_to(ROOT)), "frames": len(track), "sha256": sha},
              "command": command, "elapsedSeconds": round(time.monotonic() - started, 3),
              "exitCode": result.returncode, "modeVerified": mode_ok, "stderr": log, "outputs": {}}
    for name in ("input-recomp.txt", "ram.bin", "final.bmp", "stdout.txt", "stderr.txt"):
        p = output / name
        if p.exists():
            report["outputs"][name] = {"bytes": p.stat().st_size, "sha256": digest(p)}
    try:
        report["route"] = route_summary((output / "ram.bin").read_bytes(), len(track))
        report["execution"] = "PASS" if result.returncode == 0 and mode_ok and (output / "final.bmp").exists() else "FAIL"
    except (OSError, ValueError) as error:
        report.update(execution="FAIL", error=str(error))
    (output / "result.json").write_text(json.dumps(report, ensure_ascii=False, indent=2) + "\n",
                                       encoding="utf-8", newline="\n")
    print(json.dumps({"execution": report["execution"], "session": source.parent.name,
                      "elapsedSeconds": report["elapsedSeconds"], "route": report.get("route"),
                      "report": str(output / "result.json")}, ensure_ascii=False))
    if report["execution"] != "PASS":
        raise SystemExit(1)


def make_lua(output: Path, oam_output: str, first: int, last: int) -> None:
    output.mkdir(parents=True, exist_ok=True)
    root_line = "local root = " + json.dumps(ROOT.as_posix() + "/")
    for name in ("frame_timing.lua", "oam_timing.lua"):
        source = ROOT / "Art/Tools/DarkwingResearch" / name
        text = source.read_text(encoding="utf-8")
        if text.count(OLD_ROOT) != 1:
            raise RuntimeError(f"Unexpected root in {source}")
        text = text.replace(OLD_ROOT, root_line)
        if name == "oam_timing.lua":
            prelude = (f"OAM_SESSION = 'Art/Playthroughs/20261022T164819Z'\n"
                       f"OAM_OUTPUT = {oam_output!r}\n"
                       f"OAM_FIRST = {first}\nOAM_LAST = {last}\n"
                       "OAM_CAPTURE_STATES = true\n")
            text = prelude + text
        (output / name).write_text(text, encoding="utf-8")


def rows(path: Path) -> list[dict[str, str]]:
    with path.open(newline="", encoding="utf-8") as stream:
        return list(csv.DictReader(stream))


def one(items: list[dict[str, str]], **wanted: str) -> dict[str, str]:
    found = [row for row in items if all(row[key] == value for key, value in wanted.items())]
    if len(found) != 1:
        raise RuntimeError(f"Expected one row for {wanted}, got {len(found)}")
    return found[0]


def digest(path: Path) -> str:
    return hashlib.sha256(path.read_bytes()).hexdigest()


def compare(original_phases: Path, repeat_phases: Path, original_oam: Path,
            repeat_oam: Path, recompiled: Path, interpreter: Path, output: Path) -> None:
    original = rows(original_phases)
    candidate_rows = rows(recompiled.with_name(recompiled.name + "-nmi.csv"))
    interpreter_rows = rows(interpreter.with_name(interpreter.name + "-nmi.csv"))
    original_nmi = one(original, poll="17234", pc="C000")
    original_rti = one(original, poll="17234", pc="C114")
    original_rng = one(original, poll="17234", pc="C2DE")
    original_rng_end = one(original, poll="17234", pc="CADF")
    candidate_nmi = one(candidate_rows, event="nmi_accept", frame="17234")
    candidate_entry = one(candidate_rows, event="pc", frame="17234", pc="C000")
    candidate_rti = one(candidate_rows, event="pc", frame="17234", pc="C114")
    candidate_rng = one(candidate_rows, event="pc", frame="17234", pc="C2DE")
    candidate_rng_end = one(candidate_rows, event="pc", frame="17234", pc="CADF")
    candidate_engine = [row for row in candidate_rows if int(row["cycle"]) < int(candidate_nmi["cycle"])
                        and row["event"] == "pc" and row["pc"] == "8000" and row["bank"] == "05"][-1]
    interpreter_nmi = one(interpreter_rows, event="nmi_accept", frame="17234")

    snapshot = (original_oam / "oam_nmi.bin").read_bytes()
    if len(snapshot) != 2048:
        raise RuntimeError(f"Expected one original NMI RAM snapshot, got {len(snapshot)} bytes")
    original_fields = {"e4": "E4", "slot": "96", "cursor": "97", "parity": "95",
                       "bracket": "9A", "timer15": "043F"}
    original_context = {name: f"{snapshot[int(address, 16)]:02X}"
                        for name, address in original_fields.items()}
    candidate_context = {name: candidate_nmi[name] for name in original_fields}
    ram_path = recompiled.with_name(recompiled.name + "-ram.bin")
    ram = ram_path.read_bytes()
    if len(ram) != 17236 * 2048:
        raise RuntimeError(f"Expected 17236 recompiled RAM records, got {len(ram) // 2048}")
    record = ram[(17234 - 1) * 2048:17234 * 2048]
    candidate_record = {name: f"{record[int(address, 16)]:02X}"
                        for name, address in original_fields.items()}
    original_engine = int(one(rows(original_oam / "oam.csv"), poll="17234")["start"]) + 6
    original_nmi_cycle = int(original_nmi["cycle"]) - 7
    original_rng_cycle = int(original_rng["cycle"])
    candidate_engine_cycle = int(candidate_engine["cycle"])
    candidate_nmi_cycle = int(candidate_nmi["cycle"])
    candidate_rng_cycle = int(candidate_rng["cycle"])
    repeat_files = ("oam.csv", "oam.bin", "oam_end.bin", "oam_nmi.bin", "oam_nmi.csv")
    checks = {
        "independent_original_phase_repeat": digest(original_phases) == digest(repeat_phases),
        "independent_original_oam_repeat": all(digest(original_oam / name) == digest(repeat_oam / name)
                                               for name in repeat_files),
        "busy_nmi_same_frame": original_nmi["returnPc"] != "FE9A" and candidate_nmi["pc"].startswith("8"),
        "return_pc_exact": original_nmi["returnPc"] == candidate_nmi["pc"],
        "partial_fields_exact": original_context == candidate_context,
        "ram_record_17234_exact": original_context == candidate_record,
        "entry_context_exact": all(original_nmi[source] == candidate_entry[target] for source, target in
                                   (("x", "x"), ("y", "y"), ("sp", "sp"), ("a", "a"),
                                    ("e4", "e4"), ("bracket", "bracket"))),
        "absolute_nmi_cycle_exact": original_nmi_cycle == candidate_nmi_cycle,
        "relative_engine_to_nmi_exact": (original_nmi_cycle - original_engine ==
                                         candidate_nmi_cycle - candidate_engine_cycle),
        "relative_engine_to_rng_exact": (original_rng_cycle - original_engine ==
                                         candidate_rng_cycle - candidate_engine_cycle),
        "rng_after_rti": (int(original_nmi["cycle"]) < int(original_rti["cycle"]) < original_rng_cycle
                          and candidate_nmi_cycle < int(candidate_entry["cycle"]) <
                          int(candidate_rti["cycle"]) < candidate_rng_cycle),
        "rng_result_exact": original_rng_end["e4"] == candidate_rng_end["e4"],
        "static_interpreter_ram_exact": digest(ram_path) == digest(interpreter.with_name(interpreter.name + "-ram.bin")),
        "static_interpreter_nmi_exact": all(candidate_nmi[key] == interpreter_nmi[key]
                                            for key in ("frame", "cycle", "pc", "bank", "sp", "slot", "cursor", "parity")),
    }
    negative = dict(candidate_context)
    negative["slot"] = "00" if candidate_context["slot"] != "00" else "01"
    negative_control = negative != original_context
    result = {
        "result": "PASS" if all(checks.values()) else "FAIL",
        "scope": "ZERO-72 USA/NTSC input20261022T164819Z poll17234; no frame shift",
        "checks": checks,
        "negativeControl": "PASS" if negative_control and original_context != candidate_context else
                           "PASS" if negative_control else "NOT APPLICABLE",
        "original": {"returnPc": original_nmi["returnPc"], "nmiCycle": original_nmi_cycle,
                     "engineCycle": original_engine, "rtiCycle": int(original_rti["cycle"]),
                     "rngCycle": original_rng_cycle, "fields": original_context,
                     "rngAfter": original_rng_end["e4"]},
        "recompiled": {"returnPc": candidate_nmi["pc"], "nmiCycle": candidate_nmi_cycle,
                       "engineCycle": candidate_engine_cycle, "rtiCycle": int(candidate_rti["cycle"]),
                       "rngCycle": candidate_rng_cycle, "fields": candidate_context,
                       "ramRecord17234": candidate_record, "rngAfter": candidate_rng_end["e4"]},
        "sha256": {str(path): digest(path) for path in
                   (original_phases, repeat_phases, original_oam / "oam_nmi.bin",
                    recompiled.with_name(recompiled.name + "-nmi.csv"), ram_path,
                    interpreter.with_name(interpreter.name + "-ram.bin"))},
    }
    if output.exists():
        raise RuntimeError(f"Refuse to overwrite {output}")
    output.write_text(json.dumps(result, indent=2) + "\n", encoding="utf-8")
    print(json.dumps(result, indent=2))
    if result["result"] != "PASS":
        raise SystemExit(1)


def selftest() -> None:
    for value in range(256):
        row = encode_input(bytes([value])).strip()
        frame, buttons = row.split(" ", 1)
        assert frame == "1"
        decoded = 0
        for bit, name in ((0x80, "A"), (0x40, "B"), (0x20, "s"), (0x10, "S"),
                          (0x08, "U"), (0x04, "D"), (0x02, "L"), (0x01, "R")):
            if buttons != "-" and name in buttons:
                decoded |= bit
        assert decoded == value
    track = bytes(range(256)) * 2
    rows = encode_input(track).splitlines()
    assert len(rows) == 512 and rows[0] == "1 -" and rows[-1].startswith("512 ")
    data = bytearray(6 * 2048)
    for i, (world, count, mask, player) in enumerate(((0, 0, 0, 7), (0, 0, 0, 9),
             (0, 0, 0, 0x11), (8, 1, 0, 0), (8, 1, 1, 0), (1, 1, 1, 0))):
        for address, value in zip((0x31, 0x38, 0x69, 0x50), (world, count, mask, player)):
            data[i * 2048 + address] = value
    summary = route_summary(data, 6)
    assert summary["worldsMarkedComplete"] == [0] and summary["final"]["world"] == 1
    assert summary["deathReloads"] == [{"deathPoll": 1, "world": 0, "reloadPoll": 2}]
    changing_world = bytearray(3 * 2048)
    for i, world in enumerate((1, 0, 1)):
        changing_world[i * 2048 + 0x31] = world
        changing_world[i * 2048 + 0x50] = 7
    assert len(route_summary(changing_world, 3)["deathReloads"]) == 1, "map cursor movement is not a new state-07 entry"
    for malformed in (data[:-1], data + bytes(2048)):
        try:
            route_summary(malformed, 6)
        except ValueError:
            pass
        else:
            raise AssertionError("incomplete/extra route accepted")
    for stride in (2048, 4384):
        lag = bytearray(6 * stride)
        for i, (rng, busy, state) in enumerate(((1, 0, 1), (1, 1, 1), (2, 0, 1), (2, 0, 1), (2, 1, 9), (3, 0, 1))):
            lag[i * stride + 0xE4], lag[i * stride + 0x9A], lag[i * stride + 0x50] = rng, busy, state
        found = busy_lags(bytes(lag), stride, 6)
        assert found["busy"] == [2] and found["hold"] == [4], "busy/hold or loader-state skip mismatch"
        moved = bytearray(lag)
        moved[1 * stride + 0xE7] = 9
        assert busy_lags(bytes(moved), stride, 6)["busy"] == [], "an RNG step in any byte is not a lag"
        try:
            busy_lags(bytes(lag[:-1]), stride, 6)
        except ValueError:
            pass
        else:
            raise AssertionError("incomplete lag records accepted")
    cmp_result = lag_compare({"busy": [2, 5, 9]}, {"busy": [2, 6]}, 1, 8)
    assert cmp_result["missed"] == [5] and cmp_result["extra"] == [6]
    print(json.dumps({"result": "PASS", "converterControls": 258, "routeControls": 5, "lagControls": 7}))


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    sub = parser.add_subparsers(dest="command", required=True)
    prep = sub.add_parser("prepare")
    prep.add_argument("source", type=Path)
    conv = sub.add_parser("input")
    conv.add_argument("source", type=Path)
    conv.add_argument("output", type=Path)
    route = sub.add_parser("route", help="One complete recorded route on the prepared USA C binary")
    route.add_argument("source", type=Path)
    route.add_argument("output", type=Path)
    route.add_argument("--binary", type=Path, default=ROOT / "Art/Temp/ZERO-72/darkwing-duck.exe")
    lua = sub.add_parser("lua")
    lua.add_argument("output", type=Path)
    lua.add_argument("--oam-output", required=True)
    lua.add_argument("--first", type=int, default=17220)
    lua.add_argument("--last", type=int, default=17236)
    cmp_parser = sub.add_parser("compare")
    cmp_parser.add_argument("--original-phases", type=Path, required=True)
    cmp_parser.add_argument("--repeat-phases", type=Path, required=True)
    cmp_parser.add_argument("--original-oam", type=Path, required=True)
    cmp_parser.add_argument("--repeat-oam", type=Path, required=True)
    cmp_parser.add_argument("--recompiled", type=Path, required=True)
    cmp_parser.add_argument("--interpreter", type=Path, required=True)
    cmp_parser.add_argument("--out", type=Path, required=True)
    lags = sub.add_parser("lags", help="Busy-NMI lag polls of a RAM log; optional reference comparison")
    lags.add_argument("records", type=Path)
    lags.add_argument("--stride", type=int, choices=(2048, 4384), required=True)
    lags.add_argument("--frames", type=int, required=True)
    lags.add_argument("--reference", type=Path)
    lags.add_argument("--reference-stride", type=int, choices=(2048, 4384))
    lags.add_argument("--range", type=int, nargs=2, metavar=("FIRST", "LAST"))
    sub.add_parser("selftest")
    args = parser.parse_args()
    if args.command == "lags":
        found = busy_lags(args.records.read_bytes()[:args.frames * args.stride], args.stride, args.frames)
        if args.reference is not None:
            if args.reference_stride is None or args.range is None:
                raise SystemExit("--reference needs --reference-stride and --range")
            ref_data = args.reference.read_bytes()[:args.frames * args.reference_stride]
            reference = busy_lags(ref_data, args.reference_stride, args.frames)
            found = {"port": found, "reference": reference, "compare": lag_compare(reference, found, *args.range)}
        print(json.dumps(found))
        return
    rom = ROOT / "Art/Darkwing Duck (USA).nes"
    if hashlib.sha256(rom.read_bytes()).hexdigest() != ROM_SHA256:
        raise RuntimeError("Unexpected USA ROM identity")
    if args.command == "prepare":
        prepare(args.source)
    elif args.command == "input":
        convert_input(args.source, args.output)
    elif args.command == "route":
        run_route(args.source, args.output, args.binary)
    elif args.command == "lua":
        make_lua(args.output, args.oam_output, args.first, args.last)
    elif args.command == "compare":
        compare(args.original_phases, args.repeat_phases, args.original_oam, args.repeat_oam,
                args.recompiled, args.interpreter, args.out)
    else:
        selftest()


if __name__ == "__main__":
    main()
