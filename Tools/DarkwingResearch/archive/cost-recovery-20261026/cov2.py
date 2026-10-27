import csv, collections, pickle, sys
idle,ph=pickle.load(open(r'C:\Users\Warlock\AppData\Local\Temp\claude\C--Users-Warlock--claude-spares-spare-G7WX1P\6941d150-1f81-420b-8d72-3a6e54e688be\scratchpad\city.pkl','rb'))
rows=list(csv.DictReader(open(sys.argv[1] if len(sys.argv)>1 else 'Temp/city_cost_coverage.csv')))
lim=int(sys.argv[2]) if len(sys.argv)>2 else 6387
def g(p,k,i=0):
    v=ph.get(p,{}).get(k); return v[i] if v and len(v)>i else None
def I(x):
    try: return int(x)
    except: return None
diff=collections.defaultdict(collections.Counter); why=collections.defaultdict(collections.Counter); firstbad={}
pairs=[('player','CA40','CA59'),('scroll','CA59','CA6B'),('spawn','CA6B','CA79'),('zone','CA79','CA7C'),('palette','CA7C','CA84'),('bank6','CA84','CA8C')]
for r in rows:
    p=int(r['poll'])
    if p>lim: break
    c000=g(p,'C000'); a1=g(p,'C0A1')
    if c000 and a1:
        src=a1-c000; n0,n1=I(r['pub0']),I(r['pub1'])
        k='ok' if src in (n0,n1) else ('unsup' if n0 in (None,-1) else 'bad')
        diff['pub'][k]+=1
        if k!='ok' and 'pub' not in firstbad: firstbad['pub']=(p,n0,n1,src,r['pubWhy'])
    dc=g(p,'C0DC'); e=g(p,'C114')
    if dc and e:
        src=e+6-dc; n=I(r['audio']); k='ok' if n==src else ('unsup' if n in (None,-1) else 'bad')
        diff['audio'][k]+=1
        if k=='bad': diff['audio_d'][src-n]+=1
    if r['head']=='' or not g(p,'CAE2'): continue
    for name,a,b in pairs:
        n=I(r[name]); s=(g(p,b)-g(p,a)) if g(p,a) and g(p,b) else None
        if n is None or n<0: diff[name]['unsup']+=1; why[name][r.get(name+'Why','')]+=1; continue
        diff[name][s-n if s is not None else 'nosrc']+=1
        if s is not None and name not in firstbad and s-n not in (0,): pass
    n1,t=I(r['bank1']),I(r['tail'])
    s=(g(p,'CABB')-g(p,'CA8C')) if g(p,'CABB') and g(p,'CA8C') else 0
    if n1 is None or n1<0 or t is None or t<0: diff['bank1tail']['unsup']+=1; why['bank1tail'][r['bank1Why']+'|'+r['tailWhy']]+=1
    else: diff['bank1tail'][s-n1-t]+=1
for k,v in diff.items(): print(k,dict(v.most_common(12)))
for k,v in why.items(): print('WHY',k,dict(v.most_common(8)))
print(firstbad)
