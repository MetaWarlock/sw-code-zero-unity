import re,sys
sys.stdout.reconfigure(encoding='utf-8')
L=open(sys.argv[1],encoding='utf-8',errors='replace').read().splitlines()
pat=re.compile(r'(?i)(authorization|bearer|api[-_]?key|x-api-key|token|secret|password|cookie)(["\'=: \\]+)[^\s,"\'}\\]+')
for l in L[-62:]:
    if 'level=ERROR' in l:
        s=pat.sub(r'\1\2<REDACTED>',l)
        s=re.sub(r'sk-[A-Za-z0-9_\-]{8,}','<REDACTED>',s)
        print(len(l)); print(s[:2500]); print('----')
