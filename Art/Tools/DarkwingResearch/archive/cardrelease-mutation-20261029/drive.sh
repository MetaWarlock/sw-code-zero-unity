#!/bin/bash
# Ad-hoc driver for the ZERO-52 mutation proof. Canonical gate: Art/Tools/DarkwingResearch/cost/
# reached_body_aggregate.cs mode `cardrelease`; the exact byte patches are recorded in FACTS ZERO-52.
cd "D:/Art/SW Code Zero Unity Builds/Mission3-Liquidator" || exit 1
AGG=Art/Tools/DarkwingResearch/cost/reached_body_aggregate.cs
M=Art/Temp/ZERO-52/mutants

patch() { # file, old, new  (bytes-exact, aborts unless the target occurs exactly once)
  python - "$1" "$2" "$3" <<'PY'
import sys
path, old, new = sys.argv[1], sys.argv[2].encode(), sys.argv[3].encode()
data = open(path, 'rb').read()
if data.count(old) != 1:
    sys.exit("PATCH ABORT: %d occurrences in %s" % (data.count(old), path))
open(path, 'wb').write(data.replace(old, new))
PY
}

restore() { cp "$M/$1_pristine.cs" "$2"; }   # $1 = cardtiming|missionclock, $2 = runtime path

run() { # gate(old|new), label
  if [ "$1" = old ]; then cp "$M/aggregate_old.cs" "$AGG"; else cp "$M/aggregate_new.cs" "$AGG"; fi
  rm -f "$M/$2_$1.csv"
  dotnet build Art/Tools/DarkwingResearch/cost/reached_body_aggregate.csproj -c Release >/dev/null 2>&1
  if [ $? -ne 0 ]; then echo "$2 $1: BUILD FAIL"; return 1; fi
  dotnet run --no-build --project Art/Tools/DarkwingResearch/cost/reached_body_aggregate.csproj -c Release \
    -- cardrelease "$M/$2_$1.csv" 6420 2110 2125 > "$M/$2_$1.txt" 2>&1
  echo "== $2 / $1 gate: exit=$? =="
  grep -E "release-frame negatives|valid-context assembler|CONTROL FAIL|negativeRefusals=|gate (PASS|FAIL)" "$M/$2_$1.txt" | head -14
}
