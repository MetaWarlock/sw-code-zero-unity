import sys
from pathlib import Path
sys.path.insert(0, ".")
from Tools.DarkwingResearch.stand.stand import read_trace, read_port_trace

orig, _ = read_trace(Path('Temp/m3_original_validation/replay1/trace.csv'))
port, _ = read_port_trace(Path('Temp/m3_port_12k_platform_hazard/trace.csv'))

for f in range(10050, 10075):
    o = orig[f]
    p = port[f]
    print(f"Frame {f:5d}: orig x={o['x']:4d} y={o['y']:4d} st={o['state']:02X} | port x={p['x']:4d} y={p['y']:4d} st={p['state']:02X}")
