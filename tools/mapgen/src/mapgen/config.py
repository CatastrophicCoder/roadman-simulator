"""The `maps/<map-id>/map.yaml` schema and loader."""

import re
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
