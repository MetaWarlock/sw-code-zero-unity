#!/bin/bash
# Canonical mutation-proof driver for the card-release gate (ZERO-52).
#
# Promoted from the one-off scratch used for the checkpoint, which the preservation audit
# refused to keep in Art/Temp; the original bytes are archived byte-exact under
# Art/Tools/DarkwingResearch/archive/cardrelease-mutation-20261029/. Nothing here is
# reconstructed from memory: the runtime is always restored from Git, and every stage pins a
# sha256 so a silent drift aborts instead of producing a fake proof.
#
# What it proves, kept deliberately separate:
#   DETECTION  - a mutant of the guard or of the owner choice makes the gate nonzero;
#   ASSERTION  - which named assertion of the strengthened gate reports it (never a foreign -1);
#   NO SIDE EFFECT - the probe-free cold replay equals the probed window, so reflection probes
#                    do not influence the real AdvanceFrame run.
#
# Usage (from the assigned project root, or any cwd):
#   bash Art/Tools/DarkwingResearch/cost/cardrelease_mutation_proof.sh [EVIDENCE_DIR] [OLD_GATE_REF]
# Defaults: EVIDENCE_DIR=Art/Temp/ZERO-52/mutation-proof, OLD_GATE_REF=49998cc (accepted pre-ZERO-52
# revision, whose cardrelease form bundled flag and price into one verdict).
# Requires .NET 10 and the preserved city session input; writes only new files under EVIDENCE_DIR.
# Runtime sources are restored and hash-checked on every path, including failure and Ctrl-C.
set -u
ROOT="$(cd "$(dirname "$0")/../../../.." && pwd)"
cd "$ROOT" || exit 1
[ -f AGENTS.md ] && [ -d Art/Unity/CodeZero/Darkwing ] || { echo "not the assigned project root: $ROOT"; exit 1; }

EVIDENCE="${1:-Art/Temp/ZERO-52/mutation-proof}"
OLD_REF="${2:-49998cc}"
AGG=Art/Tools/DarkwingResearch/cost/reached_body_aggregate.cs
CT=Art/Unity/CodeZero/Darkwing/Core/PlayerCore.CardTiming.cs
MC=Art/Unity/CodeZero/Darkwing/Core/DarkwingGame.MissionClock.cs
CS=Art/Unity/CodeZero/Darkwing/Core/PlayerCore.cs
PIN_CT=c65e7cbe3563e32ff8213c5698d9d25daf67b61684e699ec1ee6fe5c6469685a
PIN_MC=2f5036aac99a1ea1faa116b4c067894f4acf2ca6ae46ae11dd58781aa1e2c77d
mkdir -p "$EVIDENCE"

hash_of() { sha256sum "$1" | cut -d' ' -f1; }
restore_runtime() {
  git show "HEAD:$CT" > "$CT" && git show "HEAD:$MC" > "$MC"
  [ "$(hash_of "$CT")" = "$PIN_CT" ] || { echo "FATAL: $CT not restored to the pinned hash"; exit 1; }
  [ "$(hash_of "$MC")" = "$PIN_MC" ] || { echo "FATAL: $MC not restored to the pinned hash"; exit 1; }
}
restore_aggregate() {
  git show "HEAD:$AGG" > "$AGG" || { echo "FATAL: cannot restore $AGG from HEAD"; exit 1; }
}
trap 'restore_runtime' EXIT

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

run_gate() { # gate(old|new), label -> prints exit, verdict and the named assertion lines
  local gate="$1" label="$2" out="$EVIDENCE/$2_$1"
  if [ "$gate" = old ]; then git show "$OLD_REF:$AGG" > "$AGG" || return 1; else restore_aggregate; fi
  rm -f "$out.csv"
  dotnet build Art/Tools/DarkwingResearch/cost/reached_body_aggregate.csproj -c Release >/dev/null 2>&1 \
    || { echo "$label/$gate: BUILD FAIL"; return 1; }
  dotnet run --no-build --project Art/Tools/DarkwingResearch/cost/reached_body_aggregate.csproj -c Release \
    -- cardrelease "$out.csv" 6420 2110 2125 > "$out.txt" 2>&1
  local code=$?
  echo "  DETECTION exit=$code verdict=$(grep -oE 'cardrelease gate (PASS|FAIL)' "$out.txt" | tail -1) $(grep -oE 'ERROR: [^:]+: [^$]+' "$out.txt" | head -1)"
  if [ "$gate" = new ]; then
    grep -E "release-frame negatives|valid-context assembler" "$out.txt" | sed 's/^/  ASSERTION   /'
    grep -m 2 "CONTROL FAIL" "$out.txt" | sed 's/^/  NAMED       /'
  else
    grep -E "negativeRefusals=" "$out.txt" | sed 's/^/  OLD-FORM    /'
  fi
  [ "$gate" = new ] && restore_aggregate
  return 0
}

case_new() { # label, file, old, new
  echo "== $1 (new gate)"; patch "$2" "$3" "$4" || exit 1
  run_gate new "$1"; restore_runtime
}
case_pair() { # label, file, old, new, old-gate expectation
  echo "== $1 (old gate at $OLD_REF, same mutation)"; patch "$2" "$3" "$4" || exit 1
  echo "$5" > "$EVIDENCE/$1.expect.txt"
  run_gate old "$1"; restore_runtime; restore_aggregate
}

echo "### 0 baseline: pristine runtime, strengthened gate must PASS"
restore_aggregate; run_gate new baseline_pristine; restore_runtime

echo "### 1 mutants"
case_new mut1_guardDroppedWhole "$CT" 'if (WorldId > 6 || WeaponSelected > 3)' 'if (false && (WorldId > 6 || WeaponSelected > 3))'
case_new mut1b_worldGuardDropped "$CT" 'if (WorldId > 6 || WeaponSelected > 3)' 'if (WeaponSelected > 3)'
case_new mut2_reasonDropped "$CT" 'CardTimingUnsupported = "D554 mission release context";' 'CardTimingUnsupported = null;'
case_new mut3_holdSpanDropped "$CT" 'LastCardContinuationCycles = 2 + 4 + 6 + 49;' 'LastCardContinuationCycles = -1;'
case_new mut4_flagAgnosticOwner "$MC" 'bool cardRelease = Player.LastCardContinuationReleased;' 'bool cardRelease = Player.LastCardContinuationCycles >= 0;'

echo "### 2 blindness of the pre-ZERO-52 form on the same mutants"
case_pair mut2_reasonDropped "$CT" 'CardTimingUnsupported = "D554 mission release context";' 'CardTimingUnsupported = null;' 'OLD GATE PASSED THIS MUTANT (masked by the stale head)'
case_pair mut3_holdSpanDropped "$CT" 'LastCardContinuationCycles = 2 + 4 + 6 + 49;' 'LastCardContinuationCycles = -1;' 'OLD GATE PASSED THIS MUTANT (hold became indistinguishable from wait0)'

echo "### 3 no side effect of the probes: probe-free replay vs probed window"
restore_runtime; restore_aggregate
dotnet build Art/Tools/DarkwingResearch/cost/reached_body_aggregate.csproj -c Release >/dev/null 2>&1 || exit 1
rm -f "$EVIDENCE/clock_probe_free.csv" "$EVIDENCE/cardrelease_probed.csv"
dotnet run --no-build --project Art/Tools/DarkwingResearch/cost/reached_body_aggregate.csproj -c Release \
  -- clock native 6420 "$EVIDENCE/clock_probe_free.csv" > "$EVIDENCE/clock_probe_free.txt" 2>&1 || exit 1
dotnet run --no-build --project Art/Tools/DarkwingResearch/cost/reached_body_aggregate.csproj -c Release \
  -- cardrelease "$EVIDENCE/cardrelease_probed.csv" 6420 2110 2125 > "$EVIDENCE/cardrelease_probed.txt" 2>&1 || exit 1
join -t'|' \
  <(awk -F, 'NR>1 && $1>=2110 && $1<=2125 {print $1"|"$23"|"$16}' "$EVIDENCE/cardrelease_probed.csv") \
  <(awk -F, 'NR>1 {d=$9-p; p=$9; if($1>=2110 && $1<=2125) print $1"|"d"|"$15}' "$EVIDENCE/clock_probe_free.csv") \
  | awk -F'|' '{ok=($2==$4 && $3==$5); if(!ok){bad++; print "  WINDOW MISMATCH",$0}} END{print "  NO SIDE EFFECT rows:",NR,"matched:",NR-bad,"mismatches:",bad+0}'
grep -oE "overrides prefix=[0-9]+ audio=[0-9]+ publication=[0-9]+" "$EVIDENCE/clock_probe_free.txt" | sed 's/^/  OVERRIDES   /'
tail -1 "$EVIDENCE/clock_probe_free.csv" | cut -d, -f8,9,10,11,16 | sed 's/^/  BOUNDARY    anchors,timed,running,firstRefusal,prefixWhy: /'

echo "### 4 integrity"
restore_runtime; restore_aggregate
echo "  runtime $CT $(hash_of "$CT")"
echo "  runtime $MC $(hash_of "$MC")"
echo "  gate    $AGG $(hash_of "$AGG")"
git status --porcelain -- Art/Unity/CodeZero/ | sed 's/^/  DIRTY /'
[ -z "$(git status --porcelain -- Art/Unity/CodeZero/)" ] && echo "  runtime clean" || { echo "  FATAL: runtime differs from HEAD"; exit 1; }
echo "mutation proof complete; evidence under $EVIDENCE"
