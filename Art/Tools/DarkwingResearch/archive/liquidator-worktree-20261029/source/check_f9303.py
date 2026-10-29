RECORD_SIZE = 4384

def read_ram(path, frame):
    with open(path, 'rb') as f:
        f.seek((frame - 1) * RECORD_SIZE)
        return f.read(2048)

for frame in range(9298, 9310):
    ro = read_ram('Temp/m3_original_validation/replay1/buffers.bin', frame)
    rp = read_ram('Temp/m3_port_10k_headbonk/buffers.bin', frame)
    px_o = ro[0x4F0] | (ro[0x500] << 8)
    py_o = ro[0x510] | (ro[0x520] << 8)
    px_p = rp[0x4F0] | (rp[0x500] << 8)
    py_p = rp[0x510] | (rp[0x520] << 8)
    xv_o = ro[0x540] if ro[0x540] < 128 else ro[0x540] - 256
    xv_p = rp[0x540] if rp[0x540] < 128 else rp[0x540] - 256
    print(f"F {frame:5d} | Orig: st={ro[0x50]:02X} anim={ro[0x420]:02X} f={ro[0x410]:02X} x={px_o:4d} y={py_o:4d} xv={xv_o:+d}.{ro[0x530]:02X} xf={ro[0x470]:02X} ctrl={ro[0xFF]:02X}")
    print(f"        | Port: st={rp[0x50]:02X} anim={rp[0x420]:02X} f={rp[0x410]:02X} x={px_p:4d} y={py_p:4d} xv={xv_p:+d}.{rp[0x530]:02X} xf={rp[0x470]:02X} ctrl={rp[0xFF]:02X}")
