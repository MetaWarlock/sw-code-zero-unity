import sys,mmap
sys.stdout.reconfigure(encoding='utf-8')
f=open(sys.argv[1],'rb'); m=mmap.mmap(f.fileno(),0,access=mmap.ACCESS_READ)
for a in sys.argv[2:]:
    o,before,after=[int(x) for x in a.split(':')]
    print('=====',o); print(m[o-before:o+after].decode('utf-8','replace'))
