#!/usr/bin/env python3
"""Throwaway: dump zero-page + object-slot traces from a probe_run capture.

ram_frames.bin layout is 2048 bytes per frame = NES RAM $0000-$07FF, written at
every frame end (see runtime_capture.lua.in). Frame index in the file is 1-based
relative to capture start; this script uses 0-based rows for convenience.

Usage: python dump_boss.py CAPTURE_DIR [--start N] [--limit N] [--zp 22,23,...]
"""
import argparse
import os
import sys

FRAME = 2048
BOSS = {0x49: 'Bushroot', 0x4A: 'Moliarty', 0x50: 'Quackerjack', 0x53: 'WolfduckCtrl',
        0x54: 'Wolfduck', 0x57: 'Liquidator', 0x5A: 'Megavolt', 0x62: 'Steelbeak'}
# behaviour ids >= $47 (bank-1 boss dispatch) are all "boss-family"
CORE = dict(BOSS)


def main():
    p = argparse.ArgumentParser(description=__doc__)
    p.add_argument('cap')
    p.add_argument('--start', type=int, default=4)
    p.add_argument('--every', type=int, default=10)
    p.add_argument('--zp', default='22,23,25,2e,31,36,38,3b,3d,50,5b,6d,66,9c,ee,ed,e4')
    p.add_argument('--boss', action='store_true', help='only list frames with a boss-family slot')
    p.add_argument('--all-ge47', action='store_true', help='count any behavior >= $47, not just the 7 cores')
    args = p.parse_args()
    path = args.cap if os.path.isabs(args.cap) else os.path.join(
        os.path.dirname(os.path.abspath(__file__)), '..', '..', '..',
        'Research', 'DarkwingDuck', 'reference', args.cap, 'ram_frames.bin')
    if os.path.isdir(path):
        path = os.path.join(path, 'ram_frames.bin')
    ram = open(path, 'rb').read()
    n = len(ram) // FRAME
    zp = [int(x, 16) for x in args.zp.split(',')]
    watched = CORE if not args.all_ge47 else None
    rows = []
    for f in range(args.start, n):
        g = f * FRAME
        for s in range(1, 16):
            if not (ram[g + 0x4D0 + s] & 0x80):
                continue
            b = ram[g + 0x4E0 + s]
            if watched is not None:
                if b not in watched:
                    continue
            elif b < 0x47:
                continue
            rows.append((f, s, b))
    print(f'{path}\nframes={n} boss-frames={len(rows)}')
    if rows:
        # collapse to per-slot spans
        from collections import defaultdict
        spans = defaultdict(lambda: [None, None, set()])
        for f, s, b in rows:
            e = spans[(s, b)]
            if e[0] is None:
                e[0] = f
            e[1] = f
            e[2].add(f)
        for (s, b), (first, last, _) in sorted(spans.items()):
            print(f'  slot{s:02d} beh=${b:02X} {CORE.get(b, "boss-family")} first=f{first} last=f{last} frames={len(set(x[0] for x in rows if x[1] == s and x[2] == b))}')
        if args.boss:
            return
    # general trace
    print('  f  ' + ' '.join(f'${a:02X}' for a in zp) + '   | slots 1-15 (beh/active)')
    for f in range(args.start, n, args.every):
        g = f * FRAME
        vals = ' '.join(f'{ram[g + a]:02X}' for a in zp)
        slots = ' '.join(f'{s}:{ram[g + 0x4E0 + s]:02X}' for s in range(1, 16)
                         if ram[g + 0x4D0 + s] & 0x80)
        print(f'{f:4d} {vals}   | {slots}')


if __name__ == '__main__':
    main()
