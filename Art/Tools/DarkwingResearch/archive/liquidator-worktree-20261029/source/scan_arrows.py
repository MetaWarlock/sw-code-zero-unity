RECORD_SIZE = 4384

def read_ram(path, frame):
    with open(path, 'rb') as f:
        f.seek((frame - 1) * RECORD_SIZE)
        return f.read(2048)

print("Scanning Slot 2 and Slot 3 in Orig and Port...")
for frame in range(7450, 7673, 10):
    ram_o = read_ram('Temp/m3_original_validation/replay1/buffers.bin', frame)
    ram_p = read_ram('Temp/m3_port_10k_bolt/buffers.bin', frame)
    d2_o = ram_o[0x4D2]; d3_o = ram_o[0x4D3]
    d2_p = ram_p[0x4D2]; d3_p = ram_p[0x4D3]
    print(f"Frame {frame:5d} | Orig: s2={d2_o:02X} beh={ram_o[0x4E2]:02X} x={ram_o[0x4F2]|(ram_o[0x502]<<8):4d} y={ram_o[0x512]|(ram_o[0x522]<<8):4d} param={ram_o[0x572]:02X} | s3={d3_o:02X} beh={ram_o[0x4E3]:02X} x={ram_o[0x4F3]|(ram_o[0x503]<<8):4d} y={ram_o[0x513]|(ram_o[0x523]<<8):4d} param={ram_o[0x573]:02X}")
    print(f"            | Port: s2={d2_p:02X} beh={ram_p[0x4E2]:02X} x={ram_p[0x4F2]|(ram_p[0x502]<<8):4d} y={ram_p[0x512]|(ram_p[0x522]<<8):4d} param={ram_p[0x572]:02X} | s3={d3_p:02X} beh={ram_p[0x4E3]:02X} x={ram_p[0x4F3]|(ram_p[0x503]<<8):4d} y={ram_p[0x513]|(ram_p[0x523]<<8):4d} param={ram_p[0x573]:02X}")
