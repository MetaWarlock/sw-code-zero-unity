import csv, pickle, sys
idle,ph=pickle.load(open('city.pkl','rb'))
S=[0,2,5,7,11,13,15,17,19,21,23,25,28,32,34,36,38,40,42,44,46,49,53,55,57,59,61,63,65,67,70,74,76,78,80,82,84,86,88,90]
rows={int(r['poll']):r for r in csv.DictReader(open(sys.argv[1]))}
segs=[]; lo=-10**9; hi=10**9; start=None; last=None
for p in sorted(rows):
    E=int(rows[p]['T'])
    c000=ph.get(p,{}).get('C000')
    if not c000: continue
    A=c000[0]-7
    n=[r for r in idle.get(p,[]) if r['event']=='nmi_idle']
    if not n: continue
    I=int(n[0]['last_idle']); s=(A-I)%93; idx=S.index(s)
    prev=A-(s-S[idx-1]) if idx>0 else A-3
    l=prev*12-E+1; h=A*12-E
    nl,nh=max(lo,l),min(hi,h)
    if nl>nh:
        segs.append((start,last,lo,hi)); lo,hi=l,h; start=p
    else: lo,hi=nl,nh; start=start or p
    last=p
segs.append((start,last,lo,hi))
for s in segs: print(s)
