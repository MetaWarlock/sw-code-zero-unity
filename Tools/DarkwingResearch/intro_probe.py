import csv, struct

D = "Research/DarkwingDuck/reference/gold_briefing_b0/"
ram = open(D + "ram_frames.bin", "rb").read()
frames = len(ram) // 2048

def r(f, a):
    return ram[f * 2048 + a]

# $2000/$2001 writes after the dialog starts
ctrl = {}
with open(D + "ppu_writes.csv") as fh:
    for row in csv.DictReader(fh):
        f = int(row["relative_frame"]); a = int(row["address"], 16)
        if a in (0x2000, 0x2001) and f >= 300:
            ctrl.setdefault((f, a), int(row["value"], 16))
print("$2000/$2001 writes f>=300:",
      ", ".join(f"f{f}:{('C' if a == 0x2000 else 'M')}{v:02X}" for (f, a), v in sorted(ctrl.items()))[:600])

# PC at the frame boundary, to see which loop owns the frames
pc = {}
with open(D + "regs.csv") as fh:
    rd = csv.DictReader(fh)
    cols = rd.fieldnames
    for row in rd:
        pc[int(row["relative_frame"] if "relative_frame" in row else row["frame"])] = row
print("regs columns:", cols)

print("\n f  $62 $0E $10 $92  $31 $F8   $0510 $0511  pc@frame")
for f in range(330, frames, 10):
    row = pc.get(f) or pc.get(f + 1) or {}
    print(f"{f:4d}  {r(f,0x62):02X}  {r(f,0x0E):02X}  {r(f,0x10):02X}  {r(f,0x92):02X}"
          f"   {r(f,0x31):02X}  {r(f,0xF8):02X}   {r(f,0x510):02X}   {r(f,0x511):02X}"
          f"   {row.get('pc', '?')}")

print("\n$0510/$0511 word (little endian) every 10 frames:")
for f in range(400, min(frames, 600), 10):
    lo, hi = r(f, 0x0510), r(f, 0x0511)
    print(f"  f{f}: ${hi:02X}{lo:02X}", end="")
print()
