from pathlib import Path

BUFFER_STRIDE = 2048 + 2048 + 256 + 32
raw = Path("Temp/m3_original_validation/replay1/buffers.bin").read_bytes()

def get_ram(frame):
    base = (frame - 1) * BUFFER_STRIDE
    return raw[base:base + 2048]

for f in range(10055, 10068):
    ram = get_ram(f)
    px = ram[0x4F0] | (ram[0x500] << 8)
    pxfrac = ram[0x470]
    py = ram[0x510] | (ram[0x520] << 8)
    pst = ram[0x50]
    p_inv = ram[0x7C]
    # Check if any object changed or moved
    active_objs = []
    for slot in range(16):
        active = (ram[0x4D0 + slot] & 0x80) != 0
        if active:
            beh = ram[0x4E0 + slot]
            ox = ram[0x4F0 + slot] | (ram[0x500 + slot] << 8)
            oxfrac = ram[0x470 + slot]
            oy = ram[0x510 + slot] | (ram[0x520 + slot] << 8)
            anim = ram[0x420 + slot]
            st = ram[0x4D0 + slot]
            prm = ram[0x570 + slot]
            active_objs.append((slot, hex(beh), ox, oxfrac, oy, anim, hex(st), hex(prm)))
    print(f"F {f:5d}: P=({px}, frac={pxfrac:02X}, {py}, st={pst:02X}) objs={active_objs}")
