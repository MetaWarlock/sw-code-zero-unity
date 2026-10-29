import csv, collections, pickle, sys
idle,ph=pickle.load(open(r'C:\Users\Warlock\AppData\Local\Temp\claude\C--Users-Warlock--claude-spares-spare-G7WX1P\6941d150-1f81-420b-8d72-3a6e54e688be\scratchpad\city.pkl','rb'))
rows=list(csv.DictReader(open(sys.argv[1] if len(sys.argv)>1 else 'Temp/city_cost_coverage.csv')))
def g(p,k,i=0):
    v=ph.get(p,{}).get(k); return v[i] if v and len(v)>i else None
stat=collections.defaultdict(collections.Counter); first=collections.defaultdict(list)
def I(x):
    try: return int(x)
    except: return None
for r in rows:
    p=int(r['poll'])
    # NMI of this poll
    c000=g(p,'C000'); a1=g(p,'C0A1'); bf=g(p,'C0BF'); dc=g(p,'C0DC'); e114=g(p,'C114')
    if c000 and a1:
        src=a1-c000; nat=I(r['pub1'] if (c000+55)%2 else r['pub0'])
        k='pub'; ok=(nat==src)
        stat[k]['ok' if ok else ('unsup' if nat in (None,-1) else 'bad')]+=1
        if not ok and len(first[k])<6: first[k].append((p,nat,src,r['pubWhy']))
    if dc and e114:
        src=e114+6-dc; nat=I(r['audio']); ok=nat==src
        stat['audio']['ok' if ok else ('unsup' if nat in (None,-1) else 'bad')]+=1
        if not ok and len(first['audio'])<6: first['audio'].append((p,nat,src))
    if a1 and bf:
        src=bf-a1; nat=I(r['timers']); 
        if nat is not None:
            stat['timers']['ok' if nat==src else 'bad']+=1
            if nat!=src and len(first['timers'])<6: first['timers'].append((p,nat,src))
    if r['head']=='': 
        if g(p,'CAE2'): stat['ran']['src_only']+=1; 
        continue
    if not g(p,'CAE2'): stat['ran']['native_only']+=1; continue
    stat['ran']['both']+=1
    fecf=g(p,'FECF'); ca40=g(p,'CA40'); cae2=g(p,'CAE2'); cabb=g(p,'CABB'); cad8=g(p,'CAD8'); cadf=g(p,'CADF')
    head=I(r['head']); hook=I(r['hook'])
    stat['resume']['ok' if 6+hook+18+head==ca40-fecf else 'bad']+=1
    comps=dict(player=I(r['player']),scroll=I(r['scroll']),spawn=I(r['spawn']),zone=I(r['zone']),palette=I(r['palette']),bank6=I(r['bank6']),bank1=I(r['bank1']),tail=I(r['tail']))
    # boundary checks
    def chk(name,nat,src,why=''):
        if nat is None or nat<0: stat[name]['unsup']+=1; 
        elif nat==src: stat[name]['ok']+=1
        else: stat[name]['bad']+=1
        if (nat is None or nat!=src) and len(first[name])<6: first[name].append((p,nat,src,why))
    ca59=g(p,'CA59'); ca6b=g(p,'CA6B'); ca79=g(p,'CA79'); ca7c=g(p,'CA7C'); ca84=g(p,'CA84'); ca8c=g(p,'CA8C')
    chk('player', None if comps['player'] is None or comps['player']<0 else comps['player']+6+3+3+3+3-12, None if not ca59 else ca59-ca40-12, r['playerWhy'])
    chk('scroll', comps['scroll'], (ca6b-ca59) if ca6b and ca59 else None, r['scrollWhy'])
    chk('spawn', comps['spawn'], (ca79-ca6b) if ca79 else None, r['spawnWhy'])
    chk('zone', comps['zone'], (ca7c-ca79) if ca7c else None, r['zoneWhy'])
    chk('palette', comps['palette'], (ca84-ca7c) if ca84 else None, r['paletteWhy'])
    chk('bank6', comps['bank6'], (ca8c-ca84) if ca8c else None, r['bank6Why'])
    b1=None if comps['bank1'] is None or comps['bank1']<0 or comps['tail'] is None or comps['tail']<0 else comps['bank1']+comps['tail']
    chk('bank1tail', b1, (cabb-ca8c) if cabb and ca8c else None, r['bank1Why']+'|'+r['tailWhy'])
    walk=I(r['walk']); chk('walk', walk+8 if walk else None, (cad8-cabb) if cad8 and cabb else None)
    chk('rng', I(r['rng']), (cadf-cad8) if cadf and cad8 else None)
for k,v in stat.items(): print(k,dict(v))
for k,v in first.items(): print(k,v)
