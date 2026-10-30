import json,sys,collections,glob
sys.stdout.reconfigure(encoding='utf-8')
fn=sys.argv[1]
c=collections.Counter(); stops=collections.Counter(); first=last=None; turns=0; errs=[]
for l in open(fn,encoding='utf-8'):
    try: o=json.loads(l)
    except: c['bad']+=1; continue
    t=o.get('type'); m=o.get('method'); p=o.get('params') if isinstance(o.get('params'),dict) else {}
    u=p.get('update') if isinstance(p.get('update'),dict) else {}
    k=t or m or ('result' if 'result' in o else 'error' if 'error' in o else '?')
    if u: k+=':'+str(u.get('sessionUpdate'))
    c[k]+=1
    if t=='turn_result': stops[json.dumps(o.get('result'),ensure_ascii=False)[:160]]+=1
    if 'error' in o: errs.append(json.dumps(o['error'],ensure_ascii=False)[:200])
print(c.most_common(25)); print(stops.most_common(5)); print(errs[:3])
