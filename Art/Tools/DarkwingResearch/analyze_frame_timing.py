"""Attribute measured coroutine windows to NMI and native game services."""
from __future__ import annotations

import argparse
import bisect
import collections
import csv
from pathlib import Path


def analyze(path: Path, oam_path: Path | None = None, dependencies: bool = False,
            focus: tuple[int, int] = (6378, 6402)):
    polls = {}
    nmis = []
    windows = []
    window = None
    nmi = None
    poll_marks = collections.defaultdict(dict)
    yield_commits = []
    yielding = None
    phases = {0xCA40: 'player', 0xCA59: 'scroll', 0xCA6B: 'spawn',
              0xCA79: 'zone', 0xCA7C: 'palette', 0xCA84: 'behaviors',
              0xCA8C: 'environment/engine4', 0xCABB: 'OAM5', 0xCAD1: 'OAM4', 0xCAD8: 'RNG'}
    with path.open() as stream:
        for row in csv.DictReader(stream):
            poll, cycle, address = int(row['poll']), int(row['cycle']), int(row['pc'], 16)
            if address in (0xC000, 0xC0A1, 0xC0F6, 0xC0F9, 0xC114, 0xC162, 0xCAE2):
                poll_marks[poll][address] = cycle
            if address == 0xCADF:
                yielding = cycle
            elif address == 0xFF19 and yielding is not None:
                yield_commits.append((yielding, cycle))
                yielding = None
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
    if dependencies:
        analyze_dependencies(windows, nmis, polls, poll_marks, yield_commits, focus)
    return windows, nmis, polls


def body_phase_costs(window):
    events = [(window['start'], 'head'), *window['events'], (window['end'], 'yield')]
    costs = collections.Counter()
    for (start, phase), (end, _) in zip(events, events[1:]):
        stolen = sum(nmi_end - entry + 7 for _, entry, nmi_end in window['nmis'] if start < entry < end)
        costs[phase] += end - start - stolen
    return costs


def analyze_dependencies(windows, nmis, polls, poll_marks, yield_commits, focus):
    nmi_by_poll = {poll: (entry, end) for poll, entry, end in nmis}
    short = [window for window in windows if window['end'] - window['start'] < 60000
             and window['poll'] + 1 in nmi_by_poll
             and any(phase in ('OAM5', 'OAM4') for _, phase in window['events'])]
    rows = []
    for window in short:
        poll = window['poll']
        boundary = nmi_by_poll[poll + 1][0] - 7
        costs = body_phase_costs(window)
        finish = window['start'] + window['own_cycles']
        rows.append((window, costs, boundary - finish))
    print('DEPENDENCIES: measured work only; no costs below are runtime constants or a predictor')
    near = sorted((row for row in rows if not row[0]['nmis']), key=lambda row: row[2])[:8]
    print('closest ordinary CADF-to-next-NMI-ack slacks:', [(row[0]['poll'], row[2]) for row in near])
    print('wake_poll margin_body_to_poll nmi_publish nmi_queue_timers nmi_audio wake_latency ' +
          ' '.join(('head', 'player', 'scroll', 'spawn', 'zone', 'palette', 'behaviors', 'environment', 'OAM', 'RNG')))
    for window, costs, slack in rows:
        poll = window['poll']
        if not focus[0] <= poll <= focus[1]:
            continue
        marks = poll_marks[poll]
        publish = marks.get(0xC0A1, 0) - marks.get(0xC000, 0)
        queue_timers = marks.get(0xC0F6, 0) - marks.get(0xC0A1, 0)
        audio = marks.get(0xC0F9, 0) - marks.get(0xC0F6, 0)
        latency = window['start'] - polls[poll]
        values = [costs[key] for key in ('head', 'player', 'scroll', 'spawn', 'zone', 'palette', 'behaviors')]
        values += [costs['environment/engine4'], costs['OAM5'] + costs['OAM4'], costs['RNG']]
        print(poll, polls[poll + 1] - window['start'] - window['own_cycles'], publish, queue_timers,
              audio, latency, *values)
    crossing = [row for row in rows if row[0]['nmis']]
    keys = sorted({key for _, costs, _ in rows for key in costs})
    print('body component omitted -> lost measured NMI crossings (other components kept exact)')
    for key in keys:
        missed = [window['poll'] + 1 for window, costs, slack in crossing if slack + costs[key] >= 0]
        print(key, len(missed), missed)
    print('NMI/resume component omitted -> lost measured NMI crossings (other components kept exact)')
    omitted_nmi = collections.defaultdict(list)
    for window, _, slack in crossing:
        marks = poll_marks[window['poll']]
        components = {
            'publish': marks[0xC0A1] - marks[0xC000],
            'queue_timers': marks[0xC0F6] - marks[0xC0A1],
            'audio': marks[0xC0F9] - marks[0xC0F6],
            'return_scheduler_pad': window['start'] - marks[0xC0F9],
        }
        for key, cost in components.items():
            if slack + cost >= 0:
                omitted_nmi[key].append(window['poll'] + 1)
    for key, missed in sorted(omitted_nmi.items()):
        print(key, len(missed), missed)
    if yield_commits:
        intervals = collections.Counter()
        nmi_starts = [entry for _, entry, _ in nmis]
        for start, end in yield_commits:
            first = bisect.bisect_right(nmi_starts, start)
            last = bisect.bisect_left(nmi_starts, end)
            stolen = sum(nmi_end - entry + 7 for _, entry, nmi_end in nmis[first:last])
            intervals[end - start - stolen] += 1
        print('CADF through completed FF17 wait-state store (FF19):', dict(intervals))
    print('scope: executed short world windows; long loader/resident paths require their own continuation contracts')


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('trace', type=Path)
    parser.add_argument('--oam', type=Path, help='Additional measured $CAD1 entry clocks from oam_timing.lua')
    parser.add_argument('--dependencies', action='store_true', help='Measured prefix sensitivity and yield-commit timing')
    parser.add_argument('--focus', nargs=2, type=int, default=(6378, 6402), metavar=('FIRST', 'LAST'))
    args = parser.parse_args()
    analyze(args.trace, args.oam, args.dependencies, args.focus)


if __name__ == '__main__':
    main()
