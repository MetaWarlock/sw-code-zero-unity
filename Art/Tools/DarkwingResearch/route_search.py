"""Reachability/route search over original-ROM jump physics.

Nodes = standable surface positions (metatile-aligned tops, centre y = top-12).
Edges = walk along a surface, or one full jump modelled with the ROM constants
(jump impulse 4.8945 px/frame, gravity 0.25 px/frame^2, air steering 1.5
px/frame, max fall 8 px/frame) - see docs/player_mechanics.xhtml #3.

  python Art/Tools/DarkwingResearch/route_search.py --world 0 --start 44,884 --goal 4064,500
"""
from __future__ import annotations
import argparse
import sys
from collections import deque
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))
from probe_run import world_collision_grid  # noqa: E402

V0 = 4.8945
GRAV = 0.25
SPD = 1.5
FEET = 12
MAXFALL = 8.0
LANDABLE = {1, 2, 3, 8, 9, 10, 11, 12, 13, 14, 15}
BLOCKING = {2, 3}


class World:
    def __init__(self, wid: int):
        grid, self.cols, self.rows, _, _ = world_collision_grid(wid)
        self.grid = grid
        self.w = self.cols * 16
        self.h = self.rows * 16

    def typ(self, x: int, y: int) -> int:
        if x < 0 or y < 0 or x >= self.w or y >= self.h:
            return 0
        return ord(self.grid[(y // 16) * self.cols + (x // 16)]) - 64

    def standable(self, x: int, y: int) -> bool:
        """(x, y) is a centre that can rest on a surface whose top is y+FEET.

        The engine's floor probe ($40=6, points -11/0/+11 at y+12) lands on the
        max result of three samples, so any of the three feet positions is enough.
        """
        headroom = (self.typ(x, y - 16) not in BLOCKING
                    and self.typ(x, y) not in BLOCKING)
        if not headroom:
            return False
        for px in (x - 11, x, x + 11):
            t = self.typ(px, y + FEET)
            if t in LANDABLE and (t != 1 or self.typ(px, y + FEET - 16) not in LANDABLE):
                return True
        return False


def descent_time(dy: float) -> float:
    """Frames until the discrete ROM jump arc is at/below dy (positive = downward).

    Integrates the engine's own scheme (y -= v, v -= 0.25, terminal fall 8) rather
    than the closed form, because the discrete apex is 50.4 px, not 48.
    """
    v = V0
    y = 0.0
    apex = 0.0
    for t in range(1, 400):
        y -= v
        v -= GRAV
        if v < -MAXFALL:
            v = -MAXFALL
        if y < apex:
            apex = y
        if v < 0 and dy < apex:
            return -1.0
        if v < 0 and y >= dy:
            return float(t)
    return -1.0


def build_nodes(w: World, step: int = 8):
    nodes = {}
    for x in range(0, w.w, step):
        for top in range(16, w.h, 16):
            if w.standable(x, top - FEET):
                nodes[(x, top - FEET)] = len(nodes)
    return sorted(nodes)


def arc_clear(w: World, x0: int, y0: int, x1: int, y1: int, t1: float) -> bool:
    """Fly the discrete arc steering toward x1; reject if the body enters a wall."""
    v = V0
    y = float(y0)
    dx = x1 - x0
    d = 1 if dx > 0 else (-1 if dx < 0 else 0)
    moved = 0.0
    for _ in range(1, int(t1) + 1):
        y -= v
        v -= GRAV
        if v < -MAXFALL:
            v = -MAXFALL
        if d and abs(moved) < abs(dx):
            moved += d * SPD
        x = x0 + moved
        yi = int(round(y))
        if w.typ(int(x), yi) in BLOCKING or w.typ(int(x), yi - 16) in BLOCKING:
            return False
    return True


def successors(w: World, node, heights, step=8):
    x0, y0 = node
    # walk: same surface left/right until it ends or a wall blocks the body
    for d in (-1, 1):
        x = x0 + d * step
        while 0 <= x < w.w and w.standable(x, y0) and w.typ(x, y0 - 16) not in BLOCKING:
            yield (x, y0)
            x += d * step
    # jump: for every surface height reachable on the descending branch
    for y1 in heights:
        dy = y1 - y0
        t1 = descent_time(dy)
        if t1 < 0 or t1 > 400:
            continue
        reach = int(SPD * t1)
        lo = max(0, ((x0 - reach) // step) * step)
        hi = min(w.w - step, ((x0 + reach) // step) * step)
        for x1 in range(lo, hi + step, step):
            if abs(x1 - x0) > reach:
                continue
            if not w.standable(x1, y1):
                continue
            if not arc_clear(w, x0, y0, x1, y1, t1):
                continue
            yield (x1, y1)


def main():
    p = argparse.ArgumentParser(description=__doc__)
    p.add_argument("--world", type=int, default=0)
    p.add_argument("--start", default="44,884")
    p.add_argument("--goal", default="")
    p.add_argument("--step", type=int, default=8)
    p.add_argument("--report", type=int, default=40)
    a = p.parse_args()
    w = World(a.world)
    sx, sy = (int(v) for v in a.start.split(","))
    nodes = build_nodes(w, a.step)
    heights = sorted({y for _, y in nodes})
    index = {n: i for i, n in enumerate(nodes)}
    start = min(nodes, key=lambda n: abs(n[0] - sx) + abs(n[1] - sy))
    print(f"world {a.world}: {len(nodes)} nodes, {len(heights)} surface heights, "
          f"start {start} (asked {sx},{sy})")
    prev = {index[start]: None}
    q = deque([start])
    while q:
        cur = q.popleft()
        for nxt in successors(w, cur, heights, a.step):
            i = index.get(nxt)
            if i is None or i in prev:
                continue
            prev[i] = index[cur]
            q.append(nxt)
    seen = sum(1 for i in range(len(nodes)) if i in prev)
    print(f"reachable {seen}/{len(nodes)}")
    far = max((nodes[i] for i in range(len(nodes)) if i in prev), key=lambda n: n[0])
    print(f"max reachable x = {far[0]} y = {far[1]}")
    hi = min((nodes[i] for i in range(len(nodes)) if i in prev), key=lambda n: n[1])
    print(f"min reachable y = {hi[0]},{hi[1]}")

    def path_to(pt):
        best = min((nodes[i] for i in range(len(nodes)) if i in prev),
                   key=lambda n: abs(n[0] - pt[0]) + abs(n[1] - pt[1]))
        out = []
        i = index[best]
        while i is not None:
            out.append(nodes[i])
            i = prev[i]
        return list(reversed(out)), best

    if a.goal:
        gx, gy = (int(v) for v in a.goal.split(","))
        route, best = path_to((gx, gy))
        print(f"goal ({gx},{gy}) -> nearest reachable {best}, {len(route)} hops")
        for n in route[-a.report:]:
            print("   ", n)
    else:
        for x in range(0, w.w, 256):
            col = [nodes[i] for i in range(len(nodes))
                   if i in prev and x <= nodes[i][0] < x + 256]
            if col:
                print(f"x {x:4d}-{x+255:4d}: reachable ys "
                      f"{sorted({y for _, y in col})}")


if __name__ == "__main__":
    main()
