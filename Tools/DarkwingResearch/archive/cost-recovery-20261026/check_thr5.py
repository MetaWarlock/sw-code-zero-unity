import pickle, collections
idle,ph=pickle.load(open('city.pkl','rb'))
S=[0,2,5,7,11,13,15,17,19,21,23,25,28,32,34,36,38,40,42,44,46,49,53,55,57,59,61,63,65,67,70,74,76,78,80,82,84,86,88,90]
polls={p:int([r for r in rows if r['event']=='poll'][0]['cycle']) for p,rows in idle.items() if any(r['event']=='poll' for r in rows)}
ps=sorted(polls); ep=0; epoch={}; prevd=None; starts=[(ps[0],0)]
for p in ps:
    if p-1 in polls:
        d=polls[p]-polls[p-1]
        if prevd is not None and d==prevd: ep+=1; starts.append((p,ep))
        prevd=d
    epoch[p]=ep
iv={}
for p in ps:
    rows=idle[p]; n=[r for r in rows if r['event']=='nmi_idle']
    if not n or p-1 not in polls: continue
    P=polls[p]; A=int(n[0]['cycle'])-7; I=int(n[0]['last_idle'])
    s=(A-I)%93; idx=S.index(s)
    prev = A-(s-S[idx-1]) if idx>0 else A-3
    key=(epoch[p],polls[p]-polls[p-1])
    lo,hi,c,f=iv.get(key,(-99,99,0,p))
    iv[key]=(max(lo,prev-P),min(hi,A-P),c+1,f)
print(starts[:60])
for k,v in sorted(iv.items()):
    if v[2]>2: print(k,v)
