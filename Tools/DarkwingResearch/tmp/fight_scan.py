import os, sys

def path(d, *p):
    return os.path.join(os.path.dirname(os.path.abspath(__file__)), '..', '..', '..',
                        'Research', 'DarkwingDuck', 'reference', d, *p)

d = sys.argv[1] if len(sys.argv) > 1 else 'tmp_boss_pilot7_wolfduck_fight'
fp = path(d, 'ram_frames.bin')
data = open(fp, 'rb').read()
n = len(data) // 2048
print('dir', d, 'frames', n, 'bytes', len(data))

def g(f, a):
    return data[f * 2048 + a]

BOSS = {0x49: 'Bushroot', 0x4A: 'Moliarty', 0x50: 'QuackCore', 0x51: 'QuackBall',
        0x53: 'WolfCtrl', 0x54: 'Wolfduck', 0x57: 'Liquidator', 0x5A: 'Megavolt',
        0x62: 'Steelbeak', 0x4F: 'BOSSDEFEAT', 0x52: 'QuackChild'}

# 1) $EE / $ED / $3B / $50 / $31 history
prev = None
print('\n-- zero page: EE/ED always; 3B/50 only on meaningful transitions --')
for f in range(n):
    cur = tuple(g(f, a) for a in (0xEE, 0xED, 0x3B, 0x50, 0x31, 0x6D, 0x5B))
    if cur == prev:
        continue
    show = False
    if prev is None:
        show = True
    else:
        if cur[0] != prev[0] or cur[1] != prev[1]:
            show = True                      # EE / ED damage+invuln
        if cur[0] or cur[1]:
            show = True
        if (cur[2] == 0) != (prev[2] == 0):
            show = True                      # 3B timer start/stop
        if cur[3] != prev[3] or cur[6] != prev[6]:
            show = True                      # player state / door byte
    if show:
        print('f%5d EE=%02X ED=%02X 3B=%02X 50=%02X 31=%02X 6D=%02X 5B=%02X' % ((f,) + cur))
    prev = cur
nz = [f for f in range(n) if g(f, 0x3B)]
print('3B nonzero window: %s' % ('f%d..f%d (%d frames)' % (nz[0], nz[-1], len(nz)) if nz else 'never'))

# 2) per-slot boss behavior windows
print('\n-- boss-ish slot windows --')
for x in range(16):
    beh = [g(f, 0x04E0 + x) for f in range(n)]
    act = [g(f, 0x04D0 + x) & 0x80 for f in range(n)]
    kinds = sorted({beh[f] for f in range(n) if act[f] and beh[f] >= 0x40})
    if not kinds:
        continue
    hits = [f for f in range(n) if act[f] and beh[f] in BOSS]
    if not hits:
        continue
    print('slot %2d kinds=%s first=f%d last=f%d count=%d' % (
        x, ','.join('%02X(%s)' % (k, BOSS.get(k, '?')) for k in kinds),
        hits[0], hits[-1], len(hits)))

# 3) track every slot whose behavior ever equals a boss id, print motion samples
print('\n-- tracked slots: anim/X/Y/health --')
tracked = []
for x in range(16):
    for f in range(n):
        if (g(f, 0x04D0 + x) & 0x80) and g(f, 0x04E0 + x) in BOSS:
            tracked.append(x)
            break
print('tracked', tracked)
for x in tracked:
    print('\nslot %d' % x)
    pr = None
    for f in range(n):
        if not (g(f, 0x04D0 + x) & 0x80):
            continue
        row = (g(f, 0x04E0 + x), g(f, 0x0400 + x), g(f, 0x0410 + x), g(f, 0x0420 + x),
               g(f, 0x0430 + x), (g(f, 0x04F0 + x) << 8) | g(f, 0x0500 + x),
               (g(f, 0x0510 + x) << 8) | g(f, 0x0520 + x), g(f, 0x0530 + x), g(f, 0x05A0 + x))
        if row != pr:
            print('  f%5d beh=%02X off=%02X a0410=%02X a0420=%02X a0430=%02X X=%04X Y=%04X h=%02X hp=%02X'
                  % ((f,) + row))
            pr = row

# 4) player position + projectiles (behavior 0x25? unknown) - just player
print('\n-- player slot 0 summary (first 40 changes) --')
pr = None
c = 0
for f in range(n):
    row = (g(f, 0x0400), g(f, 0x0410), g(f, 0x0420), (g(f, 0x04F0) << 8) | g(f, 0x0500),
           (g(f, 0x0510) << 8) | g(f, 0x0520), g(f, 0x05A0))
    if row != pr:
        c += 1
        if c <= 60:
            print('  f%5d off=%02X a=%02X a2=%02X X=%04X Y=%04X v=%02X' % ((f,) + row))
        pr = row
print('  total player state changes', c)

# 5) any frame where $B315 evidence: behavior becomes 4F
print('\n-- behavior 4F (boss defeat pop) occurrences --')
found = False
for x in range(16):
    hits = [f for f in range(n) if (g(f, 0x04D0 + x) & 0x80) and g(f, 0x04E0 + x) == 0x4F]
    if hits:
        found = True
        print('slot %d f%d..f%d n=%d' % (x, hits[0], hits[-1], len(hits)))
if not found:
    print('none')
