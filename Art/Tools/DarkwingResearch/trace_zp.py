"""Print selected RAM bytes per frame from a probe_run capture.

Usage:
  python trace_zp.py Art/Research/DarkwingDuck/reference/gold_walk 0x31 0x22 0xFA
  python trace_zp.py <dir> --start 1 --count 120 addr[ ...]

Addresses are NES zero-page/RAM offsets (0x000-0x7FF). Player slots live at
0x400 + 0x10*N, so a byte like 0x4F0 is player X-lo.
"""
from __future__ import annotations
import argparse
from pathlib import Path


def main():
    p = argparse.ArgumentParser(description=__doc__)
    p.add_argument("capture", type=Path)
    p.add_argument("addresses", nargs="+")
    p.add_argument("--start", type=int, default=1)
    p.add_argument("--count", type=int, default=0)
    p.add_argument("--stride", type=int, default=1)
    p.add_argument("--hex", action="store_true", default=True)
    args = p.parse_args()
    addrs = [int(a, 16) if a.lower().startswith("0x") else int(a) for a in args.addresses]
    ram = args.capture / "ram_frames.bin"
    data = ram.read_bytes()
    n = len(data) // 2048
    end = min(n, args.start + (args.count if args.count else n))
    for f in range(args.start, end, args.stride):
        frame = data[f * 2048:(f + 1) * 2048]
        vals = " ".join(f"{a:02X}={frame[a]:02X}" for a in addrs)
        print(f"f{f:4d} {vals}")


if __name__ == "__main__":
    main()
