#!/usr/bin/env python3
"""Authoritative mission terrain: live $0600 collision window + decoder grid.

The ROM builds a per-screen collision window at $0600-$06FF (256 bytes) that
$EBE8 probes. Each byte holds two stacked 16x16 px cells, low nibble = upper:

  addr  = $0600 + (((X >> 8) & 1) << 7) + (((Y & 0xFF) >> 5) << 4) + ((X & 0xFF) >> 4)
  nib   = high if ((Y >> 4) & 1) else low          (per $EC6D..$EC9B)
  value = 4-bit collision type, 0 air / 1 one-way / 2 solid / 3 wall / 5 door

Address bits are [7]=cell parity, [6:4]=Y bits 7:5, [3:0]=X bits 7:4, so the
window always holds an even cell and its odd neighbour. Window Y only covers
the 256px row block that holds the camera scroll ($FA lo / $FB hi), which is
why naive full-width comparisons alias.

Usage:
  python terrain_window.py --world 0 --x0 400 --x1 1120 --y0 760 --y1 1010
  python terrain_window.py --capture <dir> --frame 780
  python terrain_window.py --capture <dir> --frame 780 --diff
"""
from __future__ import annotations
import argparse
from pathlib import Path
import sys

sys.path.insert(0, str(Path(__file__).resolve().parent))
import decode_room as dr

FRAME_BYTES = 2048
LEGEND = {0: ".", 1: "=", 2: "#", 3: "|", 4: "^", 5: "D", 6: "6", 7: "L", 8: "H"}


def window_byte_index(x: int, y: int) -> tuple[int, int]:
    """($0600-relative offset, 0=low nibble 1=high nibble) for a world pixel."""
    off = (((x >> 8) & 1) << 7) | (((y & 0xFF) >> 5) << 4) | ((x & 0xFF) >> 4)
    return off, (y >> 4) & 1


def read_frame(capture: Path, frame: int) -> bytes:
    """ram_frames.bin holds one raw 2048-byte RAM page per frame, 1-based."""
    data = (capture / "ram_frames.bin").read_bytes()
    i = max(0, min(len(data) // FRAME_BYTES - 1, frame - 1))
    return data[i * FRAME_BYTES:(i + 1) * FRAME_BYTES], i + 1


def nibble_at(ram: bytes, x: int, y: int) -> int:
    off, hi = window_byte_index(x, y)
    b = ram[0x600 + off]
    return (b >> 4) if hi else (b & 0x0F)


def dump_window(capture: Path, frame: int, show: int) -> None:
    ram, tag = read_frame(capture, frame)
    cam_x = ram[0xFC] | (ram[0xFD] << 8)
    cam_y = ram[0xFA] | (ram[0xFB] << 8)
    base_x = (cam_x >> 8) & ~1
    print(f"frame {tag} (requested {frame})  player "
          f"(${(ram[0x500] << 8) | ram[0x4F0]:04X}, ${(ram[0x520] << 8) | ram[0x510]:04X}) "
          f"state ${ram[0x50]:02X}  camera x${cam_x:04X} y${cam_y:04X} "
          f"(window cells {base_x}..{base_x + 1}, row ${cam_y >> 8:02X})")
    x0 = base_x * 256
    for wrow in range(16):
        y = cam_y + wrow * 16
        row = "".join(glyph(nibble_at(ram, x, y)) for x in range(x0, x0 + show, 16))
        print(f"  y{y:5d} {row}")
    for div in (100, 10, 1):
        print("  " + " " * 9 +
              "".join(str((x // div) % 10) for x in range(x0, x0 + show, 16)))
    print("  " + " " * 9 + "ROM window above, cells "
          f"{base_x}..{base_x + 1} (x {x0}..{x0 + 511})")


def glyph(v: int) -> str:
    return LEGEND.get(v, chr(ord("A") + v % 26))


def dump_region(world: int, x0: int, x1: int, y0: int, y1: int) -> None:
    w = dr.World(world)
    rows = len(w.grid) // w.row_width
    cache: dict[int, list] = {}
    print(f"world {world}: rooms {rows} x {w.row_width}, start cell ${w.start_cell:02X},"
          f" start row {w.start_row}, set bank ${w.set_bank:02X}")
    for y in range(y0 & ~15, y1 + 1, 16):
        gy = y >> 8
        line = ""
        if gy < 0 or gy >= rows:
            continue
        for x in range(x0 & ~15, x1 + 1, 16):
            gx = x >> 8
            t = 0
            if 0 <= gx < w.row_width:
                cell = w.grid[gy * w.row_width + gx]
                if cell:
                    typ = cache.setdefault(cell, dr.decode_room(w, cell)[3])
                    t = typ[(y & 0xFF) // 16][(x & 0xFF) // 16]
            line += LEGEND.get(t, chr(ord("A") + t % 26))
        print(f"  y{y:5d} {line}")
    print("  " + " " * 7 + "".join(str((x // 100) % 10) for x in range(x0 & ~15, x1 + 1, 16)))
    print("  " + " " * 7 + "".join(str((x // 10) % 10) for x in range(x0 & ~15, x1 + 1, 16)))
    print("  " + " " * 7 + "".join(str(x % 10) for x in range(x0 & ~15, x1 + 1, 16)))


def diff_window(capture: Path, frame: int, world: int) -> None:
    """Compare the whole loaded cell pair against the decoder, 16px per cell."""
    w = dr.World(world)
    ram, tag = read_frame(capture, frame)
    cam_y = ram[0xFA] | (ram[0xFB] << 8)
    base = (ram[0xFC] | (ram[0xFD] << 8)) >> 8 & ~1
    rows = len(w.grid) // w.row_width
    cache: dict[int, list] = {}
    bad = []
    if not 0 <= cam_y >> 8 < rows:
        print(f"frame {tag}: camera row ${cam_y >> 8:02X} outside the grid")
        return
    for gx in range(base, base + 2):
        if gx >= w.row_width:
            continue
        cell = w.grid[(cam_y >> 8) * w.row_width + gx]
        typ = cache.setdefault(cell, dr.decode_room(w, cell)[3]) if cell else None
        for c in range(16):
            for r in range(15):        # Ylo only reaches $EF: one section = 240 px
                x, y = gx * 256 + c * 16, (cam_y & 0xFF) + r * 16
                a = nibble_at(ram, x, (cam_y & 0xFF00) | y)
                b = typ[r][c] if typ else 0
                if a != b:
                    bad.append((x, (cam_y & 0xFF00) | y, a, b))
    n = 2 * 16 * 15
    print(f"frame {tag}: window vs decoder {n - len(bad)}/{n} agree (cells {base},"
          f"{base + 1}; row ${cam_y >> 8:02X})")
    for x, y, a, b in bad[:20]:
        print(f"   x{x} y{y}: ROM ${a:X} decoder ${b:X}")


def dump_platforms(world: int, section: int, x0: int, x1: int) -> None:
    """Every standable surface in one 240px screen row, merged over 16px columns.

    A surface is a solid/one-way cell with air directly above; run right to find
    the jump chain the level actually offers. Standable player Y = surface - 12.
    """
    w = dr.World(world)
    cache: dict[int, list] = {}
    STANDABLE = {1: "one-way", 2: "solid", 3: "wall"}
    spans: list[tuple[int, int, int]] = []     # (y, x_start, x_end)
    for r in range(15):
        for c in range((x1 - x0) // 16 + 1):
            x = x0 + c * 16
            y = r * 16
            cell = w.grid[section * w.row_width + (x >> 8)] if 0 <= (x >> 8) < w.row_width else 0
            typ = cache.setdefault(cell, dr.decode_room(w, cell)[3]) if cell else None
            if not typ:
                continue
            here = typ[r][(x & 0xFF) >> 4]
            above = typ[r - 1][(x & 0xFF) >> 4] if r else 0
            if here in STANDABLE and above == 0:
                if spans and spans[-1][0] == y and spans[-1][2] + 16 == x:
                    spans[-1] = (y, spans[-1][1], x)
                else:
                    spans.append((y, x, x))
    print(f"world {world} screen row ${section:02X} (Ylo 0..$EF, "
          f"surface y = ${section:02X}00 + Ylo), standable spans x{x0}..{x1}:")
    for y, xs, xe in spans:
        print(f"  Ylo ${y:02X}  ({(section << 8) | y:4d})  x {xs}..{xe + 15:4d}"
              f"  w {xe - xs + 16}")


def dump_objects(world: int, x0: int, x1: int) -> None:
    """Bank-4 object seeds (level_format.xhtml §7): cellX/localX/rowY/localY/type.

    World position = (cellX*256 + localX, rowY*256 + localY); the feeder activates
    a seed when cellX == $FD and rowY == $FB. Type id *4 indexes the descriptor
    table at bank4 $8AB3; names come from object_catalog.csv when present.
    """
    b4 = dr.bank(4)
    base = 0x8000
    arrays = {"cellX": 0x8239, "localX": 0x826C, "rowY": 0x829F,
              "localY": 0x82D2, "type": 0x8305}
    col = {k: b4[v - base:] for k, v in arrays.items()}
    names: dict[int, str] = {}
    cat = next(p for p in Path(__file__).resolve().parents if (p / 'ProjectSettings').is_dir()) / "Art/Research/DarkwingDuck/object_catalog.csv"
    if cat.exists():
        import csv
        with cat.open(newline="", encoding="utf-8") as fh:
            for row in csv.DictReader(fh):
                try:
                    names[int(row["type_id"])] = row["name_or_unknown"]
                except (KeyError, ValueError):
                    pass
    n = 0
    print(f"world {world} object seeds (bank4 $8239/$826C/$829F/$82D2/$8305), "
          f"x{x0}..{x1}:")
    while n < 200 and col["type"][n] != 0xFF:
        cx, lx, ry, ly, t = (col[k][n] for k in ("cellX", "localX", "rowY",
                                                 "localY", "type"))
        x, y = cx * 256 + lx, (ry << 8) + ly
        if x0 <= x <= x1:
            print(f"  #{n:3d} type ${t:02X} {names.get(t, '?'):28s} "
                  f"x {x:5d} (cell {cx} lo ${lx:02X})  y {y:5d} "
                  f"(row {ry} lo ${ly:02X})")
        n += 1
    print(f"  ({n} seeds before the $FF terminator)")


def main():
    ap = argparse.ArgumentParser(description=__doc__)
    ap.add_argument("--world", type=lambda v: int(v, 0), default=0)
    ap.add_argument("--x0", type=int, default=0)
    ap.add_argument("--x1", type=int, default=1023)
    ap.add_argument("--y0", type=int, default=768)
    ap.add_argument("--y1", type=int, default=1023)
    ap.add_argument("--capture", type=Path)
    ap.add_argument("--frame", type=int, action="append", default=[])
    ap.add_argument("--show", type=int, default=512, help="window columns in px")
    ap.add_argument("--diff", action="store_true")
    ap.add_argument("--platforms", action="store_true",
                    help="list standable surfaces of one screen row")
    ap.add_argument("--objects", action="store_true",
                    help="list bank-4 object seeds (world 0 / bridge arrays)")
    ap.add_argument("--section", type=lambda v: int(v, 0), default=3)
    args = ap.parse_args()
    if args.capture:
        for frame in args.frame or [0]:
            dump_window(args.capture, frame, args.show)
            if args.diff:
                diff_window(args.capture, frame, args.world)
        return
    if args.objects:
        dump_objects(args.world, args.x0, args.x1)
        return
    if args.platforms:
        dump_platforms(args.world, args.section, args.x0, args.x1)
        return
    dump_region(args.world, args.x0, args.x1, args.y0, args.y1)
    print("legend: . air, = one-way, # solid, | wall, ^ spike, D door, L/H ledge")


if __name__ == "__main__":
    main()
