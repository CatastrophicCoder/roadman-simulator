#!/usr/bin/env bash
# Fails if release files contain the MML API key or an unredacted api-key value.
# Usage: .github/scripts/check-no-secret.sh <file-or-dir>...
#
# Zip files are unpacked to a temp folder and searched too. With MML_API_KEY set,
# the literal key is searched for as well (literal mode); without it only the
# pattern check runs. Prints only the names of offending files, never a match.

set -euo pipefail

[ "$#" -gt 0 ] || { echo "usage: check-no-secret.sh <file-or-dir>..." >&2; exit 2; }

WORK="$(mktemp -d)"
trap 'rm -rf "$WORK"' EXIT

targets=()
n=0
for path in "$@"; do
  [ -e "$path" ] || { echo "error: $path doesn't exist" >&2; exit 2; }
  targets+=("$path")
  while IFS= read -r -d '' zip; do
    n=$((n + 1))
    unzip -q "$zip" -d "$WORK/$n"
    targets+=("$WORK/$n")
  done < <(find "$path" -type f -name '*.zip' -print0)
done

failed=0
# search <grep options...>: prints matching file names; exits on a grep error (2),
# so an unreadable file can't pass as "no match".
search() {
  local out rc=0
  out="$(grep -rla "$@" "${targets[@]}")" || rc=$?
  if [ "$rc" -gt 1 ]; then
    echo "error: grep failed (exit $rc)" >&2
    exit 2
  fi
  printf '%s' "$out"
}

# api-key=<value> (any case, also api_key / apikey, as http.redact matches) where the
# value isn't the redaction marker.
hits="$(search -iE 'api[-_]?key=([^*&"[:space:]]|\*[^*]|\*\*[^*])')"
if [ -n "$hits" ]; then
  echo "unredacted api-key value in:" >&2
  echo "$hits" | sed "s|^$WORK/[0-9]*/|<zip>/|" >&2
  failed=1
fi

key="${MML_API_KEY:-}"
if [ -n "$key" ]; then
  hits="$(search -F -e "$key")"
  if [ -n "$hits" ]; then
    echo "MML API key found in:" >&2
    echo "$hits" | sed "s|^$WORK/[0-9]*/|<zip>/|" >&2
    failed=1
  fi
  echo "literal and pattern check: ${#targets[@]} path(s)"
else
  echo "pattern check (no key in env): ${#targets[@]} path(s)"
fi

exit "$failed"
