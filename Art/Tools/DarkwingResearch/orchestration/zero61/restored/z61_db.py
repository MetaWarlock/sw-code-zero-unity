import sqlite3,sys,json
sys.stdout.reconfigure(encoding='utf-8')
c=sqlite3.connect('file:'+sys.argv[1]+'?mode=ro',uri=True)
tabs=[r[0] for r in c.execute("select name from sqlite_master where type='table'")]
print(tabs)
sid='ses_e743e4145ffe0TjzxyPjHXVtY5'
for t in tabs:
    cols=[r[1] for r in c.execute(f'pragma table_info("{t}")')]
    if 'session_id' in cols:
        n=c.execute(f'select count(*) from "{t}" where session_id=?',(sid,)).fetchone()[0]
        print(t,cols,n)
