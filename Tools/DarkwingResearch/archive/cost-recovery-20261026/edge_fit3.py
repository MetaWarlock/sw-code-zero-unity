import csv, pickle, sys
idle,ph=pickle.load(open('city.pkl','rb'))
S=[0,2,5,7,11,13,15,17,19,21,23,25,28,32,34,36,38,40,42,44,46,49,53,55,57,59,61,63,65,67,70,74,76,78,80,82,84,86,88,90]
rows={int(r['poll']):r for r in csv.DictReader(open(sys.argv[1]))}
def true_edge(p):
    r=rows[p]
    if p<=733 and r['resetEdge']: return int(r['resetEdge'])
    E=int(r['T'])
    if 291<=p<=832: E-=4
    return E
lo=-10**9; hi=10**9; n=0
first=None
for p in sorted(rows):
    c000=ph.get(p,{}).get('C000')
    if not c000: continue
    A=c000[0]-7
    nidle=[r for r in idle.get(p,[]) if r['event']=='nmi_idle']
    E=true_edge(p)
    if nidle:
        I=int(nidle[0]['last_idle']); s=(A-I)%93; idx=S.index(s)
        prev=A-(s-S[idx-1]) if idx>0 else A-3
    elif p==6: prev=A-2
    else: continue
    l=prev*12-E+1; h=A*12-E
    lo=max(lo,l); hi=min(hi,h); n+=1
    if lo>hi and first is None: first=(p,lo,hi)
print('frames',n,'d window',lo,hi,'first conflict',first)
