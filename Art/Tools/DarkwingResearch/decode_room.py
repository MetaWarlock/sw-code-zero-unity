#!/usr/bin/env python3
"""Darkwing Duck (USA) level room decoder.

Decodes the room geometry format reverse-engineered from the ROM (full spec:
Art/Research/DarkwingDuck/docs/level_format.xhtml).

Verified against probe_m1_enter (world 0 / bridge, start cell $33):
  room 0x01 tiles  vs captured nametable $2000 rows 0-29 : 960/960 = 100%
  room 0x01 attrs  vs captured attribute table $23C0-$23FF:  64/64 = 100%

Usage:
  python decode_room.py --world 0 --grid                 # dump layout grid+flags
  python decode_room.py --world 0 --cell 0x01            # macrotiles/metatiles/types
  python decode_room.py --world 0 --cell 0x01 --tiles    # + 32x32 tile ids
  python decode_room.py --world 0 --cell 0x01 --verify   # vs probe nametable
"""
import argparse
import os
import sys

ROM_PATH = os.path.join(os.path.dirname(os.path.abspath(__file__)),
                        "..", "..", "Art", "Darkwing Duck (USA).nes")
_PRG = None


def prg():
    global _PRG
    if _PRG is None:
        with open(ROM_PATH, "rb") as f:
            _PRG = f.read()[0x10:]
    return _PRG


def bank(i):
    return prg()[i * 0x4000:(i + 1) * 0x4000]


class World:
    """Mission/world descriptor (loaded by $DD57, bank 7 tables)."""

    def __init__(self, wid):
        b7 = bank(7)
        self.id = wid
        self.set_bank = b7[0x1EAA + wid]        # $DEAA[X] -> $36: metatile bank
        self.start_cell = b7[0x1EB9 + wid]      # $DEB9[X] -> $22/$23 start cell
        self.start_row = b7[0x1EC8 + wid]       # $DEC8[X] -> $FB start grid row
        self.row_width = b7[0x1ED7 + wid] + 1   # $DED7[X] -> $2C; $2E = width
        self.chr_bg = b7[0x1EF5 + wid]          # $DEF5[X] bg CHR bank
        self.music = b7[0x1F04 + wid]           # $DF04[X]
        self.top_row = b7[0x1F41 + wid]         # $DF41[X] -> $2D highest row
        gp = b7[0x1F50 + wid] | b7[0x1F60 + wid] << 8   # $32/$33 grid pointer
        fp = b7[0x1F70 + wid] | b7[0x1F80 + wid] << 8   # $34/$35 flag pointer
        self.grid_ptr = gp
        self.flag_ptr = fp
        self.grid_base = gp - 0xC000            # offset inside bank 7
        self.flag_base = fp - 0xC000
        n = 4 * self.row_width
        self.grid = b7[self.grid_base:self.grid_base + n]
        self.flags = b7[self.flag_base:self.flag_base + n]

    def room_at(self, row, col):
        return self.grid[row * self.row_width + col]

    def index(self, row, col):
        return row * self.row_width + col


def room_bank(world):
    """PRG bank holding the 64-byte room blocks ($EAD7)."""
    return 1 if world.set_bank >= 6 else 0


def macrotile_table(world):
    """256 entries x 4 metatile ids, set bank offset 0x36E0 ($B6E0)."""
    b = bank(world.set_bank)
    return b[0x36E0:0x36E0 + 1024]


def metatile_tables(world):
    """(tl, tr, bl, br, type) 256-entry arrays, set bank.

    0x3AE0/0x3BE0/0x3CE0/0x3DE0: tile id of each metatile quadrant.
    0x3EE0: byte = (collision_type << 4) | bg_palette.
    """
    b = bank(world.set_bank)
    return (b[0x3AE0:0x3AE0 + 256], b[0x3BE0:0x3BE0 + 256],
            b[0x3CE0:0x3CE0 + 256], b[0x3DE0:0x3DE0 + 256],
            b[0x3EE0:0x3EE0 + 256])


def decode_room(world, cell):
    """Return (macrotiles 8x8, metatiles 16x16, tiles 32x32, types 16x16).

    Room block: 64 bytes at PRG bank room_bank(world), offset (cell-1)*64.
    Byte i = macrotile (i//8, i%8); each macrotile = 2x2 metatiles (32x32 px),
    each metatile = 2x2 tiles (16x16 px). Room = 256x256 px.
    """
    b = bank(room_bank(world))
    off = (cell - 1) * 64
    room = b[off:off + 64]
    if len(room) != 64:
        raise ValueError(f"room {cell:#04x} out of range")
    mt_tbl = macrotile_table(world)
    tl, tr, bl, br, ty = metatile_tables(world)
    met = [[0] * 16 for _ in range(16)]
    tiles = [[0] * 32 for _ in range(32)]
    typ = [[0] * 16 for _ in range(16)]
    pal = [[0] * 16 for _ in range(16)]
    for mr in range(8):
        for mc in range(8):
            m = room[mr * 8 + mc]
            for q in range(4):            # 0=TL 1=TR 2=BL 3=BR
                mid = mt_tbl[m * 4 + q]
                qr, qc = divmod(q, 2)
                r, c = mr * 2 + qr, mc * 2 + qc
                met[r][c] = mid
                typ[r][c] = ty[mid] >> 4          # collision type 0-15
                pal[r][c] = ty[mid] & 3           # bg palette 0-3
                tiles[r * 2][c * 2] = tl[mid]
                tiles[r * 2][c * 2 + 1] = tr[mid]
                tiles[r * 2 + 1][c * 2] = bl[mid]
                tiles[r * 2 + 1][c * 2 + 1] = br[mid]
    return room, met, tiles, typ, pal


def attr_bytes(world, cell):
    """64 PPU attribute bytes ($23C0 image) for a room."""
    _, _, _, _, pal = decode_room(world, cell)
    out = []
    for ar in range(8):
        for ac in range(8):
            v = (pal[ar * 2][ac * 2]
                 | pal[ar * 2][ac * 2 + 1] << 2
                 | pal[ar * 2 + 1][ac * 2] << 4
                 | pal[ar * 2 + 1][ac * 2 + 1] << 6)
            out.append(v)
    return out


def render(room, met, typ, pal):
    print("macrotiles 8x8:")
    for r in range(8):
        print("  " + " ".join(f"{v:02X}" for v in room[r * 8:r * 8 + 8]))
    print("metatile ids 16x16:")
    for r in range(16):
        print("  " + " ".join(f"{v:02X}" for v in met[r]))
    print("collision types 16x16 (0=air 1=one-way 2=solid 3=wall 4=spike "
          "5=door 7/8=ledge):")
    for r in range(16):
        print("  " + " ".join(f"{v:X}" for v in typ[r]))
    print("bg palette 16x16:")
    for r in range(16):
        print("  " + " ".join(f"{v}" for v in pal[r]))


def render_tiles(tiles):
    print("tile ids 32x32:")
    for r in range(32):
        print("  " + " ".join(f"{v:02X}" for v in tiles[r]))


def load_probe_nametable(path, frame=660):
    import struct
    data = open(path, "rb").read()
    rec = 4100
    n = len(data) // rec
    tags = [struct.unpack_from("<I", data, i * rec)[0] for i in range(n)]
    i = min(range(n), key=lambda k: abs(tags[k] - frame))
    off = i * rec
    return tags[i], data[off + 4:off + 4 + 4096]


def verify(world, cell, frame=660):
    probe = os.path.join(os.path.dirname(os.path.abspath(__file__)),
                         "..", "..", "Research", "DarkwingDuck", "reference",
                         "probe_m1_enter", "vram_frames.bin")
    tag, nt = load_probe_nametable(probe, frame)
    _, _, tiles, _, _ = decode_room(world, cell)
    p0 = nt[:0x400]
    ok = sum(1 for r in range(30) for c in range(32)
             if tiles[r][c] == p0[r * 32 + c])
    pa = nt[0x3C0:0x400]
    ab = attr_bytes(world, cell)
    oka = sum(1 for i in range(64) if ab[i] == pa[i])
    print(f"room {cell:02X} vs nametable frame {tag}: tiles {ok}/960 "
          f"({100.0 * ok / 960:.1f}%), attrs {oka}/64")
    return ok, oka


def main():
    ap = argparse.ArgumentParser(description=__doc__)
    ap.add_argument("--world", type=lambda x: int(x, 0), default=0)
    ap.add_argument("--cell", type=lambda x: int(x, 0), default=None,
                    help="room id (grid byte value)")
    ap.add_argument("--row", type=lambda x: int(x, 0), default=None)
    ap.add_argument("--col", type=lambda x: int(x, 0), default=None)
    ap.add_argument("--grid", action="store_true", help="dump layout grid")
    ap.add_argument("--tiles", action="store_true", help="render 32x32 tiles")
    ap.add_argument("--verify", action="store_true",
                    help="compare with probe_m1_enter capture")
    args = ap.parse_args()
    w = World(args.world)
    rb = room_bank(w)
    print(f"world {w.id}: set bank ${w.set_bank:02X}, room bank ${rb:02X}, "
          f"grid ${w.grid_ptr:04X} (flags ${w.flag_ptr:04X}), row width "
          f"{w.row_width}, start cell ${w.start_cell:02X} = row "
          f"{w.start_cell // w.row_width} col {w.start_cell % w.row_width}, "
          f"start row {w.start_row}, top row {w.top_row}")
    if args.grid:
        rows = 4
        for r in range(rows):
            print("  " + " ".join(f"{v:02X}" for v in
                                  w.grid[r * w.row_width:(r + 1) * w.row_width]))
        print("flags (bit6=scroll-h, bit7=?, low 5 bits=door id):")
        for r in range(rows):
            print("  " + " ".join(f"{v:02X}" for v in
                                  w.flags[r * w.row_width:(r + 1) * w.row_width]))
        return
    cell = args.cell
    if cell is None:
        idx = (w.index(args.row, args.col) if args.row is not None else
               w.start_cell)
        cell = w.grid[idx]
        print(f"grid cell ${idx:02X} -> room {cell:02X}")
    if cell == 0:
        print("empty cell (no room)")
        return
    room, met, tiles, typ, pal = decode_room(w, cell)
    render(room, met, typ, pal)
    if args.tiles:
        render_tiles(tiles)
    if args.verify:
        verify(w, cell)


if __name__ == "__main__":
    sys.exit(main())
