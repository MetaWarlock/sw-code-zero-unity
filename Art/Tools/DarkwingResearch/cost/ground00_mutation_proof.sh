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
#   bash Art/Tools/DarkwingResearch/cost/ground00_mutation_proof.sh [EVIDENCE_DIR] [BANKED_JSON] [EXPECTED_BOUNDARY] [REFERENCE_CLOCK_CSV] [MODE]
# MODE (ZERO-60) selects which reached-price gate is proved: ground00 (default, unchanged) or
# airborne0b. Both slices are priced by the same runtime file, and both already had a mutation driver
# pattern here, so the driver was extended instead of cloning a second one; the default mode keeps the
# accepted ZERO-54/ZERO-57 invocation and evidence exactly as it was.
# Defaults for MODE=ground00: EVIDENCE_DIR=Art/Temp/ZERO-54/mutation-proof, BANKED_JSON=Art/Temp/ZERO-54/ground00_source_1924_6420.json,
# EXPECTED_BOUNDARY=2543 (the first CAFC refusal measured after the new branch), REFERENCE_CLOCK_CSV=
# Art/Temp/ZERO-52/clock_native_after.csv (accepted evidence: every probe-free row before the first priced
# route poll must equal it byte for byte, so "nothing changed before the boundary" is measured, not claimed).
# Defaults for MODE=airborne0b: EVIDENCE_DIR=Art/Temp/ZERO-60/mutation-proof, BANKED_JSON=
# Art/Temp/ZERO-60/inputs/airborne0b_2538_2553_verified.json, EXPECTED_BOUNDARY=2666, REFERENCE_CLOCK_CSV=
# Art/Temp/ZERO-54/clock_native_after.csv.
# The banked JSON must come from cost/state09_source_price.py --ground00 or --airborne0b; the gate
# refuses a JSON that does not report its own PASS. Requires .NET 10 and the preserved city session input.
# Must not run while any runtime source is edited, and never at the same time as
# cardrelease_mutation_proof.sh, which patches runtime sources too. It also owns the mutable build
# directory: no other dotnet build/export of THIS tree may overlap (a baseline copy in its own tree
# is fine), because MSBuild contention can fail one mutant build - measured on the first ZERO-54 run,
# where the sixth mutant hit exactly that race and reported BUILD FAIL, not a gate result.
set -euo pipefail
ROOT="$(cd "$(dirname "$0")/../../../.." && pwd)"
cd "$ROOT" || exit 1
[ -f AGENTS.md ] && [ -d Art/Unity/CodeZero/Darkwing ] || { echo "not the assigned project root: $ROOT"; exit 1; }

MODE="${5:-ground00}"
case "$MODE" in
  ground00)
    EVIDENCE="${1:-Art/Temp/ZERO-54/mutation-proof}"
    BANKED="${2:-Art/Temp/ZERO-54/ground00_source_1924_6420.json}"
    BOUNDARY="${3:-2543}"
    REF_CLOCK="${4:-Art/Temp/ZERO-52/clock_native_after.csv}"
    ROUTE_KIND=route-measured; POLLS_KEY=cc44_polls ;;
  airborne0b)
    EVIDENCE="${1:-Art/Temp/ZERO-60/mutation-proof}"
    BANKED="${2:-Art/Temp/ZERO-60/inputs/airborne0b_2538_2553_verified.json}"
    BOUNDARY="${3:-2666}"
    REF_CLOCK="${4:-Art/Temp/ZERO-54/clock_native_after.csv}"
    ROUTE_KIND=routeRight; POLLS_KEY=right_polls ;;
  *) echo "unknown MODE $MODE: supported are ground00 and airborne0b"; exit 1 ;;
esac
AGG=Art/Tools/DarkwingResearch/cost/reached_body_aggregate.cs
RT=Art/Unity/CodeZero/Darkwing/Core/PlayerCore.ReachedDispatchTiming.cs
GATE=Art/Tools/DarkwingResearch/cost/ground00_mutation_proof.sh
[ -f "$BANKED" ] || { echo "missing banked pass $BANKED; run cost/state09_source_price.py once for this mode first"; exit 1; }
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
  local label="$1" expected="$2" diagnostic="$3" out="$EVIDENCE/$1"
  dotnet build Art/Tools/DarkwingResearch/cost/reached_body_aggregate.csproj -c Release >/dev/null 2>&1 \
    || { echo "  BUILD FAIL"; return 1; }
  local code=0
  dotnet run --no-build --project Art/Tools/DarkwingResearch/cost/reached_body_aggregate.csproj -c Release \
    -- "$MODE" "$BANKED" "$out.csv" "$BOUNDARY" > "$out.txt" 2>&1 || code=$?
  echo "  DETECTION exit=$code verdict=$(grep -oE "$MODE gate (PASS|FAIL)" "$out.txt" | tail -1 || true)"
  grep -E "$MODE gate:|first CAFC refusal|route polls the clock" "$out.txt" | sed 's/^/  ASSERTION   /' || true
  if [ "$code" -ne "$expected" ] || ! grep -Fq "$diagnostic" "$out.txt"; then
    echo "  PROOF FAIL: $1 expected exit=$expected and diagnostic '$diagnostic'"
    return 1
  fi
  return 0
}

echo "### 0 baseline: pristine runtime must PASS the gate"
restore_runtime; run_gate baseline_pristine 0 "$MODE gate PASS"

echo "### 1 mutants of the guard, the tail and the owners"
if [ "$MODE" = ground00 ]; then
mutate mut1_oldBlanketGuard '                bool down = (held & 0x0F) == 0x04 && (held & 3) == 0;
                if ((pressed & 0x80) != 0 || ((held & 0x0C) != 0 && !down))' '                bool down = false;
                if ((held & 12) != 0 || (pressed & 0x80) != 0)' 1 'route=0/5'
mutate mut2_tailMissingJump 'cycles = dispatch + 3 + 2 + 6 + 20 + 2 + 3 + 2 + 4 + 6;' 'cycles = dispatch + 2 + 2 + 6 + 20 + 2 + 3 + 2 + 4 + 6;' 1 'routePollsPricedByClock=0/1'
mutate mut3_branchAlwaysTaken 'ScrollBranch(0xCBE1, 0xCBE6, !down)' 'ScrollBranch(0xCBE1, 0xCBE6, true)' 1 'route=0/5'
mutate mut4_upAccepted '((held & 0x0C) != 0 && !down))' '((held & 0x04) != 0 && !down))' 1 'synthetic=50/55'
mutate mut5_shotAirPoseGuardDropped 'if (Objects.Slots[0].AnimNumber is 0x0E or 0x0F) throw new NotSupportedException("CDD5 ground shot-air pose");' '' 1 'guards=10/20'
mutate mut6_inactiveOwnerDropped 'if (Objects == null || !Objects.Slots[0].Active) throw new NotSupportedException("CAFC inactive player");
                // $CBDF compares' 'if (Objects == null) throw new NotSupportedException("CAFC inactive player");
                // $CBDF compares' 1 'guards=15/20'
# ZERO-57: the remaining listing bounds of the guard, each through the column that must name it.
mutate mut7_downWithDirection 'bool down = (held & 0x0F) == 0x04 && (held & 3) == 0;' 'bool down = (held & 0x0C) == 0x04;' 1 'synthetic=45/55'
mutate mut8_freshAPassed 'if ((pressed & 0x80) != 0 || ((held & 0x0C) != 0 && !down))' 'if (((held & 0x0C) != 0 && !down))' 1 'synthetic=50/55'
mutate mut9_upDownAccepted 'if ((pressed & 0x80) != 0 || ((held & 0x0C) != 0 && !down))' 'if ((pressed & 0x80) != 0 || ((held & 0x0C) != 0 && !down && (held & 0x0F) != 0x0C))' 1 'synthetic=50/55'
mutate mut10_bReadBeforeCrouch '                    if (down)' '                    if (down && (pressed & 0x40) == 0)' 1 'synthetic=50/55'
mutate mut11_crouchNeedsQueueOwner '                    if (down)
                    {' '                    if (down)
                    {
                        if (!audioQueueAccepts.HasValue) throw new NotSupportedException("F850 native audio queue owner unavailable");' 1 'crouchNeedsNoQueueOwner=0/5'
mutate mut12_stateGuardDropped '                if (State != 0) throw new NotSupportedException("CB8B state outside00 slice");
                if (Objects == null || !Objects.Slots[0].Active) throw new NotSupportedException("CAFC inactive player");
                // $CBDF' '                if (Objects == null || !Objects.Slots[0].Active) throw new NotSupportedException("CAFC inactive player");
                // $CBDF' 1 'guards=15/20'
else
# ZERO-60: the airborne $0B right steering, its $0E veto, the $D79E tail and the side-probe bound.
mutate mut1_oldRightRefusal '                        dispatch += 2 + 3 + 2 + ScrollBranch(0xD780, 0xD78D, CheckpointId != 0x0E);' '                        throw new NotSupportedException("D77C right steering");
                        dispatch += 2 + 3 + 2 + ScrollBranch(0xD780, 0xD78D, CheckpointId != 0x0E);' 1 'routeRight=0/10'
mutate mut2_f200BodyMissing 'horizontal = ExpandedHorizontalSimple(X, nextY, true, XVelFrac, XVelInt, 0, true);' 'horizontal = 0;' 1 'routeRight=0/10'
mutate mut3_d77aPricedTaken 'dispatch += 2 + 3 + 2 + ScrollBranch(0xD780, 0xD78D, CheckpointId != 0x0E);' 'dispatch += 3 + 3 + 2 + ScrollBranch(0xD780, 0xD78D, CheckpointId != 0x0E);' 1 'routeRight=0/10'
mutate mut4_jmpD792Missing 'dispatch += 2 + 6 + 3;                           // $D78D LDA #$00, $D78F JSR $F200, $D792 JMP $D79A' 'dispatch += 2 + 6;                               // $D78D LDA #$00, $D78F JSR $F200, $D792 JMP $D79A' 1 'routeRight=0/10'
mutate mut5_tailRtsMissing 'if ((pressed & 0x40) != 0) throw new NotSupportedException("D79E airborne shot");
                dispatch += 3 + 2 + ScrollBranch(0xD79E, 0xD7B6, true) + 6;' 'if ((pressed & 0x40) != 0) throw new NotSupportedException("D79E airborne shot");
                dispatch += 3 + 2 + ScrollBranch(0xD79E, 0xD7B6, true);' 1 'routeRight=0/10'
mutate mut6_leftUsesRightBody '                    if (direction == 2)' '                    if (direction == 3)' 1 'synthetic=60/80'
mutate mut7_rightLeftGoesLeft 'int direction = held & 3;
                dispatch += 2 + 3 + 2' 'int direction = (held & 3) == 3 ? 2 : (held & 3);
                dispatch += 2 + 3 + 2' 1 'synthetic=70/80'
mutate mut8_vetoIgnoresCheckpoint 'if (CheckpointId == 0x0E)
                        {
                            dispatch += 4 + 2 + ScrollBranch(0xD787, 0xD7B6, Flags == 0x50);' 'if (Flags == 0x50 || Flags == 0x10)
                        {
                            dispatch += 4 + 2 + ScrollBranch(0xD787, 0xD7B6, Flags == 0x50);' 1 'synthetic=60/80'
mutate mut9_vetoIgnoresFlags 'dispatch += 4 + 2 + ScrollBranch(0xD787, 0xD7B6, Flags == 0x50);
                            if (Flags == 0x50) { cycles = dispatch + vertical + probeCost + pair + 6; return true; }' 'dispatch += 4 + 2 + ScrollBranch(0xD787, 0xD7B6, true);
                            if (true) { cycles = dispatch + vertical + probeCost + pair + 6; return true; }' 1 'synthetic=70/80'
mutate mut10_vetoSecondFlagAsFirst 'dispatch += 2 + ScrollBranch(0xD78B, 0xD7B6, Flags == 0x10);
                            if (Flags == 0x10) { cycles = dispatch + vertical + probeCost + pair + 6; return true; }' 'dispatch += ScrollBranch(0xD78B, 0xD7B6, Flags == 0x10);
                            if (Flags == 0x10) { cycles = dispatch + vertical + probeCost + pair + 4; return true; }' 1 'synthetic=70/80'
mutate mut11_freshBCharged 'if ((pressed & 0x40) != 0) throw new NotSupportedException("D79E airborne shot");
                dispatch += 3 + 2' 'dispatch += 3 + 2' 1 'freshB=10/40'
mutate mut12_solidSideCharged 'horizontal = ExpandedHorizontalSimple(X, nextY, true, XVelFrac, XVelInt, 0, true);' 'horizontal = ExpandedHorizontalSimple(X, nextY, true, XVelFrac, XVelInt, 0, false);' 1 'geometry=5/8'
mutate mut13_stateGuardDropped 'if (State != 0x0B) throw new NotSupportedException("D626 state outside 0B slice");' '' 1 'guards=30/40'
mutate mut14_steeringNeedsOwner 'dispatch += 2 + 6 + 3;                           // $D78D LDA #$00, $D78F JSR $F200, $D792 JMP $D79A' 'dispatch += 2 + 6 + 3;                           // $D78D LDA #$00, $D78F JSR $F200, $D792 JMP $D79A
                        if (!audioQueueAccepts.HasValue) throw new NotSupportedException("F850 native audio queue owner unavailable");' 1 'rightNeedsNoQueueOwner=0/10'
mutate mut15_landingOwnerDropped 'if (!audioQueueAccepts.HasValue) throw new NotSupportedException("F850 native audio queue owner unavailable");
                        dispatch += 2 + 6 + ExpandedQueueCycles(audioQueueAccepts.Value)' 'dispatch += 2 + 6 + ExpandedQueueCycles(audioQueueAccepts ?? true)' 1 'landingNeedsOwner=0/1'
fi

echo "### 2 no side effect: probe-free cold replay vs the probed gate run"
restore_runtime
dotnet build Art/Tools/DarkwingResearch/cost/reached_body_aggregate.csproj -c Release >/dev/null 2>&1 || exit 1
dotnet run --no-build --project Art/Tools/DarkwingResearch/cost/reached_body_aggregate.csproj -c Release \
  -- clock native 6420 "$EVIDENCE/clock_probe_free.csv" > "$EVIDENCE/clock_probe_free.txt" 2>&1 || exit 1
grep -oE "overrides prefix=[0-9]+ audio=[0-9]+ publication=[0-9]+" "$EVIDENCE/clock_probe_free.txt" | sed 's/^/  OVERRIDES   /'
python - "$EVIDENCE/baseline_pristine.csv" "$EVIDENCE/clock_probe_free.csv" "$BOUNDARY" "$ROUTE_KIND" <<'PY' | tee -a "$EVIDENCE/no_side_effect.txt"
import csv, sys
gate = {int(r['poll']): r for r in csv.DictReader(open(sys.argv[1])) if r['kind'] == sys.argv[4]}
clock = {int(r['poll']): r for r in csv.DictReader(open(sys.argv[2]))}
refused = min(int(r['unsupportedPoll']) for r in clock.values() if int(r['unsupportedPoll'] or -1) > 0)
bad = []
# Only route polls the clock actually priced carry a body-prefix claim; the blocked ones compare -1
# with -1 and are reported separately so the count is never read as clock coverage (ZERO-57).
priced = sorted(p for p, r in gate.items() if int(r['clockPrefix']) >= 0)
for poll, row in gate.items():
    native = int(clock[poll]['prefixNative']) if poll in clock else None
    if native is None:
        bad.append('poll %d missing from the probe-free replay' % poll)
    elif int(row['clockPrefix']) != native:
        bad.append('poll %d gate %s vs probe-free %d' % (poll, row['clockPrefix'], native))
print('  NO SIDE EFFECT route polls: %d, clock-priced: %d %s, first refusal probe-free: %d, gate: %s, mismatches: %d %s'
      % (len(gate), len(priced), priced, refused, sys.argv[3], len(bad), '; '.join(bad)))
sys.exit(1 if bad or refused != int(sys.argv[3]) or not priced else 0)
PY

echo "### 3 unchanged before the boundary: probe-free rows vs the accepted reference clock CSV"
[ -f "$REF_CLOCK" ] || { echo "missing reference clock CSV $REF_CLOCK"; exit 1; }
FIRST_ROUTE="$(python -c "import json,sys;print(json.load(open(sys.argv[1],encoding='utf-8'))['summary'][sys.argv[2]][0])" "$BANKED" "$POLLS_KEY")"
python - "$EVIDENCE/clock_probe_free.csv" "$REF_CLOCK" "$FIRST_ROUTE" <<'PY' | tee -a "$EVIDENCE/unchanged_before.txt"
import csv, sys
new = list(csv.DictReader(open(sys.argv[1], encoding='utf-8')))
ref = list(csv.DictReader(open(sys.argv[2], encoding='utf-8')))
if [c for c in new[0]] != [c for c in ref[0]]:
    sys.exit('column schemas differ: %s vs %s' % (list(new[0]), list(ref[0])))
lim, byPoll = int(sys.argv[3]), {r['poll']: r for r in ref}
rows = [r for r in new if int(r['poll']) < lim]
below = [r['poll'] for r in ref if int(r['poll']) < lim]
missing = [r['poll'] for r in rows if r['poll'] not in byPoll]
differing = [r['poll'] for r in rows if r['poll'] in byPoll and ','.join(r.values()) != ','.join(byPoll[r['poll']].values())]
print('  UNCHANGED BEFORE poll %d: rows=%d reference=%d missing=%d differing=%d %s'
      % (lim, len(rows), len(below), len(missing), len(differing), (missing + differing)[:8]))
sys.exit(0 if rows and len(rows) == len(below) and not missing and not differing else 1)
PY

echo "### 4 integrity"
restore_runtime
echo "  runtime $RT $(hash_of "$RT")"
echo "  gate    $AGG $(hash_of "$AGG")"
echo "  banked  $BANKED $(hash_of "$BANKED")"
[ -z "$(git status --porcelain -- Art/Unity/CodeZero/)" ] && echo "  runtime clean" || { echo "  FATAL: runtime differs from HEAD"; exit 1; }
echo "ground00 mutation proof complete; evidence under $EVIDENCE"
