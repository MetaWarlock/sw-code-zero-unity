"""Why does each $31 story-panel segment end where it does?

Usage:
  python Art/Tools/DarkwingResearch/intro_exit_gates.py <capture_dir> <lo:hi> [<lo:hi> ...]

For each [lo,hi] window (a $31 segment), prints the frame-by-frame trajectory of the
slot-1/slot-2 X/Y/velocity fields plus the generic counters around the exit frame, and
which 16-byte ROM table $0310 actually matches in the pre-body gap. All values are read
straight from ram_frames.bin; nothing is inferred from names.
"""
import os
import sys

FIELDS = [("31", 0x31), ("14", 0x14), ("18", 0x18), ("97", 0x97), ("62", 0x62),
          ("51", 0x51), ("53", 0x53), ("5D", 0x5D), ("66", 0x66), ("6B", 0x6B),
          ("F8", 0xF8), ("421", 0x421), ("4D1", 0x4D1), ("4E1", 0x4E1),
          ("4F1", 0x4F1), ("4F2", 0x4F2), ("511", 0x511), ("512", 0x512),
          ("531", 0x531), ("541", 0x541), ("551", 0x551), ("561", 0x561)]

cap = sys.argv[1]
ram = open(os.path.join(cap, "ram_frames.bin"), "rb").read()
frames = len(ram) // 2048
TABLES = {}
for b in range(7):
    path = f"Art/Unity/CodeZero/Darkwing/Data/prg_bank{b}.bytes"
    if os.path.exists(path):
        TABLES[f"bank{b}"] = open(path, "rb").read()


def r(f, a):
    return ram[f * 2048 + a]


def head(f, tag):
    print(f"  {tag} f{f}: " + " ".join(f"${n}={r(f, int(n, 16)):02X}" for n, _ in FIELDS))


def match(f):
    """Which bank2 byte table does $0310 equal at frame f, if any?"""
    cur = [r(f, 0x310 + i) for i in range(16)]
    out = []
    for name, img in TABLES.items():
        for off in range(0, len(img) - 16, 2):
            if img[off:off + 16] == bytes(cur):
                out.append(f"${name}@{off + 0x8000:04X}")
    return ",".join(out) if out else "no-table"


for win in sys.argv[2:]:
    lo, hi = (int(x, 16) if False else int(x) for x in win.split(":"))
    print(f"\n=== window f{lo}..f{hi}  $31=${r(lo,0x31):02X} ===")
    for f in (lo, hi):
        head(f, "edge")
        print(f"      $0310 == {match(f)}")
    print("  exit trajectory (frames hi-4 .. hi+4):")
    for f in range(max(lo - 1, hi - 4), min(frames - 1, hi + 4) + 1):
        print(f"    f{f}: $31={r(f,0x31):02X} $04F1={r(f,0x4F1):02X} $04F2={r(f,0x4F2):02X}"
              f" $0511={r(f,0x511):02X} $0512={r(f,0x512):02X} $0531={r(f,0x531):02X}"
              f" $0541={r(f,0x541):02X} $0551={r(f,0x551):02X} $0561={r(f,0x561):02X}"
              f" $51={r(f,0x51):02X} $53={r(f,0x53):02X} $14={r(f,0x14):02X}"
              f" $04D1={r(f,0x4D1):02X} $18={r(f,0x18):02X}")
    print("  pre-body gap: first frame each of the window where $0310 matches a table")
    seen = None
    for f in range(lo, hi + 1):
        m = match(f)
        if m != seen:
            print(f"    f{f}: {m}")
            seen = m
