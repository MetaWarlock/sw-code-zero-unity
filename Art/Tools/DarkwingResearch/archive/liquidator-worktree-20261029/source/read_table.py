with open("Assets/Darkwing/Data/prg_bank6.bytes", "rb") as f:
    b6 = f.read()

table_lo = 0x825F - 0x8000
table_hi = 0x82A7 - 0x8000

for i in range(min(20, table_hi - table_lo)):
    lo = b6[table_lo + i]
    hi = b6[table_hi + i]
    addr = lo | (hi << 8)
    print(f"Behavior {i:02X}: 0x{addr:04X}")
