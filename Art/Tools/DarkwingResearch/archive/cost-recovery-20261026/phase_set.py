import sys
from phase_sim import frames, step
p0=int(sys.argv[1]); p1=int(sys.argv[2]); span=int(sys.argv[3]) if len(sys.argv)>3 else 200
base=frames[p0]['commit'][-1]
cur={base+d for d in range(-span,span+1)}
hist=[]
for p in range(p0+1,p1+1):
    f=frames[p]
    nxt=set(); crosses=0
    for C in cur:
        r,err=step(C,p)
        if err: crosses+=1; continue
        nxt.add(r)
    act=f['commit'][0]
    offs=sorted(c-act for c in nxt)
    hist.append((p,len(nxt),crosses,offs[0] if offs else None,offs[-1] if offs else None))
    cur=nxt
    if len(nxt)==1 and act in nxt:
        print('collapsed to actual at',p); break
for h in hist[:5]+hist[-5:]: print(h)
# print size evolution sparsely
for h in hist[::max(1,len(hist)//30)]: print(h)
