import os, sys

def p(d, *p):
    return os.path.join(os.path.dirname(os.path.abspath(__file__)), '..', '..', '..',
                        'Research', 'DarkwingDuck', 'reference', d, *p)

d = sys.argv[1]
lo = int(sys.argv[2]) if len(sys.argv) > 2 else 0
hi = int(sys.argv[3]) if len(sys.argv) > 3 else 1500
step = int(sys.argv[4]) if len(sys.argv) > 4 else 10
data = open(p(d, 'ram_frames.bin'), 'rb').read()
n = len(data) // 2048
def g(f, a):
    return data[f * 2048 + a]
def w(f, lo_a, hi_a):
    return (g(f, hi_a) << 8) | g(f, lo_a)

print('f     EE ED  50  pl.X  pl.Y  |  s14 beh anim   X    Y   |  s15 beh anim   X    Y   | live slots')
for f in range(lo, min(hi, n), step):
    s = []
    for x in (14, 15):
        s.append((g(f, 0x04E0 + x), g(f, 0x0420 + x), w(f, 0x04F0 + x, 0x0500 + x),
                  w(f, 0x0510 + x, 0x0520 + x)))
    live = [x for x in range(1, 16) if g(f, 0x04D0 + x) & 0x80]
    print('%5d %02X %02X %02X %5s %5s | %02X %02X %5s %5s | %02X %02X %5s %5s | %s' % (
        f, g(f, 0xEE), g(f, 0xED), g(f, 0x50),
        '%04X' % w(f, 0x04F0, 0x0500), '%04X' % w(f, 0x0510, 0x0520),
        s[0][0], s[0][1], '%04X' % s[0][2], '%04X' % s[0][3],
        s[1][0], s[1][1], '%04X' % s[1][2], '%04X' % s[1][3],
        ','.join(str(x) for x in live)))

# hit frames
print('\nhit frames (EE increments):')
prev = 0
for f in range(n):
    e = g(f, 0xEE)
    if e != prev:
        print('  f%5d EE %02X->%02X  player %04X/%04X state %02X' % (
            f, prev, e, w(f, 0x04F0, 0x0500), w(f, 0x0510, 0x0520), g(f, 0x50)))
        prev = e
print('\nbehavior 4F / slot 14-15 offsets history:')
for x in (14, 15):
    seen = {}
    for f in range(n):
        k = (g(f, 0x04E0 + x), g(f, 0x0400 + x))
        seen.setdefault(k, []).append(f)
    for k, fs in sorted(seen.items(), key=lambda kv: kv[1][0]):
        print('  slot%d beh=%02X off=%02X frames %d..%d (%d)' % (x, k[0], k[1], fs[0], fs[-1], len(fs)))
