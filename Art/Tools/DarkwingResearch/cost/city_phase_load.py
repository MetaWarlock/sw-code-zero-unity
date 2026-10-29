"""Pack existing original phase CSVs; both full and reduced PC/cycle schema work.

This packs evidence, it does not obtain new evidence or recover lost registers.
"""
import argparse, csv, collections, pickle
from pathlib import Path
parser = argparse.ArgumentParser(description=__doc__)
parser.add_argument('output', type=Path)
parser.add_argument('--idle', required=True, type=Path)
parser.add_argument('--phases', required=True, type=Path)
args = parser.parse_args()
if args.output.exists():
    parser.error('Output exists; never overwrite the preserved reference')
idle=collections.defaultdict(list)
with args.idle.open() as f:
    r=csv.DictReader(f)
    for row in r:
        idle[int(row['poll'])].append(row)
ph=collections.defaultdict(lambda: collections.defaultdict(list))
with args.phases.open() as f:
    r=csv.DictReader(f)
    if not {'poll','cycle','pc'} <= set(r.fieldnames or []):
        parser.error('Missing poll/cycle/pc columns')
    for row in r:
        ph[int(row['poll'])][row['pc']].append(int(row['cycle']))
args.output.parent.mkdir(parents=True, exist_ok=True)
with args.output.open('xb') as target:
    pickle.dump((dict(idle),{k:dict(v) for k,v in ph.items()}), target)
