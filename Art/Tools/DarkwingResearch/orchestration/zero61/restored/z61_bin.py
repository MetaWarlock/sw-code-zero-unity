import sys,re,mmap
sys.stdout.reconfigure(encoding='utf-8')
f=open(sys.argv[1],'rb'); m=mmap.mmap(f.fileno(),0,access=mmap.ACCESS_READ)
print('size',len(m))
for pat in [b'retry-after-ms', b'Free usage limit', b'FreeUsageLimitError', b'type: "retry"', b'type:"retry"']:
    offs=[x.start() for x in re.finditer(re.escape(pat),m)][:6]
    print(pat,offs)
