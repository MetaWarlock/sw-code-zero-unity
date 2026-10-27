"""Diagnostic cost deltas, not game acceptance. Nonzero deltas stay visible.

--expect-json compares the complete saved diagnostic summary; without it exit 0
only means a complete table was analyzed, not equivalence to the ROM.
"""
import argparse, csv, collections, pickle, json
from pathlib import Path
DATA = Path(__file__).resolve().parents[3] / 'Research/DarkwingDuck/reference/cost-harness/data'
parser = argparse.ArgumentParser(description=__doc__)
parser.add_argument('coverage', type=Path)
parser.add_argument('limit', nargs='?', type=int, default=6387)
parser.add_argument('--first', type=int, default=1924)
parser.add_argument('--last', type=int, default=6400)
parser.add_argument('--reference', type=Path, default=DATA / 'city_phase.pkl')
parser.add_argument('--json', type=Path)
parser.add_argument('--expect-json', type=Path)
args = parser.parse_args()
with args.reference.open('rb') as source:
    idle, ph = pickle.load(source)
with args.coverage.open(newline='') as source:
    reader = csv.DictReader(source)
    required = {'poll','pub0','pub1','pubWhy','audio','head','bank1','tail','bank1Why','tailWhy'}
    required.update(('player','scroll','spawn','zone','palette','bank6'))
    if not required <= set(reader.fieldnames or []):
        parser.error('Missing required cost columns')
    rows = list(reader)
lim = args.limit
polls = [int(r['poll']) for r in rows]
if not polls or polls != list(range(args.first, args.last + 1)) or not args.first <= lim <= args.last:
    parser.error('Missing, duplicated, reordered, extra leading or truncated poll data')
def g(p, k, i=0):
    v = ph.get(p, {}).get(k); return v[i] if v and len(v) > i else None
def I(x):
    try: return int(x)
    except: return None
diff = collections.defaultdict(collections.Counter); why = collections.defaultdict(collections.Counter); firstbad = {}
pairs = [('player','CA40','CA59'),('scroll','CA59','CA6B'),('spawn','CA6B','CA79'),('zone','CA79','CA7C'),('palette','CA7C','CA84'),('bank6','CA84','CA8C')]
for r in rows:
    p = int(r['poll'])
    if p > lim: break
    c000 = g(p,'C000'); a1 = g(p,'C0A1')
    if c000 and a1:
        src = a1-c000; n0, n1 = I(r['pub0']), I(r['pub1'])
        k = 'ok' if src in (n0, n1) else ('unsup' if n0 in (None, -1) else 'bad')
        diff['pub'][k] += 1
        if k != 'ok' and 'pub' not in firstbad: firstbad['pub'] = (p, n0, n1, src, r['pubWhy'])
    dc = g(p,'C0DC'); e = g(p,'C114')
    if dc and e:
        src = e+6-dc; n = I(r['audio']); k = 'ok' if n == src else ('unsup' if n in (None, -1) else 'bad')
        diff['audio'][k] += 1
        if k == 'bad': diff['audio_d'][src-n] += 1
    if r['head'] == '' or not g(p,'CAE2'): continue
    for name, a, b in pairs:
        n = I(r[name]); s = (g(p,b)-g(p,a)) if g(p,a) and g(p,b) else None
        if n is None or n < 0:
            diff[name]['unsup'] += 1; why[name][r.get(name+'Why', '')] += 1; continue
        diff[name][s-n if s is not None else 'nosrc'] += 1
        if s is not None and name not in firstbad and s-n not in (0,): pass
    n1, t = I(r['bank1']), I(r['tail'])
    s = (g(p,'CABB')-g(p,'CA8C')) if g(p,'CABB') and g(p,'CA8C') else 0
    if n1 is None or n1 < 0 or t is None or t < 0:
        diff['bank1tail']['unsup'] += 1; why['bank1tail'][r['bank1Why']+'|'+r['tailWhy']] += 1
    else: diff['bank1tail'][s-n1-t] += 1
for k, v in diff.items(): print(k, dict(v.most_common(12)))
for k, v in why.items(): print('WHY', k, dict(v.most_common(8)))
print(firstbad)
summary = {'first': args.first, 'last': args.last, 'limit': lim,
           'diff': {k: {str(x): n for x,n in v.items()} for k,v in diff.items()},
           'why': {k: dict(v) for k,v in why.items()}, 'firstbad': firstbad}
summary = json.loads(json.dumps(summary))
if args.json:
    args.json.parent.mkdir(parents=True, exist_ok=True)
    with args.json.open('x', encoding='utf-8') as target:
        json.dump(summary, target, indent=2, sort_keys=True)
if args.expect_json:
    expected = json.loads(args.expect_json.read_text(encoding='utf-8'))
    if summary != expected:
        raise SystemExit('FAIL: diagnostic summary differs from preserved baseline')
    print('PASS: diagnostic summary equals preserved baseline (not full game equivalence)')
