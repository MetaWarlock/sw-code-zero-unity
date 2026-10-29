import re, sys
# usage: xtext.py FILE NEEDLE [CHARS]
s = open(sys.argv[1], encoding='utf-8').read()
n = int(sys.argv[3]) if len(sys.argv) > 3 else 8000
for m in re.finditer(re.escape(sys.argv[2]), s):
    t = re.sub(r'<[^>]+>', ' ', s[m.start():m.start() + n])
    print(re.sub(r'\s+', ' ', t))
    print('-----')
    break
