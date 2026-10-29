#!/bin/bash
# Canonical mutation-proof driver for the ZERO-54 ground00 crouch-entry gate.
#
# Same contract as the accepted card-release driver: nothing is reconstructed from memory, the
# runtime is always restored from Git, every stage pins a sha256, and a mutant is only proved when
# the gate exits with the expected code *and* names the expected assertion. Restores run on every
# path, including failure and Ctrl-C.
#
# What it proves, kept separate:
#   DETECTION - each targeted break of the guard, of the $CC44 tail or of a slice owner makes the
#               gate nonzero;
#   ASSERTION - which named column of the gate reports it (route / synthetic / guards / clock
#               prefix), so a pass cannot come from an unrelated refusal;
#   NO SIDE EFFECT - the probe-free cold replay gives the same body prefix at the priced route poll
#               and the same first CAFC refusal poll as the probed gate run.
#
# Usage (from the assigned project root, or any cwd):
#   bash Art/Tools/DarkwingResearch/cost/ground00_mutation_proof.sh [EVIDENCE_DIR] [BANKED_JSON] [OUT_DIR_PREFIX]
# Defaults: EVIDENCE_DIR=Art/Temp/ZERO-54/mutation-proof, BANKED_JSON=Art/Temp/ZERO-54/ground00_source_1924_6420.json.
# The banked JSON must come from cost/state09_source_price.py --ground00; the gate refuses a JSON
# that does not report its own PASS. Requires .NET 10 and the preserved city session input.
# Must not run while any runtime source is edited, and never at the same time as
# cardrelease_mutation_proof.sh, which patches runtime sources too.
set -euo pipefail
ROOT="$(cd "$(dirname "$0")/../../../.." && pwd)"
cd "$ROOT" || exit 1
[ -f AGENTS.md ] && [ -d Art/Unity/CodeZero/Darkwing ] || { echo "not the assigned project root: $ROOT"; exit 1; }

EVIDENCE="${1:-Art/Temp/ZERO-54/mutation-proof}"
BANKED="${2:-Art/Temp/ZERO-54/ground00_source_1924_6420.json}"
BOUNDARY="${3:-2543}"
AGG=Art/Tools/DarkwingResearch/cost/reached_body_aggregate.cs
RT=Art/Unity/CodeZero/Darkwing/Core/PlayerCore.ReachedDispatchTiming.cs
GATE=Art/Tools/DarkwingResearch/cost/ground00_mutation_proof.sh
[ -f "$BANKED" ] || { echo "missing banked pass $BANKED; run cost/state09_source_price.py --ground00 first"; exit 1; }
git diff --quiet HEAD -- "$RT" "$AGG" "$GATE" || { echo 'Refusing to run over changed runtime or gate source'; exit 1; }
EVIDENCE="$(python - "$ROOT" "$EVIDENCE" <<'PY'
from pathlib import Path
import sys
root = Path(sys.argv[1]).resolve()
out = Path(sys.argv[2]).resolve()
if not out.is_relative_to(root / 'Art' / 'Temp') or out == root / 'Art' / 'Temp':
    sys.exit('Evidence must be a new directory below Art/Temp')
if out.exists():
    sys.exit('Evidence directory exists; choose a new path')
print(out.as_posix())
PY
)"

hash_of() { sha256sum "$1" | cut -d' ' -f1; }
PIN_RT="$(git show "HEAD:$RT" | sha256sum | cut -d' ' -f1)"
PIN_AGG="$(git show "HEAD:$AGG" | sha256sum | cut -d' ' -f1)"
mkdir -p "$EVIDENCE"
restore_runtime() {
  git show "HEAD:$RT" > "$RT"
  [ "$(hash_of "$RT")" = "$PIN_RT" ] || { echo "FATAL: $RT not restored to the HEAD hash"; exit 1; }
}
trap 'restore_runtime' EXIT
trap 'exit 130' INT TERM
patch() { # file, old, new - byte exact, aborts unless the target occurs exactly once
  python - "$1" "$2" "$3" <<'PY'
import sys
path, old, new = sys.argv[1], sys.argv[2].encode(), sys.argv[3].encode()
data = open(path, 'rb').read()
if data.count(old) != 1:
    sys.exit("PATCH ABORT: %d occurrences in %s" % (data.count(old), path))
open(path, 'wb').write(data.replace(old, new))
PY
}
mutate() { # label, old, new, expected exit, required diagnostic
  echo "== $1"; patch "$RT" "$2" "$3" || exit 1
  run_gate "$1" "$4" "$5"; restore_runtime
}
run_gate() { # label, expected exit, required diagnostic
  local label="$1" expected="$2" diagnostic="$3" out="$EVIDENCE/$label"
  dotnet build Art/Tools/DarkwingResearch/cost/reached_body_aggregate.csproj -c Release >/dev/null 2>&1 \
    || { echo "  BUILD FAIL"; return 1; }
  local code=0
  dotnet run --no-build --project Art/Tools/DarkwingResearch/cost/reached_body_aggregate.csproj -c Release \
    -- ground00 "$BANKED" "$out.csv" "$BOUNDARY" > "$out.txt" 2>&1 || code=$?
  echo "  DETECTION exit=$code verdict=$(grep -oE 'ground00 gate (PASS|FAIL)' "$out.txt" | tail -1 || true)"
  grep -E 'ground00 gate:|first CAFC refusal|route polls the clock' "$out.txt" | sed 's/^/  ASSERTION   /' || true
  if [ "$code" -ne "$expected" ] || ! grep -Fq "$diagnostic" "$out.txt"; then
    echo "  PROOF FAIL: $1 expected exit=$expected and diagnostic '$diagnostic'"
    return 1
  fi
  return 0
}

echo "### 0 baseline: pristine runtime must PASS the gate"
restore_runtime; run_gate baseline_pristine 0 'ground00 gate PASS'

echo "### 1 mutants of the guard, the tail and the owners"
mutate mut1_oldBlanketGuard '                bool down = (held & 0x0F) == 0x04 && (held & 3) == 0;
                if ((pressed & 0x80) != 0 || ((held & 0x0C) != 0 && !down))' '                bool down = false;
                if ((held & 12) != 0 || (pressed & 0x80) != 0)' 1 'route=0/5'
mutate mut2_tailMissingJump 'cycles = dispatch + 3 + 2 + 6 + 20 + 2 + 3 + 2 + 4 + 6;' 'cycles = dispatch + 2 + 2 + 6 + 20 + 2 + 3 + 2 + 4 + 6;' 1 'routePollsPricedByClock=0/1'
mutate mut3_branchAlwaysTaken 'ScrollBranch(0xCBE1, 0xCBE6, !down)' 'ScrollBranch(0xCBE1, 0xCBE6, true)' 1 'route=0/5'
mutate mut4_upAccepted '((held & 0x0C) != 0 && !down))' '((held & 0x04) != 0 && !down))' 1 'synthetic=50/55'
mutate mut5_shotAirPoseGuardDropped 'if (Objects.Slots[0].AnimNumber is 0x0E or 0x0F) throw new NotSupportedException("CDD5 ground shot-air pose");' '' 1 'guards=10/20'
mutate mut6_inactiveOwnerDropped 'if (Objects == null || !Objects.Slots[0].Active) throw new NotSupportedException("CAFC inactive player");
                if ((pressed & 0x80)' 'if ((pressed & 0x80)' 1 'guards=15/20'

echo "### 2 no side effect: probe-free cold replay vs the probed gate run"
restore_runtime
dotnet build Art/Tools/DarkwingResearch/cost/reached_body_aggregate.csproj -c Release >/dev/null 2>&1 || exit 1
dotnet run --no-build --project Art/Tools/DarkwingResearch/cost/reached_body_aggregate.csproj -c Release \
  -- clock native 6420 "$EVIDENCE/clock_probe_free.csv" > "$EVIDENCE/clock_probe_free.txt" 2>&1 || exit 1
grep -oE "overrides prefix=[0-9]+ audio=[0-9]+ publication=[0-9]+" "$EVIDENCE/clock_probe_free.txt" | sed 's/^/  OVERRIDES   /'
python - "$EVIDENCE/baseline_pristine.csv" "$EVIDENCE/clock_probe_free.csv" "$BOUNDARY" <<'PY' | tee -a "$EVIDENCE/no_side_effect.txt"
import csv, sys
gate = {int(r['poll']): r for r in csv.DictReader(open(sys.argv[1])) if r['kind'] == 'route-measured'}
clock = {int(r['poll']): r for r in csv.DictReader(open(sys.argv[2]))}
refused = min(int(r['unsupportedPoll']) for r in clock.values() if int(r['unsupportedPoll'] or -1) > 0)
bad = []
for poll, row in gate.items():
    native = int(clock[poll]['prefixNative']) if poll in clock else None
    if native is None:
        bad.append('poll %d missing from the probe-free replay' % poll)
    elif int(row['clockPrefix']) != native:
        bad.append('poll %d gate %s vs probe-free %d' % (poll, row['clockPrefix'], native))
print('  NO SIDE EFFECT priced route polls: %d, first refusal probe-free: %d, gate: %s, mismatches: %d %s'
      % (len(gate), refused, sys.argv[3], len(bad), '; '.join(bad)))
sys.exit(1 if bad or refused != int(sys.argv[3]) or not gate else 0)
PY

echo "### 3 integrity"
restore_runtime
echo "  runtime $RT $(hash_of "$RT")"
echo "  gate    $AGG $(hash_of "$AGG")"
echo "  banked  $BANKED $(hash_of "$BANKED")"
[ -z "$(git status --porcelain -- Art/Unity/CodeZero/)" ] && echo "  runtime clean" || { echo "  FATAL: runtime differs from HEAD"; exit 1; }
echo "ground00 mutation proof complete; evidence under $EVIDENCE"
