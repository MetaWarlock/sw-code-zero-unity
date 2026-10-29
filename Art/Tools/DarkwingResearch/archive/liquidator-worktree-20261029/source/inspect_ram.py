from pathlib import Path

BUFFER_STRIDE = 2048 + 2048 + 256 + 32
raw = Path("Temp/m3_original_validation/replay1/buffers.bin").read_bytes()

def get_ram(frame):
    base = (frame - 1) * BUFFER_STRIDE
    return raw[base:base + 2048]

for f in range(9488, 9495):
    ram = get_ram(f)
    px = ram[0x4F0] | (ram[0x500] << 8)
    py = ram[0x510] | (ram[0x520] << 8)
    pst = ram[0x50]
    php = ram[0x5E4]
    p_invuln = ram[0x7C]
    print(f"--- Frame {f} ---")
    print(f"Player: X={px} Y={py} State={pst:02X} HP={php} Invuln={p_invuln:02X}")
    print("Active Objects:")
    for slot in range(16):
        active = (ram[0x4D0 + slot] & 0x80) != 0
        beh = ram[0x4E0 + slot]
        if active or beh != 0:
            ox = ram[0x4F0 + slot] | (ram[0x500 + slot] << 8)
            oy = ram[0x510 + slot] | (ram[0x520 + slot] << 8)
            anim = ram[0x420 + slot]
            flags = ram[0x400 + slot]
            st = ram[0x4D0 + slot]
            param = ram[0x570 + slot]
            hp = ram[0x5A0 + slot]
            t480 = ram[0x480 + slot]
            t490 = ram[0x490 + slot]
            print(f"  slot {slot:2d}: active={active} beh=0x{beh:02X} x={ox:4d} y={oy:4d} anim=0x{anim:02X} flags=0x{flags:02X} st=0x{st:02X} param=0x{param:02X} t480={t480:02X} t490={t490:02X}")
