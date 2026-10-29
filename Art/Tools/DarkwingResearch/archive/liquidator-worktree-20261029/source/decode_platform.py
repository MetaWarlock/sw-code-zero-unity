from pathlib import Path

rom = Path("Art/Darkwing Duck (USA).nes").read_bytes()
bank1 = rom[0x4010:0x8010]

# Simple 6502 disassembler table for our block
opcodes = {
    0x18: ("CLC", 1), 0x38: ("SEC", 1), 0x60: ("RTS", 1),
    0x20: ("JSR ${:04X}", 3), 0x4C: ("JMP ${:04X}", 3),
    0xA0: ("LDY #${:02X}", 2), 0x84: ("STY ${:02X}", 2),
    0xA2: ("LDX #${:02X}", 2), 0x86: ("STX ${:02X}", 2), 0xA6: ("LDX ${:02X}", 2),
    0xA5: ("LDA ${:02X}", 2), 0x85: ("STA ${:02X}", 2),
    0xAD: ("LDA ${:04X}", 3), 0x8D: ("STA ${:04X}", 3),
    0xBD: ("LDA ${:04X},X", 3), 0x9D: ("STA ${:04X},X", 3),
    0x69: ("ADC #${:02X}", 2), 0xE9: ("SBC #${:02X}", 2),
    0xC9: ("CMP #${:02X}", 2), 0x29: ("AND #${:02X}", 2),
    0x90: ("BCC ${:04X}", 2), 0xB0: ("BCS ${:04X}", 2),
    0xF0: ("BEQ ${:04X}", 2), 0xD0: ("BNE ${:04X}", 2),
    0x30: ("BMI ${:04X}", 2), 0x10: ("BPL ${:04X}", 2),
    0xEE: ("INC ${:04X}", 3),
}

pc = 0x8DC0
while pc < 0x8E75:
    b = bank1[pc - 0x8000]
    if b in opcodes:
        name, length = opcodes[b]
        if length == 1:
            print(f"${pc:04X}: {name}")
        elif length == 2:
            arg = bank1[pc - 0x8000 + 1]
            if b in (0x90, 0xB0, 0xF0, 0xD0, 0x30, 0x10):
                target = pc + 2 + (arg if arg < 0x80 else arg - 0x100)
                print(f"${pc:04X}: {name.split()[0]} ${target:04X}")
            else:
                print(f"${pc:04X}: " + name.format(arg))
        elif length == 3:
            lo = bank1[pc - 0x8000 + 1]
            hi = bank1[pc - 0x8000 + 2]
            arg = (hi << 8) | lo
            print(f"${pc:04X}: " + name.format(arg))
        pc += length
    else:
        print(f"${pc:04X}: .db ${b:02X}")
        pc += 1
