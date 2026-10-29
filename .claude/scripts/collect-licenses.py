"""Copies the third-party licence texts of an exported build into <build-dir>/licenses/.

Usage: python3 -I .claude/scripts/collect-licenses.py <build-dir>

- Godot: LICENSE.txt, COPYRIGHT.txt (third-party notices with their licence texts) and
  AUTHORS.md from the Godot repository at the commit of the version in
  game/RoadmanSimulator.csproj. They are downloaded once into $GODOT_CACHE (default
  ~/.cache/godot/<version>/licenses) and checked against the SHA-256 values below.
- NuGet: every package and runtime pack listed in the build's *.deps.json files (what the
  export actually shipped), copied from the restored package folder ($NUGET_PACKAGES or
  ~/.nuget/packages): its licence and third-party notice files, and the file a nuspec names
  with <license type="file">. A package without such a file is accepted only if its nuspec
  points at the Godot repository at the same commit (GodotSharp); it gets Godot's LICENSE.txt.

Exits 1 if any text can't be found or verified. To upgrade Godot, add the new version's
commit and hashes to GODOT_SOURCES (and install-godot.sh).
"""

import hashlib
import json
import os
import re
import shutil
import sys
import urllib.request
import xml.etree.ElementTree as ET
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
CSPROJ = ROOT / "game" / "RoadmanSimulator.csproj"

# version -> (commit, {file: sha256}); the commit is the one the X.Y.Z-stable tag points at.
GODOT_SOURCES = {
    "4.7.2": (
        "ed1daf0bf001b61586d9930840f2f1394092c079",
        {
            "LICENSE.txt": "b0435e3b3e4e55238f05f4b306f30524a1b2e20147810d436eaa554fa6855c80",
            "COPYRIGHT.txt": "cb1980c88089573bcacd7221d777c689bb8bbd778799f24c27fca0fe5f774d6d",
            "AUTHORS.md": "6ca51d335b9de108ea53c5621e820c187d98c6faf92e14c8413ddb946b5e56cf",
        },
    ),
}
GODOT_RAW = "https://raw.githubusercontent.com/godotengine/godot/{commit}/{file}"
GODOT_REPO = "https://github.com/godotengine/godot"
LICENCE_FILE = re.compile(
    r"^(licen[cs]e|copying|notice|third[-_]?party[-_]?notices)(\.(txt|md))?$",
    re.IGNORECASE,
)
NUSPEC_NS = re.compile(r"^\{[^}]*\}")


class LicenceError(Exception):
    pass


def sha256(path: Path) -> str:
    return hashlib.sha256(path.read_bytes()).hexdigest()


def godot_version() -> str:
    match = re.search(r'Sdk="Godot\.NET\.Sdk/([0-9.]+)"', CSPROJ.read_text())
    if not match:
        raise LicenceError(f"no Godot.NET.Sdk version in {CSPROJ}")
    return match.group(1)


def godot_texts(version: str) -> tuple[str, dict[str, Path]]:
    if version not in GODOT_SOURCES:
        raise LicenceError(f"no commit and hashes for Godot {version} in {__file__}")
    commit, hashes = GODOT_SOURCES[version]
    cache = Path(os.environ.get("GODOT_CACHE", Path.home() / ".cache" / "godot" / version))
    cache = cache / "licenses"
    cache.mkdir(parents=True, exist_ok=True)
    files = {}
    for name, expected in hashes.items():
        path = cache / name
        if not path.is_file() or sha256(path) != expected:
            url = GODOT_RAW.format(commit=commit, file=name)
            with urllib.request.urlopen(url, timeout=60) as response:
                path.write_bytes(response.read())
            if sha256(path) != expected:
                path.unlink()
                raise LicenceError(f"SHA-256 mismatch for Godot {version} {name}")
        files[name] = path
    return commit, files


def shipped_packages(build: Path) -> dict[tuple[str, str], str]:
    """(id, version) -> library type, from every *.deps.json in the build."""
    packages: dict[tuple[str, str], str] = {}
    deps_files = sorted(build.rglob("*.deps.json"))
    if not deps_files:
        raise LicenceError(f"no *.deps.json in {build}")
    for deps in deps_files:
        for key, library in json.loads(deps.read_text())["libraries"].items():
            if library["type"] == "project":
                continue
            name, version = key.split("/", 1)
            name = name.removeprefix("runtimepack.")
            packages[(name, version)] = library["type"]
    return packages


def read_nuspec(package_dir: Path) -> dict[str, str]:
    nuspecs = list(package_dir.glob("*.nuspec"))
    if len(nuspecs) != 1:
        raise LicenceError(f"expected one .nuspec in {package_dir}")
    info: dict[str, str] = {}
    for element in ET.parse(nuspecs[0]).getroot().iter():
        tag = NUSPEC_NS.sub("", element.tag)
        if tag in ("license", "copyright", "licenseUrl", "projectUrl"):
            info[tag] = (element.text or "").strip()
            if tag == "license":
                info["license_type"] = element.get("type", "")
        elif tag == "repository":
            info["repository_url"] = element.get("url", "")
            info["repository_commit"] = element.get("commit", "")
    return info


def main(argv: list[str]) -> int:
    if len(argv) != 2 or not Path(argv[1]).is_dir():
        print("usage: collect-licenses.py <build-dir>", file=sys.stderr)
        return 2
    build = Path(argv[1]).resolve()
    out = build / "licenses"
    nuget = Path(os.environ.get("NUGET_PACKAGES", Path.home() / ".nuget" / "packages"))
    try:
        version = godot_version()
        commit, godot_files = godot_texts(version)
        packages = shipped_packages(build)
        if out.exists():
            shutil.rmtree(out)
        godot_dir = out / f"godot-{version}"
        godot_dir.mkdir(parents=True)
        for name, path in godot_files.items():
            shutil.copyfile(path, godot_dir / name)
        readme = [
            "Third-party licences",
            "",
            "Licence texts of the third-party software in this build, taken from the exact",
            "versions it was built with.",
            "",
            f"godot-{version}/: Godot Engine {version} (engine and export templates).",
            f"  Source: {GODOT_REPO} at commit {commit}.",
            "  LICENSE.txt is Godot's MIT licence; COPYRIGHT.txt lists the third-party",
            "  components in the engine with their copyright notices and licence texts.",
        ]
        for (name, pkg_version), kind in sorted(packages.items()):
            package_dir = nuget / name.lower() / pkg_version
            if not package_dir.is_dir():
                raise LicenceError(f"{name} {pkg_version} not restored in {nuget}")
            nuspec = read_nuspec(package_dir)
            texts = [p for p in package_dir.iterdir() if p.is_file() and LICENCE_FILE.match(p.name)]
            if nuspec.get("license_type") == "file":
                named = package_dir / nuspec["license"]
                if named.is_file() and named not in texts:
                    texts.append(named)
            dest = out / f"{name}-{pkg_version}"
            dest.mkdir()
            note = ""
            if texts:
                for path in sorted(texts):
                    shutil.copyfile(path, dest / path.name)
            elif (
                nuspec.get("repository_url", "").startswith(GODOT_REPO)
                and nuspec.get("repository_commit") == commit
            ):
                shutil.copyfile(godot_files["LICENSE.txt"], dest / "LICENSE.txt")
                note = (
                    "  The package has no licence file; it is built from Godot commit\n"
                    f"  {commit}, so LICENSE.txt is Godot's licence at that commit."
                )
            else:
                raise LicenceError(f"{name} {pkg_version}: no licence file in {package_dir}")
            readme += [
                "",
                f"{dest.name}/: NuGet {kind} {name} {pkg_version}.",
                f"  Licence: {nuspec.get('license') or nuspec.get('licenseUrl', 'not stated')}."
                f" {nuspec.get('copyright', '')}".rstrip(),
                f"  Files: {', '.join(sorted(p.name for p in dest.iterdir()))}.",
            ]
            if note:
                readme.append(note)
        (out / "README.txt").write_text("\n".join(readme) + "\n")
    except (LicenceError, OSError, ValueError, ET.ParseError) as exc:
        print(f"error: {exc}", file=sys.stderr)
        return 1
    print(f"licenses: {out} (Godot {version}, {len(packages)} NuGet package(s))")
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv))
