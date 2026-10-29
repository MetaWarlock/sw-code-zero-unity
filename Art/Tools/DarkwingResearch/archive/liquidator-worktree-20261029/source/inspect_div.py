import sys
from pathlib import Path
sys.path.insert(0, ".")
from Tools.DarkwingResearch.stand.stand import read_trace, read_port_trace

orig, ofull = read_trace(Path('Temp/m3_original_validation/replay1/trace.csv'))
port, pfull = read_port_trace(Path('Temp/m3_original_validation/unity/trace.csv'))

print("Frame | Orig (x, y, st, ctrl) | Port (x, y, st, ctrl)")
for f in range(7668, 7682):
    o = orig[f]
    p = port[f]
    print(f"{f:5d} | orig: x={o['x']:4d} y={o['y']:4d} s={o['state']:2d} c={o['ctrl']:2d} | port: x={p['x']:4d} y={p['y']:4d} s={p['state']:2d} c={p['ctrl']:2d}")
