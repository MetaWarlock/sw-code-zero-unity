"""Read-only, ROM-hash-locked Darkwing Duck research extractor (Python stdlib).

Formats are independently implemented from the MIT CadEditor configuration:
spiiin/CadEditor @ b28316ddd9be4e79ac5e6adf89b7751df609d94e.
Exports source data, not a complete game. Runtime palette/bank bindings, audio,
collision semantics and exceptional screen references still require tracing.
"""
from __future__ import annotations
import argparse
import hashlib
import json
from pathlib import Path
import struct
import zlib

ROOT = next(p for p in Path(__file__).resolve().parents if (p / 'ProjectSettings').is_dir())
EXPECTED_SHA256 = "1271dce8678ffc261e2c05cc785ae53e29ac1f9ecf6265a4265bed825abb0a00"
LEVELS = [
    ("bridge", 0x10315, 51, 17, 4, 0x1DFA0, 0, 0, 0x01),
    ("city", 0x10438, 60, 17, 4, 0x1DFE4, 1, 2, 0x21),
    ("sewers", 0x10584, 68, 17, 4, 0x1E028, 2, 4, 0x41),
    ("tower", 0x106A0, 54, 10, 12, 0x1E06C, 3, 6, 0x61),
    ("forest", 0x10816, 80, 19, 3, 0x1E0E4, 4, 10, 0x81),
    ("warehouse", 0x10962, 63, 19, 3, 0x1E11D, 5, 12, 0xA1),
    ("fowl_fortress", 0x10A89, 58, 19, 3, 0x1E156, 6, 9, 0x101),
]


def sha(data: bytes) -> str:
    return hashlib.sha256(data).hexdigest()


def save_json(path: Path, data):
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(json.dumps(data, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")


def png_gray(path: Path, w: int, h: int, pixels: bytes):
    assert len(pixels) == w * h
    def chunk(kind, payload):
        return struct.pack(">I", len(payload)) + kind + payload + struct.pack(">I", zlib.crc32(kind + payload))
    data = b"".join(b"\0" + pixels[y*w:(y+1)*w] for y in range(h))
    path.write_bytes(b"\x89PNG\r\n\x1a\n" + chunk(b"IHDR", struct.pack(">IIBBBBB", w, h, 8, 0, 0, 0, 0))
                     + chunk(b"IDAT", zlib.compress(data, 9)) + chunk(b"IEND", b""))


def decode_tile(raw: bytes) -> bytes:
    assert len(raw) == 16
    return bytes(((raw[y] >> (7-x)) & 1) | (((raw[y+8] >> (7-x)) & 1) << 1)
                 for y in range(8) for x in range(8))


def encode_tile(pixels: bytes) -> bytes:
    assert len(pixels) == 64 and max(pixels) < 4
    return bytes(sum(((pixels[y*8+x] >> plane) & 1) << (7-x) for x in range(8))
                 for plane in range(2) for y in range(8))


def bank5_offset(cpu: int) -> int:
    if not 0x8000 <= cpu < 0xC000:
        raise ValueError(f"Unexpected bank 5 CPU pointer: {cpu:04X}")
    return 0x10 + 5 * 0x4000 + cpu - 0x8000


def extract(rom_path: Path, out: Path):
    rom = rom_path.read_bytes()
    if sha(rom) != EXPECTED_SHA256:
        raise ValueError("ROM SHA256 differs from researched file. Do not use fixed offsets on another revision.")
    assert len(rom) == 0x40010 and rom[:4] == b"NES\x1a"
    out.mkdir(parents=True, exist_ok=True)
    raw_dir, chr_dir = out / "raw", out / "chr"
    raw_dir.mkdir(exist_ok=True)
    chr_dir.mkdir(exist_ok=True)
    prg, chr_raw = rom[0x10:0x20010], rom[0x20010:0x40010]
    raw_dir.joinpath("header.bin").write_bytes(rom[:16])
    raw_dir.joinpath("prg.bin").write_bytes(prg)
    raw_dir.joinpath("chr.bin").write_bytes(chr_raw)
    assert rom[:16] + prg + chr_raw == rom
    sections = []
    for kind, data, base, size in [("prg", prg, 0x10, 0x4000), ("chr", chr_raw, 0x20010, 0x1000)]:
        for n in range(len(data)//size):
            b = data[n*size:(n+1)*size]
            raw_dir.joinpath(f"{kind}_{n:02d}.bin").write_bytes(b)
            sections.append({"kind": kind, "bank": n, "file_offset": base+n*size,
                             "size": size, "sha256": sha(b)})

    tiles = [decode_tile(chr_raw[i:i+16]) for i in range(0, len(chr_raw), 16)]
    assert b"".join(encode_tile(t) for t in tiles) == chr_raw
    chr_dir.joinpath("tiles_index2.bin").write_bytes(b"".join(tiles))
    for page in range(32):
        pixels = bytearray(128*128)
        for t in range(256):
            tile = tiles[page*256+t]
            for y in range(8):
                start = ((t//16)*8+y)*128 + (t%16)*8
                pixels[start:start+8] = bytes(v*85 for v in tile[y*8:y*8+8])
        png_gray(chr_dir / f"bank_{page:02d}_index_preview.png", 128, 128, pixels)

    # Preserve raw flags, not only interpreted low/high bits.
    sets = []
    for bank in range(7):
        big_base, small_base = 0x36F0 + bank*0x4000, 0x3AF0 + bank*0x4000
        big = [list(rom[big_base+i*4:big_base+i*4+4]) for i in range(256)]
        small = [{"id": i, "tiles": [rom[small_base+j*256+i] for j in range(4)],
                  "raw_attribute": rom[small_base+1024+i],
                  "palette_slot": rom[small_base+1024+i] & 3,
                  "type_nibble": rom[small_base+1024+i] >> 4}
                 for i in range(256)]
        assert b"".join(bytes(v) for v in big) == rom[big_base:big_base+1024]
        rebuilt = bytes(s["tiles"][j] for j in range(4) for s in small) + bytes(s["raw_attribute"] for s in small)
        assert rebuilt == rom[small_base:small_base+1280]
        sets.append({"bank": bank, "big_offset": big_base, "small_offset": small_base,
                     "macrotiles32": big, "metatiles16": small})
    save_json(out / "level_tile_sets.json", sets)

    palettes = [{"id": i, "file_offset": 0x1C36D+i*16, "raw": list(rom[0x1C36D+i*16:0x1C36D+(i+1)*16])}
                for i in range(32)]
    save_json(out / "palette_candidates.json", {"note": "CadEditor configured range; verify runtime use and transitions", "records": palettes})
    screens = [{"id": i, "file_offset": 0x10+i*64, "macrotiles": list(rom[0x10+i*64:0x10+(i+1)*64])}
               for i in range(300)]
    save_json(out / "screen_candidates.json", {"note": "Configured 300 slots are candidates, not proof of 300 used rooms", "records": screens})
    levels, exceptions, type_ids = [], [], set()
    for n, (name, ob, count, w, h, la, bank, pal, first) in enumerate(LEVELS):
        cells = list(rom[la:la+w*h])
        scrolls = list(rom[la+508:la+508+w*h])
        objs = []
        for i in range(count):
            vals = [rom[ob-count*j+i] for j in (4,3,2,1,0)]
            sx,x,sy,y,t = vals
            type_ids.add(t)
            objs.append({"index": i, "type": t, "screen_x": sx, "x": x, "screen_y": sy, "y": y,
                         "source_offsets": [ob-count*j+i for j in (4,3,2,1,0)]})
        assert bytes(o["type"] for o in objs) == rom[ob:ob+count]
        display_ids = [v+(256 if n==6 else 0) for v in cells]
        for i,v in enumerate(display_ids):
            if cells[i] != 0 and v >= 300:
                exceptions.append({"level": name, "layout_index": i, "raw": cells[i],
                                   "cadeditor_display_id": v, "status": "UNRESOLVED: do not treat as normal screen"})
        levels.append({"id": n, "name": name, "width": w, "height": h, "layout_offset": la,
                       "layout_raw": cells, "cadeditor_display_ids": display_ids,
                       "scroll_offset": la+508, "scroll_raw": scrolls,
                       "object_type_offset": ob, "object_count": count, "objects": objs,
                       "tile_set": bank, "bg_chr_4k_bank": 16+bank,
                       "cadeditor_default_palette": pal, "first_screen_candidate": first})
        # Diagnostic grayscale only: source color index 0..3, NOT original colors.
        pixels = bytearray(256*256)
        screen = screens[first]["macrotiles"]
        for cell, big_id in enumerate(screen):
            for q, small_id in enumerate(sets[bank]["macrotiles32"][big_id]):
                for t, tile_id in enumerate(sets[bank]["metatiles16"][small_id]["tiles"]):
                    tile = tiles[(16+bank)*256+tile_id]
                    tx = (cell%8)*32 + (q%2)*16 + (t%2)*8
                    ty = (cell//8)*32 + (q//2)*16 + (t//2)*8
                    for y in range(8):
                        pixels[(ty+y)*256+tx:(ty+y)*256+tx+8] = bytes(v*85 for v in tile[y*8:y*8+8])
        png_gray(out / f"{name}_first_screen_indices.png", 256, 256, pixels)
    save_json(out / "levels.json", levels)

    # Two map-screen command streams configured by CadEditor. Addresses are
    # BIG endian, unlike ordinary 6502 pointers. Not a general Capcom decoder.
    ui_maps = []
    for map_id, offset in enumerate((0x80B1, 0x83FC)):
        cursor, commands, vram = offset, [], bytearray(0x800)
        while rom[cursor] != 0xFF:
            addr = int.from_bytes(rom[cursor:cursor+2], "big")
            count = rom[cursor+2]
            data = rom[cursor+3:cursor+3+count]
            assert 0x2000 <= addr and addr+count <= 0x2800
            assert count > 0 and len(data) == count
            commands.append({"file_offset":cursor,"ppu_address":addr,"data":list(data)})
            vram[addr-0x2000:addr-0x2000+count] = data
            cursor += 3+count
            assert cursor < offset+0x2000
        rebuilt = b"".join(c['ppu_address'].to_bytes(2,'big')+bytes([len(c['data'])])+bytes(c['data']) for c in commands)+b'\xff'
        assert rebuilt == rom[offset:cursor+1]
        ui_maps.append({"id":map_id,"file_offset":offset,"end_offset_exclusive":cursor+1,
                        "commands":commands,"unwritten_bytes_defaulted_to_zero":True})
        raw_dir.joinpath(f"ui_map_{map_id}_nametable_candidate.bin").write_bytes(vram)
    save_json(out / "ui_map_commands.json", ui_maps)

    def pointer(lo, hi, idx):
        return rom[bank5_offset(lo)+idx] | (rom[bank5_offset(hi)+idx] << 8)
    coords = [pointer(0xAE53, 0xAF23, i) for i in range(208)]
    frames = []
    for i in range(314):
        cpu = pointer(0x9B0B, 0x9C45, i)
        if cpu < 0x800:
            frames.append({"id": i, "cpu_pointer": cpu, "kind": "dynamic_ram",
                           "status": "Requires runtime RAM capture; not a PRG offset"})
            continue
        off = bank5_offset(cpu)
        count, ci = rom[off]+1, rom[off+1]
        assert ci < len(coords)
        co = bank5_offset(coords[ci])
        parts = []
        for j in range(count):
            y,x = rom[co+j*2:co+j*2+2]
            parts.append({"tile": rom[off+2+j*2], "attribute": rom[off+3+j*2],
                          "x": x if x<128 else x-256, "y": y if y<128 else y-256})
        assert bytes([count-1,ci]+[v for t in parts for v in (t['tile'],t['attribute'])]) == rom[off:off+2+2*count]
        assert bytes(v&255 for t in parts for v in (t['y'],t['x'])) == rom[co:co+count*2]
        frames.append({"id": i, "kind": "static_rom", "cpu_pointer": cpu, "file_offset": off, "coord_id": ci,
                       "coord_file_offset": co, "parts": parts})
    animations = []
    for i in range(199):
        cpu = pointer(0xB429, 0xB4F0, i)
        off = bank5_offset(cpu)
        raw = rom[off]
        count = (raw+1) % 128  # mirror the researched format, preserve sentinel values
        shift = 0 if raw+1 < 128 else 256
        ids = [v+shift for v in rom[off+2:off+2+count]]
        assert all(v < 314 for v in ids)
        animations.append({"id": i, "cpu_pointer": cpu, "file_offset": off,
                           "raw_header": raw, "timer_raw": rom[off+1], "frames": ids,
                           "needs_runtime_timer_validation": True})
        assert bytes([raw,rom[off+1]]+[v-shift for v in ids]) == rom[off:off+2+count]
    save_json(out / "metasprite_frames.json", frames)
    save_json(out / "animations.json", animations)
    save_json(out / "coordinate_pointers.json", [{"id": i,"cpu": v,"file_offset": bank5_offset(v)} for i,v in enumerate(coords)])
    manifest = {
        "schema": 1, "source_path": str(rom_path.resolve()), "sha256": sha(rom),
        "sha1": hashlib.sha1(rom).hexdigest(), "md5": hashlib.md5(rom).hexdigest(),
        "crc32": f"{zlib.crc32(rom):08x}", "header_hex": rom[:16].hex(" "),
        "format": "NES 2.0" if rom[7]&12 == 8 else "iNES",
        "mapper": (rom[6]>>4) | (rom[7]&0xF0) | ((rom[8]&15)<<8), "submapper": rom[8]>>4,
        "region_byte12": rom[12], "trainer": bool(rom[6]&4), "battery": bool(rom[6]&2),
        "file_size": len(rom), "prg_bytes": len(prg), "chr_bytes": len(chr_raw),
        "payload_sha256": sha(rom[16:]), "prg_sha256": sha(prg), "chr_sha256": sha(chr_raw),
        "vectors": {name: int.from_bytes(rom[a:a+2],"little") for name,a in [("NMI",0x2000A),("RESET",0x2000C),("IRQ",0x2000E)]},
        "sections": sections,
        "counts": {"chr_tiles_8x8": len(tiles), "levels": len(levels), "placed_object_records": sum(len(v['objects']) for v in levels),
                   "placed_type_ids_including_ff": len(type_ids),
                   "placed_type_ids_excluding_ff": len(type_ids-{255}),
                   "ff_object_records": sum(o['type']==255 for v in levels for o in v['objects']),
                   "animation_records": len(animations), "metasprite_frames": len(frames),
                   "static_metasprite_frames": sum(v['kind']=='static_rom' for v in frames),
                   "dynamic_ram_frames": sum(v['kind']=='dynamic_ram' for v in frames),
                   "coordinate_pointers": len(coords), "configured_screen_slots": len(screens)},
        "placed_type_ids": sorted(type_ids), "unresolved_screen_references": exceptions,
        "dynamic_frame_records": [v for v in frames if v['kind']=='dynamic_ram'],
        "verification": {"rom_reassembly": "PASS", "chr_encode_decode_roundtrip": "PASS",
                         "tile_table_roundtrip": "PASS", "static_frame_and_coordinate_roundtrip": "PASS",
                         "animation_record_roundtrip": "PASS", "runtime_semantics": "NOT_TESTED"},
        "limitations": ["No music/SFX extraction yet", "No complete UI/cutscene decoding yet",
                        "No mapping of metasprites to runtime CHR banks/palettes", "No gameplay port",
                        "Screen-zero semantics and FOWL raw 0x98 need tracing",
                        "Grayscale previews show bitplane indices, not final game colors",
                        "300 configured screen slots are not 300 proven playable rooms"]}
    save_json(out / "manifest.json", manifest)
    print(json.dumps({"output": str(out.resolve()), "counts": manifest['counts'],
                      "verification": manifest['verification'], "exceptions": exceptions}, indent=2))


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--rom", type=Path, default=ROOT / "Art/Darkwing Duck (USA).nes")
    parser.add_argument("--out", type=Path, default=ROOT / "Art/Research/DarkwingDuck/extracted")
    args = parser.parse_args()
    extract(args.rom, args.out)
