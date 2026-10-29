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
"""
import hashlib
import json
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
           0x88: 'DEY', 0xCA: 'DEX'}
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
        cyc = {'PHA': 3, 'PLA': 4}.get(name, 2)
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


def walk(ram, bank7):
    cpu = Cpu(ram, bank7)
    cpu.push(0xCA); cpu.push(0x42)  # JSR $CAFC from CA40 returns to CA43
    base_s = cpu.s
    pc = 0xCAFC
    for _ in range(5000):
        if pc == 0xCA43 and cpu.s == (base_s + 2) & 0xFF:
            return cpu
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


def main(argv):
    if len(argv) == 4 and argv[1] == '--selftest':
        rom = open(argv[2], 'rb').read()
        if hashlib.sha256(rom).hexdigest() != ROM_SHA:
            sys.exit('ROM SHA-256 mismatch')
        return selftest(rom[16 + 16384 * (rom[4] - 1):16 + 16384 * rom[4]], argv[3])
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
