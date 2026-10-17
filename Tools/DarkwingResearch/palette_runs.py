"""Where does the RAM palette shadow equal a ROM table, frame by frame?

Usage:
  python Tools/DarkwingResearch/palette_runs.py <capture_dir> <lo> <hi> <bank_file> <table_addr_hex>

The story panels copy 16 bytes of a bank-2 table into $0310 AND $0330 and set $18=$FF,
so the equality run marks the frames where the panel body (not the $DD57 loader) owns
the palette. Prints contiguous equal-runs plus $04F1/$0421/$31 at both boundaries.
"""
import os
import sys

cap, lo, hi = sys.argv[1], int(sys.argv[2]), int(sys.argv[3])
bank_file, table = sys.argv[4], int(sys.argv[5], 16)
ram = open(os.path.join(cap, "ram_frames.bin"), "rb").read()
frames = len(ram) // 2048
tab = list(open(bank_file, "rb").read()[table - 0x8000:table - 0x8000 + 16])


def r(f, a):
    return ram[f * 2048 + a]


def eq(f):
    return [r(f, 0x310 + i) for i in range(16)] == tab


runs, cur, st = [], None, lo
for f in range(lo, min(hi, frames - 1) + 1):
    v = eq(f)
    if v != cur:
        if cur is not None:
            runs.append((cur, st, f - 1))
        cur, st = v, f
runs.append((cur, st, min(hi, frames - 1)))
print(f"table ${table:04X} = " + " ".join(f"{x:02X}" for x in tab))
for v, a, b in runs:
    if not v:
        continue
    print(f"  $0310==${table:04X}-table f{a}..f{b} ({b - a + 1})"
          f"  start: $31={r(a,0x31):02X} $18={r(a,0x18):02X} $97={r(a,0x97):02X} "
          f"$0421={r(a,0x421):02X} $04F1={r(a,0x4F1):02X} $04F2={r(a,0x4F2):02X} $0511={r(a,0x511):02X}")
    for edge in (a, b):
        if 0 < edge < frames - 1:
            print(f"      boundary f{edge}: $31={r(edge-1,0x31):02X}->{r(edge,0x31):02X} "
                  f"$04F1={r(edge-1,0x4F1):02X}->{r(edge,0x4F1):02X} "
                  f"$14={r(edge,0x14):02X} $18={r(edge,0x18):02X}")
