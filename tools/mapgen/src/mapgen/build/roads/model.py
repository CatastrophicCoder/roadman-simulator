"""The map pack's `road_graph.json` (schema version 1) and its committed JSON Schema.

Coordinates are Godot local metres `[x, y, z]` (+X east, +Y up, -Z north). Each edge's
`from` -> `to` is its allowed driving direction when `oneway` is true. Every named
feature has `name`, `name_sv` and `ref` (string or null). The schema in
`schemas/road-graph-v1.schema.json` is generated from `RoadGraphDocument` and committed,
so the game side can read it. A test checks that the two match. After a model change,
regenerate it with:

    uv run python -c "from mapgen.build.roads.model import write_schema; write_schema()"
"""

import json
from importlib import resources
from pathlib import Path
from typing import Any, Literal

import jsonschema
from pydantic import BaseModel, ConfigDict, Field

ROAD_GRAPH_NAME = "road_graph.json"
SCHEMA_FILE = "road-graph-v1.schema.json"
SCHEMA_VERSION = 1

Vec3 = tuple[float, float, float]
ValueSource = Literal["osm", "default"]


class RoadGraphError(Exception):
    """A road graph document doesn't match the committed schema."""


class _StrictJsonModel(BaseModel):
    model_config = ConfigDict(extra="forbid", frozen=True, strict=True, allow_inf_nan=False)


class RoadNode(_StrictJsonModel):
    id: str = Field(description="osm:node/<id>, or cut:way/<id>:<n> for an area_edge cut")
    kind: Literal["junction", "joint", "dead_end", "area_edge"]
    position: Vec3 = Field(description="[x, y, z] in Godot local metres")
    osm_node: int | None


class RoadEdge(_StrictJsonModel):
    id: str = Field(description="osm:way/<id>#<n>, n = the edge's index along the way")
    from_: str = Field(alias="from")
    to: str
    class_: str = Field(alias="class")
    drivable: bool
    drivable_source: ValueSource
    walkable: bool
    walkable_source: ValueSource
    lanes: int = Field(ge=1)
    lanes_source: ValueSource
    speed_kmh: int | None = Field(gt=0, description="null iff not drivable")
    speed_source: ValueSource | None
    width_m: float = Field(gt=0)
    width_source: ValueSource
    oneway: bool = Field(description="true: only from -> to is allowed for driving")
    bridge: bool
    bridge_source: Literal["osm", "inferred"] | None
    tunnel: bool
    layer: int
    length_m: float = Field(ge=0, description="3D length of the polyline")
    osm_way: int
    name: str | None
    name_sv: str | None
    ref: str | None
    points: list[Vec3] = Field(min_length=2)


class RoadGraphDocument(_StrictJsonModel):
    schema_version: Literal[1]
    map_id: str
    area: Literal["full", "test"]
    nodes: list[RoadNode]
    edges: list[RoadEdge]


def empty_graph_document(map_id: str, area: str) -> dict[str, Any]:
    """A valid road graph with no nodes or edges (a map without a `roads` block)."""
    return {
        "schema_version": SCHEMA_VERSION,
        "map_id": map_id,
        "area": area,
        "nodes": [],
        "edges": [],
    }


def schema_path() -> Path:
    return Path(str(resources.files("mapgen.build").joinpath("schemas", SCHEMA_FILE)))


def generated_schema() -> dict[str, Any]:
    schema = RoadGraphDocument.model_json_schema(by_alias=True)
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


def validate_document(document: dict[str, Any]) -> None:
    """Validate a document against the model and the committed schema."""
    try:
        RoadGraphDocument.model_validate_json(json.dumps(document))  # JSON mode: arrays
        jsonschema.validate(document, committed_schema())
    except jsonschema.ValidationError as exc:
        raise RoadGraphError(f"road graph does not match {SCHEMA_FILE}: {exc.message}") from exc
    except ValueError as exc:  # pydantic's ValidationError
        raise RoadGraphError(f"invalid road graph: {exc}") from exc


def validate_file(path: Path) -> None:
    """Validate a written `road_graph.json` against the committed schema."""
    validate_document(json.loads(path.read_text(encoding="utf-8")))
