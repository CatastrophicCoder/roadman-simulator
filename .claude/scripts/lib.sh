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
