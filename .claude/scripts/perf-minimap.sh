#!/usr/bin/env bash
# Measures what the minimap costs per frame (docs/specs/minimap.md, "Godot", criterion 19).
# Usage: .claude/scripts/perf-minimap.sh   (MAP_ID defaults to somero; needs the full pack)
#
# For each spot (spawn, roadblock) and mode (headless, windowed) it runs the game's PerfTool (`-- --perf ...`,
# debug builds only) $PERF_REPEATS times (default 3) per configuration, alternating full and no-minimap, and
# writes $PERF_OUT/minimap-<spot>-<mode>-<config>-<n>.json (default build/perf/). Headless runs use
# --fixed-fps <physics ticks per second> (one physics step per frame, no frame cap, no sleeping) and give the
# mean real frame time, that is the process and physics work per frame; windowed runs give the main
# viewport's render time (CPU + GPU), with the real windowed frame time as information only. Per spot and
# mode it reports the medians and the minimap's cost (median full - median no-minimap), and the minimap's
# drawn road segment, label and symbol counts (means over the measured frames, medians over the runs). Per
# spot, headless cost + windowed render cost must be under 0.5 ms: PASS, else FAIL (exit 1). Not part of
# /verify. Windowed runs open a window for some seconds each (see game/CLAUDE.md, "Screenshots"). On a pack
# without road 52's north roadblock (a test-area pack) the tool exits 2 and this prints N/A.

set -u

ROOT="$(cd "$(dirname "$0")/../.." && pwd)"
# shellcheck source=lib.sh
. "$ROOT/.claude/scripts/lib.sh"

MAP_ID="${MAP_ID:-somero}"
OUT="${PERF_OUT:-$ROOT/build/perf}"
REPEATS="${PERF_REPEATS:-3}"
# physics/common/physics_ticks_per_second from project.godot (Godot's default 60 when it isn't set).
TICKS="$(sed -n 's/^physics_ticks_per_second=\([0-9][0-9]*\)$/\1/p' "$ROOT/game/project.godot" | head -1)"
TICKS="${TICKS:-60}"
LOGS="${PERF_LOG_DIR:-$(mktemp -d "${TMPDIR:-/tmp}/perf-minimap.XXXXXX")}"
mkdir -p "$OUT" "$LOGS"

if ! GODOT_BIN="$(find_godot)"; then
  echo "N/A: godot not on PATH and \$GODOT not set"
  exit 0
fi
if [ ! -f "$ROOT/game/maps/$MAP_ID/manifest.json" ]; then
  echo "N/A: no map pack at game/maps/$MAP_ID (run /regen-map $MAP_ID)"
  exit 0
fi
if ! (cd "$ROOT" && dotnet build game/RoadmanSimulator.sln) >"$LOGS/dotnet-build.log" 2>&1; then
  echo "FAIL: dotnet build (log: $LOGS/dotnet-build.log)"
  exit 1
fi

failed=0
first=1
for spot in spawn roadblock; do
  for mode in headless windowed; do
    for rep in $(seq 1 "$REPEATS"); do
      for config in full no-minimap; do
        json="$OUT/minimap-$spot-$mode-$config-$rep.json"
        log="$LOGS/minimap-$spot-$mode-$config-$rep.log"
        rm -f "$json"
        flags=()
        case "$config" in
          no-minimap) flags=(--no-minimap) ;;
        esac
        if [ "$mode" = headless ]; then
          godot_flags=(--headless --fixed-fps "$TICKS")
        else
          godot_flags=(--resolution "${SCREENSHOT_RESOLUTION:-1600x900}")
        fi
        run_with_timeout "${GODOT_PERF_TIMEOUT:-300}" "$log" \
          "$GODOT_BIN" "${godot_flags[@]}" --path "$ROOT/game" \
          -- --perf "$spot" "$json" --seed 1 --map "$MAP_ID" ${flags[@]+"${flags[@]}"}
        rc=$?
        if [ "$rc" -eq 2 ] && [ "$first" -eq 1 ]; then
          echo "N/A: $(grep -m1 'N/A' "$log" || echo "not the full pack") (log: $log)"
          exit 0
        fi
        first=0
        if [ "$rc" -ne 0 ] || [ ! -s "$json" ] || grep -qE '^(SCRIPT )?ERROR' "$log"; then
          echo "FAIL  $spot $mode $config #$rep: exit $rc, $(grep -m1 -E 'ERROR' "$log" || tail -1 "$log") (log: $log)"
          failed=1
        else
          echo "done  $spot $mode $config #$rep"
        fi
      done
    done
  done
done

if [ "$failed" -ne 0 ]; then
  echo "FAIL: some runs failed; no summary (logs: $LOGS)"
  exit 1
fi

python3 -I - "$OUT" "$REPEATS" <<'PY'
import json
import pathlib
import statistics
import sys

out = pathlib.Path(sys.argv[1])
repeats = int(sys.argv[2])
budget_ms = 0.5
configs = ("full", "no-minimap")


def runs(spot, mode, config):
    return [json.loads((out / f"minimap-{spot}-{mode}-{config}-{n}.json").read_text()) for n in range(1, repeats + 1)]


def budget_figure(run, mode):
    if mode == "headless":
        return run["mean_frame_ms"]
    return run["mean_render_ms"]


print()
print(f"medians of {repeats} runs, ms per frame")
print(f"{'spot':<10} {'mode':<9} {'figure':<22} {'full':>8} {'no map':>8} {'cost':>8}")
ok = True
for spot in ("spawn", "roadblock"):
    spot_cost = 0.0
    for mode in ("headless", "windowed"):
        med = {c: statistics.median(budget_figure(r, mode) for r in runs(spot, mode, c)) for c in configs}
        cost = med["full"] - med["no-minimap"]
        spot_cost += cost
        label = "frame (fixed fps)" if mode == "headless" else "render cpu + gpu"
        print(f"{spot:<10} {mode:<9} {label:<22} {med['full']:>8.3f} {med['no-minimap']:>8.3f} {cost:>+8.3f}")
        if mode == "windowed":
            for part in ("mean_render_cpu_ms", "mean_render_gpu_ms", "mean_frame_ms"):
                m = {c: statistics.median(r[part] for r in runs(spot, mode, c)) for c in configs}
                name = {"mean_render_cpu_ms": "  render cpu", "mean_render_gpu_ms": "  render gpu", "mean_frame_ms": "  real frame (info)"}[part]
                print(f"{'':<10} {'':<9} {name:<22} {m['full']:>8.3f} {m['no-minimap']:>8.3f} {m['full'] - m['no-minimap']:>+8.3f}")
    full = runs(spot, "headless", "full")
    counts = {k: statistics.median(r[k] for r in full) for k in ("minimap_segments", "minimap_labels", "minimap_symbols")}
    print(f"{spot:<10} drawn per frame: {counts['minimap_segments']:.1f} road segments, {counts['minimap_labels']:.1f} labels, {counts['minimap_symbols']:.1f} symbols")
    result = "PASS" if spot_cost < budget_ms else "FAIL"
    ok = ok and spot_cost < budget_ms
    print(f"{spot:<10} budget: headless cost + windowed render cost = {spot_cost:+.3f} ms (< {budget_ms} ms): {result}")
print()
print("headless: mean real frame time with --fixed-fps (one physics step per frame); windowed: render cpu + gpu")
print("PASS" if ok else "FAIL")
sys.exit(0 if ok else 1)
PY
