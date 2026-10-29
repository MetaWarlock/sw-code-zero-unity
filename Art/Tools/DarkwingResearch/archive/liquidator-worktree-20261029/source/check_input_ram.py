RECORD_SIZE = 4384

def read_ram(path, frame):
    with open(path, 'rb') as f:
        f.seek((frame - 1) * RECORD_SIZE)
        return f.read(2048)

for frame in range(9300, 9306):
    ro = read_ram('Temp/m3_original_validation/replay1/buffers.bin', frame)
    rp = read_ram('Temp/m3_port_10k_headbonk/buffers.bin', frame)
    print(f"F {frame:5d} | Orig: held(14)={ro[0x14]:02X} pressed(16)={ro[0x16]:02X} st={ro[0x50]:02X} anim={ro[0x420]:02X} xf={ro[0x470]:02X} xv={ro[0x540]:02X}.{ro[0x530]:02X}")
    print(f"        | Port: held(14)={rp[0x14]:02X} pressed(16)={rp[0x16]:02X} st={rp[0x50]:02X} anim={rp[0x420]:02X} xf={rp[0x470]:02X} xv={rp[0x540]:02X}.{rp[0x530]:02X}")
