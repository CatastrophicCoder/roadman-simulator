"""The `notice` command: a plain-text NOTICE of the data credits of built map packs.

The credits come only from each pack's `manifest.json` (ADR 0006), which lists the
licence and attribution of every raw file the build read. Licence URLs are looked up by
licence name, so the code knows licences, never maps. `expected_lines` lists the lines a
NOTICE must contain; `missing_lines` checks a file against them (`mapgen notice --check`).
"""

import json
from collections.abc import Iterable, Sequence
from dataclasses import dataclass
from pathlib import Path

from pydantic import ValidationError

from mapgen import paths
from mapgen.build.pack_manifest import (
    MANIFEST_NAME,
    ManifestError,
    PackCredit,
    PackManifest,
    validate_manifest_file,
)

HEADING = "THIRD-PARTY MAP DATA NOTICES"
MODIFIED = (
    "The data listed below was modified: it was reprojected, cut to the map area and "
    "processed into 3D map tiles."
)
# Licence names as adapters record them in the download manifest -> licence text URL.
LICENCE_URLS: dict[str, str] = {
    "CC BY 4.0": "https://creativecommons.org/licenses/by/4.0/",
    "ODbL 1.0": "https://opendatacommons.org/licenses/odbl/1-0/",
}


class NoticeError(Exception):
    """A pack or its manifest is missing or invalid, or a licence has no known URL."""


@dataclass(frozen=True)
class PackCredits:
    map_id: str
    display_name: str
    area: str
    credits: tuple[PackCredit, ...]


def licence_url(licence: str) -> str:
    try:
        return LICENCE_URLS[licence]
    except KeyError:
        known = ", ".join(LICENCE_URLS)
        raise NoticeError(
            f"no URL known for licence {licence!r} (known: {known}); add it to LICENCE_URLS"
        ) from None


def licence_line(credit: PackCredit) -> str:
    return f"Licence: {credit.licence}, {licence_url(credit.licence)}"


def read_pack_credits(repo_root: Path, map_id: str, pack_dir: Path | None = None) -> PackCredits:
    """The credits of the built pack of `map_id` (default folder: `game/maps/<map-id>/`)."""
    pack = pack_dir or paths.pack_dir(repo_root, map_id)
    manifest_file = pack / MANIFEST_NAME
    if not pack.is_dir():
        raise NoticeError(f"map pack not found: {pack}; run `mapgen build {map_id}` first")
    if not manifest_file.is_file():
        raise NoticeError(f"{manifest_file} not found; run `mapgen build {map_id}` first")
    try:
        validate_manifest_file(manifest_file)
        manifest = PackManifest.model_validate(
            json.loads(manifest_file.read_text(encoding="utf-8"))
        )
    except (ManifestError, ValidationError, ValueError) as exc:
        raise NoticeError(f"{manifest_file} is invalid: {exc}") from exc
    if manifest.map_id != map_id:
        raise NoticeError(f"{manifest_file} is for map {manifest.map_id!r}, not {map_id!r}")
    if not manifest.credits:
        raise NoticeError(f"{manifest_file} lists no credits")
    return PackCredits(map_id, manifest.display_name, manifest.area, tuple(manifest.credits))


def read_all(repo_root: Path, map_ids: Sequence[str]) -> list[PackCredits]:
    return [read_pack_credits(repo_root, map_id) for map_id in dict.fromkeys(map_ids)]


def expected_lines(packs: Iterable[PackCredits]) -> list[str]:
    """Every credit's attribution (exactly as in the manifest) and its licence line."""
    lines: list[str] = []
    for pack in packs:
        for credit in pack.credits:
            lines += [credit.attribution, licence_line(credit)]
    return list(dict.fromkeys(lines))


def render_notice(packs: Sequence[PackCredits]) -> str:
    out = [HEADING, "=" * len(HEADING), "", MODIFIED, ""]
    licences: dict[str, str] = {}
    for pack in packs:
        title = f"Map: {pack.display_name} ({pack.map_id})"
        out += [title, "-" * len(title), ""]
        for credit in pack.credits:
            out += [credit.attribution, licence_line(credit), ""]
            licences[credit.licence] = licence_url(credit.licence)
    out += ["Licences", "--------", ""]
    out += [f"{name}: {url}" for name, url in licences.items()]
    return "\n".join(out) + "\n"


def missing_lines(text: str, packs: Iterable[PackCredits]) -> list[str]:
    """The expected lines that aren't a whole line of `text` (surrounding spaces ignored)."""
    present = {line.strip() for line in text.splitlines()}
    return [line for line in expected_lines(packs) if line not in present]
