RECORD_SIZE = 4384

def read_ram(path, frame):
    with open(path, 'rb') as f:
        f.seek((frame - 1) * RECORD_SIZE)
        return f.read(2048)

ram = read_ram('Temp/m3_original_validation/replay1/buffers.bin', 9301)
tile_buf = ram[0x600:0x700]

# Darkwing is at X=1035, Y=444
# ScrollX = ? ScrollY = ?
sx = ram[0xFC] | (ram[0xFD] << 8)
sy = ram[0xFA] | (ram[0xFB] << 8)
print(f"Scroll: X={sx} Y={sy}")
print(f"Player: X={ram[0x4F0]|(ram[0x500]<<8)} Y={ram[0x510]|(ram[0x520]<<8)}")
print(f"Facing/Dir: 04A0={ram[0x4A0]:02X}")

# What objects are active at 9301?
print("Active objects at 9301:")
for s in range(1, 16):
    if ram[0x4D0+s] & 0x80:
        ox = ram[0x4F0+s] | (ram[0x500+s] << 8)
        oy = ram[0x510+s] | (ram[0x520+s] << 8)
        print(f"  Slot {s:2d}: beh={ram[0x4E0+s]:02X} anim={ram[0x420+s]:02X} X={ox:4d} Y={oy:4d} param={ram[0x570+s]:02X}")
