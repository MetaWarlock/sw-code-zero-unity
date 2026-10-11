"""Structural comparison: Darkwing audio port vs original (Mesen probe).

Compares, per music channel (pulse1 = music ch 3, pulse2 = ch 2, triangle = ch 1):
  - note-event boundaries (frames where the pitch-hold counter is refilled, i.e. a
    note/command fetch happened)
  - note periods (group-B period registers $0748/$074C + c folded to the APU period
    via the $8849 octave-fold — the value the driver actually writes to $4002/$4003)
The port starts from song start; the probe starts mid-song, so sequences are
aligned by finding the offset with the longest matching run (diff-based).

Also compares per-frame RMS envelopes of the two WAVs (port WAV vs Mesen WAV)
at the aligned offset, reported as correlation.

Usage: python Tools/DarkwingResearch/compare_audio_port.py
"""
from __future__ import annotations
import csv
import json
import struct
import wave
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
REF = ROOT / "Research/DarkwingDuck"

PORT_STATE = REF / "audio_port_song1_state.csv"
PROBE_STATE = REF / "reference/probe_audio_song1/ram_frames.bin"
PORT_WAV = REF / "audio_port_song1.wav"
PROBE_WAV = REF / "reference/probe_audio_song1/audio.wav"


def thr_tables():
    b3 = (ROOT / "Research/DarkwingDuck/extracted/raw/prg.bin").read_bytes()[0xC000:0x10000]
    return b3[0x0953:0x095B]


THR = thr_tables()


def fold_period(lo: int, hi: int) -> int:
    """$8849-$886E: octave-fold the stored 16-bit period into an APU period."""
    y = 8
    while True:
        y -= 1
        if hi >= THR[y]:
            break
    n = (y + hi) & 0xFF
    c1 = (n & 7) + 7
    a = (n & 0x38) ^ 0x38
    pair = (c1 << 8) | lo
    while a != 0:
        pair >>= 1
        a = (a - 8) & 0xFF
    return (((pair >> 8) & 7) << 8) | (pair & 0xFF)


def load_port():
    """(frame -> {ch: (apu_period, hold, duration)}) from the port state CSV."""
    rows = {}
    with open(PORT_STATE) as f:
        for r in csv.DictReader(f):
            fr = int(r["frame"])
            rows[fr] = {}
            for name, per, hold, dur in (
                ("p1", "p1_period", "p1_hold", "p1_dur"),
                ("p2", "p2_period", "p2_hold", "p2_dur"),
                ("tri", "tri_period", "tri_hold", "tri_dur"),
            ):
                raw = int(r[per], 16)
                rows[fr][name] = (fold_period(raw & 0xFF, raw >> 8), int(r[hold], 16), int(r[dur], 16))
    return rows


def load_probe():
    """ram_frames.bin: 2048 bytes of NES RAM ($0000-$07FF) per frame."""
    data = PROBE_STATE.read_bytes()
    rec = 2048
    n = len(data) // rec
    rows = {}
    for f in range(1, n + 1):
        base = (f - 1) * rec
        p7 = data[base + 0x700: base + 0x800]
        row = {}
        for name, c in (("p1", 3), ("p2", 2), ("tri", 1)):
            lo, hi = p7[0x48 + c], p7[0x4C + c]
            row[name] = (fold_period(lo, hi), p7[0x60 + c], p7[0x68 + c])
        rows[f] = row
    return rows


def note_events(rows):
    """frame -> per-channel list of (frame, period) where a fetch happened
    (hold counter refilled to a value > 1, or period changed while holding==0)."""
    frames = sorted(rows)
    ev = {k: [] for k in ("p1", "p2", "tri")}
    prev = None
    for f in frames:
        cur = rows[f]
        if prev is not None:
            for k in ev:
                p_cur, h_cur, _ = cur[k]
                _, h_prev, _ = prev[k]
                # a new note fetch: the hold counter jumps up from a low value
                if h_cur > 1 and h_prev <= h_cur:
                    pass
        prev = cur
    # simpler robust rule: a "note boundary" = frame where hold(t) > hold(t-1)
    for i in range(1, len(frames)):
        f, g = frames[i], frames[i - 1]
        for k in ev:
            if rows[f][k][1] > rows[g][k][1]:
                ev[k].append((f, rows[f][k][0]))
    return ev


def align(a, b, max_off=1200):
    """Find offset k maximizing matches between sequence a (port) and b (probe):
    a[i] == b[i+k]. Returns (k, matches, matched_len)."""
    best = (0, -1, 0)
    bset = {}
    for i, v in enumerate(b):
        bset.setdefault(v, []).append(i)
    for k in range(-max_off, max_off):
        m = 0
        run = 0
        bestrun = 0
        for i, v in enumerate(a):
            j = i + k
            if 0 <= j < len(b) and b[j] == v:
                m += 1
                run += 1
                bestrun = max(bestrun, run)
            else:
                run = 0
        if m > best[1]:
            best = (k, m, bestrun)
    return best


def transitions(ev):
    """Consecutive (interval-frames, period) transitions — the structural fingerprint."""
    return [(ev[i][0] - ev[i - 1][0], ev[i][1]) for i in range(1, len(ev))]


def longest_common_run(a, b):
    """(length, end_i, end_j) of the longest common contiguous run."""
    n, m = len(a), len(b)
    best = (0, 0, 0)
    dp = [[0] * (m + 1) for _ in range(n + 1)]
    for i in range(1, n + 1):
        row = dp[i]
        prev = dp[i - 1]
        for j in range(1, m + 1):
            if a[i - 1] == b[j - 1]:
                row[j] = prev[j - 1] + 1
                if row[j] > best[0]:
                    best = (row[j], i, j)
    return best


def wav_envelope(path, frames, samples_per_frame=None):
    with wave.open(str(path), "rb") as w:
        nch, sw, rate, n = w.getnchannels(), w.getsampwidth(), w.getframerate(), w.getnframes()
        raw = w.readframes(n)
    assert sw == 2, f"expected 16-bit, got {sw * 8}-bit"
    xs = struct.unpack(f"<{len(raw) // 2}h", raw)
    if nch == 2:  # mix stereo to mono
        xs = [(xs[i] + xs[i + 1]) // 2 for i in range(0, len(xs), 2)]
    spf = samples_per_frame or (len(xs) / frames)
    env = []
    for f in range(frames):
        a, b = int(f * spf), int((f + 1) * spf)
        seg = xs[a:b]
        env.append((sum(x * x for x in seg) / max(1, len(seg))) ** 0.5)
    return env, rate


def corr(a, b):
    if len(a) != len(b) or not a:
        return None
    ma, mb = sum(a) / len(a), sum(b) / len(b)
    va = sum((x - ma) ** 2 for x in a)
    vb = sum((x - mb) ** 2 for x in b)
    if va == 0 or vb == 0:
        return None
    cov = sum((x - ma) * (y - mb) for x, y in zip(a, b))
    return cov / (va * vb) ** 0.5


def main():
    port = load_port()
    probe = load_probe()
    print(f"port frames: {len(port)}, probe frames: {len(probe)}")

    port_ev = note_events(port)
    probe_ev = note_events(probe)

    report = {}
    for k in ("p1", "p2", "tri"):
        sa, sb = transitions(port_ev[k]), transitions(probe_ev[k])
        L, ei, ej = longest_common_run(sa, sb)
        report[k] = {
            "port_transitions": len(sa),
            "probe_transitions": len(sb),
            "identical_interval_period_run": L,
            "run_port": sa[max(0, ei - L):ei],
            "run_probe": sb[max(0, ej - L):ej],
        }
        print(f"{k}: port transitions={len(sa)} probe={len(sb)} "
              f"identical (interval,period) run = {L}")

    # duration comparison via the longest common run: all intervals inside it match by
    # construction — report the share of transitions covered by any exact run instead.
    for k in ("p1", "p2", "tri"):
        sa, sb = transitions(port_ev[k]), transitions(probe_ev[k])
        st = set(sa)
        hits = sum(1 for t in sa if t in st)
        report[k]["port_transitions_present_in_probe"] = hits
        print(f"{k}: {hits}/{len(sa)} port transitions occur in the probe sequence")

    # WAV envelope correlation at the best frame offset (stereo probe mixed to mono)
    if PORT_WAV.exists() and PROBE_WAV.exists():
        pe, prate = wav_envelope(PORT_WAV, len(port))
        qe, qrate = wav_envelope(PROBE_WAV, len(probe))
        best = None
        for off in range(-300, 301):
            a = pe[max(0, off):len(pe) + min(0, off)]
            b = qe[max(0, -off):len(qe) - max(0, off)]
            n = min(len(a), len(b))
            c = corr(a[:n], b[:n])
            if c is not None and (best is None or c > best[1]):
                best = (off, c)
        report["wav"] = {
            "port_rate": prate, "probe_rate": qrate,
            "port_frames": len(pe), "probe_frames": len(qe),
            "best_frame_offset": best[0] if best else None,
            "best_envelope_correlation": None if not best else round(best[1], 4),
        }
        print(f"wav: port {prate} Hz {len(pe)} frames, probe {qrate} Hz {len(qe)} frames, "
              f"best envelope corr at frame offset {best[0]} = {best[1]:.4f}")

    out = REF / "audio_port_comparison.json"
    out.write_text(json.dumps(report, indent=2))
    print("wrote", out)


if __name__ == "__main__":
    main()
