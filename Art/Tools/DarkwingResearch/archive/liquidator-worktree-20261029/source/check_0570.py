with open('Temp/m3_original_validation/replay1/buffers.bin', 'rb') as f:
    f.seek((7673 - 1) * 4384)
    ram = f.read(2048)

for s in [2, 3]:
    print(f"Slot {s}: 0570={ram[0x570 + s]:02X} 0400={ram[0x400 + s]:02X} 04D0={ram[0x4D0 + s]:02X} 04E0={ram[0x4E0 + s]:02X}")
