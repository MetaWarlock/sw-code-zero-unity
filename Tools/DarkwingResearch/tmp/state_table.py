b=open('D:/Art/SW Code Zero Unity/Research/DarkwingDuck/extracted/raw/prg.bin','rb').read()
base=7*0x4000
lo=b[base+0x0B5F:base+0x0B7F]
hi=b[base+0x0B75:base+0x0B95]
for i in range(32):
    t=(hi[i]<<8)|lo[i]
    print('state %2d ($%02X) -> $%04X' % (i,i,t))
