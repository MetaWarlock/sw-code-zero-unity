import pickle, collections, sys
idle,ph=pickle.load(open('city.pkl','rb'))
S=[0,2,5,7,11,13,15,17,19,21,23,25,28,32,34,36,38,40,42,44,46,49,53,55,57,59,61,63,65,67,70,74,76,78,80,82,84,86,88,90]
STARTUP=[0,2,4,6,10,13,15]   # FF19..FE91 boundaries relative to commit; FE92 at +17
def ev(p,name):
    return [int(r['cycle']) for r in idle.get(p,[]) if r['event']==name]
polls={p:ev(p,'poll')[0] for p in idle if ev(p,'poll')}
frames={}
for p in sorted(idle):
    if p not in polls: continue
    c000=ph.get(p,{}).get('C000',[None])[0]
    c114=ph.get(p,{}).get('C114',[None])[0]
    c023=ph.get(p,{}).get('C023',[None])[0]
    ready=ev(p,'ready'); commit=ev(p,'commit'); launch=ev(p,'launch')
    frames[p]=dict(P=polls[p],A=(c000-7) if c000 else None,H=(c114+6-(c000-7)) if c114 and c000 else None,
                  dma=c023, ready=ready, commit=commit, launch=launch)
def boundaries(C):
    for s in STARTUP: yield C+s, ('start',s)
    I=C+17; j=0
    while True:
        for s in S: yield I+93*j+s, ('scan',s,I+93*j)
        j+=1
def step(Cprev,p,Toff=1):
    f=frames[p]; T=f['P']+Toff
    if Cprev-4>=T: return None,'cross'
    for b,info in boundaries(Cprev):
        if b>=T: A=b; break
    H=f['H']
    if f['dma'] is not None:
        d=A-f['A']
        if d%2: H+= -1 if f['dma']%2==0 else 1
    R=A+H
    if info[0]=='start':
        # continue startup to FE92 at C+17, then clear $90 and dispatch first pass
        ready=R+(17-info[1])+16
    else:
        s,scan=info[1],info[2]
        if s in (0,2): ready=R+16-s
        elif s in (5,7): ready=R+38-s
        else: ready=R+109-s
    # actual body cost from final ready to commit
    rf=[r for r in f['ready'] if r<=f['launch'][0]][-1]
    C=ready+(f['commit'][0]-rf)
    return C,None
def check_actual(p0,p1):
    bad=0
    for p in range(p0,p1):
        f=frames.get(p); g=frames.get(p+1)
        if not f or not g or not f['commit'] or not g['commit'] or not g['launch']: continue
        C,err=step(f['commit'][-1],p+1)
        if C!=g['commit'][0]: bad+=1; 
        if bad and bad<5 and C!=g['commit'][0]: print('mismatch',p+1,C,g['commit'][0],C-g['commit'][0])
    print('actual-check bad',bad)
if __name__=='__main__':
    p0,p1=int(sys.argv[1]),int(sys.argv[2])
    check_actual(p0,p1)
