RECORD_SIZE = 4384

def read_ram(path, frame):
    with open(path, 'rb') as f:
        f.seek((frame - 1) * RECORD_SIZE)
        return f.read(2048)

for frame in range(7490, 7496):
    ro = read_ram('Temp/m3_original_validation/replay1/buffers.bin', frame)
    rp = read_ram('Temp/m3_port_10k_bolt/buffers.bin', frame)
    print(f"F {frame} | Orig: anim={ro[0x423]:02X} f={ro[0x413]:02X} timer={ro[0x433]:02X} st={ro[0x4D3]:02X}")
    print(f"       | Port: anim={rp[0x423]:02X} f={rp[0x413]:02X} timer={rp[0x433]:02X} st={rp[0x4D3]:02X}")
