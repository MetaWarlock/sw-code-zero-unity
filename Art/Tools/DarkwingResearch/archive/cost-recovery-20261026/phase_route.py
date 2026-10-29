import sys
from phase_sim import frames, polls, boundaries
LOADERS=[289,715,774,833,1030,1923,10279,20473,21664,22835,24898,27916,28651]
BR={6388,6391,6398,6573,6578,6625,6629,7180,8262,18289,19479,27386}
def Toff(p):
    d=polls[p]-polls[p-1]
    if 1926<=p<10275 or 24901<=p<27911: return 1
    return 2 if d==29780 else 1
def dispatch(Cprev,p):
    """NMI p finds slot Waiting (commit Cprev). returns (ready time of launch) or None if crossing"""
    f=frames[p]; T=f['P']+Toff(p)
    if Cprev-4>=T: return None
    for b,info in boundaries(Cprev):
        if b>=T: A=b; break
    H=f['H']
    if f['dma'] is not None and (A-f['A'])%2: H+= -1 if f['dma']%2==0 else 1
    R=A+H
    if info[0]=='start': return R+(17-info[1])+16
    s=info[1]
    return R+(16-s if s in (0,2) else 38-s if s in (5,7) else 109-s)
def actual_ready(p):
    f=frames[p]; return [r for r in f['ready'] if r<=f['launch'][0]][-1]
def actual_commit_of_body(p):
    """commit of body launched in poll p (may be recorded in p+1 if crossing)"""
    L=frames[p]['launch'][0]
    c=[x for x in frames[p]['commit'] if x>L]
    if c: return c[0],False
    n=frames.get(p+1)
    if n and n['commit'] and not n['launch']: return n['commit'][0],True
    return None,None
span=int(sys.argv[1]) if len(sys.argv)>1 else 200
pend=int(sys.argv[2]) if len(sys.argv)>2 else 29359
cur=None; log=[]; collapsed=None; start=None
p=1900
while p<=pend:
    f=frames.get(p)
    if p in LOADERS:
        base=f['commit'][-1]; cur={base+d for d in range(-span,span+1)}; start=p; collapsed=None; p+=1; continue
    if cur is None or not f or not f['launch']: p+=1; continue
    rA=actual_ready(p); cA,crossA=actual_commit_of_body(p)
    if cA is None: log.append((p,'body enters non-ordinary path; clock dropped')); cur=None; p+=1; continue
    B=cA-rA  # includes inserted short NMI if crossing
    nxt=set(); amb=0
    for C in cur:
        r=dispatch(C,p)
        if r is None: amb+=1; continue
        c=r+B
        if crossA:
            # actual crossed NMI p+1; candidate's uninterrupted commit = c - Hshort; check it also crosses
            T=frames[p+1]['P']+Toff(p+1)
            Hs=frames[p+1]['H']
            if (c-Hs)-4<T: amb+=1; continue
        nxt.add(c)
    if amb: log.append((p,'AMBIG',amb,len(nxt)))
    cur=nxt
    if len(cur)==1 and cA in cur and collapsed is None: collapsed=p; log.append((start,'collapsed',p))
    if crossA: p+=2  # the next poll's NMI was absorbed
    else: p+=1
for l in log: print(l)
import json
