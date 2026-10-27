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
GAME_CSPROJ="game/RoadmanSimulator.csproj"
CORE_TESTS="game/Roadman.Core.Tests"
RUNSETTINGS="game/.runsettings"
MAPGEN="tools/mapgen"

# Test code must not ship: build the game assembly as ExportRelease and check
# that neither test assemblies nor test types end up in the output.
release_check() {
  local log="$LOGS/release-check.log" out="$LOGS/export-release"
  {
    rm -rf "$out" &&
      dotnet build "$ROOT/$GAME_CSPROJ" -c ExportRelease -o "$out" &&
      ! find "$out" -iname 'gdUnit4*' -o -iname 'xunit*' -o -iname 'Microsoft.TestPlatform*' \
          -o -iname 'Microsoft.VisualStudio.TestPlatform*' -o -iname 'testhost*' -o -iname 'Moq*' |
        grep . &&
      dotnet run "$ROOT/.claude/scripts/check-release-assembly.cs" -- "$out/RoadmanSimulator.dll"
  } >"$log" 2>&1
  if [ $? -eq 0 ]; then
    report PASS "release has no tests" "log: $log"
  else
    report FAIL "release has no tests" "$(tail -3 "$log" | tr '\n' ' ') (log: $log)"
  fi
}

# C#
HAVE_DOTNET=0
if ! command -v dotnet >/dev/null 2>&1; then
  for s in "dotnet build" "dotnet test" "dotnet format" "release has no tests"; do
    report N/A "$s" "dotnet not on PATH"
  done
else
  HAVE_DOTNET=1
  step "dotnet build" dotnet-build dotnet build "$SLN"
  step "dotnet test" dotnet-test dotnet test "$CORE_TESTS"
  step "dotnet format" dotnet-format dotnet format "$SLN" --verify-no-changes
  release_check
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

# Godot. The import also builds the .godot/ cache that the gdUnit4 run needs.
if GODOT_BIN="$(find_godot)"; then
  if godot_import "$GODOT_BIN" "$LOGS/godot-import.log"; then
    report PASS "godot headless import" "$IMPORT_DETAIL (log: $LOGS/godot-import.log)"
  else
    report FAIL "godot headless import" "$IMPORT_DETAIL (log: $LOGS/godot-import.log)"
  fi
  if [ "$HAVE_DOTNET" -eq 0 ]; then
    report N/A "gdunit4 tests" "dotnet not on PATH"
  else
    log="$LOGS/gdunit4.log"
    (cd "$ROOT" && GODOT_BIN="$GODOT_BIN" run_with_timeout "${GDUNIT_TIMEOUT:-600}" "$log" \
      dotnet test "$GAME_CSPROJ" --settings "$RUNSETTINGS")
    rc=$?
    if [ "$rc" -eq 0 ]; then
      report PASS "gdunit4 tests" "log: $log"
    elif [ "$rc" -eq 124 ]; then
      report FAIL "gdunit4 tests" "timed out after ${GDUNIT_TIMEOUT:-600}s (log: $log)"
    else
      report FAIL "gdunit4 tests" "$(tail -3 "$log" | tr '\n' ' ') (log: $log)"
    fi
  fi
else
  report N/A "godot headless import" "godot not on PATH and \$GODOT not set"
  report N/A "gdunit4 tests" "godot not on PATH and \$GODOT not set"
fi

# TODO (prompt 6): screenshot script. Headless mode can't render, so it needs a windowed run.
report N/A "screenshots" "no screenshot script yet (prompt 6)"

echo "verify summary (logs: $LOGS)"
printf '%s\n' "${SUMMARY[@]}"
exit 0
