"""ZERO-41 scratch: oracle CAFC player price per poll vs native city CSV.
usage: state09_residual.py CITY_CSV CITY_PHASES_CSV"""
import csv, sys
from collections import Counter, defaultdict

city = {int(r['poll']): r for r in csv.DictReader(open(sys.argv[1]))}
pts = defaultdict(dict)
with open(sys.argv[2]) as f:
    next(f)
    for line in f:
        p, c, pc = line.rstrip().split(',')
        p = int(p)
        if p in city:
            pts[p].setdefault(pc, int(c))

def native_prefix(r, player):
    v = [int(r[k]) for k in ('hook', 'head', 'scroll', 'spawn', 'zone', 'palette', 'bank6', 'bank1', 'tail')]
    hook, head, scroll, spawn, zone, pal, b6, b1, tail = v
    return (6 + hook + 18 + head + 6 + player + 12 + scroll
            + 6 + spawn + 11 + zone + pal + 6 + b6 + 2 + 6 + 55 + b1 + tail)

res = Counter(); ok = bad = 0; why = Counter(); ranges = []
for p in sorted(city):
    r = city[p]
    if not r['head'] or r['player'] != '-1':
        continue
    m = pts[p]
    if not all(k in m for k in ('FECF', 'CA40', 'CA59', 'CABB')):
        why['missing source point'] += 1
        continue
    player = m['CA59'] - m['CA40'] - 18
    res[player] += 1
    try:
        prefix = native_prefix(r, player)
    except ValueError:
        why['other part unsupported'] += 1
        continue
    if prefix == m['CABB'] - m['FECF']:
        ok += 1
    else:
        bad += 1
        if bad <= 5:
            print('prefix mismatch', p, prefix, m['CABB'] - m['FECF'])
    ranges.append(p)
print('unsupported-player polls', len(ranges), 'first', ranges[:1], 'last', ranges[-1:])
print('oracle player price histogram', sorted(res.items()))
print('full prefix exact with oracle player', ok, 'mismatch', bad, dict(why))
