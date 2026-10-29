from pathlib import Path
import sys
sys.path.insert(0, ".")
from Tools.DarkwingResearch.stand.stand import read_trace, read_port_trace

orig, _ = read_trace(Path('Temp/m3_original_validation/replay1/trace.csv'))
port, _ = read_port_trace(Path('Temp/m3_port_12k_platform_hazard/trace.csv'))

for f in range(11150, 11210, 5):
    o = orig[f]; p = port[f]
    print(f"F {f}: Orig=({o['x']}, {o['y']}, st={o['state']:02X}) | Port=({p['x']}, {p['y']}, st={p['state']:02X})")
