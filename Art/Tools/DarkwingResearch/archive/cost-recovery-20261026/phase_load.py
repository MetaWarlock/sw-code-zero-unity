import csv, collections, pickle, sys
idle=collections.defaultdict(list)
with open('Temp/city_idle_phase.csv') as f:
    r=csv.DictReader(f)
    for row in r:
        idle[int(row['poll'])].append(row)
ph=collections.defaultdict(lambda: collections.defaultdict(list))
with open('Temp/city_phases.csv') as f:
    r=csv.reader(f); next(r)
    for p,c,pc,bank,x,y,sp,e4,br in r:
        ph[int(p)][pc].append(int(c))
pickle.dump((dict(idle),{k:dict(v) for k,v in ph.items()}),open(sys.argv[1],'wb'))
