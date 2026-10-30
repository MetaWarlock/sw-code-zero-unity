"""Independent bank-7 source price of the CAFC player dispatch for state 09 (ZERO-44).

Walks the original fixed bank 7 instructions from CAFC to the RTS that returns to CA43,
reading only the pre-pass original RAM record of the poll and bank-7 ROM bytes, and sums
NMOS 6502 cycles (page-cross reads, taken/crossing branches). Any access outside RAM
$0000-$07FF or bank 7 $C000-$FFFF, or an unlisted opcode, stops the poll as UNSUPPORTED:
the walk is restricted to the reached dispatch slices and is not a general emulator.

The measured original price (CA59 - CA40 - 18, valid when $65 >= 0 and $30 == 0) is read
only AFTER the walk, for comparison. Nothing here is used by the game runtime.

usage: state09_source_price.py ROM CITY_PHASES_CSV ORIGINAL_BUFFERS FIRST LAST [--json OUT]
       state09_source_price.py --selftest ROM ORIGINAL_BUFFERS
       state09_source_price.py --dump-cases ROM ORIGINAL_BUFFERS OUT_JSON [CAP] [SEED] [POOL]
       state09_source_price.py --ground00 ROM CITY_PHASES_CSV ORIGINAL_BUFFERS INPUT_BIN FIRST LAST [--json OUT]
       state09_source_price.py --airborne0b ROM CITY_PHASES_CSV ORIGINAL_BUFFERS INPUT_BIN FIRST LAST [--json OUT]
       state09_source_price.py --ccea ROM CITY_PHASES_CSV ORIGINAL_BUFFERS INPUT_BIN FIRST LAST [--focus P,..] [--mutant NAME] [--json OUT]
       state09_source_price.py --ccea-pair ROM ORIGINAL_BUFFERS OUT_JSON [POLL]

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


def walk(ram, bank7, events=None, marks=None):
    """Run the dispatch. When `events` is a list, record every return from the real $EBE8
    point routine: the point index it was entered with, the class it computed, and the probe
    coordinates it left in its scratch ($03/$06 = X, $07/$08 = Y). Everything in an event comes
    from executed bank-7 bytes, nothing from a transcription.

    `marks` (ZERO-64, opt-in so the older modes' outputs stay byte-identical) is a list that
    receives (trace index, pc, $10, Y, $04, player X) at $EC91 (the $0600,Y map read) and at $CCEA."""
    cpu = Cpu(ram, bank7)
    cpu.push(0xCA); cpu.push(0x42)  # JSR $CAFC from CA40 returns to CA43
    base_s = cpu.s
    pc = 0xCAFC
    inside = None  # (entry stack pointer, point index) while $EBE8 is running
    for _ in range(5000):
        if pc == 0xCA43 and cpu.s == (base_s + 2) & 0xFF:
            return cpu
        if marks is not None and pc in (0xEC91, 0xCCEA):
            marks.append((len(cpu.trace), pc, cpu.ram[0x10], cpu.y, cpu.ram[0x04],
                          cpu.ram[0x500] << 8 | cpu.ram[0x4F0]))
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


# ---------------------------------------------------------------------------
# ZERO-56 --airborne0b: the reached D626 slice, especially D77C right steering.
# The CLI adds the current recorded input exactly as --ground00 does. Route rows
# compare against CA59-CA40-18 only after walk(); synthetic controls never supply
# an original price and are labelled separately from the measured route.
# ---------------------------------------------------------------------------

AIRBORNE_POST_FIELDS = (0x50, 0x52, 0x400, 0x4D0, 0x470, 0x4F0, 0x500,
                        0x510, 0x520, 0x460, 0x550, 0x560, 0xDA)
AIRBORNE_PATH = (0xD626, 0xD6FB, 0xD756, 0xD76E, 0xD774, 0xD77C, 0xD780,
                 0xD787, 0xD78B, 0xD795, 0xD79A, 0xD79E, 0xD7B6,
                 0xF200, 0xF254, 0xF4FD, 0xF51B, 0xF612, 0xF724,
                 0xED85, 0xED9D, 0xF850, 0xF85C, 0xF866, 0xCE39)
AIRBORNE_VARIANTS = (
    ('idle', 0x00, 0x00, {}, 'D79A idle'),
    ('right', 0x01, 0x00, {}, 'D77C right'),
    ('left', 0x02, 0x00, {}, 'D795 left'),
    ('right+left uses right', 0x03, 0x00, {}, 'D77C right'),
    ('checkpoint veto flags50', 0x01, 0x00, {0x6D: 0x0E, 0x400: 0x50}, 'D77C veto'),
    ('checkpoint veto flags10', 0x01, 0x00, {0x6D: 0x0E, 0x400: 0x10}, 'D77C veto'),
    ('flags50 without checkpoint0E', 0x01, 0x00, {0x400: 0x50}, 'D77C right'),
    ('checkpoint0E without veto flags', 0x01, 0x00, {0x6D: 0x0E}, 'D77C right'),
)


def airborne0b_walk(ram, bank7):
    events = []
    cpu = walk(bytes(ram), bank7, events)
    pcs = [pc for pc, _ in cpu.trace]
    if 0xD6FB in pcs:
        branch = 'D6FB land'
    elif 0xD77C in pcs:
        branch = 'D77C veto' if 0xF200 not in pcs else 'D77C right'
    elif 0xD795 in pcs:
        branch = 'D795 left'
    else:
        branch = 'D79A idle'
    result = {'branch': branch,
              'path': ['%04X' % pc for pc in pcs if pc in AIRBORNE_PATH],
              'probes': events}
    if 0xD76E not in pcs:
        return cpu, result
    i = pcs.index(0xD76E)
    result['pre_d76e'] = sum(c for _, c in cpu.trace[:i])
    if 0xD79A in pcs:
        j = pcs.index(0xD79A)
        result['direction_block'] = sum(c for _, c in cpu.trace[i:j])
        result['shot_tail'] = sum(c for _, c in cpu.trace[j:])
        horizontal_pc = 0xF200 if 0xF200 in pcs else (0xF254 if 0xF254 in pcs else None)
        if horizontal_pc is not None:
            k = pcs.index(horizontal_pc)
            end = j - 1 if horizontal_pc == 0xF200 and pcs[j - 1] == 0xD792 else j
            horizontal_trace = cpu.trace[k:end]
            result['horizontal'] = sum(c for _, c in horizontal_trace)
            integrate_range = ((0xF4FD, 0xF51A) if horizontal_pc == 0xF200 else (0xF51B, 0xF538))
            integrate = sum(c for pc, c in horizontal_trace if integrate_range[0] <= pc <= integrate_range[1])
            pair = sum(c for pc, c in horizontal_trace
                       if 0xF612 <= pc <= 0xF647 or 0xF699 <= pc <= 0xF74D)
            probe = sum(c for pc, c in horizontal_trace
                        if 0xEB52 <= pc <= 0xEBC8 or 0xEBE8 <= pc <= 0xECA6)
            result['horizontal_parts'] = {'integrate': integrate, 'pair': pair, 'probe': probe,
                                          'other': result['horizontal'] - integrate - pair - probe}
    else:
        result['direction_block'] = sum(c for _, c in cpu.trace[i:])
        result['shot_tail'] = 0
    return cpu, result


def airborne0b(rom_path, phases_path, buf_path, input_path, first, last, out):
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
            if ram[0x50] != 0x0B or not ram[0x4D0] & 0x80:
                continue
            held = inp[p - 1]
            pressed = held & ~inp[p - 2] & 0xFF
            ram[0x16], ram[0x14] = held, pressed
            row = {'poll': p, 'held': '%02X' % held, 'pressed': '%02X' % pressed,
                   'input_equal': post[0x16] == held and post[0x14] == pressed,
                   'checkpoint': '%02X' % ram[0x6D], 'flags': '%02X' % ram[0x400]}
            try:
                cpu, info = airborne0b_walk(ram, bank7)
            except Unsupported as e:
                row['status'] = 'UNSUPPORTED %s' % e
                rows.append(row)
                continue
            row.update(source=cpu.cyc, **info)
            row['post_equal'] = all(cpu.ram[k] == post[k] for k in AIRBORNE_POST_FIELDS)
            row['post_diff'] = {'%04X' % k: ['%02X' % cpu.ram[k], '%02X' % post[k]]
                                for k in AIRBORNE_POST_FIELDS if cpu.ram[k] != post[k]}
            if 0xF850 in [pc for pc, _ in cpu.trace]:
                row['queue'] = {'owner_pre': audio_of(ram),
                                'enqueued': 0xF85C in [pc for pc, _ in cpu.trace]}
            m = pts.get(p, {})
            if not row['input_equal']:
                row['status'] = 'INPUT_MISMATCH'
            elif 'CA40' in m and 'CA59' in m and ram[0x65] < 0x80 and ram[0x30] == 0:
                row['oracle'] = m['CA59'] - m['CA40'] - 18
                row['status'] = verdict(cpu.cyc, row['oracle'])
            else:
                row['status'] = 'NO_ORACLE'
            rows.append(row)
            if info['branch'] == 'D6FB land':
                r = bytearray(ram)
                set_audio(r, 'busy')
                try:
                    vcpu, vinfo = airborne0b_walk(r, bank7)
                    busy_pcs = [pc for pc, _ in vcpu.trace]
                    price = vcpu.cyc
                    ok = price == cpu.cyc - 15 and 0xF85C not in busy_pcs and 0xF866 in busy_pcs
                except Unsupported as e:
                    price, ok = str(e), False
                controls.append({'poll': p, 'kind': 'synthetic', 'variant': 'landing queue busy',
                                 'source': price, 'delta': price - cpu.cyc if isinstance(price, int) else None,
                                 'ok': ok})
                continue
            if info['branch'] != 'D77C right':
                continue
            for name, h, pr, edits, want in AIRBORNE_VARIANTS:
                r = bytearray(ram)
                r[0x16], r[0x14] = h, pr
                for address, value in edits.items():
                    r[address] = value
                try:
                    vcpu, vinfo = airborne0b_walk(r, bank7)
                    got, price = vinfo['branch'], vcpu.cyc
                except Unsupported as e:
                    got, price = 'UNSUPPORTED', str(e)
                controls.append({'poll': p, 'kind': 'synthetic', 'variant': name,
                                 'held': '%02X' % h, 'pressed': '%02X' % pr,
                                 'branch': got, 'want': want, 'source': price,
                                 'ok': got == want})
            controls.append({'poll': p, 'kind': 'comparator',
                             'variant': 'comparator rejects source+1',
                             'ok': verdict(cpu.cyc, cpu.cyc + 1) == 'MISMATCH'})
    for r in rows:
        print(json.dumps(r, sort_keys=True))
    for c in controls:
        print('control', json.dumps(c, sort_keys=True))
    right = [r['poll'] for r in rows if r.get('branch') == 'D77C right']
    landing = [r['poll'] for r in rows if r.get('branch') == 'D6FB land']
    route_ok = bool(rows) and all(r['status'] == 'EXACT' and r['input_equal'] and r['post_equal']
                                  for r in rows)
    ok = route_ok and bool(right) and all(c['ok'] for c in controls)
    summary = {'rows': len(rows), 'right_polls': right, 'landing_polls': landing,
               'right_prices': [r['source'] for r in rows if r['poll'] in right],
               'controls': len(controls),
               'controls_failed': sum(not c['ok'] for c in controls),
               'route_exact': sum(r['status'] == 'EXACT' for r in rows),
               'verdict': 'PASS' if ok else 'FAIL'}
    print('summary', json.dumps(summary, sort_keys=True))
    if out:
        with open(out, 'x') as f:
            json.dump({'rows': rows, 'controls': controls, 'summary': summary}, f, indent=1,
                      sort_keys=True)
    return 0 if ok else 1


# ---------------------------------------------------------------------------
# ZERO-64 --ccea: the reached horizontal side probe $CCBA -> $F200/$F254 -> $CCEA.
# State 01 reaches it from $CD18 (any held left/right), ground walking from $CCB4.
# $F200/$F254 integrate X ($F4FD/$F51B), try the actor pair ($F612; its $F699 uses
# SBC abs,Y, outside the walk), probe three side points through $EB52 (right 0..2,
# left 3..5), push back by ($06 & $0F) through $ED85/$ED9D when the class is >= 3,
# set $0400 bit6 and return; $CCEA then turns class 4 with $7C == 0 into state 06.
# Same walk(), same input convention as --ground00; the original price is read only
# after the walk. Synthetic controls edit the real map cells the walk itself read
# and carry expectations derived from the listing, not from the walk.
# ---------------------------------------------------------------------------

CCEA_POST_HARD = (0x50, 0x4F0, 0x500, 0x470, 0x510, 0x520, 0x560, 0x550, 0x460)
CCEA_POST_SOFT = (0x400, 0x10, 0x52, 0x56, 0x7C, 0x51, 0xDA, 0x6D)
# EB52 per-point bookkeeping for X = 0, hand-counted from EB6E..EB9A: the update block
# (BCC not taken, STA $10, CPX/BNE, $4C/$4B, $4A) is 45 cycles plus the $08 vs $FB tail
# (BCC taken 3 / BEQ taken 5 / +$2E add 15); a point whose class is below the running
# maximum takes BCC $EB9A for 3. An all-empty side therefore updates on every point.
EB52_UPDATE = 45
EB52_SKIP = 3
# EBAA..EBBC exit for X = 0 is 25 cycles; class 6 (BEQ $EBE3, $10 := 0) costs 27 and
# class 7 (BEQ $EBDB, $0400 |= $20, $10 := 0) costs 42. Class 5 reads ($34),Y: not built.
EB52_EXIT = {6: 2, 7: 17}
# F242 BCC taken (3) -> not taken (2) + JSR $ED85 (6) + ED85 body to RTS (39) = +44.
ED85_EXTRA = 44
# F2A1 BCC taken (3) -> not taken (2) + JSR $ED9D (6) + ED9D body to RTS (35) = +40.
ED9D_EXTRA = 40
# CCEA..CD0D when not hurt: LDA zp 3, CMP 2, BNE $CD0D taken across the page CCF0->CD0D 4,
# RTS 6 = 15; class 4 with $7C != 0: BNE not taken 2, LDA zp 3, BNE CCF4->CD0D crossing 4 = 20.
CCEA_TAIL = 15
CCEA_TAIL_TIMER = 20
# --mutant NAME: one deliberately wrong listing assumption; the controls must then FAIL.
CCEA_MUTANTS = {
    'tail-no-page-cross': lambda g: g.update(CCEA_TAIL=14, CCEA_TAIL_TIMER=19),
    'update-46': lambda g: g.update(EB52_UPDATE=46),
    'ed85-43': lambda g: g.update(ED85_EXTRA=43),
    'ed9d-as-ed85': lambda g: g.update(ED9D_EXTRA=44),
    'class7-exit-as-6': lambda g: g.update(EB52_EXIT={6: 2, 7: 2}),
    'equal-class-skips': lambda g: g.update(SKIP_ON_EQUAL=True),
}
SKIP_ON_EQUAL = False


# Read opcodes the reached CCEA polls need beyond the older slices: $F6A4 SBC $04F0,Y in the
# $F699 actor-pair distance, $CDAE CMP $0420 and $CF55 CMP $CFD3,Y. They are enabled only
# by --ccea so the earlier modes keep their recorded UNSUPPORTED rows byte for byte.
CCEA_OPS = {0xF9: ('SBC', 'absy'), 0xCD: ('CMP', 'abs'), 0xD9: ('CMP', 'absy')}


def row_tail(y_high, fb):
    return 3 if y_high < fb else (5 if y_high == fb else 15)


def ccea_walk(ram, bank7):
    events, marks = [], []
    cpu = walk(bytes(ram), bank7, events, marks)
    pcs = [pc for pc, _ in cpu.trace]
    if 0xCCEA not in pcs:
        return cpu, None
    k_ccea = pcs.index(0xCCEA)
    k_side = max(i for i in range(k_ccea) if pcs[i] in (0xF200, 0xF254))
    k_ret = next(i for i in range(k_side, len(pcs)) if pcs[i] in (0xCCDC, 0xCCEA))
    k_end = next(i for i in range(k_ccea, len(pcs)) if pcs[i] == 0xCD0D)
    entries = [i for i, pc in enumerate(pcs) if pc == 0xEBE8]
    reads = {k: (y, d04) for k, pc, _, y, d04, _ in marks if pc == 0xEC91}
    side = []
    for n, (entry, event) in enumerate(zip(entries, events)):
        if not k_side <= entry < k_ret:
            continue
        stop = entries[n + 1] if n + 1 < len(entries) else len(pcs)
        window = pcs[entry:stop]
        read = next(((k, v) for k, v in reads.items() if entry <= k < stop), None)
        cell = nibble = None
        if read:
            cell = 0x600 + read[1][0]
            nibble = 'low' if bank7[0xECA9 - 0xC000 + read[1][1]] else 'high'
        side.append({'point': event['point'], 'class': event['class'], 'x': event['x'],
                     'y': event['y'], 'read': event['read'], 'cell': cell, 'nibble': nibble,
                     'update': 0xEB72 in window, 'row_tail': row_tail(event['y'] >> 8, ram[0xFB])})
    side_trace = cpu.trace[k_side:k_ret]
    parts = {'integrate': 0, 'pair': 0, 'probe': 0, 'push': 0}
    for pc, c in side_trace:
        if 0xF4FD <= pc <= 0xF538:
            parts['integrate'] += c
        elif 0xF612 <= pc <= 0xF647 or 0xF699 <= pc <= 0xF7FF:
            parts['pair'] += c
        elif 0xEB52 <= pc <= 0xECA6:
            parts['probe'] += c
        elif 0xED85 <= pc <= 0xEDB2:
            parts['push'] += c
    total_side = sum(c for _, c in side_trace)
    parts['other'] = total_side - sum(parts.values())
    window = set(pcs[k_side:k_ret])
    info = {
        'caller': 'CD18 air01' if 0xCD18 in pcs[:k_side] else ('CCB4 ground' if 0xCCB4 in pcs[:k_side] else 'other'),
        'direction': 'right F200' if pcs[k_side] == 0xF200 else 'left F254',
        'side': side, 'side_max': next(v for k, pc, v, _, _, _ in marks if pc == 0xCCEA),
        'x_at_ccea': next(x for k, pc, _, _, _, x in marks if pc == 0xCCEA),
        'pre_side': sum(c for _, c in cpu.trace[:k_side]),
        'side_cycles': total_side, 'side_parts': parts,
        'between': sum(c for _, c in cpu.trace[k_ret:k_ccea]),
        'ccea_tail': sum(c for _, c in cpu.trace[k_ccea:k_end + 1]),
        'after_ccea': sum(c for _, c in cpu.trace[k_end + 1:]),
        'actor_pair_hit': 0xF210 in window or 0xF264 in window,
        'push': 'ED85' if 0xED85 in window else ('ED9D' if 0xED9D in window else None),
        'hurt': 0xCCF4 in pcs,
        'edge_flip': 0xF235 in window or 0xF290 in window,
        'eb_exit': 'EBDB' if 0xEBDB in window else ('EBE3' if 0xEBE3 in window else 'EBBC'),
        'skipped': [s['point'] for s in side if not s['update']],
    }
    # The priced CCEA slice: the side call (from its first instruction to the return), the
    # right-hand JMP $CCEA and the CCEA..CD0D tail. Hurt rows continue into CCF4..F9C2.
    info['slice'] = total_side + info['between'] + info['ccea_tail']
    return cpu, info


def ccea_expect(side, edits_classes, fb):
    """Listing expectation for the side point classes: which points take the update block,
    whether the push and the hurt test run, and the probe delta from an all-update set."""
    running, skipped, delta = 0, [], 0
    for s, cls in zip(side, edits_classes):
        if cls > running or (cls == running and not (SKIP_ON_EQUAL and cls)):
            running = cls
        else:
            skipped.append(s['point'])
            delta -= EB52_UPDATE + row_tail(s['y'] >> 8, fb) - EB52_SKIP
    delta += EB52_EXIT.get(running, 0)
    return {'skipped': skipped, 'probe_delta': delta, 'max': running,
            'side_max': 0 if running in (6, 7) else running}


def set_class(ram, s, cls):
    if s['nibble'] == 'low':
        ram[s['cell']] = (ram[s['cell']] & 0xF0) | cls
    else:
        ram[s['cell']] = (ram[s['cell']] & 0x0F) | cls << 4


def ccea_controls(p, ram, bank7, cpu, info):
    """Synthetic controls on one CCEA poll. Every expectation is written before the walk
    from the listing (CCBA..CD0D, F200/F254, EB52..EBE7, ED85/ED9D, CD0E..CD3E); the walk
    only has to agree. None of these rows are original-route evidence."""
    out = []

    def run(r):
        try:
            c, i = ccea_walk(r, bank7)
            return c, i, None
        except Unsupported as e:
            return None, None, str(e)

    def add(kind, name, ok, **extra):
        row = {'poll': p, 'kind': kind, 'variant': name, 'ok': bool(ok)}
        row.update(extra)
        out.append(row)

    def geometry(ram, info, tag):
        """Class patterns on the three map cells this walk's side probes read."""
        side = info['side']
        cells = [(s['cell'], s['nibble']) for s in side]
        distinct = all(s['read'] for s in side) and len(set(cells)) == len(cells)
        fb = ram[0xFB]
        base_classes = [s['class'] for s in side]
        # --- geometry: class patterns on the same three map cells the route poll read ---
        if distinct:
            zero = bytearray(ram)
            for s in side:
                set_class(zero, s, 0)
            zc, zi, ze = run(zero)
            add('synthetic-geometry', tag + 'side 000 (all update)',
                zi is not None and zi['skipped'] == [] and zi['side_max'] == 0 and zi['push'] is None,
                source=zc.cyc if zc else ze)
            if zi is not None:
                patterns = [(1, 0, 0), (0, 1, 0), (0, 0, 1), (2, 1, 0), (1, 2, 0), (2, 0, 0),
                            (3, 0, 0), (0, 0, 3), (4, 0, 0), (7, 0, 0), (6, 0, 0)]
                if tuple(base_classes) not in patterns:
                    patterns.insert(0, tuple(base_classes))
                # The $7C damage timer only matters for class 4: keep it nonzero for the price rows
                # so the hurt path stays an explicit owner control below.
                timer = ram[0x7C] if ram[0x7C] else 0x3C
                # Points 0..2 are the right set (push ED85), 3..5 the left set (ED9D); an
                # edge flip (F235/F290) probes the other set, so the set decides, not $16.
                right = side[0]['point'] < 3
                for pat in patterns:
                    r = bytearray(zero)
                    for s, cls in zip(side, pat):
                        set_class(r, s, cls)
                    r[0x7C] = timer
                    exp = ccea_expect(side, pat, fb)
                    c, i, err = run(r)
                    name = 'side %d%d%d' % pat
                    if i is None:
                        add('synthetic-geometry', tag + name, False, source=err)
                        continue
                    got = [s['class'] for s in i['side']]
                    push = exp['max'] in (3, 4)
                    # Listing expectation for the slice: all-update base, minus the skipped
                    # points, plus the EBAA exit, the F242 push call and the CCEA timer tail.
                    want = (zi['slice'] + exp['probe_delta'] + ((ED85_EXTRA if right else ED9D_EXTRA) if push else 0)
                            + (CCEA_TAIL_TIMER - CCEA_TAIL if exp['max'] == 4 else 0))
                    ok = got == list(pat) and i['skipped'] == exp['skipped']
                    ok &= i['side_max'] == exp['side_max'] and not i['hurt']
                    ok &= (i['push'] == ('ED85' if right else 'ED9D')) if push else i['push'] is None
                    ok &= i['slice'] == want
                    ok &= i['ccea_tail'] == (CCEA_TAIL_TIMER if exp['max'] == 4 else CCEA_TAIL)
                    # Class 7 sets $0400 bit5 at EBDB; the later vertical EB52 clears it again
                    # (EB58), so the proof is the executed EBDB, not the final RAM byte.
                    ok &= (i['eb_exit'] == {6: 'EBE3', 7: 'EBDB'}.get(exp['max'], 'EBBC'))
                    add('synthetic-geometry', tag + name, ok, classes=got, skipped=i['skipped'],
                        want_skipped=exp['skipped'], slice=i['slice'],
                        want_slice=want,
                        slice_vs_000=i['slice'] - zi['slice'], source=c.cyc, push=i['push'],
                        side_max=i['side_max'], x_post='%02X%02X' % (c.ram[0x500], c.ram[0x4F0]))
                # Class 4 with $7C == 0: CCF4 sets $50=06/$51=0F, FA78 animation 09, F850 sound $15,
                # $7C=3C, F9C2 saves $58=06; in state 01 CE22..CE36 then writes $50 back to 01
                # (06 is not 04/0E/0D/07). A busy queue skips F85C..F864: 15 cycles cheaper.
                hurt = {}
                for owner in ('accepts', 'busy'):
                    r = bytearray(zero)
                    set_class(r, side[0], 4)
                    r[0x7C] = 0
                    set_audio(r, owner)
                    c, i, err = run(r)
                    name = 'side 400, $7C=0, queue %s (hurt)' % owner
                    if i is None:
                        add('synthetic-owner', tag + name, False, source=err)
                        continue
                    pcs = [pc for pc, _ in c.trace]
                    enq = pcs.count(0xF85C)
                    hurt[owner] = (c.cyc, enq)
                    # A poll that also lands (CE08..CE19) queues sound $10 after the hurt $15 and
                    # writes $50 = 00; otherwise state 01 is restored. The edited cell can also be
                    # under the feet, so landing is read from the same map with $7C = 3C.
                    same_map = bytearray(r)
                    same_map[0x7C] = 0x3C
                    landed = 0xCE17 in [pc for pc, _ in walk(bytes(same_map), bank7).trace]
                    want_state = (0x00 if landed else 0x01) if info['caller'] == 'CD18 air01' else None
                    ok = i['hurt'] and c.ram[0x58] == 0x06 and c.ram[0x7C] == 0x3C and c.ram[0x51] == 0x0F
                    ok &= want_state is None or c.ram[0x50] == want_state
                    ok &= (enq >= 1) if owner == 'accepts' else enq == 0
                    ok &= pcs.count(0xF850) == (2 if landed else 1) or want_state is None
                    add('synthetic-owner', tag + name, ok, source=c.cyc, state='%02X' % c.ram[0x50],
                        saved58='%02X' % c.ram[0x58], timer7C='%02X' % c.ram[0x7C], enqueued=enq,
                        queue_calls=pcs.count(0xF850), landed=landed)
                if len(hurt) == 2:
                    # Each enqueue that a busy slot refuses saves F85C..F864 (15 cycles).
                    delta = hurt['accepts'][0] - hurt['busy'][0]
                    add('synthetic-owner', tag + 'hurt queue busy is 15 cycles cheaper per enqueue',
                        delta == 15 * hurt['accepts'][1], delta=delta, enqueues=hurt['accepts'][1])
        else:
            add('synthetic-geometry', tag + 'side cells not distinct/read: geometry controls not built', True,
                cells=cells)
    geometry(ram, info, '')
    if info['caller'] == 'CD18 air01' and info['direction'] == 'right F200':
        # The route never shows a nonempty left side: build it on the same RAM with Left held.
        left = bytearray(ram)
        left[0x16] = (ram[0x16] & ~3 & 0xFF) | 2
        left[0x14] = ram[0x14] & ~3 & 0xFF
        try:
            _, linfo = ccea_walk(left, bank7)
        except Unsupported as e:
            linfo = None
            add('synthetic-geometry', 'left: walk unsupported', False, source=str(e))
        if linfo is not None:
            geometry(left, linfo, 'left ')
    # Not built (ZERO-64 boundary): the actor pair and edge flip F210..F23C run only when
    # $F612 returns carry set. The route has no such CCEA poll; two synthetic attempts (flip
    # through ($6E,$6F) alone - refuted by F20E BCC $F23F - and slot 1 at dX 2/14 with
    # $04D1/$0401/$04E1 set - F612 still returned carry clear) gave no controlled case, so
    # a native price must refuse a carry-set F612 instead of extrapolating this slice.
    # --- input/owner gates on CCBA..CCE7 and CD0E..CD3E ---
    direction = ram[0x16] & 3
    gates = []
    if info['caller'] == 'CD18 air01':
        gates += [('no direction (A only)', {0x16: ram[0x16] & ~3 & 0xFF, 0x14: 0}, 'no CCEA'),
                  ('right+left uses right', {0x16: (ram[0x16] | 3), 0x14: 0}, 'right F200'),
                  ('left', {0x16: (ram[0x16] & ~3 & 0xFF) | 2, 0x14: 0}, 'left F254'),
                  ('right', {0x16: (ram[0x16] & ~3 & 0xFF) | 1, 0x14: 0}, 'right F200'),
                  ('$9E bit7 blocks', {0x9E: 0x80}, 'no CCEA'),
                  ('fresh B -> CF3A', {0x14: 0x40, 0x16: ram[0x16] | 0x40}, 'CF3A')]
        if ram[0x50] == 0x01 and ram[0x560] & 0x80:  # a state-00 jump poll reads A at CBB7
            gates.append(('A released while falling: same price', {0x16: ram[0x16] & 0x7F, 0x14: 0},
                          'same'))
    if direction & 1:
        gates += [('checkpoint 0E + flags 50 veto', {0x6D: 0x0E, 0x400: 0x50}, 'no CCEA'),
                  ('checkpoint 0E + flags 10 veto', {0x6D: 0x0E, 0x400: 0x10}, 'no CCEA'),
                  ('checkpoint 0E, route flags', {0x6D: 0x0E}, 'right F200' if ram[0x400] not in (0x50, 0x10)
                   else 'no CCEA')]
    for name, edits, want in gates:
        r = bytearray(ram)
        for a, v in edits.items():
            r[a] = v
        c, i, err = run(r)
        if err:
            got = 'unsupported %s' % err
        elif want == 'CF3A':
            got = 'CF3A' if 0xCF3A in [pc for pc, _ in c.trace] else 'no CF3A'
        elif i is None:
            got = 'no CCEA'
        elif want == 'same':
            got = 'same' if c.cyc == cpu.cyc else 'differs %d' % (c.cyc - cpu.cyc)
        else:
            got = i['direction']
        add('synthetic-input', name, got == want, want=want, got=got,
            source=c.cyc if c else err)
    add('comparator', 'comparator rejects source+1', verdict(cpu.cyc, cpu.cyc + 1) == 'MISMATCH')
    return out


def ccea(rom_path, phases_path, buf_path, input_path, first, last, out, focus, mutant=None):
    OPS.update(CCEA_OPS)
    if mutant:
        CCEA_MUTANTS[mutant](globals())
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
    rows, controls, unsupported = [], [], {}
    with open(buf_path, 'rb') as buf:
        def rec(p):
            buf.seek((p - 1) * RECORD)
            r = bytearray(buf.read(2048))
            if len(r) != 2048:
                sys.exit('missing record for poll %d' % p)
            return r
        for p in range(first, last + 1):
            ram, post = rec(p), rec(p + 1)
            if not ram[0x4D0] & 0x80 or not inp[p - 1] & 3:
                continue  # $CCBA needs the active player and a held left/right
            held = inp[p - 1]
            pressed = held & ~inp[p - 2] & 0xFF
            ram[0x16], ram[0x14] = held, pressed
            try:
                cpu, info = ccea_walk(ram, bank7)
            except Unsupported as e:
                key = 'state %02X: %s' % (ram[0x50], e)
                unsupported[key] = unsupported.get(key, 0) + 1
                continue
            if info is None:
                continue
            row = {'poll': p, 'state': '%02X' % ram[0x50], 'held': '%02X' % held,
                   'pressed': '%02X' % pressed,
                   'input_equal': post[0x16] == held and post[0x14] == pressed,
                   'source': cpu.cyc}
            row.update(info)
            # A hard field the dispatch never changed but the next record did was written later
            # in the frame, outside CAFC (5313: $50 00 -> 13, $51 = 78); that is reported apart.
            diff = {k for k in CCEA_POST_HARD if cpu.ram[k] != post[k]}
            row['post_diff'] = {'%04X' % k: ['%02X' % cpu.ram[k], '%02X' % post[k]]
                                for k in sorted(diff) if cpu.ram[k] != ram[k]}
            row['post_external'] = {'%04X' % k: ['%02X' % cpu.ram[k], '%02X' % post[k]]
                                    for k in sorted(diff) if cpu.ram[k] == ram[k]}
            # Soft fields are shared scratch/queue/timer bytes that later frame code may rewrite;
            # split them the same way so a value the dispatch itself wrote stays visible.
            soft = [k for k in CCEA_POST_SOFT if cpu.ram[k] != post[k]]
            row['post_soft_diff'] = {'%04X' % k: ['%02X' % cpu.ram[k], '%02X' % post[k]]
                                     for k in soft if cpu.ram[k] != ram[k]}
            row['post_soft_external'] = {'%04X' % k: ['%02X' % cpu.ram[k], '%02X' % post[k]]
                                         for k in soft if cpu.ram[k] == ram[k]}
            m = pts.get(p, {})
            if not row['input_equal']:
                row['status'] = 'INPUT_MISMATCH'
            elif 'CA40' in m and 'CA59' in m and ram[0x65] < 0x80 and ram[0x30] == 0:
                row['oracle'] = m['CA59'] - m['CA40'] - 18
                row['status'] = verdict(cpu.cyc, row['oracle'])
            else:
                row['status'] = 'NO_ORACLE'
            rows.append(row)
            nonempty = row['side_max'] != 0 or any(s['class'] for s in info['side'])
            if p in focus or (nonempty and not focus):
                controls.extend(ccea_controls(p, ram, bank7, cpu, info))
    for r in rows:
        print(json.dumps(r, sort_keys=True))
    for c in controls:
        print('control', json.dumps(c, sort_keys=True))
    nonempty = [r for r in rows if any(s['class'] for s in r['side'])]
    tally = {}
    for r in rows:
        k = '%s/%s/%s' % (r['caller'], 'nonempty' if r in nonempty else 'empty', r['status'])
        tally[k] = tally.get(k, 0) + 1
    by_kind = {}
    for c in controls:
        k = c['kind']
        by_kind.setdefault(k, [0, 0])
        by_kind[k][0] += 1
        by_kind[k][1] += not c['ok']
    route_ok = all(r['status'] in ('EXACT', 'NO_ORACLE') and not r['post_diff'] for r in rows)
    ok = bool(rows) and route_ok and all(c['ok'] for c in controls)
    summary = {'rows': len(rows), 'tally': tally, 'unsupported': unsupported,
               'nonempty_polls': [r['poll'] for r in nonempty],
               'nonempty': [{'poll': r['poll'], 'state': r['state'], 'held': r['held'],
                             'classes': [s['class'] for s in r['side']], 'skipped': r['skipped'],
                             'push': r['push'], 'hurt': r['hurt'], 'source': r['source'],
                             'oracle': r.get('oracle'), 'status': r['status'],
                             'side_cycles': r['side_cycles'], 'side_parts': r['side_parts']}
                            for r in nonempty],
               'mismatch_polls': [r['poll'] for r in rows if r['status'] == 'MISMATCH'],
               'edge_flip_polls': [r['poll'] for r in rows if r['edge_flip']],
               'actor_pair_polls': [r['poll'] for r in rows if r['actor_pair_hit']],
               'left_polls': len([r for r in rows if r['direction'] == 'left F254']),
               'post_external': {r['poll']: r['post_external'] for r in rows if r['post_external']},
               'post_diff_polls': [r['poll'] for r in rows if r['post_diff']],
               'controls': len(controls), 'controls_by_kind': by_kind,
               'controls_failed': sum(not c['ok'] for c in controls),
               'mutant': mutant, 'verdict': 'PASS' if ok else 'FAIL'}
    print('summary', json.dumps(summary, sort_keys=True))
    if out:
        with open(out, 'x') as f:
            json.dump({'rows': rows, 'controls': controls, 'summary': summary}, f, indent=1,
                      sort_keys=True)
    return 0 if ok else 1


# ---------------------------------------------------------------------------
# ZERO-65 --ccea-pair: direct source-instruction fixtures for F612 and the two
# push/edge-flip tails. F612 cannot return carry set: after CMP #$18, every
# accepted A=0..23 has N=1 at F642, so BMI $F646 rejects it. Direct F210/F264
# entries price the otherwise unreachable tails without patching the ROM; they
# are synthetic instruction fixtures, not a reachable actor-pair or route PASS.
# ---------------------------------------------------------------------------

CCEA_PAIR_OPS = {0xF5: ('SBC', 'zpx')}
CCEA_PAIR_REJECTIONS = (0xF619, 0xF61E, 0xF623, 0xF628, 0xF62F, 0xF63E, 0xF642)
CCEA_PAIR_MARKERS = {
    0xF207, 0xF209, 0xF20B, 0xF210, 0xF216, 0xF218, 0xF21B, 0xF228, 0xF233, 0xF235,
    0xF23F, 0xF24B, 0xF253, 0xF264, 0xF26A, 0xF26C, 0xF26F, 0xF27D, 0xF281, 0xF28C,
    0xF28E, 0xF290, 0xF29A, 0xF2A6, 0xF2AE, 0xF612, 0xF616, 0xF619, 0xF61B, 0xF61E,
    0xF620, 0xF623, 0xF625, 0xF628, 0xF62A, 0xF62D, 0xF62F, 0xF631, 0xF639, 0xF63C,
    0xF63E, 0xF640, 0xF642, 0xF644, 0xF645, 0xF646, 0xF647, 0xF699, 0xF69E, 0xF6A0,
    0xF6C6, 0xF6DF, 0xF6E9, 0xF6F0, 0xF6FF, 0xF70B, 0xF718, 0xF722, 0xF723,
}


def ccea_pair_fragment(ram, bank7, entry, a=0, x=0, stops=(0xCA43, 0xF647)):
    cpu = Cpu(ram, bank7)
    cpu.a, cpu.x = a, x
    cpu.push(0xCA)
    cpu.push(0x42)
    pc = entry
    for _ in range(5000):
        if pc in stops:
            return cpu
        old = pc
        pc, cycles = step(cpu, pc)
        cpu.trace.append((old, cycles))
        cpu.cyc += cycles
    raise Unsupported('fragment did not reach its stop within 5000 instructions')


def ccea_pair_fixture(rom_path, buf_path, out_path, poll):
    OPS.update(CCEA_OPS)
    OPS.update(CCEA_PAIR_OPS)
    rom = open(rom_path, 'rb').read()
    buffers = open(buf_path, 'rb').read()
    if hashlib.sha256(rom).hexdigest() != ROM_SHA:
        sys.exit('ROM SHA-256 mismatch')
    bank7 = rom[16 + 16384 * (rom[4] - 1):16 + 16384 * rom[4]]
    start = (poll - 1) * RECORD
    if poll < 1 or start + 2048 > len(buffers):
        sys.exit('missing record for poll %d' % poll)
    base = bytearray(buffers[start:start + 2048])
    cases = []

    def diff(before, after, map_zero=False):
        edits = {'%04X' % a: '%02X' % after[a] for a in range(2048)
                 if before[a] != after[a] and not (map_zero and 0x600 <= a < 0x700)}
        if map_zero:
            edits['0600..06FF'] = '00'
        return edits

    def path_of(cpu):
        return ['%04X' % pc for pc, _ in cpu.trace if pc in CCEA_PAIR_MARKERS]

    def pair_ram(direction, dx, dy, direct, camera_correction):
        ram = bytearray(base)
        px = (ram[0x4F0] | ram[0x500] << 8) + (2 if direction == 'right' else -2)
        py = ram[0x510] | ram[0x520] << 8
        ram[0x4D0] |= 0x80
        ram[0x4D1] = 0x80
        ram[0x400] = (ram[0x400] | 0x80 | (0x10 if direct else 0)) & (0xFF if direct else ~0x10)
        ram[0x401] = 0x80
        ram[0x4E1] = 1
        ox = px + dx if direct else px + dx + (ram[0xFC] if camera_correction else 0)
        oy = py + dy
        for address, value in ((0x4F0, px), (0x4F1, ox), (0x510, py), (0x511, oy)):
            ram[address] = value & 0xFF
        for address, value in ((0x500, px), (0x501, ox), (0x520, py), (0x521, oy)):
            ram[address] = value >> 8 & 0xFF
        return ram

    def add_pair(name, direction='right', dx=2, dy=0, direct=True, camera_correction=True,
                 want='F642', edits=None, required=()):
        ram = pair_ram(direction, dx, dy, direct, camera_correction)
        for address, value in (edits or {}).items():
            ram[address] = value
        cpu = ccea_pair_fragment(ram, bank7, 0xF612)
        pcs = [pc for pc, _ in cpu.trace]
        successors = dict(zip(pcs, pcs[1:]))
        reject = next(('%04X' % pc for pc in CCEA_PAIR_REJECTIONS
                       if successors.get(pc) == 0xF646), None)
        carry_set = 0xF644 in pcs and 0xF645 in pcs
        ok = reject == want and not carry_set and all(pc in pcs for pc in required)
        cases.append({'id': name, 'kind': 'F612 guard', 'entry': 'F612',
                      'edits': diff(base, ram), 'want_reject': want, 'reject': reject,
                      'want_path': ['%04X' % pc for pc in required], 'path': path_of(cpu),
                      'carry_set': carry_set, 'cycles': cpu.cyc, 'ok': ok})

    add_pair('right direct dX=2 dY=0 reaches final BMI', required=(0xF6A0,))
    add_pair('Claude ZERO64 direct dX=14 dY=0 reaches final BMI', dx=14, required=(0xF6A0,))
    add_pair('left direct dX=2 dY=0 reaches final BMI', direction='left', required=(0xF6A0,))
    add_pair('right direct signed dX=-2 dY=-2', dx=-2, dy=-2, required=(0xF6A0,))
    add_pair('right camera-aware dX=2 reaches final BMI', direct=False, required=(0xF6C6,))
    add_pair('right camera-unadjusted dX=2 also reaches final BMI because F699 returns zero',
             direct=False, camera_correction=False, required=(0xF6C6,))
    add_pair('camera branch dX=17 bypasses horizontal CMP but fails BMI', dx=17,
             direct=False, camera_correction=False, required=(0xF6C6,))
    for name, direct, correction, want_a, want_scratch in (
            ('direct F699 returns dX in A and scratch', True, True, 2, 2),
            ('camera F699 returns zero with corrected scratch dX', False, True, 0, 2),
            ('camera F699 returns zero while scratch contains camera-adjusted value 23',
             False, False, 0, 23)):
        ram = pair_ram('right', 2, 0, direct, correction)
        cpu = ccea_pair_fragment(ram, bank7, 0xF612, stops=(0xF62D,))
        ok = cpu.a == want_a and cpu.ram[0] == want_scratch
        cases.append({'id': name, 'kind': 'F699 return', 'entry': 'F612 stop before F62D',
                      'edits': diff(base, ram), 'want_a': want_a, 'a': cpu.a,
                      'want_scratch00': want_scratch, 'scratch00': cpu.ram[0],
                      'path': path_of(cpu), 'carry_set': False, 'cycles': cpu.cyc, 'ok': ok})
    add_pair('horizontal distance 16 passes CMP but fails BMI', dx=16)
    add_pair('horizontal distance 17 rejected at F62F', dx=17, want='F62F')
    add_pair('vertical distance 23 passes CMP but fails BMI', dy=23)
    add_pair('vertical distance 24 rejected at F63E', dy=24, want='F63E')
    add_pair('other actor inactive rejected at F619', want='F619', edits={0x4D1: 0})
    add_pair('self flags bit7 clear rejected at F61E', want='F61E', edits={0x400: 0x10})
    add_pair('other flags bit7 clear rejected at F623', want='F623', edits={0x401: 0})
    add_pair('other object zero rejected at F628', want='F628', edits={0x4E1: 0})
    ram = pair_ram('right', 2, 0, True, True)
    for address, value in ((0x510, 0x00), (0x520, 0x03), (0x511, 0xF8), (0x521, 0x02)):
        ram[address] = value
    cpu = ccea_pair_fragment(ram, bank7, 0xF612)
    pcs = [pc for pc, _ in cpu.trace]
    ok = 0xF6FF in pcs and 0xF642 in pcs and 0xF644 not in pcs
    cases.append({'id': 'vertical lower-high wrap adds 16 then fails BMI', 'kind': 'F6DF guard',
                  'entry': 'F612', 'edits': diff(base, ram), 'want_reject': 'F642',
                  'reject': 'F642' if 0xF642 in pcs else None, 'want_path': ['F6FF'],
                  'path': path_of(cpu), 'carry_set': 0xF644 in pcs, 'cycles': cpu.cyc, 'ok': ok})
    ram = pair_ram('right', 2, 0, True, True)
    for address, value in ((0x510, 0xF8), (0x520, 0x03), (0x511, 0x00), (0x521, 0x04)):
        ram[address] = value
    cpu = ccea_pair_fragment(ram, bank7, 0xF612)
    pcs = [pc for pc, _ in cpu.trace]
    ok = 0xF6F0 in pcs and 0xF642 in pcs and 0xF644 not in pcs
    cases.append({'id': 'vertical higher-high wrap subtracts 16 then fails BMI', 'kind': 'F6DF guard',
                  'entry': 'F612', 'edits': diff(base, ram), 'want_reject': 'F642',
                  'reject': 'F642' if 0xF642 in pcs else None, 'want_path': ['F6F0'],
                  'path': path_of(cpu), 'carry_set': 0xF644 in pcs, 'cycles': cpu.cyc, 'ok': ok})

    cpu = ccea_pair_fragment(base, bank7, 0xF207, x=2, stops=(0xF23F,))
    pcs = [pc for pc, _ in cpu.trace]
    ok = pcs == [0xF207, 0xF209] and 0xF612 not in pcs
    cases.append({'id': 'actor index X=2 skips F612 at F207/F209', 'kind': 'index guard',
                  'entry': 'F207 X=2', 'edits': {}, 'want_path': ['F207', 'F209'],
                  'path': path_of(cpu), 'carry_set': False, 'cycles': cpu.cyc, 'ok': ok})

    final_guard = []
    for value in range(256):
        cpu = ccea_pair_fragment(base, bank7, 0xF63C, a=value)
        pcs = [pc for pc, _ in cpu.trace]
        successors = dict(zip(pcs, pcs[1:]))
        want = 0xF642 if value < 0x18 else 0xF63E
        ok = (successors.get(want) == 0xF646 and 0xF644 not in pcs
              and 0xF645 not in pcs and 0xF646 in pcs)
        final_guard.append({'a': value, 'reject': '%04X' % want, 'ok': ok})

    side_specs = (
        ('right', 15, 'no', 1027), ('right', 4, 'no', 1025), ('right', 3, 'no', 1025),
        ('right', 15, 'flip', 1069), ('right', 15, 'equal', 1027),
        ('left', 15, 'no', 1049), ('left', 4, 'no', 1047), ('left', 3, 'no', 1047),
        ('left', 15, 'flip', 1067), ('left', 15, 'equal', 1047),
    )
    for direction, push, edge_mode, want_cycles in side_specs:
        ram = bytearray(base)
        ram[0x40], ram[0x41], ram[0x10] = 0 if direction == 'right' else 3, 2, push
        for address in range(0x600, 0x700):
            ram[address] = 0
        pre = 0x175 if direction == 'right' else 0x171
        actual_shift = 5 if direction == 'right' and push > 4 else min(push, 4)
        want_x = pre - actual_shift if direction == 'right' else pre + actual_shift
        edge = {'no': 0 if direction == 'right' else 0xFFFF,
                'flip': want_x + 1 if direction == 'right' else want_x - 1,
                'equal': want_x}[edge_mode]
        ram[0x4F0], ram[0x500] = pre & 0xFF, pre >> 8
        ram[0x6E], ram[0x6F] = edge & 0xFF, edge >> 8
        entry = 0xF210 if direction == 'right' else 0xF264
        cpu = ccea_pair_fragment(ram, bank7, entry)
        pcs = [pc for pc, _ in cpu.trace]
        post = cpu.ram[0x4F0] | cpu.ram[0x500] << 8
        flip_pc = 0xF235 if direction == 'right' else 0xF290
        clamp_pc = 0xF216 if direction == 'right' else 0xF26A
        flipped = flip_pc in pcs
        clamped = clamp_pc in pcs
        returned = pcs[-1] == (0xF253 if direction == 'right' else 0xF2AE)
        ok = (post == want_x and cpu.cyc == want_cycles and flipped == (edge_mode == 'flip')
              and clamped == (push > 4) and returned)
        cases.append({'id': '%s push=%d edge=%s' % (direction, push, edge_mode),
                      'kind': 'direct downstream fragment (not reachable through F612)',
                      'entry': '%04X' % entry, 'edits': diff(base, ram, map_zero=True),
                      'pre_x': '%04X' % pre, 'want_x': '%04X' % want_x, 'post_x': '%04X' % post,
                      'actual_shift': actual_shift, 'clamp4': clamped, 'edge_flip': flipped,
                      'returned': returned, 'want_cycles': want_cycles, 'cycles': cpu.cyc,
                      'path': path_of(cpu), 'carry_set': False, 'ok': ok})

    controls = len(cases) + len(final_guard)
    failed = sum(not c['ok'] for c in cases) + sum(not c['ok'] for c in final_guard)
    carry_cases = sum(c.get('carry_set', False) for c in cases)
    document = {
        'task': 'ZERO-65', 'mode': 'state09_source_price.py --ccea-pair',
        'scope': 'Synthetic bank-7 RAM/instruction fixtures on one original pre-frame RAM record; '
                 'not a continuous route, original actor-pair observation, native implementation or mission PASS.',
        'base': {'poll': poll, 'record_sha256': hashlib.sha256(base).hexdigest()},
        'rom_sha256': ROM_SHA, 'bank7_sha256': hashlib.sha256(bank7).hexdigest(),
        'buffers_sha256': hashlib.sha256(buffers).hexdigest(),
        'carry_set_fixture_possible': False,
        'reason': 'F63C CMP #$18 leaves N=1 for every A=0..23 accepted by F640 BCC; '
                  'F642 BMI $F646 therefore rejects all of them. A>=24 is rejected at F63E. '
                  'No RAM value can reach F644 SEC/F645 RTS through this listing.',
        'claude_zero64_explanation': 'The active slot-1 dX=2/14 equal-Y setup can pass the active, flags, '
                                     'object and distance tests, but is rejected by F642 BMI. With $0400 bit4 '
                                     'clear, F6C6 computes otherX-$FC-playerX into scratch $00, but F6DA '
                                     'loads zero before RTS: F62D sees A=0 regardless of that scratch distance.',
        'downstream_cycle_basis': {
            'right_no_flip_clamped': '60 + 942 + 25 = 1027; unclamped removes STA but takes BCS: -2',
            'left_no_flip_clamped': '70 + 954 + 25 = 1049; unclamped: -2; equal-edge BEQ path: 1047',
            'right_edge_flip_clamped': '70 + 954 + 45 = 1069',
            'left_edge_flip_clamped': '80 + 942 + 45 = 1067',
        },
        'final_guard_exhaustive': final_guard, 'cases': cases,
        'summary': {'controls': controls, 'failed': failed, 'carry_set_cases': carry_cases,
                    'final_guard_values': len(final_guard), 'verdict': 'PASS' if not failed and not carry_cases else 'FAIL'},
    }
    with open(out_path, 'x') as f:
        json.dump(document, f, indent=1, sort_keys=True)
    print('summary', json.dumps(document['summary'], sort_keys=True))
    print('fixture', out_path)
    return 0 if document['summary']['verdict'] == 'PASS' else 1


def main(argv):
    if argv[1] == '--ccea-pair' and len(argv) in (5, 6):
        return ccea_pair_fixture(argv[2], argv[3], argv[4], int(argv[5]) if len(argv) == 6 else 2666)
    if len(argv) >= 8 and argv[1] == '--ccea':
        # --ccea ROM PHASES BUFFERS INPUT FIRST LAST [--focus P,P] [--mutant NAME] [--json OUT]
        out = argv[argv.index('--json') + 1] if '--json' in argv else None
        focus = ({int(v) for v in argv[argv.index('--focus') + 1].split(',')}
                 if '--focus' in argv else set())
        mutant = argv[argv.index('--mutant') + 1] if '--mutant' in argv else None
        return ccea(argv[2], argv[3], argv[4], argv[5], int(argv[6]), int(argv[7]), out, focus, mutant)
    if len(argv) >= 8 and argv[1] == '--airborne0b':
        # --airborne0b ROM PHASES BUFFERS INPUT FIRST LAST [--json OUT]
        out = argv[argv.index('--json') + 1] if '--json' in argv else None
        return airborne0b(argv[2], argv[3], argv[4], argv[5], int(argv[6]), int(argv[7]), out)
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
