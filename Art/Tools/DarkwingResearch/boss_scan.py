#!/usr/bin/env python3
"""Scan every original-ROM capture for frames that contain an active boss slot.

Signature read straight from the port's dispatch rule (EnemyDispatcher.cs:110..115 =
bank 6 $8000 -> bank 1 $8802): a slot is a boss when $04D0 bit7 is set and the
behavior byte $04E0 is >= $47. Nothing here uses the port's output; it only reads
the ROM captures, so the result says which boss fights already have golden evidence.

Usage: python boss_scan.py [reference-dir ...]
"""
import os
import sys
from collections import Counter

REF = os.path.join(os.path.dirname(os.path.abspath(__file__)),
                   '..', '..', 'Research', 'DarkwingDuck', 'reference')
FRAME = 2048

# The seven mission bosses' CORE behavior ids, read off the bank-1 $8802 dispatch table
# as ported in EnemyBehaviors.cs:3580..3617 (Bushroot $49, Moliarty $4A, Quackerjack $50,
# Wolfduck controller $53 / core $54, Liquidator $57, Megavolt $5A, Steelbeak $62).
# Other ids >= $47 in that table are doors, spawners and grabbers, not bosses.
BOSS = {0x49, 0x4A, 0x50, 0x53, 0x54, 0x57, 0x5A, 0x62}
NAMES = {0x49: 'Bushroot', 0x4A: 'Moliarty', 0x50: 'Quackerjack',
         0x53: 'WolfduckCtrl', 0x54: 'Wolfduck', 0x57: 'Liquidator',
         0x5A: 'Megavolt', 0x62: 'Steelbeak'}
# Frames 0..3 of every capture hold power-on RAM (COVERAGE: determinism row), so they
# are not evidence about any object.
SKIP = 4


def scan(path):
    name = os.path.join(path, 'ram_frames.bin')
    if not os.path.exists(name):
        return None
    with open(name, 'rb') as fh:
        ram = fh.read()
    n = len(ram) // FRAME
    hits = []
    kinds = Counter()
    for f in range(SKIP, n):
        g = f * FRAME
        for s in range(1, 16):
            if not (ram[g + 0x4D0 + s] & 0x80):
                continue
            b = ram[g + 0x4E0 + s]
            if b not in BOSS:
                continue
            hits.append((f, s, b, ram[g + 0x420 + s],
                         ram[g + 0x500 + s] << 8 | ram[g + 0x4F0 + s],
                         ram[g + 0x520 + s] << 8 | ram[g + 0x510 + s],
                         ram[g + 0x31], ram[g + 0x50], ram[g + 0x62],
                         ram[g + 0x500] << 8 | ram[g + 0x4F0],
                         ram[g + 0x520] << 8 | ram[g + 0x510]))
            kinds[b] += 1
    return n, hits, kinds


def main():
    dirs = sys.argv[1:]
    if not dirs:
        dirs = sorted(d for d in os.listdir(REF)
                      if os.path.exists(os.path.join(REF, d, 'ram_frames.bin')))
    covered = 0
    for d in dirs:
        r = scan(os.path.join(REF, d))
        if r is None:
            continue
        n, hits, kinds = r
        if not hits:
            continue
        covered += 1
        first, last = hits[0][0], hits[-1][0]
        print(f'{d:26s} frames={n:5d}  BOSS f{first}..f{last}  samples={len(hits)}'
              f'  kinds=' + ', '.join(f'${k:02X} {NAMES[k]} x{c}' for k, c in kinds.items()))
        for f, s, b, anim, x, y, z31, z50, z62, px, py in hits[:4]:
            print(f'    f{f:5d} slot{s:02d} ${NAMES[b]:12s} beh=${b:02X} anim=${anim:02X}'
                  f' X=${x:04X} Y=${y:04X} $31=${z31:02X} $50=${z50:02X} $62=${z62:02X}'
                  f' player X=${px:04X} Y=${py:04X}')
    print(f'\ncaptures scanned: {len(dirs)}   with a boss core slot: {covered}')


if __name__ == '__main__':
    main()
