from pathlib import Path
import sys
sys.path.insert(0, ".")
from Tools.DarkwingResearch.stand.stand import read_trace, read_port_trace

orig, _ = read_trace(Path('Temp/m3_original_validation/replay1/trace.csv'))
port, _ = read_port_trace(Path('Temp/m3_port_12k_platform_hazard/trace.csv'))

# Compare positions every 100 frames up to 12000
print("Sample comparison every 100 frames:")
for f in range(10000, 12001, 200):
    o = orig[f]
    p = port[f]
    print(f"Frame {f:5d}: Orig=({o['x']:4d}, {o['y']:4d}, st={o['state']:02X}) | Port=({p['x']:4d}, {p['y']:4d}, st={p['state']:02X}) | dx={p['x']-o['x']} dy={p['y']-o['y']}")
