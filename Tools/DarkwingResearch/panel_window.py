"""Compare nametable + palette shadow across a $31 story-panel window.

Usage:
  python Tools/DarkwingResearch/panel_window.py <capture_dir> <lo> <hi> <bank_file> <table_addr_hex>

Checks, for the RAM window [lo, hi] of a panel:
  (a) whether the $2000 nametable (960 B) is constant across the vram_frames.bin
      records inside the window, and how it differs from the records outside it;
  (b) whether the $0310/$0330 palette-shadow buffers equal the ROM byte table
      read from <bank_file> (a 16 KiB $8000-$BFFF image) at <table_addr>.
"""
import os
import struct
import sys

cap, lo, hi = sys.argv[1], int(sys.argv[2]), int(sys.argv[3])
bank_file, table = sys.argv[4], int(sys.argv[5], 16)

ram = open(os.path.join(cap, "ram_frames.bin"), "rb").read()
frames = len(ram) // 2048
vb = open(os.path.join(cap, "vram_frames.bin"), "rb").read()
RE = 4 + 0x1000
recs = [(struct.unpack_from("<I", vb, i * RE)[0], vb[i * RE + 4:i * RE + 0x1004])
        for i in range(len(vb) // RE)]

print(f"frames={frames} vram_records={len(recs)}")
groups = {}
for fn, bytes_ in recs:
    key = bytes_[0:0x3C0]
    groups.setdefault(key, []).append(fn)
print(f"distinct $2000 nametables (960 B) over whole capture: {len(groups)}")
for key, fns in sorted(groups.items(), key=lambda kv: kv[1][0]):
    hist = {}
    for b in key:
        hist[b] = hist.get(b, 0) + 1
    top = sorted(hist.items(), key=lambda kv: -kv[1])[:6]
    tag = "inside" if any(lo <= f <= hi for f in fns) else "outside"
    print(f"  [{tag}] f{fns[0]}..f{fns[-1]} ({len(fns)} recs) first32=" +
          " ".join(f"{b:02X}" for b in key[:12]) + "  top tiles=" +
          ", ".join(f"{b:02X}x{n}" for b, n in top))

pal = open(os.path.join(cap, "palette_frames.bin"), "rb").read()


def p(f, i):
    return pal[f * 32 + i]


b = open(bank_file, "rb").read()
off = table - 0x8000
tab = b[off:off + 16]
print(f"\ntable {os.path.basename(bank_file)}@${table:04X} = " + " ".join(f"{x:02X}" for x in tab))
for f in (lo - 10, lo, lo + 30, hi - 30, hi):
    if not 0 <= f < frames:
        continue
    a = [ram[f * 2048 + 0x310 + i] for i in range(16)]
    c = [ram[f * 2048 + 0x330 + i] for i in range(16)]
    p0 = [p(f, i) for i in range(1, 17)]
    print(f"  f{f}: $0310=" + " ".join(f"{x:02X}" for x in a) +
          " eq=" + str(a == list(tab)) +
          " | $0330 eq=$0310:" + str(c == a) +
          " | $3F01..10 eq:" + str(p0 == list(tab)) +
          f" | $18={ram[f * 2048 + 0x18]:02X} $31={ram[f * 2048 + 0x31]:02X}")

bad = [f for f in range(lo, hi + 1)
       if [ram[f * 2048 + 0x310 + i] for i in range(16)] != list(tab)]
print(f"frames in [lo,hi] where $0310 != table: {len(bad)}" +
      (f" first={bad[:8]}" if bad else ""))
