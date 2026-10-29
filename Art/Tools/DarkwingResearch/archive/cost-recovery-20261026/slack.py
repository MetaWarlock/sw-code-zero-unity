from phase_route import frames, polls, Toff, actual_ready, actual_commit_of_body, BR, LOADERS
import collections
sl=[]
for p in range(1926,29359):
    f=frames.get(p)
    if not f or not f['launch'] or p+1 not in frames: continue
    c,cross=actual_commit_of_body(p)
    if c is None: continue
    T=frames[p+1]['P']+Toff(p+1)
    if cross:
        Hs=frames[p+1]['H']; un=c-Hs
        sl.append((p,-(un-4-T)+0,'X'))
    else:
        sl.append((p,T-(c-4),''))
near=[s for s in sl if abs(s[1])<300]
print(len(sl)); print(sorted(near,key=lambda s:abs(s[1]))[:40])
h=collections.Counter((s[1]//100)*100 for s in sl if s[1]<1000); print(sorted(h.items()))
