import csv, collections, sys
sys.path.insert(0, r'C:\Users\Warlock\AppData\Local\Temp\claude\C--Users-Warlock--claude-spares-spare-G7WX1P\6941d150-1f81-420b-8d72-3a6e54e688be\scratchpad')
exec(open(r'C:\Users\Warlock\AppData\Local\Temp\claude\C--Users-Warlock--claude-spares-spare-G7WX1P\6941d150-1f81-420b-8d72-3a6e54e688be\scratchpad\slots.py').read().split('stat=collections')[0])
want=sys.argv[1]; keys=sys.argv[2].split(',')
c=collections.Counter(); ex={}
for row in csv.DictReader(open('Temp/city_bank6_native.csv')):
    p=int(row['poll'])
    if p>6387 or row['active']!='1' or f"{row['behavior']}/{int(row['state'],16)&15:X}"!=want: continue
    n=int(row['slot']); beh,post=seg(p)
    if n not in beh: continue
    nat=int(row['envelope'])+int(row['behaviorCycles']) if int(row['behaviorCycles'])>=0 else None
    if nat is None: continue
    k=(beh[n]-nat,)+tuple(row[x] for x in keys)
    c[k]+=1; ex.setdefault(k,p)
for k,v in sorted(c.items(), key=lambda kv:-kv[1])[:30]: print(k,v,ex[k])
