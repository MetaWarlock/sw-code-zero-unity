"""Independent bank-7 source price of the CAFC player dispatch for state 09 (ZERO-44).

Walks the original fixed bank 7 instructions from CAFC to the RTS that returns to CA43,
reading only the pre-pass original RAM record of the poll and bank-7 ROM bytes, and sums
NMOS 6502 cycles (page-cross reads, taken/crossing branches). Any access outside RAM
$0000-$07FF or bank 7 $C000-$FFFF, or an unlisted opcode, stops the poll as UNSUPPORTED:
the walk is restricted to the reached state-09 code and is not a general emulator.

The measured original price (CA59 - CA40 - 18, valid when $65 >= 0 and $30 == 0) is read
only AFTER the walk, for comparison. Nothing here is used by the game runtime.

usage: state09_source_price.py ROM CITY_PHASES_CSV ORIGINAL_BUFFERS FIRST LAST [--json OUT]
       state09_source_price.py --selftest ROM ORIGINAL_BUFFERS
       state09_source_price.py --dump-cases ROM ORIGINAL_BUFFERS OUT_JSON [CAP] [SEED] [POOL]
       state09_source_price.py --ground00 ROM CITY_PHASES_CSV ORIGINAL_BUFFERS INPUT_BIN FIRST LAST [--json OUT]

--dump-cases (ZERO-47) writes the fixture the C# controls read: expected prices taken from
walk() over bank-7, the class/coordinates each executed $EBE8 returned, and the branch labels
proved by the walk's own instruction addresses. It reads no C# value and no oracle.
"""
import hashlib
import json
import os
import random
import sys

RECORD = 4384
ROM_SHA = '1271dce8678ffc261e2c05cc785ae53e29ac1f9ecf6265a4265bed825abb0a00'


class Unsupported(Exception):
    pass


class Cpu:
    def __init__(self, ram, bank7):
        self.ram, self.rom = bytearray(ram), bank7
        self.a = self.x = self.y = 0
        self.c = self.z = self.n = 0
        self.s = 0xFD
        self.cyc = 0
        self.trace = []  # (pc, cycles) for every executed instruction

    def rd(self, a):
        if a < 0x0800:
            return self.ram[a]
        if a >= 0xC000:
            return self.rom[a - 0xC000]
        raise Unsupported('read $%04X' % a)

    def wr(self, a, v):
        if a >= 0x0800:
            raise Unsupported('write $%04X' % a)
        self.ram[a] = v & 0xFF

    def nz(self, v):
        v &= 0xFF
        self.z, self.n = int(v == 0), v >> 7
        return v

    def push(self, v):
        self.wr(0x100 + self.s, v)
        self.s = (self.s - 1) & 0xFF

    def pull(self):
        self.s = (self.s + 1) & 0xFF
        return self.rd(0x100 + self.s)


# mode, base cycles; '+' adds one cycle on page cross (reads only)
MODES = {'imm': 2, 'zp': 3, 'zpx': 4, 'abs': 4, 'absx': 4, 'absy': 4, 'izy': 5}
OPS = {
    0xA9: ('LDA', 'imm'), 0xA5: ('LDA', 'zp'), 0xB5: ('LDA', 'zpx'), 0xAD: ('LDA', 'abs'),
    0xBD: ('LDA', 'absx'), 0xB9: ('LDA', 'absy'), 0xB1: ('LDA', 'izy'),
    0xA2: ('LDX', 'imm'), 0xA6: ('LDX', 'zp'), 0xAE: ('LDX', 'abs'),
    0xA0: ('LDY', 'imm'), 0xA4: ('LDY', 'zp'), 0xAC: ('LDY', 'abs'),
    0x85: ('STA', 'zp'), 0x95: ('STA', 'zpx'), 0x8D: ('STA', 'abs'), 0x9D: ('STA', 'absx'),
    0x99: ('STA', 'absy'), 0x86: ('STX', 'zp'), 0x84: ('STY', 'zp'), 0x8E: ('STX', 'abs'),
    0x8C: ('STY', 'abs'),
    0x29: ('AND', 'imm'), 0x25: ('AND', 'zp'), 0x09: ('ORA', 'imm'), 0x05: ('ORA', 'zp'),
    0x19: ('ORA', 'absy'), 0x49: ('EOR', 'imm'),
    0x69: ('ADC', 'imm'), 0x65: ('ADC', 'zp'), 0x7D: ('ADC', 'absx'), 0x79: ('ADC', 'absy'),
    0xE9: ('SBC', 'imm'), 0xE5: ('SBC', 'zp'), 0xFD: ('SBC', 'absx'),
    0xC9: ('CMP', 'imm'), 0xC5: ('CMP', 'zp'), 0xDD: ('CMP', 'absx'),
    0xE0: ('CPX', 'imm'), 0xC0: ('CPY', 'imm'),
    0xE6: ('INC', 'zp'), 0xC6: ('DEC', 'zp'), 0xEE: ('INC', 'abs'), 0xCE: ('DEC', 'abs'),
}
IMPLIED = {0xE8: 'INX', 0xA8: 'TAY', 0x8A: 'TXA', 0xAA: 'TAX', 0x98: 'TYA', 0x4A: 'LSRA',
           0x0A: 'ASLA', 0x48: 'PHA', 0x68: 'PLA', 0x38: 'SEC', 0x18: 'CLC', 0xC8: 'INY',
           0x88: 'DEY', 0xCA: 'DEX', 0x08: 'PHP', 0x28: 'PLP'}
# ZERO-53: PHP/PLP carry the $F2D7 floor carry across CD69..CDF5 in the ground vertical
# step. Only N/Z/C are modelled; V/D/I are not, and a V branch stays an unlisted opcode.
BRANCH = {0x10: ('n', 0), 0x30: ('n', 1), 0x90: ('c', 0), 0xB0: ('c', 1), 0xD0: ('z', 0),
          0xF0: ('z', 1)}


def addr(cpu, pc, mode):
    """Return (effective address or None for imm, operand length, page-cross flag)."""
    lo = cpu.rd(pc + 1)
    if mode == 'imm':
        return None, 2, False
    if mode == 'zp':
        return lo, 2, False
    if mode == 'zpx':
        return (lo + cpu.x) & 0xFF, 2, False
    if mode == 'izy':
        base = cpu.rd(lo) | cpu.rd((lo + 1) & 0xFF) << 8
        ea = (base + cpu.y) & 0xFFFF
        return ea, 2, (base & 0xFF00) != (ea & 0xFF00)
    base = lo | cpu.rd(pc + 2) << 8
    if mode == 'abs':
        return base, 3, False
    ea = (base + (cpu.x if mode == 'absx' else cpu.y)) & 0xFFFF
    return ea, 3, (base & 0xFF00) != (ea & 0xFF00)


def step(cpu, pc):
    op = cpu.rd(pc)
    if op in IMPLIED:
        name = IMPLIED[op]
        cyc = {'PHA': 3, 'PLA': 4, 'PHP': 3, 'PLP': 4}.get(name, 2)
        if name == 'INX': cpu.x = cpu.nz(cpu.x + 1)
        elif name == 'DEX': cpu.x = cpu.nz(cpu.x - 1)
        elif name == 'INY': cpu.y = cpu.nz(cpu.y + 1)
        elif name == 'DEY': cpu.y = cpu.nz(cpu.y - 1)
        elif name == 'TAY': cpu.y = cpu.nz(cpu.a)
        elif name == 'TAX': cpu.x = cpu.nz(cpu.a)
        elif name == 'TXA': cpu.a = cpu.nz(cpu.x)
        elif name == 'TYA': cpu.a = cpu.nz(cpu.y)
        elif name == 'LSRA': cpu.c = cpu.a & 1; cpu.a = cpu.nz(cpu.a >> 1)
        elif name == 'ASLA': cpu.c = cpu.a >> 7; cpu.a = cpu.nz(cpu.a << 1)
        elif name == 'PHA': cpu.push(cpu.a)
        elif name == 'PLA': cpu.a = cpu.nz(cpu.pull())
        elif name == 'PHP': cpu.push(cpu.n << 7 | 0x30 | cpu.z << 1 | cpu.c)
        elif name == 'PLP':
            f = cpu.pull(); cpu.n, cpu.z, cpu.c = f >> 7, (f >> 1) & 1, f & 1
        elif name == 'SEC': cpu.c = 1
        elif name == 'CLC': cpu.c = 0
        return pc + 1, cyc
    if op in BRANCH:
        flag, want = BRANCH[op]
        nxt = pc + 2
        if getattr(cpu, flag) != want:
            return nxt, 2
        off = cpu.rd(pc + 1)
        tgt = (nxt + (off - 256 if off & 0x80 else off)) & 0xFFFF
        return tgt, 3 + ((nxt & 0xFF00) != (tgt & 0xFF00))
    if op == 0x20:  # JSR
        ret = pc + 2
        cpu.push(ret >> 8); cpu.push(ret & 0xFF)
        return cpu.rd(pc + 1) | cpu.rd(pc + 2) << 8, 6
    if op == 0x60:  # RTS
        lo = cpu.pull(); hi = cpu.pull()
        return ((hi << 8 | lo) + 1) & 0xFFFF, 6
    if op == 0x4C:
        return cpu.rd(pc + 1) | cpu.rd(pc + 2) << 8, 3
    if op == 0x6C:  # NMOS page-wrap bug irrelevant for $0000
        p = cpu.rd(pc + 1) | cpu.rd(pc + 2) << 8
        return cpu.rd(p) | cpu.rd((p & 0xFF00) | ((p + 1) & 0xFF)) << 8, 5
    if op not in OPS or OPS[op] is None:
        raise Unsupported('opcode %02X at %04X' % (op, pc))
    name, mode = OPS[op]
    ea, ln, cross = addr(cpu, pc, mode)
    val = (lambda: cpu.rd(pc + 1)) if ea is None else (lambda: cpu.rd(ea))
    cyc = MODES[mode]
    if name in ('STA', 'STX', 'STY'):
        cpu.wr(ea, {'STA': cpu.a, 'STX': cpu.x, 'STY': cpu.y}[name])
        cyc += 1 if mode in ('absx', 'absy', 'izy') else 0
        return pc + ln, cyc
    if name in ('INC', 'DEC'):
        cpu.wr(ea, cpu.nz(cpu.rd(ea) + (1 if name == 'INC' else -1)))
        return pc + ln, cyc + 2
    cyc += cross
    v = val()
    if name == 'LDA': cpu.a = cpu.nz(v)
    elif name == 'LDX': cpu.x = cpu.nz(v)
    elif name == 'LDY': cpu.y = cpu.nz(v)
    elif name == 'AND': cpu.a = cpu.nz(cpu.a & v)
    elif name == 'ORA': cpu.a = cpu.nz(cpu.a | v)
    elif name == 'EOR': cpu.a = cpu.nz(cpu.a ^ v)
    elif name == 'ADC':
        r = cpu.a + v + cpu.c; cpu.c = int(r > 0xFF); cpu.a = cpu.nz(r)
    elif name == 'SBC':
        r = cpu.a - v - (1 - cpu.c); cpu.c = int(r >= 0); cpu.a = cpu.nz(r)
    elif name in ('CMP', 'CPX', 'CPY'):
        reg = {'CMP': cpu.a, 'CPX': cpu.x, 'CPY': cpu.y}[name]
        cpu.c = int(reg >= v); cpu.nz(reg - v)
    return pc + ln, cyc


def walk(ram, bank7, events=None):
    """Run the dispatch. When `events` is a list, record every return from the real $EBE8
    point routine: the point index it was entered with, the class it computed, and the probe
    coordinates it left in its scratch ($03/$06 = X, $07/$08 = Y). Everything in an event comes
    from executed bank-7 bytes, nothing from a transcription."""
    cpu = Cpu(ram, bank7)
    cpu.push(0xCA); cpu.push(0x42)  # JSR $CAFC from CA40 returns to CA43
    base_s = cpu.s
    pc = 0xCAFC
    inside = None  # (entry stack pointer, point index) while $EBE8 is running
    for _ in range(5000):
        if pc == 0xCA43 and cpu.s == (base_s + 2) & 0xFF:
            return cpu
        if events is not None:
            if pc == 0xEBE8:
                inside = (cpu.s, cpu.y, len(cpu.trace))
            elif inside is not None and cpu.rd(pc) == 0x60 and cpu.s == inside[0]:
                # $EC6D is the BEQ target that leads into the $0600 read: a probe that never
                # reaches it is off the camera page and returns class 0 without touching the map.
                window = [p for p, _ in cpu.trace[inside[2]:]]
                reads = (0xEC6D, 0xEC6F) in list(zip(window, window[1:]))
                events.append({'point': inside[1], 'class': cpu.a, 'read': reads,
                               'x': cpu.ram[0x06] | cpu.ram[0x03] << 8,
                               'y': cpu.ram[0x07] | cpu.ram[0x08] << 8})
                inside = None
        nxt, c = step(cpu, pc)
        cpu.trace.append((pc, c))
        cpu.cyc += c
        pc = nxt
    raise Unsupported('no return within 5000 instructions')


def segments(trace):
    """Split the cycle total into named source segments for the implementation checklist."""
    seg = {}
    stack = ['dispatch CAFC/CB41']
    for pc, c in trace:
        if pc == 0xD517: stack = ['D517 body']
        name = 'D517 body'
        if 0xF5A4 <= pc <= 0xF5C8: name = 'F5A4 integrate'
        elif 0xF5DA <= pc <= 0xF5FB: name = 'F5DA gravity'
        elif 0xEB52 <= pc <= 0xECA6: name = 'EB52 probe'
        elif 0xEDD2 <= pc <= 0xEDF0: name = 'EDD2 snap'
        elif 0xF5FC <= pc <= 0xF606: name = 'F5FC land vel'
        elif 0xF850 <= pc <= 0xF868: name = 'F850 sfx queue'
        elif 0xF4D7 <= pc <= 0xF4FC: name = 'F4D7 frame'
        elif pc < 0xD517: name = 'dispatch CAFC/CB41'
        seg[name] = seg.get(name, 0) + c
    return seg


def verdict(source, oracle):
    return 'EXACT' if source == oracle else 'MISMATCH'


# ---------------------------------------------------------------------------
# ZERO-47 fixture dump: executable C# controls need expected values that come
# only from executed bank-7 bytes. Each case below is (input RAM, expected
# outcome) where the expected outcome is walk() on that RAM; refusal categories
# come from the RAM condition or from a real $EBE8 return value, never from the
# C# under test. Scenario selection is a search over generated inputs filtered
# by the branch labels the WALKER's own trace shows, so no case is claimed to
# exercise a branch on the strength of a hand-written label.
# ---------------------------------------------------------------------------

FIELD_ADDR = [0x50, 0x99, 0x400, 0x460, 0x4D0, 0x4F0, 0x500, 0x510, 0x520, 0x550, 0x560, 0xFA, 0xFB]
FIELD_NAME = ['50', '99', '400', '460', '4d0', '4f0', '500', '510', '520', '550', '560', 'fa', 'fb']

# $EBE8 indexes its point offsets with LDA $ED19,Y / LDA $ECAD,Y: read the tables
# out of the bank-7 slice so the fixture carries original coordinates.
def point_offsets(bank7):
    def table(address, count=16):
        base = address - 0xC000
        return [v - 256 if v & 0x80 else v for v in bank7[base:base + count]]
    return table(0xED19), table(0xECAD)


def labels_of(trace):
    """Branch labels proven by the executed instruction addresses of this walk."""
    seen = {pc for pc, _ in trace}
    pairs = list(zip([pc for pc, _ in trace], [pc for pc, _ in trace][1:]))
    out = []
    for name, test in LABELS:
        if test(seen, pairs):
            out.append(name)
    return out


LABELS = [
    ('D539 plain branch', lambda s, p: 0xD539 in s),
    ('F5BB F5A4 row wrap', lambda s, p: 0xF5BB in s),
    ('F5ED slow fall past clamp', lambda s, p: 0xF5ED in s and 0xF5EF in s),
    ('F5EB BPL rising result', lambda s, p: 0xF5EB in s and 0xF5ED not in s),
    ('F5F1 F5DA clamp', lambda s, p: 0xF5F1 in s),
    ('EC2D probe row wrap', lambda s, p: 0xEC2D in s),
    ('EC62 off-camera probe', lambda s, p: 0xEC62 in s),
    ('EC6D probe same page', lambda s, p: 0xEC6D in s),
    ('EB93 row below camera row (+$2E)', lambda s, p: 0xEB93 in s),
    ('EB9A via EB70 lower class', lambda s, p: (0xEB70, 0xEB9A) in p),
    ('F4E8 hit 0', lambda s, p: 0xF4E8 in s and 0xF4EA not in s),
    ('F4EC class 1', lambda s, p: 0xF4EC in s),
    ('F4EC class 1, $0560>=FC no landing', lambda s, p: 0xF4EC in s and 0xF4F3 not in s),
    ('F4EA class >=2 landing', lambda s, p: (0xF4EA, 0xF4F3) in p),
    ('F4F3 landing', lambda s, p: 0xF4F3 in s),
    ('F5FC land vel', lambda s, p: 0xF5FC in s),
    ('EDE3 EDD2 borrow', lambda s, p: 0xEDE3 in s),
    ('F85C audio enqueued', lambda s, p: 0xF85C in s),
    ('F866 audio queue busy', lambda s, p: 0xF850 in s and 0xF85C not in s),
]


def refusal_of(ram, events):
    """Why the state-09 measure must refuse this input, read off the input and off the
    real $EBE8 returns. None means the case is expected to be priced."""
    if ram[0x50] != 0x09:
        return 'state $%02X is not 09' % ram[0x50]
    if not ram[0x4D0] & 0x80:
        return '$04D0 bit7 clear: CAFC player inactive'
    if ram[0x4D0] & 0x0F:
        return '$04D0 low nibble %X: D51C leaves for $D53F' % (ram[0x4D0] & 0x0F)
    if ram[0x400] & 0x20:
        return '$0400 bit5 set: EB52 pre-hook path'
    for event in events:
        if event['x'] > 0x7FFF:
            return 'point %d probe X $%04X: EC03 negative-X scratch' % (event['point'], event['x'])
    for event in events:
        if event['class'] > 3:
            return 'point %d returned class %d: outside the verified 0..3 slice' % (
                event['point'], event['class'])
    return None


def audio_of(ram):
    return 'accepts' if ram[0xDC + ram[0xDA]] == 0x88 else 'busy'


def set_audio(ram, mode):
    ram[0xDC + ram[0xDA]] = 0x88 if mode == 'accepts' else 0x10


def fields_of(ram):
    return ' '.join('%02X' % ram[a] for a in FIELD_ADDR)


def price_of(ram, bank7):
    """Walk once and package the case: expected price or expected refusal, the real
    $EBE8 returns, and the branch labels the executed trace shows."""
    events = []
    try:
        cpu = walk(bytes(ram), bank7, events)
        rom = 'price %d' % cpu.cyc
        unsupported = None
    except Unsupported as error:
        cpu, rom, unsupported = None, 'unsupported %s' % error, str(error)
    return {'events': events, 'rom': rom, 'unsupported': unsupported,
            'refusal': refusal_of(ram, events),
            'labels': labels_of(cpu.trace) if cpu else []}


def case_row(name, kind, ram, bank7, poll, fill, audio, pair=''):
    got = price_of(ram, bank7)
    # [point index, class $EBE8 returned, probe X, probe Y, did that probe read the map].
    # All five come out of executed ROM instructions: they are the map data a synthetic case
    # injects, and what the port's own TileMap.TypeAt is compared against on a route case.
    probes = [[e['point'], e['class'], e['x'], e['y'], 1 if e['read'] else 0] for e in got['events']]
    if got['refusal']:
        expect = {'refuse': got['refusal'] + (' (walker: %s)' % got['rom'] if got['unsupported'] else '')}
    elif got['unsupported']:
        expect = {'unsupported': got['rom']}  # the walk left the reached state-09 code
    else:
        expect = {'price': int(got['rom'].split()[1])}
    return {'id': name, 'kind': kind, 'poll': poll, 'fields': fields_of(ram),
            'map': 'route' if fill == 'route' else {'fill': fill, 'fallback': 0},
            'audio': audio, 'expect': expect, 'rom': got['rom'], 'probes': probes,
            'labels': got['labels'], 'pair': pair}


def synth_combos(records):
    """Parameter grid of generated inputs: physics bytes that make the integrate/gravity
    branches reachable, a tile-map fill (uniform, or per-byte nibbles so the three points of
    one probe set can carry different classes), and a camera candidate that steers the probe
    on or off the window page (only then does $EBE8 read the map at all)."""
    combos = []
    for base in (1955, 1965, 1970):
        for vel in (0x00, 0x01, 0x05, 0x80, 0xF6, 0xF8, 0xF9, 0xFB, 0xFC, 0xFF):
            for vfrac in (0x00, 0x20, 0x40, 0x80, 0xC0):
                for yfrac in (0x00, 0x80):
                    for ylow in (0x00, 0x03, 0x80, 0xE9, 0xF5, 0xF9, 0xFE):
                        for fill in ('uniform 0', 'uniform 1', 'uniform 2', 'uniform 3', 'mixed 0'):
                            for audio in ('accepts', 'busy'):
                                for step, frac in ((0, 0x00), (0, 0x80), (-1, 0x80), (1, 0x00)):
                                    combos.append((base, vel, vfrac, yfrac, ylow, fill,
                                                   audio, step, frac))
    return combos


def synth_ram(records, combo, index):
    base, vel, vfrac, yfrac, ylow, fill, audio, step, frac = combo
    ram = bytearray(records[base])
    ram[0x50], ram[0x4D0], ram[0x400] = 0x09, 0x80, 0xD0
    ram[0x560], ram[0x550], ram[0x460], ram[0x99] = vel, vfrac, yfrac, 0x40
    ram[0x510], ram[0x520] = ylow, 0x0A
    ram[0x4F0], ram[0x500] = 0x20, 0x01
    if fill.startswith('uniform'):
        cls = int(fill.split()[1])
        for a in range(0x600, 0x700):
            ram[a] = cls * 0x11  # both nibbles agree, so the class $EBE8 returns does not
    else:                        # depend on which half of $0600,Y the index math picked
        dice = random.Random(index)
        for a in range(0x600, 0x700):
            ram[a] = dice.randrange(4) * 0x10 + dice.randrange(4)
    set_audio(ram, audio)
    ram[0xFB] = (0x0A + step) & 0xFF
    ram[0xFA] = frac
    return ram, base, fill, audio


# Input errors for the comparator's negative control. Each one can change the branch structure:
# $0550/$0460 flip the $F5A4 and $F5DA borrows, $0510 the row wrap, $0560 the $D523 plain branch
# and the $F5EF clamp, the $DC+$DA slot byte the $F85A queue branch.
MUTATIONS = [
    ('0550', '$0550 +$80 (fraction borrow)', lambda r: r.__setitem__(0x550, (r[0x550] + 0x80) & 0xFF)),
    ('0460', '$0460 +$80 (fraction borrow)', lambda r: r.__setitem__(0x460, (r[0x460] + 0x80) & 0xFF)),
    ('0510', '$0510 +$20 (row position)', lambda r: r.__setitem__(0x510, (r[0x510] + 0x20) & 0xFF)),
    ('0560', '$0560 plain<->falling', lambda r: r.__setitem__(0x560, 0x05 if r[0x560] >= 0xFA else 0xFA)),
    ('dc+da', '$DC+$DA queue slot', lambda r: r.__setitem__(
        0xDC + r[0xDA], 0x10 if r[0xDC + r[0xDA]] == 0x88 else 0x88)),
]


def emit_cases(rom_path, buf_path, out_path, cap, seed, pool_size):
    rom = open(rom_path, 'rb').read()
    if hashlib.sha256(rom).hexdigest() != ROM_SHA:
        sys.exit('ROM SHA-256 mismatch')
    bank7 = rom[16 + 16384 * (rom[4] - 1):16 + 16384 * rom[4]]
    records = {}
    with open(buf_path, 'rb') as f:
        for p in range(1941, 1978):
            f.seek((p - 1) * RECORD)
            records[p] = bytearray(f.read(2048))
    cases, wanted = [], {name for name, _ in LABELS}

    for p, record in records.items():
        cases.append(case_row('route-%d' % p, 'route', bytearray(record), bank7, p, 'route',
                              audio_of(record)))
        # Comparator negative control: one deliberately relevant input error per route poll.
        # A mutation is kept only when the ROM's own price for it differs, so the expectation
        # the port has to miss by is proven relevant instead of asserted by construction.
        for field, desc, mutate in MUTATIONS:
            neg = bytearray(record)
            mutate(neg)
            if neg == record:
                continue
            before, after = price_of(bytearray(record), bank7), price_of(neg, bank7)
            if before['refusal'] or after['refusal'] or before['rom'] == after['rom']:
                continue
            row = case_row('route-%d-neg%s' % (p, field), 'negative', neg, bank7, p, 'route',
                           audio_of(neg), pair='route-%d' % p)
            row['mutation'] = desc
            cases.append(row)
            break

    explicit = []
    for p in (1965, 1977):
        for what, fill, edit in (
                ('state0B', 'route', lambda r: r.__setitem__(0x50, 0x0B)),
                ('state12', 'route', lambda r: r.__setitem__(0x50, 0x12)),
                ('inactive', 'route', lambda r: r.__setitem__(0x4D0, 0x00)),
                ('landed', 'route', lambda r: r.__setitem__(0x4D0, 0x81)),
                ('hookbit', 'route', lambda r: r.__setitem__(0x400, 0xF0)),
                ('class5', 'uniform 5', None)):
            ram = bytearray(records[p])
            if what == 'class5':
                # A fixed setup so $EBE8 really reads the injected nibble: the probe lands on
                # the camera page, and $0560 < $FA keeps the $D525 probe call in play.
                ram[0x50], ram[0x4D0], ram[0x400] = 0x09, 0x80, 0xD0
                ram[0x560], ram[0x550], ram[0x460], ram[0x99] = 0xF0, 0x00, 0x00, 0x40
                ram[0x510], ram[0x520] = 0x20, 0x0A
                ram[0x4F0], ram[0x500] = 0x20, 0x01
                ram[0xFA], ram[0xFB] = 0x00, 0x0A
                for a in range(0x600, 0x700):
                    ram[a] = 0x55
            else:
                edit(ram)
            row = case_row('refuse-%s-%d' % (what, p), 'refusal', ram, bank7, p, fill,
                           audio_of(ram))
            row['condition'] = what
            explicit.append(row)
    cases.extend(explicit)

    rng = random.Random(seed)
    combos = synth_combos(records)
    generated = len(combos)
    rng.shuffle(combos)
    combos = combos[:pool_size]
    rows, walked, unassertable = {}, 0, 0
    for index, combo in enumerate(combos):
        ram, base, fill, audio = synth_ram(records, combo, index)
        row = case_row('synth-%04d' % index, 'synthetic', ram, bank7, base, fill, audio)
        walked += 1
        if 'unsupported' in row['expect']:
            unassertable += 1  # the walk left the reached state-09 code: no expected value to assert
            continue
        rows[row['id']] = (ram, row)
    by_label, selected, pool_ram, extra = {}, {}, {}, {}
    for key, (ram, row) in sorted(rows.items(), key=lambda kv: (-len(kv[1][1]['labels']), kv[0])):
        new = [label for label in row['labels'] if label not in by_label]
        if not new or len(selected) >= cap:
            continue
        selected[row['id']] = row
        pool_ram[row['id']] = ram
        for label in new:
            by_label[label] = row
    # Breadth on top of branch coverage: keep adding rows whose branch-label signature is new
    # until the cap, so the formula is asserted on many different paths, not only on the
    # minimum set that touches every label.
    signatures = {frozenset(row['labels']) for row in selected.values()}
    for key in sorted(rows):
        if key in selected or len(selected) + len(extra) >= cap:
            continue
        row = rows[key][1]
        signature = frozenset(row['labels'])
        if signature in signatures:
            continue
        signatures.add(signature)
        extra[key] = row
        pool_ram[key] = rows[key][0]
    cases.extend(selected[key] for key in sorted(selected))
    cases.extend(extra[key] for key in sorted(extra))
    # Every selected case that reaches the $F850 queue also gets its opposite audio sibling,
    # so the busy/free pair is asserted on identical physics bytes rather than on two draws.
    siblings = 0
    chosen = dict(selected)
    chosen.update(extra)
    for key in sorted(chosen):
        row = chosen[key]
        if not any(label.startswith('F85C') or label.startswith('F866') for label in row['labels']):
            continue
        ram = pool_ram[key]
        other = 'busy' if row['audio'] == 'accepts' else 'accepts'
        alt = bytearray(ram)
        set_audio(alt, other)
        sibling = case_row('%s-audio' % key, 'audio', alt, bank7, row['poll'],
                           row['map']['fill'], other, pair=key)
        if 'price' in sibling['expect'] and sibling['expect'] != row['expect']:
            cases.append(sibling)
            siblings += 1
    covered = {label for row in chosen.values() for label in row['labels']}
    document = {
        'tool': 'state09_source_price.py --dump-cases',
        'provenance': 'expected prices/refs come from walk() over bank-7 $C000-$FFFF and the '
                      'pre-frame original RAM record of the named poll; $EBE8 probe classes and '
                      'coordinates are the values the executed ROM left in its scratch, and the '
                      'point offsets come from the ROM tables $ED19/$ECAD. Nothing here reads the '
                      'C# under test.',
        'rom_sha256': ROM_SHA, 'seed': seed, 'cap': cap,
        'bank7_sha256': hashlib.sha256(bank7).hexdigest(),
        'records_sha256': hashlib.sha256(b''.join(records[p] for p in sorted(records))).hexdigest(),
        'records': 'the 2048 RAM bytes of the pre-frame record of polls 1941..1977 in %s'
                   % buf_path.replace('\\', '/'),
        'pool': {'generated': generated, 'walked': walked, 'left the slice': unassertable,
                 'label cover': len(selected), 'breadth': len(extra),
                 'audio siblings': siblings},
        'fields': FIELD_NAME,
        'labels': [name for name, _ in LABELS],
        'point_offsets_x': [v for v in point_offsets(bank7)[0]],
        'point_offsets_y': [v for v in point_offsets(bank7)[1]],
        'uncovered_labels': sorted(wanted - covered),
        'cases': cases,
    }
    with open(out_path, 'x') as f:
        json.dump(document, f, separators=(',', ':'), sort_keys=True)
    print('cases=%d synthetic chosen=%d walked=%d left-slice=%d covered=%d/%d' % (
        len(cases), len(chosen), walked, unassertable, len(covered), len(LABELS)))
    for name, _ in LABELS:
        print('%-38s %s' % (name, 'covered' if name in covered else 'UNCOVERED'))
    print('fixture', out_path, os.path.getsize(out_path), 'bytes')
    return 0 if not (wanted - covered) else 1


def selftest(bank7, buf_path):
    """Negative controls: hand-counted bank-7 deltas must appear in the walked segments.

    Mutations are synthetic RAM edits of real records; they check the walker against
    independent hand counts of the listing, they are not measured original behavior."""
    def rec(p):
        with open(buf_path, 'rb') as b:
            b.seek((p - 1) * RECORD)
            return bytearray(b.read(2048))

    def seg(ram):
        return segments(walk(bytes(ram), bank7).trace)

    checks = []
    base65, base77 = seg(rec(1965)), seg(rec(1977))
    r = rec(1965); r[0x510] = 0xEA  # new $0510 = EA - F9 - 0 -> F1 >= F0: row wrap
    checks.append(('F5A4 row wrap +17', seg(r)['F5A4 integrate'] - base65['F5A4 integrate'], 17))
    r = rec(1965); r[0x560] = 0x02  # rising: F5DA result positive, BPL taken
    checks.append(('F5DA BPL positive -4', seg(r)['F5DA gravity'] - base65['F5DA gravity'], -4))
    r = rec(1965); r[0x550] = 0x00  # 00 - 40 borrows: F9 -> F8 < F9, clamp
    checks.append(('F5DA clamp +13', seg(r)['F5DA gravity'] - base65['F5DA gravity'], 13))
    r = rec(1977); r[0xDC + r[0xDA]] = 0x10  # audio queue slot busy: BNE taken, no enqueue
    checks.append(('F850 busy -15', seg(r)['F850 sfx queue'] - base77['F850 sfx queue'], -15))
    r = rec(1965); r[0x4D0] = 0x81  # landed low nibble: D51C BNE D53F leaves the reached code
    try:
        walk(bytes(r), bank7)
        checks.append(('04D0&0F!=0 unsupported', 'walked', 'UNSUPPORTED'))
    except Unsupported:
        checks.append(('04D0&0F!=0 unsupported', 'UNSUPPORTED', 'UNSUPPORTED'))
    src = walk(bytes(rec(1965)), bank7).cyc
    checks.append(('comparator rejects oracle+1', verdict(src, src + 1), 'MISMATCH'))
    checks.append(('comparator accepts equal', verdict(src, src), 'EXACT'))
    ok = True
    for name, got, want in checks:
        ok &= got == want
        print('%-30s got %-12s want %-12s %s' % (name, got, want, 'PASS' if got == want else 'FAIL'))
    print('selftest', 'PASS' if ok else 'FAIL')
    return 0 if ok else 1


# ---------------------------------------------------------------------------
# ZERO-53 --ground00: the same walk() over CAFC for ground state 00, where the first
# unsupported native clock price (city poll 2379) enters the crouch branch CBD7 -> CC44.
# The pre-pass RAM record precedes the C162 input hook, so $16 (held) and $14 (pressed)
# of the CURRENT frame are taken from the recording's input.bin (held = byte p-1,
# pressed = held & ~byte p-2), never from original RAM; the next record's $14/$16 are
# compared afterwards (input_equal) so an input/RAM disagreement is reported, not hidden.
# ---------------------------------------------------------------------------

GROUND_BRANCHES = (('CBBD', 'jump A pressed'), ('CC3A', 'up -> state 03'),
                   ('CC44', 'down -> crouch state 02'), ('CC53', 'left/right walk'),
                   ('CBEC', 'B shot idle'), ('CBEF', 'idle'))
# Input-only variants of one CC44 poll: (name, held, pressed, expected branch). Expected
# branches follow the listing CBB7..CBE3; the walk proves them and gives their price.
GROUND_VARIANTS = (('down fresh', 0x04, 0x04, 'CC44'), ('down held', 0x04, 0x00, 'CC44'),
                   ('down+B pressed', 0x44, 0x44, 'CC44'), ('down, A held', 0x84, 0x00, 'CC44'),
                   ('down+select/start', 0x34, 0x04, 'CC44'), ('up+down', 0x0C, 0x0C, 'CBEF'),
                   ('up', 0x08, 0x08, 'CC3A'), ('down+right', 0x05, 0x05, 'CC53'),
                   ('down+left', 0x06, 0x06, 'CC53'), ('down+A pressed', 0x84, 0x84, 'CBBD'),
                   ('idle', 0x00, 0x00, 'CBEF'))


def ground_walk(ram, bank7):
    cpu = walk(bytes(ram), bank7)
    pcs = [pc for pc, _ in cpu.trace]
    branch = next((name for name, _ in GROUND_BRANCHES if int(name, 16) in pcs), 'other')
    pre = sum(c for _, c in cpu.trace[:pcs.index(0xCBE1)]) if 0xCBE1 in pcs else None
    return cpu, branch, pre


def ground00(rom_path, phases_path, buf_path, input_path, first, last, out):
    rom = open(rom_path, 'rb').read()
    if hashlib.sha256(rom).hexdigest() != ROM_SHA:
        sys.exit('ROM SHA-256 mismatch')
    bank7 = rom[16 + 16384 * (rom[4] - 1):16 + 16384 * rom[4]]
    inp = open(input_path, 'rb').read()
    if first < 2 or last >= len(inp):
        sys.exit('range needs a previous input byte and a next RAM record')
    pts = {}
    with open(phases_path) as f:
        next(f)
        for line in f:
            p, c, pc = line.rstrip().split(',')
            p = int(p)
            if first <= p <= last and pc in ('CA40', 'CA59'):
                pts.setdefault(p, {}).setdefault(pc, int(c))
    rows, controls = [], []
    with open(buf_path, 'rb') as buf:
        def rec(p):
            buf.seek((p - 1) * RECORD)
            r = bytearray(buf.read(2048))
            if len(r) != 2048:
                sys.exit('missing record for poll %d' % p)
            return r
        for p in range(first, last + 1):
            ram, post = rec(p), rec(p + 1)
            if ram[0x50] != 0x00 or not ram[0x4D0] & 0x80:
                continue
            held = inp[p - 1]; pressed = held & ~inp[p - 2] & 0xFF
            ram[0x16], ram[0x14] = held, pressed
            row = {'poll': p, 'held': '%02X' % held, 'pressed': '%02X' % pressed,
                   'input_equal': post[0x16] == held and post[0x14] == pressed}
            try:
                cpu, branch, pre = ground_walk(ram, bank7)
            except Unsupported as e:
                row['status'] = 'UNSUPPORTED %s' % e
                rows.append(row)
                continue
            row.update(source=cpu.cyc, branch=branch, pre_cbe1=pre)
            if pre is not None:
                row['tail_cbe1'] = cpu.cyc - pre
            post_keys = (0x50, 0x420, 0x410, 0x490)
            row['post_diff'] = {'%04X' % k: ['%02X' % cpu.ram[k], '%02X' % post[k]]
                                for k in post_keys if cpu.ram[k] != post[k]}
            m = pts.get(p, {})
            if not row['input_equal']:
                row['status'] = 'INPUT_MISMATCH'
            elif 'CA40' in m and 'CA59' in m and ram[0x65] < 0x80 and ram[0x30] == 0:
                row['oracle'] = m['CA59'] - m['CA40'] - 18
                row['status'] = verdict(cpu.cyc, row['oracle'])
            else:
                row['status'] = 'NO_ORACLE'
            rows.append(row)
            if branch != 'CC44':
                continue
            for name, h, pr, want in GROUND_VARIANTS:
                r = bytearray(ram); r[0x16], r[0x14] = h, pr
                try:
                    _, got, vpre = ground_walk(r, bank7)
                    price = walk(bytes(r), bank7).cyc
                except Unsupported as e:
                    got, vpre, price = 'UNSUPPORTED', None, str(e)
                controls.append({'poll': p, 'variant': name, 'held': '%02X' % h,
                                 'pressed': '%02X' % pr, 'branch': got, 'want': want,
                                 'source': price, 'pre_cbe1': vpre, 'ok': got == want})
            controls.append({'poll': p, 'variant': 'comparator rejects source+1',
                             'ok': verdict(cpu.cyc, cpu.cyc + 1) == 'MISMATCH'})
    for r in rows:
        print(json.dumps(r, sort_keys=True))
    for c in controls:
        print('control', json.dumps(c, sort_keys=True))
    tally = {}
    for r in rows:
        k = (r.get('branch', '-'), r['status'].split()[0] if r['status'].startswith('UNSUPPORTED')
             else r['status'])
        tally['%s/%s' % k] = tally.get('%s/%s' % k, 0) + 1
    unsupported = {}
    for r in rows:
        if r['status'].startswith('UNSUPPORTED'):
            unsupported[r['status']] = unsupported.get(r['status'], 0) + 1
    cc44 = [r for r in rows if r.get('branch') == 'CC44']
    tails = sorted({r['tail_cbe1'] for r in cc44})
    ok = (not any(r['status'] == 'MISMATCH' for r in rows) and cc44
          and all(r['status'] == 'EXACT' for r in cc44) and all(c['ok'] for c in controls))
    summary = {'tally': tally, 'unsupported': unsupported, 'cc44_polls': [r['poll'] for r in cc44],
               'cc44_tail_cbe1': tails, 'controls': len(controls),
               'controls_failed': sum(not c['ok'] for c in controls), 'verdict': 'PASS' if ok else 'FAIL'}
    print('summary', json.dumps(summary, sort_keys=True))
    if out:
        with open(out, 'x') as f:
            json.dump({'rows': rows, 'controls': controls, 'summary': summary}, f, indent=1,
                      sort_keys=True)
    return 0 if ok else 1


def main(argv):
    if len(argv) >= 8 and argv[1] == '--ground00':
        # --ground00 ROM PHASES BUFFERS INPUT FIRST LAST [--json OUT]
        out = argv[argv.index('--json') + 1] if '--json' in argv else None
        return ground00(argv[2], argv[3], argv[4], argv[5], int(argv[6]), int(argv[7]), out)
    if len(argv) == 4 and argv[1] == '--selftest':
        rom = open(argv[2], 'rb').read()
        if hashlib.sha256(rom).hexdigest() != ROM_SHA:
            sys.exit('ROM SHA-256 mismatch')
        return selftest(rom[16 + 16384 * (rom[4] - 1):16 + 16384 * rom[4]], argv[3])
    if argv[1] == '--dump-cases':
        # --dump-cases ROM ORIGINAL_BUFFERS OUT_JSON [CAP] [SEED] [POOL]
        rest = argv[2:]
        cap = int(rest[3]) if len(rest) > 3 else 48
        seed = int(rest[4]) if len(rest) > 4 else 47
        pool = int(rest[5]) if len(rest) > 5 else 4000
        if os.path.exists(rest[2]):
            sys.exit('fixture exists; --dump-cases will not overwrite: ' + rest[2])
        return emit_cases(rest[0], rest[1], rest[2], cap, seed, pool)
    out = None
    if '--json' in argv:
        i = argv.index('--json'); out = argv[i + 1]; argv = argv[:i] + argv[i + 2:]
    rom_path, phases_path, buf_path, first, last = argv[1], argv[2], argv[3], int(argv[4]), int(argv[5])
    rom = open(rom_path, 'rb').read()
    if hashlib.sha256(rom).hexdigest() != ROM_SHA:
        sys.exit('ROM SHA-256 mismatch')
    prg = rom[4]
    bank7 = rom[16 + 16384 * (prg - 1):16 + 16384 * prg]
    pts = {}
    with open(phases_path) as f:
        next(f)
        for line in f:
            p, c, pc = line.rstrip().split(',')
            p = int(p)
            if first <= p <= last and pc in ('CA40', 'CA59'):
                pts.setdefault(p, {}).setdefault(pc, int(c))
    rows = []
    with open(buf_path, 'rb') as buf:
        for p in range(first, last + 1):
            buf.seek((p - 1) * RECORD)
            ram = buf.read(2048)
            buf.seek(p * RECORD)
            post = buf.read(2048)
            if len(ram) != 2048:
                sys.exit('missing record for poll %d' % p)
            if ram[0x50] != 0x09 or not ram[0x4D0] & 0x80:
                rows.append({'poll': p, 'state': ram[0x50], 'status': 'NOT_STATE09'})
                continue
            row = {'poll': p, 'vel': '%02X' % ram[0x560], 'land': ram[0x4D0] & 0x0F}
            try:
                cpu = walk(ram, bank7)
                row['source'] = cpu.cyc
                row['segments'] = segments(cpu.trace)
                row['class10'] = cpu.ram[0x10]
                row['path'] = ['%04X' % pc for pc, _ in cpu.trace
                               if pc in (0xD525, 0xD539, 0xEBC8, 0xEBDB, 0xEC62, 0xEC65, 0xECA0,
                                         0xEC9D, 0xF4F3, 0xF4FB, 0xD530, 0xF85C, 0xF866,
                                         0xF5BB, 0xF5F1, 0xEDE3, 0xEC2D, 0xEC47, 0xEC36)]
                post_keys = (0x510, 0x520, 0x550, 0x560, 0x4D0)
                row['post_equal'] = all(cpu.ram[k] == post[k] for k in post_keys)
                row['post_diff'] = {'%04X' % k: ['%02X' % cpu.ram[k], '%02X' % post[k]]
                                    for k in post_keys if cpu.ram[k] != post[k]}
            except Unsupported as e:
                row['status'] = 'UNSUPPORTED %s' % e
                rows.append(row)
                continue
            m = pts.get(p, {})
            if 'CA40' in m and 'CA59' in m and ram[0x65] < 0x80 and ram[0x30] == 0:
                row['oracle'] = m['CA59'] - m['CA40'] - 18
                row['status'] = verdict(row['source'], row['oracle'])
            else:
                row['status'] = 'NO_ORACLE'
            rows.append(row)
    for r in rows:
        print(json.dumps(r, sort_keys=True))
    tally = {}
    for r in rows:
        k = r['status'].split()[0]
        tally[k] = tally.get(k, 0) + 1
    print('summary', json.dumps(tally, sort_keys=True))
    if out:
        with open(out, 'x') as f:
            json.dump({'rows': rows, 'summary': tally}, f, indent=1, sort_keys=True)
    return 0 if set(tally) <= {'EXACT', 'NOT_STATE09'} else 1


if __name__ == '__main__':
    sys.exit(main(sys.argv))
