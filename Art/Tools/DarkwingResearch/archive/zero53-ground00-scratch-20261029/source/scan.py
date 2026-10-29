import sys; sys.path.insert(0,'Art/Tools/DarkwingResearch/cost')
import state09_source_price as s
from collections import Counter
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
T=Counter(); cc44=[]
for p in range(lo,hi+1):
    r=rec(p)
    if r[0x50]!=0 or not r[0x4D0]&0x80: continue
    h=inp[p-1]; r[0x16]=h; r[0x14]=h&~inp[p-2]&0xFF
    try: c=s.walk(r,b7)
    except s.Unsupported as e: T['UNSUP '+str(e)]+=1; continue
    m=ph.get(p,{})
    if 'CA59' in m and 'CA40' in m and r[0x65]<0x80 and r[0x30]==0:
        st='EXACT' if c.cyc==m['CA59']-m['CA40']-18 else 'MISMATCH'
    else: st='NO_ORACLE'
    pcs={pc for pc,_ in c.trace}
    if 0xCC44 in pcs:
        i=[k for k,(pc,_) in enumerate(c.trace) if pc==0xCBE1][0]
        tail=sum(cy for _,cy in c.trace[i:])
        cc44.append((p,'%02X'%h,'%02X'%r[0x14],c.cyc,st,tail,r[0x420],r[0x560]))
    br='CC44' if 0xCC44 in pcs else 'CC3A' if 0xCC3A in pcs else 'CBBD' if 0xCBBD in pcs else 'CC53' if 0xCC53 in pcs else 'CBEF' if 0xCBEF in pcs else 'CBEC' if 0xCBEC in pcs else 'other'
    T[(br,st)]+=1
for k,v in sorted(T.items(),key=str): print(k,v)
print('CC44 polls (poll,held,pressed,src,status,tailFromCBE1,anim420,vel560):')
for x in cc44: print(x)
