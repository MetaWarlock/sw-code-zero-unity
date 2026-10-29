b=open('D:/Art/SW Code Zero Unity/Art/Research/DarkwingDuck/extracted/raw/prg.bin','rb').read()
def tbl(bank, lo_off, hi_off, n, base=0x8000):
    bb=bank*0x4000
    lo=b[bb+lo_off:bb+lo_off+n]; hi=b[bb+hi_off:bb+hi_off+n]
    out=[]
    for i in range(n):
        t=(hi[i]<<8)|lo[i]
        out.append((i, t))
    return out
print('bank6 behavior table ($825F/$82A7), ids 0..$46:')
for i,t in tbl(6,0x025F,0x02A7,0x47):
    print('  behavior %02X -> $%04X' % (i,t))
