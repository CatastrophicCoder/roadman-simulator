#!/usr/bin/env bash
# Runs every check in the definition of done and reports each step as
# PASS, FAIL or N/A (not available yet). Always exits 0; read the summary.
# Logs go to $VERIFY_LOG_DIR (default: a temp directory).

set -u

ROOT="$(cd "$(dirname "$0")/../.." && pwd)"
# shellcheck source=lib.sh
. "$ROOT/.claude/scripts/lib.sh"

LOGS="${VERIFY_LOG_DIR:-$(mktemp -d "${TMPDIR:-/tmp}/verify.XXXXXX")}"
mkdir -p "$LOGS"
SUMMARY=()

report() { # report <status> <step> <detail>
  SUMMARY+=("$(printf '%-5s %-28s %s' "$1" "$2" "$3")")
}

step() { # step <name> <logname> <command...>
  local name="$1" log="$LOGS/$2.log"
  shift 2
  if (cd "$ROOT" && "$@") >"$log" 2>&1; then
    report PASS "$name" "log: $log"
  else
    report FAIL "$name" "$(tail -3 "$log" | tr '\n' ' ') (log: $log)"
  fi
}

SLN="game/RoadmanSimulator.sln"
MAPGEN="tools/mapgen"

# C#
if ! command -v dotnet >/dev/null 2>&1; then
  report N/A "dotnet build" "dotnet not on PATH"
  report N/A "dotnet test" "dotnet not on PATH"
  report N/A "dotnet format" "dotnet not on PATH"
else
  step "dotnet build" dotnet-build dotnet build "$SLN"
  if find "$ROOT/game" -name '*Tests.csproj' -not -path '*/maps/*' | grep -q .; then
    step "dotnet test" dotnet-test dotnet test "$SLN"
  else
    report N/A "dotnet test" "no *Tests.csproj project yet"
  fi
  step "dotnet format" dotnet-format dotnet format "$SLN" --verify-no-changes
fi

# Python pipeline
if [ ! -f "$ROOT/$MAPGEN/pyproject.toml" ]; then
  for s in "pytest" "ruff check" "ruff format --check"; do
    report N/A "$s" "no $MAPGEN/pyproject.toml yet"
  done
elif ! command -v uv >/dev/null 2>&1; then
  for s in "pytest" "ruff check" "ruff format --check"; do
    report N/A "$s" "uv not on PATH"
  done
else
  step "pytest" pytest uv run --project "$MAPGEN" pytest "$MAPGEN"
  step "ruff check" ruff-check uv run --project "$MAPGEN" ruff check "$MAPGEN"
  step "ruff format --check" ruff-format uv run --project "$MAPGEN" ruff format --check "$MAPGEN"
fi

# Godot
if GODOT_BIN="$(find_godot)"; then
  if godot_import "$GODOT_BIN" "$LOGS/godot-import.log"; then
    report PASS "godot headless import" "$IMPORT_DETAIL (log: $LOGS/godot-import.log)"
  else
    report FAIL "godot headless import" "$IMPORT_DETAIL (log: $LOGS/godot-import.log)"
  fi
else
  report N/A "godot headless import" "godot not on PATH and \$GODOT not set"
fi

# TODO: gdUnit4 headless command once the add-on is installed.
if [ -d "$ROOT/game/addons/gdUnit4" ]; then
  report FAIL "gdunit4 tests" "add-on present but verify.sh has no run command yet; add it"
else
  report N/A "gdunit4 tests" "game/addons/gdUnit4 not installed"
fi

# TODO (prompt 6): screenshot script. Headless mode can't render, so it needs a windowed run.
report N/A "screenshots" "no screenshot script yet (prompt 6)"

echo "verify summary (logs: $LOGS)"
printf '%s\n' "${SUMMARY[@]}"
exit 0
