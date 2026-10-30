# ZERO-69 independent negatives for the complete F612 scan (Claude review).
# Usage: python -I independent_negatives.py <tool_dir> <rom> <buffers> <poll> <out.json>
import hashlib, importlib.util, json, sys

tool_dir, rom_path, buf_path, poll, out_path = sys.argv[1:6]
spec = importlib.util.spec_from_file_location('s9', tool_dir + '/state09_source_price.py')
s9 = importlib.util.module_from_spec(spec)
spec.loader.exec_module(s9)
s9.OPS.update(s9.CCEA_OPS)
s9.OPS.update(s9.CCEA_PAIR_OPS)
rom = open(rom_path, 'rb').read()
assert hashlib.sha256(rom).hexdigest() == s9.ROM_SHA
bank7 = rom[16 + 16384 * (rom[4] - 1):16 + 16384 * rom[4]]
start = (int(poll) - 1) * s9.RECORD
base = bytearray(open(buf_path, 'rb').read()[start:start + 2048])


def scan(slot=2, dx=2, dy=0, size=1, state=1, extra=None, patched=None):
    ram = bytearray(base)
    ram[0x50] = state
    ram[0x16], ram[0x14] = 0x81, 0
    ram[0x400] |= 0x90
    ram[0x4E1] = 0
    for y in range(2, 16):
        ram[0x4D0 + y], ram[0x400 + y], ram[0x570 + y] = 0, 0, 0
    ram[0x4D0 + slot], ram[0x400 + slot], ram[0x570 + slot] = 0x80, 0x80, 0x80 | size
    px = (ram[0x4F0] | ram[0x500] << 8) + 2
    py = ram[0x510] | ram[0x520] << 8
    ox, oy = px + dx & 0xFFFF, py + dy & 0xFFFF
    for a, v in ((0x4F0, px), (0x500, px >> 8), (0x4F0 + slot, ox), (0x500 + slot, ox >> 8),
                 (0x510 + slot, oy), (0x520 + slot, oy >> 8)):
        ram[a] = v & 0xFF
    for a, v in (extra or {}).items():
        ram[a] = v
    cpu = s9.ccea_pair_fragment(ram, patched or bank7, 0xF612)
    pcs = [pc for pc, _ in cpu.trace]
    return {'stop_pc': '%04X' % cpu.stop_pc, 'return_pc': '%04X' % pcs[-1],
            'carry': bool(cpu.c), 'f644': 0xF644 in pcs, 'cycles': cpu.cyc}


cases = []


def check(name, want_carry, want_ret, **kw):
    r = scan(**kw)
    r.update(id=name, ok=r['stop_pc'] == 'CA43' and r['carry'] == want_carry
             and r['return_pc'] == want_ret and not r['f644'])
    cases.append(r)


check('slot15 last-slot hit', True, 'F7DF', slot=15)
check('slot3 hit after slots 2 empty (loop continues)', True, 'F7DF', slot=3)
check('size class 2 dX=18 equals FC09[2] hits', True, 'F7DF', size=2, dx=18)
check('size class 2 dX=19 misses', False, 'F74D', size=2, dx=19)
check('size class 2 dY=17 below FBE9[2]=18 hits', True, 'F7DF', size=2, dy=17)
check('size class 2 dY=18 misses', False, 'F74D', size=2, dy=18)
check('size class 0 dX=22 hits', True, 'F7DF', size=0, dx=22)
check('size class 0 dX=23 misses', False, 'F74D', size=0, dx=23)
check('slot1 only (not scanned) returns CLC', False, 'F74D', slot=1)
# Mutant: F744 JMP F7DE -> JMP F74C (CLC). A hit must then lose carry.
mut = bytearray(bank7)
mut[0xF744 - 0xC000:0xF747 - 0xC000] = bytes((0x4C, 0x4C, 0xF7))
r = scan(patched=bytes(mut))
r.update(id='mutant F744->F74C kills carry (detector check)', ok=not r['carry'] and r['return_pc'] == 'F74D')
cases.append(r)
# Mutant: F642 BMI -> BPL makes direct pair 0/1 reach F644 SEC.
mut = bytearray(bank7)
mut[0xF642 - 0xC000] = 0x10
cpu = s9.ccea_pair_fragment(bytearray(base), bytes(mut), 0xF63C, a=5)
cases.append({'id': 'mutant F642 BMI->BPL reaches F644 SEC (dead-code is BMI-only)',
              'stop_pc': '%04X' % cpu.stop_pc, 'carry': bool(cpu.c),
              'ok': cpu.stop_pc == 0xCA43 and bool(cpu.c)})

failed = sum(not c['ok'] for c in cases)
doc = {'task': 'ZERO-69', 'reviewer': 'Claude Pelmen (independent)', 'rom_sha256': s9.ROM_SHA,
       'record_sha256': hashlib.sha256(base).hexdigest(), 'cases': cases,
       'summary': {'controls': len(cases), 'failed': failed, 'verdict': 'PASS' if not failed else 'FAIL'}}
with open(out_path, 'x') as f:
    json.dump(doc, f, indent=1, sort_keys=True)
print(json.dumps(doc['summary']))
for c in cases:
    print(c['ok'], c['id'], c.get('return_pc'), c['carry'], c.get('cycles'))
