import sys; sys.path.insert(0,'Art/Tools/DarkwingResearch/cost')
import state09_source_price as s
R=4384
rom=open('Art/Darkwing Duck (USA).nes','rb').read(); b7=rom[16+16384*(rom[4]-1):16+16384*rom[4]]
buf=open('Art/Playthroughs/20261019T212535Z/replay1/buffers.bin','rb')
inp=open('Art/Playthroughs/20261019T212535Z/input.bin','rb').read()
lo,hi=int(sys.argv[1]),int(sys.argv[2])
ph={}
for line in open('Art/Research/DarkwingDuck/reference/cost-harness/data/phases/city_phases.csv'):
    if line.startswith('poll'):continue
    p,c,pc=line.strip().split(',');p=int(p)
    if lo<=p<=hi and pc in('CA40','CA59'): ph.setdefault(p,{}).setdefault(pc,int(c))
def rec(p): buf.seek((p-1)*R); return bytearray(buf.read(2048))
from collections import Counter
tally=Counter()
for p in range(lo,hi+1):
    r=rec(p)
    if r[0x50]!=int(sys.argv[3],16) or not r[0x4D0]&0x80: continue
    r[0x16]=inp[p-1]; r[0x14]=inp[p-1]&~inp[p-2]&0xFF
    try:
        c=s.walk(r,b7); src=c.cyc
    except s.Unsupported as e:
        tally['UNSUP '+str(e)]+=1; print(p,'UNSUP',e); continue
    m=ph.get(p,{})
    if 'CA59' in m and 'CA40' in m and r[0x65]<0x80 and r[0x30]==0:
        o=m['CA59']-m['CA40']-18; st='EXACT' if src==o else 'MISMATCH'
    else: o=None; st='NO_ORACLE'
    post=rec(p+1)
    keys=[0x50,0x490,0x410,0x420,0x430,0x530,0x540,0x550,0x560,0x4D0,0x500,0x510,0x520]
    pe=[('%X'%k,c.ram[k],post[k]) for k in keys if c.ram[k]!=post[k]]
    path=[('%04X'%pc) for pc,_ in c.trace if pc in (0xCBBD,0xCBD4,0xCC3A,0xCC44,0xCBEC,0xCBEF,0xCC53,0xCD3E,0xCDD5,0xCE06,0xCE22,0xCE21,0xCE08)]
    tally[st]+=1
    if st!='EXACT' or len(sys.argv)>4: print(p,'in %02X'%inp[p-1],'src',src,'oracle',o,st,pe,path)
print(dict(tally))
