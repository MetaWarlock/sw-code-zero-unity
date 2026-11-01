"""ZERO-72: prepare and feed a pinned darkwing-duck-recomp NTSC probe."""
from __future__ import annotations

import argparse
import csv
import hashlib
import json
from pathlib import Path

ROOT = next(p for p in Path(__file__).resolve().parents if (p / "ProjectSettings").is_dir())
ROM_SHA256 = "1271dce8678ffc261e2c05cc785ae53e29ac1f9ecf6265a4265bed825abb0a00"
INPUT_SHA256 = "137190299728c9ea6285ebf057fa25b1efaf916b9aeb2ad6d21fa46efb7f6275"
EXPECTED_POLLS = 38886
ROUTE_INPUTS = {
    INPUT_SHA256: EXPECTED_POLLS,
    "ee97b1e9a30d3746b85d1cb573edb913ec6fe0e81f21e9a4148d82d6c08c73f6": 77688,
}
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
    output.write_text(encode_input(track), encoding="ascii")


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
    print(json.dumps({"result": "PASS", "controls": 258}))


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    sub = parser.add_subparsers(dest="command", required=True)
    prep = sub.add_parser("prepare")
    prep.add_argument("source", type=Path)
    conv = sub.add_parser("input")
    conv.add_argument("source", type=Path)
    conv.add_argument("output", type=Path)
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
    sub.add_parser("selftest")
    args = parser.parse_args()
    rom = ROOT / "Art/Darkwing Duck (USA).nes"
    if hashlib.sha256(rom.read_bytes()).hexdigest() != ROM_SHA256:
        raise RuntimeError("Unexpected USA ROM identity")
    if args.command == "prepare":
        prepare(args.source)
    elif args.command == "input":
        convert_input(args.source, args.output)
    elif args.command == "lua":
        make_lua(args.output, args.oam_output, args.first, args.last)
    elif args.command == "compare":
        compare(args.original_phases, args.repeat_phases, args.original_oam, args.repeat_oam,
                args.recompiled, args.interpreter, args.out)
    else:
        selftest()


if __name__ == "__main__":
    main()
