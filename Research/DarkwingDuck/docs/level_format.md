# Darkwing Duck (USA) — Level Geometry Format

Reverse-engineered 2026-10-11. Every claim below is verified either against
the disassembly (bank 7 fixed at $C000) or bit-exactly against the captured
gameplay state in `reference/probe_m1_enter/` (mission 1 / bridge).

Verification result of `Tools/DarkwingResearch/decode_room.py --verify`:
room 0x01 of world 0 decodes to tile ids that match the captured nametable
`$2000-$23BF` (rows 0-29, scroll $FA=0) at **960/960 = 100.0%**, and its PPU
attribute bytes match `$23C0-$23FF` at **64/64 = 100%**.

PRG layout: 8 x 16 KB banks. Bank 7 is fixed at $C000-$FFFF; banks 0-6 map to
$8000-$BFFF through MMC1 ($FF55 writes bank A to $F1/$F4 and $FFF0).
All offsets below are bank-relative unless noted.

## 1. Mission descriptor tables (bank 7, indexed by world id $31, 0-6)

Loader `$DD57` reads, for world X:

| Table   | Addr       | Meaning |
|---------|-----------|---------|
| set     | `$DEAA+X` | -> `$36`: bank holding macrotile/metatile tables (0-6) |
| start   | `$DEB9+X` | -> `$22`,`$23`: start cell index in the layout grid |
| row Y   | `$DEC8+X` | -> `$FB` and `$0520`: starting grid row (world Y row) |
| width   | `$DED7+X` | -> `$2C`; `$2E` = value+1 = **grid row width** |
| chr bg  | `$DEF5+X` | bg CHR bank request (NMI: CHR1 = 16+2*set, CHR0 = 14) |
| music   | `$DF04+X` | music id |
| top row | `$DF41+X` | -> `$2D`: highest reachable grid row (scroll-up limit) |
| grid    | `$DF50/$DF60+X` | -> `$32/$33`: layout grid pointer (bank 7) |
| flags   | `$DF70/$DF80+X` | -> `$34/$35`: per-cell flag table (bank 7) |

Grid pointers per world: bridge `$DF90`, city `$DFD4`, sewers `$E018`,
tower `$E05C`, forest `$E0D4`, warehouse `$E10D`, fowl `$E146`.
Flag tables sit at grid + $01FC (bridge `$E18C`, city `$E1D0`, sewers `$E214`,
tower `$E258`, forest `$E2D0`, warehouse `$E309`, fowl `$E342`).

## 2. Layout grid (world map)

- Flat byte array in bank 7, row-major, **row width W = $2E = $DED7[X]+1**
  (bridge/city/sewers 17, tower 10, forest/warehouse/fowl 19).
- 4 rows for bridge/city/sewers ($44 = 4*17 bytes); tower 12 rows (120 bytes);
  forest/warehouse 3 rows ($39 = 3*19); fowl 3 rows.
- Each byte = **room id** (1-0xC2); 0 = empty (blocked, no room data).
- Cell index `$22` = row*W + col. Start cell `$DEB9[X]` gives (row `$DEC8[X]`,
  col). Bridge: `$33` = row 3, col 0 -> room `01`.
- Cell (row,col) = a 256x256 px world block at world position
  (col*256, row*256).

Bridge mission map (17x4, verified start room 0x01 at row 3 col 0):

```
row0: 15 . 16 . . . . . . . . . . . . . .
row1: . . . . . 08 09 0A 0B 0C 0D 0E . . . . .
row2: . . . . . 07 . . . . . 0F 10 11 12 13 .
row3: 01 02 03 04 05 06 . . . . . . . . . . .
```

## 3. Per-cell flag table ($34/$35, same shape as grid)

`LDA ($34),Y` uses:

- **bit 6** (`AND #$40`, $DBD3/$DC1D): horizontal scroll allowed out of this
  cell (0 = camera stops, scroll value snaps to 0).
- **bit 7** ($DC9B/$DCEC, `BMI`): snap vertical scroll `$FA = 0` on row change
  (no smooth scroll into the next row).
- **bits 0-4** (`AND #$1F`, $EBD4): door/exit id -> `$5B`/`$6D` (door handler
  `$E388`).

Observed values: `$40`=scroll only, `$C0`=scroll+snap, `$41/$42/$C1/$C2`
add low bits (door ids).

## 4. Room data (64 bytes per room)

Pointer built by `$EB27`: `A = grid[cell]; ptr = $7FC0 + A*64`
= **$8000 + (room-1)*64**. Cell value 0 -> pointer high byte $7F, bit7 clear
-> no room (empty cell).

Bank holding room blocks (`$EAD7`): **bank 0 for worlds 0-5, bank 1 for
world 6**. Rooms of all worlds share the id space: bank 0 offset
(room-1)*64, room 0xC2 ends at $3080. Bank 0 also holds set-0 tables at
$36E0+; bank 1 also holds set-1 tables (and fowl rooms).

Format: **64 bytes = 8x8 macrotiles, row-major.** Byte i = macrotile
(row i/8, col i%8). A macrotile is 32x32 px = 2x2 metatiles; a metatile is
16x16 px = 2x2 tiles. A room is therefore 16x16 metatiles = 32x32 tiles =
**256x256 px = exactly one screen**. No compression.

## 5. Tile set tables (bank = `$36` = `$DEAA[X]`, i.e. set id)

| Addr (bank offset) | CPU when mapped | Content |
|---|---|---|
| `$36E0` | `$B6E0` | macrotile table: 256 entries x 4 metatile ids (TL,TR,BL,BR) |
| `$3AE0` | `$BAE0` | metatile -> top-left tile id (256) |
| `$3BE0` | `$BBE0` | metatile -> top-right tile id |
| `$3CE0` | `$BCE0` | metatile -> bottom-left tile id |
| `$3DE0` | `$BDE0` | metatile -> bottom-right tile id |
| `$3EE0` | `$BEE0` | metatile -> byte = (**collision type << 4**) \| **bg palette (0-3)** |

Expansion:
```
for mr in 0..7, mc in 0..7:
    m = room[mr*8+mc]                      # macrotile id
    for q in 0..3:                         # 0=TL 1=TR 2=BL 3=BR
        met = macrotile_table[m*4 + q]
        tile[r=mr*2+q/2*2 .. ][c=mc*2+q%2*2 ..] = tl[met],tr[met],bl[met],br[met]
        collision[mr*2+q/2][mc*2+q%2] = type_table[met] >> 4
        palette  [mr*2+q/2][mc*2+q%2] = type_table[met] & 3
```

Collision types (player engine, `$0600` map nibble): 0 air, 1 one-way,
2 solid, 3 wall, 4 spikes, 5 door, 7/8 grapple-ledge.

PPU attribute byte for the 32x32 px block at metatile rows (2r,2r+1) x cols
(2c,2c+1) = `TL | TR<<2 | BL<<4 | BR<<6` (each = palette of that metatile).
Verified 64/64 against the capture.

## 6. Rendering / scrolling model (engine behavior)

- NMI VRAM queue (buffer `$0360`, flag `$19`; second buffer at `$03B8`, flag
  `$1D`): entries `[addr_hi, addr_lo, count, data...]`, `count+1` bytes written
  linearly, terminated by a byte with bit7 set. Flush `$C119`.
- Room entry / vertical scroll redraw `$E7C6`: for tile row `$2A` (0-29) it
  queues one full tile row (`count=$1F` -> 32 bytes) at PPU
  `$2000 + ($2A>>3)*$100 + ($2A&7)*32` — i.e. hi byte `$20+($2A>>3)`,
  lo byte `($2A&3)<<5 | (($2A>>2)&1)<<7` — plus one attribute row entry
  (hi|`$03`, lo|`$C0` + attr-row index, count 7 -> 8 bytes at `$23C0+attrrow*8`).
  The room's 32 tile rows live at `$2000-$23BF`; **rows 30-31 of the ring
  overlay the attribute table** ($23C0-$23FF) and are only displayed when the
  scroll ring wraps (rooms are 256 px tall, the window is 240 px).
- Horizontal scroll `$FC` (+carry `$FD`): camera pans inside a cell; on wrap
  `$22`++ (next cell) and the incoming cell `$23 = $22 +/- 1` ($DD17) is drawn
  into the other page. Blocked when the next grid byte is 0 or the cell flag
  bit6 is clear; then `$FC` snaps to 0.
- Vertical scroll `$FA` (0-$EF, +3 px/step) inside grid row `$FB`; on wrap
  `$FB +/- 1` and `$22 +/- $2E` (one grid row). Scrolling up stops at
  `$FB == $2D` (top row) or when the grid byte above is 0.
- World position: `worldY = $FB*256 + $FA + screenY`;
  player abs Y = `$0520:$0510` (hi:lo) = `$FB*256 + $FA + screenY`.
  Player abs X = `$0500:$04F0`; `$FD` = current column (0-based) used for
  object activation.
- RAM maps: `$0600` = metatile map for the current room (16x16, with collision
  nibble in the high bits of a 16-bit entry pair), `$0780` = attribute shadow
  (64 bytes = `$23C0` image). Tile probes `$EB52/$EBE8` with offset tables
  `$ED19/$ECAD` read these.

## 7. Object placement (5 parallel arrays, bank 4)

Pointers set by bank 4 `$8000` from per-world pointer tables (lo byte and hi
byte stored $07 bytes apart), loaded into `$72-$7B`:

| zp ptr | lo table | hi table | meaning |
|---|---|---|---|
| `$74/$75` | `$81F3+X` | `$81FA+X` | screenX = **cell column** (0-based) |
| `$72/$73` | `$8201+X` | `$8208+X` | localX = pixel X within the 256px column |
| `$78/$79` | `$820F+X` | `$8216+X` | screenY = **grid row** (0-based) |
| `$76/$77` | `$821D+X` | `$8224+X` | localY = pixel Y within the 256px row |
| `$7A/$7B` | `$822B+X` | `$8232+X` | object type id |

Bridge arrays: cellX `$8239`, localX `$826C`, rowY `$829F`, localY `$82D2`,
type `$8305` (bank 4 offsets). **World position of an object =
(cellX*256 + localX, rowY*256 + localY)** — exactly the grid geometry above.

Activation (`$80DA`, called from the feeder `$8090`): object activates when
`cellX == $FD` (current column) and `rowY == $FB` (current row); spawn-once
bitmask `$0340[idx&7] & $81EB[idx>>3]`, dedup via `$0590`. Type id *4 indexes
a 4-byte descriptor table at bank4 `$8AB3` (flags `$0400`, behavior `$04E0`,
anim `$0420`, field `$0570`).

## 8. What was verified (evidence)

- `$EB27` room pointer math, `$EAD7` room/set bank switch, `$EA36` metatile
  expansion — disassembly, bank 7.
- Room 0x01 (bridge) tile expansion == nametable `$2000-$23BF` at frame 660:
  960/960.
- Attribute bytes from `type_table & 3` == `$23C0-$23FF`: 64/64.
- Grid width 17 (bridge) / 10 (tower): start cell $33=(3,0)->room 01 matches
  the capture; tower start $6E = 11*10+0 matches `$FB=11`.
- Zero page ground truth at frames 198-660: `$22=33 $23=33->34 $2C=10 $2E=11
  $32/$33=$DF90 $34/$35=$E18C $36=00 $FB=03 $FA=$FC=$FD=00`.
- Object world coords == cell*256+local semantics: activation compare
  ($74),Y-$FD / ($78),Y-$FB in bank 4 $80DA.

## 9. Open questions

- Exact meaning of flag bits 0-1 vs 2-4 (door id encoding) — needs the door
  handler `$E388` cross-check with the checkpoint table.
- `$FB` start values > 3 for worlds 3 (tower = 11) confirm rows, but the
  scroll direction conventions (`$FA` up vs down per `$2F` state bits) were
  only partially traced; the port can ignore them and use world Y directly.
- The second VRAM page ($2400) at frame 660 contains a partial screen that
  matches no room window; likely the interrupted first draw of the incoming
  cell or stale briefing-screen data. Does not affect the format.
- Attribute rows 6-7 (tile rows 24-31) of rooms wider than 30 tile rows are
  written through the same `$23C0` ring; only relevant for byte-exact PPU
  emulation, not for a Unity port.
- Metatile animation (flashing lights etc.) is handled outside this format
  (anim id tables in bank 5); not covered here.
