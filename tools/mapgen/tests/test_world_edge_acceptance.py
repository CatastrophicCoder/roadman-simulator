"""World edge, slices (a) to (c): the acceptance criteria on whole builds.

Criterion numbers (`acN`) refer to docs/specs/world-edge-pipeline.md. Slice (a) covers
criteria 1-13, 15, 16 (default rectangle only), 18, 19 and 21; slice (b) the custom
polygon (17 and the custom half of 16); slice (c) the cliff meshes (14; 20 is the
screenshot). The unit tests on synthetic rings are in test_world_edge.py and
test_world_edge_cliff.py; this file adds what they don't cover.

Four kinds of build, all fast:

- `somero_area` (conftest): the Somero test area from the committed fixture, as CI
  builds it.
- `fixture_build` (conftest): the testtown fixture, a map without a `world_edge` block.
- `edgeville`: a hand-written OSM snippet on the testtown origin with a bumpy DEM, laid
  out so that each case of the spec sits at a known place on the test-area square (see
  `edge_osm`). Expected values come from that layout, the map's `roads` block and the
  config defaults, never from guessed ranges. Run once with OSM water (the
  `mml_topographic` source disabled, its GeoPackage still in the raw folder) and once
  with MML water.
- `edge_custom`: edgeville's full build with a small bbox and a custom boundary polygon
  (see "AC16 and AC17" below); the criteria tests that take `built` run on it too.

The test-area ring is the square x, n -150..150 (local metres, n north), counter-
clockwise from (-150, -150): south edge s 0..300 (s = x + 150), east 300..600
(s = 450 + n), north 600..900 (s = 750 - x), west 900..1200 (s = 1050 - n).
"""

import copy
import json
import math
import re
import shutil
from collections.abc import Callable, Iterator, Sequence
from dataclasses import dataclass, field
from pathlib import Path
from typing import Any

import jsonschema
import numpy as np
import pytest
import shapely
import yaml
from shapely.geometry import LineString, Point, Polygon, box, shape

from conftest import (
    FIXED_NOW,
    FIXTURE_ORIGIN,
    FIXTURE_YAML,
    REPO_ROOT,
    FixtureBuild,
    SomeroArea,
    TmpRepo,
    build_somero_fixture,
    glb_primitive,
    lidar_entry,
    read_tile,
    write_fixture_raw,
    write_markers,
    write_rakennus_gpkg,
)
from mapgen import paths
from mapgen.build import BuildError, BuildReport, run_build
from mapgen.build.buildings import node_name
from mapgen.build.export_glb import TERRAIN_NODE, from_gltf, read_glb_node_names
from mapgen.build.markers import MARKERS_SCHEMA
from mapgen.build.pack_checks import (
    PackCheckError,
    check_custom_boundary,
    check_pack,
    check_world_edge,
    read_glb_meshes,
    work_heightfield,
)
from mapgen.build.places.model import POIS_NAME
from mapgen.build.roads.model import committed_schema as road_graph_schema
from mapgen.build.world_edge import roadblock_record
from mapgen.build.world_edge.model import BOUNDARY_SCHEMA
from mapgen.build.world_edge.ring import Ring
from mapgen.build.world_edge.roadblocks import EdgeNode, roadblock_spans
from mapgen.build.world_edge.void import ARC_EXCESS_M, void_quads
from mapgen.cli import EXIT_OK, main
from mapgen.config import MapConfig, WorldEdgeConfig
from mapgen.geo import map_to_wgs84
from test_roads_snippets import SNIPPET_ROADS, Osm, write_dem

SPEC = REPO_ROOT / "docs" / "specs" / "world-edge-pipeline.md"
SOMERO_MARKERS = REPO_ROOT / "maps" / "somero" / "markers.json"
MAP_ID = "edgeville"
CRS = "EPSG:3067"
HALF = 150.0  # the test area is x, n -HALF..HALF (testtown test_area.size_m 300)
MM = 1e-3
ROUNDING = 2e-6  # boundary.json rounds to 1 µm
SURFACE_TOLERANCE_M = 1e-4  # a surface this close to the ring reaches it
DEFAULTS = WorldEdgeConfig()
WIDTH = {name: c["width_m"] for name, c in SNIPPET_ROADS["classes"].items()}


def _json(path: Path) -> Any:
    return json.loads(path.read_text(encoding="utf-8"))


# The edgeville layout (local x, n). Way ids are the OSM ids.

ROAD_90 = 3  # residential across the south edge at x = -20, at 90°
ROAD_90_X = -20.0
ROAD_20 = 5  # residential across the south edge at (80, -150), 20° to the edge
ROAD_20_AT = (80.0, -HALF)
ROAD_20_DEG = 20.0
BESIDE_ROAD, BESIDE_CYCLEWAY = 1, 2  # x = -100 and a cycleway whose surface is 4 m east
BESIDE_ROAD_X = -100.0
BESIDE_GAP_M = 4.0
APART_ROAD = 4  # residential whose surface is 20 m east of ROAD_90's
APART_GAP_M = 20.0
CORNER_ROAD = 6  # residential along n = x + 2: crosses the north edge 2 m from the NE corner
TOUCHING_ROAD = 7  # residential along n = 148, x -120..-60: its surface reaches the ring
TOUCHING_N = 148.0
OSM_LAKE = 20  # natural=water, west edge n -40..20
OSM_LAKE_BOX = (-170.0, -40.0, -120.0, 20.0)
OSM_POND = 21  # natural=water, north edge x 60..80 (no MML area there)
OSM_POND_BOX = (60.0, 140.0, 80.0, 170.0)
OSM_STREAM = 22  # waterway=stream line across the north edge at x = -20
OSM_STREAM_X = -20.0
OVERHANGING = 30  # building over the east edge: 8 x 10 m outside
OVERHANGING_BOX = (138.0, 0.0, 158.0, 10.0)
NEAR_EDGE = 31  # building 1 m inside the east edge
NEAR_EDGE_BOX = (139.0, -20.0, 149.0, -10.0)
KEPT = 32  # building 3 m inside the east edge
KEPT_BOX = (120.0, 30.0, 147.0, 40.0)
FAR_RELATION = 400  # an open water multipolygon far from the boundary (left out, no error)
NEAR_RELATION = 401  # an open water multipolygon 5 m inside the west edge
# MML water (GeoPackage), local coordinates.
MML_LAKE_BOX = (-170.0, -40.0, -120.0, 25.0)  # the OSM lake's north end 5 m further
MML_RIVER_BOX = (140.0, -80.0, 170.0, -60.0)  # east edge n -80..-60, not in OSM
MML_STREAM_X = -30.0  # virtavesikapea across the north edge


def _s(x: float, n: float) -> float:
    """s of a point on the test-area ring (see the module docstring)."""
    if n == -HALF:
        return x + HALF
    if x == HALF:
        return 3 * HALF + n
    if n == HALF:
        return 5 * HALF - x
    return 7 * HALF - n


def _bumpy(x: np.ndarray, y: np.ndarray) -> np.ndarray:
    return 100.0 + 0.02 * x + 0.01 * y + 1.2 * np.sin(x / 9.0) * np.cos(y / 13.0)


def edge_osm(*, near_relation: bool = False) -> Osm:
    osm = Osm()
    residential, cycleway = {"highway": "residential"}, {"highway": "cycleway"}
    osm.way(BESIDE_ROAD, [(BESIDE_ROAD_X, -170), (BESIDE_ROAD_X, -100)], residential)
    beside_x = BESIDE_ROAD_X + WIDTH["residential"] / 2 + BESIDE_GAP_M + WIDTH["cycleway"] / 2
    osm.way(BESIDE_CYCLEWAY, [(beside_x, -170), (beside_x, -100)], cycleway)
    osm.way(ROAD_90, [(ROAD_90_X, -170), (ROAD_90_X, -100)], residential)
    apart_x = ROAD_90_X + WIDTH["residential"] + APART_GAP_M
    osm.way(APART_ROAD, [(apart_x, -170), (apart_x, -100)], residential)
    a = math.radians(ROAD_20_DEG)
    x0, n0 = ROAD_20_AT
    osm.way(
        ROAD_20,
        [
            (x0 - 40 * math.cos(a), n0 - 40 * math.sin(a)),
            (x0 + 60 * math.cos(a), n0 + 60 * math.sin(a)),
        ],
        residential,
    )
    osm.way(CORNER_ROAD, [(120, 122), (170, 172)], residential)
    osm.way(TOUCHING_ROAD, [(-120, TOUCHING_N), (-60, TOUCHING_N)], residential)

    def closed(
        way_id: int, bounds: tuple[float, float, float, float], tags: dict[str, str]
    ) -> None:
        x0, y0, x1, y1 = bounds
        ids = [osm.node(x, y) for x, y in ((x0, y0), (x1, y0), (x1, y1), (x0, y1))]
        osm.way(way_id, [*ids, ids[0]], tags)

    closed(OSM_LAKE, OSM_LAKE_BOX, {"natural": "water"})
    closed(OSM_POND, OSM_POND_BOX, {"natural": "water"})
    osm.way(OSM_STREAM, [(OSM_STREAM_X, 130), (OSM_STREAM_X, 170)], {"waterway": "stream"})
    closed(OVERHANGING, OVERHANGING_BOX, {"building": "yes"})
    closed(NEAR_EDGE, NEAR_EDGE_BOX, {"building": "yes"})
    closed(KEPT, KEPT_BOX, {"building": "yes"})
    osm.way(40, [(-60, -40), (-40, -40), (-40, -20)], {})
    osm.relations[FAR_RELATION] = ([(40, "outer")], {"type": "multipolygon", "natural": "water"})
    if near_relation:
        osm.way(41, [(-145, 60), (-130, 60), (-130, 80)], {})
        osm.relations[NEAR_RELATION] = (
            [(41, "outer")],
            {"type": "multipolygon", "natural": "water"},
        )
    return osm


def _world(geometry: Any) -> Any:
    return shapely.transform(geometry, lambda c: c + np.array(FIXTURE_ORIGIN))


def mml_water() -> dict[str, list[tuple[int, Any]] | None]:
    return {
        "jarvi": [(1, _world(box(*MML_LAKE_BOX)))],
        "virtavesialue": [(2, _world(box(*MML_RIVER_BOX)))],
        "virtavesikapea": [(3, _world(LineString([(MML_STREAM_X, 120), (MML_STREAM_X, 170)])))],
    }


@dataclass
class EdgeMap:
    root: Path
    config: MapConfig
    raw: Path
    map_dir: Path
    pack: Path
    work: Path
    logs: list[str] = field(default_factory=list)
    report: BuildReport | None = None

    def doc(self) -> dict[str, Any]:
        return _json(self.pack / "boundary.json")

    def edge_report(self) -> dict[str, Any]:
        return _json(self.work / "10-world-edge" / "report.json")

    def graph(self) -> dict[str, Any]:
        return _json(self.pack / "road_graph.json")


def setup_edge_map(
    root: Path,
    *,
    topographic: bool = False,
    world_edge: dict[str, Any] | None = None,
    markers: Sequence[tuple[float, float]] | None = None,
    water: dict[str, list[tuple[int, Any]] | None] | None = None,
    near_relation: bool = False,
    data_update: dict[str, Any] | None = None,
    osm: Osm | None = None,
    area: str = "test",
    rakennus: Sequence[tuple[float, float, float, float]] = (),
) -> EdgeMap:
    """The edgeville repo (map.yaml, markers.json, raw data), not built yet. The MML
    GeoPackage is always in the raw folder; `topographic` enables its source.
    `data_update` replaces top-level map.yaml keys, `osm` the OSM data (default
    `edge_osm`), `area` is the area the work folder is for, and `rakennus` lists
    building-use records as local boxes (x0, n0, x1, n1), numbered from 1 (feature class
    42211, use class 1)."""
    data = yaml.safe_load(FIXTURE_YAML.read_text(encoding="utf-8"))
    data["id"] = MAP_ID
    data["roads"] = copy.deepcopy(SNIPPET_ROADS)
    data["sources"]["mml_topographic"]["enabled"] = topographic
    if world_edge is not None:
        data["world_edge"] = world_edge
    data.update(copy.deepcopy(data_update or {}))
    repo = TmpRepo(root)
    (root / "tools" / "mapgen").mkdir(parents=True, exist_ok=True)
    config = repo.load(data)
    map_dir = repo.map_dir(MAP_ID)
    if markers is not None:
        write_markers(map_dir, markers)
    raw = write_fixture_raw(repo.raw_dir(MAP_ID), MAP_ID)
    (osm or edge_osm(near_relation=near_relation)).write(raw / "osm" / f"{MAP_ID}.osm")
    write_dem(raw / "mml" / "dem_2m" / "plane.tif", _bumpy)
    gpkg = "mml/topographic/edge.gpkg"
    rows = [(k, _world(box(*b)), 42211, 1) for k, b in enumerate(rakennus, start=1)]
    write_rakennus_gpkg(raw / gpkg, rows, water=mml_water() if water is None else water)
    manifest = _json(raw / "download-manifest.json")
    entry = lidar_entry(gpkg)
    entry.update(source="mml_topographic", dataset="Topographic Database")
    manifest["files"].append(entry)
    (raw / "download-manifest.json").write_text(json.dumps(manifest), encoding="utf-8")
    return EdgeMap(
        root,
        config,
        raw,
        map_dir,
        paths.pack_dir(root, MAP_ID),
        paths.work_dir(root, MAP_ID, area),
    )


def build_edge_map(root: Path, *, area: str = "test", **options: Any) -> EdgeMap:
    edge = setup_edge_map(root, area=area, **options)
    edge.report = run_build(
        edge.config,
        area,  # type: ignore[arg-type]
        edge.raw,
        edge.pack,
        edge.work,
        map_dir=edge.map_dir,
        now=lambda: FIXED_NOW,
        log=edge.logs.append,
    )
    return edge


@pytest.fixture(scope="module")
def edge_osm_build(tmp_path_factory: pytest.TempPathFactory) -> EdgeMap:
    """Edgeville with OSM water (treat as read-only)."""
    return build_edge_map(tmp_path_factory.mktemp("edge-osm"))


@pytest.fixture(scope="module")
def edge_mml_build(tmp_path_factory: pytest.TempPathFactory) -> EdgeMap:
    """Edgeville with MML water (treat as read-only)."""
    return build_edge_map(tmp_path_factory.mktemp("edge-mml"), topographic=True)


OVERRIDES = {"fence": {"max_segment_m": 7.5}, "roadblock": {"clearance_m": 2.0}}


@pytest.fixture(scope="module")
def edge_override_build(tmp_path_factory: pytest.TempPathFactory) -> EdgeMap:
    """Edgeville with two world_edge overrides (treat as read-only)."""
    return build_edge_map(tmp_path_factory.mktemp("edge-override"), world_edge=OVERRIDES)


@dataclass
class Built:
    """A built pack as the checks see it: boundary.json, the graph, the work folder."""

    name: str
    pack: Path
    work: Path
    config: MapConfig

    @property
    def doc(self) -> dict[str, Any]:
        return _json(self.pack / "boundary.json")

    @property
    def origin(self) -> tuple[float, float]:
        return (self.config.origin.x, self.config.origin.y)


BUILDS = ["somero", "edge_osm", "edge_mml", "edge_override", "edge_custom"]


@pytest.fixture(params=BUILDS)
def built(request: pytest.FixtureRequest) -> Built:
    if request.param == "somero":
        area: SomeroArea = request.getfixturevalue("somero_area")
        return Built("somero", area.pack, area.work, area.config)
    edge: EdgeMap = request.getfixturevalue(f"{request.param}_build")
    return Built(request.param, edge.pack, edge.work, edge.config)


# Geometry helpers, independent of mapgen.build.world_edge.


def _local(point: Sequence[float]) -> tuple[float, float]:
    """A Godot [x, z] or [x, y, z] point as local (x, n)."""
    return (float(point[0]), -float(point[-1]))


@dataclass
class DocRing:
    coords: list[tuple[float, float]]

    @classmethod
    def of(cls, doc: dict[str, Any]) -> "DocRing":
        return cls([_local(p) for p in doc["boundary"]["ring"]])

    @property
    def line(self) -> LineString:
        return LineString([*self.coords, self.coords[0]])

    @property
    def polygon(self) -> Polygon:
        return Polygon(self.coords)

    @property
    def length(self) -> float:
        return float(self.line.length)

    def s(self, x: float, n: float) -> float:
        value = float(self.line.project(Point(x, n)))
        return 0.0 if value >= self.length - 1e-9 else value

    def vertex_s(self) -> list[float]:
        line = self.line
        return [float(line.project(Point(c))) for c in self.coords]

    def stretches(self, geometry: Any) -> list[tuple[float, float]]:
        """The circular stretches of the ring within SURFACE_TOLERANCE_M of `geometry`."""
        hit = self.line.intersection(geometry.buffer(SURFACE_TOLERANCE_M))
        pieces = [hit] if isinstance(hit, LineString) else list(getattr(hit, "geoms", []))
        raw = []
        for piece in pieces:
            if not isinstance(piece, LineString) or piece.length == 0:
                continue
            a = float(self.line.project(Point(piece.coords[0])))
            b = a + float(piece.length)
            raw.append((a, b))
        raw.sort()
        merged: list[list[float]] = []
        for a, b in raw:
            if merged and a <= merged[-1][1] + 1e-6:
                merged[-1][1] = max(merged[-1][1], b)
            else:
                merged.append([a, b])
        if len(merged) > 1 and merged[0][0] <= 1e-6 and merged[-1][1] >= self.length - 1e-6:
            first = merged.pop(0)
            merged[-1][1] = first[1] + self.length
        return [(a, b) for a, b in merged]

    def contains(self, interval: tuple[float, float], s: float, slack: float = 0.0) -> bool:
        a, b = interval
        return any(a - slack <= s + k * self.length <= b + slack for k in (-1, 0, 1, 2))

    def covers(self, outer: tuple[float, float], inner: tuple[float, float], slack: float) -> bool:
        a, b = outer
        c, d = inner
        return any(
            a - slack <= c + k * self.length and d + k * self.length <= b + slack
            for k in (-1, 0, 1)
        )


def _surfaces(work: Path, origin: tuple[float, float]) -> list[tuple[dict[str, Any], Any]]:
    """The road, path and bridge surfaces of `04-roads/roads.geojson`, in local metres."""
    offset = np.array(origin)
    return [
        (f["properties"], shapely.transform(shape(f["geometry"]), lambda c: c - offset))
        for f in _json(work / "04-roads" / "roads.geojson")["features"]
        if f["properties"].get("surface") in ("road", "path", "bridge")
    ]


def _items(doc: dict[str, Any]) -> list[dict[str, Any]]:
    return sorted([*doc["roadblocks"], *doc["fences"]], key=lambda item: item["s_start"])


def _fence_union(doc: dict[str, Any], kind: str | None = None) -> list[tuple[float, float]]:
    return [(f["s_start"], f["s_end"]) for f in doc["fences"] if kind is None or f["type"] == kind]


def _covered(intervals: Sequence[tuple[float, float]], a: float, b: float) -> bool:
    """Whether the linear stretch a..b is covered by the (linear) intervals."""
    position = a
    for lo, hi in sorted(intervals):
        if lo <= position + MM and hi > position:
            position = hi
    return position >= b - MM


# AC1


@pytest.mark.parametrize("which", ["somero", "testtown", "edgeville"])
def test_ac1_test_area_boundary_json_validates_against_the_committed_schema(
    which: str, request: pytest.FixtureRequest
) -> None:
    if which == "somero":
        pack, expected = request.getfixturevalue("somero_area").pack, ("somero", "test")
    elif which == "testtown":
        pack, expected = request.getfixturevalue("fixture_build").pack_dir, ("testtown", "test")
    else:
        pack, expected = request.getfixturevalue("edge_osm_build").pack, (MAP_ID, "test")
    document = _json(pack / "boundary.json")
    jsonschema.Draft202012Validator(_json(BOUNDARY_SCHEMA.path())).validate(document)
    assert (document["schema_version"], document["map_id"], document["area"]) == (1, *expected)
    assert document["boundary"]["source"] == "test_area"
    manifest = _json(pack / "manifest.json")
    assert "boundary.json" not in json.dumps(manifest)  # not listed in the manifest
    assert manifest["schema_version"] == 1


def test_ac1_committed_boundary_schema_rejects_unknown_keys(somero_area: SomeroArea) -> None:
    document = _json(somero_area.pack / "boundary.json")
    validator = jsonschema.Draft202012Validator(BOUNDARY_SCHEMA.committed())
    for target in (document, document["roadblocks"][0], document["fences"][0]):
        target["extra"] = 1
        assert not validator.is_valid(document)
        del target["extra"]
    assert validator.is_valid(document)


@pytest.mark.parametrize("path", [SOMERO_MARKERS, FIXTURE_YAML.parent / "markers.json"])
def test_ac1_committed_markers_files_validate_against_the_committed_schema(path: Path) -> None:
    jsonschema.Draft202012Validator(MARKERS_SCHEMA.committed()).validate(_json(path))


# AC2


def test_ac2_two_test_area_builds_write_byte_identical_boundary_json(
    somero_area: SomeroArea, tmp_path: Path
) -> None:
    again = build_somero_fixture(tmp_path)
    first = (somero_area.pack / "boundary.json").read_bytes()
    assert first and first == (again.pack / "boundary.json").read_bytes()


# AC3


def _spec_defaults() -> dict[str, str]:
    """`key -> default` from the table under "Config" in the spec."""
    text = SPEC.read_text(encoding="utf-8")
    section = text.split("### Config", 1)[1].split("###", 1)[0]
    rows = re.findall(r"^\| `([a-z_.0-9]+)` \| ([^|]+) \|", section, flags=re.MULTILINE)
    return {key: default.strip() for key, default in rows}


def _get(config: Any, dotted: str) -> Any:
    for part in dotted.split("."):
        config = getattr(config, part)
    return config


def test_ac3_every_default_matches_the_spec_table(
    tmp_repo: TmpRepo, fixture_data: dict[str, Any]
) -> None:
    table = _spec_defaults()
    assert len(table) == 16, (
        table
    )  # boundary, clearance, min feature width, 2 fence, 2 roadblock, 2 cliff, 3 void, 4 spawn
    assert "world_edge" not in fixture_data
    edge = tmp_repo.load(fixture_data).world_edge
    for key, default in table.items():
        expected = None if default == "none" else float(default)
        assert _get(edge, key) == expected, key


def test_ac3_a_map_without_the_block_builds_with_the_defaults(
    fixture_build: FixtureBuild,
) -> None:
    report = _json(fixture_build.work_dir / "10-world-edge" / "report.json")
    assert report["config"] == DEFAULTS.model_dump(mode="json")


def _flatten(value: Any, prefix: str = "") -> dict[str, Any]:
    if isinstance(value, dict):
        return {
            k: v
            for key, item in value.items()
            for k, v in _flatten(item, f"{prefix}{key}.").items()
        }
    return {prefix.rstrip("."): value}


def test_ac3_an_override_changes_only_its_values_in_the_build(
    edge_override_build: EdgeMap, edge_osm_build: EdgeMap
) -> None:
    used = _flatten(edge_override_build.edge_report()["config"])
    defaults = _flatten(DEFAULTS.model_dump(mode="json"))
    changed = {k for k in defaults if used[k] != defaults[k]}
    assert changed == {"fence.max_segment_m", "roadblock.clearance_m"}
    assert used["fence.max_segment_m"] == 7.5 and used["roadblock.clearance_m"] == 2.0
    doc = edge_override_build.doc()
    assert max(f["s_end"] - f["s_start"] for f in doc["fences"]) <= 7.5 + ROUNDING
    # Every roadblock grows by the extra clearance (1.0 m more) on each side.
    base = {rb["id"]: rb for rb in edge_osm_build.doc()["roadblocks"]}
    extra = 2.0 - DEFAULTS.roadblock.clearance_m
    for rb in doc["roadblocks"]:
        assert rb["s_start"] == pytest.approx(base[rb["id"]]["s_start"] - extra, abs=ROUNDING)
        assert rb["s_end"] == pytest.approx(base[rb["id"]]["s_end"] + extra, abs=ROUNDING)


@pytest.mark.parametrize(
    "block",
    [
        {"colour": "red"},
        {"fence": {"height_m": 1.0}},
        {"roadblock": {"merge_m": 10}},
        {"void": {"depth_m": 5}},
        {"spawn": {"radius_m": 3}},
    ],
    ids=["top-level", "fence", "roadblock", "void", "spawn"],
)
def test_ac3_unknown_world_edge_keys_fail_validation(
    tmp_repo: TmpRepo, fixture_data: dict[str, Any], block: dict[str, Any]
) -> None:
    from mapgen.config import ConfigError

    fixture_data["world_edge"] = block
    with pytest.raises(ConfigError):
        tmp_repo.load(fixture_data)


# AC4


def test_ac4_every_area_edge_node_is_in_exactly_one_roadblock(built: Built) -> None:
    graph = _json(built.pack / "road_graph.json")
    edge_nodes = sorted(n["id"] for n in graph["nodes"] if n["kind"] == "area_edge")
    in_roadblocks = sorted(n["node"] for rb in built.doc["roadblocks"] for n in rb["nodes"])
    assert edge_nodes and in_roadblocks == edge_nodes


def test_ac4_spans_cover_the_whole_surface_stretch_plus_clearance(built: Built) -> None:
    """Each roadblock covers the ring stretch of the road or path surfaces at each of its
    nodes, plus `roadblock.clearance_m` on both sides."""
    doc = built.doc
    ring = DocRing.of(doc)
    clearance = built.config.world_edge.roadblock.clearance_m
    surface = shapely.union_all([g for _, g in _surfaces(built.work, built.origin)])
    stretches = ring.stretches(surface)
    nodes = {n["id"]: n for n in _json(built.pack / "road_graph.json")["nodes"]}
    short = []
    for rb in doc["roadblocks"]:
        for item in rb["nodes"]:
            s = ring.s(*_local(nodes[item["node"]]["position"]))
            hits = [iv for iv in stretches if ring.contains(iv, s, MM)]
            if len(hits) != 1:
                short.append((rb["id"], item["node"], "surface stretches at the node", hits))
                continue
            (stretch,) = hits
            needed = (stretch[0] - clearance, stretch[1] + clearance)
            if not ring.covers((rb["s_start"], rb["s_end"]), needed, MM):
                short.append((rb["id"], item["node"], needed, (rb["s_start"], rb["s_end"])))
    assert not short, f"{len(short)} spans don't cover their surface + clearance: {short[:4]}"


def _roadblock_of(edge: EdgeMap, way_id: int) -> dict[str, Any]:
    (rb,) = [
        rb
        for rb in edge.doc()["roadblocks"]
        if any(f"way/{way_id}:" in n["node"] for n in rb["nodes"])
    ]
    return rb


def test_ac4_a_road_at_90_degrees_spans_its_width_plus_clearance(edge_osm_build: EdgeMap) -> None:
    rb = _roadblock_of(edge_osm_build, ROAD_90)
    half = WIDTH["residential"] / 2 + DEFAULTS.roadblock.clearance_m
    expected = (_s(ROAD_90_X - half, -HALF), _s(ROAD_90_X + half, -HALF))
    assert (rb["s_start"], rb["s_end"]) == pytest.approx(expected, abs=MM)


def test_ac4_a_road_at_20_degrees_spans_its_whole_slanted_stretch(edge_osm_build: EdgeMap) -> None:
    """At 20° the surface covers width / sin(20°) of the ring, centred on the crossing."""
    rb = _roadblock_of(edge_osm_build, ROAD_20)
    stretch = WIDTH["residential"] / math.sin(math.radians(ROAD_20_DEG))
    centre = _s(*ROAD_20_AT)
    clearance = DEFAULTS.roadblock.clearance_m
    expected = (centre - stretch / 2 - clearance, centre + stretch / 2 + clearance)
    assert (rb["s_start"], rb["s_end"]) == pytest.approx(expected, abs=0.05)


def test_ac4_a_crossing_at_a_corner_spans_round_the_corner(edge_osm_build: EdgeMap) -> None:
    """The corner road's surface reaches the ring on both sides of the NE corner (s 600),
    so its span runs round it; start, end and position stay on the ring."""
    rb = _roadblock_of(edge_osm_build, CORNER_ROAD)
    corner = _s(HALF, HALF)
    ring = DocRing.of(edge_osm_build.doc())
    for key in ("start", "end", "position"):
        assert ring.line.distance(Point(_local(rb[key]))) <= ROUNDING, key
    assert rb["s_start"] < corner < rb["s_end"], (rb["s_start"], rb["s_end"])
    assert _local(rb["start"])[0] == pytest.approx(HALF, abs=ROUNDING)  # east edge
    assert _local(rb["end"])[1] == pytest.approx(HALF, abs=ROUNDING)  # north edge


# AC5


def test_ac5_no_two_roadblocks_are_closer_than_the_merge_distance(built: Built) -> None:
    doc = built.doc
    length = doc["boundary"]["length_m"]
    spans = sorted((rb["s_start"], rb["s_end"]) for rb in doc["roadblocks"])
    merge = built.config.world_edge.roadblock.merge_distance_m
    for k, (_, end) in enumerate(spans):
        if len(spans) < 2:
            break
        following = spans[(k + 1) % len(spans)][0] + (length if k == len(spans) - 1 else 0.0)
        assert following - end >= merge - MM, (spans[k], spans[(k + 1) % len(spans)])


def test_ac5_a_road_and_a_cycleway_4_m_apart_are_one_roadblock_with_both_roles(
    edge_osm_build: EdgeMap,
) -> None:
    rb = _roadblock_of(edge_osm_build, BESIDE_ROAD)
    assert rb == _roadblock_of(edge_osm_build, BESIDE_CYCLEWAY)
    roles = {n["node"].split(":")[1]: n["role"] for n in rb["nodes"]}
    assert roles == {
        f"way/{BESIDE_ROAD}": "spawn_despawn",
        f"way/{BESIDE_CYCLEWAY}": "turn_around",
    }
    assert rb["drivable"] and rb["walkable"] and rb["class"] == "residential"
    assert rb["width_m"] == WIDTH["residential"]
    assert [n["node"] for n in rb["nodes"]] == sorted(n["node"] for n in rb["nodes"])
    assert rb["id"] == f"roadblock:{min(n['node'] for n in rb['nodes'])}"
    merged = edge_osm_build.edge_report()["merged_roadblocks"]
    assert {"id": rb["id"], "parts": 2, "nodes": [n["node"] for n in rb["nodes"]]} in merged


def test_ac5_roads_20_m_apart_stay_two_roadblocks(edge_osm_build: EdgeMap) -> None:
    first = _roadblock_of(edge_osm_build, ROAD_90)
    second = _roadblock_of(edge_osm_build, APART_ROAD)
    assert first["id"] != second["id"]
    assert len(first["nodes"]) == len(second["nodes"]) == 1


# AC6


def test_ac6_roles_follow_the_edge_and_the_graph_stays_at_version_1(built: Built) -> None:
    graph = _json(built.pack / "road_graph.json")
    jsonschema.validate(graph, road_graph_schema())
    assert graph["schema_version"] == 1
    edges = {e["id"]: e for e in graph["edges"]}
    for rb in built.doc["roadblocks"]:
        for item in rb["nodes"]:
            edge = edges[item["edge"]]
            assert item["node"] in (edge["from"], edge["to"])
            assert item["role"] == ("spawn_despawn" if edge["drivable"] else "turn_around")
        assert rb["drivable"] == any(edges[n["edge"]]["drivable"] for n in rb["nodes"])
        assert rb["walkable"] == any(edges[n["edge"]]["walkable"] for n in rb["nodes"])
    assert '"role"' not in (built.pack / "road_graph.json").read_text(encoding="utf-8")


# AC7


def test_ac7_roadblocks_and_fences_close_the_ring(built: Built) -> None:
    doc = built.doc
    length = DocRing.of(doc).length
    assert doc["boundary"]["length_m"] == pytest.approx(length, abs=MM)
    items = _items(doc)
    for k, item in enumerate(items):
        following = items[(k + 1) % len(items)]
        gap = math.dist(_local(item["end"]), _local(following["start"]))
        assert gap <= MM, (item["id"], following["id"], gap)
        if k + 1 < len(items):
            assert following["s_start"] == pytest.approx(item["s_end"], abs=MM)
    total = sum(item["s_end"] - item["s_start"] for item in items)
    assert total == pytest.approx(length, abs=MM)


# AC8


def test_ac8_no_fence_comes_within_1_mm_of_a_road_graph_edge(built: Built) -> None:
    graph = _json(built.pack / "road_graph.json")
    edges = [LineString([_local(p) for p in e["points"]]) for e in graph["edges"]]
    if not edges:
        pytest.skip("no road graph edges")
    tree = shapely.STRtree(edges)
    for fence in built.doc["fences"]:
        line = LineString([_local(fence["start"]), _local(fence["end"])])
        hits = tree.query(line, predicate="dwithin", distance=MM)
        assert len(hits) == 0, (fence["id"], [graph["edges"][int(i)]["id"] for i in hits])


def test_ac8_a_road_touching_the_ring_gets_no_roadblock_and_a_fence_runs_over_it(
    edge_osm_build: EdgeMap,
) -> None:
    doc = edge_osm_build.doc()
    ring = DocRing.of(doc)
    touching = [
        g
        for props, g in _surfaces(edge_osm_build.work, FIXTURE_ORIGIN)
        if f"osm:way/{TOUCHING_ROAD}" in props.get("osm_ways", [])
    ]
    (stretch,) = ring.stretches(shapely.union_all(touching))
    # The stretch is the surface's reach past n = 148 along x -120..-60 (s 810..870),
    # longer by its end caps.
    assert stretch[0] <= _s(-60.0, HALF) and stretch[1] >= _s(-120.0, HALF)
    for rb in doc["roadblocks"]:
        assert rb["s_end"] <= stretch[0] or rb["s_start"] >= stretch[1], rb["id"]
    assert _covered(_fence_union(doc, "land"), *stretch)
    (listed,) = edge_osm_build.edge_report()["touched_without_crossing"]
    assert (listed["s_start"], listed["s_end"]) == pytest.approx(stretch, abs=MM)
    assert listed["length_m"] == pytest.approx(stretch[1] - stretch[0], abs=MM)
    assert listed["covered_by_roadblock"] is None
    assert not any(
        f"way/{TOUCHING_ROAD}:" in n["node"] for rb in doc["roadblocks"] for n in rb["nodes"]
    )


def _overlap(length: float, first: tuple[float, float], second: tuple[float, float]) -> float:
    """The length two circular stretches of a ring `length` long share."""
    (a0, b0), (a1, b1) = first, second
    return max(0.0, *(min(b0, b1 + k * length) - max(a0, a1 + k * length) for k in (-1, 0, 1)))


def _fence_on_surface(built: Built) -> list[tuple[str, tuple[float, float], float]]:
    """(fence id, surface stretch, overlap) for every fence that runs over the ring stretch
    of a road, path or deck surface by more than 1 mm, except the stretches the report
    lists as touched without a crossing (the fence runs over those, criterion 8)."""
    doc = built.doc
    ring = DocRing.of(doc)
    surface = shapely.union_all([g for _, g in _surfaces(built.work, built.origin)])
    touched = [
        (t["s_start"], t["s_end"])
        for t in _json(built.work / "10-world-edge" / "report.json")["touched_without_crossing"]
    ]
    found = []
    for stretch in ring.stretches(surface):
        if any(ring.covers(t, stretch, MM) for t in touched):
            continue
        for fence in doc["fences"]:
            overlap = _overlap(ring.length, stretch, (fence["s_start"], fence["s_end"]))
            if overlap > MM:
                found.append((fence["id"], stretch, overlap))
    return found


def test_ac8_no_fence_lies_on_road_or_path_surface(built: Built) -> None:
    """Stage 4's surfaces reach the boundary across their whole width, and the roadblock
    spans cover them, so no fence runs over a road, path or deck stretch of the ring."""
    found = _fence_on_surface(built)
    assert not found, f"{len(found)} fences on road or path surface: {found[:4]}"


@pytest.mark.parametrize("way_id", [ROAD_20, CORNER_ROAD])
def test_ac8_at_an_angled_crossing_no_fence_lies_on_the_road_surface(
    edge_osm_build: EdgeMap, way_id: int
) -> None:
    """At 20° and round the NE corner the surface reaches the ring across the road's whole
    width (it isn't cut short by a flat cap at the cut node), and the fences next to the
    roadblock overlap that stretch by at most 1 mm."""
    doc = edge_osm_build.doc()
    ring = DocRing.of(doc)
    own = [
        g
        for props, g in _surfaces(edge_osm_build.work, FIXTURE_ORIGIN)
        if f"osm:way/{way_id}" in props.get("osm_ways", [])
    ]
    (stretch,) = ring.stretches(shapely.union_all(own))
    if way_id == ROAD_20:
        expected = WIDTH["residential"] / math.sin(math.radians(ROAD_20_DEG))
        assert stretch[1] - stretch[0] == pytest.approx(expected, abs=0.05)
    else:  # both sides of the corner
        assert stretch[0] < _s(HALF, HALF) < stretch[1]
    rb = _roadblock_of(edge_osm_build, way_id)
    assert ring.covers((rb["s_start"], rb["s_end"]), stretch, MM)
    for fence in doc["fences"]:
        overlap = _overlap(ring.length, stretch, (fence["s_start"], fence["s_end"]))
        assert overlap <= MM, (fence["id"], overlap)


# AC9


def _water_stretches(
    boxes: Sequence[tuple[float, float, float, float]],
) -> list[tuple[float, float]]:
    ring = DocRing([(-HALF, -HALF), (HALF, -HALF), (HALF, HALF), (-HALF, HALF)])
    return sorted(ring.stretches(shapely.union_all([box(*b) for b in boxes])))


@pytest.mark.parametrize(
    ("which", "boxes", "ids"),
    [
        (
            "edge_osm_build",
            [OSM_POND_BOX, OSM_LAKE_BOX],
            [[f"osm:way/{OSM_POND}"], [f"osm:way/{OSM_LAKE}"]],
        ),
        (
            "edge_mml_build",
            [MML_RIVER_BOX, MML_LAKE_BOX],
            [["mml:virtavesialue/2"], ["mml:jarvi/1"]],
        ),
    ],
    ids=["osm", "mml"],
)
def test_ac9_water_fences_cover_exactly_the_water_on_the_ring(
    which: str,
    boxes: list[tuple[float, float, float, float]],
    ids: list[list[str]],
    request: pytest.FixtureRequest,
) -> None:
    edge: EdgeMap = request.getfixturevalue(which)
    doc = edge.doc()
    expected = _water_stretches(boxes)
    # Account for the shrinking by SURFACE_TOLERANCE_M on both ends of the box stretches.
    expected = [(a + SURFACE_TOLERANCE_M, b - SURFACE_TOLERANCE_M) for a, b in expected]
    crossings = [s for c in doc["water_crossings"] for s in (c["s_start"], c["s_end"])]
    assert crossings == pytest.approx([s for iv in expected for s in iv], abs=MM)
    assert [c["water_ids"] for c in doc["water_crossings"]] == ids
    water = _fence_union(doc, "water")
    for (a, b), water_ids in zip(expected, ids, strict=True):
        assert _covered(water, a, b)
        assert all(
            f["water_ids"] == water_ids
            for f in doc["fences"]
            if f["type"] == "water" and a - MM <= f["s_start"] < b
        )
    for fence in doc["fences"]:
        if fence["type"] == "water":
            assert any(a - MM <= fence["s_start"] and fence["s_end"] <= b + MM for a, b in expected)
    assert not any(rb["over_water"] for rb in doc["roadblocks"])


def test_ac9_a_stream_line_gets_a_land_fence_and_a_report_entry(edge_osm_build: EdgeMap) -> None:
    doc = edge_osm_build.doc()
    s = _s(OSM_STREAM_X, HALF)
    (fence,) = [f for f in doc["fences"] if f["s_start"] <= s <= f["s_end"]]
    assert fence["type"] == "land"
    (line,) = edge_osm_build.edge_report()["water"]["lines"]
    assert (line["id"], line["source"]) == (f"osm:way/{OSM_STREAM}", "osm")
    assert line["s"] == pytest.approx(s, abs=MM)
    assert line["local"] == pytest.approx([OSM_STREAM_X, HALF], abs=MM)
    assert line["in_water_crossing"] is False


# AC10


def test_ac10_an_osm_only_map_uses_osm_areas_even_with_mml_water_in_the_raw_folder(
    edge_osm_build: EdgeMap,
) -> None:
    assert (edge_osm_build.raw / "mml" / "topographic" / "edge.gpkg").is_file()
    water = edge_osm_build.edge_report()["water"]
    assert (water["mode"], water["source"]) == ("osm", "osm")
    ids = {i for c in edge_osm_build.doc()["water_crossings"] for i in c["water_ids"]}
    assert ids == {f"osm:way/{OSM_LAKE}", f"osm:way/{OSM_POND}"}
    assert {c["source"] for c in edge_osm_build.doc()["water_crossings"]} == {"osm"}
    # The open relation far from the boundary is left out without failing the build.
    load = _json(edge_osm_build.work / "01-load" / "report.json")
    assert load["water"]["relations_left_out"] == 1
    assert water["left_out_relations_near_boundary"] == []


def test_ac10_an_osm_relation_left_out_near_the_boundary_fails_naming_it(tmp_path: Path) -> None:
    with pytest.raises(BuildError, match=f"osm:relation/{NEAR_RELATION}"):
        build_edge_map(tmp_path, near_relation=True)
    assert not (tmp_path / "game" / "maps" / MAP_ID).exists()


def test_ac10_mml_mode_uses_mml_areas(edge_mml_build: EdgeMap) -> None:
    water = edge_mml_build.edge_report()["water"]
    assert (water["mode"], water["source"]) == ("water_areas", "mml_topographic")
    assert {c["source"] for c in edge_mml_build.doc()["water_crossings"]} == {"mml_topographic"}


def test_ac9_a_missing_mml_water_table_fails_the_build_naming_it(tmp_path: Path) -> None:
    water = mml_water()
    water["virtavesikapea"] = None
    with pytest.raises(BuildError, match="virtavesikapea"):
        build_edge_map(tmp_path, topographic=True, water=water)


# AC11


def _endpoint(x: float, n: float) -> dict[str, Any]:
    wx, wy = x + FIXTURE_ORIGIN[0], n + FIXTURE_ORIGIN[1]
    lon, lat = map_to_wgs84(wx, wy, CRS)
    return {"local": [x, n], "map_crs": [wx, wy], "wgs84": {"lat": lat, "lon": lon}}


def _assert_endpoint(actual: dict[str, Any], x: float, n: float) -> None:
    expected = _endpoint(x, n)
    assert actual["local"] == pytest.approx(expected["local"], abs=MM)
    assert actual["map_crs"] == pytest.approx(expected["map_crs"], abs=MM)
    assert actual["wgs84"]["lat"] == pytest.approx(expected["wgs84"]["lat"], abs=1e-7)
    assert actual["wgs84"]["lon"] == pytest.approx(expected["wgs84"]["lon"], abs=1e-7)


def test_ac11_the_report_lists_each_water_crossing_with_length_endpoints_ids_and_source(
    edge_osm_build: EdgeMap,
) -> None:
    water = edge_osm_build.edge_report()["water"]
    pond, lake = water["crossings"]
    # The pond on the north edge runs from x 80 to 60 (ring order), the lake on the west
    # edge from n 20 to -40.
    for entry, (x0, n0), (x1, n1), wid in (
        (pond, (OSM_POND_BOX[2], HALF), (OSM_POND_BOX[0], HALF), OSM_POND),
        (lake, (-HALF, OSM_LAKE_BOX[3]), (-HALF, OSM_LAKE_BOX[1]), OSM_LAKE),
    ):
        assert entry["length_m"] == pytest.approx(math.dist((x0, n0), (x1, n1)), abs=MM)
        assert entry["s_start"] == pytest.approx(_s(x0, n0), abs=MM)
        assert entry["s_end"] == pytest.approx(_s(x1, n1), abs=MM)
        _assert_endpoint(entry["start"], x0, n0)
        _assert_endpoint(entry["end"], x1, n1)
        assert (entry["water_ids"], entry["source"]) == ([f"osm:way/{wid}"], "osm")
    assert water["total_m"] == pytest.approx(pond["length_m"] + lake["length_m"], abs=MM)
    printed = [line for line in edge_osm_build.logs if "water crossing:" in line]
    assert len(printed) == 2
    assert f"water crossing: {pond['length_m']:.1f} m at" in printed[0]
    assert f"osm:way/{OSM_POND}" in printed[0]
    assert any(f"total {water['total_m']:.1f} m" in line for line in edge_osm_build.logs)


def test_ac11_mml_mode_lists_osm_areas_disagreements_and_both_sources_lines(
    edge_mml_build: EdgeMap,
) -> None:
    water = edge_mml_build.edge_report()["water"]
    river, lake = water["crossings"]
    assert river["osm_areas_on_stretch"] == []
    assert lake["osm_areas_on_stretch"] == [f"osm:way/{OSM_LAKE}"]
    kinds = {d["kind"]: d for d in water["disagreements"]}
    assert set(kinds) == {"only_in_used_source", "only_in_other_source", "ends_differ"}
    assert kinds["only_in_used_source"]["water_ids"] == ["mml:virtavesialue/2"]
    assert kinds["only_in_other_source"]["water_ids"] == [f"osm:way/{OSM_POND}"]
    differ = kinds["ends_differ"]
    assert differ["start_difference_m"] == pytest.approx(MML_LAKE_BOX[3] - OSM_LAKE_BOX[3], abs=MM)
    assert differ["end_difference_m"] == pytest.approx(0.0, abs=MM)
    lines = {(line["id"], line["source"]): line["s"] for line in water["lines"]}
    assert lines == pytest.approx(
        {
            (f"osm:way/{OSM_STREAM}", "osm"): _s(OSM_STREAM_X, HALF),
            ("mml:virtavesikapea/3", "mml_topographic"): _s(MML_STREAM_X, HALF),
        },
        abs=MM,
    )


# AC12


def test_ac12_fence_ends_sit_on_the_terrain_and_follow_it_within_the_limits(built: Built) -> None:
    heightfield = work_heightfield(built.work, built.origin)
    cfg = built.config.world_edge.fence
    t = np.linspace(0.0, 1.0, 401)
    worst = 0.0
    for fence in built.doc["fences"]:
        (x0, n0), (x1, n1) = _local(fence["start"]), _local(fence["end"])
        for (x, n), y in (((x0, n0), fence["start"][1]), ((x1, n1), fence["end"][1])):
            assert abs(y - float(heightfield.sample(x, n))) <= MM, fence["id"]
        assert math.dist((x0, n0), (x1, n1)) <= cfg.max_segment_m + MM, fence["id"]
        assert fence["s_end"] - fence["s_start"] <= cfg.max_segment_m + MM, fence["id"]
        if fence["type"] == "land":
            z = heightfield.sample(x0 + (x1 - x0) * t, n0 + (n1 - n0) * t)
            line = z[0] + (z[-1] - z[0]) * t
            deviation = float(np.abs(z - line).max())
            worst = max(worst, deviation)
            assert deviation <= cfg.max_terrain_deviation_m + 1e-9, (fence["id"], deviation)
    print(f"\n{built.name}: largest land fence deviation {worst:.4f} m")


# AC13


def test_ac13_roadblock_position_yaw_and_outward(built: Built) -> None:
    doc = built.doc
    ring = DocRing.of(doc)
    polygon = ring.polygon
    vertices = ring.vertex_s()
    for rb in doc["roadblocks"]:
        position = _local(rb["position"])
        for key in ("start", "end", "position"):
            assert ring.line.distance(Point(_local(rb[key]))) <= ROUNDING, (rb["id"], key)
        middle = (rb["s_start"] + rb["s_end"]) / 2 % ring.length
        assert ring.s(*position) == pytest.approx(middle, abs=MM)
        yaw = math.radians(rb["yaw_deg"])
        along = np.array([math.cos(yaw), -math.sin(yaw)])  # Godot (x, z) of local +X
        ahead = ring.line.interpolate((middle + 0.01) % ring.length)
        behind = ring.line.interpolate((middle - 0.01) % ring.length)
        tangent = np.array([ahead.x - behind.x, -(ahead.y - behind.y)])
        tangent /= np.linalg.norm(tangent)
        if not any(abs(middle - v) < 0.02 for v in vertices):
            assert along == pytest.approx(tangent, abs=1e-6), rb["id"]
        straight = not any(
            rb["s_start"] < v + k * ring.length < rb["s_end"] for v in vertices for k in (0, 1)
        )
        if straight:
            chord = np.array(rb["end"])[[0, 2]] - np.array(rb["start"])[[0, 2]]
            assert along == pytest.approx(chord / np.linalg.norm(chord), abs=1e-5), rb["id"]
        outward = np.array(rb["outward"])
        assert outward[1] == 0.0 and np.linalg.norm(outward) == pytest.approx(1.0, abs=1e-6)
        assert float(np.dot(outward[[0, 2]], along)) == pytest.approx(0.0, abs=1e-6)
        out = Point(position[0] + outward[0], position[1] - outward[2])
        back = Point(position[0] - outward[0], position[1] + outward[2])
        assert not polygon.contains(out) and polygon.distance(out) > 0.5, rb["id"]
        assert polygon.contains(back), rb["id"]


def test_ac13_a_corner_record_keeps_its_points_on_the_ring() -> None:
    """A span over a vertex (unit level): start, end and position on the ring, position +
    outward outside, the yaw along the ring at the position."""
    from mapgen.build.heightfield import Heightfield

    ring = Ring.rectangle(0, 0, 200, 100)
    node = EdgeNode("a", "e:a", 200, 98, 0.0, True, True, "residential", 6.0, "default")
    surface = LineString([(190, 88), (210, 108)]).buffer(3.0, cap_style="flat")
    (span,) = roadblock_spans(ring, [node], surface, 1.0, 15.0)
    assert span.a < 300 < span.b  # vertex (200, 100) is at s 300
    heightfield = Heightfield(np.full((3, 3), 50.0), -10.0, -10.0, 150.0, 150.0)
    record = roadblock_record(ring, span, heightfield, 0.05, [])
    polygon = ring.polygon()
    for key in ("start", "end", "position"):
        assert ring.line().distance(Point(_local(record[key]))) <= ROUNDING, key
        assert record[key][1] == pytest.approx(50.05)
    assert _local(record["start"])[0] == pytest.approx(200.0)  # east edge
    assert _local(record["end"])[1] == pytest.approx(100.0)  # north edge
    position, outward = _local(record["position"]), record["outward"]
    assert not polygon.contains(Point(position[0] + outward[0], position[1] - outward[2]))


# AC15


INLET = [(0, 0), (200, 0), (200, 100), (110, 100), (110, 30), (100, 30), (100, 100), (0, 100)]
INLET_NARROW = [
    (0, 0),
    (200, 0),
    (200, 100),
    (104, 100),
    (104, 20),
    (100, 20),
    (100, 100),
    (0, 100),
]
SHARP = [(0, 0), (120, 0), (130, 80), (60, 10), (0, 60)]  # a sharp convex tip and a deep notch


def _band_points(
    polygon: Polygon, inner: float, width: float, count: int, rng: np.random.Generator
) -> np.ndarray:
    line = polygon.exterior
    vertices = shapely.multipoints(list(line.coords))
    min_x, min_y, max_x, max_y = polygon.bounds
    reach = inner + width
    chosen: list[np.ndarray] = []
    total = 0
    while total < count:
        xy = np.column_stack(
            [
                rng.uniform(min_x - reach, max_x + reach, 50_000),
                rng.uniform(min_y - reach, max_y + reach, 50_000),
            ]
        )
        points = shapely.points(xy)
        distance = shapely.distance(points, line)
        # The band the prisms cover: from inner, or from inner + ARC_EXCESS_M within
        # inner + ARC_EXCESS_M of a ring vertex (ADR 0015 amendment of 2026-11-04).
        near_vertex = shapely.distance(points, vertices) < inner + ARC_EXCESS_M + 1e-6
        start = np.where(near_vertex, inner + ARC_EXCESS_M, inner)
        keep = (distance > start + 1e-6) & (distance < reach - 1e-6)
        keep &= ~shapely.contains_xy(polygon, xy[:, 0], xy[:, 1])
        chosen.append(xy[keep])
        total += int(keep.sum())
    return np.vstack(chosen)[:count]


def _inside_points(polygon: Polygon, count: int, rng: np.random.Generator) -> np.ndarray:
    min_x, min_y, max_x, max_y = polygon.bounds
    chosen: list[np.ndarray] = []
    total = 0
    while total < count:
        xy = np.column_stack([rng.uniform(min_x, max_x, 50_000), rng.uniform(min_y, max_y, 50_000)])
        keep = shapely.contains_xy(polygon, xy[:, 0], xy[:, 1])
        chosen.append(xy[keep])
        total += int(keep.sum())
    return np.vstack(chosen)[:count]


def _check_prisms(
    polygon: Polygon,
    footprints: Sequence[Sequence[tuple[float, float]]],
    inner: float,
    width: float,
    heights: tuple[float, float],
) -> None:
    """Every footprint is a valid convex quadrilateral, 10,000 band points at heights
    between bottom_y and top_y each lie in a prism, and no prism holds any of 10,000
    points inside the boundary. All problems are reported together."""
    rng = np.random.default_rng(15)
    problems: list[str] = []
    # A self-intersecting quad is read as the area it encloses (make_valid).
    shapes = [shapely.make_valid(Polygon(fp)) for fp in footprints]
    for k, fp in enumerate(footprints):
        raw = Polygon(fp)
        if not raw.is_valid or raw.area <= 0:
            problems.append(f"footprint {k} isn't a valid polygon: {list(fp)}")
        elif raw.convex_hull.area - raw.area > 1e-9 * max(raw.area, 1.0):
            problems.append(f"footprint {k} isn't convex: {list(fp)}")
    xy = _band_points(polygon, inner, width, 10_000, rng)
    y = rng.uniform(*heights, len(xy))
    tree = shapely.STRtree([s.buffer(1e-6, join_style="mitre") for s in shapes])
    hit, _ = tree.query(shapely.points(xy), predicate="intersects")
    in_some = np.zeros(len(xy), dtype=bool)
    in_some[np.unique(hit)] = True
    in_some &= (y >= heights[0]) & (y <= heights[1])  # every prism spans bottom..top
    if not in_some.all():
        problems.append(
            f"{int((~in_some).sum())} band points in no prism, e.g. {xy[~in_some][:3].tolist()}"
        )
    inside = _inside_points(polygon, 10_000, rng)
    for k, shape_ in enumerate(shapes):
        hits = shapely.contains_xy(shape_, inside[:, 0], inside[:, 1])
        if hits.any():
            over = shape_.intersection(polygon).area
            problems.append(
                f"footprint {k} holds {int(hits.sum())} points inside the boundary "
                f"({over:.1f} m² over the playable area), e.g. {inside[hits][:2].tolist()}"
            )
    assert not problems, "\n".join(problems)


@pytest.mark.parametrize(
    "coords", [INLET, INLET_NARROW, SHARP], ids=["inlet-10m", "inlet-4m", "sharp-notch"]
)
def test_ac15_band_samples_lie_in_prisms_and_no_prism_reaches_inside(coords: Any) -> None:
    ring = Ring.from_coords(coords)
    inner, width = DEFAULTS.void.inner_offset_m, DEFAULTS.void.width_m
    quads = void_quads(ring, inner, width)
    _check_prisms(ring.polygon(), quads, inner, width, (0.0, 100.0))


def test_ac15_the_void_trigger_of_a_build_covers_its_band(built: Built) -> None:
    doc = built.doc
    void = doc["void_trigger"]
    cfg = built.config.world_edge
    footprints = [[_local(p) for p in prism["footprint"]] for prism in void["prisms"]]
    _check_prisms(
        DocRing.of(doc).polygon,
        footprints,
        cfg.void.inner_offset_m,
        cfg.void.width_m,
        (void["bottom_y"], void["top_y"]),
    )
    # Heights: the terrain along the whole ring (sampled every 5 cm) and the roadblock
    # ends (at road height). top_y = highest + top_margin_m, cliff bottom_y = lowest -
    # depth_m, void bottom_y = cliff bottom_y - width_m.
    ring = DocRing.of(doc)
    heightfield = work_heightfield(built.work, built.origin)
    s = np.arange(0.0, ring.length, 0.05)
    points = [ring.line.interpolate(v) for v in s]
    terrain = heightfield.sample(np.array([p.x for p in points]), np.array([p.y for p in points]))
    ends = [p[1] for rb in doc["roadblocks"] for p in (rb["start"], rb["end"])]
    high, low = max(float(terrain.max()), *ends), min(float(terrain.min()), *ends)
    assert void["top_y"] == pytest.approx(high + cfg.void.top_margin_m, abs=0.01)
    assert doc["cliff"]["bottom_y"] == pytest.approx(low - cfg.cliff.depth_m, abs=0.01)
    assert void["bottom_y"] == pytest.approx(doc["cliff"]["bottom_y"] - cfg.void.width_m, abs=MM)
    assert doc["cliff"]["ledge_m"] == cfg.cliff.ledge_m


# AC16 (default rectangle)


def test_ac16_buildings_crossing_the_rectangle_are_dropped_and_listed(
    edge_osm_build: EdgeMap,
) -> None:
    kept = {b["id"] for b in _json(edge_osm_build.pack / "buildings.json")["buildings"]}
    assert f"osm:way/{KEPT}" in kept
    assert not {f"osm:way/{OVERHANGING}", f"osm:way/{NEAR_EDGE}"} & kept
    overhang = box(*OVERHANGING_BOX).difference(box(-HALF, -HALF, HALF, HALF)).area
    expected = [
        {
            "id": f"osm:way/{OVERHANGING}",
            "source": "osm",
            "area_outside_m2": pytest.approx(overhang, abs=MM),
            "distance_m": pytest.approx(0.0, abs=MM),
        },
        {
            "id": f"osm:way/{NEAR_EDGE}",
            "source": "osm",
            "area_outside_m2": pytest.approx(0.0, abs=MM),
            "distance_m": pytest.approx(HALF - NEAR_EDGE_BOX[2], abs=MM),
        },
    ]
    assert edge_osm_build.edge_report()["dropped_buildings"] == expected
    buildings_report = _json(edge_osm_build.work / "03-buildings" / "report.json")
    assert buildings_report["world_edge"]["dropped_buildings"] == expected
    assert edge_osm_build.report is not None and edge_osm_build.report.dropped_buildings == 2


def test_ac16_no_building_in_the_somero_test_area_pack_crosses_its_boundary(
    somero_area: SomeroArea,
) -> None:
    origin = np.array([somero_area.config.origin.x, somero_area.config.origin.y])
    footprints = {
        f["properties"]["id"]: shapely.transform(shape(f["geometry"]), lambda c: c - origin)
        for f in _json(somero_area.work / "03-buildings" / "buildings.geojson")["features"]
    }
    polygon = DocRing.of(_json(somero_area.pack / "boundary.json")).polygon
    clearance = somero_area.config.world_edge.building_clearance_m
    buildings = _json(somero_area.pack / "buildings.json")["buildings"]
    assert buildings
    for building in buildings:
        footprint = footprints[building["id"]]
        assert polygon.contains(footprint), building["id"]
        assert footprint.distance(polygon.exterior) >= clearance, building["id"]
    dropped = {
        d["id"]
        for d in _json(somero_area.work / "10-world-edge" / "report.json")["dropped_buildings"]
    }
    assert dropped and not dropped & {b["id"] for b in buildings}
    pois = _json(somero_area.pack / "pois.json")["pois"]
    linked = {p["building_id"] for p in pois} | {
        i for p in pois for i in p["contains_building_ids"]
    }
    assert not dropped & linked


# AC16 and AC17: a custom boundary polygon (slice (b))
#
# `edge_custom`: edgeville with a small bbox (a "full" area of x -202..202, n -200..200,
# 4 x 4 tiles of 128 m, so a full build stays fast) and a custom polygon inside it that
# cuts across the fixture's data: its south edge cuts the roads at n = -135, its west
# edge crosses the OSM lake at x = -140, its north edge the pond and the stream at
# n = 145, a notch (x > 130, n -30..-5) leaves out building NEAR_EDGE and a POI, and
# tile_x-2_y-2 (x -202..-128, n -200..-128) lies entirely outside it.

CUSTOM_BBOX = {"lat_min": 60.6283, "lat_max": 60.6317, "lon_min": 23.5165, "lon_max": 23.5235}
CUSTOM_AREA = (-202.0, -200.0, 202.0, 200.0)  # CUSTOM_BBOX projected, rounded outward
CUSTOM_TILE_M = 128
CUSTOM_RING = [
    (-120.0, -135.0),
    (165.0, -135.0),
    (165.0, -30.0),
    (130.0, -30.0),
    (130.0, -5.0),
    (165.0, -5.0),
    (165.0, 145.0),
    (-140.0, 145.0),
    (-140.0, -60.0),
    (-120.0, -60.0),
]
OUTSIDE_TILE = "tile_x-2_y-2"
CUSTOM_SOUTH_N = -135.0
POI_INSIDE = "Inside"  # name of an amenity=cafe node at (0, 20)
POI_OUTSIDE = "Outside"  # name of an amenity=cafe node in the notch, at (150, -15)
POI_AREA = 62  # amenity=parking area x 140..175, n 60..80: across the east edge (x 165)
POI_AREA_BOX = (140.0, 60.0, 175.0, 80.0)
# Variants crossing a building (criterion 16): the notch moved to x > 142 cuts through
# NEAR_EDGE (centroid outside); a second notch at x > 148.5, n 25..45 passes 1.5 m from
# KEPT (x 120..147), inside the 2 m clearance.
THROUGH_RING = [(142.0 if x == 130.0 else x, n) for x, n in CUSTOM_RING]
NEAR_RING = [
    *CUSTOM_RING[:6],
    (165.0, 25.0),
    (148.5, 25.0),
    (148.5, 45.0),
    (165.0, 45.0),
    *CUSTOM_RING[6:],
]


def custom_osm() -> Osm:
    osm = edge_osm()
    osm.node(0.0, 20.0, {"amenity": "cafe", "name": POI_INSIDE})
    osm.node(150.0, -15.0, {"amenity": "cafe", "name": POI_OUTSIDE})
    x0, y0, x1, y1 = POI_AREA_BOX
    ids = [osm.node(x, y) for x, y in ((x0, y0), (x1, y0), (x1, y1), (x0, y1))]
    osm.way(POI_AREA, [*ids, ids[0]], {"amenity": "parking"})
    return osm


def custom_data(ring: Sequence[tuple[float, float]] = CUSTOM_RING) -> dict[str, Any]:
    return {
        "bbox_wgs84": CUSTOM_BBOX,
        "tile_size_m": CUSTOM_TILE_M,
        "world_edge": {"boundary_wgs84": _latlon(ring)},
    }


def build_custom_map(
    root: Path,
    area: str = "full",
    ring: Sequence[tuple[float, float]] = CUSTOM_RING,
    **options: Any,
) -> EdgeMap:
    return build_edge_map(
        root, area=area, data_update=custom_data(ring), osm=custom_osm(), **options
    )


@pytest.fixture(scope="module")
def edge_custom_build(tmp_path_factory: pytest.TempPathFactory) -> EdgeMap:
    """Edgeville's full build with the custom polygon (treat as read-only)."""
    return build_custom_map(tmp_path_factory.mktemp("edge-custom"))


def _poi_by_osm(pois: Sequence[dict[str, Any]], osm_id: str) -> list[dict[str, Any]]:
    return [p for p in pois if p["id"] == osm_id]


def _poi_by_name(pois: Sequence[dict[str, Any]], name: str) -> list[dict[str, Any]]:
    return [p for p in pois if p["name"] == name]


def test_ac17_the_custom_build_uses_the_polygon_as_its_boundary(
    edge_custom_build: EdgeMap,
) -> None:
    edge = edge_custom_build
    doc = edge.doc()
    assert (doc["area"], doc["boundary"]["source"]) == ("full", "custom")
    ring = DocRing.of(doc)
    expected = Polygon(CUSTOM_RING)
    # The committed WGS84 points (7 decimals) round-trip to within 1 cm.
    assert ring.polygon.symmetric_difference(expected).area < 0.01 * expected.length
    assert len(ring.coords) == len(CUSTOM_RING)
    config = edge.config
    local = config.boundary_local()
    assert local is not None and sorted(ring.coords) == sorted(local)


def test_ac17_area_edge_nodes_lie_on_the_polygon(edge_custom_build: EdgeMap) -> None:
    ring = DocRing.of(edge_custom_build.doc())
    nodes = [n for n in edge_custom_build.graph()["nodes"] if n["kind"] == "area_edge"]
    assert nodes
    for node in nodes:
        x, n = _local(node["position"])
        assert ring.line.distance(Point(x, n)) <= MM, node["id"]
    # The polygon's own edges cut the roads: the south edge at n = -135, not the area's.
    south = sorted(
        round(_local(n["position"])[0], 1)
        for n in nodes
        if abs(_local(n["position"])[1] - CUSTOM_SOUTH_N) < 0.05
    )
    assert ROAD_90_X in south and BESIDE_ROAD_X in south
    # Every graph point is inside the polygon (1 mm).
    grown = ring.polygon.buffer(MM)
    for edge in edge_custom_build.graph()["edges"]:
        line = LineString([_local(p) for p in edge["points"]])
        assert grown.covers(line), edge["id"]


def test_ac17_no_kept_building_centroid_or_poi_lies_outside_the_polygon(
    edge_custom_build: EdgeMap,
) -> None:
    edge = edge_custom_build
    polygon = DocRing.of(edge.doc()).polygon
    origin = np.array(FIXTURE_ORIGIN)
    footprints = {
        f["properties"]["id"]: shapely.transform(shape(f["geometry"]), lambda c: c - origin)
        for f in _json(edge.work / "03-buildings" / "buildings.geojson")["features"]
    }
    kept = {b["id"] for b in _json(edge.pack / "buildings.json")["buildings"]}
    assert kept == {f"osm:way/{OVERHANGING}", f"osm:way/{KEPT}"}
    for bid in kept:
        assert polygon.contains(footprints[bid].centroid), bid
    pois = _json(edge.pack / "pois.json")["pois"]
    assert _poi_by_name(pois, POI_INSIDE)
    assert not _poi_by_name(pois, POI_OUTSIDE)
    grown = polygon.buffer(MM)
    for poi in pois:
        assert grown.covers(Point(_local(poi["position"]))), poi["id"]
        for rings in poi["outline"] or []:
            assert grown.covers(Polygon([_local(p) for p in rings[0]])), poi["id"]
    (area_poi,) = _poi_by_osm(pois, f"osm:way/{POI_AREA}")
    outline = Polygon([_local(p) for p in area_poi["outline"][0][0]])
    clipped = box(*POI_AREA_BOX).intersection(polygon)
    assert outline.symmetric_difference(clipped).area <= 0.01 * clipped.length
    assert outline.bounds[2] == pytest.approx(165.0, abs=0.02)  # cut at the east edge


def test_ac17_no_terrain_triangle_lies_outside_the_polygon(edge_custom_build: EdgeMap) -> None:
    edge = edge_custom_build
    polygon = DocRing.of(edge.doc()).polygon
    grown = polygon.buffer(MM)
    covered = 0.0
    manifest = _json(edge.pack / "manifest.json")
    for tile in manifest["tiles"]:
        vertices, faces = read_tile(edge.pack / tile["file"]).nodes[TERRAIN_NODE]
        triangles = shapely.polygons(np.asarray(vertices)[np.asarray(faces)][:, :, :2])
        outside = ~shapely.covers(grown, triangles)
        assert not outside.any(), f"{tile['id']}: {int(outside.sum())} triangles outside"
        covered += float(shapely.area(triangles).sum())
    # And the terrain covers the whole polygon (float32 vertices: 0.01 m² slack).
    assert covered == pytest.approx(polygon.area, abs=0.01)
    stats = _json(edge.work / "02-terrain" / "stats.json")["boundary_cut"]
    assert stats["totals"]["removed"] > 0 and stats["totals"]["cut"] > 0


def test_ac17_a_tile_entirely_outside_the_polygon_is_not_written(
    edge_custom_build: EdgeMap,
) -> None:
    edge = edge_custom_build
    manifest = _json(edge.pack / "manifest.json")
    ids = {t["id"] for t in manifest["tiles"]}
    files = {p.stem for p in (edge.pack / "tiles").glob("*.glb")}
    x0, y0, x1, y1 = CUSTOM_AREA
    t = CUSTOM_TILE_M
    every = {
        f"tile_x{i}_y{j}"
        for i in range(math.floor(x0 / t), math.ceil(x1 / t))
        for j in range(math.floor(y0 / t), math.ceil(y1 / t))
    }
    assert ids == files == every - {OUTSIDE_TILE}
    # The tile grid and bounds are still the build area's.
    bounds = np.array([tile["bounds_local"] for tile in manifest["tiles"]])
    assert (bounds[:, 0].min(), bounds[:, 1].min()) == (x0, y0)
    assert (bounds[:, 2].max(), bounds[:, 3].max()) == (x1, y1)
    assert edge.report is not None and edge.report.tile_count == len(every) - 1
    stats = _json(edge.work / "02-terrain" / "stats.json")["boundary_cut"]
    assert stats["tiles_not_written"] == [OUTSIDE_TILE]


def test_ac17_the_world_edge_pack_checks_pass_on_the_custom_build(
    edge_custom_build: EdgeMap,
) -> None:
    edge = edge_custom_build
    summary = check_pack(edge.pack, edge.work, edge.config.roads, world_edge=edge.config.world_edge)
    doc = edge.doc()
    assert summary.roadblocks == len(doc["roadblocks"]) > 0
    # The lake crossing on the west edge (x = -140, n -40..20) and the pond on the north
    # edge (x 60..80) are water fences; the stream is a land fence.
    ring = DocRing.of(doc)
    water = _fence_union(doc, "water")
    for (x, n0), (_, n1) in (((-140.0, 20.0), (-140.0, -40.0)),):
        a, b = ring.s(x, n0), ring.s(x, n1)
        assert _covered(water, min(a, b), max(a, b))
    a, b = ring.s(80.0, 145.0), ring.s(60.0, 145.0)
    assert _covered(water, min(a, b), max(a, b))
    lines = edge.edge_report()["water"]["lines"]
    assert [item["id"] for item in lines] == [f"osm:way/{OSM_STREAM}"]


def test_ac17_a_test_build_of_a_custom_polygon_map_uses_the_test_area_square(
    tmp_path: Path,
) -> None:
    edge = build_custom_map(tmp_path, area="test")
    doc = edge.doc()
    assert (doc["area"], doc["boundary"]["source"]) == ("test", "test_area")
    assert DocRing.of(doc).polygon.equals(box(-HALF, -HALF, HALF, HALF))
    terrain = _json(edge.work / "02-terrain" / "stats.json")
    assert "boundary_cut" not in terrain
    # The test-area rules apply: buildings crossing the square are dropped and listed.
    dropped = {d["id"] for d in edge.edge_report()["dropped_buildings"]}
    assert dropped == {f"osm:way/{OVERHANGING}", f"osm:way/{NEAR_EDGE}"}


@pytest.mark.parametrize(
    ("ring", "message"),
    [
        (THROUGH_RING, rf"osm:way/{NEAR_EDGE} \(0\.000 m from the boundary"),
        (NEAR_RING, rf"osm:way/{KEPT} \(1\.[45]\d\d m from the boundary"),
    ],
    ids=["through", "within-clearance"],
)
def test_ac16_a_custom_polygon_crossing_a_building_fails_the_build_naming_it(
    tmp_path: Path, ring: Sequence[tuple[float, float]], message: str
) -> None:
    with pytest.raises(BuildError, match=message) as info:
        build_custom_map(tmp_path, ring=ring)
    assert "1 building(s)" in str(info.value)
    assert not paths.pack_dir(tmp_path, MAP_ID).exists()


# A 2 m wide, 10 m deep notch in the west side, under min_feature_width_m (3 m).
NARROW_RING = [
    *CUSTOM_RING[:-2],
    (-140.0, 51.0),
    (-130.0, 51.0),
    (-130.0, 49.0),
    (-140.0, 49.0),
    *CUSTOM_RING[-2:],
]


def test_a_narrow_custom_boundary_fails_before_any_stage_output(tmp_path: Path) -> None:
    with pytest.raises(BuildError, match="narrower than world_edge.min_feature_width_m") as info:
        build_custom_map(tmp_path, ring=NARROW_RING)
    message = str(info.value)
    assert re.search(r"notch at x -1[34]\d\.\d\d, z -50\.\d\d \(Godot local\)", message), message
    assert "nearest boundary_wgs84 entries (0-based)" in message
    assert not paths.pack_dir(tmp_path, MAP_ID).exists()
    work = paths.work_dir(tmp_path, MAP_ID, "full")
    assert not work.exists() or not any(work.rglob("*")), sorted(work.rglob("*"))


# The notch's west side moved from x 130 to x 138.5: NEAR_EDGE (x 139..149, in the notch)
# stays outside the polygon, 0.5 m from it (WGS84 rounding moves that by a few mm), inside
# the 2 m clearance.
CLOSE_OUTSIDE_RING = [(138.5 if x == 130.0 else x, n) for x, n in CUSTOM_RING]
# MML building-use records in the notch (x 130..165, n -30..-5), centroids outside the
# polygon, with the MML source on: one 0.5 m from the notch side, one reaching 3 m in, one
# 5 m from its sides.
MML_CLOSE_BOX = (130.5, -25.0, 136.0, -15.0)
MML_INTO_BOX = (127.0, -25.0, 135.0, -15.0)
MML_FAR_BOX = (140.0, -25.0, 150.0, -10.0)


def _assert_not_in_pack(edge: EdgeMap, building_id: str) -> None:
    """The building has no entry in buildings.json, no POI link and no tile node."""
    buildings = _json(edge.pack / "buildings.json")["buildings"]
    assert building_id not in {b["id"] for b in buildings}
    for poi in _json(edge.pack / POIS_NAME)["pois"]:
        assert poi["building_id"] != building_id
        assert building_id not in poi["contains_building_ids"]
    node = node_name(building_id)
    for glb in (edge.pack / "tiles").glob("*.glb"):
        names = read_glb_node_names(glb)
        assert not [n for n in names if n == node or n.startswith(f"{node}_c")], glb.name


def test_ac16_an_osm_building_outside_the_polygon_within_clearance_is_left_out(
    tmp_path: Path,
) -> None:
    """Centroid outside and footprint not reaching inside: the building isn't in the pack,
    so the clearance (which keeps barriers out of walls) doesn't apply to it."""
    edge = build_custom_map(tmp_path, ring=CLOSE_OUTSIDE_RING)
    footprint = box(*NEAR_EDGE_BOX)
    distance = footprint.distance(DocRing.of(edge.doc()).line)
    assert 0.4 < distance < edge.config.world_edge.building_clearance_m
    _assert_not_in_pack(edge, f"osm:way/{NEAR_EDGE}")
    assert edge.edge_report()["dropped_buildings"] == []


def test_ac16_an_mml_only_record_outside_the_polygon_fails_the_build_when_it_reaches_in(
    tmp_path: Path,
) -> None:
    message = r"mml:rakennus/1 \(0\.000 m from the boundary, 50\.0 m² outside\)"
    with pytest.raises(BuildError, match=message) as info:
        build_custom_map(tmp_path, topographic=True, rakennus=[MML_INTO_BOX])
    assert "1 building(s)" in str(info.value)
    assert not paths.pack_dir(tmp_path, MAP_ID).exists()


@pytest.mark.parametrize("record", [MML_CLOSE_BOX, MML_FAR_BOX], ids=["within-2m", "far"])
def test_an_mml_only_record_outside_the_polygon_is_left_out_quietly(
    tmp_path: Path, record: tuple[float, float, float, float]
) -> None:
    """A record in the notch is `outside`, not crossing, also 0.5 m from the polygon: the
    build passes and leaves it out of the pack."""
    edge = build_custom_map(tmp_path, topographic=True, rakennus=[record])
    report = _json(edge.work / "03-buildings" / "report.json")["mml_only"]
    assert report["not_added_ids"]["outside"] == ["mml:rakennus/1"]
    _assert_not_in_pack(edge, "mml:rakennus/1")


@pytest.fixture(scope="module")
def edge_rect_build(tmp_path_factory: pytest.TempPathFactory) -> EdgeMap:
    """Edgeville's full build over CUSTOM_BBOX without a custom polygon: the boundary is
    the build area rectangle (treat as read-only)."""
    return build_edge_map(
        tmp_path_factory.mktemp("edge-rect"),
        area="full",
        data_update={"bbox_wgs84": CUSTOM_BBOX, "tile_size_m": CUSTOM_TILE_M},
        osm=custom_osm(),
    )


# Kept building KEPT (x 120..147, n 30..40) moved east in a copy's 03-buildings footprints:
# its east wall 1 m inside the ring (within building_clearance_m), or 5 m over it.
KEPT_MOVES = {"1m-inside": -1.0, "partly-outside": 5.0}


@pytest.mark.parametrize("move", sorted(KEPT_MOVES))
@pytest.mark.parametrize(
    ("build", "source"),
    [("edge_custom", "custom"), ("edge_osm", "test_area"), ("edge_rect", "area")],
)
def test_ac16_the_pack_check_fails_a_kept_building_crossing_or_near_the_boundary(
    build: str, source: str, move: str, request: pytest.FixtureRequest, tmp_path: Path
) -> None:
    """The check uses each kept building's footprint, the boundary.json ring (whatever its
    source) and the configured building_clearance_m."""
    edge: EdgeMap = request.getfixturevalue(f"{build}_build")
    assert edge.doc()["boundary"]["source"] == source
    pack, work = tmp_path / "pack", tmp_path / "work"
    shutil.copytree(edge.pack, pack)
    shutil.copytree(edge.work, work)
    manifest = _json(pack / "manifest.json")
    assert check_world_edge(pack, work, manifest, edge.config.world_edge) >= 0  # copy passes
    ring = DocRing.of(edge.doc())
    x0, y0, x1, y1 = KEPT_BOX
    across = ring.polygon.intersection(LineString([(x0, (y0 + y1) / 2), (1e4, (y0 + y1) / 2)]))
    east = across.bounds[2]  # the ring's east side at the building's mid n
    dx = east + KEPT_MOVES[move] - x1
    path = work / "03-buildings" / "buildings.geojson"
    document = _json(path)
    for feature in document["features"]:
        if feature["properties"]["id"] == f"osm:way/{KEPT}":
            moved = _world(box(x0 + dx, y0, x1 + dx, y1))
            feature["geometry"] = shapely.geometry.mapping(moved)
    path.write_text(json.dumps(document), encoding="utf-8")
    distance = "1.000" if move == "1m-inside" else "0.000"
    with pytest.raises(
        PackCheckError, match=rf"buildings crossing the boundary: osm:way/{KEPT} \({distance} m\)"
    ):
        check_world_edge(pack, work, manifest, edge.config.world_edge)


def test_ac16_no_building_in_the_custom_pack_crosses_its_boundary(
    edge_custom_build: EdgeMap,
) -> None:
    edge = edge_custom_build
    origin = np.array(FIXTURE_ORIGIN)
    footprints = {
        f["properties"]["id"]: shapely.transform(shape(f["geometry"]), lambda c: c - origin)
        for f in _json(edge.work / "03-buildings" / "buildings.geojson")["features"]
    }
    ring = DocRing.of(edge.doc())
    clearance = edge.config.world_edge.building_clearance_m
    for building in _json(edge.pack / "buildings.json")["buildings"]:
        footprint = footprints[building["id"]]
        assert ring.polygon.contains(footprint)
        assert footprint.distance(ring.line) >= clearance
    assert edge.edge_report()["dropped_buildings"] == []


# AC16 and AC17: the custom boundary pack checks (`check_custom_boundary`) catch what the
# polygon clips lying outside it. Each case corrupts a copy of the custom build; the
# NOTCH point (x 150, n -15) lies outside the polygon, 15 m from its edge.

NOTCH = (150.0, -15.0)


def _godot(x: float, n: float, y: float = 0.0) -> list[float]:
    return [x, y, -n]


def _node_outside(pack: Path, work: Path) -> None:
    path = pack / "road_graph.json"
    graph = _json(path)
    node = next(n for n in graph["nodes"] if n["kind"] != "area_edge")
    node["position"] = _godot(*NOTCH, node["position"][1])
    path.write_text(json.dumps(graph), encoding="utf-8")


def _edge_point_outside(pack: Path, work: Path) -> None:
    path = pack / "road_graph.json"
    graph = _json(path)
    edge = next(e for e in graph["edges"] if len(e["points"]) >= 2)
    y = edge["points"][0][1]
    edge["points"].insert(1, _godot(*NOTCH, y))  # its nodes stay where they are
    path.write_text(json.dumps(graph), encoding="utf-8")


def _poi_outside(pack: Path, work: Path) -> None:
    path = pack / "pois.json"
    document = _json(path)
    (poi,) = _poi_by_name(document["pois"], POI_INSIDE)
    poi["position"] = _godot(*NOTCH, poi["position"][1])
    path.write_text(json.dumps(document), encoding="utf-8")


def _outline_outside(pack: Path, work: Path) -> None:
    path = pack / "pois.json"
    document = _json(path)
    (poi,) = _poi_by_osm(document["pois"], f"osm:way/{POI_AREA}")
    # The outline's side on the east edge (x 165) moved 10 mm further east.
    east = max(point[0] for point in poi["outline"][0][0])
    assert east == pytest.approx(165.0, abs=0.02)
    for point in poi["outline"][0][0]:
        if point[0] == east:
            point[0] += 0.01
    path.write_text(json.dumps(document), encoding="utf-8")


def _centroid_outside(pack: Path, work: Path) -> None:
    path = work / "03-buildings" / "buildings.geojson"
    document = _json(path)
    for feature in document["features"]:
        if feature["properties"]["id"] == f"osm:way/{KEPT}":
            _, y0, _, y1 = KEPT_BOX
            moved = _world(box(170.0, y0, 180.0, y1))  # x 170..180: east of the edge (165)
            feature["geometry"] = shapely.geometry.mapping(moved)
    path.write_text(json.dumps(document), encoding="utf-8")


CUSTOM_CORRUPTIONS: dict[str, tuple[Callable[[Path, Path], None], str]] = {
    "graph-node-outside": (_node_outside, r"road graph node .* outside the custom boundary"),
    "graph-edge-point-outside": (_edge_point_outside, r"road graph edge .* leaves the custom"),
    "poi-position-outside": (_poi_outside, r"POI .* at \(150\.000, -15\.000\) is outside"),
    "poi-outline-outside": (_outline_outside, rf"POI osm:way/{POI_AREA}: outline leaves"),
    "building-centroid-outside": (_centroid_outside, rf"building osm:way/{KEPT}: centroid"),
}


def _custom_copy(edge: EdgeMap, tmp_path: Path) -> tuple[Path, Path, dict[str, Any]]:
    pack, work = tmp_path / "pack", tmp_path / "work"
    shutil.copytree(edge.pack, pack)
    shutil.copytree(edge.work, work)
    manifest = _json(pack / "manifest.json")
    assert check_custom_boundary(pack, work, manifest)  # the copy passes
    return pack, work, manifest


@pytest.mark.parametrize("case", sorted(CUSTOM_CORRUPTIONS))
def test_ac17_each_custom_boundary_pack_check_catches_a_corrupted_pack(
    case: str, edge_custom_build: EdgeMap, tmp_path: Path
) -> None:
    mutate, message = CUSTOM_CORRUPTIONS[case]
    pack, work, manifest = _custom_copy(edge_custom_build, tmp_path)
    mutate(pack, work)
    with pytest.raises(PackCheckError, match=message):
        check_custom_boundary(pack, work, manifest)


def test_ac17_the_custom_boundary_pack_check_catches_a_terrain_triangle_outside(
    edge_custom_build: EdgeMap, tmp_path: Path
) -> None:
    """A terrain vertex on the east edge (x about 165) moved 10 mm east: its triangles
    leave the polygon by more than 1 mm."""
    pack, work, manifest = _custom_copy(edge_custom_build, tmp_path)
    tiles = {t["id"]: (t, read_glb_meshes(pack / t["file"])) for t in manifest["tiles"]}
    east = max(x for x, _ in DocRing.of(edge_custom_build.doc()).coords)  # about 165
    tid = next(
        tid
        for tid, (_, m) in tiles.items()
        if abs(m.nodes[TERRAIN_NODE][0][:, 0].max() - east) < 1e-4
    )
    vertices, faces = tiles[tid][1].nodes[TERRAIN_NODE]
    vertices = vertices.copy()
    vertices[int(np.argmax(vertices[:, 0])), 0] += 0.01
    tiles[tid][1].nodes[TERRAIN_NODE] = (vertices, faces)
    with pytest.raises(PackCheckError, match=rf"{tid}: \d+ terrain triangle\(s\) outside"):
        check_custom_boundary(pack, work, manifest, tiles)
    # A 0.5 mm move stays within the 1 mm tolerance.
    vertices[int(np.argmax(vertices[:, 0])), 0] -= 0.0095
    assert check_custom_boundary(pack, work, manifest, tiles)


def test_ac17_check_pack_runs_the_custom_boundary_checks(
    edge_custom_build: EdgeMap, tmp_path: Path
) -> None:
    pack, work, _ = _custom_copy(edge_custom_build, tmp_path)
    _poi_outside(pack, work)
    with pytest.raises(PackCheckError, match="is outside the custom boundary"):
        check_pack(
            pack,
            work,
            edge_custom_build.config.roads,
            world_edge=edge_custom_build.config.world_edge,
        )


def test_the_custom_boundary_checks_skip_packs_without_a_custom_polygon(
    edge_osm_build: EdgeMap,
) -> None:
    manifest = _json(edge_osm_build.pack / "manifest.json")
    assert not check_custom_boundary(edge_osm_build.pack, edge_osm_build.work, manifest)


# AC18


def _snapshot(folder: Path) -> dict[str, tuple[bytes, int]]:
    return {
        str(p.relative_to(folder)): (p.read_bytes(), p.stat().st_mtime_ns)
        for p in sorted(folder.rglob("*"))
        if p.is_file()
    }


@dataclass
class CliRepo:
    edge: EdgeMap
    pack: Path
    snapshot: dict[str, tuple[bytes, int]]


@pytest.fixture(scope="module")
def cli_repo(tmp_path_factory: pytest.TempPathFactory) -> CliRepo:
    """Edgeville built once through the CLI; the cases below change its inputs and put
    them back."""
    edge = setup_edge_map(tmp_path_factory.mktemp("edge-cli"))
    root = str(edge.root)
    assert main(["--repo-root", root, "build", MAP_ID, "--area", "test"]) == EXIT_OK
    pack = paths.pack_dir(edge.root, MAP_ID)
    return CliRepo(edge, pack, _snapshot(pack))


@pytest.fixture
def restore(cli_repo: CliRepo) -> Iterator[CliRepo]:
    map_yaml = cli_repo.edge.map_dir / "map.yaml"
    markers = cli_repo.edge.map_dir / "markers.json"
    saved = (map_yaml.read_bytes(), markers.read_bytes())
    try:
        yield cli_repo
    finally:
        map_yaml.write_bytes(saved[0])
        markers.write_bytes(saved[1])


def _latlon(points: Sequence[tuple[float, float]]) -> list[dict[str, float]]:
    result = []
    for x, n in points:
        lon, lat = map_to_wgs84(x + FIXTURE_ORIGIN[0], n + FIXTURE_ORIGIN[1], CRS)
        result.append({"lat": round(lat, 7), "lon": round(lon, 7)})
    return result


BAD_BOUNDARIES = {
    "under-3-points": ([(0, 0), (100, 0)], "at least 3 points|too_short|List should have"),
    "self-intersecting": ([(0, 0), (100, 100), (100, 0), (0, 100)], "isn't simple"),
    "outside-build-area": ([(0, 0), (5000, 0), (0, 100)], "outside the build area"),
}
BAD_SPAWNS = {
    "under-3-points": ([(40, 100), (60, 100)], "spawn_area_wgs84"),
    "self-intersecting": ([(40, 100), (80, 125), (80, 100), (40, 125)], "isn't simple"),
    "partly-outside-test-area": (
        [(100, 100), (160, 100), (160, 140), (100, 140)],
        r"isn't inside the test area .*: [0-9.]+ m² of it lies outside",
    ),
}


def _run_cli(capsys: pytest.CaptureFixture[str], repo: CliRepo) -> tuple[int, str]:
    capsys.readouterr()
    code = main(["--repo-root", str(repo.edge.root), "build", MAP_ID, "--area", "test"])
    captured = capsys.readouterr()
    return code, captured.out + captured.err


@pytest.mark.parametrize("case", sorted(BAD_BOUNDARIES))
def test_ac18_an_invalid_boundary_fails_validation_and_leaves_the_pack_untouched(
    case: str, restore: CliRepo, capsys: pytest.CaptureFixture[str]
) -> None:
    points, reason = BAD_BOUNDARIES[case]
    map_yaml = restore.edge.map_dir / "map.yaml"
    data = yaml.safe_load(map_yaml.read_text(encoding="utf-8"))
    data["world_edge"] = {"boundary_wgs84": _latlon(points)}
    map_yaml.write_text(yaml.safe_dump(data, sort_keys=False), encoding="utf-8")
    code, output = _run_cli(capsys, restore)
    assert code != EXIT_OK
    assert re.search(reason, output), output
    assert _snapshot(restore.pack) == restore.snapshot


@pytest.mark.parametrize("case", sorted(BAD_SPAWNS))
def test_ac18_an_invalid_spawn_area_fails_validation_and_leaves_the_pack_untouched(
    case: str, restore: CliRepo, capsys: pytest.CaptureFixture[str]
) -> None:
    points, reason = BAD_SPAWNS[case]
    write_markers(restore.edge.map_dir, points)
    code, output = _run_cli(capsys, restore)
    assert code != EXIT_OK
    assert re.search(reason, output), output
    assert _snapshot(restore.pack) == restore.snapshot


# AC19


def _round_tripped(map_dir: Path) -> Polygon:
    """The spawn polygon of `map_dir/markers.json` in local metres, after the 7-decimal
    WGS84 round trip (what the build measures)."""
    from mapgen.geo import wgs84_ring_to_local

    points = _json(map_dir / "markers.json")["spawn_area_wgs84"]
    coords = wgs84_ring_to_local([(p["lat"], p["lon"]) for p in points], CRS, *FIXTURE_ORIGIN)
    return Polygon(coords)


def _measured(message: str) -> tuple[float, float]:
    area = re.search(r"open ground ([0-9.]+) m² \(minimum ([0-9.]+)\)", message)
    radius = re.search(r"largest clear disc r ([0-9.]+) m \(minimum ([0-9.]+)\)", message)
    assert area and radius, message
    assert float(area.group(2)) == DEFAULTS.spawn.min_open_area_m2
    assert float(radius.group(2)) == DEFAULTS.spawn.min_clear_radius_m
    return float(area.group(1)), float(radius.group(1))


OPEN = "open"  # the open ground is the whole spawn polygon (no obstacle near it)
SPAWN_FAILURES: dict[str, tuple[list[tuple[float, float]], str]] = {
    # The kept building, 2 m in from its walls on every side.
    "covered-by-building": (
        [(122, 32), (138, 32), (138, 38), (122, 38)],
        f"building osm:way/{KEPT}",
    ),
    "covered-by-water": (
        [(-138, -30), (-122, -30), (-122, 10), (-138, 10)],
        f"water osm:way/{OSM_LAKE}",
    ),
    "covered-by-roads": ([(-22, -130), (-18, -130), (-18, -105), (-22, -105)], r"road road:\d+"),
    # 20 x 20 m of open ground: under 500 m², though a disc of r 10 m fits.
    "open-ground-under-minimum": ([(-80, 40), (-60, 40), (-60, 60), (-80, 60)], OPEN),
    # 200 x 8 m: 1600 m² of open ground, but only a disc of r 4 m fits.
    "no-room-for-the-clear-disc": ([(-100, 60), (100, 60), (100, 68), (-100, 68)], OPEN),
}


@pytest.mark.parametrize("case", sorted(SPAWN_FAILURES))
def test_ac19_the_spawn_check_fails_the_build_with_the_measured_values(
    case: str, tmp_path: Path
) -> None:
    ring, obstacle = SPAWN_FAILURES[case]
    with pytest.raises(BuildError) as error:
        build_edge_map(tmp_path, markers=ring)
    message = str(error.value)
    assert "spawn area check failed" in message
    area, radius = _measured(message)
    spawn = _round_tripped(paths.map_dir(tmp_path, MAP_ID))
    assert f"spawn area {spawn.area:.1f} m²" in message
    if obstacle == OPEN:
        assert "largest obstacle overlaps (grown by 2 m): none" in message
        assert area == pytest.approx(spawn.area, abs=0.05)  # printed to 0.1 m²
        rectangle = spawn.minimum_rotated_rectangle.exterior.coords
        short = min(math.dist(rectangle[0], rectangle[1]), math.dist(rectangle[1], rectangle[2]))
        assert radius == pytest.approx(
            short / 2, abs=0.02
        )  # 0.01 m disc precision, printed to 0.01
        assert area < DEFAULTS.spawn.min_open_area_m2 or radius < DEFAULTS.spawn.min_clear_radius_m
    else:
        assert (area, radius) == (0.0, 0.0)
        assert re.search(obstacle, message), message
    assert not paths.pack_dir(tmp_path, MAP_ID).exists()


def test_ac19_a_spawn_area_partly_outside_the_test_area_fails_the_build(tmp_path: Path) -> None:
    points, _ = BAD_SPAWNS["partly-outside-test-area"]
    with pytest.raises(BuildError) as error:
        build_edge_map(tmp_path, markers=points)
    outside = _round_tripped(paths.map_dir(tmp_path, MAP_ID)).difference(
        box(-HALF, -HALF, HALF, HALF)
    )
    match = re.search(
        r"isn't inside the test area .*: ([0-9.]+) m² of it lies outside", str(error.value)
    )
    assert match, str(error.value)
    assert float(match.group(1)) == pytest.approx(outside.area, abs=0.05)


def test_ac19_somero_spawn_area_passes_in_the_test_build_with_the_spec_values(
    somero_area: SomeroArea,
) -> None:
    """Spec "Somero: spawn area candidates", test build row: 15.02 m from the boundary,
    open ground 5308 m², largest clear disc r 30.0 m."""
    spawn = _json(somero_area.work / "10-world-edge" / "report.json")["spawn"]
    assert spawn["passed"] is True
    assert round(spawn["boundary_distance_m"], 2) == 15.02
    assert round(spawn["open_area_m2"]) == 5308
    assert spawn["clear_radius_m"] >= 30.0
    document = _json(somero_area.pack / "boundary.json")["spawn_area"]
    assert document["open_area_m2"] == pytest.approx(spawn["open_area_m2"], abs=MM)
    assert len(document["polygon"]) == len(_json(SOMERO_MARKERS)["spawn_area_wgs84"])


# AC21


def test_ac21_the_pack_checks_pass_on_the_somero_test_area_build(somero_area: SomeroArea) -> None:
    summary = check_pack(
        somero_area.pack,
        somero_area.work,
        somero_area.config.roads,
        world_edge=somero_area.config.world_edge,
    )
    assert summary.roadblocks == len(_json(somero_area.pack / "boundary.json")["roadblocks"]) > 0


def _drop_a_node(doc: dict[str, Any]) -> None:
    rb = next(rb for rb in doc["roadblocks"] if len(rb["nodes"]) > 1)
    rb["nodes"].pop()


def _drop_a_fence(doc: dict[str, Any]) -> None:
    del doc["fences"][len(doc["fences"]) // 2]


def _wrong_role(doc: dict[str, Any]) -> None:
    item = doc["roadblocks"][0]["nodes"][0]
    item["role"] = "turn_around" if item["role"] == "spawn_despawn" else "spawn_despawn"


def _fence_height(doc: dict[str, Any]) -> None:
    doc["fences"][0]["end"][1] += 0.01


def _land_as_water(doc: dict[str, Any]) -> None:
    doc["fences"][0]["type"] = "water"
    doc["fences"][0]["water_ids"] = ["osm:way/1"]


def _drop_a_prism(doc: dict[str, Any]) -> None:
    prisms = doc["void_trigger"]["prisms"]
    del prisms[max(range(len(prisms)), key=lambda k: Polygon(prisms[k]["footprint"]).area)]


def _prism_too_close(doc: dict[str, Any]) -> None:
    """Move the first prism 0.5 m towards the ring: its inner side is then 0.5 m from
    the ring, under void.inner_offset_m (1.0 m)."""
    ring = Ring.from_coords([_local(p) for p in doc["boundary"]["ring"]])
    prism = doc["void_trigger"]["prisms"][0]
    points = [_local(p) for p in prism["footprint"]]
    centre = Polygon(points).centroid
    outward = ring.outward_at(ring.project(centre.x, centre.y))
    prism["footprint"] = [
        [x - 0.5 * float(outward[0]), -(n - 0.5 * float(outward[1]))] for x, n in points
    ]


def _narrow_notch(doc: dict[str, Any]) -> None:
    """Cut a 2 m wide, 10 m deep notch into the middle of the ring's first segment (and
    fix length_m), under world_edge.min_feature_width_m (3 m)."""
    points = [_local(p) for p in doc["boundary"]["ring"]]
    (x0, n0), (x1, n1) = points[0], points[1]
    ux, un = (
        (x1 - x0) / math.dist(points[0], points[1]),
        (n1 - n0) / math.dist(points[0], points[1]),
    )
    mx, mn = (x0 + x1) / 2, (n0 + n1) / 2
    left = (-un, ux)  # into the playable area (counter-clockwise ring)
    notch = [
        (mx - ux, mn - un),
        (mx - ux + 10 * left[0], mn - un + 10 * left[1]),
        (mx + ux + 10 * left[0], mn + un + 10 * left[1]),
        (mx + ux, mn + un),
    ]
    ring = [points[0], *notch, *points[1:]]
    doc["boundary"]["ring"] = [[x, -n] for x, n in ring]
    doc["boundary"]["length_m"] = Ring.from_coords(ring).length


def _fence_over_road(doc: dict[str, Any]) -> None:
    """Move a roadblock's start, and the end of the fence before it, clearance + 0.5 m
    into the road surface, keeping the loop closed."""
    ring = Ring.from_coords([_local(p) for p in doc["boundary"]["ring"]])
    items = _items(doc)
    k = next(
        k
        for k, item in enumerate(items)
        if item["id"].startswith("roadblock:") and items[k - 1]["id"].startswith("fence:")
    )
    rb, fence = items[k], items[k - 1]
    s = rb["s_start"] + DEFAULTS.roadblock.clearance_m + 0.5
    x, n = ring.point_at(s)
    for item, s_key, p_key in ((rb, "s_start", "start"), (fence, "s_end", "end")):
        item[s_key] = s
        item[p_key] = [float(x), item[p_key][1], -float(n)]


CORRUPTIONS: dict[str, tuple[Callable[[dict[str, Any]], None], str]] = {
    "fence-over-road-surface": (_fence_over_road, "runs .* m over road or path surface"),
    "node-missing-from-roadblocks": (_drop_a_node, "area_edge nodes in no roadblock"),
    "gap-in-the-loop": (_drop_a_fence, "doesn't end where"),
    "wrong-role": (_wrong_role, "has role"),
    "fence-end-height-off-by-1-cm": (_fence_height, "end at .* != terrain"),
    "land-fence-marked-water": (_land_as_water, "is water but"),
    "void-prism-missing": (_drop_a_prism, "lie in no prism"),
    "void-prism-too-close": (_prism_too_close, "closer to the ring than void.inner_offset_m"),
    "narrow-notch-in-the-ring": (_narrow_notch, "spike.* or notch.* narrower than"),
}


@pytest.mark.parametrize("case", sorted(CORRUPTIONS))
def test_ac21_each_world_edge_pack_check_catches_a_corrupted_boundary_json(
    case: str, somero_area: SomeroArea, tmp_path: Path
) -> None:
    mutate, message = CORRUPTIONS[case]
    pack, work = tmp_path / "pack", tmp_path / "work"
    shutil.copytree(somero_area.pack, pack)
    shutil.copytree(somero_area.work, work)
    manifest = _json(pack / "manifest.json")
    cfg = somero_area.config.world_edge
    assert check_world_edge(pack, work, manifest, cfg) > 0  # the copy passes
    document = _json(pack / "boundary.json")
    mutate(document)
    (pack / "boundary.json").write_text(json.dumps(document), encoding="utf-8")
    with pytest.raises(PackCheckError, match=message):
        check_world_edge(pack, work, manifest, cfg)


# AC14: the cliff meshes (slice (c))
#
# Read back from the tiles of three builds: the Somero test area (the test-area square),
# `edge_custom` (a custom polygon with convex and concave corners, one tile not written)
# and `edge_rect` (the build area rectangle over the same data). Expected values come
# from boundary.json (ring, roadblocks, fences, cliff) and the terrain meshes in the
# same tiles. Positions are float32 in the GLB: about 6e-5 m at 1 km, so geometric
# checks allow FLOAT32_M.

FLOAT32_M = 2e-4
CLIFF_BUILDS = ["somero", "edge_custom", "edge_rect"]


@dataclass
class CliffNode:
    """A flat-shaded node read back: per-triangle local positions and vertex normals."""

    triangles: np.ndarray  # (m, 3, 3) x east, n north, z up
    normals: np.ndarray  # (m, 3, 3)
    indices: np.ndarray  # (m, 3) as stored


@dataclass
class CliffPack:
    name: str
    pack: Path
    manifest: dict[str, Any]
    doc: dict[str, Any]
    cliff: dict[str, CliffNode]  # tile id -> edge_cliff-col
    caps: dict[str, CliffNode]  # tile id -> edge_bottom
    terrain: dict[str, tuple[np.ndarray, np.ndarray]]
    names: dict[str, list[str]]

    @property
    def ring(self) -> DocRing:
        return DocRing.of(self.doc)

    @property
    def bottom(self) -> float:
        return float(self.doc["cliff"]["bottom_y"])

    @property
    def ledge_m(self) -> float:
        return float(self.doc["cliff"]["ledge_m"])

    def all(self, nodes: dict[str, CliffNode]) -> CliffNode:
        parts = list(nodes.values())
        return CliffNode(
            np.concatenate([p.triangles for p in parts]),
            np.concatenate([p.normals for p in parts]),
            np.concatenate([p.indices for p in parts]),
        )

    def ledge(self) -> np.ndarray:
        """The up-facing (ledge) triangles of every tile's edge_cliff-col."""
        node = self.all(self.cliff)
        return node.triangles[node.normals[:, 0, 2] > 0.5]


def _cliff_node(path: Path, name: str) -> CliffNode | None:
    if name not in read_glb_node_names(path):
        return None
    stored = glb_primitive(path, name)
    assert "TEXCOORD_0" not in stored, f"{path.name}/{name} has UVs"
    indices = np.asarray(stored["indices"], dtype=np.int64)
    positions = from_gltf(stored["POSITION"])
    normals = from_gltf(stored["NORMAL"])
    return CliffNode(positions[indices], normals[indices], indices)


def _cliff_pack(name: str, pack: Path) -> CliffPack:
    from mapgen.build.world_edge.cliff import BOTTOM_NODE, CLIFF_NODE

    manifest = _json(pack / "manifest.json")
    cliff: dict[str, CliffNode] = {}
    caps: dict[str, CliffNode] = {}
    terrain = {}
    names = {}
    for tile in manifest["tiles"]:
        path = pack / tile["file"]
        names[tile["id"]] = read_glb_node_names(path)
        for nodes, wanted in ((cliff, CLIFF_NODE), (caps, BOTTOM_NODE)):
            node = _cliff_node(path, wanted)
            if node is not None:
                nodes[tile["id"]] = node
        terrain[tile["id"]] = read_glb_meshes(path).nodes[TERRAIN_NODE]
    return CliffPack(
        name, pack, manifest, _json(pack / "boundary.json"), cliff, caps, terrain, names
    )


@pytest.fixture(params=CLIFF_BUILDS)
def cliff_pack(request: pytest.FixtureRequest) -> CliffPack:
    if request.param == "somero":
        return _cliff_pack("somero", request.getfixturevalue("somero_area").pack)
    edge: EdgeMap = request.getfixturevalue(f"{request.param}_build")
    return _cliff_pack(request.param, edge.pack)


def _geometric_normals(triangles: np.ndarray) -> tuple[np.ndarray, np.ndarray]:
    cross = np.cross(triangles[:, 1] - triangles[:, 0], triangles[:, 2] - triangles[:, 0])
    area = np.linalg.norm(cross, axis=1) / 2
    return cross / np.where(area > 0, 2 * area, 1.0)[:, None], area


def _through(ring_coords: Sequence[Sequence[float]], tiles: Sequence[dict[str, Any]]) -> set[str]:
    """The tiles the boundary passes through (spec amendment, slice (c)): a tile whose
    bounds meet the ring in a straight stretch longer than 1 mm, on the stretch's playable
    side: of the two points 1 mm to either side of the stretch's midpoint, the one inside
    the ring lies in the tile's bounds. A stretch on a grid line counts only for the tile
    inside the ring."""
    polygon = Polygon(ring_coords)
    line = LineString([*ring_coords, ring_coords[0]])
    result = set()
    for tile in tiles:
        bounds = box(*tile["bounds_local"])
        for part in shapely.get_parts(line.intersection(bounds)):
            if part.geom_type != "LineString":
                continue
            coords = np.asarray(part.coords)
            for a, b in zip(coords[:-1], coords[1:], strict=True):
                length = float(np.hypot(*(b - a)))
                if length <= MM:
                    continue
                side = np.array([a[1] - b[1], b[0] - a[0]]) / length * MM
                mid = (a + b) / 2
                inward = [Point(q) for q in (mid + side, mid - side) if polygon.contains(Point(q))]
                if len(inward) == 1 and bounds.covers(inward[0]):
                    result.add(str(tile["id"]))
    return result


def test_ac14_every_tile_the_boundary_passes_through_has_the_cliff_and_every_tile_a_cap(
    cliff_pack: CliffPack,
) -> None:
    from mapgen.build.pack_checks import boundary_tiles, check_import_names

    through = _through(cliff_pack.ring.coords, cliff_pack.manifest["tiles"])
    assert through and set(cliff_pack.cliff) == through
    ring = Ring.from_coords(cliff_pack.ring.coords)
    assert boundary_tiles(ring, cliff_pack.manifest["tiles"]) == sorted(through)
    assert set(cliff_pack.caps) == {t["id"] for t in cliff_pack.manifest["tiles"]}
    for tid, names in cliff_pack.names.items():
        check_import_names(tid, names)  # edge_cliff-col and edge_bottom import as themselves
        assert names.count("edge_bottom") == 1 and names.count("edge_cliff-col") <= 1
    if cliff_pack.name == "edge_custom":
        assert OUTSIDE_TILE not in cliff_pack.names  # not written, so no cliff either


def test_ac14_faces_are_planar_flat_shaded_and_share_no_vertices(cliff_pack: CliffPack) -> None:
    for nodes in (cliff_pack.cliff, cliff_pack.caps):
        for tid, node in nodes.items():
            assert np.array_equal(node.indices.ravel(), np.arange(node.indices.size)), tid
            # One normal per face, a unit vector...
            assert np.array_equal(node.normals, np.repeat(node.normals[:, :1], 3, axis=1)), tid
            normal = node.normals[:, 0]
            assert np.allclose(np.linalg.norm(normal, axis=1), 1.0, atol=1e-6), tid
            # ... that is the face's own normal (well-conditioned faces; thin skirt
            # slivers lose their direction to float32 positions).
            geometric, area = _geometric_normals(node.triangles)
            big = area > 0.01
            assert np.abs(geometric[big] - normal[big]).max() < 1e-3, tid
            # Triangles sharing an edge and a normal are one planar face.
            edges: dict[bytes, list[int]] = {}
            for k, tri in enumerate(node.triangles.astype(np.float32)):
                for a, b in ((0, 1), (1, 2), (2, 0)):
                    key = b"".join(sorted((tri[a].tobytes(), tri[b].tobytes())))
                    edges.setdefault(key, []).append(k)
            for pair in edges.values():
                if len(pair) != 2 or not np.allclose(normal[pair[0]], normal[pair[1]]):
                    continue
                first, second = node.triangles[pair[0]], node.triangles[pair[1]]
                offset = np.abs((second - first[0]) @ normal[pair[0]]).max()
                assert offset <= FLOAT32_M, (tid, pair, offset)


def test_ac14_the_face_reaches_bottom_y_all_around_the_slab(cliff_pack: CliffPack) -> None:
    node = cliff_pack.all(cliff_pack.cliff)
    bottom = cliff_pack.bottom
    assert node.triangles[:, :, 2].min() == pytest.approx(bottom, abs=FLOAT32_M)
    vertical = np.abs(node.normals[:, 0, 2]) < 1e-6
    at_bottom = np.abs(node.triangles[:, :, 2] - bottom) <= FLOAT32_M
    bottom_edges = [
        LineString([t[a, :2], t[b, :2]])
        for t, low in zip(node.triangles[vertical], at_bottom[vertical], strict=True)
        for a, b in ((0, 1), (1, 2), (2, 0))
        if low[a] and low[b]
    ]
    caps = shapely.union_all(shapely.polygons(cliff_pack.all(cliff_pack.caps).triangles[:, :, :2]))
    outline = [g.exterior for g in shapely.get_parts(caps)]
    outline += [r for g in shapely.get_parts(caps) for r in g.interiors if Polygon(r).area > 0.01]
    tree = shapely.STRtree(bottom_edges)
    for ring in outline:
        samples = [ring.interpolate(d) for d in np.arange(0.0, ring.length, 0.5)]
        near = tree.query(samples, predicate="dwithin", distance=MM)
        assert set(near[0]) == set(range(len(samples))), "a stretch of the cap has no face"


def test_ac14_every_written_tile_has_a_cap_at_bottom_y_facing_down(cliff_pack: CliffPack) -> None:
    for tid, cap in cliff_pack.caps.items():
        assert np.abs(cap.triangles[:, :, 2] - cliff_pack.bottom).max() <= FLOAT32_M, tid
        assert np.array_equal(cap.normals[:, 0], np.tile([0.0, 0.0, -1.0], (len(cap.normals), 1)))
        geometric, area = _geometric_normals(cap.triangles)
        assert (geometric[area > 0.01, 2] < 0).all(), tid  # float32 may flip a sliver
    caps = shapely.union_all(shapely.polygons(cliff_pack.all(cliff_pack.caps).triangles[:, :, :2]))
    ledge = shapely.union_all(shapely.polygons(cliff_pack.ledge()[:, :, :2]))
    slab = shapely.union_all([cliff_pack.ring.polygon, ledge])
    assert caps.symmetric_difference(slab).area < 0.01 * slab.length * FLOAT32_M / MM


def _ring_segments(triangles: np.ndarray, line: LineString) -> np.ndarray:
    """Every triangle edge (k, 2, 3) whose ends and midpoint lie on the ring."""
    edges = np.concatenate([triangles[:, [0, 1]], triangles[:, [1, 2]], triangles[:, [2, 0]]])
    on = np.ones(len(edges), dtype=bool)
    for points in (edges[:, 0, :2], edges[:, 1, :2], edges[:, :, :2].mean(axis=1)):
        on &= shapely.distance(shapely.points(points), line) <= FLOAT32_M
    return edges[on]


def _segment_distance(points: np.ndarray, segments: np.ndarray) -> np.ndarray:
    """3D distance from each point to the nearest segment (candidates within 1 mm in 2D)."""
    tree = shapely.STRtree(shapely.linestrings(segments[:, :, :2]))
    hits = tree.query(shapely.points(points[:, :2]), predicate="dwithin", distance=MM)
    best = np.full(len(points), np.inf)
    a, b = segments[hits[1], 0], segments[hits[1], 1]
    d = b - a
    p = points[hits[0]]
    t = np.clip(((p - a) * d).sum(axis=1) / np.maximum((d**2).sum(axis=1), 1e-300), 0, 1)
    np.minimum.at(best, hits[0], np.linalg.norm(a + d * t[:, None] - p, axis=1))
    return best


def test_ac14_the_ledge_inner_edge_is_on_the_ring_at_the_fence_and_roadblock_heights(
    cliff_pack: CliffPack,
) -> None:
    from mapgen.build.pack_checks import TriangleSampler

    ring = Ring.from_coords(cliff_pack.ring.coords)
    ledge = cliff_pack.ledge()
    vertices = ledge.reshape(-1, 3)
    sampler = TriangleSampler(vertices, np.arange(len(vertices)).reshape(-1, 3))
    for item in _items(cliff_pack.doc):
        for end in (item["start"], item["end"]):
            x, n = _local(end)
            gap = np.hypot(vertices[:, 0] - x, vertices[:, 1] - n)
            close = gap <= MM
            assert close.any() and np.abs(vertices[close, 2] - end[1]).min() <= MM, item["id"]
        # Between its ends the ledge's inner edge runs straight from height to height.
        for fraction in (0.25, 0.5, 0.75):
            s = item["s_start"] + fraction * (item["s_end"] - item["s_start"])
            x, n = ring.point_at(s)
            expected = item["start"][1] + fraction * (item["end"][1] - item["start"][1])
            (height,) = sampler.height(np.array([[x, n]]), distance=FLOAT32_M)
            assert height == pytest.approx(expected, abs=MM), (item["id"], fraction)


def test_ac14_the_terrain_edge_meets_the_cliff_mesh_within_1_mm(cliff_pack: CliffPack) -> None:
    """Every terrain vertex and edge midpoint on the ring (the build area or tile edge of a
    rectangle, the cut edge of a custom polygon) lies on an edge of the cliff mesh."""
    line = cliff_pack.ring.line
    cliff_edges = _ring_segments(cliff_pack.all(cliff_pack.cliff).triangles, line)
    checked = 0
    for vertices, faces in cliff_pack.terrain.values():
        on = _ring_segments(np.asarray(vertices)[np.asarray(faces)], line)
        if len(on) == 0:
            continue
        points = np.concatenate([on[:, 0], on.mean(axis=1)])
        distance = _segment_distance(points, cliff_edges)
        assert distance.max() <= MM, points[np.argmax(distance)]
        checked += len(points)
    assert checked > cliff_pack.ring.length / 2  # at least one point per 2 m


def test_ac14_the_ledge_is_ledge_m_wide_with_mitred_and_trimmed_corners(
    cliff_pack: CliffPack,
) -> None:
    from mapgen.build.world_edge.cliff import MITRE_LIMIT

    ring = Ring.from_coords(cliff_pack.ring.coords)
    polygon, line, width = ring.polygon(), ring.line(), cliff_pack.ledge_m
    ledge = cliff_pack.ledge()
    shapes = shapely.polygons(ledge[:, :, :2])
    union = shapely.union_all(shapes)
    # Not over the playable area, and not over itself.
    inside = shapely.get_coordinates(shapely.intersection(shapes, polygon))
    assert len(inside) == 0 or shapely.distance(shapely.points(inside), line).max() <= FLOAT32_M
    assert float(shapely.area(shapes).sum()) == pytest.approx(union.area, abs=0.01)
    # ledge_m wide along every segment, away from its corners.
    covered = union.buffer(FLOAT32_M)
    clear = 2 * MITRE_LIMIT * width
    for i in range(ring.n):
        a, _ = ring.segment(i)
        length = float(ring.cum[i + 1] - ring.cum[i])
        for u in np.arange(clear, length - clear, 5.0):
            p = a + ring.direction(i) * u
            for offset, expect in ((width - MM, True), (width + MM, False)):
                q = Point(p + ring.normal(i) * offset)
                if not expect and line.distance(q) < width + MM / 2:
                    continue  # another stretch of the ring is closer
                assert covered.contains(q) == expect, (i, u, offset)
    # Convex corners reach the mitre point; concave ones are cut along the bisector.
    corners = 0
    lengths = np.diff(ring.cum)
    for v in range(ring.n):
        if min(lengths[v], lengths[v - 1]) < clear:
            continue
        n0, n1 = ring.normal(v - 1), ring.normal(v)
        m = (n0 + n1) / np.hypot(*(n0 + n1))
        tip = ring.xy[v] + m * width / float(np.dot(m, n0))
        d0, d1 = ring.direction(v - 1), ring.direction(v)
        convex = d0[0] * d1[1] - d0[1] * d1[0] > 0
        if convex and width / float(np.dot(m, n0)) > MITRE_LIMIT * width:
            continue
        assert np.hypot(*(ledge.reshape(-1, 3)[:, :2] - tip).T).min() <= FLOAT32_M, v
        if not convex:
            # No ledge triangle reaches across the bisector (beyond float32 noise).
            bisector = LineString([ring.xy[v], tip])
            inner = shapely.buffer(shapes, -FLOAT32_M)
            assert shapely.length(shapely.intersection(inner, bisector)).max() == 0, v
        corners += 1
    assert corners >= 4


def test_ac14_a_stretch_on_a_grid_line_counts_only_for_the_tile_inside() -> None:
    """A 50 m square on 50 m tiles: its east edge lies on x = 50, on the bounds of both
    tile_x0_y0 and tile_x1_y0, but only tile_x0_y0 is on its playable side. tile_x1_y0
    meets the ring only along that line (and its corners), so it isn't passed through."""
    from mapgen.build.pack_checks import boundary_tiles

    coords = [(0.0, 0.0), (50.0, 0.0), (50.0, 50.0), (0.0, 50.0)]
    tiles = [
        {"id": "tile_x0_y0", "bounds_local": [0.0, 0.0, 50.0, 50.0]},
        {"id": "tile_x1_y0", "bounds_local": [50.0, 0.0, 100.0, 50.0]},
        {"id": "tile_x0_y1", "bounds_local": [0.0, 50.0, 50.0, 100.0]},
    ]
    assert _through(coords, tiles) == {"tile_x0_y0"}
    assert boundary_tiles(Ring.from_coords(coords), tiles) == ["tile_x0_y0"]


def _first_ring_tile(pack: Path, manifest: dict[str, Any]) -> str:
    doc = _json(pack / "boundary.json")
    return sorted(_through([_local(p) for p in doc["boundary"]["ring"]], manifest["tiles"]))[0]


def _ledge_on_ring(pack: Path, vertices: np.ndarray, faces: np.ndarray) -> int:
    """The index of the ledge triangle with the longest edge on the ring."""
    doc = _json(pack / "boundary.json")
    line = DocRing.of(doc).line
    tri = vertices[faces]
    normal, _ = _geometric_normals(tri)
    best, index = 0.0, -1
    for k in np.flatnonzero(normal[:, 2] > 0.5):
        for a, b in ((0, 1), (1, 2), (2, 0)):
            ends = [Point(tri[k, a, :2]), Point(tri[k, b, :2])]
            if all(line.distance(e) <= FLOAT32_M for e in ends):
                length = float(np.hypot(*(tri[k, a, :2] - tri[k, b, :2])))
                if length > best:
                    best, index = length, int(k)
    assert index >= 0
    return index


def _drop_triangle(meshes: Any, name: str, k: int) -> None:
    """Triangle k of a flat-shaded node (vertices 3k..3k+2) and its normals removed."""
    vertices, _ = meshes.nodes[name]
    rows = [3 * k, 3 * k + 1, 3 * k + 2]
    kept = np.delete(vertices, rows, axis=0)
    meshes.nodes[name] = (kept, np.arange(len(kept)).reshape(-1, 3))
    meshes.normals[name] = np.delete(meshes.normals[name], rows, axis=0)


def _no_cliff(pack: Path, tiles: dict[str, Any], tid: str) -> None:
    del tiles[tid][1].nodes["edge_cliff-col"]


def _no_cap(pack: Path, tiles: dict[str, Any], tid: str) -> None:
    del tiles[tid][1].nodes["edge_bottom"]


def _shared_vertex(pack: Path, tiles: dict[str, Any], tid: str) -> None:
    vertices, faces = tiles[tid][1].nodes["edge_cliff-col"]
    faces = faces.copy()
    faces[1, 0] = faces[0, 0]
    tiles[tid][1].nodes["edge_cliff-col"] = (vertices, faces)


def _ledge_gap(pack: Path, tiles: dict[str, Any], tid: str) -> None:
    vertices, faces = tiles[tid][1].nodes["edge_cliff-col"]
    _drop_triangle(tiles[tid][1], "edge_cliff-col", _ledge_on_ring(pack, vertices, faces))


def _no_face(pack: Path, tiles: dict[str, Any], tid: str) -> None:
    """The face triangle with the longest bottom edge removed, so one stretch of the
    slab's outline has no face down to bottom_y."""
    meshes = tiles[tid][1]
    vertices, faces = meshes.nodes["edge_cliff-col"]
    bottom = float(_json(pack / "boundary.json")["cliff"]["bottom_y"])
    tri = vertices[faces]
    low = np.abs(tri[:, :, 2] - bottom) <= FLOAT32_M
    best, index = 0.0, -1
    for k in np.flatnonzero(low.sum(axis=1) == 2):
        a, b = tri[k, low[k], :2]
        if (length := float(np.hypot(*(a - b)))) > best:
            best, index = length, int(k)
    assert best > 1.0
    _drop_triangle(meshes, "edge_cliff-col", index)


def _no_cap_triangle(pack: Path, tiles: dict[str, Any], tid: str) -> None:
    meshes = tiles[tid][1]
    _, area = _geometric_normals(meshes.nodes["edge_bottom"][0].reshape(-1, 3, 3))
    _drop_triangle(meshes, "edge_bottom", int(np.argmax(area)))


def _tilted_normal(pack: Path, tiles: dict[str, Any], tid: str) -> None:
    """The three vertex normals of the largest cliff triangle tilted by about 0.1 rad."""
    meshes = tiles[tid][1]
    vertices, faces = meshes.nodes["edge_cliff-col"]
    _, area = _geometric_normals(vertices[faces])
    k = int(np.argmax(area))
    normals = meshes.normals["edge_cliff-col"].copy()
    tilted = normals[faces[k, 0]] + np.array([0.1, 0.1, 0.1])
    normals[faces[k]] = tilted / np.linalg.norm(tilted)
    meshes.normals["edge_cliff-col"] = normals


def _one_normal_off(pack: Path, tiles: dict[str, Any], tid: str) -> None:
    """One vertex normal of the largest cap triangle tilted: a smooth-shaded vertex."""
    meshes = tiles[tid][1]
    vertices, faces = meshes.nodes["edge_bottom"]
    _, area = _geometric_normals(vertices[faces])
    normals = meshes.normals["edge_bottom"].copy()
    tilted = normals[faces[int(np.argmax(area)), 0]] + np.array([0.1, 0.0, 0.0])
    normals[faces[int(np.argmax(area)), 0]] = tilted / np.linalg.norm(tilted)
    meshes.normals["edge_bottom"] = normals


def _cliff_moved_to_the_neighbour(pack: Path, tiles: dict[str, Any], tid: str) -> None:
    """The tile's edge_cliff-col also copied into another tile the boundary passes
    through, as if the build had assigned its pieces to both."""
    manifest = _json(pack / "manifest.json")
    doc = _json(pack / "boundary.json")
    through = sorted(_through([_local(p) for p in doc["boundary"]["ring"]], manifest["tiles"]))
    other = next(t for t in through if t != tid)
    source, target = tiles[tid][1], tiles[other][1]
    v0, _ = target.nodes["edge_cliff-col"]
    v1, _ = source.nodes["edge_cliff-col"]
    merged = np.concatenate([v0, v1])
    target.nodes["edge_cliff-col"] = (merged, np.arange(len(merged)).reshape(-1, 3))
    target.normals["edge_cliff-col"] = np.concatenate(
        [target.normals["edge_cliff-col"], source.normals["edge_cliff-col"]]
    )


def _moved(meshes: Any, name: str, vertices: np.ndarray, changed: np.ndarray) -> None:
    """`vertices` stored for the node, with the normals of every triangle using a
    `changed` vertex set to its new face normal (as a build writing these positions
    would), so only the check under test sees the change."""
    _, faces = meshes.nodes[name]
    meshes.nodes[name] = (vertices, faces)
    normals = meshes.normals[name].copy()
    touched = np.flatnonzero(changed[faces].any(axis=1))
    geometric, _ = _geometric_normals(vertices[faces[touched]])
    normals[faces[touched]] = geometric[:, None, :]
    meshes.normals[name] = normals


def _below_bottom(pack: Path, tiles: dict[str, Any], tid: str) -> None:
    vertices, faces = tiles[tid][1].nodes["edge_cliff-col"]
    vertices = vertices.copy()
    lowest = int(np.argmin(vertices[:, 2]))
    vertices[lowest, 2] -= 0.01
    _moved(tiles[tid][1], "edge_cliff-col", vertices, np.arange(len(vertices)) == lowest)


def _ledge_height(pack: Path, tiles: dict[str, Any], tid: str) -> None:
    """Every ledge vertex at one fence or roadblock end raised by 1 cm."""
    vertices, faces = tiles[tid][1].nodes["edge_cliff-col"]
    k = _ledge_on_ring(pack, vertices, faces)
    corner = vertices[faces[k, 0]]
    doc = _json(pack / "boundary.json")
    ends = np.array([_local(e) for i in _items(doc) for e in (i["start"], i["end"])])
    x, n = ends[np.argmin(np.hypot(*(ends - corner[:2]).T))]
    normal, _ = _geometric_normals(vertices[faces])
    up = np.repeat(normal[:, 2] > 0.5, 3)
    at = up & (np.hypot(vertices[:, 0] - x, vertices[:, 1] - n) <= MM)
    vertices = vertices.copy()
    vertices[at, 2] += 0.01
    _moved(tiles[tid][1], "edge_cliff-col", vertices, at)


def _over_playable(pack: Path, tiles: dict[str, Any], tid: str) -> None:
    """A ledge vertex on the ring moved 1 cm into the playable area."""
    vertices, faces = tiles[tid][1].nodes["edge_cliff-col"]
    k = _ledge_on_ring(pack, vertices, faces)
    ring = Ring.from_coords(DocRing.of(_json(pack / "boundary.json")).coords)
    vertices = vertices.copy()
    changed = np.zeros(len(vertices), dtype=bool)
    for index in faces[k]:
        x, n = vertices[index, :2]
        if ring.line().distance(Point(x, n)) <= FLOAT32_M:
            normal = ring.normal(ring.segment_index(ring.project(float(x), float(n))))
            vertices[index, :2] -= normal * 0.01
            changed[index] = True
            break
    _moved(tiles[tid][1], "edge_cliff-col", vertices, changed)


CLIFF_CORRUPTIONS: dict[str, tuple[Callable[[Path, dict[str, Any], str], None], str]] = {
    "no-edge-cliff-in-a-ring-tile": (_no_cliff, "passes through without edge_cliff-col"),
    "no-cap": (_no_cap, "without edge_bottom"),
    "shared-vertex": (_shared_vertex, "faces share vertices"),
    "gap-in-the-ledge": (_ledge_gap, "gap along the boundary"),
    "vertex-below-bottom-y": (_below_bottom, "lowest cliff vertex"),
    "ledge-off-a-fence-end-height": (_ledge_height, "no ledge vertex at its end"),
    "ledge-over-the-playable-area": (_over_playable, r"reaches 0\.01\d* m over the playable"),
    "missing-face-triangle": (_no_face, "face doesn't reach cliff.bottom_y"),
    "missing-cap-triangle": (_no_cap_triangle, "edge_bottom leaves .* m² of the slab"),
    "vertex-normals-off-their-face": (_tilted_normal, r"vertex normal is 0\.1\d* off its face"),
    "one-vertex-normal-off": (_one_normal_off, "vertices of a face have different normals"),
    "ledge-also-in-another-tile": (_cliff_moved_to_the_neighbour, "belongs to another tile"),
}


@pytest.mark.parametrize("case", sorted(CLIFF_CORRUPTIONS))
def test_ac14_each_cliff_pack_check_catches_a_corrupted_tile(
    case: str, somero_area: SomeroArea
) -> None:
    from mapgen.build.pack_checks import check_cliff

    mutate, message = CLIFF_CORRUPTIONS[case]
    pack = somero_area.pack
    manifest = _json(pack / "manifest.json")
    tiles = {t["id"]: (t, read_glb_meshes(pack / t["file"])) for t in manifest["tiles"]}
    assert check_cliff(pack, manifest, tiles) == len(tiles)  # the pack passes
    mutate(pack, tiles, _first_ring_tile(pack, manifest))
    with pytest.raises(PackCheckError, match=message):
        check_cliff(pack, manifest, tiles)


def test_ac14_the_planarity_check_catches_a_bent_quad() -> None:
    """Two triangles sharing an edge and the stored normal (0, 0, 1), one with its far
    corner 1 cm up: a quad bent along its diagonal. A flat one passes."""
    from mapgen.build.pack_checks import TileMeshes, _planar

    def quad(lift: float) -> TileMeshes:
        v = np.array([[0, 0, 0], [2, 0, 0], [2, 2, 0], [0, 0, 0], [2, 2, 0], [0, 2, lift]])
        normals = np.tile([0.0, 0.0, 1.0], (6, 1))
        faces = np.arange(6).reshape(2, 3)
        return TileMeshes(
            {"edge_cliff-col": (v.astype(float), faces)}, [], {"edge_cliff-col": normals}
        )

    _planar("t", "edge_cliff-col", quad(0.0))
    with pytest.raises(PackCheckError, match=r"isn't planar \(0\.0100 m off"):
        _planar("t", "edge_cliff-col", quad(0.01))


@pytest.mark.parametrize("keep", [False, True], ids=["moved", "copied"])
def test_ac14_the_cliff_pack_check_catches_a_cliff_node_in_a_tile_off_the_boundary(
    keep: bool, edge_rect_build: EdgeMap
) -> None:
    """edge_rect's 4 x 4 tiles: the rectangle runs through the 12 outer ones. An outer
    tile's edge_cliff-col moved (or copied) into an inner tile, as a build assigning
    pieces to the wrong tile would write it."""
    from mapgen.build.pack_checks import check_cliff

    pack = edge_rect_build.pack
    manifest = _json(pack / "manifest.json")
    tiles = {t["id"]: (t, read_glb_meshes(pack / t["file"])) for t in manifest["tiles"]}
    doc = _json(pack / "boundary.json")
    through = _through([_local(p) for p in doc["boundary"]["ring"]], manifest["tiles"])
    inner = sorted(set(tiles) - through)
    assert len(through) == 12 and len(inner) == 4
    assert check_cliff(pack, manifest, tiles) == 12
    source, target = tiles[sorted(through)[0]][1], tiles[inner[0]][1]
    target.nodes["edge_cliff-col"] = source.nodes["edge_cliff-col"]
    target.normals["edge_cliff-col"] = source.normals["edge_cliff-col"]
    if not keep:
        del source.nodes["edge_cliff-col"]
    message = "doesn't pass through" if keep else "passes through without edge_cliff-col"
    with pytest.raises(PackCheckError, match=message):
        check_cliff(pack, manifest, tiles)


@pytest.mark.parametrize("which", ["edge_custom", "edge_rect"])
def test_ac14_the_cliff_pack_checks_pass_on_the_full_builds(
    which: str, request: pytest.FixtureRequest
) -> None:
    from mapgen.build.pack_checks import check_cliff

    edge: EdgeMap = request.getfixturevalue(f"{which}_build")
    manifest = _json(edge.pack / "manifest.json")
    assert check_cliff(edge.pack, manifest) == len({t for t in _cliff_pack(which, edge.pack).cliff})


def test_ac14_check_pack_runs_the_cliff_checks(
    somero_area: SomeroArea, monkeypatch: pytest.MonkeyPatch
) -> None:
    import mapgen.build.pack_checks as pack_checks

    def failing(*args: Any, **kwargs: Any) -> int:
        raise PackCheckError("cliff checked")

    monkeypatch.setattr(pack_checks, "check_cliff", failing)
    with pytest.raises(PackCheckError, match="cliff checked"):
        check_pack(
            somero_area.pack,
            somero_area.work,
            somero_area.config.roads,
            world_edge=somero_area.config.world_edge,
        )


def test_ac14_the_report_lists_the_cliff(somero_area: SomeroArea) -> None:
    report = _json(somero_area.work / "10-world-edge" / "report.json")["cliff"]
    doc = _json(somero_area.pack / "boundary.json")
    assert report["bottom_y"] == doc["cliff"]["bottom_y"]
    assert report["ledge_m"] == doc["cliff"]["ledge_m"]
    assert report["corners"] == {"mitre": 4}  # the test-area square
    pack = _cliff_pack("somero", somero_area.pack)
    triangles = report["triangles"]
    assert triangles["edge_cliff"] == sum(len(n.triangles) for n in pack.cliff.values())
    assert triangles["edge_bottom"] == sum(len(n.triangles) for n in pack.caps.values())
    assert report["pieces_moved_to_another_tile"] == report["caps_moved_to_another_tile"] == []
