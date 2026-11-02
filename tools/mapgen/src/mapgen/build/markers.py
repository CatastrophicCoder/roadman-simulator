"""The map's `maps/<map-id>/markers.json` (schema version 1) and its committed JSON Schema.

Version 1 holds only the spawn area: `spawn_area_wgs84`, one polygon as a list of
`{lat, lon}` points in degrees, without repeating the first point (the format of
`world_edge.boundary_wgs84` in map.yaml). Unknown keys are errors. The build reads it
before the load stage, like `camera_presets.json`; a missing file fails the build, because
respawn needs a spawn area in every pack. The polygon must be a valid, simple polygon with
at least 3 points lying inside the map's test area, so one spawn area serves both the test
and the full build (docs/specs/world-edge-pipeline.md, "Spawn area").

The schema in `schemas/markers-v1.schema.json` is generated from `Markers` and committed;
a test checks that the two match. After a model change, regenerate it with:

    uv run python -c "from mapgen.build.markers import MARKERS_SCHEMA; MARKERS_SCHEMA.write()"
"""

import json
from pathlib import Path
from typing import Any, Literal

from pydantic import BaseModel, ConfigDict, Field, ValidationError, field_validator
from shapely.geometry import Polygon, box

from mapgen import geo
from mapgen.build.places.model import CommittedSchema
from mapgen.config import MapConfig

MARKERS_NAME = "markers.json"
MARKERS_SCHEMA_VERSION = 1


class MarkersError(Exception):
    """The markers file is missing or invalid. The message is shown to the user."""


class _StrictJsonModel(BaseModel):
    model_config = ConfigDict(extra="forbid", frozen=True, strict=True, allow_inf_nan=False)


class LatLonPoint(_StrictJsonModel):
    lat: float = Field(ge=-90, le=90, description="latitude, degrees (WGS84)")
    lon: float = Field(ge=-180, le=180, description="longitude, degrees (WGS84)")


class Markers(_StrictJsonModel):
    schema_version: Literal[1]
    spawn_area_wgs84: list[LatLonPoint] = Field(
        min_length=3,
        description="the spawn area: one polygon, without repeating the first point",
    )

    @field_validator("schema_version", mode="before")
    @classmethod
    def _not_a_bool(cls, value: Any) -> Any:
        if isinstance(value, bool):  # True == 1 would pass the Literal check
            raise ValueError("schema_version must be the integer 1")
        return value


MARKERS_SCHEMA = CommittedSchema(Markers, "markers-v1.schema.json", MarkersError)


def parse_markers(data: bytes, source: Path | str) -> Markers:
    """Validate the bytes of a markers file against the model and the committed schema."""
    try:
        document = json.loads(data)
    except ValueError as exc:
        raise MarkersError(f"invalid markers {source}: {exc}") from exc
    try:
        MARKERS_SCHEMA.validate(document, str(source))
        return Markers.model_validate_json(data)
    except ValidationError as exc:
        raise MarkersError(f"invalid markers {source}: {exc}") from exc


def spawn_polygon(markers: Markers, config: MapConfig) -> Polygon:
    """The spawn area in local coordinates (x east, n north), checked: a valid, simple
    polygon with at least 3 points, inside the map's test area. Raises `MarkersError`."""
    coords = geo.wgs84_ring_to_local(
        [(p.lat, p.lon) for p in markers.spawn_area_wgs84],
        config.crs,
        config.origin.x,
        config.origin.y,
    )
    problem = geo.polygon_problem(coords)
    if problem is not None:
        raise MarkersError(f"spawn_area_wgs84: {problem}")
    test_area = config.test_area_bbox.offset(-config.origin.x, -config.origin.y)
    polygon = Polygon(coords)
    if not box(*test_area.as_list()).covers(polygon):
        outside = polygon.difference(box(*test_area.as_list())).area
        raise MarkersError(
            f"spawn_area_wgs84 isn't inside the test area {test_area.as_list()} (local "
            f"metres): {outside:.1f} m² of it lies outside"
        )
    return polygon


def read_markers(path: Path, config: MapConfig) -> tuple[Markers, Polygon]:
    """The validated markers file at `path` and its spawn polygon in local coordinates.

    A missing file is an error: every pack needs a spawn area.
    """
    if not path.is_file():
        raise MarkersError(
            f"{path} not found: every map needs a markers.json with a spawn area "
            "(docs/specs/world-edge-pipeline.md, 'Spawn area')"
        )
    markers = parse_markers(path.read_bytes(), path)
    return markers, spawn_polygon(markers, config)
