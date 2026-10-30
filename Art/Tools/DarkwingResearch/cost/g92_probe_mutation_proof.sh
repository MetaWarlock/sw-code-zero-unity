#!/bin/bash
set -euo pipefail
ROOT="$(cd "$(dirname "$0")/../../../.." && pwd)"
cd "$ROOT" || exit 1
[ -f AGENTS.md ] && [ -d Art/Unity/CodeZero/Darkwing ] || { echo "not the assigned project root: $ROOT"; exit 1; }

EVIDENCE="${1:-Art/Temp/ZERO-68/mutation-proof}"
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
RT_PRICE=Art/Unity/CodeZero/Darkwing/Core/PlayerCore.ReachedDispatchTiming.cs
RT_MAIN=Art/Unity/CodeZero/Darkwing/Core/PlayerCore.cs
GATE=Art/AgentScripts/PhysCompare.cs
[ -f "$GATE" ] || { echo "missing gate $GATE"; exit 1; }
git diff --quiet HEAD -- "$RT_PRICE" "$RT_MAIN" || { echo 'Refusing to run over changed runtime'; exit 1; }
mkdir -p "$EVIDENCE"
hash_of() { sha256sum "$1" | cut -d' ' -f1; }
PIN_PRICE="$(hash_of "$RT_PRICE")"
PIN_MAIN="$(hash_of "$RT_MAIN")"
restore_runtime() {
  git show "HEAD:$RT_PRICE" > "$RT_PRICE"
  git show "HEAD:$RT_MAIN" > "$RT_MAIN"
  [ "$(hash_of "$RT_PRICE")" = "$PIN_PRICE" ] || { echo "FATAL: $RT_PRICE not restored"; exit 1; }
  [ "$(hash_of "$RT_MAIN")" = "$PIN_MAIN" ] || { echo "FATAL: $RT_MAIN not restored"; exit 1; }
}
trap 'restore_runtime' EXIT
trap 'exit 130' INT TERM
patch() {
  python - "$1" "$2" "$3" <<'PY'
import sys
path, old, new = sys.argv[1], sys.argv[2].encode(), sys.argv[3].encode()
data = open(path, 'rb').read()
if data.count(old) != 1:
    sys.exit("PATCH ABORT: %d occurrences in %s" % (data.count(old), path))
open(path, 'wb').write(data.replace(old, new))
PY
}
run_g92() {
  local label="$1" expected="$2" diagnostic="${3:-}" out="$EVIDENCE/$1" code=0
  unity status --until-ready --project-path "$ROOT" --format json > "$out.ready.json" || { echo "EDITOR NOT READY: $label"; return 1; }
  unity recompile --project-path "$ROOT" --format json > "$out.recompile.json" || { echo "RECOMPILE FAIL: $label"; return 1; }
  python Art/Tools/DarkwingResearch/run_check.py --entry PhysCompare.G92Exit --out "$out" > "$out.driver.json" 2>&1 || code=$?
  python - "$out" "$code" "$expected" "$diagnostic" <<'PY'
import json
from pathlib import Path
import sys
out, code, expected, diagnostic = Path(sys.argv[1]), int(sys.argv[2]), sys.argv[3], sys.argv[4]
if expected == 'pass':
    returned = (out / 'returned.txt').read_text(encoding='utf-8') if (out / 'returned.txt').is_file() else ''
    if code != 0 or not returned.startswith('PASS G92 '):
        sys.exit('BASELINE FAIL: exit=%d, returned=%r' % (code, returned))
    print('PASS %s: %s' % (out.name, returned))
    raise SystemExit(0)
response = json.loads((out / 'stdout.json').read_text(encoding='utf-8-sig'))
result = (response.get('data') or {}).get('result') or {}
details = result.get('errorDetails') or ''
if code != 2 or not result.get('compileMs') or diagnostic not in details:
    sys.exit('MUTATION PROOF FAIL: exit=%d, expected relevant assertion %r, details=%r' % (code, diagnostic, details))
print('PASS %s: compiled mutant rejected by %s' % (out.name, diagnostic))
PY
}

restore_runtime
run_g92 baseline_current pass

git show "12901dd:$RT_PRICE" > "$RT_PRICE"
run_g92 baseline_pre_zero60 pass
restore_runtime

OLD='                PushUpAndLand(lift);                                // $D709 JSR $D7D8, $50 = 0
                return false;'
for probe in 'ProbeHook();' 'ProbeSide(true);'; do
  label="mutant_${probe%%(*}"
  patch "$RT_MAIN" "$OLD" "                PushUpAndLand(lift);                                // \$D709 JSR \$D7D8, \$50 = 0
                $probe
                return false;"
  run_g92 "$label" reject 'G92: physical pass only initial floor probes; no hook/side probe'
  restore_runtime
done

restore_runtime
git diff --quiet HEAD -- "$RT_PRICE" "$RT_MAIN"
printf 'mutation proof complete; runtime restored: price=%s main=%s\n' "$PIN_PRICE" "$PIN_MAIN"
