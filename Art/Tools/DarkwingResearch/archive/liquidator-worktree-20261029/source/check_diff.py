import sys
from pathlib import Path
sys.path.insert(0, ".")
from Tools.DarkwingResearch.stand.stand import read_trace, read_port_trace

orig, ofull = read_trace(Path('Temp/m3_original_validation/replay1/trace.csv'))
port, pfull = read_port_trace(Path('Temp/m3_port_12k_platform_hazard/trace.csv'))

max_f = min(max(orig.keys()), max(port.keys()))
print(f"Total frames to check: {max_f}")

first_gameplay_div = None
for f in range(8, max_f + 1):
    o = orig[f]
    p = port[f]
    if (o['x'], o['y'], o['state']) != (p['x'], p['y'], p['state']):
        first_gameplay_div = f
        print(f"First gameplay divergence at frame {f}:")
        print(f"  orig: x={o['x']} y={o['y']} state={o['state']}")
        print(f"  port: x={p['x']} y={p['y']} state={p['state']}")
        break

if not first_gameplay_div:
    print(f"All {max_f} frames have identical gameplay (x, y, state)!")

# Also check nt/oam differences
print("\nChecking buffer sums (nt, oam)...")
for f in range(8, min(8000, max_f + 1)):
    if f in ofull and f in pfull:
        ont, ooam = ofull[f]
        pnt, poam = pfull[f]
        if (ont, ooam) != (pnt, poam):
            print(f"First buffer sum divergence at frame {f}: orig=({ont}, {ooam}) port=({pnt}, {poam})")
            break
