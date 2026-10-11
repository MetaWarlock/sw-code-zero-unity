"""Compare a 256x240 Unity framebuffer PNG (NES-index-encoded via Mesen palette)
against a golden emulator PNG. Reports exact-pixel match stats and first differences.
Usage:
  python compare_frames.py GOLDEN.png UNITY.png [--max-report 20]
"""
from __future__ import annotations
import argparse
import struct
import sys
import zlib
from pathlib import Path

# Mesen 2C02 default palette (RGB, alpha ignored) — mirrors Assets/Darkwing/UnityApp/NesPalette.cs
MESEN_2C02 = [
    0x666666,0x002A88,0x1412A7,0x3B00A4,0x5C007E,0x6E0040,0x6C0600,0x561D00,
    0x333500,0x0B4800,0x005200,0x004F08,0x00404D,0x000000,0x000000,0x000000,
    0xADADAD,0x155FD9,0x4240FF,0x7527FE,0xA01ACC,0xB71E7B,0xB53120,0x994E00,
    0x6B6D00,0x388700,0x0C9300,0x008F32,0x007C8D,0x000000,0x000000,0x000000,
    0xFFFEFF,0x64B0FF,0x9290FF,0xC676FF,0xF36AFF,0xFE6ECC,0xFE8170,0xEA9E22,
    0xBCBE00,0x88D800,0x5CE430,0x45E082,0x48CDDE,0x4F4F4F,0x000000,0x000000,
    0xFFFEFF,0xC0DFFF,0xD3D2FF,0xE8C8FF,0xFBC2FF,0xFEC4EA,0xFECCC5,0xF7D8A5,
    0xE4E594,0xCFEF96,0xBDF4AB,0xB3F3CC,0xB5EBF2,0xB8B8B8,0x000000,0x000000,
]


def read_png_rgb(path: Path):
    data = path.read_bytes()
    assert data[:8] == b"\x89PNG\r\n\x1a\n", f"{path} is not a PNG"
    pos, w, h, bitd, ctype = 8, None, None, None, None
    idat = b""
    while pos < len(data):
        ln = struct.unpack_from(">I", data, pos)[0]
        kind = data[pos+4:pos+8]
        payload = data[pos+8:pos+8+ln]
        if kind == b"IHDR":
            w, h, bitd, ctype = struct.unpack_from(">IIBB", payload)
        elif kind == b"IDAT":
            idat += payload
        elif kind == b"IEND":
            break
        pos += 12 + ln
    raw = zlib.decompress(idat)
    assert bitd == 8, f"unsupported bit depth {bitd}"
    channels = {0: 1, 2: 3, 4: 2, 6: 4}[ctype]
    stride = w * channels
    # defilter
    out = bytearray(w * h * 3)
    prev = bytearray(stride)
    pos = 0
    for y in range(h):
        f = raw[pos]; pos += 1
        line = bytearray(raw[pos:pos+stride]); pos += stride
        if f == 1:
            for i in range(channels, stride):
                line[i] = (line[i] + line[i-channels]) & 0xFF
        elif f == 2:
            for i in range(stride):
                line[i] = (line[i] + prev[i]) & 0xFF
        elif f == 3:
            for i in range(stride):
                a = line[i-channels] if i >= channels else 0
                line[i] = (line[i] + ((a + prev[i]) >> 1)) & 0xFF
        elif f == 4:
            for i in range(stride):
                a = line[i-channels] if i >= channels else 0
                b = prev[i]
                c = prev[i-channels] if i >= channels else 0
                p = a + b - c
                pa, pb, pc = abs(p-a), abs(p-b), abs(p-c)
                pr = a if (pa <= pb and pa <= pc) else (b if pb <= pc else c)
                line[i] = (line[i] + pr) & 0xFF
        out[y*w*3:(y+1)*w*3] = bytes(v for i in range(0, stride, channels) for v in line[i:i+3])
        prev = line
    return w, h, bytes(out)


def to_index_map(w, h, rgb):
    """Map each pixel RGB to NES index via palette (exact match required)."""
    lut = {}
    for idx, v in enumerate(MESEN_2C02):
        lut[(v >> 16, (v >> 8) & 0xFF, v & 0xFF)] = idx
    idxs = bytearray(w*h)
    unmapped = 0
    for i in range(w*h):
        key = (rgb[i*3], rgb[i*3+1], rgb[i*3+2])
        v = lut.get(key)
        if v is None:
            unmapped += 1
            v = 0
        idxs[i] = v
    return bytes(idxs), unmapped


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("golden", type=Path)
    ap.add_argument("unity", type=Path)
    ap.add_argument("--max-report", type=int, default=20)
    args = ap.parse_args()
    w1, h1, rgb1 = read_png_rgb(args.golden)
    w2, h2, rgb2 = read_png_rgb(args.unity)
    if (w1, h1) != (w2, h2):
        print(json_result(False, f"size mismatch {w1}x{h1} vs {w2}x{h2}"))
        return 1
    idx1, unm1 = to_index_map(w1, h1, rgb1)
    idx2, unm2 = to_index_map(w2, h2, rgb2)
    diffs = [i for i in range(w1*h1) if idx1[i] != idx2[i]]
    result = {
        "golden": str(args.golden), "unity": str(args.unity),
        "pixels": w1*h1, "mismatched": len(diffs),
        "match_pct": round(100.0 * (w1*h1 - len(diffs)) / (w1*h1), 4),
        "unmapped_golden": unm1, "unmapped_unity": unm2,
    }
    if diffs:
        first = []
        for i in diffs[:args.max_report]:
            x, y = i % w1, i // w1
            first.append({"x": x, "y": y, "golden_idx": idx1[i], "unity_idx": idx2[i]})
        result["first_differences"] = first
    import json
    print(json.dumps(result, indent=1))
    return 0 if not diffs and not unm1 and not unm2 else 1


def json_result(ok, reason):
    import json
    return json.dumps({"ok": ok, "reason": reason})


if __name__ == "__main__":
    sys.exit(main())
