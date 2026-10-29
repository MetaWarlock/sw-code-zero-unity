from pathlib import Path

BUFFER_STRIDE = 2048 + 2048 + 256 + 32
raw = Path("Temp/m3_original_validation/replay1/buffers.bin").read_bytes()

def get_ram(frame):
    base = (frame - 1) * BUFFER_STRIDE
    return raw[base:base + 2048]

for f in range(10085, 10095):
    ram = get_ram(f)
    px = ram[0x4F0] | (ram[0x500] << 8)
    pxfrac = ram[0x470]
    py = ram[0x510] | (ram[0x520] << 8)
    print(f"Orig F {f}: P=({px}, frac={pxfrac:02X}, {py})")
    for slot in range(16):
        if (ram[0x4D0 + slot] & 0x80) != 0:
            beh = ram[0x4E0 + slot]
            if beh in (0x56, 0x65, 0x67, 0x17, 0x37):
                ox = ram[0x4F0 + slot] | (ram[0x500 + slot] << 8)
                oxfrac = ram[0x470 + slot]
                oy = ram[0x510 + slot] | (ram[0x520 + slot] << 8)
                st = ram[0x4D0 + slot]
                t480 = ram[0x480 + slot]
                d = ram[0x580 + slot]
                print(f"   slot {slot:2d} beh={beh:02X} x={ox}.{oxfrac:02X} y={oy} st={st:02X} t480={t480} dir={d}")
