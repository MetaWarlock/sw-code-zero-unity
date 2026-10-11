"""Export extracted Darkwing Duck data into Unity-ready assets.

Reads the ROM (hash-locked) and emits:
  Assets/Darkwing/Data/chr_full.bytes        raw 128 KiB CHR (32 banks x 4 KiB)
  Assets/Darkwing/Data/levelsets.bytes       7 sets: macrotiles 256x4, metatile tile ids 256x4, attributes 256
  Assets/Darkwing/Data/screens.bytes         300 screens x 64 macrotile indices
  Assets/Darkwing/Data/levels.json           layouts, scroll/door arrays, object placements (readable)
  Assets/Darkwing/Data/anims.json            animations, metasprite frames, coordinate sets (readable)
  Assets/Darkwing/Data/manifest.json         provenance: ROM hash, offsets, extractor version

Reuses the verified decode logic from extract_rom.py.
"""
from __future__ import annotations
import hashlib
import json
from pathlib import Path
import sys

sys.path.insert(0, str(Path(__file__).parent))
from extract_rom import ROOT, EXPECTED_SHA256, LEVELS  # noqa: E402


def main():
    rom = (ROOT / "Art/Darkwing Duck (USA).nes").read_bytes()
    assert hashlib.sha256(rom).hexdigest() == EXPECTED_SHA256
    out = ROOT / "Assets/Darkwing/Data"
    out.mkdir(parents=True, exist_ok=True)

    prg = rom[0x10:0x20010]
    chr_raw = rom[0x20010:0x40010]
    (out / "chr_full.bytes").write_bytes(chr_raw)

    # 7 level sets: 256 macrotiles * 4 metatile ids, 256 metatiles * 4 tile ids, 256 attrs
    blob = bytearray()
    for bank in range(7):
        big_base, small_base = 0x36F0 + bank * 0x4000, 0x3AF0 + bank * 0x4000
        blob += rom[big_base:big_base + 1024]          # macrotile -> 4 metatile ids
        blob += rom[small_base:small_base + 1024]      # metatile -> 4 tile ids
        blob += rom[small_base + 1024:small_base + 1280]  # attributes (raw; palette=raw&3, type=raw>>4)
    (out / "levelsets.bytes").write_bytes(bytes(blob))

    screens = rom[0x10:0x10 + 300 * 64]
    (out / "screens.bytes").write_bytes(screens)

    levels = []
    for n, (name, ob, count, w, h, la, bank, pal, first) in enumerate(LEVELS):
        cells = list(rom[la:la + w * h])
        scrolls = list(rom[la + 508:la + 508 + w * h])
        objs = []
        for i in range(count):
            sx, x, sy, y, t = [rom[ob - count * j + i] for j in (4, 3, 2, 1, 0)]
            objs.append({"index": i, "type": t, "screenX": sx, "x": x, "screenY": sy, "y": y})
        levels.append({"id": n, "name": name, "width": w, "height": h,
                       "layoutOffset": la, "cells": cells, "scrollDoor": scrolls,
                       "objectCount": count, "objects": objs, "tileSet": bank,
                       "bgChrBank": 16 + bank, "cadeditorPalette": pal,
                       "firstScreen": first})

    # animation data from bank 5 (pointers $B429/$B4F0, frames $9B0B/$9C45, coords $AE53/$AF23)
    b5 = prg[5 * 0x4000:(5 + 1) * 0x4000]

    def bank5(cpu: int) -> int:
        return cpu - 0x8000

    def ptr(lo_tab, hi_tab, idx):
        return b5[bank5(lo_tab) + idx] | (b5[bank5(hi_tab) + idx] << 8)

    coords = [ptr(0xAE53, 0xAF23, i) for i in range(208)]
    frames = []
    for i in range(314):
        cpu = ptr(0x9B0B, 0x9C45, i)
        if cpu < 0x800:
            frames.append({"id": i, "kind": "ram_dynamic", "cpu": cpu})
            continue
        off = bank5(cpu)
        count = b5[off] + 1
        ci = b5[off + 1]
        co = bank5(coords[ci])
        parts = []
        for j in range(count):
            y, x = b5[co + j * 2], b5[co + j * 2 + 1]
            parts.append({"tile": b5[off + 2 + j * 2], "attr": b5[off + 3 + j * 2],
                          "x": x if x < 128 else x - 256, "y": y if y < 128 else y - 256})
        frames.append({"id": i, "kind": "rom", "coordId": ci, "parts": parts})
    anims = []
    for i in range(199):
        cpu = ptr(0xB429, 0xB4F0, i)
        off = bank5(cpu)
        raw = b5[off]
        count = (raw + 1) % 128
        shift = 0 if raw + 1 < 128 else 256
        anims.append({"id": i, "rawHeader": raw, "timerRaw": b5[off + 1],
                      "frames": [v + shift for v in b5[off + 2:off + 2 + count]]})

    (out / "levels.json").write_text(json.dumps({
        "romSha256": EXPECTED_SHA256, "levels": levels}, indent=1), encoding="utf-8")
    (out / "anims.json").write_text(json.dumps({
        "romSha256": EXPECTED_SHA256,
        "coords": [{"id": i, "cpu": c} for i, c in enumerate(coords)],
        "frames": frames, "animations": anims}, indent=1), encoding="utf-8")

    # Binary mirrors for engine loading (Core has no JSON parser).
    lb = bytearray()
    lb += len(levels).to_bytes(4, "little")
    for lv in levels:
        lb += lv["id"].to_bytes(4, "little")
        lb += lv["width"].to_bytes(4, "little")
        lb += lv["height"].to_bytes(4, "little")
        lb += lv["objectCount"].to_bytes(4, "little")
        lb += lv["tileSet"].to_bytes(4, "little")
        lb += lv["bgChrBank"].to_bytes(4, "little")
        lb += lv["firstScreen"].to_bytes(4, "little")
        lb += lv["layoutOffset"].to_bytes(4, "little")
        lb += bytes(lv["cells"])
        lb += bytes(lv["scrollDoor"])
        for o in lv["objects"]:
            lb += bytes((o["screenX"], o["x"], o["screenY"], o["y"], o["type"]))
    (out / "levels.bytes").write_bytes(bytes(lb))

    ab = bytearray()
    ab += len(coords).to_bytes(4, "little")
    for c in coords:
        ab += c.to_bytes(2, "little")
    ab += len(frames).to_bytes(4, "little")
    for f in frames:
        ab += f["id"].to_bytes(2, "little")
        kind = 0 if f["kind"] == "rom" else 1
        ab.append(kind)
        ci = f.get("coordId", 0xFFFF) if f["kind"] == "rom" else 0xFFFF
        ab += ci.to_bytes(2, "little")
        parts = f.get("parts", [])
        ab += len(parts).to_bytes(2, "little")
        for p in parts:
            ab += bytes((p["tile"], p["attr"], p["x"] & 0xFF, p["y"] & 0xFF))
    ab += len(anims).to_bytes(4, "little")
    for a in anims:
        ab += a["id"].to_bytes(2, "little")
        ab.append(a["rawHeader"])
        ab.append(a["timerRaw"])
        ab += len(a["frames"]).to_bytes(2, "little")
        for fid in a["frames"]:
            ab += fid.to_bytes(2, "little")
    (out / "anims.bytes").write_bytes(bytes(ab))

    manifest = {
        "exporter": "Tools/DarkwingResearch/export_unity.py v1",
        "romSha256": EXPECTED_SHA256,
        "files": {
            "chr_full.bytes": {"bytes": len(chr_raw), "source": "ROM 0x20010..0x4000F (32 CHR banks x 4KiB)"},
            "levelsets.bytes": {"bytes": len(blob), "source": "per set: macro 0x36F0+set*0x4000 (1024), meta 0x3AF0+set*0x4000 (1024), attr +1024 (256)"},
            "screens.bytes": {"bytes": 300 * 64, "source": "ROM 0x10 + screenId*0x40 (300 candidates x 64 macrotiles, 8x8)"},
            "levels.json": {"source": "CadEditor-verified tables; layout offsets in records"},
            "levels.bytes": {"bytes": len(lb), "source": "binary mirror of levels.json for engine loading"},
            "anims.json": {"source": "PRG bank 5 pointer tables; 3 ram_dynamic frames excluded from static decode"},
            "anims.bytes": {"bytes": len(ab), "source": "binary mirror of anims.json for engine loading"},
        },
        "notes": [
            "Metatile attribute: palette=raw&3, surface type=raw>>4 (0 bg,1 hook,2 platform,3 block,4 spikes,5 door — semantics from editor config, verify per-tile)",
            "Level set index != mission order; mission descriptors live in PRG bank 7 ($DF50/$DF60 per flow research)",
        ],
    }
    (out / "manifest.json").write_text(json.dumps(manifest, indent=2), encoding="utf-8")
    print(json.dumps({"out": str(out), "chr": len(chr_raw), "levelsets": len(blob),
                      "screens": 300 * 64, "levels": len(levels), "anims": len(anims),
                      "frames": len(frames)}, indent=1))


if __name__ == "__main__":
    main()
