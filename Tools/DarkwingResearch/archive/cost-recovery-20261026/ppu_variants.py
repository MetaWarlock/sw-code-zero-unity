import sys
import phase_route as PR   # runs once with defaults (prints log)
from phase_sim import frames, polls, boundaries
base_Toff=PR.Toff
results={}
for name,mod in [('d80+1',lambda p,d:1 if d==29780 else 0),('d80-1',lambda p,d:-1 if d==29780 else 0),
                 ('d81+1',lambda p,d:1 if d==29781 else 0),('d81-1',lambda p,d:-1 if d==29781 else 0),
                 ('all+1',lambda p,d:1),('all-1',lambda p,d:-1)]:
    PR.Toff=lambda p,mod=mod: base_Toff(p)+mod(p,polls[p]-polls[p-1])
    src=open('phase_route.py').read().split("span=int")[1]
    src="span=int"+src
    g=dict(PR.__dict__); g['__name__']='v'
    import io,contextlib
    buf=io.StringIO()
    with contextlib.redirect_stdout(buf):
        sys.argv=['x','200','29359']; exec(compile(src,'v','exec'),g)
    print('==',name); print(buf.getvalue())
