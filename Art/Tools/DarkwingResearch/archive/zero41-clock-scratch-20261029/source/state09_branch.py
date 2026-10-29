"""ZERO-41 scratch: classify oracle state-09 CAFC prices by the original pre-pass RAM.
Static model (bank 7): CAFC/CB41 dispatch 49 + D517..D523 17 + JSR 6 + F5A4 (39, 56 on $0510 row wrap)
+ JMP 3 + F5DA (38 while $0560 stays >= F9) = 152.
usage: state09_branch.py CITY_PHASES_CSV ORIGINAL_BUFFERS FIRST LAST"""
import sys
from collections import defaultdict

first, last = int(sys.argv[3]), int(sys.argv[4])
pts = defaultdict(dict)
with open(sys.argv[1]) as f:
    next(f)
    for line in f:
        p, c, pc = line.rstrip().split(',')
        p = int(p)
        if first <= p <= last:
            pts[p].setdefault(pc, int(c))
buf = open(sys.argv[2], 'rb')
agree = disagree = 0
for p in range(first, last + 1):
    m = pts[p]
    if 'CA40' not in m or 'CA59' not in m:
        continue
    buf.seek((p - 1) * 4384)
    ram = buf.read(2048)
    if ram[0x50] != 0x09:
        continue
    oracle = m['CA59'] - m['CA40'] - 18
    vel, frac, ylow, yvf, landed = ram[0x560], ram[0x460], ram[0x510], ram[0x550], ram[0x4D0] & 0x0F
    cheap = landed == 0 and vel >= 0xFA
    model = None
    if cheap:
        borrow = 1 if frac < yvf else 0
        newy = (ylow - vel - borrow) & 0xFF
        wrap = newy >= 0xF0
        newvel = (vel - (1 if yvf < ram[0x99] else 0)) & 0xFF
        clamp = newvel < 0xF9
        model = 152 + (17 if wrap else 0) if not clamp else None
    ok = (model == oracle) if cheap else None
    if cheap:
        agree += ok; disagree += not ok
    print(p, 'vel=%02X 04D0=%X' % (vel, landed), 'cheap' if cheap else 'probe', 'oracle', oracle, 'model', model)
print('cheap-branch static model agree', agree, 'disagree', disagree)
