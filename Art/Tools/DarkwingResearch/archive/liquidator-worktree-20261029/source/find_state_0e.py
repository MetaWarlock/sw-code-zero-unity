import glob

for path in glob.glob('Assets/Darkwing/Data/*.bytes'):
    with open(path, 'rb') as f:
        data = f.read()
    # Search for LDA #$0E (A9 0E) followed by STA $50 (85 50)
    for i in range(len(data) - 3):
        if data[i] == 0xA9 and data[i+1] == 0x0E and data[i+2] == 0x85 and data[i+3] == 0x50:
            print(f"Found in {path} at offset {i:04X} (bank addr {0x8000+i:04X} or {0xC000+i:04X})")
