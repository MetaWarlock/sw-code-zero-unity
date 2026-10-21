"""Attribute measured coroutine windows to NMI and native game services."""
from __future__ import annotations

import argparse
import bisect
import collections
import csv
from pathlib import Path


def analyze(path: Path, oam_path: Path | None = None):
    polls = {}
    nmis = []
    windows = []
    window = None
    nmi = None
    phases = {0xCA40: 'player', 0xCA59: 'scroll', 0xCA6B: 'spawn',
              0xCA79: 'zone', 0xCA7C: 'palette', 0xCA84: 'behaviors',
              0xCA8C: 'environment/engine4', 0xCABB: 'OAM5', 0xCAD1: 'OAM4', 0xCAD8: 'RNG'}
    with path.open() as stream:
        for row in csv.DictReader(stream):
            poll, cycle, address = int(row['poll']), int(row['cycle']), int(row['pc'], 16)
            if address == 0:
                polls[poll] = cycle
            elif address == 0xC000:
                if nmi is not None:
                    raise ValueError('Nested or missing NMI return')
                nmi = (poll, cycle)
            elif address == 0xC114:
                if nmi is None:
                    raise ValueError('NMI return without entry')
                nmis.append((nmi[0], nmi[1], cycle + 6))
                nmi = None
            elif address == 0xCAE2:
                if window is not None:
                    raise ValueError('Body resumed before yielding')
                window = dict(poll=poll, start=cycle, events=[])
            elif address == 0xCADF and window is not None:
                window['end'] = cycle
                windows.append(window)
                window = None
            elif window is not None and address in phases:
                window['events'].append((cycle, phases[address]))
    nmi_starts = [entry for _, entry, _ in nmis]
    internal_entries = []
    if oam_path:
        with oam_path.open() as stream:
            internal_entries = sorted(int(row['start']) for row in csv.DictReader(stream) if row.get('engine') == '4')
    brackets = []
    for window in windows:
        internal_first = bisect.bisect_left(internal_entries, window['start'])
        internal_last = bisect.bisect_left(internal_entries, window['end'])
        window['events'].extend((cycle, 'OAM4') for cycle in internal_entries[internal_first:internal_last])
        window['events'].sort()
        first = bisect.bisect_right(nmi_starts, window['start'])
        last = bisect.bisect_left(nmi_starts, window['end'])
        window['nmis'] = nmis[first:last]
        window['own_cycles'] = window['end'] - window['start'] - sum(
            end - entry + 7 for _, entry, end in window['nmis'])
        if window['nmis'] and window['end'] - window['start'] < 60000:
            brackets.append(window)
    phase_gaps = collections.Counter(entry - polls[poll] for poll, entry, _ in nmis if poll in polls)
    print(f'polls={len(polls)} windows={len(windows)} brackets={len(brackets)}')
    print(f'measured NMI-entry minus poll cycles: {dict(sorted(phase_gaps.items()))}')
    print('bracket: wake_poll crossed_poll phase body_cycles remaining_body_cycles wake_after_poll')
    for window in brackets:
        for poll, entry, end in window['nmis']:
            phase = next((name for cycle, name in reversed(window['events']) if cycle < entry), 'head')
            remaining = window['end'] - end
            print(f"{window['poll']:7d} {poll:7d} {phase:12s} {window['own_cycles']:7d} "
                  f"{remaining:7d} {window['start'] - polls[window['poll']]:7d}")
    print('phase costs exclude nested NMI entry/return including the seven hardware entry cycles')
    for window in brackets[:1]:
        events = [(window['start'], 'head'), *window['events'], (window['end'], 'yield')]
        for (start, phase), (end, _) in zip(events, events[1:]):
            stolen = sum(nmi_end - entry + 7 for _, entry, nmi_end in window['nmis'] if start < entry < end)
            print(f'{phase:12s} {end - start - stolen:7d}')
    return windows, nmis, polls


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('trace', type=Path)
    parser.add_argument('--oam', type=Path, help='Additional measured $CAD1 entry clocks from oam_timing.lua')
    args = parser.parse_args()
    analyze(args.trace, args.oam)


if __name__ == '__main__':
    main()
