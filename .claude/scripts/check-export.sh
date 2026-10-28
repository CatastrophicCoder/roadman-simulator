#!/usr/bin/env bash
# Checks an exported build folder before it is zipped for a release.
# Usage: .claude/scripts/check-export.sh <build-dir>
#
# <build-dir> holds a Windows export (RoadmanSimulator.exe with the PCK embedded and
# data_RoadmanSimulator_windows_x86_64/) or a macOS export (RoadmanSimulator.app),
# plus NOTICE.txt and CREDITS.md. Fails if:
# - NOTICE.txt or CREDITS.md is missing, or NOTICE.txt lacks a credit line of a
#   pack in game/maps/ (checked by `mapgen notice --check`);
# - an exported game assembly contains test code (check-release-assembly.cs), or
#   test framework files were exported;
# - the PCK lists a file from game/tests/ or game/addons/gdUnit4/;
# - macOS: the executable isn't universal (x86_64 + arm64) or the bundle is unsigned.
# Prints one line per check and exits non-zero if any failed.

set -u

ROOT="$(cd "$(dirname "$0")/../.." && pwd)"
DIR="${1:-}"
[ -n "$DIR" ] && [ -d "$DIR" ] || { echo "usage: check-export.sh <build-dir>" >&2; exit 2; }
DIR="$(cd "$DIR" && pwd)"
failed=0

pass() { echo "PASS  $1"; }
fail() { echo "FAIL  $1"; failed=1; }

# Attribution
for f in NOTICE.txt CREDITS.md; do
  if [ -s "$DIR/$f" ]; then pass "$f present"; else fail "$f missing"; fi
done
maps=()
for m in "$ROOT"/game/maps/*/manifest.json; do
  [ -f "$m" ] && maps+=("$(basename "$(dirname "$m")")")
done
if [ "${#maps[@]}" -eq 0 ]; then
  fail "no map pack in game/maps/ (the build would ship without a map)"
elif [ -s "$DIR/NOTICE.txt" ]; then
  if out="$(cd "$ROOT" && uv run --project tools/mapgen mapgen notice --check "$DIR/NOTICE.txt" "${maps[@]}" 2>&1)"; then
    pass "NOTICE.txt has every credit of: ${maps[*]}"
  else
    fail "NOTICE.txt credits: $(echo "$out" | tail -3 | tr '\n' ' ')"
  fi
fi

# Platform layout
if [ -f "$DIR/RoadmanSimulator.exe" ]; then
  platform=windows
  pcks=("$DIR/RoadmanSimulator.exe")
  data_dirs=("$DIR"/data_RoadmanSimulator_windows_*)
elif [ -d "$DIR/RoadmanSimulator.app" ]; then
  platform=macos
  app="$DIR/RoadmanSimulator.app"
  pcks=("$app"/Contents/Resources/*.pck)
  data_dirs=("$app"/Contents/Resources/data_RoadmanSimulator_macos_*)
else
  echo "FAIL  no RoadmanSimulator.exe or RoadmanSimulator.app in $DIR"
  exit 1
fi
echo "platform: $platform"

# Test code in the C# build output
dlls=()
for d in "${data_dirs[@]}"; do
  [ -f "$d/RoadmanSimulator.dll" ] && dlls+=("$d/RoadmanSimulator.dll")
done
if [ "${#dlls[@]}" -eq 0 ]; then
  fail "no exported RoadmanSimulator.dll"
fi
for dll in "${dlls[@]}"; do
  if out="$(dotnet run "$ROOT/.claude/scripts/check-release-assembly.cs" -- "$dll" 2>&1)"; then
    pass "no test code in ${dll#"$DIR"/}"
  else
    fail "test code in ${dll#"$DIR"/}: $(echo "$out" | tail -3 | tr '\n' ' ')"
  fi
done
if hits="$(find "$DIR" \( -iname 'gdUnit4*' -o -iname 'xunit*' -o -iname 'Microsoft.TestPlatform*' \
  -o -iname 'Microsoft.VisualStudio.TestPlatform*' -o -iname 'testhost*' -o -iname 'Moq*' \) | grep .)"; then
  fail "test framework files exported: $(echo "$hits" | head -3 | tr '\n' ' ')"
else
  pass "no test framework files"
fi

# Excluded resources in the PCK. plugin.cfg is left out of the search: project.godot
# names it under editor_plugins, so its path is in the PCK even when the file isn't.
excluded="$(cd "$ROOT/game" && git ls-files tests addons/gdUnit4 | grep -v '^addons/gdUnit4/plugin\.cfg$')"
if [ "${#pcks[@]}" -eq 0 ] || [ ! -f "${pcks[0]}" ]; then
  fail "no PCK found"
elif [ -z "$excluded" ]; then
  fail "can't list game/tests and game/addons/gdUnit4 with git"
else
  # Python, because BSD grep with hundreds of -F patterns is very slow on a large file.
  found="$(printf '%s\n' "$excluded" | python3 -I -c '
import sys
data = b"".join(open(p, "rb").read() for p in sys.argv[1:])
for line in sys.stdin.read().splitlines():
    if line and line.encode() in data:
        print(line)
' "${pcks[@]}")"
  if [ -n "$found" ]; then
    fail "PCK contains excluded files: $(echo "$found" | head -3 | tr '\n' ' ')"
  else
    pass "PCK has none of $(echo "$excluded" | wc -l | tr -d ' ') files from tests/ and addons/gdUnit4/"
  fi
fi

# macOS bundle: universal executable and an ad-hoc signature
if [ "$platform" = macos ]; then
  exe="$app/Contents/MacOS/RoadmanSimulator"
  if archs="$(python3 -I - "$exe" <<'EOF'
import struct, sys
names = {0x01000007: "x86_64", 0x0100000C: "arm64"}
with open(sys.argv[1], "rb") as f:
    magic, count = struct.unpack(">II", f.read(8))
    if magic != 0xCAFEBABE:
        sys.exit("not a universal (fat) Mach-O file")
    archs = sorted(names.get(struct.unpack(">5I", f.read(20))[0], "other") for _ in range(count))
print(" ".join(archs))
sys.exit(0 if {"x86_64", "arm64"} <= set(archs) else 1)
EOF
  )"; then
    pass "universal executable ($archs)"
  else
    fail "executable isn't universal: $archs"
  fi
  if [ -s "$app/Contents/_CodeSignature/CodeResources" ]; then
    pass "bundle is signed"
  else
    fail "no Contents/_CodeSignature/CodeResources (unsigned)"
  fi
fi

exit "$failed"
