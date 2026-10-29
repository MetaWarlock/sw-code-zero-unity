with open('Assets/Darkwing/Data/prg_bank1.bytes', 'rb') as f:
    b1 = f.read()

for i in range(len(b1) - 4):
    if b1[i] == 0xC9 and b1[i+1] == 0x47:
        print(f"CMP #$47 at {0x8000+i:04X}")
    if b1[i] == 0xE9 and b1[i+1] == 0x47:
        print(f"SBC #$47 at {0x8000+i:04X}")
