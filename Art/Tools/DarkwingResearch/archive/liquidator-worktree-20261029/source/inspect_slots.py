import sys

RECORD_SIZE = 2048 + 2048 + 256 + 32  # 4384 bytes

def get_ram(frame):
    with open('Temp/m3_original_validation/replay1/buffers.bin', 'rb') as f:
        f.seek((frame - 1) * RECORD_SIZE)
        return f.read(2048)

for frame in [7673, 7674]:
    ram = get_ram(frame)
    print(f"--- Frame {frame} ---")
    px = ram[0x4F0] | (ram[0x500] << 8)
    py = ram[0x510] | (ram[0x520] << 8)
    print(f"Player: X={px} Y={py} State={ram[0x50]:02X} Flags={ram[0x400]:02X}")
    for s in range(1, 16):
        d0 = ram[0x4D0 + s]
        if (d0 & 0x80) != 0:
            beh = ram[0x4E0 + s]
            anim = ram[0x420 + s]
            frame_num = ram[0x410 + s]
            ox = ram[0x4F0 + s] | (ram[0x500 + s] << 8)
            oy = ram[0x510 + s] | (ram[0x520 + s] << 8)
            flags = ram[0x400 + s]
            st = ram[0x4D0 + s] & 0x7F
            hp = ram[0x5A0 + s]
            print(f"  Slot {s:2d}: beh={beh:02X} anim={anim:02X} f={frame_num:02X} st={st:02X} X={ox:4d} Y={oy:4d} flags={flags:02X} hp={hp:02X}")
