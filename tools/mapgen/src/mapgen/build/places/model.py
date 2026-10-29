"""The pack's `buildings.json` (schema version 2) and `pois.json` (schema version 1), and
the map's `overrides/buildings.json` (schema version 1), with their committed JSON Schemas.

Coordinates in `pois.json` are Godot local metres: `position` is `[x, y, z]` (+X east, +Y
up, -Z north) and outline points are `[x, z]`. Every named feature has `name`, `name_sv`
and `ref` (string or null; ADR 0012). The schemas in `schemas/` are generated from the
models here and committed, so the game side can read them; tests check that they match.
After a model change, regenerate them with:

    uv run python -c "from mapgen.build.places.model import write_schemas; write_schemas()"
"""

import json
from dataclasses import dataclass
from datetime import date
from importlib import resources
from pathlib import Path
from typing import Annotated, Any, Literal

import jsonschema
from pydantic import (
    BaseModel,
    ConfigDict,
    Field,
    StringConstraints,
    field_validator,
    model_validator,
)
from pydantic import ValidationError as PydanticValidationError

from mapgen.build.places.categories import Category, CategorySource, NameSource
from mapgen.licences import LICENCE_URLS

BUILDINGS_SCHEMA_VERSION = 2
POIS_SCHEMA_VERSION = 1
OVERRIDES_SCHEMA_VERSION = 1
POIS_NAME = "pois.json"
# The licence value of an override that is the project's own work (no credit). Any other
# licence is third-party data and must be one `mapgen notice` knows a URL for.
OWN_WORK = "own work"
OVERRIDE_LICENCES: tuple[str, ...] = (OWN_WORK, *LICENCE_URLS)

Vec2 = tuple[float, float]
Vec3 = tuple[float, float, float]
PoiId = Annotated[str, StringConstraints(pattern=r"^osm:(node|way|relation)/[0-9]+$")]
BuildingId = Annotated[str, StringConstraints(pattern=r"^[a-z][a-z0-9_]*:[a-z][a-z0-9_]*/[0-9]+$")]
PoiType = Literal["shop", "amenity", "office", "tourism", "leisure"]


class PlacesError(Exception):
    """A places document doesn't match its model or committed schema."""


def _no_default(schema: dict[str, Any]) -> None:
    schema.pop("default", None)


def _optional(**kwargs: Any) -> Any:
    """A key that may be absent but, when present, isn't null (no default in the schema)."""
    return Field(default=None, json_schema_extra=_no_default, **kwargs)


class _StrictJsonModel(BaseModel):
    # Unknown keys are errors; strict: no "1" -> 1; no NaN or infinity.
    model_config = ConfigDict(extra="forbid", frozen=True, strict=True, allow_inf_nan=False)


def _not_a_bool(value: Any) -> Any:
    if isinstance(value, bool):  # True == 1 would pass a Literal[1] check
        raise ValueError("schema_version must be an integer")
    return value


# buildings.json

# The `category_raw` key each category source needs a value in.
_SOURCE_RAW = {
    "override": "override",
    "osm_building": "osm_building",
    "osm_poi": "osm_poi",
    "osm_poi_area": "osm_poi_area",
    "mml_use_class": "mml_feature_class",
    "osm_landuse": "osm_landuse",
}


class Address(_StrictJsonModel):
    street: str | None
    housenumber: str | None


class CategoryRaw(_StrictJsonModel):
    """Every source's raw value, whichever step won."""

    osm_building: str | None = Field(description="the `building` tag")
    osm_poi: list[str] = Field(
        description="POI `key=value` tags on the building and on POIs on or in it (sorted)"
    )
    osm_poi_area: list[str] = Field(
        description="POI `key=value` tags of the POI areas the building is inside (sorted)"
    )
    mml_feature_class: int | None = Field(description="matched MML building's kohdeluokka")
    mml_use_class: int | None = Field(description="matched MML building's kayttotarkoitus")
    osm_landuse: str | None = Field(description="the land use value used for step 6")
    override: Category | None = Field(description="the category from the override file")


class AppliedOverride(_StrictJsonModel):
    """An override file entry that set something on this building."""

    id: BuildingId = Field(description="the override file key")
    source: str = Field(min_length=1)
    licence: str = Field(min_length=1)
    fields: list[Literal["category", "name"]] = Field(
        min_length=1, description="what it set, sorted; name stands for all three name fields"
    )


class BuildingEntry(_StrictJsonModel):
    id: BuildingId = Field(description="e.g. osm:way/123")
    tile: str = Field(pattern=r"^tile_x-?\d+_y-?\d+$")
    node: str
    collision_nodes: list[str] = Field(min_length=1)
    height_m: float = Field(gt=0)
    height_source: Literal["mml_lidar", "mml_3d", "osm_height", "osm_levels", "default"]
    base_z: float
    base_source: Literal["dem"]
    roof_shape: Literal["flat"]
    roof_shape_source: Literal["default"]
    lidar_points: int = _optional(ge=0)
    lidar_buffer_m: float = _optional(ge=0)
    lidar_returns: Literal["all", "single"] = _optional()
    roof_z_median: float = _optional()
    roof_z_p90: float = _optional()
    lidar_rejected: Literal["few_points", "tree_cover", "too_low", "too_high"] = _optional()
    category: Category
    category_source: CategorySource | None
    category_raw: CategoryRaw
    name: str | None
    name_sv: str | None
    ref: str | None
    name_source: NameSource | None
    address: Address
    address_source: Literal["osm"] | None
    poi_ids: list[PoiId] = Field(description="POIs on or in the building (sorted)")
    area_poi_ids: list[PoiId] = Field(description="POI areas the building is inside (sorted)")
    mml_building: BuildingId | None = Field(description="matched MML building, e.g. mml:rakennus/1")
    mml_overlap: float | None = Field(
        ge=0, le=1, description="share of the footprint the MML building covers, 3 decimals"
    )
    overrides: list[AppliedOverride] = Field(
        description="override entries that set something on this building, sorted by id"
    )

    @model_validator(mode="after")
    def _consistent(self) -> "BuildingEntry":
        ids = [o.id for o in self.overrides]
        if ids != sorted(set(ids)):
            raise ValueError("overrides must be sorted by id, without duplicates")
        for applied in self.overrides:
            if applied.fields != sorted(set(applied.fields)):
                raise ValueError("override fields must be sorted, without duplicates")
        set_fields = {f for o in self.overrides for f in o.fields}
        if (self.category_source == "override") != ("category" in set_fields):
            raise ValueError("category_source override exactly when an override sets category")
        if (self.name_source == "override") != ("name" in set_fields):
            raise ValueError("name_source override exactly when an override sets the name")
        if self.category_source is None and self.category != "unknown":
            raise ValueError("category_source is null only for category unknown")
        if self.category_source is not None:
            raw = getattr(self.category_raw, _SOURCE_RAW[self.category_source])
            if raw is None or raw == []:
                raise ValueError(f"category_source {self.category_source} without its raw value")
        names = (self.name, self.name_sv, self.ref)
        if self.name_source in (None, "osm", "osm_poi") and (self.name_source is None) != all(
            n is None for n in names
        ):
            raise ValueError("name_source is null exactly when all name fields are null")
        has_address = self.address.street is not None or self.address.housenumber is not None
        if (self.address_source == "osm") != has_address:
            raise ValueError("address_source is osm exactly when the address has a value")
        if (self.mml_building is None) != (self.mml_overlap is None):
            raise ValueError("mml_building and mml_overlap are null together")
        for key in ("poi_ids", "area_poi_ids"):
            ids = getattr(self, key)
            if len(set(ids)) != len(ids):
                raise ValueError(f"{key} has duplicates")
        return self


class BuildingsDocument(_StrictJsonModel):
    schema_version: Literal[2]
    map_id: str
    buildings: list[BuildingEntry]

    _version = field_validator("schema_version", mode="before")(_not_a_bool)


# pois.json


Ring = Annotated[list[Vec2], Field(min_length=4)]


Outline = Annotated[list[Annotated[list[Ring], Field(min_length=1)]], Field(min_length=1)]


class PoiEntry(_StrictJsonModel):
    id: PoiId
    name: str | None
    name_sv: str | None
    ref: str | None
    type: PoiType = Field(description="the first of shop, amenity, office, tourism, leisure")
    subtype: str = Field(description="that tag's value")
    other_tags: list[str] = Field(description="further POI tags as sorted key=value strings")
    geometry: Literal["point", "area"]
    position: Vec3 = Field(description="[x, y, z] Godot local metres, y = terrain height")
    outline: Outline | None = Field(
        description="areas: polygons of closed rings of [x, z] (exterior first); null for points"
    )
    address: Address
    address_source: Literal["osm", "building"] | None
    building_id: BuildingId | None
    contains_building_ids: list[BuildingId]

    @field_validator("outline")
    @classmethod
    def _closed_rings(cls, value: list[list[list[Vec2]]] | None) -> Any:
        if value is not None and not value:
            raise ValueError("an outline needs at least one polygon")
        for polygon in value or []:
            if not polygon:
                raise ValueError("an outline polygon needs an exterior ring")
            for ring in polygon:
                if ring[0] != ring[-1]:
                    raise ValueError("outline rings must be closed")
        return value

    @model_validator(mode="after")
    def _consistent(self) -> "PoiEntry":
        if (self.geometry == "point") != (self.outline is None):
            raise ValueError("outline is null exactly for points")
        if self.geometry == "point" and self.contains_building_ids:
            raise ValueError("a point contains no buildings")
        has_address = self.address.street is not None or self.address.housenumber is not None
        if (self.address_source is None) == has_address:
            raise ValueError("address_source is null exactly when the address is empty")
        if self.address_source == "building" and self.building_id is None:
            raise ValueError("address_source building needs a building_id")
        ids = self.contains_building_ids
        if len(set(ids)) != len(ids):
            raise ValueError("contains_building_ids has duplicates")
        return self


class PoisDocument(_StrictJsonModel):
    schema_version: Literal[1]
    map_id: str
    area: Literal["full", "test"]
    pois: list[PoiEntry]

    _version = field_validator("schema_version", mode="before")(_not_a_bool)


# maps/<map-id>/overrides/buildings.json


class BuildingOverride(_StrictJsonModel):
    """A category and/or name override. Setting any name field sets all three (missing
    ones become null). `source` and `licence` record where the fix came from."""

    category: Category = _optional()
    name: str | None = _optional()
    name_sv: str | None = _optional()
    ref: str | None = _optional()
    source: str = Field(min_length=1)
    licence: str = Field(
        description=f"{OWN_WORK!r} for the project's own work, else a licence mapgen notice "
        "knows (third-party data, credited in the manifest)",
        json_schema_extra={"enum": list(OVERRIDE_LICENCES)},
    )
    attribution: str = _optional(min_length=1, description="third-party entries only")
    retrieved_at: str = _optional(
        pattern=r"^[0-9]{4}-[0-9]{2}-[0-9]{2}$",
        description="YYYY-MM-DD; third-party entries only",
    )

    model_config = ConfigDict(
        extra="forbid",
        frozen=True,
        strict=True,
        allow_inf_nan=False,
        json_schema_extra={
            "if": {"properties": {"licence": {"const": OWN_WORK}}},
            "then": {
                "not": {"anyOf": [{"required": ["attribution"]}, {"required": ["retrieved_at"]}]}
            },
            "else": {"required": ["attribution", "retrieved_at"]},
            "anyOf": [{"required": [key]} for key in ("category", "name", "name_sv", "ref")],
        },
    )

    @model_validator(mode="after")
    def _something_to_override(self) -> "BuildingOverride":
        if not self.model_fields_set & {"category", "name", "name_sv", "ref"}:
            raise ValueError("an override needs a category or a name field")
        if self.licence not in OVERRIDE_LICENCES:
            known = ", ".join(repr(x) for x in OVERRIDE_LICENCES)
            raise ValueError(f"unknown licence {self.licence!r} (known: {known})")
        third_party = {"attribution", "retrieved_at"} & self.model_fields_set
        if self.licence == OWN_WORK:
            if third_party:
                raise ValueError(f"attribution and retrieved_at aren't allowed on {OWN_WORK!r}")
        else:
            if third_party != {"attribution", "retrieved_at"}:
                raise ValueError("a third-party override needs attribution and retrieved_at")
            try:
                date.fromisoformat(self.retrieved_at)
            except ValueError:
                raise ValueError(f"invalid retrieved_at {self.retrieved_at!r}") from None
        return self

    @property
    def is_own_work(self) -> bool:
        return self.licence == OWN_WORK

    @property
    def sets_names(self) -> bool:
        return bool(self.model_fields_set & {"name", "name_sv", "ref"})


class BuildingOverrides(_StrictJsonModel):
    schema_version: Literal[1]
    buildings: dict[BuildingId, BuildingOverride] = Field(
        description="building id (osm:way/<id>, osm:relation/<id>) or MML id "
        "(mml:rakennus/<mtk_id>) -> override",
        json_schema_extra={"additionalProperties": False},  # keys must match the pattern
    )

    _version = field_validator("schema_version", mode="before")(_not_a_bool)


# Committed schemas.


@dataclass(frozen=True)
class CommittedSchema:
    model: type[BaseModel]
    file: str

    def path(self) -> Path:
        return Path(str(resources.files("mapgen.build").joinpath("schemas", self.file)))

    def generated(self) -> dict[str, Any]:
        schema = self.model.model_json_schema()
        return {
            "$schema": "https://json-schema.org/draft/2020-12/schema",
            "$id": self.file,
            **schema,
        }

    def committed(self) -> dict[str, Any]:
        return json.loads(self.path().read_text(encoding="utf-8"))

    def write(self, path: Path | None = None) -> Path:
        target = path or self.path()
        target.write_text(json.dumps(self.generated(), indent=2) + "\n", encoding="utf-8")
        return target

    def validate(self, document: Any, what: str) -> None:
        """Validate a document against the model and the committed schema."""
        try:
            self.model.model_validate_json(json.dumps(document))  # JSON mode: arrays
            jsonschema.validate(document, self.committed())
        except jsonschema.ValidationError as exc:
            raise PlacesError(f"{what} does not match {self.file}: {exc.message}") from exc
        except PydanticValidationError as exc:
            raise PlacesError(f"invalid {what}: {exc}") from exc

    def validate_file(self, path: Path) -> None:
        self.validate(json.loads(path.read_text(encoding="utf-8")), path.name)


BUILDINGS_SCHEMA = CommittedSchema(BuildingsDocument, "buildings-v2.schema.json")
POIS_SCHEMA = CommittedSchema(PoisDocument, "pois-v1.schema.json")
OVERRIDES_SCHEMA = CommittedSchema(BuildingOverrides, "building-overrides-v1.schema.json")
SCHEMAS = (BUILDINGS_SCHEMA, POIS_SCHEMA, OVERRIDES_SCHEMA)


def write_schemas() -> list[Path]:
    return [schema.write() for schema in SCHEMAS]
