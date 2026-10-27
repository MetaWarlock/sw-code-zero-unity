import pickle, collections
idle,ph=pickle.load(open('city.pkl','rb'))
S=[0,2,5,7,11,13,15,17,19,21,23,25,28,32,34,36,38,40,42,44,46,49,53,55,57,59,61,63,65,67,70,74,76,78,80,82,84,86,88,90]
polls={p:int([r for r in rows if r['event']=='poll'][0]['cycle']) for p,rows in idle.items() if any(r['event']=='poll' for r in rows)}
iv=collections.defaultdict(lambda:[-99,99])
for p,rows in idle.items():
    n=[r for r in rows if r['event']=='nmi_idle']
    if not n or p not in polls or p-1 not in polls: continue
    P=polls[p]; A=int(n[0]['cycle'])-7; I=int(n[0]['last_idle'])
    s=(A-I)%93; idx=S.index(s)
    prev = A-(s-S[idx-1]) if idx>0 else A-3  # previous boundary (JMP 3 cycles before scan start)
    key=(P-polls[p-1], p%2)
    lo,hi=iv[key]
    iv[key]=[max(lo,prev-P),min(hi,A-P)]   # T in (prev, A]
for k,v in sorted(iv.items()): print(k,v)
