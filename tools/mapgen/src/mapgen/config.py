"""The `maps/<map-id>/map.yaml` schema and loader."""

import re
from datetime import date
from pathlib import Path
from typing import Any, ClassVar

import yaml
from pydantic import (
    BaseModel,
    ConfigDict,
    Field,
    ValidationError,
    ValidationInfo,
    field_validator,
    model_validator,
)
from pyproj import CRS
from pyproj.exceptions import CRSError

from mapgen import geo

SLUG_PATTERN = r"^[a-z0-9]+(?:[-_][a-z0-9]+)*$"
REPO_PATTERN = r"^[A-Za-z0-9-]+/[A-Za-z0-9_-][A-Za-z0-9._-]*$"  # <owner>/<name>, no leading .
_TAG_DATE_RE = re.compile(r"-(\d{4}-\d{2}-\d{2})$")


class ConfigError(ValueError):
    """A map config is missing or invalid."""


class StrictModel(BaseModel):
    model_config = ConfigDict(extra="forbid", frozen=True)


class BBoxWGS84(StrictModel):
    lat_min: float = Field(ge=-90, le=90)
    lat_max: float = Field(ge=-90, le=90)
    lon_min: float = Field(ge=-180, le=180)
    lon_max: float = Field(ge=-180, le=180)

    @model_validator(mode="after")
    def _min_below_max(self) -> "BBoxWGS84":
        if self.lat_min >= self.lat_max:
            raise ValueError("lat_min must be less than lat_max")
        if self.lon_min >= self.lon_max:
            raise ValueError("lon_min must be less than lon_max")
        return self

    def projected(self, crs: str) -> geo.BBox:
        return geo.projected_bbox(self.lon_min, self.lat_min, self.lon_max, self.lat_max, crs)

    def centre_in(self, crs: str) -> tuple[float, float]:
        return geo.bbox_centre_in_map(self.lon_min, self.lat_min, self.lon_max, self.lat_max, crs)


class Origin(StrictModel):
    """The local origin in metres in the map CRS (ADR 0003)."""

    x: float
    y: float


class TestArea(StrictModel):
    """A square of `size_m` metres centred on the origin, used by fast tests."""

    __test__: ClassVar[bool] = False  # not a pytest test class

    size_m: float = Field(gt=0)


class SourceConfig(StrictModel):
    """One data source. `options` are validated by the adapter named in `adapter`.

    `debug_only` sources (e.g. the orthophoto for the debug view) are skipped by
    `download --release` and `build --release`, so release packs never contain their data.
    """

    adapter: str = Field(min_length=1)
    enabled: bool = True
    debug_only: bool = False
    options: dict[str, Any] = Field(default_factory=dict)


class SnapshotConfig(StrictModel):
    """The pinned raw data snapshot that release builds fetch (`mapgen snapshot fetch`).

    The archive is the single asset of the GitHub release `release_tag` in `repo`. That the
    tag names this map is checked by `MapConfig`, which knows the map id.
    """

    repo: str = Field(pattern=REPO_PATTERN)
    release_tag: str = Field(min_length=1)
    asset: str = Field(min_length=1)
    sha256: str = Field(pattern=r"^[0-9a-f]{64}$")

    @field_validator("release_tag")
    @classmethod
    def _tag_has_a_real_date(cls, value: str) -> str:
        match = _TAG_DATE_RE.search(value)
        if not match:
            raise ValueError(f"release_tag {value!r} must end in -YYYY-MM-DD")
        try:
            date.fromisoformat(match.group(1))
        except ValueError:
            raise ValueError(f"release_tag {value!r} has no valid date") from None
        return value

    @field_validator("asset")
    @classmethod
    def _asset_is_a_plain_zip_name(cls, value: str) -> str:
        if "/" in value or "\\" in value or ".." in value:
            raise ValueError(f"asset {value!r} must be a plain file name (no /, \\ or ..)")
        if not re.fullmatch(r"[A-Za-z0-9][A-Za-z0-9._-]*\.zip", value):
            raise ValueError(f"asset {value!r} must be a .zip file name of letters, digits, ._-")
        return value

    def asset_url(self) -> str:
        """The public download URL of the asset (no `gh`, key or login needed)."""
        return f"https://github.com/{self.repo}/releases/download/{self.release_tag}/{self.asset}"


class BridgeConfig(StrictModel):
    """Bridge deck geometry, the clearance check and the waterways that imply a bridge."""

    deck_thickness_m: float = Field(gt=0)
    railing_height_m: float = Field(gt=0)
    railing_width_m: float = Field(gt=0)
    min_clearance_m: float = Field(ge=0)  # deck underside above terrain; below: a warning
    # Road side of each deck end: the surface blends from the deck end to the terrain.
    ramp_length_m: float = Field(gt=0)
    # `waterway` values whose lines, crossed by a way without a bridge tag, imply a bridge.
    inferred_waterways: list[str]


class RoadClassConfig(StrictModel):
    """Defaults for one road class (a `highway` value, or `driveway` / `parking_aisle`)."""

    width_m: float = Field(gt=0)
    lanes: int = Field(ge=1)
    drivable: bool
    walkable: bool
    speed_kmh: int | None = Field(default=None, gt=0)  # drivable only; else default_speed_kmh


class RoadsConfig(StrictModel):
    """The `roads` block: every road default is per map, none is in the pipeline."""

    surface_offset_m: float = Field(gt=0)  # road surface height above the terrain mesh
    default_speed_kmh: int = Field(gt=0)
    bridge: BridgeConfig
    classes: dict[str, RoadClassConfig]

    @field_validator("classes")
    @classmethod
    def _class_names_are_slugs(
        cls, value: dict[str, RoadClassConfig]
    ) -> dict[str, RoadClassConfig]:
        for name in value:
            if not re.match(SLUG_PATTERN, name):
                raise ValueError(f"road class name {name!r} must be a lowercase slug")
        return value


class MapConfig(StrictModel):
    id: str = Field(pattern=SLUG_PATTERN)
    display_name: str = Field(min_length=1)
    bbox_wgs84: BBoxWGS84
    crs: str
    origin: Origin
    tile_size_m: int = Field(gt=0)
    test_area: TestArea
    style_preset: str = Field(min_length=1)
    sources: dict[str, SourceConfig] = Field(min_length=1)
    snapshot: SnapshotConfig | None = None
    roads: RoadsConfig | None = None  # without it, the map builds without roads

    @field_validator("crs")
    @classmethod
    def _crs_is_projected(cls, value: str) -> str:
        try:
            crs = CRS.from_user_input(value)
        except CRSError as exc:
            raise ValueError(f"unknown CRS {value!r}") from exc
        if not crs.is_projected:
            raise ValueError(f"CRS {value!r} is not projected; the map CRS must be metric")
        return value

    @field_validator("sources")
    @classmethod
    def _source_names_are_slugs(cls, value: dict[str, SourceConfig]) -> dict[str, SourceConfig]:
        for name in value:
            if not re.match(SLUG_PATTERN, name):
                raise ValueError(f"source name {name!r} must be a lowercase slug")
        return value

    @model_validator(mode="after")
    def _check_geometry_and_id(self, info: ValidationInfo) -> "MapConfig":
        context = info.context or {}
        folder_id = context.get("map_id")
        if folder_id is not None and folder_id != self.id:
            raise ValueError(f"id {self.id!r} does not match the map folder name {folder_id!r}")
        bbox = self.projected_bbox
        if not bbox.contains_point(self.origin.x, self.origin.y):
            raise ValueError(
                f"origin ({self.origin.x}, {self.origin.y}) is outside the projected bbox "
                f"{[round(v, 1) for v in bbox.as_list()]}"
            )
        if not bbox.contains(self.test_area_bbox):
            raise ValueError("test_area does not fit inside the bbox around the origin")
        if self.snapshot is not None:
            tag = self.snapshot.release_tag
            if not re.fullmatch(rf"data-{re.escape(self.id)}-\d{{4}}-\d{{2}}-\d{{2}}", tag):
                raise ValueError(f"snapshot.release_tag {tag!r} must be data-{self.id}-YYYY-MM-DD")
        return self

    @property
    def projected_bbox(self) -> geo.BBox:
        """The WGS84 bbox in the map CRS (envelope of the densified edges)."""
        return self.bbox_wgs84.projected(self.crs)

    @property
    def test_area_bbox(self) -> geo.BBox:
        """The test area in the map CRS (world coordinates)."""
        return geo.square_around(self.origin.x, self.origin.y, self.test_area.size_m)

    def enabled_sources(self) -> dict[str, SourceConfig]:
        return {name: src for name, src in self.sources.items() if src.enabled}

    def debug_only_sources(self) -> list[str]:
        """Names of the enabled sources marked `debug_only`, in map.yaml order."""
        return [name for name, src in self.enabled_sources().items() if src.debug_only]

    def for_release(self) -> "MapConfig":
        """This config with every `debug_only` source disabled (for release builds)."""
        sources = {
            name: src.model_copy(update={"enabled": False}) if src.debug_only else src
            for name, src in self.sources.items()
        }
        return self.model_copy(update={"sources": sources})


def read_yaml(path: Path) -> dict[str, Any]:
    if not path.is_file():
        raise ConfigError(f"map config not found: {path}")
    try:
        data = yaml.safe_load(path.read_text(encoding="utf-8"))
    except yaml.YAMLError as exc:
        raise ConfigError(f"{path}: invalid YAML: {exc}") from exc
    if not isinstance(data, dict):
        raise ConfigError(f"{path}: expected a mapping at the top level")
    return data


def load_map_config(path: Path) -> MapConfig:
    """Load and validate `maps/<map-id>/map.yaml`, including each source's adapter options."""
    data = read_yaml(path)
    try:
        config = MapConfig.model_validate(data, context={"map_id": path.parent.name})
    except ValidationError as exc:
        raise ConfigError(f"{path}: {exc}") from exc

    from mapgen.sources.registry import validate_source_options

    validate_source_options(config, path)
    return config
