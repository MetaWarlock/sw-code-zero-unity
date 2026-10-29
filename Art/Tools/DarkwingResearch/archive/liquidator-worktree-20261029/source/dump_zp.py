from pathlib import Path

BUFFER_STRIDE = 2048 + 2048 + 256 + 32
raw = Path("Temp/m3_original_validation/replay1/buffers.bin").read_bytes()

def get_ram(frame):
    base = (frame - 1) * BUFFER_STRIDE
    return raw[base:base + 2048]

for f in (9490, 9491):
    ram = get_ram(f)
    print(f"=== Frame {f} ===")
    print("Zero page $10..$7F:")
    for row in range(1, 8):
        print(f"  ${row*16:02X}: " + " ".join(f"{ram[row*16 + col]:02X}" for col in range(16)))
