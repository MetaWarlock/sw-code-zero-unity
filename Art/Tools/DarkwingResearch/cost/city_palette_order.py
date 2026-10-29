"""ZERO-23 canonical profile of the captured palette-shadow snapshots.

Reads an existing fixed-record capture (4384 bytes: 2048 RAM + 2048 NT + 256 OAM + 32 palette,
1-based frames, the layout Art/Tools/DarkwingResearch/stand/stand.py exports) and reports what the
capture shows, for a frame window, about the palette shadow $0300 and the palette $3F00.

SCOPE AND LIMITS (read before quoting a number).
This compares byte snapshots taken at the emulator's input poll. It does NOT observe CPU writes and
does NOT observe a $C059 call: a "*Change*" metric means "the captured bytes differ from the
previous captured frame", nothing more. A store that leaves the 32 shadow bytes unchanged, an upload
that repeats the already displayed bytes, and the $3F10/$3F14/$3F18/$3F1C mirrors landing on
$3F00/$3F04/$3F08/$3F0C are all invisible or contradictory to a plain change/equality test, so
change counts are lower bounds and the equality test is reported plain and alias-aware side by side.
Where the $C059 upload actually executes is NOT MEASURED by this tool and needs an
instruction-level trace, not this capture.

--side is mandatory because the two sides export different fields: the port's RAM column is a
sparse, explicitly-mapped export, so $0300..$03FF and $18 are normally $00 there. Those zeros mean
"field not exported", NOT "shadow of value zero": a port run reports the shadow and flag metrics as
NOT MEASURED and uses only the palette column, while --side original refuses such a capture.

Nothing in the capture, ROM, input or golden set is written. Both outputs (the CSV and its sibling
summary) are checked before anything is created, so a refused run leaves no orphan file.

Usage:
  python -I Art/Tools/DarkwingResearch/cost/city_palette_order.py --side original \
      "Art/Playthroughs/20261019T212535Z/replay1/buffers.bin" 9 29360 <new.csv>
  python -I Art/Tools/DarkwingResearch/cost/city_palette_order.py --selftest
"""
import argparse
import csv
import hashlib
import json
import os
import sys
import tempfile
from pathlib import Path

STRIDE = 4384
RAM, PAL = 0, 4352
SHADOW, SOURCE, FLAG = 0x300, 0x320, 0x18
# $2007 writes to $3F10/$3F14/$3F18/$3F1C land on $3F00/$3F04/$3F08/$3F0C, so a captured entry
# 0/4/8/12 may hold shadow[i] or shadow[i+16].
ALIAS_PAIRS = ((0, 16), (4, 20), (8, 24), (12, 28))
MAX_LAG = 40
NOT_MEASURED = 'NOT MEASURED (this side does not export the field)'

ROOT = next(p for p in Path(__file__).resolve().parents if (p / 'ProjectSettings').is_dir())


class Refuse(Exception):
    """Input or output guard: the run stops before anything is written."""


class Capture:
    """Fixed-record capture over a bytes buffer; frames are 1-based and inclusive."""

    def __init__(self, raw, path):
        if len(raw) % STRIDE:
            raise Refuse(f'{path}: size {len(raw)} is not a whole number of {STRIDE}-byte records')
        self.raw = raw
        self.path = path
        self.records = len(raw) // STRIDE
        if self.records < 2:
            raise Refuse(f'{path}: capture holds {self.records} frame(s), at least 2 are required')

    def _record(self, f):
        return self.raw[(f - 1) * STRIDE:f * STRIDE]

    def in_range(self, f):
        return 1 <= f <= self.records

    def shadow(self, f):
        r = self._record(f)
        return bytes(r[RAM + SHADOW:RAM + SHADOW + 32])

    def source(self, f):
        r = self._record(f)
        return bytes(r[RAM + SOURCE:RAM + SOURCE + 32])

    def palette(self, f):
        r = self._record(f)
        return bytes(r[PAL:PAL + 32])

    def flag(self, f):
        return self._record(f)[RAM + FLAG]


def alias_equal(pal, shadow):
    """Palette vs shadow, allowing the $3F10/$14/$18/$1C mirror to own entries 0/4/8/12."""
    return (all(pal[i] == shadow[i] or pal[i] == shadow[m] for i, m in ALIAS_PAIRS)
            and all(pal[i] == shadow[i] for i in range(32) if i % 4))


def analyse(cap, lo, hi, side):
    """Observable byte-snapshot comparison over [lo, hi]. No CPU or instruction claims."""
    if lo < 2:
        raise Refuse('the first comparable frame is 2 (frame 1 has no predecessor)')
    if hi <= lo:
        raise Refuse(f'window must satisfy hi > lo, got lo={lo} hi={hi}')
    if hi > cap.records:
        raise Refuse(f'capture holds {cap.records} records, window asks for hi={hi}')

    shadow = [cap.shadow(f) for f in range(1, hi + 1)]
    pal = [cap.palette(f) for f in range(1, hi + 1)]
    shadow_change = {f for f in range(lo, hi + 1) if shadow[f - 1] != shadow[f - 2]}
    pal_change = {f for f in range(lo, hi + 1) if pal[f - 1] != pal[f - 2]}
    known_successor = {f for f in shadow_change if cap.in_range(f + 1)}

    armed = sorted(f for f in shadow_change if cap.flag(f) != 0)
    result = {
        'side': side,
        'capture': cap.path.replace(os.sep, '/'),
        'sha256': hashlib.sha256(cap.raw).hexdigest(),
        'records': cap.records,
        'stride': STRIDE,
        'window': [lo, hi],
        'observable': {
            'shadowContentChanges': len(shadow_change),
            'palSnapshotChanges': len(pal_change),
            'shadowChangeAndPalChangeSameFrame': len(shadow_change & pal_change),
            'shadowChangeThenPalChangeNextFrame': len({f for f in known_successor if f + 1 in pal_change}),
            'shadowChangeWithNoPalChangeAtOrNextFrame': sorted(
                f for f in known_successor if f not in pal_change and f + 1 not in pal_change),
            'palChangeWithoutShadowChangeAtOrPreviousFrame': sorted(
                f for f in pal_change if f not in shadow_change and f - 1 not in shadow_change),
            'shadowChangeFramesWithFlagNonZero': len(armed),
            'shadowChangeFramesWithUnknownSuccessor': sorted(f for f in shadow_change if f not in known_successor),
        },
        'shadowColumnNonZeroFrames': sum(1 for f in range(lo, hi + 1) if any(shadow[f - 1])),
        'notClaimed': [
            'a CPU write to $0300: the test sees captured bytes, not stores',
            'a $C059 call or its instruction address: needs an instruction-level trace',
            'an upload that repeats the already displayed bytes: no snapshot change to see',
            'a store that leaves the 32 compared shadow bytes unchanged',
            '$3F10/$3F14/$3F18/$3F1C mirrors: plain equality can fail where alias-aware equality holds',
        ],
    }

    if side == 'port':
        for key in list(result['observable']):
            if key != 'palSnapshotChanges':
                result['observable'][key] = NOT_MEASURED
        result['observable']['palChangeFrames'] = sorted(pal_change)
        result['observedEquality'] = NOT_MEASURED
        result['minAliasAwareLagDistribution'] = NOT_MEASURED
        return result

    plain_prev = plain_this = alias_prev = alias_this = only_fresh = unmatched = 0
    lags = {}
    for f in range(lo, hi + 1):
        p, s_prev, s_this = pal[f - 1], shadow[f - 2], shadow[f - 1]
        plain_prev += p == s_prev
        plain_this += p == s_this
        a_prev, a_this = alias_equal(p, s_prev), alias_equal(p, s_this)
        alias_prev += a_prev
        alias_this += a_this
        only_fresh += a_this and not a_prev and s_this != s_prev
        lag = next((k for k in range(1, MAX_LAG) if f - k >= 1 and alias_equal(p, shadow[f - k - 1])), None)
        if lag is None:
            unmatched += 1
        else:
            lags[lag] = lags.get(lag, 0) + 1
    result['observedEquality'] = {
        'framesCompared': hi - lo + 1,
        'palEqualsShadowPrevFrame_plain': plain_prev,
        'palEqualsShadowThisFrame_plain': plain_this,
        'palEqualsShadowPrevFrame_aliasAware': alias_prev,
        'palEqualsShadowThisFrame_aliasAware': alias_this,
        'palMatchesOnlyTheFreshShadow_aliasAware': only_fresh,
        'palWithoutAnyAliasAwareMatchWithinMaxLag': unmatched,
        'maxLagFrames': MAX_LAG,
    }
    result['minAliasAwareLagDistribution'] = lags
    return result


def run(argv=None):
    parser = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    parser.add_argument('capture', nargs='?', type=str)
    parser.add_argument('lo', nargs='?', type=int)
    parser.add_argument('hi', nargs='?', type=int)
    parser.add_argument('out', nargs='?', type=str)
    parser.add_argument('--side', choices=('original', 'port'),
                        help='original: RAM $0300/$0320/$18 carry the console state; port: that '
                             'column is a sparse export, so shadow/flag stay NOT MEASURED')
    parser.add_argument('--selftest', action='store_true',
                        help='run the built-in positive/negative controls and exit')
    args = parser.parse_args(argv)

    if args.selftest:
        return selftest()
    if not (args.capture and args.lo is not None and args.hi is not None and args.out):
        parser.error('capture, lo, hi and out are required unless --selftest')
    if not args.side:
        parser.error('--side original|port is required: the two sides export different fields')
    if not os.path.exists(args.capture):
        raise Refuse(f'capture not found: {args.capture}')

    summary_path = os.path.splitext(args.out)[0] + '.summary.json'
    for taken in (args.out, summary_path):
        if os.path.exists(taken):
            raise Refuse(f'refuse to overwrite existing output: {taken}')

    cap = Capture(open(args.capture, 'rb').read(), args.capture)
    result = analyse(cap, args.lo, args.hi, args.side)
    if args.side == 'original' and result['shadowColumnNonZeroFrames'] == 0:
        raise Refuse(f'RAM $0300..$031F is empty across f{args.lo}..f{args.hi}: that is the sparse '
                     f'port export, rerun with --side port instead of reading the zeros as a shadow')
    if args.side == 'port' and result['shadowColumnNonZeroFrames'] > 0:
        result['portShadowColumnAppearsMapped'] = result['shadowColumnNonZeroFrames']

    with open(args.out, 'w', newline='') as handle:
        writer = csv.writer(handle)
        writer.writerow(['frame', 'flag18', 'shadow_content_change', 'pal_snapshot_change',
                         'shadow0300', 'source0320', 'palette3f00'])
        for f in range(args.lo, args.hi + 1):
            writer.writerow([f, f'{cap.flag(f):02X}', cap.shadow(f) != cap.shadow(f - 1),
                             cap.palette(f) != cap.palette(f - 1),
                             cap.shadow(f).hex(), cap.source(f).hex(), cap.palette(f).hex()])
    with open(summary_path, 'w', encoding='utf-8') as handle:
        json.dump(result, handle, indent=1, sort_keys=True)
    print(json.dumps(result, indent=1, sort_keys=True))
    return 0


def build_capture(rows):
    """Synthetic fixed-record capture from (shadow32, flag, pal32) tuples."""
    out = bytearray()
    for shadow, flag, pal in rows:
        ram = bytearray(2048)
        ram[SHADOW:SHADOW + 32] = shadow
        ram[SOURCE:SOURCE + 32] = shadow
        ram[FLAG] = flag
        out += ram + bytes(2048) + bytes(256) + pal
    return bytes(out)


def selftest():
    """Positive/negative controls on synthetic captures plus the real input/output guards."""
    results = []

    def check(name, ok, detail=''):
        results.append((name, bool(ok), detail))

    base = bytes([0x0F] * 32)
    ramp = bytes([0x0F, 0x36, 0x17, 0x06, 0x0F, 0x30, 0x00, 0x11] + [0x0F] * 24)
    armed, off = 0xFF, 0x00

    def write(tmp, name, payload):
        path = os.path.join(tmp, name)
        with open(path, 'wb') as handle:
            handle.write(payload)
        return path

    def summary(tmp, name):
        return json.load(open(os.path.join(tmp, name), encoding='utf-8'))

    with tempfile.TemporaryDirectory(dir=ROOT / 'Art/Temp', prefix='city-palette-order-') as tmp:
        good = write(tmp, 'good.bin', build_capture([(base, armed, base)] * 3))

        try:
            run(['--side', 'original', os.path.join(tmp, 'absent.bin'), '2', '3',
                 os.path.join(tmp, 'never.csv')])
            check('missing capture refused', False)
        except Refuse as exc:
            check('missing capture refused', 'not found' in str(exc), str(exc))

        cut = write(tmp, 'cut.bin', build_capture([(base, armed, base)] * 3)[:-7])
        try:
            run(['--side', 'original', cut, '2', '3', os.path.join(tmp, 'never2.csv')])
            check('truncated capture refused', False)
        except Refuse as exc:
            check('truncated capture refused', 'whole number' in str(exc), str(exc))

        # hi == records: the frame beyond the capture is unknown, never a phantom change.
        edge = write(tmp, 'edge.bin', build_capture([(base, armed, base), (base, armed, base),
                                                     (ramp, armed, base)]))
        run(['--side', 'original', edge, '2', '3', os.path.join(tmp, 'edge.csv')])
        obs = summary(tmp, 'edge.summary.json')['observable']
        check('end boundary marks unknown successor instead of a silent verdict',
              obs['shadowChangeFramesWithUnknownSuccessor'] == [3]
              and obs['shadowChangeWithNoPalChangeAtOrNextFrame'] == []
              and obs['shadowChangeThenPalChangeNextFrame'] == 0
              and obs['shadowChangeAndPalChangeSameFrame'] == 0, str(obs))

        try:
            run(['--side', 'original', good, '2', '3', os.path.join(tmp, 'edge.csv')])
            check('existing CSV refused', False)
        except Refuse as exc:
            check('existing CSV refused', 'overwrite' in str(exc), str(exc))

        orphan = os.path.join(tmp, 'orphan.csv')
        with open(os.path.splitext(orphan)[0] + '.summary.json', 'w') as handle:
            handle.write('{}')
        try:
            run(['--side', 'original', good, '2', '3', orphan])
            check('existing sibling summary refused and no orphan CSV created', False)
        except Refuse as exc:
            check('existing sibling summary refused and no orphan CSV created',
                  'summary.json' in str(exc) and not os.path.exists(orphan), str(exc))

        # Positive: shadow written at f3, palette visible at f4 -> exactly one frame.
        lag = write(tmp, 'lag.bin', build_capture([(base, armed, base), (base, armed, base),
                                                   (ramp, armed, base), (ramp, armed, ramp),
                                                   (ramp, armed, ramp)]))
        run(['--side', 'original', lag, '2', '5', os.path.join(tmp, 'lag.csv')])
        obs = summary(tmp, 'lag.summary.json')['observable']
        check('positive lag-1 control',
              obs['shadowContentChanges'] == 1 and obs['palSnapshotChanges'] == 1
              and obs['shadowChangeAndPalChangeSameFrame'] == 0
              and obs['shadowChangeThenPalChangeNextFrame'] == 1
              and obs['palChangeWithoutShadowChangeAtOrPreviousFrame'] == [], str(obs))

        # Negative: a deliberate relevant PAL shift must be detected as same-frame publication,
        # proving the correlation is measured rather than hard-wired to +1.
        shift = write(tmp, 'shift.bin', build_capture([(base, armed, base), (base, armed, base),
                                                       (ramp, armed, ramp), (ramp, armed, ramp),
                                                       (ramp, armed, ramp)]))
        run(['--side', 'original', shift, '2', '5', os.path.join(tmp, 'shift.csv')])
        s = summary(tmp, 'shift.summary.json')
        check('deliberate PAL shift detected as same-frame publication',
              s['observable']['shadowChangeAndPalChangeSameFrame'] == 1
              and s['observable']['shadowChangeThenPalChangeNextFrame'] == 0
              and s['observedEquality']['palMatchesOnlyTheFreshShadow_aliasAware'] == 1, str(s))

        # Silent: shadow content changes with $18 = 0 -> nothing published next frame.
        silent = write(tmp, 'silent.bin', build_capture([(base, armed, base), (base, armed, base),
                                                         (ramp, off, base), (ramp, off, base)]))
        run(['--side', 'original', silent, '2', '4', os.path.join(tmp, 'silent.csv')])
        s = summary(tmp, 'silent.summary.json')
        check('unarmed shadow change reported silent and counted as flag-gated',
              s['observable']['shadowContentChanges'] == 1 and s['observable']['palSnapshotChanges'] == 0
              and s['observable']['shadowChangeWithNoPalChangeAtOrNextFrame'] == [3]
              and s['observable']['shadowChangeFramesWithFlagNonZero'] == 0, str(s['observable']))

        # Repeated upload of identical bytes must NOT look like a change (the known blind spot).
        repeat = write(tmp, 'repeat.bin', build_capture([(base, armed, base)] * 4))
        run(['--side', 'original', repeat, '2', '4', os.path.join(tmp, 'repeat.csv')])
        obs = summary(tmp, 'repeat.summary.json')['observable']
        check('repeated identical upload shows no snapshot change (blind spot, documented)',
              obs['shadowContentChanges'] == 0 and obs['palSnapshotChanges'] == 0, str(obs))

        # Alias: $3F00[0] carries shadow[16] -> plain equality fails, alias-aware holds.
        shadow_a = bytearray(base)
        shadow_a[16] = 0x27
        pal_a = bytearray(base)
        pal_a[0] = 0x27
        pal_a[16] = 0x27
        alias = write(tmp, 'alias.bin', build_capture([(base, armed, base),
                                                       (bytes(shadow_a), armed, bytes(pal_a)),
                                                       (bytes(shadow_a), armed, bytes(pal_a))]))
        run(['--side', 'original', alias, '2', '3', os.path.join(tmp, 'alias.csv')])
        eq = summary(tmp, 'alias.summary.json')['observedEquality']
        check('alias-aware equality tolerates the $3F10 mirror, plain does not',
              eq['palEqualsShadowPrevFrame_plain'] < eq['palEqualsShadowPrevFrame_aliasAware'], str(eq))

        # Port scope: an unmapped shadow column must not be read as a zero shadow.
        port = write(tmp, 'port.bin', build_capture([(bytes(32), off, base), (bytes(32), off, base),
                                                     (bytes(32), off, ramp), (bytes(32), off, ramp)]))
        run(['--side', 'port', port, '2', '4', os.path.join(tmp, 'port.csv')])
        p = summary(tmp, 'port.summary.json')
        check('port scope reports shadow/flag NOT MEASURED and keeps the palette column',
              p['observable']['shadowContentChanges'].startswith('NOT MEASURED')
              and p['observable']['palSnapshotChanges'] == 1
              and p['shadowColumnNonZeroFrames'] == 0
              and p['observedEquality'].startswith('NOT MEASURED'), str(p['observable']))

        try:
            run(['--side', 'original', port, '2', '4', os.path.join(tmp, 'never3.csv')])
            check('--side original refuses an unmapped shadow column', False)
        except Refuse as exc:
            check('--side original refuses an unmapped shadow column', 'sparse' in str(exc), str(exc))

        try:
            run([good, '2', '3', os.path.join(tmp, 'never4.csv')])
            check('--side is required', False)
        except SystemExit:
            check('--side is required', True)

    for name, ok, detail in results:
        print(f'{"PASS" if ok else "FAIL"}  {name}' + ('' if ok else f'  [{detail}]'))
    failed = [name for name, ok, _ in results if not ok]
    print(f'selftest: {len(results) - len(failed)}/{len(results)} controls passed')
    return 1 if failed else 0


def main():
    try:
        return run()
    except Refuse as exc:
        print(f'REFUSED: {exc}', file=sys.stderr)
        return 2


if __name__ == '__main__':
    sys.exit(main())
