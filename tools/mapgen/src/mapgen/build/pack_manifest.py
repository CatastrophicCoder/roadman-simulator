"""The map pack's `manifest.json` (schema version 1) and its committed JSON Schema.

The schema in `schemas/manifest-v1.schema.json` is generated from `PackManifest` and
committed, so the game side can read it. A test checks that the two match. After a
model change, regenerate it with:

    uv run python -c "from mapgen.build.pack_manifest import write_schema; write_schema()"
"""

import json
from collections.abc import Iterable, Mapping
from datetime import datetime
from importlib import resources
from pathlib import Path
from typing import Any, Literal

import jsonschema
from pydantic import Field

from mapgen.config import StrictModel

SCHEMA_VERSION = 1
SCHEMA_FILE = "manifest-v1.schema.json"
MANIFEST_NAME = "manifest.json"
BUILDINGS_NAME = "buildings.json"
TILES_DIR = "tiles"
# The DEM files don't state their vertical datum, so record what the heights are.
HEIGHT_DATUM = "source DEM heights, metres"


class ManifestError(Exception):
    """The manifest or its credits could not be built or failed validation."""


class PackOrigin(StrictModel):
    x: float
    y: float


class PackAxes(StrictModel):
    x: Literal["east"] = "east"
    y: Literal["up"] = "up"
    z: Literal["-north"] = "-north"


class PackTile(StrictModel):
    id: str = Field(pattern=r"^tile_x-?\d+_y-?\d+$")
    file: str = Field(description="path relative to the pack folder")
    bounds_local: list[float] = Field(
        min_length=4, max_length=4, description="[min_x, min_y, max_x, max_y] in local metres"
    )
    building_count: int = Field(ge=0)


class PackCredit(StrictModel):
    source: str
    dataset: str
    licence: str
    attribution: str
    retrieved_at: str


class PackTool(StrictModel):
    name: str
    version: str


class PackManifest(StrictModel):
    schema_version: Literal[1]
    map_id: str
    display_name: str
    area: Literal["full", "test"]
    crs: str
    origin: PackOrigin
    height_datum: str
    tile_size_m: int = Field(gt=0)
    axes: PackAxes
    tiles: list[PackTile]
    credits: list[PackCredit]
    generated_at: str
    tool: PackTool


def schema_path() -> Path:
    return Path(str(resources.files("mapgen.build").joinpath("schemas", SCHEMA_FILE)))


def generated_schema() -> dict[str, Any]:
    schema = PackManifest.model_json_schema()
    return {
        "$schema": "https://json-schema.org/draft/2020-12/schema",
        "$id": SCHEMA_FILE,
        **schema,
    }


def committed_schema() -> dict[str, Any]:
    return json.loads(schema_path().read_text(encoding="utf-8"))


def write_schema(path: Path | None = None) -> Path:
    target = path or schema_path()
    target.write_text(json.dumps(generated_schema(), indent=2) + "\n", encoding="utf-8")
    return target


def validate_manifest_file(path: Path) -> None:
    """Validate a written manifest against the committed JSON Schema."""
    document = json.loads(path.read_text(encoding="utf-8"))
    try:
        jsonschema.validate(document, committed_schema())
    except jsonschema.ValidationError as exc:
        raise ManifestError(f"{path} does not match {SCHEMA_FILE}: {exc.message}") from exc


def credits_for(
    files: Iterable[Path],
    raw_dir: Path,
    entries: Mapping[str, Mapping[str, Any]],
    extra: Iterable[PackCredit] = (),
) -> list[PackCredit]:
    """Credits from the download manifest entries of the raw files the build read, plus
    `extra` credits (e.g. third-party building overrides).

    Every read file must have an entry, so no data reaches a pack without its licence.
    Credits with the same source, dataset, licence and attribution (e.g. several DEM
    sheets) fold into one credit with the latest `retrieved_at`.
    """
    folded: dict[tuple[str, str, str, str], PackCredit] = {}

    def add(credit: PackCredit) -> None:
        key = (credit.source, credit.dataset, credit.licence, credit.attribution)
        previous = folded.get(key)
        if previous is None or _instant(credit.retrieved_at) > _instant(previous.retrieved_at):
            folded[key] = credit

    for credit in extra:
        add(credit)
    for path in files:
        try:
            rel = path.resolve().relative_to(raw_dir.resolve()).as_posix()
        except ValueError:
            raise ManifestError(f"{path} is outside the raw data folder {raw_dir}") from None
        entry = entries.get(rel)
        if entry is None:
            raise ManifestError(
                f"{rel} is not in the download manifest, so its licence is unknown; "
                "run `mapgen download` to register it"
            )
        try:
            credit = PackCredit(
                source=str(entry["source"]),
                dataset=str(entry["dataset"]),
                licence=str(entry["licence"]),
                attribution=str(entry["attribution"]),
                retrieved_at=str(entry["retrieved_at"]),
            )
        except KeyError as exc:
            raise ManifestError(f"download manifest entry {rel} has no {exc}") from None
        add(credit)
    return sorted(folded.values(), key=lambda c: (c.source, c.dataset, c.attribution))


def _instant(text: str) -> datetime:
    try:
        return datetime.fromisoformat(text.replace("Z", "+00:00"))
    except ValueError:
        raise ManifestError(f"invalid retrieved_at {text!r} in the download manifest") from None


def validate_manifest_document(document: Mapping[str, Any]) -> None:
    """Validate a manifest document (before it is written) against the committed schema."""
    try:
        jsonschema.validate(dict(document), committed_schema())
    except jsonschema.ValidationError as exc:
        raise ManifestError(f"manifest does not match {SCHEMA_FILE}: {exc.message}") from exc
