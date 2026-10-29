import sys

RECORD_SIZE = 2048 + 2048 + 256 + 32  # 4384 bytes

def get_ram(frame):
    with open('Temp/m3_original_validation/replay1/buffers.bin', 'rb') as f:
        f.seek((frame - 1) * RECORD_SIZE)
        return f.read(2048)

print("Frame | 4F0 500 (X) | 530 540 (Xv) | 510 520 (Y) | 550 560 (Yv) | 4A0 | 46 47 48 49 | 50")
for frame in range(7670, 7678):
    ram = get_ram(frame)
    x = ram[0x4F0] | (ram[0x500] << 8)
    y = ram[0x510] | (ram[0x520] << 8)
    xv = (ram[0x540] if ram[0x540] < 128 else ram[0x540] - 256)
    yv = (ram[0x560] if ram[0x560] < 128 else ram[0x560] - 256)
    print(f"{frame:5d} | {ram[0x4F0]:02X}  {ram[0x500]:02X} ({x:4d}) | {ram[0x530]:02X}  {ram[0x540]:02X} ({xv:+d}) | {ram[0x510]:02X}  {ram[0x520]:02X} ({y:4d}) | {ram[0x550]:02X}  {ram[0x560]:02X} ({yv:+d}) |  {ram[0x4A0]:02X} | {ram[0x46]:02X} {ram[0x47]:02X} {ram[0x48]:02X} {ram[0x49]:02X} | {ram[0x50]:02X}")
