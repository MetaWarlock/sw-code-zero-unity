import csv, collections, sys
src=collections.defaultdict(list)
with open('Temp/city_bank6_slots.csv') as f:
    r=csv.reader(f); next(r)
    for row in r:
        p=int(row[0]); pc=row[2]; bank=row[3]
        if bank!='06' or pc not in ('8008','8027','8032'): continue
        src[p].append((pc,int(row[4],16),int(row[1])))
def seg(p):
    beh={}; post={}; t8008={}; t8032={}
    for pc,x,c in src.get(p,[]):
        if pc=='8008': t8008[x]=c
        elif pc=='8027':
            if x in t8008: beh[x]=c-t8008[x]
        elif pc=='8032': t8032[x]=c
    for x,c in t8032.items():
        if x in t8008: post[x]=t8008[x]-c
    return beh,post
stat=collections.defaultdict(collections.Counter); ex={}
lim=int(sys.argv[1]) if len(sys.argv)>1 else 6387
for row in csv.DictReader(open('Temp/city_bank6_native.csv')):
    p=int(row['poll'])
    if p>lim: continue
    n=int(row['slot']); beh,post=seg(p)
    key=f"{row['behavior']}/{int(row['state'],16)&15:X}" if row['active']=='1' else 'inactive'
    if n in beh:
        nat=int(row['envelope'])+max(0,int(row['behaviorCycles'])) if int(row['behaviorCycles'])>=0 else -1
        if int(row['behaviorCycles'])==-2: nat=int(row['envelope'])
        k='beh '+key
        res='ok' if nat==beh[n] else ('unsup' if nat<0 else 'bad')
        stat[k][res]+=1
        if res!='ok' and (k,res) not in ex: ex[(k,res)]=(p,n,nat,beh[n],row['why'])
    if n in post:
        nat=int(row['post'])
        k='post '+key
        res='ok' if nat==post[n] else ('unsup' if nat<0 else 'bad')
        stat[k][res]+=1
        if res!='ok' and (k,res) not in ex: ex[(k,res)]=(p,n,nat,post[n],row['why'])
for k in sorted(stat): print(k,dict(stat[k]))
for k,v in ex.items(): print(k,v)
