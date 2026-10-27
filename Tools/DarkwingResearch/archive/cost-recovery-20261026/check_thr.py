import pickle, collections
idle,ph=pickle.load(open('city.pkl','rb'))
S=[0,2,5,7,11,13,15,17,19,21,23,25,28,32,34,36,38,40,42,44,46,49,53,55,57,59,61,63,65,67,70,74,76,78,80,82,84,86,88,90]
def first_boundary(I,T):
    # scan boundaries I+93j+s
    if T<=I: return None
    j=(T-I)//93
    for jj in (j,j+1):
        for s in S:
            b=I+93*jj+s
            if b>=T: return b
res=collections.Counter(); bad=[]
for p,rows in idle.items():
    poll=[r for r in rows if r['event']=='poll']
    n=[r for r in rows if r['event']=='nmi_idle']
    if not poll or not n: continue
    P=int(poll[0]['cycle']); A=int(n[0]['cycle'])-7; I=int(n[0]['last_idle'])
    for k in range(0,5):
        if first_boundary(I,P+k)==A: res[k]+=1
    if first_boundary(I,P+1)!=A: bad.append((p,P,A,I,(A-I)%93,A-P))
print(res, len(bad)); print(bad[:20])
