#!/usr/bin/env bash
# Builds the Somero test-area pack for the gdUnit4 tests from the committed fixture
# (tools/mapgen/tests/fixtures/raw/somero-test-area/) into game/tests/packs/somero-test/,
# then runs the headless Godot import. Offline: it never reads or changes
# tools/mapgen/data/raw/ and never contacts Overpass or MML (docs/specs/test-performance.md, ADR 0020).
#
# Usage: .claude/scripts/build-test-pack.sh [--if-stale] [--no-import]
#   --if-stale   build only when the pack is missing or its stamp doesn't match the fixture,
#                the pipeline (tools/mapgen/src, pyproject.toml, uv.lock) and maps/somero/
#   --no-import  skip the Godot import (CI runs its own import step)
# Prints one line: "built ...", "up to date ..." or "FAIL ...". Exits non-zero on failure.

set -u

ROOT="$(cd "$(dirname "$0")/../.." && pwd)"
# shellcheck source=lib.sh
. "$ROOT/.claude/scripts/lib.sh"

MAP_ID="somero"
FIXTURE="tools/mapgen/tests/fixtures/raw/somero-test-area"
PACK="game/tests/packs/somero-test"
STAMP="$ROOT/$PACK/.test-pack-stamp"

if_stale=0
do_import=1
for arg in "$@"; do
  case "$arg" in
    --if-stale) if_stale=1 ;;
    --no-import) do_import=0 ;;
    *) echo "usage: build-test-pack.sh [--if-stale] [--no-import]" >&2; exit 2 ;;
  esac
done

# A hash over the pack's inputs: file paths and contents of the fixture, the pipeline
# source and lock, and the map config.
stamp() {
  (cd "$ROOT" && python3 -I - "$FIXTURE" tools/mapgen/src tools/mapgen/pyproject.toml tools/mapgen/uv.lock "maps/$MAP_ID" <<'EOF'
import hashlib, sys
from pathlib import Path
h = hashlib.sha256()
for arg in sys.argv[1:]:
    p = Path(arg)
    files = [p] if p.is_file() else sorted(f for f in p.rglob("*") if f.is_file() and "__pycache__" not in f.parts)
    for f in files:
        h.update(f.as_posix().encode() + b"\0" + f.read_bytes() + b"\0")
print(h.hexdigest())
EOF
  )
}

want="$(stamp)" || { echo "FAIL  test pack: can't hash the pack inputs"; exit 1; }
if [ "$if_stale" -eq 1 ] && [ -f "$ROOT/$PACK/manifest.json" ] && [ "$(cat "$STAMP" 2>/dev/null)" = "$want" ]; then
  echo "up to date: $PACK (stamp matches the fixture, pipeline and maps/$MAP_ID)"
  exit 0
fi

LOGS="$(mktemp -d "${TMPDIR:-/tmp}/build-test-pack.XXXXXX")"
rm -f "$STAMP"
if ! (cd "$ROOT" && uv run --project tools/mapgen mapgen build "$MAP_ID" --area test --raw "$FIXTURE" --out "$PACK") >"$LOGS/mapgen.log" 2>&1; then
  echo "FAIL  test pack: mapgen build failed: $(tail -3 "$LOGS/mapgen.log" | tr '\n' ' ') (log: $LOGS/mapgen.log)"
  exit 1
fi
if [ "$do_import" -eq 1 ]; then
  if ! GODOT_BIN="$(find_godot)"; then
    echo "FAIL  test pack: godot not on PATH and \$GODOT not set (needed for the import)"
    exit 1
  fi
  if ! godot_import "$GODOT_BIN" "$LOGS/godot-import.log"; then
    echo "FAIL  test pack: Godot import: $IMPORT_DETAIL (log: $LOGS/godot-import.log)"
    exit 1
  fi
fi
echo "$want" >"$STAMP"
echo "built: $PACK from $FIXTURE (logs: $LOGS)"
