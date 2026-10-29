"""Scan every world's cell grid for real hook-ride / snap columns.

Rules read directly from bank 7 (no inference):
  $DAFD caller  : gate $FC==0, script nibble ($34),Y & $0F == 1 → hook grab ($DB6D),
                  nibble == 2 → vertical snap ($DB40 → up-scroll).
  $DC6E down    : needs ($32), $22+$2E != 0 (cell BELOW exists) and $FB != $2D.
  $DCC4 up      : needs ($32), $22-$2E != 0 (cell ABOVE exists) and $FB != 0.
  $DD36 / $DD17 : both require $FC==0 to start a row change.
"""
import sys
sys.path.insert(0, 'Art/Tools/DarkwingResearch')
import decode_room as dr

for wid in range(8):
    try:
        w = dr.World(wid)
    except Exception as e:
        print(wid, 'ERR', e)
        continue
    rows = len(w.grid) // w.row_width
    hits = []
    for idx in range(len(w.grid)):
        f = w.flags[idx]
        nib = f & 0x0F
        if nib == 0:
            continue
        row, col = divmod(idx, w.row_width)
        below = w.grid[idx + w.row_width] if idx + w.row_width < len(w.grid) else 0
        above = w.grid[idx - w.row_width] if idx >= w.row_width else 0
        if nib == 1 and below:
            hits.append(('HOOK-DOWN', idx, row, col, f, w.grid[idx], below, row == w.top_row))
        elif nib == 2 and above:
            hits.append(('SNAP-UP', idx, row, col, f, w.grid[idx], above, row == w.top_row))
    print(f'world {wid}: start_cell={w.start_cell:02X} start_row={w.start_row} '
          f'width={w.row_width} rows={rows} top_row($2D)={w.top_row}')
    for h in hits:
        print('   %-9s cell=%3d (row %d col %2d) flag=%02X grid=%02X neigh=%02X at_top=%s'
              % (h[0], h[1], h[2], h[3], h[4], h[5], h[6], h[7]))
    if not hits:
        print('   (no nibble-1 cell with a below neighbour and no nibble-2 cell with an above neighbour)')
