#!/bin/sh
# ZERO-39 step 2: additive per-slot snapshots of the legacy root data with the existing preserve.py.
PY="C:/Users/Warlock/.cache/codex-runtimes/codex-primary-runtime/dependencies/python/python.exe"
BK="/d/Art/SW Code Zero Unity - Git Backups"
LOGS="/d/Art/SW Code Zero Unity/Art/Temp/zero39"

for slot in Mission2-Wolfduck Mission3-Liquidator; do
  case "$slot" in
    Mission2-Wolfduck) tag=a ;;
    Mission3-Liquidator) tag=b ;;
  esac
  wt="/d/Art/SW Code Zero Unity Builds/$slot"
  echo "=== slot-$tag ($slot) snapshot start $(date -u +%FT%TZ)"
  (cd "$wt" && "$PY" Art/Tools/DarkwingResearch/preserve.py snapshot \
      --include Playthroughs --include Research --include Tools \
      "$BK/20261029-zero39-slot-$tag-legacy-roots") > "$LOGS/slot-$tag-legacy-roots.snapshot.json" 2>&1
  echo "exit=$? snapshot json:"
  cat "$LOGS/slot-$tag-legacy-roots.snapshot.json"
  echo "=== slot-$tag verify start $(date -u +%FT%TZ)"
  (cd "$wt" && "$PY" Art/Tools/DarkwingResearch/preserve.py verify \
      "$BK/20261029-zero39-slot-$tag-legacy-roots") > "$LOGS/slot-$tag-legacy-roots.verify.json" 2>&1
  echo "exit=$? verify json (truncated):"
  head -c 2000 "$LOGS/slot-$tag-legacy-roots.verify.json"
  echo
done
echo "=== step2 done $(date -u +%FT%TZ)"
