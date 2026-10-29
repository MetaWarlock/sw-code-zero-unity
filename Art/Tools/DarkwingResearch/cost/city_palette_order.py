"""ZERO-23 canonical profile: shadow $0300 write vs $C059 upload ($3F00) per frame.

Reads an existing fixed-record capture (4384 bytes: 2048 RAM + 2048 NT + 256 OAM + 32 palette,
1-based frames, the layout Art/Tools/DarkwingResearch/stand/stand.py exports) and answers one
question for a frame window: in which frame is the shadow written, in which frame does the
uploaded palette change, and was $18 armed at the write. Port and original captures use the same
record, so the same test runs on both sides.

Nothing in the capture, ROM, input or golden set is written; the profile and summary are new files
and an existing output is refused. Zeros in the RAM column of a PORT capture are not a shadow:
that column is a sparse explicitly-mapped export, so --side port only trusts the palette column.

Usage:
  python -I Art/Tools/DarkwingResearch/cost/city_palette_order.py <buffers.bin> <lo> <hi> <out.csv>
"""
import argparse
import csv
import hashlib
import json
import os
import sys

STRIDE = 4384
PAL, RAM = 4352, 0
SHADOW, SOURCE = 0x300, 0x320

parser = argparse.ArgumentParser(description=__doc__.splitlines()[0])
parser.add_argument('capture', type=str)
parser.add_argument('lo', type=int)
parser.add_argument('hi', type=int)
parser.add_argument('out', type=str)
parser.add_argument('--shadow-bytes', type=int, default=32,
                    help='how many $0300 bytes define a shadow change (default 32)')
args = parser.parse_args()
n = args.shadow_bytes
if not 1 <= n <= 32:
    parser.error('--shadow-bytes must be within 1..32')
if args.lo < 2 or args.hi <= args.lo:
    parser.error('Frame window must be lo>=2 and hi>lo')
if os.path.exists(args.out):
    parser.error(f'Refuse to overwrite existing output: {args.out}')

raw = open(args.capture, 'rb').read()
if len(raw) % STRIDE:
    parser.error(f'{args.capture} is not a whole number of {STRIDE}-byte records')
frames = len(raw) // STRIDE
if args.hi > frames:
    parser.error(f'capture holds {frames} frames, window asks for {args.hi}')


def record(f):
    return raw[(f - 1) * STRIDE:f * STRIDE]


def shadow(f):
    r = record(f)
    return bytes(r[RAM + SHADOW:RAM + SHADOW + n])


def source(f):
    r = record(f)
    return bytes(r[RAM + SOURCE:RAM + SOURCE + n])


def palette(f):
    r = record(f)
    return bytes(r[PAL:PAL + 32])


def flag(f):
    return record(f)[RAM + 0x18]


writes, uploads = [], []
for f in range(max(2, args.lo - 1), args.hi + 2):
    if shadow(f) != shadow(f - 1):
        writes.append(f)
    if palette(f) != palette(f - 1):
        uploads.append(f)
window_writes = [f for f in writes if args.lo <= f <= args.hi]
window_uploads = [f for f in uploads if args.lo <= f <= args.hi]
upload_set = set(uploads)
armed = [f for f in window_writes if flag(f) != 0]
same_frame = [f for f in window_writes if f in upload_set]
lagged = [f for f in window_writes if f + 1 in upload_set]
silent = [f for f in window_writes if f not in upload_set and f + 1 not in upload_set]
unexplained = [f for f in window_uploads if (f - 1) not in set(window_writes) and f not in set(window_writes)]

with open(args.out, 'w', newline='') as handle:
    w = csv.writer(handle)
    w.writerow(['frame', 'flag18', 'shadow_write', 'upload_change',
                'shadow0300', 'source0320', 'palette3f00'])
    for f in range(args.lo, args.hi + 1):
        w.writerow([f, f'{flag(f):02X}', f in set(window_writes), f in set(window_uploads),
                    shadow(f).hex(), source(f).hex(), palette(f).hex()])

summary = {
    'capture': args.capture.replace(os.sep, '/'),
    'sha256': hashlib.sha256(raw).hexdigest(),
    'records': frames,
    'stride': STRIDE,
    'window': [args.lo, args.hi],
    'shadowBytesCompared': n,
    'shadowWrites': len(window_writes),
    'uploadChanges': len(window_uploads),
    'writeAndUploadSameFrame': len(same_frame),
    'writeThenUploadNextFrame': len(lagged),
    'writeWithNoUploadAtWriteOrNext': silent,
    'uploadWithoutShadowWriteAtThatOrPreviousFrame': unexplained,
    'shadowWritesWithFlagArmed': len(armed),
    'note': 'flag18 is read at the capture sample point; a port capture leaves RAM $0300/$18 '
            'unmapped (sparse export), so zeros there are not a shadow value',
}
side = os.path.splitext(args.out)[0] + '.summary.json'
with open(side, 'w', encoding='utf-8') as handle:
    json.dump(summary, handle, indent=1, sort_keys=True)
print(json.dumps(summary, indent=1, sort_keys=True))
sys.exit(0)
