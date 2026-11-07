#!/usr/bin/env bash
# Runs every check in the definition of done and reports each step as
# PASS, FAIL, WARN or N/A (not available yet). Always exits 0 (except for a usage error,
# exit 2, before any step runs); read the summary.
# Logs go to $VERIFY_LOG_DIR (default: a temp directory).
#
# Usage: verify.sh                         the full run: the gate before every commit
#        verify.sh --quick [<base>]        quick mode for iterating (docs/specs/test-performance.md,
#                                          ADR 0020): only the gdUnit4 groups the changed files select
#                                          (game/tests/test-groups.json), pytest only when a pytest input
#                                          changed, no export check or screenshots. Changed files: the
#                                          working tree against HEAD, or against <base>, plus untracked files.
#        verify.sh --quick --dry-run [<base>]  print the changed paths, rules, groups and suites; run nothing

set -u

ROOT="$(cd "$(dirname "$0")/../.." && pwd)"
# shellcheck source=lib.sh
. "$ROOT/.claude/scripts/lib.sh"

QUICK=0
DRY_RUN=0
BASE=""
for arg in "$@"; do
  case "$arg" in
    --quick) QUICK=1 ;;
    --dry-run) DRY_RUN=1 ;;
    -*) echo "usage: verify.sh [--quick [--dry-run] [<base>]]" >&2; exit 2 ;;
    *)
      [ -z "$BASE" ] || { echo "usage: verify.sh [--quick [--dry-run] [<base>]]" >&2; exit 2; }
      BASE="$arg"
      ;;
  esac
done
if [ "$QUICK" -eq 0 ] && { [ "$DRY_RUN" -eq 1 ] || [ -n "$BASE" ]; }; then
  echo "usage: --dry-run and <base> need --quick" >&2
  exit 2
fi

SELECT=(python3 -I "$ROOT/.claude/scripts/test_select.py")
if [ "$DRY_RUN" -eq 1 ]; then
  "${SELECT[@]}" select --dry-run --repo "$ROOT" ${BASE:+"$BASE"}
  exit 0
fi

LOGS="${VERIFY_LOG_DIR:-$(mktemp -d "${TMPDIR:-/tmp}/verify.XXXXXX")}"
mkdir -p "$LOGS"
SUMMARY=()
QUICK_NA="quick mode; the full /verify runs it"

# Quick mode: the selection, computed once before any step (test_select.py, shell format).
SEL_MODE=all SEL_GROUPS="" SEL_FILTER="" SEL_ALL_REASON="" SEL_PYTEST=1 SEL_PYTEST_REASON=""
SEL_REASONS=()
if [ "$QUICK" -eq 1 ]; then
  if sel_out="$("${SELECT[@]}" select --format shell --repo "$ROOT" ${BASE:+"$BASE"} 2>"$LOGS/test-select.log")"; then
    while IFS='=' read -r key value; do
      case "$key" in
        MODE) SEL_MODE="$value" ;;
        GROUPS) SEL_GROUPS="$value" ;;
        FILTER) SEL_FILTER="$value" ;;
        ALL_REASON) SEL_ALL_REASON="$value" ;;
        PYTEST) SEL_PYTEST="$value" ;;
        PYTEST_REASON) SEL_PYTEST_REASON="$value" ;;
        REASON_*) SEL_REASONS+=("$value") ;;
      esac
    done <<<"$sel_out"
  else
    SEL_MODE=all
    SEL_ALL_REASON="test_select.py failed (log: $LOGS/test-select.log)"
    SEL_PYTEST=1
    SEL_PYTEST_REASON="$SEL_ALL_REASON"
  fi
fi

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
  if [ "$QUICK" -eq 1 ] && [ "$SEL_PYTEST" != 1 ]; then
    report N/A "pytest" "quick mode; no pytest input changed (the full /verify runs it)"
  else
    step "pytest" pytest uv run --project "$MAPGEN" pytest "$MAPGEN"
    [ "$QUICK" -eq 1 ] && SUMMARY[${#SUMMARY[@]}-1]+=" (quick: $SEL_PYTEST_REASON)"
  fi
  step "ruff check" ruff-check uv run --project "$MAPGEN" ruff check "$MAPGEN"
  step "ruff format --check" ruff-format uv run --project "$MAPGEN" ruff format --check "$MAPGEN"
fi

# The gdUnit4 group map (criterion 7 of docs/specs/test-performance.md) and the selection
# logic's self-tests, in both modes.
if ! command -v python3 >/dev/null 2>&1; then
  report N/A "test groups" "python3 not on PATH"
else
  log="$LOGS/test-groups.log"
  if (cd "$ROOT" && python3 -I -m unittest discover -s .claude/scripts/tests &&
    "${SELECT[@]}" check --repo "$ROOT") >"$log" 2>&1; then
    report PASS "test groups" "$(tail -1 "$log") (log: $log)"
  else
    report FAIL "test groups" "$(grep -m1 -E 'ERROR|FAIL' "$log" || tail -1 "$log") (log: $log)"
  fi
fi

# The test-area pack the gdUnit4 tests load (game/tests/packs/somero-test/), rebuilt from the
# committed fixture when it is missing or stale; the import below picks it up.
if ! command -v uv >/dev/null 2>&1 || [ ! -f "$ROOT/$MAPGEN/pyproject.toml" ]; then
  report N/A "test pack" "uv or $MAPGEN/pyproject.toml missing"
else
  log="$LOGS/test-pack.log"
  if "$ROOT/.claude/scripts/build-test-pack.sh" --if-stale --no-import >"$log" 2>&1; then
    report PASS "test pack" "$(tail -1 "$log")"
  else
    report FAIL "test pack" "$(tail -1 "$log") (log: $log)"
  fi
fi

# Godot. The import also builds the .godot/ cache that the gdUnit4 run needs.
if GODOT_BIN="$(find_godot)"; then
  if godot_import "$GODOT_BIN" "$LOGS/godot-import.log"; then
    report PASS "godot headless import" "$IMPORT_DETAIL (log: $LOGS/godot-import.log)"
  else
    report FAIL "godot headless import" "$IMPORT_DETAIL (log: $LOGS/godot-import.log)"
  fi
  gd_name="gdunit4 tests"
  gd_filter=()
  if [ "$QUICK" -eq 1 ]; then
    case "$SEL_MODE" in
      groups)
        gd_name="gdunit4 tests (quick: ${SEL_GROUPS// /, })"
        gd_filter=(--filter "$SEL_FILTER")
        ;;
      none) gd_name="gdunit4 tests (quick)" ;;
      *) gd_name="gdunit4 tests (quick: all)" ;;
    esac
  fi
  # Other Godot processes (an editor, a game, a stuck test host) slow the run down and skew timings.
  others="$(pgrep -l '^[Gg]odot' 2>/dev/null || true)"
  if [ -n "$others" ]; then
    report WARN "other godot processes" "$(echo "$others" | wc -l | tr -d ' ') running before the gdUnit4 step; tests may be slower: $(echo "$others" | head -3 | cut -c1-80 | tr '\n' ';')"
  fi
  if [ "$HAVE_DOTNET" -eq 0 ]; then
    report N/A "$gd_name" "dotnet not on PATH"
  elif [ "$QUICK" -eq 1 ] && [ "$SEL_MODE" = none ]; then
    report N/A "$gd_name" "no changed file affects a gdUnit4 group"
  else
    log="$LOGS/gdunit4.log"
    (cd "$ROOT" && GODOT_BIN="$GODOT_BIN" run_with_timeout "${GDUNIT_TIMEOUT:-1200}" "$log" \
      dotnet test "$GAME_CSPROJ" --settings "$RUNSETTINGS" ${gd_filter[@]+"${gd_filter[@]}"})
    rc=$?
    if [ "$rc" -eq 0 ]; then
      report PASS "$gd_name" "log: $log"
    elif [ "$rc" -eq 124 ]; then
      report FAIL "$gd_name" "timed out after ${GDUNIT_TIMEOUT:-1200}s (log: $log)"
    else
      report FAIL "$gd_name" "$(tail -3 "$log" | tr '\n' ' ') (log: $log)"
    fi
  fi
else
  report N/A "godot headless import" "godot not on PATH and \$GODOT not set"
  report N/A "gdunit4 tests" "godot not on PATH and \$GODOT not set"
fi

# Headless startup (definition of done): headless_startup in lib.sh, the same
# check CI runs against its test-area pack.
START_MAP="$(sed -n 's|^map/id="\(.*\)"$|\1|p' "$ROOT/game/project.godot")"
START_MAP="${START_MAP:-somero}"
if ! GODOT_BIN="$(find_godot)"; then
  report N/A "headless startup" "godot not on PATH and \$GODOT not set"
elif [ "$BUILD_OK" -eq 0 ]; then
  report N/A "headless startup" "dotnet build failed or didn't run; the game would run stale code"
elif [ ! -f "$ROOT/game/maps/$START_MAP/manifest.json" ]; then
  report N/A "headless startup" "no map pack at game/maps/$START_MAP (run /regen-map $START_MAP)"
else
  log="$LOGS/headless-startup.log"
  if headless_startup "$GODOT_BIN" "$log"; then
    report PASS "headless startup" "--new-game on $START_MAP: $STARTUP_DETAIL (log: $log)"
  else
    report FAIL "headless startup" "$STARTUP_DETAIL (log: $log)"
  fi
fi

# Exported build: a Windows export (the faster one) checked by check-export.sh, the
# same check release.yml runs on both platforms.
GODOT_VERSION="$(sed -n 's|.*Sdk="Godot\.NET\.Sdk/\([0-9.]*\)".*|\1|p' "$ROOT/$GAME_CSPROJ")"
TEMPLATES=""
for d in "$HOME/Library/Application Support/Godot" "${XDG_DATA_HOME:-$HOME/.local/share}/godot" "${APPDATA:-}/Godot"; do
  [ -d "$d/export_templates/$GODOT_VERSION.stable.mono" ] && TEMPLATES="$d/export_templates/$GODOT_VERSION.stable.mono"
done
if [ "$QUICK" -eq 1 ]; then
  report N/A "exported build" "$QUICK_NA"
elif ! GODOT_BIN="$(find_godot)"; then
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
if [ "$QUICK" -eq 1 ]; then
  report N/A "screenshots" "$QUICK_NA"
elif ! GODOT_BIN="$(find_godot)"; then
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
  # The place inspector and HUD street label at street level (docs/specs/place-information.md).
  log="$LOGS/screenshot-centre-street-level-inspect.log"
  if [ ! -f "$PACK/buildings.json" ] || [ ! -f "$PACK/pois.json" ]; then
    report N/A "screenshot inspect" "pack has no buildings.json or pois.json (run /regen-map $MAP_ID)"
  elif ! grep -qx "centre-street-level" <<<"$PRESETS"; then
    report N/A "screenshot inspect" "pack has no centre-street-level preset"
  else
    screenshot_preset "$GODOT_BIN" "$MAP_ID" centre-street-level "$SHOTS/centre-street-level-inspect.png" "$log" --inspect
    case $? in
      0) report PASS "screenshot inspect" "build/screenshots/centre-street-level-inspect.png" ;;
      2) report N/A "screenshot inspect" "outside built area (log: $log)" ;;
      124) report FAIL "screenshot inspect" "timed out after ${GODOT_SCREENSHOT_TIMEOUT:-120}s (log: $log)" ;;
      *) report FAIL "screenshot inspect" "$(grep -m1 -E 'ERROR|error' "$log" || tail -1 "$log") (log: $log)" ;;
    esac
  fi
  # The player in the third-person view at street level (docs/specs/player-on-foot.md, criterion 23).
  log="$LOGS/screenshot-centre-street-level-player.log"
  if ! grep -qx "centre-street-level" <<<"$PRESETS"; then
    report N/A "screenshot player" "pack has no centre-street-level preset"
  else
    screenshot_preset "$GODOT_BIN" "$MAP_ID" centre-street-level "$SHOTS/centre-street-level-player.png" "$log" --player --seed 1
    case $? in
      0) report PASS "screenshot player" "build/screenshots/centre-street-level-player.png" ;;
      2) report N/A "screenshot player" "outside built area (log: $log)" ;;
      124) report FAIL "screenshot player" "timed out after ${GODOT_SCREENSHOT_TIMEOUT:-120}s (log: $log)" ;;
      *) report FAIL "screenshot player" "$(grep -m1 -E 'ERROR|error' "$log" || tail -1 "$log") (log: $log)" ;;
    esac
  fi
  # The death sequence at the world edge: the death camera's frame and the death screen (docs/specs/world-edge-game.md,
  # criterion 25). N/A on a test-area pack, as the world-edge preset is.
  for death in fall screen; do
    log="$LOGS/screenshot-world-edge-death-$death.log"
    if ! grep -qx "world-edge" <<<"$PRESETS"; then
      report N/A "screenshot death $death" "pack has no world-edge preset"
      continue
    fi
    screenshot_preset "$GODOT_BIN" "$MAP_ID" world-edge "$SHOTS/world-edge-death-$death.png" "$log" --death "$death" --seed 1
    case $? in
      0) report PASS "screenshot death $death" "build/screenshots/world-edge-death-$death.png" ;;
      2) report N/A "screenshot death $death" "outside built area (log: $log)" ;;
      124) report FAIL "screenshot death $death" "timed out after ${GODOT_SCREENSHOT_TIMEOUT:-120}s (log: $log)" ;;
      *) report FAIL "screenshot death $death" "$(grep -m1 -E 'ERROR|error' "$log" || tail -1 "$log") (log: $log)" ;;
    esac
  done
  # The menus over the overview preset: main menu, pause menu, settings view, slot list with sample saves
  # (docs/specs/menu-settings-save.md, criterion 24), and the inventory screen with five items given
  # (docs/specs/inventory.md, criterion 24).
  for menu in main pause settings slots inventory; do
    log="$LOGS/screenshot-menu-$menu.log"
    if ! grep -qx "overview" <<<"$PRESETS"; then
      report N/A "screenshot menu $menu" "pack has no overview preset"
      continue
    fi
    menu_args=(--menu "$menu")
    if [ "$menu" = inventory ]; then
      menu_args+=(--give baseball_bat,crowbar,tracksuit,hoodie,phone)
    fi
    screenshot_preset "$GODOT_BIN" "$MAP_ID" overview "$SHOTS/menu-$menu.png" "$log" "${menu_args[@]}"
    case $? in
      0) report PASS "screenshot menu $menu" "build/screenshots/menu-$menu.png" ;;
      2) report N/A "screenshot menu $menu" "outside built area (log: $log)" ;;
      124) report FAIL "screenshot menu $menu" "timed out after ${GODOT_SCREENSHOT_TIMEOUT:-120}s (log: $log)" ;;
      *) report FAIL "screenshot menu $menu" "$(grep -m1 -E 'ERROR|error' "$log" || tail -1 "$log") (log: $log)" ;;
    esac
  done
fi

if [ "$QUICK" -eq 1 ]; then
  echo "verify summary, quick mode (logs: $LOGS)"
else
  echo "verify summary (logs: $LOGS)"
fi
printf '%s\n' "${SUMMARY[@]}"
if [ "$QUICK" -eq 1 ]; then
  echo
  case "$SEL_MODE" in
    groups)
      echo "gdUnit4 selection: ${SEL_GROUPS// /, }"
      for r in ${SEL_REASONS[@]+"${SEL_REASONS[@]}"}; do echo "  $r"; done
      ;;
    none) echo "gdUnit4 selection: none (no changed file affects a gdUnit4 group)" ;;
    *) echo "gdUnit4 selection: all suites, because $SEL_ALL_REASON" ;;
  esac
  echo "Git-ignored files (a regenerated pack in game/maps/, the test pack) are invisible to the selection."
  echo "Quick mode doesn't meet the definition of done: run the full /verify before a commit."
fi
exit 0
