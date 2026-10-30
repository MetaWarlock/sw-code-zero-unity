import sqlite3,sys,json
sys.stdout.reconfigure(encoding='utf-8')
c=sqlite3.connect('file:'+sys.argv[1]+'?mode=ro',uri=True)
sid='ses_e743e4145ffe0TjzxyPjHXVtY5'
for row in c.execute('select id,time_created,time_updated,data from message where session_id=? order by time_created',(sid,)):
    d=json.loads(row[3])
    keep={k:d.get(k) for k in ('role','time','error','finish','modelID','providerID','mode','agent','tokens','cost')}
    print(row[0],row[1],row[2],json.dumps(keep,ensure_ascii=False)[:1500])
for row in c.execute('select id,message_id,time_created,data from part where session_id=?',(sid,)):
    d=json.loads(row[3]); print('part',row[1],d.get('type'),len(row[3]))
for row in c.execute("select id,time_created,time_updated from session where id=?",(sid,)): print('session',row)
cols=[r[1] for r in c.execute('pragma table_info(event)')]; print('event cols',cols)
