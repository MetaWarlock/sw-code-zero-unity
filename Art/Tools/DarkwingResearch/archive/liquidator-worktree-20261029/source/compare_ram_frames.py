RECORD_SIZE = 4384

def read_ram(path, frame):
    with open(path, 'rb') as f:
        f.seek((frame - 1) * RECORD_SIZE)
        return f.read(2048)

for frame in [7672, 7673, 7674, 7675]:
    ram_o = read_ram('Temp/m3_original_validation/replay1/buffers.bin', frame)
    ram_p = read_ram('Temp/m3_port_10k_bolt/buffers.bin', frame)
    print(f"=== Frame {frame} ===")
    px_o = ram_o[0x4F0] | (ram_o[0x500] << 8)
    py_o = ram_o[0x510] | (ram_o[0x520] << 8)
    px_p = ram_p[0x4F0] | (ram_p[0x500] << 8)
    py_p = ram_p[0x510] | (ram_p[0x520] << 8)
    print(f"  Orig: Player X={px_o} Y={py_o} st={ram_o[0x50]:02X} flags={ram_o[0x400]:02X}")
    print(f"  Port: Player X={px_p} Y={py_p} st={ram_p[0x50]:02X} flags={ram_p[0x400]:02X}")
    print("  Active Slots Orig vs Port:")
    for s in range(1, 16):
        d_o = ram_o[0x4D0 + s]
        d_p = ram_p[0x4D0 + s]
        if (d_o & 0x80) or (d_p & 0x80):
            xo = ram_o[0x4F0 + s] | (ram_o[0x500 + s] << 8)
            yo = ram_o[0x510 + s] | (ram_o[0x520 + s] << 8)
            xp = ram_p[0x4F0 + s] | (ram_p[0x500 + s] << 8)
            yp = ram_p[0x510 + s] | (ram_p[0x520 + s] << 8)
            print(f"    Slot {s:2d}:")
            print(f"      Orig: beh={ram_o[0x4E0+s]:02X} anim={ram_o[0x420+s]:02X} st={d_o:02X} X={xo:4d} Y={yo:4d} param={ram_o[0x570+s]:02X} t480={ram_o[0x480+s]:02X}")
            print(f"      Port: beh={ram_p[0x4E0+s]:02X} anim={ram_p[0x420+s]:02X} st={d_p:02X} X={xp:4d} Y={yp:4d} param={ram_p[0x570+s]:02X} t480={ram_p[0x480+s]:02X}")
