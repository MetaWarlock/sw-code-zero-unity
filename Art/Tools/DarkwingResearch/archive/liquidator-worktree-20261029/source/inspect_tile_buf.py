with open('Temp/m3_original_validation/replay1/buffers.bin', 'rb') as f:
    f.seek((7673 - 1) * 4384)
    ram = f.read(2048)

tile_buf = ram[0x600:0x700]
print("Tile buffer $0600..$06FF (16x16 grid or similar):")
for r in range(16):
    row_bytes = tile_buf[r*16:(r+1)*16]
    print(f"Row {r:2d} (Y={r*16:3d}..{r*16+15:3d}): " + " ".join(f"{b:02X}" for b in row_bytes))
