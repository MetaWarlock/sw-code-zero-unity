"""ZERO-45 read-only review: differential check of the native state-09 price formula.

Transcribes PlayerCore.TryMeasureWorldEntryFall09Cycles and the shared helpers it calls
(ExpandedProbeSet/ExpandedPoint/PlayerSnapUp/ExpandedQueueCycles, Core of commit 8f3bbe1)
line by line into Python. Native fields are read from the same original RAM bytes; the
per-point tile class (native _tiles.TypeAt) is taken from bank 7 EBE8 itself, so this checks
the cycle formula, branch structure and refusals, NOT the native tile map. Each case is then
priced independently by state09_source_price.walk and compared.

Cases: the 37 route polls, plus seeded synthetic RAM mutations of route records (fall bytes,
fractions, row position/wrap, camera rows, random $0600 class nibbles, audio queue slot).
Synthetic cases test formula/source agreement only; they are not original behavior.

usage: state09_native_diff.py ROM ORIGINAL_BUFFERS [CASES] [SEED] [--mutant]
Exit 0 only with no MISMATCH, no native price where the source walk is unsupported, and every
listed off-route branch exercised; --mutant must exit 1 (negative control).
"""
import os
import random
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))  # sibling walker under python -I
import state09_source_price as src  # noqa: E402

RECORD = 4384
POINT_X = {6: -11, 7: 0, 8: 11}
POINT_Y = {6: 12, 7: 12, 8: 12}


def branch(address, target, taken):
    return 3 + (((address + 2) & 0xFF00) != (target & 0xFF00)) if taken else 2


class Refused(Exception):
    pass


def rom_class(ram, bank7, point, next_y):
    """Bank-7 EBE8 for one point at the integrated Y: the value native TypeAt must equal."""
    cpu = src.Cpu(ram, bank7)
    cpu.ram[0x510], cpu.ram[0x520] = next_y & 255, next_y >> 8
    cpu.x, cpu.y = 0, point
    cpu.push(0xFF); cpu.push(0xFE)
    pc = 0xEBE8
    while pc != 0xFFFF:
        pc, _ = src.step(cpu, pc)
    return cpu.a


def expanded_point(n, bank7, cx, cy, point):
    ox, oy = POINT_X[point], POINT_Y[point]
    read_x = 4 + ((0x19 + point) > 255)
    read_y = 4 + ((0xAD + point) > 255)
    c = 2 + 3 + read_x + branch(0xEBEF, 0xEBF3, ox >= 0)
    if ox < 0: c += 5
    px = cx + ox
    c += 4 + 2 + read_x + 3 + 4 + 3 + 3 + branch(0xEC03, 0xEC4D, (px & 0x8000) != 0)
    if px & 0x8000: raise Refused('EC03 negative X probe scratch residency')
    c += 30 + 4 + 3 + read_y + branch(0xEC1E, 0xEC36, oy < 0)
    low = (cy & 255) + oy
    row = cy >> 8
    c += 4 + 2 + read_y + branch(0xEC27, 0xEC2D, low > 255)
    if low <= 255: c += 2 + branch(0xEC2B, 0xEC31, low < 240)
    if low >= 240: low = (low + 0x10) & 255; row += 1; c += 2 + 5
    c += 3 + 3
    low &= 255
    py = (row << 8) | low
    cam = (n['FB'] << 8) | n['FA']
    same = ((py - cam) & 0xFF00) == 0  # C# int: negative differences keep high bits set
    c += 3 + 2 + 3 + 3 + 3 + branch(0xEC56, 0xEC6D, same)
    if not same:
        below = py >= cam
        c += branch(0xEC58, 0xEC62, below)
        if not below:
            c += 2 + branch(0xEC5C, 0xEC62, False) + 2 + branch(0xEC60, 0xEC65, point < 6)
            if point < 6: raise Refused('EC65 camera-clamped side probe')
        return c + 2 + 6, row, 0, low
    cls = rom_class(n['ram'], bank7, point, cy)
    c += 71
    high = (low & 0x10) != 0
    c += branch(0xEC9B, 0xECA0, high)
    c += 3 + 8 + 6 if high else 3 + 6
    return c, row, cls, low


def expanded_probe_set(n, bank7, cx, cy, first, allowed):
    c = 3 + 2 + branch(0xEB56, 0xEB60, False) + 4 + 2 + 5 + 2 + 3
    maximum = last_y = 0
    for k in range(3):
        pc, row, cls, last_y = expanded_point(n, bank7, cx, cy, first + k)
        if cls > allowed: raise Refused('EB52 class outside verified slice')
        lower = cls < maximum
        c += 3 + 6 + pc + 3 + 5 + 3 + branch(0xEB70, 0xEB9A, lower)
        if not lower:
            maximum = cls
            c += 3 + 2 + branch(0xEB76, 0xEB80, False) + 3 + 3 + 3 + 3
            c += 3 + 2 + 3 + 2 + 3 + 3 + 3 + 2 + 3
            diff = row - n['FB']
            c += branch(0xEB8F, 0xEB9A, diff < 0)
            if diff >= 0:
                c += branch(0xEB91, 0xEB9A, diff == 0)
                if diff != 0: c += 3 + 2 + 3 + 3
        c += 2 + branch(0xEB9C, 0xEBA4, True) + 5 + 5 + branch(0xEBA8, 0xEB64, k != 2)
    c += (2 + branch(0xEBAC, 0xEBBD, False) + 3 + 2 + branch(0xEBB2, 0xEBC8, False)
          + 2 + branch(0xEBB6, 0xEBE3, False) + 2 + branch(0xEBBA, 0xEBDB, False) + 6)
    return c, maximum, last_y


MUTANT = False  # --mutant: deliberate one-cycle error on the rare EDD2 borrow path


def snap_up(y, nibble):
    borrow = (y & 255) - nibble < 0
    return 3 + 2 + 3 + 4 + 2 + 3 + 5 + (2 + 2 + 5 + 4 + 2 + 5 + MUTANT if borrow else 3) + 6


def queue(accepts):
    c = 3 + 3 + 3 + 4 + 2 + branch(0xF85A, 0xF866, not accepts)
    if accepts: c += 3 + 4 + 2 + 2 + 2 + 3
    return c + 3 + 6


def native(ram, bank7):
    """TryMeasureWorldEntryFall09Cycles; native fields mapped from their documented RAM bytes."""
    n = {'ram': ram, 'FA': ram[0xFA], 'FB': ram[0xFB]}
    if ram[0x50] != 0x09: raise Refused('D517 state outside 09 slice')
    if not ram[0x4D0] & 0x80: raise Refused('CAFC inactive player')
    if ram[0x4D0] & 0x0F: raise Refused('D53F state09 intro-card phase')
    if ram[0x400] & 0x20: raise Refused('EB52 pre-hook flag')
    vel, vfrac, yfrac, grav = ram[0x560], ram[0x550], ram[0x460], ram[0x99]
    x = ram[0x500] << 8 | ram[0x4F0]
    y = ram[0x520] << 8 | ram[0x510]
    d = 4 + branch(0xCAFF, 0xCB41, True)
    d += 2 + 2 + 5 + 2 + 5 + 3 + 2 + 2 + 4 + 3 + 4 + 3 + 5
    plain = vel >= 0xFA
    d += 4 + 2 + 2 + 4 + 2 + branch(0xD523, 0xD539, plain)
    fraction = yfrac - vfrac
    row, low = (y >> 8) & 255, (y & 255) - vel - (1 if fraction < 0 else 0)
    new_low = low & 255
    wrap = new_low >= 0xF0
    integ = 4 + 2 + 4 + 5 + 4 + 4 + 5 + 2 + branch(0xF5B9, 0xF5C8, not wrap)
    if wrap:
        integ += 2 + 5 + 4 + 2 + 5
        if new_low + 0x10 > 255: row = (row + 1) & 255
        new_low = (new_low + 0x10) & 255
    integ += 6
    next_y = row << 8 | new_low
    velocity = ((vel << 8 | vfrac) - grav) & 65535
    rising = (velocity & 0x8000) == 0
    grv = 4 + 2 + 3 + 5 + 4 + 2 + 5 + branch(0xF5EB, 0xF5FB, rising)
    fall = (velocity >> 8) & 255
    if not rising:
        grv += 2 + branch(0xF5EF, 0xF5FB, fall >= 0xF9)
        if fall < 0xF9: grv += 2 + 5 + 2 + 5; fall = 0xF8
    grv += 6
    if plain: return d + 6 + integ + 3 + grv
    d += 2 + 2 + 6 + 3 + 3
    d += 6 + integ + 6 + grv
    probe, hit, last_y = expanded_probe_set(n, bank7, x, next_y, 6, 3)
    d += 6 + probe + 3 + 2 + branch(0xF4E8, 0xF4FB, hit == 0)
    lands = False
    if hit:
        d += branch(0xF4EA, 0xF4F3, hit >= 2)
        if hit == 1:
            d += 4 + 2 + branch(0xF4F1, 0xF4FB, fall >= 0xFC)
            lands = fall < 0xFC
        else:
            lands = True
    if lands:
        d += 6 + snap_up(next_y, last_y & 15)
        d += 6 + 2 + 5 + 2 + 5 + 6
        d += 2 + 6
    else:
        d += 2 + 6
    d += 3 + branch(0xD52E, 0xD538, hit == 0)
    if hit:
        d += 2 + 6 + queue(ram[0xDC + ram[0xDA]] == 0x88) + 6
    return d + 6


def classify(ram, bank7, tally, examples, label):
    try:
        nat = native(ram, bank7)
    except Refused as e:
        nat = 'R:' + str(e)
    cpu = None
    try:
        cpu = src.walk(ram, bank7)
        walked, cls = cpu.cyc, cpu.ram[0x10]
    except src.Unsupported as e:
        walked, cls = 'U:' + str(e), None
    if isinstance(nat, int) and isinstance(walked, int):
        key = 'EXACT' if nat == walked else 'MISMATCH'
    elif isinstance(nat, int):
        key = 'NATIVE_PRICED_SOURCE_UNSUPPORTED'
    elif isinstance(walked, int):
        key = 'REFUSED ' + nat[2:]
    else:
        key = 'BOTH_UNSUPPORTED'
    tally[key] = tally.get(key, 0) + 1
    if key in ('MISMATCH', 'NATIVE_PRICED_SOURCE_UNSUPPORTED') and len(examples) < 10:
        examples.append((label, key, nat, walked, cls))
    if key == 'EXACT':
        pcs = [pc for pc, _ in cpu.trace]
        seen = set(pcs)
        for name, hit in BRANCHES.items():
            if hit(seen, pcs):
                covered[name] = covered.get(name, 0) + 1
    return key, cls


# Off-route source paths whose agreement the synthetic cases must actually exercise.
BRANCHES = {
    'F5BB F5A4 row wrap': lambda s, p: 0xF5BB in s,
    'F5F1 F5DA clamp': lambda s, p: 0xF5F1 in s,
    'F5EB BPL rising result': lambda s, p: 0xF5EB in s and 0xF5ED not in s,
    'EC2D probe row wrap': lambda s, p: 0xEC2D in s,
    'EC62 off-camera probe': lambda s, p: 0xEC62 in s,
    'EB93 row below camera row (+$2E)': lambda s, p: 0xEB93 in s,
    'EB9A via EB70 lower class': lambda s, p: any(a == 0xEB70 and b == 0xEB9A for a, b in zip(p, p[1:])),
    'F4EC class 1': lambda s, p: 0xF4EC in s,
    'F4EC class 1, $0560>=FC no landing': lambda s, p: 0xF4EC in s and 0xF4F3 not in s,
    'F4F3 landing': lambda s, p: 0xF4F3 in s,
    'EDE3 EDD2 borrow': lambda s, p: 0xEDE3 in s,
    'F866 audio queue busy': lambda s, p: 0xF850 in s and 0xF85C not in s,
    'F85C audio enqueued': lambda s, p: 0xF85C in s,
    'D539 plain branch': lambda s, p: 0xD539 in s,
}
covered = {}


def main(argv):
    global MUTANT
    if '--mutant' in argv:
        MUTANT = True
        argv = [a for a in argv if a != '--mutant']
    rom = open(argv[1], 'rb').read()
    bank7 = rom[16 + 16384 * (rom[4] - 1):16 + 16384 * rom[4]]
    cases = int(argv[3]) if len(argv) > 3 else 20000
    rng = random.Random(int(argv[4]) if len(argv) > 4 else 45)
    records = {}
    with open(argv[2], 'rb') as f:
        for p in range(1941, 1978):
            f.seek((p - 1) * RECORD)
            records[p] = bytearray(f.read(2048))
    tally, examples = {}, []
    for p, r in records.items():
        classify(bytes(r), bank7, tally, examples, 'route %d' % p)
    print('route', tally)
    tally, examples, paths = {}, [], {}
    for i in range(cases):
        r = bytearray(records[rng.choice(list(records))])
        r[0x560] = rng.choice([0xFF, 0xFC, 0xFA, 0xF9, 0xF8, 0xF0, 0x80, 0x00, 0x01, 0x05, 0x7F, rng.randrange(256)])
        r[0x550] = rng.randrange(256)
        r[0x460] = rng.randrange(256)
        r[0x510] = rng.choice([rng.randrange(0xF0), rng.randrange(0xE0, 0xF0), rng.randrange(0x10)])
        if rng.random() < 0.3: r[0x520] = (r[0x520] + rng.choice([-1, 1])) & 255
        if rng.random() < 0.3: r[0xFB] = (r[0xFB] + rng.choice([-1, 1, 2])) & 255
        if rng.random() < 0.3: r[0xFA] = rng.randrange(0xF0)
        if rng.random() < 0.8:
            for a in range(0x600, 0x700):
                r[a] = rng.choice([0, 0, 1, 2, 3]) << 4 | rng.choice([0, 0, 1, 2, 3])
        if rng.random() < 0.05:
            r[0x600 + rng.randrange(256)] = 0x55
        if rng.random() < 0.3: r[0xDC + r[0xDA]] = 0x10
        key, cls = classify(bytes(r), bank7, tally, examples, 'synthetic %d' % i)
        if key == 'EXACT': paths[cls] = paths.get(cls, 0) + 1
    print('synthetic', dict(sorted(tally.items())))
    print('synthetic EXACT by final $10', dict(sorted(paths.items(), key=lambda kv: str(kv[0]))))
    for e in examples: print('example', e)
    missing = [name for name in BRANCHES if not covered.get(name)]
    for name in BRANCHES: print('covered %-38s %d' % (name, covered.get(name, 0)))
    ok = 'MISMATCH' not in tally and 'NATIVE_PRICED_SOURCE_UNSUPPORTED' not in tally and not missing
    print('differential', 'PASS' if ok else 'FAIL', 'uncovered', missing)
    return 0 if ok else 1


if __name__ == '__main__':
    sys.exit(main(sys.argv))
