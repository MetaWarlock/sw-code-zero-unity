RECORD_SIZE = 4384

def read_ram(path, frame):
    with open(path, 'rb') as f:
        f.seek((frame - 1) * RECORD_SIZE)
        return f.read(2048)

for frame in range(7490, 7505):
    ro = read_ram('Temp/m3_original_validation/replay1/buffers.bin', frame)
    rp = read_ram('Temp/m3_port_10k_bolt/buffers.bin', frame)
    
    # Slot 3
    s3_o = (ro[0x4D3], ro[0x4E3], ro[0x423], ro[0x413], ro[0x4F3]|(ro[0x503]<<8), ro[0x513]|(ro[0x523]<<8), ro[0x573])
    s3_p = (rp[0x4D3], rp[0x4E3], rp[0x423], rp[0x413], rp[0x4F3]|(rp[0x503]<<8), rp[0x513]|(rp[0x523]<<8), rp[0x573])
    print(f"F {frame} | Orig: st={s3_o[0]:02X} beh={s3_o[1]:02X} anim={s3_o[2]:02X} f={s3_o[3]:02X} x={s3_o[4]} y={s3_o[5]} param={s3_o[6]:02X}")
    print(f"       | Port: st={s3_p[0]:02X} beh={s3_p[1]:02X} anim={s3_p[2]:02X} f={s3_p[3]:02X} x={s3_p[4]} y={s3_p[5]} param={s3_p[6]:02X}")
