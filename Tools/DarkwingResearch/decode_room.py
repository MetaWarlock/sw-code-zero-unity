#!/usr/bin/env python3
"""Darkwing Duck (USA) level room decoder.

Decodes the room geometry format reverse-engineered from the ROM (see
Research/DarkwingDuck/docs/level_format.md).

Verified: room 0x01 of world 0 (bridge) expands to a 32x32 tile grid whose
first 30 rows match the captured nametable (frame 660 of probe_m1_enter)
at 960/960 = 100%.

Usage:
  python decode_room.py --world 0 --cell 0x01            # render macrotiles
  python decode_room.py --world 0 --cell 0x01 --tiles    # render tile ids
  python decode_room.py --world 0 --cell 0x01 --verify    # vs probe nametable
  python decode_room.py --world 0 --grid                 # dump layout grid
"""
import argparse
import os
import sys

ROM_PATH = os.path.join(os.path.dirname(__file__), "..", "..", "Art",
                        "Darkwing Duck (USA).nes")
PRG = None  # lazy


def prg():
    global PRG
    if PRG is None:
        with open(ROM_PATH, "rb") as f:
            PRG = f.read()[0x10:]
    return PRG


def bank(i):
    return prg()[i * 0x4000:(i + 1) * 0x4000]


# ---------------------------------------------------------------- descriptors
WORLD_COUNT = 7


class World:
    def __init__(self, wid):
        b7 = bank(7)
        self.id = wid
        # descriptor tables (bank 7 offsets, CPU = 0xC000 + off)
        self.set_bank = b7[0x1EAA + wid]          # $DEAA metatile/room-table bank
        self.start_cell = b7[0x1EB9 + wid]        # $DEB9 start cell index
        self.start_row = b7[0x1EC8 + wid]         # $DEC8 start grid row ($FB)
        self.row_width = b7[0x1ED7 + wid] + 1     # $DED7 stride+1 = grid row width
        self.chr_bg = b7[0x1EF5 + wid]            # $DEF5 CHR bank (bg)
        self.music = b7[0x1F04 + wid]             # $DF04
        self.top_row = b7[0x1F41 + wid]           # $DF41 topmost reachable row ($2D)
        grid_lo, grid_hi = b7[0x1F50 + wid], b7[0x1F60 + wid]
        flag_lo, flag_hi = b7[0x1F70 + wid], b7[0x1F80 + wid]
        self.grid_ptr = grid_lo | grid_hi << 8    # $32/$33 layout grid (CPU addr)
        self.flag_ptr = flag_lo | flag_hi << 8    # $34/$35 per-cell flag table
        self.grid_base = self.grid_ptr - 0xC000   # bank7-relative (b7 is bank 7 alone)
        self.flag_base = self.flag_ptr - 0xC000
        self.grid = b7[self.grid_base:self.grid_base + 4 * self.row_width]
        self.flags = b7[self.flag_base:self.flag_base + 4 * self.row_width]

    def cell_at(self, row, col):
        return self.grid[row * self.row_width + col]

    def cell_index(self, row, col):
        return row * self.row_width + col


def room_bank(world):
    """PRG bank holding the 64-byte room blocks (EAD7)."""
    return 1 if world.set_bank >= 6 else 0


def room_offset(cell):
    """File offset of room `cell`'s 64-byte block inside its PRG bank.

    EB27: pointer = $7FC0 + cell*64  ==  $8000 + (cell-1)*64.
    """
    return (cell - 1) * 64


def macrotile_table(world):
    """256 entries x 4 metatile ids, in the set bank at 0x36E0 ($B6E0)."""
    b = bank(world.set_bank)
    return b[0x36E0:0x36E0 + 1024]


def metatile_tables(world):
    """(tl, tr, bl, br, type) arrays of 256 entries each, set bank.

    0x3AE0/0x3BE0/0x3CE0/0x3DE0 = tile id per metatile quadrant,
    0x3EE0 = collision/type byte (low nib = top metatile row, high nib = bottom).
    """
    b = bank(world.set_bank)
    return (b[0x3AE0:0x3AE0 + 256], b[0x3BE0:0x3BE0 + 256],
            b[0x3CE0:0x3CE0 + 256], b[0x3DE0:0x3DE0 + 256],
            b[0x3EE0:0x3EE0 + 256])


def decode_room(world, cell):
    """Return (macrotiles 8x8, metatiles 16x16, tiles 32x32, types 16x16)."""
    b = bank(room_bank(world))
    off = room_offset(cell)
    room = b[off:off + 64]
    if len(room) != 64:
        raise ValueError("room out of range")
    mt_tbl = macrotile_table(world)
    tl, tr, bl, br, ty = metatile_tables(world)
    met = [[0] * 16 for _ in range(16)]
    tiles = [[0] * 32 for _ in range(32)]
    typ = [[0] * 16 for _ in range(16)]
    for mr in range(8):
        for mc in range(8):
            m = room[mr * 8 + mc]
            for q in range(4):
                mid = mt_tbl[m * 4 + q]
                qr, qc = divmod(q, 2)
                r, c = mr * 2 + qr, mc * 2 + qc
                met[r][c] = mid
                typ[r][c] = (ty[mid] >> 4) if qr else (ty[mid] & 0x0F)
                tiles[r * 2][c * 2] = tl[mid]
                tiles[r * 2][c * 2 + 1] = tr[mid]
                tiles[r * 2 + 1][c * 2] = bl[mid]
                tiles[r * 2 + 1][c * 2 + 1] = br[mid]
    return room, met, tiles, typ


def render(room, met, tiles, typ):
    print("macrotiles (8x8), metatile id map (16x16), collision type (16x16):")
    for r in range(8):
        print("  " + " ".join(f"{v:02X}" for v in room[r * 8:r * 8 + 8]))
    print("metatiles:")
    for r in range(16):
        print("  " + " ".join(f"{v:02X}" for v in met[r]))
    print("collision types:")
    for r in range(16):
        print("  " + " ".join(f"{v:X}" for v in typ[r]))


def render_tiles(tiles):
    for r in range(32):
        print("  " + " ".join(f"{v:02X}" for v in tiles[r]))


# ------------------------------------------------------------------- verify
def load_probe_nametable(path, frame=660):
    import struct
    data = open(path, "rb").read()
    rec = 4100
    n = len(data) // rec
    tags = [struct.unpack_from("<I", data, i * rec)[0] for i in range(n)]
    i = min(range(n), key=lambda k: abs(tags[k] - frame))
    off = i * rec
    nt = data[off + 4:off + 4 + 4096]
    return tags[i], nt


def verify(world, cell, frame=660):
    probe = os.path.join(os.path.dirname(__file__), "..", "..", "Research",
                         "DarkwingDuck", "reference", "probe_m1_enter",
                         "vram_frames.bin")
    tag, nt = load_probe_nametable(probe, frame)
    _, _, tiles, _ = decode_room(world, cell)
    p0 = nt[:0x400]
    # at scroll $FA=0 the visible window = room tile rows 0..29 in $2000-$23BF
    ok = sum(1 for r in range(30) for c in range(32)
             if tiles[r][c] == p0[r * 32 + c])
    print(f"room {cell:02X} vs nametable frame {tag}: "
          f"{ok}/960 rows 0-29 = {100.0 * ok / 960:.1f}%")
    return ok


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--world", type=lambda x: int(x, 0), default=0)
    ap.add_argument("--cell", type=lambda x: int(x, 0), default=None)
    ap.add_argument("--row", type=lambda x: int(x, 0), default=None)
    ap.add_argument("--col", type=lambda x: int(x, 0), default=None)
    ap.add_argument("--grid", action="store_true", help="dump layout grid")
    ap.add_argument("--tiles", action="store_true", help="render 32x32 tiles")
    ap.add_argument("--verify", action="store_true")
    args = ap.parse_args()
    w = World(args.world)
    print(f"world {w.id}: set bank ${w.set_bank:02X} room bank "
          f"${room_bank(w):02X} grid ${w.grid_ptr:04X} row width {w.row_width} "
          f"start cell ${w.start_cell:02X} (row {w.start_row}) top row "
          f"{w.top_row} music ${w.music:02X} chr ${w.chr_bg:02X}")
    if args.grid:
        for r in range(4):
            print("  " + " ".join(f"{v:02X}" for v in
                                  w.grid[r * w.row_width:(r + 1) * w.row_width]))
        print("flags:")
        for r in range(4):
            print("  " + " ".join(f"{v:02X}" for v in
                                  w.flags[r * w.row_width:(r + 1) * w.row_width]))
        return
    cell = args.cell
    if cell is None:
        idx = w.start_cell if (args.row is None and args.col is None) else \
            w.cell_index(args.row, args.col)
        cell = w.grid[idx]
        print(f"grid cell index ${idx:02X} -> room {cell:02X}")
    if cell == 0:
        print("empty cell")
        return
    room, met, tiles, typ = decode_room(w, cell)
    render(room, met, tiles, typ)
    if args.tiles:
        render_tiles(tiles)
    if args.verify:
        verify(w, cell)


if __name__ == "__main__":
    sys.exit(main())
