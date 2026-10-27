"""Reconstruct reduced CSVs from preserved local pickle, not a new ROM capture.

The recovered pickle has poll/PC/cycle and idle rows; lost register columns cannot
be reconstructed. Only load trusted local pickle data. Refuse output overwrite.
"""
import argparse, csv, os, pickle
from pathlib import Path

DATA = Path(__file__).resolve().parents[3] / 'Research/DarkwingDuck/reference/cost-harness/data'
parser = argparse.ArgumentParser(description=__doc__)
parser.add_argument('output', nargs='?', type=Path, default=DATA / 'phases')
parser.add_argument('--reference', type=Path, default=DATA / 'city_phase.pkl')
args = parser.parse_args()
out_dir = args.output
os.makedirs(out_dir, exist_ok=True)
for name in ('city_idle_phase.csv', 'city_phases.csv'):
    if (out_dir / name).exists():
        parser.error(f'Output exists, use a new directory: {out_dir / name}')
with args.reference.open('rb') as source:
    idle, ph = pickle.load(source)

with open(os.path.join(out_dir, 'city_idle_phase.csv'), 'w', newline='') as f:
    columns = list(next(iter(idle.values()))[0].keys())
    w = csv.DictWriter(f, fieldnames=columns)
    w.writeheader()
    for poll in sorted(idle):
        for row in idle[poll]:
            w.writerow(row)

with open(os.path.join(out_dir, 'city_phases.csv'), 'w', newline='') as f:
    w = csv.writer(f)
    w.writerow(['poll', 'cycle', 'pc'])
    for poll in sorted(ph):
        for pc in sorted(ph[poll]):
            for cycle in ph[poll][pc]:
                w.writerow([poll, cycle, pc])

commits = sum(1 for poll in idle for row in idle[poll] if row.get('event') == 'commit')
print(f"{out_dir}/city_idle_phase.csv polls={len(idle)} commit-rows={commits}; "
      f"{out_dir}/city_phases.csv polls={len(ph)} marks={sum(len(v) for v in ph.values())}")
