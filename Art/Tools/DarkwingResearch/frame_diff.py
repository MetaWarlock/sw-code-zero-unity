#!/usr/bin/env python3
"""List every RAM byte that changes between two golden frames (ram_frames.bin, 2048 B/frame)."""
import sys

path, a, b = sys.argv[1], int(sys.argv[2]), int(sys.argv[3])
with open(path, "rb") as fh:
    data = fh.read()
fr = len(data) // 2048
print(f"frames={fr}")
for lo, hi in ((a, b), (b, b + 1)):
    if hi >= fr:
        break
    ra, rb = data[lo * 2048:(lo + 1) * 2048], data[hi * 2048:(hi + 1) * 2048]
    diffs = [(i, ra[i], rb[i]) for i in range(2048) if ra[i] != rb[i]]
    zp = [f"${x:02X}:{a:02X}>{b:02X}" for x, a, b in diffs if x < 0x100]
    ot = [f"${x:04X}:{a:02X}>{b:02X}" for x, a, b in diffs if x >= 0x100]
    print(f"--- f{lo}->f{hi}: {len(diffs)} bytes")
    print("  ZP: " + " ".join(zp))
    print("  OT: " + " ".join(ot))
