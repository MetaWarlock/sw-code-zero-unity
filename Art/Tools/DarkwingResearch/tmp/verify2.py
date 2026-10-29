b=open('D:/Art/SW Code Zero Unity/Art/Research/DarkwingDuck/extracted/raw/prg.bin','rb').read()
b5=5*0x4000; b7=7*0x4000
# anim entry raw bytes for player anims
for a,ptr in [(0x01,0xaff3),(0x03,0xaff9),(0x04,0xb000),(0x05,0xb005),(0x06,0xb00a),(0x09,0xb017),(0x0c,0xb022),(0x0d,0xb027),(0x11,0xb038),(0x12,0xb03b)]:
    off=ptr-0x8000
    e=b[b5+off:b5+off+14]
    print('anim %02X @$%04X: %s' % (a,ptr,' '.join('%02X'%x for x in e)))
# CF2E area raw
print('CF21-CF45:', ' '.join('%02X'%x for x in b[b7+0x0F21:b7+0x0F45]))
# state table raw around CB41
print('CB41-CB60:', ' '.join('%02X'%x for x in b[b7+0x0B41:b7+0x0B60]))
