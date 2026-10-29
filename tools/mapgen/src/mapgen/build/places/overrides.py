"""The optional `maps/<map-id>/overrides/buildings.json`: category and name fixes.

Keys are building ids (`osm:way/<id>`, `osm:relation/<id>`) or building-use ids
(`mml:rakennus/<mtk_id>`). A building-use key applies to every building matched to that
record. When both reach a building, the building's own key wins, field by field. An
override that sets any name field sets all three. `mapgen build` validates the file
before the load stage, so an invalid file fails the build before the pack is touched; a
missing file is allowed. An entry with the licence `own work` adds no credit; any other
(third-party) entry that applies to a building adds a manifest credit (`override_credits`).
"""

import json
from collections.abc import Mapping
from dataclasses import dataclass
from pathlib import Path
from typing import Any

from pydantic import ValidationError

from mapgen.build.names import name_value
from mapgen.build.pack_manifest import PackCredit
from mapgen.build.places.categories import Category
from mapgen.build.places.model import BuildingOverride, BuildingOverrides

OVERRIDES_CREDIT_SOURCE = "overrides"

OVERRIDES_DIR = "overrides"
OVERRIDES_NAME = "buildings.json"


class BuildingOverridesError(Exception):
    """The overrides file is invalid. The message is shown to the user."""


def overrides_path(map_dir: Path) -> Path:
    return map_dir / OVERRIDES_DIR / OVERRIDES_NAME


class _DuplicateKeyError(Exception):
    def __init__(self, key: str) -> None:
        super().__init__(key)
        self.key = key


def _reject_duplicate_keys(pairs: list[tuple[str, Any]]) -> dict[str, Any]:
    result: dict[str, Any] = {}
    for key, value in pairs:
        if key in result:
            raise _DuplicateKeyError(key)
        result[key] = value
    return result


def parse_building_overrides(data: bytes, source: Path | str) -> BuildingOverrides:
    """Validate the bytes of an overrides file; `source` names it in the error message.

    A repeated key in any object is an error (JSON readers would silently keep one).
    """
    try:
        json.loads(data, object_pairs_hook=_reject_duplicate_keys)
    except _DuplicateKeyError as exc:
        raise BuildingOverridesError(
            f"invalid building overrides {source}: duplicate key {json.dumps(exc.key)}"
        ) from None
    except ValueError:
        pass  # malformed JSON: pydantic reports it below
    try:
        return BuildingOverrides.model_validate_json(data)
    except ValidationError as exc:
        raise BuildingOverridesError(f"invalid building overrides {source}: {exc}") from exc


def read_building_overrides(path: Path) -> BuildingOverrides | None:
    """The validated overrides file at `path`; None when it doesn't exist."""
    if not path.is_file():
        return None
    return parse_building_overrides(path.read_bytes(), path)


@dataclass(frozen=True)
class ResolvedOverride:
    """What the override file sets for one building, and which keys it came from."""

    category: Category | None = None
    category_key: str | None = None
    names: Mapping[str, str | None] | None = None
    names_key: str | None = None


def override_names(override: BuildingOverride) -> dict[str, str | None]:
    """All three name fields of an override that sets names (missing ones null)."""
    return {
        "name": name_value(override.name),
        "name_sv": name_value(override.name_sv),
        "ref": name_value(override.ref),
    }


def resolve_override(
    overrides: BuildingOverrides | None, building_id: str, use_id: str | None
) -> ResolvedOverride:
    """The override fields for a building: its own key first, then its building-use key."""
    if overrides is None:
        return ResolvedOverride()
    keys = [building_id] + ([use_id] if use_id is not None else [])
    category: Category | None = None
    category_key: str | None = None
    names: dict[str, str | None] | None = None
    names_key: str | None = None
    for key in keys:
        entry = overrides.buildings.get(key)
        if entry is None:
            continue
        if category is None and "category" in entry.model_fields_set:
            category, category_key = entry.category, key
        if names is None and entry.sets_names:
            names, names_key = override_names(entry), key
    return ResolvedOverride(category, category_key, names, names_key)


def override_credits(overrides: BuildingOverrides | None, applied: set[str]) -> list[PackCredit]:
    """Manifest credits of the applied third-party entries (`applied`: their keys).

    Each gives `source` "overrides", `dataset` the entry's `source`, and its licence,
    attribution and `retrieved_at`; `own work` entries give none. Identical credits fold
    when the manifest credits are built.
    """
    if overrides is None:
        return []
    credits: list[PackCredit] = []
    for key in sorted(applied):
        entry = overrides.buildings[key]
        if entry.is_own_work:
            continue
        assert entry.attribution is not None and entry.retrieved_at is not None
        credits.append(
            PackCredit(
                source=OVERRIDES_CREDIT_SOURCE,
                dataset=entry.source,
                licence=entry.licence,
                attribution=entry.attribution,
                retrieved_at=entry.retrieved_at,
            )
        )
    return credits
