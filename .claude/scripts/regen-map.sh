#!/usr/bin/env bash
# Regenerates one map pack and reimports it in Godot.
# Usage: .claude/scripts/regen-map.sh <map-id>
# Reports each step as PASS, FAIL or N/A. Exits non-zero if a step failed.

set -u

ROOT="$(cd "$(dirname "$0")/../.." && pwd)"
# shellcheck source=lib.sh
. "$ROOT/.claude/scripts/lib.sh"

MAP_ID="${1:-}"
if [ -z "$MAP_ID" ] || [ ! -d "$ROOT/maps/$MAP_ID" ]; then
  echo "usage: regen-map.sh <map-id>; known maps: $(ls "$ROOT/maps" | tr '\n' ' ')"
  exit 2
fi

LOGS="$(mktemp -d "${TMPDIR:-/tmp}/regen-map.XXXXXX")"
failed=0

if [ ! -f "$ROOT/maps/$MAP_ID/map.yaml" ]; then
  echo "N/A   mapgen build     maps/$MAP_ID/map.yaml doesn't exist yet"
elif [ ! -f "$ROOT/tools/mapgen/pyproject.toml" ]; then
  echo "N/A   mapgen build     no tools/mapgen/pyproject.toml yet"
elif (cd "$ROOT" && uv run --project tools/mapgen mapgen build "$MAP_ID") >"$LOGS/mapgen.log" 2>&1; then
  echo "PASS  mapgen build     game/maps/$MAP_ID/ (log: $LOGS/mapgen.log)"
else
  echo "FAIL  mapgen build     $(tail -3 "$LOGS/mapgen.log" | tr '\n' ' ') (log: $LOGS/mapgen.log)"
  failed=1
fi

if ! GODOT_BIN="$(find_godot)"; then
  echo "N/A   godot import     godot not on PATH and \$GODOT not set"
elif godot_import "$GODOT_BIN" "$LOGS/godot-import.log"; then
  echo "PASS  godot import     $IMPORT_DETAIL (log: $LOGS/godot-import.log)"
else
  echo "FAIL  godot import     $IMPORT_DETAIL (log: $LOGS/godot-import.log)"
  failed=1
fi

exit "$failed"
