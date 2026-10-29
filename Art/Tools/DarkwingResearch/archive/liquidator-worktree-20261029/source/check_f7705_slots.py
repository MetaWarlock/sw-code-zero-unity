RECORD_SIZE = 4384

def read_ram(path, frame):
    with open(path, 'rb') as f:
        f.seek((frame - 1) * RECORD_SIZE)
        return f.read(2048)

ro = read_ram('Temp/m3_original_validation/replay1/buffers.bin', 7705)
rp = read_ram('Temp/m3_port_10k_recompiled/buffers.bin', 7705)

print("Active slots in Orig at F 7705:")
for s in range(1, 16):
    if ro[0x4D0+s] & 0x80:
        xo = ro[0x4F0+s] | (ro[0x500+s] << 8)
        yo = ro[0x510+s] | (ro[0x520+s] << 8)
        print(f"  Slot {s:2d}: beh={ro[0x4E0+s]:02X} anim={ro[0x420+s]:02X} st={ro[0x4D0+s]:02X} X={xo:4d} Y={yo:4d} param={ro[0x570+s]:02X}")

print("Active slots in Port at F 7705:")
for s in range(1, 16):
    if rp[0x4D0+s] & 0x80:
        xp = rp[0x4F0+s] | (rp[0x500+s] << 8)
        yp = rp[0x510+s] | (rp[0x520+s] << 8)
        print(f"  Slot {s:2d}: beh={rp[0x4E0+s]:02X} anim={rp[0x420+s]:02X} st={rp[0x4D0+s]:02X} X={xp:4d} Y={yp:4d} param={rp[0x570+s]:02X}")
