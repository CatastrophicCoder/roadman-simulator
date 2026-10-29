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
    return 1
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
BUILD_OK=0
if ! command -v dotnet >/dev/null 2>&1; then
  for s in "dotnet build" "dotnet test" "dotnet format" "release has no tests"; do
    report N/A "$s" "dotnet not on PATH"
  done
else
  HAVE_DOTNET=1
  step "dotnet build" dotnet-build dotnet build "$SLN" && BUILD_OK=1
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

# Exported build: a Windows export (the faster one) checked by check-export.sh, the
# same check release.yml runs on both platforms.
GODOT_VERSION="$(sed -n 's|.*Sdk="Godot\.NET\.Sdk/\([0-9.]*\)".*|\1|p' "$ROOT/$GAME_CSPROJ")"
TEMPLATES=""
for d in "$HOME/Library/Application Support/Godot" "${XDG_DATA_HOME:-$HOME/.local/share}/godot" "${APPDATA:-}/Godot"; do
  [ -d "$d/export_templates/$GODOT_VERSION.stable.mono" ] && TEMPLATES="$d/export_templates/$GODOT_VERSION.stable.mono"
done
if ! GODOT_BIN="$(find_godot)"; then
  report N/A "exported build" "godot not on PATH and \$GODOT not set"
elif [ -z "$TEMPLATES" ]; then
  report N/A "exported build" "no export templates for $GODOT_VERSION.stable.mono installed"
elif ! ls "$ROOT"/game/maps/*/manifest.json >/dev/null 2>&1; then
  report N/A "exported build" "no map pack in game/maps/ (run /regen-map <map-id>)"
else
  log="$LOGS/export-check.log" out="$LOGS/export-windows"
  maps=()
  for m in "$ROOT"/game/maps/*/manifest.json; do maps+=("$(basename "$(dirname "$m")")"); done
  rm -rf "$out" && mkdir -p "$out"
  if {
    run_with_timeout "${GODOT_EXPORT_TIMEOUT:-900}" "$LOGS/export-windows.log" \
      "$GODOT_BIN" --headless --path "$ROOT/game" --export-release "Windows Desktop" "$out/RoadmanSimulator.exe" &&
      cp "$ROOT/CREDITS.md" "$out/" &&
      python3 -I "$ROOT/.claude/scripts/collect-licenses.py" "$out" &&
      (cd "$ROOT" && uv run --project "$MAPGEN" mapgen notice "${maps[@]}" --out "$out/NOTICE.txt") &&
      "$ROOT/.claude/scripts/check-export.sh" "$out"
  } >"$log" 2>&1; then
    report PASS "exported build" "Windows export checked (log: $log)"
  else
    report FAIL "exported build" "$(grep -m1 FAIL "$log" || tail -1 "$log") (log: $log, export log: $LOGS/export-windows.log)"
  fi
fi

# Screenshots: one per camera preset in the pack, to build/screenshots/<preset>.png.
# Headless mode can't render, so each one is a short windowed run (see game/CLAUDE.md).
MAP_ID="${MAP_ID:-somero}"
PACK="$ROOT/game/maps/$MAP_ID"
SHOTS="$ROOT/build/screenshots"
if ! GODOT_BIN="$(find_godot)"; then
  report N/A "screenshots" "godot not on PATH and \$GODOT not set"
elif [ "$BUILD_OK" -eq 0 ]; then
  report N/A "screenshots" "dotnet build failed or didn't run; the game would run stale code"
elif [ ! -f "$PACK/manifest.json" ]; then
  report N/A "screenshots" "no map pack at game/maps/$MAP_ID (run /regen-map $MAP_ID)"
elif [ ! -f "$PACK/camera_presets.json" ]; then
  report N/A "screenshots" "pack has no camera_presets.json (run /regen-map $MAP_ID)"
elif ! command -v python3 >/dev/null 2>&1; then
  report N/A "screenshots" "python3 not on PATH (needed to read the preset names)"
elif ! PRESETS="$(camera_preset_names "$PACK/camera_presets.json" 2>"$LOGS/screenshot-presets.log")"; then
  report FAIL "screenshots" "can't read camera_presets.json (log: $LOGS/screenshot-presets.log)"
else
  [ -n "$PRESETS" ] || report FAIL "screenshots" "camera_presets.json lists no presets"
  while IFS= read -r preset; do
    [ -n "$preset" ] || continue
    log="$LOGS/screenshot-$preset.log"
    screenshot_preset "$GODOT_BIN" "$MAP_ID" "$preset" "$SHOTS/$preset.png" "$log"
    case $? in
      0) report PASS "screenshot $preset" "build/screenshots/$preset.png" ;;
      2) report N/A "screenshot $preset" "outside built area (log: $log)" ;;
      124) report FAIL "screenshot $preset" "timed out after ${GODOT_SCREENSHOT_TIMEOUT:-120}s (log: $log)" ;;
      *) report FAIL "screenshot $preset" "$(grep -m1 -E 'ERROR|error' "$log" || tail -1 "$log") (log: $log)" ;;
    esac
  done <<<"$PRESETS"
  # The road graph debug view over the overview preset (docs/specs/roads-and-road-graph.md).
  log="$LOGS/screenshot-overview-road-graph.log"
  if [ ! -f "$PACK/road_graph.json" ]; then
    report N/A "screenshot road graph" "pack has no road_graph.json (run /regen-map $MAP_ID)"
  elif ! grep -qx "overview" <<<"$PRESETS"; then
    report N/A "screenshot road graph" "pack has no overview preset"
  else
    screenshot_preset "$GODOT_BIN" "$MAP_ID" overview "$SHOTS/overview-road-graph.png" "$log" --road-graph
    case $? in
      0) report PASS "screenshot road graph" "build/screenshots/overview-road-graph.png" ;;
      2) report N/A "screenshot road graph" "outside built area (log: $log)" ;;
      124) report FAIL "screenshot road graph" "timed out after ${GODOT_SCREENSHOT_TIMEOUT:-120}s (log: $log)" ;;
      *) report FAIL "screenshot road graph" "$(grep -m1 -E 'ERROR|error' "$log" || tail -1 "$log") (log: $log)" ;;
    esac
  fi
fi

echo "verify summary (logs: $LOGS)"
printf '%s\n' "${SUMMARY[@]}"
exit 0
