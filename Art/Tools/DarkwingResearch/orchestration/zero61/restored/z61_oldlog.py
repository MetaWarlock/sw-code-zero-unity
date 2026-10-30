import re,sys,json,glob,collections,os
sys.stdout.reconfigure(encoding='utf-8')
sid=None
for f in glob.glob(os.path.expanduser('~/.acpx/sessions/*.json')):
    if f.endswith('ndjson'): continue
    d=json.load(open(f,encoding='utf-8'))
    if d.get('name')=='ZERO-58': sid=d['acp_session_id']
print('old sid',sid)
c=collections.Counter(); steps=[]; first=last=None; errs=collections.Counter()
for l in open(sys.argv[1],encoding='utf-8',errors='replace'):
    if sid not in l: continue
    ts=l[10:34]; first=first or ts; last=ts
    m=re.search(r'message=("[^"]*"|\S+)',l); msg=m.group(1) if m else '?'
    c[msg]+=1
    s=re.search(r' step=(\d+)',l)
    if s: steps.append(int(s.group(1)))
    if 'level=ERROR' in l:
        e=re.search(r'error\.error="([^"]{0,140})',l); errs[e.group(1) if e else msg]+=1
print(first,last); print(c.most_common(15)); print('steps',len(steps),max(steps) if steps else None, 'step0 count',steps.count(0)); print(errs.most_common(5))
