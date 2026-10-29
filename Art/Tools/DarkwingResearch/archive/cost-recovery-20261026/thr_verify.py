import csv, pickle, sys, collections
idle,ph=pickle.load(open('city.pkl','rb'))
S=[0,2,5,7,11,13,15,17,19,21,23,25,28,32,34,36,38,40,42,44,46,49,53,55,57,59,61,63,65,67,70,74,76,78,80,82,84,86,88,90]
rows={int(r['poll']):r for r in csv.DictReader(open(sys.argv[1]))}
ok=bad=0; badl=[]; loose=0
for p in sorted(rows):
    T=int(rows[p]['T'])
    c000=ph.get(p,{}).get('C000')
    if not c000: continue
    A=c000[0]-7
    n=[r for r in idle.get(p,[]) if r['event']=='nmi_idle']
    if n:
        I=int(n[0]['last_idle']); s=(A-I)%93; idx=S.index(s)
        prev=A-(s-S[idx-1]) if idx>0 else A-3
        good= prev<T<=A
    else:
        good= A-7<T<=A; loose+=1
    if good: ok+=1
    else:
        bad+=1
        if len(badl)<25: badl.append((p,T,A,A-T,'idle' if n else 'other'))
print('ok',ok,'bad',bad,'(loose',loose,')'); print(badl)
