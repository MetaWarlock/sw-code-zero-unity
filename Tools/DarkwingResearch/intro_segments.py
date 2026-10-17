"""Segment a zero-page byte across a probe_run RAM dump and locate a panel body.

Usage:
  python Tools/DarkwingResearch/intro_segments.py <capture_dir> [addr_hex=31] [panel=9A9C]

Prints contiguous segments of RAM[addr], the frames where the pad latch $14 is
non-zero, the nametable-dump frames (vram_frames.bin writes every 60th frame:
u32 frame + 4 KiB NES VRAM), and which 16 KiB switchable PRG bank contains the
literal bytes at the window address (panel arg).
"""
import os
import struct
import sys

cap, addr = sys.argv[1], int(sys.argv[2] if len(sys.argv) > 2 else "31", 16)
panel = int(sys.argv[3] if len(sys.argv) > 3 else "9A9C", 16)
ram = open(os.path.join(cap, "ram_frames.bin"), "rb").read()
frames = len(ram) // 2048


def r(f, a):
    return ram[f * 2048 + a]


print(f"cap={os.path.basename(cap)} frames={frames}")
cur, start = None, 0
for f in range(frames):
    v = r(f, addr)
    if v != cur:
        if cur is not None:
            print(f"  ${addr:02X}={cur:02X} f{start}..f{f - 1} ({f - start})")
        cur, start = v, f
print(f"  ${addr:02X}={cur:02X} f{start}..f{frames - 1} ({frames - start})")

print("pad1 nonzero:", [f for f in range(frames) if r(f, 0x14)], sep=" ")

vpath = os.path.join(cap, "vram_frames.bin")
if os.path.exists(vpath):
    vb = open(vpath, "rb").read()
    n = len(vb) // (4 + 0x1000)
    print(f"vram records={n} rem={len(vb) % (4 + 0x1000)}")
    for i in range(n):
        fn = struct.unpack_from("<I", vb, i * (4 + 0x1000))[0]
        print(f"  vram dump f{fn}", end="")
    print()

bankmap = {2: "prg_bank2.bytes", 1: "prg_bank1.bytes", 5: "prg_bank5.bytes",
           6: "prg_bank6.bytes", 4: "prg_bank4.bytes", 3: "prg_bank3.bytes",
           0: "prg_bank0.bytes"}
lo = panel & 0xFF00
print(f"literal bytes at ${panel:04X} in each $8000-window image:")
for name in bankmap.values():
    p = os.path.join("Assets", "Darkwing", "Data", name)
    if not os.path.exists(p):
        continue
    b = open(p, "rb").read()
    for base in (0x8000, 0xA000):
        off = panel - base
        if 0 <= off < len(b) and len(b) == 0x4000:
            print(f"  {name} @${panel:04X}: " + " ".join(f"{x:02X}" for x in b[off:off + 16]))
