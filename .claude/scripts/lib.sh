# Shared helpers for the scripts in .claude/scripts/. Source, don't run.

# Resolve the Godot .NET binary. Uses $GODOT if set, else `godot` on PATH.
# Symlinks are resolved: run through a symlink, Godot .NET can't find its
# GodotSharp assemblies and crashes with ".NET: Assemblies not found".
find_godot() {
  local g="${GODOT:-godot}"
  g="$(command -v "$g" 2>/dev/null)" || return 1
  if command -v realpath >/dev/null 2>&1; then
    g="$(realpath "$g")"
  fi
  printf '%s\n' "$g"
}

# run_with_timeout <seconds> <logfile> <command...>
# Runs the command with output to logfile. Returns its exit code, or 124 on timeout.
run_with_timeout() {
  local secs="$1" log="$2"
  shift 2
  "$@" >"$log" 2>&1 &
  local pid=$! i=0
  while kill -0 "$pid" 2>/dev/null; do
    if [ "$i" -ge "$secs" ]; then
      kill "$pid" 2>/dev/null
      sleep 1
      kill -9 "$pid" 2>/dev/null
      wait "$pid" 2>/dev/null
      return 124
    fi
    sleep 1
    i=$((i + 1))
  done
  wait "$pid"
}

# Headless Godot import. Fails on a non-zero exit, an ERROR line or a timeout.
# Sets IMPORT_DETAIL to a one-line reason.
godot_import() {
  local godot="$1" log="$2" rc
  run_with_timeout "${GODOT_IMPORT_TIMEOUT:-300}" "$log" "$godot" --headless --path "$ROOT/game" --import
  rc=$?
  if [ "$rc" -eq 124 ]; then
    IMPORT_DETAIL="timed out after ${GODOT_IMPORT_TIMEOUT:-300}s"
    return 1
  fi
  if [ "$rc" -ne 0 ]; then
    IMPORT_DETAIL="exit $rc: $(grep -m1 -E 'ERROR|crash' "$log")"
    return 1
  fi
  if grep -qE '^(SCRIPT )?ERROR' "$log"; then
    IMPORT_DETAIL="$(grep -cE '^(SCRIPT )?ERROR' "$log") error(s), first: $(grep -m1 -E '^(SCRIPT )?ERROR' "$log")"
    return 1
  fi
  IMPORT_DETAIL="no errors"
  return 0
}

# Screenshot of one camera preset with a windowed (not headless) Godot run:
# --headless disables rendering. Returns 0 (saved), 2 (preset outside the
# built area, no PNG), 124 (timeout) or 1 (any other failure).
# screenshot_preset <godot> <map-id> <preset> <out.png> <logfile> [extra tool flags...]
screenshot_preset() {
  local godot="$1" map="$2" preset="$3" out="$4" log="$5" rc
  shift 5
  mkdir -p "$(dirname "$out")"
  rm -f "$out"
  run_with_timeout "${GODOT_SCREENSHOT_TIMEOUT:-120}" "$log" \
    "$godot" --path "$ROOT/game" --resolution "${SCREENSHOT_RESOLUTION:-1600x900}" \
    -- --map "$map" --screenshot "$preset" "$out" "$@"
  rc=$?
  case "$rc" in
    0)
      [ -s "$out" ] && ! grep -qE '^(SCRIPT )?ERROR' "$log" && return 0
      return 1
      ;;
    2)
      # N/A only when nothing else went wrong (for example, a tile failed to load).
      grep -qE '^(SCRIPT )?ERROR' "$log" && return 1
      return 2
      ;;
    124) return 124 ;;
    *) return 1 ;;
  esac
}

# Prints the preset names in a pack's camera_presets.json, one per line.
camera_preset_names() {
  python3 -I -c 'import json, sys; print("\n".join(json.load(open(sys.argv[1]))["presets"]))' "$1"
}
