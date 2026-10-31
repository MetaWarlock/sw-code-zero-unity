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

# Mesen 2C02 default palette (RGB, alpha ignored) — mirrors Art/Unity/CodeZero/Darkwing/UnityApp/NesPalette.cs
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


def read_frame_rgb(path: Path):
    """Accept the sparse native framebuffer samples emitted by cost/... buffers as well as PNG."""
    if path.suffix != ".indices":
        return read_png_rgb(path)
    raw = path.read_bytes()
    if len(raw) != 256 * 240 or any(x >= 64 for x in raw):
        raise ValueError("Expected 256x240 NES palette indices in range 0..63")
    return 256, 240, b"".join(MESEN_2C02[x].to_bytes(3, "big") for x in raw)


def write_frame_png(source: Path, output: Path):
    """Render a native index sample with the Player's palette, without an emulator dependency."""
    if output.exists():
        raise ValueError("Output exists; use a new path")
    w, h, rgb = read_frame_rgb(source)
    def chunk(kind, data):
        return struct.pack(">I", len(data)) + kind + data + struct.pack(">I", zlib.crc32(kind + data))
    scanlines = b"".join(b"\0" + rgb[y * w * 3:(y + 1) * w * 3] for y in range(h))
    output.write_bytes(b"\x89PNG\r\n\x1a\n" + chunk(b"IHDR", struct.pack(">IIBBBBB", w, h, 8, 2, 0, 0, 0)) +
                       chunk(b"IDAT", zlib.compress(scanlines)) + chunk(b"IEND", b""))


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


def compare_route(original: Path, editor_path: Path, player_path: Path):
    """Compare corresponding stable screens; retain raw differences, never relax pixel equality."""
    import base64
    import hashlib
    import json
    request = json.loads((original / "request.json").read_text(encoding="utf-8"))
    argv = request["argv"]
    expected = "1271dce8678ffc261e2c05cc785ae53e29ac1f9ecf6265a4265bed825abb0a00"
    if request["rom_sha256"] != expected or "--load-state" in argv:
        raise ValueError("Ordinary original route must start at reset with the documented ROM")
    if "local patches = {}" not in (original / "capture.lua").read_text(encoding="utf-8"):
        raise ValueError("Ordinary route cannot use RAM patches")
    editor = json.loads(editor_path.read_text()); player = json.loads(player_path.read_text())
    if not editor["editor"] or player["editor"] or editor["states"] != player["states"]:
        raise ValueError("Editor/player scene states differ")
    for entry in player["input"]["inputs"]:
        names = {16:"start",64:"b",128:"a",1:"right"}
        token = f"0:{names[entry['buttons']]}@{entry['first']+920}-{entry['last']+920}"
        if token not in argv:
            raise ValueError("Original/port input schedules differ: " + token)
    ram = (original / "ram_frames.bin").read_bytes()
    pal = (original / "palette_frames.bin").read_bytes()
    oam = (original / "oam_frames.bin").read_bytes()
    vram = (original / "vram_frames.bin").read_bytes()
    count = int(argv[argv.index("--frames")+1])
    if (len(ram),len(pal),len(oam)) != (count*2048,count*32,count*256):
        raise ValueError("Incomplete original capture")
    chr_rows = {int(line.split(",")[0]):[int(v) for v in line.split(",")[2:4]] for line in (original/"chr_map.csv").read_text().splitlines()}
    tables = {struct.unpack_from("<I",vram,i)[0]:vram[i+4:i+2052] for i in range(0,len(vram),4100)}
    metrics=[]
    for pf,gf in [(80,1000),(700,1600),(1000,2100),(2680,3600),(2710,3631),(2800,3721),(2900,3821),(3000,3921)]:
        state=next(x for x in player["states"] if x["frame"]==pf)
        data=ram[(gf-1)*2048:gf*2048]
        vf=max(f for f in tables if f<=gf)
        nt=base64.b64decode(state["nametable"])
        slot0=next(x for x in state["slots"].split(";") if x.startswith("00:"))
        slot_xy=[int(x,16) for x in slot0.split("@")[1].split(",")]
        row={"portFrame":pf,"originalFrame":gf,"phase":state["phase"],"vramFrame":vf,
             "visibleNametableDiff":sum(a!=b for a,b in zip(nt[:1024],tables[vf][:1024])),
             "allNametableDiff":sum(a!=b for a,b in zip(nt,tables[vf])),
             "paletteDiff":sum(a!=b for a,b in zip(base64.b64decode(state["palette"]),pal[(gf-1)*32:gf*32])),
             "oamDiff":sum(a!=b for a,b in zip(base64.b64decode(state["oam"]),oam[(gf-1)*256:gf*256])),
             "originalXY":[data[0x4F0]|data[0x500]<<8,data[0x510]|data[0x520]<<8],
             "portXY":[state["x"],state["y"]] if state["phase"]=="Mission" else slot_xy,"originalState":data[0x50],"portState":state["state"],"originalCtrlShadow":data[0xFF],"portCtrl":state["ctrl"],"originalMask":data[0xFE],"portMask":state["mask"],
             "portChr":[state["chr0"],state["chr1"]],"originalChr":chr_rows[gf],"originalSlots":[
                 f"{i:02X}:{data[0x420+i]:02X}/{data[0x4E0+i]:02X}" for i in range(16) if data[0x4D0+i]&128],
             "portSlots":state["slots"]}
        png=original/f"frame_{gf:06d}.png"
        if pf in (80,700,1000,2680) and not png.exists():
            raise ValueError("Missing required original key-screen PNG: " + str(png))
        if png.exists():
            w,h,rgb=read_png_rgb(png)
            fb=base64.b64decode(state["framebuffer"])
            row["pixelsDiff"]=sum(rgb[i*3:i*3+3] != MESEN_2C02[fb[i]&63].to_bytes(3,"big") for i in range(w*h))
        metrics.append(row)
    return {"scope":"Corresponding logical screens; transition timings differ. Pixel/OAM differences are not accepted as exact matches.",
            "resetOriginal":True,"sameButtonsOffset":920,"inputConvention":"Mesen CLI relative f appears in Lua capture f+1; port replay frames are 1-based. Control sample offset=921.","editorPlayerStatesIdentical":len(player["states"]),
            "originalRequest":request,"originalHashes":{p.name:hashlib.sha256(p.read_bytes()).hexdigest()
                for p in [original/n for n in ["request.json","ram_frames.bin","palette_frames.bin","oam_frames.bin","vram_frames.bin"]]},
            "input":player["input"],"comparisons":metrics,
            "imagesCompared":sum("pixelsDiff" in x for x in metrics),
            "pixelEquivalence":all(x["pixelsDiff"]==0 for x in metrics if "pixelsDiff" in x)}


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("golden", type=Path, nargs="?")
    ap.add_argument("unity", type=Path, nargs="?")
    ap.add_argument("--max-report", type=int, default=20)
    ap.add_argument("--route", type=Path, nargs=3, metavar=("ORIGINAL", "EDITOR_JSON", "PLAYER_JSON"))
    ap.add_argument("--render-indices", type=Path, help="Render a 256x240 native .indices capture")
    ap.add_argument("--output", type=Path, help="New PNG path for --render-indices")
    args = ap.parse_args()
    if args.render_indices:
        if not args.output:
            ap.error("--render-indices requires --output")
        write_frame_png(args.render_indices, args.output)
        return 0
    if args.route:
        import json
        result = compare_route(*args.route)
        print(json.dumps(result, indent=2))
        return 0 if result["pixelEquivalence"] else 1
    if not args.golden or not args.unity:
        ap.error("Provide two PNGs or --route ORIGINAL EDITOR_JSON PLAYER_JSON")
    w1, h1, rgb1 = read_frame_rgb(args.golden)
    w2, h2, rgb2 = read_frame_rgb(args.unity)
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
