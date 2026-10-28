#!/usr/bin/env bash
# Installs Godot .NET for Linux x86_64 (and optionally its export templates) in CI.
# Usage: .github/scripts/install-godot.sh [--templates]
#
# The version comes from the Godot.NET.Sdk version in game/RoadmanSimulator.csproj.
# Downloads go to $GODOT_CACHE (default ~/.cache/godot/<version>) and are checked
# against the SHA-512 values below, taken from the release's SHA512-SUMS.txt.
# To upgrade Godot, add the new version's two hashes here.
# Sets GODOT (the real binary, not a symlink) in $GITHUB_ENV when that is set.

set -euo pipefail

ROOT="$(cd "$(dirname "$0")/../.." && pwd)"
VERSION="$(sed -n 's|.*Sdk="Godot\.NET\.Sdk/\([0-9.]*\)".*|\1|p' "$ROOT/game/RoadmanSimulator.csproj")"
[ -n "$VERSION" ] || { echo "error: no Godot.NET.Sdk version in game/RoadmanSimulator.csproj" >&2; exit 1; }

case "$VERSION" in
  4.7.2)
    EDITOR_SHA512=1855960b27ee3ef5e66e5e228cced69d55637b24334a7411162687dcd077d8f9f645348cdb8eae984bec8135d49ed855a1e3a16476786b8bce60774fd8402d13
    TEMPLATES_SHA512=bb5c41d72370ed743660361f6228006f808ab04ca33abdc545d740b044f3fe057f32ae8cb7873a1bc86ddcd82ae683b9f6dfdfe4179852f2c0f1acde2ff6bd5a
    ;;
  *)
    echo "error: no SHA-512 values for Godot $VERSION in $0; add them from SHA512-SUMS.txt of the release" >&2
    exit 1
    ;;
esac

BASE="https://github.com/godotengine/godot-builds/releases/download/$VERSION-stable"
NAME="Godot_v$VERSION-stable_mono_linux_x86_64"
CACHE="${GODOT_CACHE:-$HOME/.cache/godot/$VERSION}"
mkdir -p "$CACHE"

# fetch <file> <sha512>: downloads into the cache unless a verified copy is there.
fetch() {
  local file="$1" sum="$2" path="$CACHE/$1"
  if [ -f "$path" ] && echo "$sum  $path" | sha512sum --check --status; then
    echo "cached: $file"
    return
  fi
  rm -f "$path"
  echo "downloading: $file"
  curl --fail --location --silent --show-error --retry 3 --output "$path.part" "$BASE/$file"
  echo "$sum  $path.part" | sha512sum --check --status || {
    echo "error: SHA-512 mismatch for $file" >&2
    rm -f "$path.part"
    exit 1
  }
  mv "$path.part" "$path"
}

fetch "$NAME.zip" "$EDITOR_SHA512"
rm -rf "$CACHE/editor"
unzip -q "$CACHE/$NAME.zip" -d "$CACHE/editor"
GODOT_BIN="$CACHE/editor/$NAME/Godot_v$VERSION-stable_mono_linux.x86_64"
chmod +x "$GODOT_BIN"
"$GODOT_BIN" --headless --version

if [ "${1:-}" = "--templates" ]; then
  fetch "Godot_v$VERSION-stable_mono_export_templates.tpz" "$TEMPLATES_SHA512"
  DEST="$HOME/.local/share/godot/export_templates/$VERSION.stable.mono"
  rm -rf "$DEST" "$CACHE/templates"
  unzip -q "$CACHE/Godot_v$VERSION-stable_mono_export_templates.tpz" -d "$CACHE/templates"
  mkdir -p "$(dirname "$DEST")"
  mv "$CACHE/templates/templates" "$DEST"
  echo "templates: $DEST"
fi

if [ -n "${GITHUB_ENV:-}" ]; then
  echo "GODOT=$GODOT_BIN" >>"$GITHUB_ENV"
fi
echo "GODOT=$GODOT_BIN"
