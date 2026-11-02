"""The map pack's `boundary.json` (schema version 1) and its committed JSON Schema.

Godot local metres: points are `[x, y, z]` (+X east, +Y up, -Z north) and footprints
`[x, z]`. `s` values are metres along the boundary ring from its first point, in ring
order (counter-clockwise seen from above). An item spanning the ring's first point has
`s_end > boundary.length_m` (`s_end - s_start` is always its length). The file isn't listed
in the manifest. The schema in `schemas/boundary-v1.schema.json` is generated from
`BoundaryDocument` and committed; a test checks that they match. After a model change,
regenerate it with:

    uv run python -c "from mapgen.build.world_edge.model import BOUNDARY_SCHEMA; \\
BOUNDARY_SCHEMA.write()"
"""

from typing import Annotated, Any, Literal

from pydantic import BaseModel, ConfigDict, Field, field_validator, model_validator

from mapgen.build.places.model import CommittedSchema

BOUNDARY_NAME = "boundary.json"
BOUNDARY_SCHEMA_VERSION = 1

Vec2 = tuple[float, float]
Vec3 = tuple[float, float, float]
NonNegative = Annotated[float, Field(ge=0)]


def _no_default(schema: dict[str, Any]) -> None:
    schema.pop("default", None)


class BoundaryError(Exception):
    """A boundary document doesn't match its model or committed schema."""


class _StrictJsonModel(BaseModel):
    model_config = ConfigDict(extra="forbid", frozen=True, strict=True, allow_inf_nan=False)


class Ring(_StrictJsonModel):
    ring: list[Vec2] = Field(
        min_length=3,
        description="[x, z] points in ring order, not closed (the last joins the first)",
    )
    length_m: float = Field(gt=0)
    source: Literal["area", "custom", "test_area"] = Field(
        description="area: the build area rectangle; custom: world_edge.boundary_wgs84; "
        "test_area: the test-area square (every test build)"
    )


class RoadblockNode(_StrictJsonModel):
    node: str = Field(description="an area_edge node id of road_graph.json")
    edge: str = Field(description="its edge id")
    role: Literal["spawn_despawn", "turn_around"] = Field(
        description="spawn_despawn for a drivable edge, turn_around for a walk-only edge"
    )


class Roadblock(_StrictJsonModel):
    id: str = Field(description="roadblock:<lowest node id>")
    nodes: list[RoadblockNode] = Field(min_length=1, description="sorted by node id")
    class_: str = Field(alias="class", description="the class of the widest edge")
    drivable: bool
    walkable: bool
    width_m: float = Field(gt=0, description="the widest edge's width")
    width_source: Literal["osm", "default"]
    start: Vec3 = Field(description="span start on the boundary")
    end: Vec3 = Field(description="span end on the boundary")
    position: Vec3 = Field(description="the span's midpoint on the ring")
    yaw_deg: float = Field(
        gt=-180, le=180, description="rotation about +Y that turns local +X along the ring"
    )
    outward: Vec3 = Field(description="unit [x, 0, z] pointing out of the playable area")
    s_start: NonNegative
    s_end: NonNegative
    over_water: bool

    @model_validator(mode="after")
    def _ordered(self) -> "Roadblock":
        if self.s_end <= self.s_start:
            raise ValueError("s_end must be greater than s_start")
        return self


class Fence(_StrictJsonModel):
    id: str = Field(pattern=r"^fence:[0-9]+$", description="fence:<n>, in ring order")
    type: Literal["land", "water"]
    start: Vec3
    end: Vec3
    s_start: NonNegative
    s_end: NonNegative
    outward: Vec3 = Field(description="unit [x, 0, z] pointing out of the playable area")
    # Present for water segments only (absent, never null, for land segments).
    water_ids: list[str] = Field(
        default=None,  # type: ignore[assignment]
        min_length=1,
        description="water segments only: the water areas it crosses",
        json_schema_extra=_no_default,
    )

    @model_validator(mode="after")
    def _consistent(self) -> "Fence":
        if self.s_end <= self.s_start:
            raise ValueError("s_end must be greater than s_start")
        if (self.type == "water") != (self.water_ids is not None):
            raise ValueError("water_ids is set exactly for water segments")
        return self


class Cliff(_StrictJsonModel):
    ledge_m: NonNegative
    bottom_y: float = Field(description="(lowest boundary height) - cliff.depth_m")


class Prism(_StrictJsonModel):
    footprint: list[Vec2] = Field(
        min_length=4, max_length=4, description="convex quadrilateral of [x, z] points"
    )


class VoidTrigger(_StrictJsonModel):
    bottom_y: float
    top_y: float
    prisms: list[Prism] = Field(min_length=1)

    @model_validator(mode="after")
    def _ordered(self) -> "VoidTrigger":
        if self.top_y <= self.bottom_y:
            raise ValueError("top_y must be above bottom_y")
        return self


class SpawnArea(_StrictJsonModel):
    polygon: list[Vec2] = Field(min_length=3, description="[x, z] ring, not closed")
    open_ground: list[list[list[Vec2]]] = Field(
        min_length=1,
        description="polygons of closed [x, z] rings, exterior first",
    )
    open_area_m2: float = Field(gt=0)

    @field_validator("open_ground")
    @classmethod
    def _closed_rings(cls, value: list[list[list[Vec2]]]) -> list[list[list[Vec2]]]:
        for polygon in value:
            if not polygon:
                raise ValueError("a polygon needs an exterior ring")
            for ring in polygon:
                if len(ring) < 4 or ring[0] != ring[-1]:
                    raise ValueError("rings are closed with at least 4 points")
        return value


class WaterCrossing(_StrictJsonModel):
    s_start: NonNegative
    s_end: NonNegative
    source: str = Field(min_length=1, description="the map.yaml source of the water areas")
    water_ids: list[str] = Field(min_length=1)


class BoundaryDocument(_StrictJsonModel):
    schema_version: Literal[1]
    map_id: str
    area: Literal["full", "test"]
    boundary: Ring
    roadblocks: list[Roadblock]
    fences: list[Fence]
    cliff: Cliff
    void_trigger: VoidTrigger
    spawn_area: SpawnArea
    water_crossings: list[WaterCrossing]

    @field_validator("schema_version", mode="before")
    @classmethod
    def _not_a_bool(cls, value: Any) -> Any:
        if isinstance(value, bool):
            raise ValueError("schema_version must be the integer 1")
        return value


class _BoundarySchema(CommittedSchema):
    def generated(self) -> dict[str, Any]:
        schema = self.model.model_json_schema(by_alias=True)
        return {
            "$schema": "https://json-schema.org/draft/2020-12/schema",
            "$id": self.file,
            **schema,
        }


BOUNDARY_SCHEMA = _BoundarySchema(BoundaryDocument, "boundary-v1.schema.json", BoundaryError)
