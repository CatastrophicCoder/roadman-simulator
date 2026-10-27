#!/usr/bin/env bash
# PostToolUse hook: format the file Claude just edited.
# .cs files under game/ get `dotnet format whitespace`, .py files get `ruff format`.
# Never blocks: a missing or failing tool prints a warning and exits 0.

set -u

root="${CLAUDE_PROJECT_DIR:-$(pwd)}"

warn() {
  local msg="format hook: $1"
  if command -v jq >/dev/null 2>&1; then
    jq -cn --arg m "$msg" \
      '{systemMessage: $m, hookSpecificOutput: {hookEventName: "PostToolUse", additionalContext: $m}}'
  else
    printf '{"systemMessage": "format hook: warning (jq not found to format the message)"}\n'
  fi
  exit 0
}

input="$(cat)"
if command -v jq >/dev/null 2>&1; then
  file="$(printf '%s' "$input" | jq -r '.tool_input.file_path // empty')"
elif command -v python3 >/dev/null 2>&1; then
  file="$(printf '%s' "$input" | python3 -I -c 'import json, sys; print(json.load(sys.stdin).get("tool_input", {}).get("file_path", ""))')"
else
  warn "neither jq nor python3 found, skipped formatting"
fi

[ -n "$file" ] && [ -f "$file" ] || exit 0

rel="${file#"$root"/}"

case "$rel" in
  game/maps/*)
    exit 0
    ;;
  game/*.cs)
    command -v dotnet >/dev/null 2>&1 || warn "dotnet not found, $rel not formatted"
    out="$(cd "$root/game" && dotnet format whitespace . --folder --include "${rel#game/}" 2>&1)" \
      || warn "dotnet format failed on $rel: $(printf '%s' "$out" | tail -5)"
    ;;
  tools/mapgen/*.py)
    if [ -f "$root/tools/mapgen/pyproject.toml" ] && command -v uv >/dev/null 2>&1; then
      out="$(uv run --project "$root/tools/mapgen" --no-sync ruff format "$file" 2>&1)" \
        || warn "ruff format failed on $rel (is ruff a dev dependency in tools/mapgen?): $(printf '%s' "$out" | tail -3)"
    elif command -v ruff >/dev/null 2>&1; then
      out="$(ruff format "$file" 2>&1)" || warn "ruff format failed on $rel: $(printf '%s' "$out" | tail -3)"
    else
      warn "ruff not set up yet (no tools/mapgen/pyproject.toml, no ruff on PATH), $rel not formatted"
    fi
    ;;
  *.py)
    if command -v ruff >/dev/null 2>&1; then
      out="$(ruff format "$file" 2>&1)" || warn "ruff format failed on $rel: $(printf '%s' "$out" | tail -3)"
    else
      warn "ruff not on PATH, $rel not formatted"
    fi
    ;;
esac

exit 0
