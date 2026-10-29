from pathlib import Path
import sys
sys.path.insert(0, ".")
from Tools.DarkwingResearch.stand.stand import read_trace, read_port_trace

orig, _ = read_trace(Path('Temp/m3_original_validation/replay1/trace.csv'))
port, _ = read_port_trace(Path('Temp/m3_port_12k_platform_hazard/trace.csv'))

for f in range(10080, 10100):
    o = orig[f]
    p = port[f]
    p_shift = port[f - 1]
    print(f"Orig {f:5d}: x={o['x']:4d} y={o['y']:4d} st={o['state']:02X} | Port {f:5d}: x={p['x']:4d} | Port-1: x={p_shift['x']:4d}")
