from pathlib import Path

rom = Path("Art/Darkwing Duck (USA).nes").read_bytes()
bank1_offset = 0x0010 + 1 * 0x4000
code = rom[bank1_offset + (0x8D60 - 0x8000) : bank1_offset + (0x8E50 - 0x8000)]

# We can use custom disassembler or simple 6502 decoding
# Let's write a simple 6502 disassembler or print hex
print("Bytes at $8D60..$8E40:")
for i in range(0, len(code), 16):
    addr = 0x8D60 + i
    chunk = code[i:i+16]
    hex_str = " ".join(f"{b:02X}" for b in chunk)
    print(f"${addr:04X}: {hex_str}")
