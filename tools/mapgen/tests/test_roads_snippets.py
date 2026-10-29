"""Roads on hand-written OSM snippets, built end to end (OSM file -> pack).

Criterion numbers (`acN`) refer to docs/specs/roads-and-road-graph.md. Two synthetic
maps, both with a `roads` block that differs from Somero's (criterion 18):

- `attributes`: short separate ways on the sloped plane DEM, one per tag case (5, 6, 7, 14).
- `bridges`: a valley along local y at x = 0 and a hill, with tagged bridges, inferred
  bridges over water, excluded crossings, a tunnel and a bridge cut by the area edge
  (11, 12, 13, 13a, 13b, 18). The shared pack checks (pack_checks.py) run on it too.

Every expected number comes from `SNIPPET_ROADS`, the DEM functions or the snippet
layout below. Nothing touches the network.
"""

import copy
import json
import unicodedata
import xml.etree.ElementTree as ET
from collections.abc import Callable
from dataclasses import dataclass, field
from pathlib import Path
from typing import Any

import numpy as np
import pytest
import shapely
import yaml
from shapely.geometry import LineString

from conftest import (
    FIXED_NOW,
    FIXTURE_ORIGIN,
    FIXTURE_YAML,
    TmpRepo,
    plane_z,
    read_tile,
    write_fixture_raw,
)
from mapgen.build import BuildReport, run_build
from mapgen.config import MapConfig
from mapgen.geo import map_to_wgs84
from pack_checks import (
    ROAD_SURFACES,
    RoadSummary,
    TriangleSampler,
    check_pack,
    local_points,
)

MAP_ID = "snippetville"
CRS = "EPSG:3067"

# Deliberately unlike maps/somero/map.yaml and the testtown fixture (criterion 18).
SNIPPET_ROADS: dict[str, Any] = {
    "surface_offset_m": 0.07,
    "default_speed_kmh": 45,
    "bridge": {
        "deck_thickness_m": 0.6,
        "railing_height_m": 1.1,
        "railing_width_m": 0.25,
        "min_clearance_m": 1.5,
        "ramp_length_m": 4.0,
        "inferred_waterways": ["river", "stream", "ditch"],
    },
    "classes": {
        "primary": {"width_m": 8.5, "lanes": 2, "drivable": True, "walkable": False},
        "tertiary": {"width_m": 7.0, "lanes": 2, "drivable": True, "walkable": False,
                     "speed_kmh": 55},
        "residential": {"width_m": 5.5, "lanes": 2, "drivable": True, "walkable": True,
                        "speed_kmh": 35},
        "service": {"width_m": 4.0, "lanes": 1, "drivable": True, "walkable": True,
                    "speed_kmh": 25},
        "driveway": {"width_m": 3.0, "lanes": 1, "drivable": True, "walkable": True,
                     "speed_kmh": 15},
        "parking_aisle": {"width_m": 5.0, "lanes": 1, "drivable": True, "walkable": True,
                          "speed_kmh": 10},
        "cycleway": {"width_m": 3.0, "lanes": 1, "drivable": False, "walkable": True},
        "footway": {"width_m": 2.0, "lanes": 1, "drivable": False, "walkable": True},
        "pedestrian": {"width_m": 5.0, "lanes": 1, "drivable": False, "walkable": True},
    },
}  # fmt: skip
CLASSES = SNIPPET_ROADS["classes"]
OFFSET = SNIPPET_ROADS["surface_offset_m"]
BRIDGE = SNIPPET_ROADS["bridge"]


# Synthetic OSM and DEM.


@dataclass
class Osm:
    """Hand-written OSM data in local metres (converted to WGS84 when written)."""

    nodes: dict[int, tuple[float, float, dict[str, str]]] = field(default_factory=dict)
    ways: dict[int, tuple[list[int], dict[str, str]]] = field(default_factory=dict)
    relations: dict[int, tuple[list[tuple[int, str]], dict[str, str]]] = field(default_factory=dict)
    next_node: int = 1

    def node(self, x: float, y: float, tags: dict[str, str] | None = None) -> int:
        node_id = self.next_node
        self.next_node += 1
        self.nodes[node_id] = (x, y, tags or {})
        return node_id

    def way(
        self, way_id: int, points: list[tuple[float, float] | int], tags: dict[str, str]
    ) -> list[int]:
        """A way through new nodes at (x, y) points, or existing node ids."""
        refs = [p if isinstance(p, int) else self.node(*p) for p in points]
        self.ways[way_id] = (refs, tags)
        return refs

    def write(self, path: Path) -> None:
        root = ET.Element("osm", version="0.6", generator="mapgen test snippets")
        ox, oy = FIXTURE_ORIGIN
        for node_id, (x, y, tags) in sorted(self.nodes.items()):
            lon, lat = map_to_wgs84(x + ox, y + oy, CRS)
            node = ET.SubElement(root, "node", id=str(node_id), lat=f"{lat:.9f}", lon=f"{lon:.9f}")
            for k, v in tags.items():
                ET.SubElement(node, "tag", k=k, v=v)
        for way_id, (refs, tags) in sorted(self.ways.items()):
            way = ET.SubElement(root, "way", id=str(way_id))
            for ref in refs:
                ET.SubElement(way, "nd", ref=str(ref))
            for k, v in tags.items():
                ET.SubElement(way, "tag", k=k, v=v)
        for rel_id, (members, tags) in sorted(self.relations.items()):
            rel = ET.SubElement(root, "relation", id=str(rel_id))
            for ref, role in members:
                ET.SubElement(rel, "member", type="way", ref=str(ref), role=role)
            for k, v in tags.items():
                ET.SubElement(rel, "tag", k=k, v=v)
        path.parent.mkdir(parents=True, exist_ok=True)
        ET.ElementTree(root).write(path, encoding="utf-8", xml_declaration=True)


def write_dem(path: Path, z: Callable[[np.ndarray, np.ndarray], np.ndarray]) -> None:
    """A north-up GeoTIFF of z(x_local, y_local) at pixel centres, 2 m, around the origin
    (the same grid as conftest.write_plane_dem)."""
    import rasterio
    from rasterio.transform import from_origin

    ox, oy = FIXTURE_ORIGIN
    half, res = 220.0, 2.0
    size = int(round(2 * half / res))
    cols = ox - half + (np.arange(size) + 0.5) * res - ox
    rows = oy + half - (np.arange(size) + 0.5) * res - oy
    gx, gy = np.meshgrid(cols, rows)
    with rasterio.open(
        path,
        "w",
        driver="GTiff",
        width=size,
        height=size,
        count=1,
        dtype="float32",
        crs=CRS,
        transform=from_origin(ox - half, oy + half, res, res),
        nodata=-9999.0,
    ) as dst:
        dst.write(z(gx, gy).astype("float32"), 1)


VALLEY_DEPTH = 10.0
HILL = (90.0, -100.0, 8.0, 15.0)  # centre x, y, height, radius


def valley_z(x: np.ndarray, y: np.ndarray) -> np.ndarray:
    """A tilted plane with a valley along y at x = 0 and a hill."""
    hx, hy, height, radius = HILL
    hill = height * np.exp(-((x - hx) ** 2 + (y - hy) ** 2) / radius**2)
    return 100.0 + 0.02 * x + 0.01 * y - VALLEY_DEPTH * np.exp(-((x / 12.0) ** 2)) + hill


@dataclass
class SnippetBuild:
    config: MapConfig
    pack: Path
    work: Path
    report: BuildReport
    logs: list[str]
    graph: dict[str, Any]
    roads_report: dict[str, Any]

    def edges(self, way_id: int) -> list[dict[str, Any]]:
        return [e for e in self.graph["edges"] if e["osm_way"] == way_id]

    def edge(self, way_id: int) -> dict[str, Any]:
        (edge,) = self.edges(way_id)
        return edge


def build_snippets(root: Path, osm: Osm, dem: Callable[..., np.ndarray] | None) -> SnippetBuild:
    data = yaml.safe_load(FIXTURE_YAML.read_text(encoding="utf-8"))
    data["id"] = MAP_ID
    data["roads"] = copy.deepcopy(SNIPPET_ROADS)
    repo = TmpRepo(root)
    (root / "tools" / "mapgen").mkdir(parents=True, exist_ok=True)
    config = repo.load(data)
    raw = write_fixture_raw(repo.raw_dir(MAP_ID), MAP_ID)  # plane DEM, manifest
    osm.write(raw / "osm" / f"{MAP_ID}.osm")
    if dem is not None:
        write_dem(raw / "mml" / "dem_2m" / "plane.tif", dem)
    logs: list[str] = []
    pack, work = root / "pack", root / "work"
    report = run_build(config, "test", raw, pack, work, now=lambda: FIXED_NOW, log=logs.append)
    graph = json.loads((pack / "road_graph.json").read_text(encoding="utf-8"))
    roads_report = json.loads((work / "04-roads" / "report.json").read_text(encoding="utf-8"))
    return SnippetBuild(config, pack, work, report, logs, graph, roads_report)


# The attributes map: one short way per case at y = ROW0 + k * ROW_STEP, x = 20..40.

ROW0, ROW_STEP = -140.0, 2.5
DEF = "default"
OSM = "osm"

# way id -> (tags, expectations). Expectations: "class", "drivable"/"walkable" as
# (value, source), "speed"/"lanes"/"width" as (value, source), "oneway", "skip" (reason),
# "invalid" (the tag that falls back), "names".
CASES: dict[int, tuple[dict[str, str], dict[str, Any]]] = {
    # AC5: class.
    101: ({"highway": "service"}, {"class": "service"}),
    102: ({"highway": "service", "service": "driveway"}, {"class": "driveway"}),
    103: ({"highway": "service", "service": "parking_aisle"}, {"class": "parking_aisle"}),
    104: ({"highway": "service", "service": "alley"}, {"class": "service"}),
    # AC5: class defaults.
    110: ({"highway": "primary"}, {"drivable": (False, DEF)}),  # placeholder, set below
    111: ({"highway": "residential"}, {}),
    112: ({"highway": "cycleway"}, {}),
    113: ({"highway": "footway"}, {}),
    114: ({"highway": "pedestrian"}, {}),
    # AC5: access tags.
    121: ({"highway": "primary", "access": "no", "foot": "yes"},
          {"drivable": (False, OSM), "walkable": (True, OSM)}),
    122: ({"highway": "cycleway", "access": "no", "motor_vehicle": "yes"},
          {"drivable": (True, OSM), "walkable": (False, OSM)}),
    123: ({"highway": "residential", "motor_vehicle": "no"},
          {"drivable": (False, OSM), "walkable": (True, DEF)}),
    124: ({"highway": "residential", "motor_vehicle": "private"}, {"drivable": (False, OSM)}),
    125: ({"highway": "residential", "motorcar": "no"}, {"drivable": (False, OSM)}),
    126: ({"highway": "residential", "motorcar": "private"}, {"drivable": (False, OSM)}),
    127: ({"highway": "cycleway", "motor_vehicle": "destination"},
          {"drivable": (True, OSM), "walkable": (True, DEF), "speed": (45, DEF)}),
    128: ({"highway": "footway", "motor_vehicle": "designated"}, {"drivable": (True, OSM)}),
    129: ({"highway": "footway", "motorcar": "yes"}, {"drivable": (True, OSM)}),
    130: ({"highway": "residential", "foot": "no"},
          {"drivable": (True, DEF), "walkable": (False, OSM)}),
    131: ({"highway": "primary", "foot": "yes"}, {"walkable": (True, OSM)}),
    132: ({"highway": "primary", "foot": "designated"}, {"walkable": (True, OSM)}),
    # Skipped ways.
    120: ({"highway": "residential", "access": "no"}, {"skip": "no_access"}),
    133: ({"highway": "residential", "motor_vehicle": "no", "foot": "no"}, {"skip": "no_access"}),
    134: ({"highway": "cycleway", "access": "no"}, {"skip": "no_access"}),
    140: ({"highway": "pedestrian", "area": "yes"}, {"skip": "area"}),
    141: ({"highway": "construction"}, {"skip": "class_not_configured"}),
    142: ({"highway": "proposed"}, {"skip": "class_not_configured"}),
    143: ({"highway": "steps"}, {"skip": "class_not_configured"}),
    # AC6: values and sources.
    150: ({"highway": "residential", "maxspeed": "30"}, {"speed": (30, OSM)}),
    151: ({"highway": "residential", "maxspeed": "20 mph"}, {"speed": (32, OSM)}),  # 20 * 1.609344
    152: ({"highway": "residential", "maxspeed": "FI:urban"},
          {"speed": (35, DEF), "invalid": "maxspeed"}),
    153: ({"highway": "residential", "maxspeed": "none"},
          {"speed": (35, DEF), "invalid": "maxspeed"}),
    154: ({"highway": "residential", "maxspeed": "signals"},
          {"speed": (35, DEF), "invalid": "maxspeed"}),
    155: ({"highway": "primary"}, {"speed": (45, DEF)}),  # class without speed_kmh
    156: ({"highway": "primary", "maxspeed": "80"}, {"speed": (80, OSM)}),
    157: ({"highway": "residential", "lanes": "3"}, {"lanes": (3, OSM)}),
    158: ({"highway": "residential", "lanes": "two"}, {"lanes": (2, DEF), "invalid": "lanes"}),
    159: ({"highway": "residential", "lanes": "0"}, {"lanes": (2, DEF), "invalid": "lanes"}),
    160: ({"highway": "residential", "width": "3"}, {"width": (3.0, OSM)}),
    161: ({"highway": "residential", "width": "3.5 m"}, {"width": (3.5, OSM)}),
    162: ({"highway": "residential", "width": "0"}, {"width": (5.5, DEF), "invalid": "width"}),
    163: ({"highway": "residential", "width": "wide"}, {"width": (5.5, DEF), "invalid": "width"}),
    164: ({"highway": "residential", "width": "-2"}, {"width": (5.5, DEF), "invalid": "width"}),
    165: ({"highway": "cycleway", "maxspeed": "20"}, {"speed": (None, None)}),
    166: ({"highway": "residential", "width": "4", "lanes": "1"},
          {"width": (4.0, OSM), "lanes": (1, OSM)}),
    # AC7: one-way (+1: along the way, -1: stored reversed, 0: two-way).
    170: ({"highway": "residential", "oneway": "yes"}, {"oneway": 1}),
    171: ({"highway": "residential", "oneway": "true"}, {"oneway": 1}),
    172: ({"highway": "residential", "oneway": "1"}, {"oneway": 1}),
    173: ({"highway": "residential", "oneway": "-1"}, {"oneway": -1}),
    174: ({"highway": "residential", "oneway": "no"}, {"oneway": 0}),
    175: ({"highway": "residential"}, {"oneway": 0}),
    176: ({"highway": "residential", "oneway": "reversible"}, {"oneway": 0}),
    177: ({"highway": "residential", "junction": "roundabout"}, {"oneway": 1}),
    178: ({"highway": "residential", "junction": "roundabout", "oneway": "no"}, {"oneway": 0}),
    # AC14: name fields.
    180: ({"highway": "residential"}, {"names": (None, None, None)}),
    181: ({"highway": "residential", "name": "", "name:sv": "", "ref": ""},
          {"names": (None, None, None)}),
    182: ({"highway": "residential", "name": "   ", "ref": "  "}, {"names": (None, None, None)}),
    183: ({"highway": "residential", "name": "  Härkälä  ", "name:sv": " Åbovägen ",
           "ref": " 52 "}, {"names": ("Härkälä", "Åbovägen", "52")}),
    184: ({"highway": "residential", "name": unicodedata.normalize("NFD", "Härkälä")},
          {"names": ("Härkälä", None, None)}),
    185: ({"highway": "primary", "name": "Somerontie;Joensuuntie", "ref": "52;2341"},
          {"names": ("Somerontie;Joensuuntie", None, "52;2341")}),
}  # fmt: skip

# Class defaults (AC5): the expected flags come from the config.
for _way, (_tags, _expect) in CASES.items():
    if 110 <= _way <= 114:
        cls = CLASSES[_tags["highway"]]
        _expect.clear()
        _expect.update(
            {
                "drivable": (cls["drivable"], DEF),
                "walkable": (cls["walkable"], DEF),
                "class": _tags["highway"],
            }
        )

CLOSED_ROUNDABOUT = 190
OUTSIDE = 191  # one node inside the area, two outside: clipped at the edge


def _attributes_osm() -> Osm:
    osm = Osm()
    for k, (way_id, (tags, _)) in enumerate(sorted(CASES.items())):
        y = ROW0 + k * ROW_STEP
        osm.way(way_id, [(20.0, y), (30.0, y), (40.0, y)], tags)
    y = ROW0 + len(CASES) * ROW_STEP
    loop = osm.node(20.0, y)
    osm.way(
        CLOSED_ROUNDABOUT,
        [loop, (30.0, y), (30.0, y + 6.0), (20.0, y + 6.0), loop],
        {"highway": "residential", "junction": "roundabout"},
    )
    osm.way(OUTSIDE, [(140.0, -60.0), (160.0, -60.0), (170.0, -60.0)], {"highway": "residential"})
    return osm


@pytest.fixture(scope="module")
def attributes(tmp_path_factory: pytest.TempPathFactory) -> SnippetBuild:
    osm = _attributes_osm()
    assert ROW0 + (len(CASES) + 3) * ROW_STEP < 150  # the layout fits the test area
    return build_snippets(tmp_path_factory.mktemp("attributes"), osm, dem=None)


def _skipped(build: SnippetBuild) -> dict[int, str]:
    return {int(s["osm_way"].split("/")[1]): s["reason"] for s in build.roads_report["skipped"]}


def test_ac5_classes_flags_and_their_sources(attributes: SnippetBuild) -> None:
    for way_id, (tags, expect) in CASES.items():
        if "skip" in expect:
            continue
        edge = attributes.edge(way_id)
        cls = expect.get("class", "service" if tags["highway"] == "service" else tags["highway"])
        assert edge["class"] == cls, way_id
        defaults = CLASSES[cls]
        drivable = expect.get("drivable", (defaults["drivable"], DEF))
        walkable = expect.get("walkable", (defaults["walkable"], DEF))
        assert (edge["drivable"], edge["drivable_source"]) == drivable, way_id
        assert (edge["walkable"], edge["walkable_source"]) == walkable, way_id


def test_ac5_skipped_ways_are_reported_with_their_reason(attributes: SnippetBuild) -> None:
    skipped = _skipped(attributes)
    expected = {w: e["skip"] for w, (_, e) in CASES.items() if "skip" in e}
    assert skipped == expected
    included = {e["osm_way"] for e in attributes.graph["edges"]}
    assert not included & set(skipped)
    # OUTSIDE has one node in the area: its line is clipped at the area edge, not skipped.
    (outside,) = attributes.edges(OUTSIDE)
    assert outside["to"].startswith("cut:way/") or outside["from"].startswith("cut:way/")
    counts = attributes.roads_report["ways"]
    assert counts["included"] + counts["skipped"] == counts["highway_ways_in_area"]
    assert counts["highway_ways_in_area"] == len(CASES) + 2  # + roundabout and OUTSIDE


def test_ac6_osm_values_win_and_fallbacks_are_counted(attributes: SnippetBuild) -> None:
    for way_id, (_, expect) in CASES.items():
        if "skip" in expect:
            continue
        edge = attributes.edge(way_id)
        cls = CLASSES[edge["class"]]
        speed_default = cls.get("speed_kmh", SNIPPET_ROADS["default_speed_kmh"])
        if edge["drivable"]:
            speed = expect.get("speed", (speed_default, DEF))
        else:
            speed = expect.get("speed", (None, None))
            assert speed == (None, None), way_id
        assert (edge["speed_kmh"], edge["speed_source"]) == speed, way_id
        assert (edge["lanes"], edge["lanes_source"]) == expect.get("lanes", (cls["lanes"], DEF))
        assert (edge["width_m"], edge["width_source"]) == expect.get("width", (cls["width_m"], DEF))
    invalid: dict[str, int] = {}
    for _, expect in CASES.values():
        if "invalid" in expect:
            invalid[expect["invalid"]] = invalid.get(expect["invalid"], 0) + 1
    assert attributes.roads_report["invalid_tags"] == invalid


def test_ac7_oneway_directions_and_reversed_storage(attributes: SnippetBuild) -> None:
    osm = _attributes_osm()
    for way_id, (_, expect) in CASES.items():
        if "oneway" not in expect:
            continue
        edge = attributes.edge(way_id)
        refs = osm.ways[way_id][0]
        direction = expect["oneway"]
        assert edge["oneway"] is (direction != 0), way_id
        first, last = f"osm:node/{refs[0]}", f"osm:node/{refs[-1]}"
        xs = [p[0] for p in edge["points"]]
        if direction < 0:
            assert (edge["from"], edge["to"]) == (last, first), way_id
            assert xs == sorted(xs, reverse=True), way_id  # the way runs west to east
        else:
            assert (edge["from"], edge["to"]) == (first, last), way_id
            assert xs == sorted(xs), way_id


def test_closed_roundabout_has_no_zero_length_edge(attributes: SnippetBuild) -> None:
    edges = attributes.edges(CLOSED_ROUNDABOUT)
    assert edges and all(e["oneway"] for e in edges)
    assert all(e["length_m"] > 0 for e in edges)
    loop = _attributes_osm().ways[CLOSED_ROUNDABOUT][0][0]
    assert edges[0]["from"] == edges[-1]["to"] == f"osm:node/{loop}"
    # The 10 m x 6 m loop on the plane: 2D perimeter, stretched by the slope.
    perimeter = 2 * (10.0 + 6.0)
    assert sum(e["length_m"] for e in edges) == pytest.approx(perimeter, rel=2e-3)


def test_ac14_name_fields_are_trimmed_nfc_and_kept_as_tagged(attributes: SnippetBuild) -> None:
    for edge in attributes.graph["edges"]:
        assert {"name", "name_sv", "ref"} <= set(edge), edge["id"]
    for way_id, (_, expect) in CASES.items():
        if "names" in expect:
            edge = attributes.edge(way_id)
            assert (edge["name"], edge["name_sv"], edge["ref"]) == expect["names"], way_id
    raw = (attributes.pack / "road_graph.json").read_bytes()
    assert "Härkälä".encode() in raw and "Åbovägen".encode() in raw
    assert b"\\u00e4" not in raw and b"\\u00c5" not in raw
    raw.decode("utf-8")  # valid UTF-8
    nfd = unicodedata.normalize("NFD", "Härkälä").encode()
    assert nfd not in raw


def test_ac18_attributes_use_this_maps_roads_block(attributes: SnippetBuild) -> None:
    edge = attributes.edge(175)  # plain residential
    residential = CLASSES["residential"]
    assert (edge["width_m"], edge["lanes"], edge["speed_kmh"]) == (
        residential["width_m"],
        residential["lanes"],
        residential["speed_kmh"],
    )
    # Surfaces are this map's offset above the plane.
    tile = read_tile(attributes.pack / "tiles" / "tile_x0_y-1.glb")
    v, _ = tile.nodes["road-col"]
    assert np.abs(v[:, 2] - plane_z(v[:, 0], v[:, 1]) - OFFSET).max() < 1e-3


# The bridges map.

ROWS = {
    "tagged": 120.0,
    "multi": 95.0,
    "river": 70.0,
    "lake_way": 45.0,
    "lake_relation": 20.0,
    "culvert": -5.0,
    "negative_layer": -30.0,
    "ford_node": -55.0,
    "ford_way": -80.0,
    "tunnel": -100.0,
    "low_bridge": -125.0,
    "both_tags": -140.0,
    "cut_bridge": 140.0,
    "mid_join": 132.0,
}
SPAN_X = 30.0  # inferred and tagged valley bridges run from -SPAN_X to SPAN_X
TERTIARY = {"highway": "tertiary"}
TAGGED = {"highway": "tertiary", "bridge": "yes", "layer": "1"}

# way ids
TAGGED_BRIDGE, MULTI_A, MULTI_B = 1002, 1012, 1013
RIVER_ROAD, LAKE_WAY_ROAD, LAKE_REL_ROAD = 1021, 1031, 1041
CULVERT_ROAD, NEG_LAYER_ROAD, FORD_NODE_ROAD, FORD_WAY_ROAD = 1051, 1061, 1071, 1091
TUNNEL, LOW_BRIDGE, BOTH_TAGS, CUT_BRIDGE = 1081, 1101, 1121, 1111
MID_BRIDGE, MID_PATH = 1131, 1132  # a footway joins the bridge at a node in its middle
MID_X = -10.0  # where it joins
RIVER, LAKE_WAY, LAKE_REL, BROKEN_REL = "osm:way/2001", "osm:way/2002", "osm:relation/2101", 2102


def _crossing_road(osm: Osm, way_id: int, y: float, tags: dict[str, str]) -> list[int]:
    return osm.way(way_id, [(-120.0, y), (-SPAN_X, y), (SPAN_X, y), (120.0, y)], tags)


def _bridges_osm() -> Osm:
    osm = Osm()
    # Tagged bridge over the valley, with ground roads on both sides, over a river too.
    y = ROWS["tagged"]
    a, b = osm.node(-SPAN_X, y), osm.node(SPAN_X, y)
    osm.way(1001, [(-120.0, y), a], TERTIARY)
    osm.way(TAGGED_BRIDGE, [a, b], TAGGED)
    osm.way(1003, [b, (120.0, y)], TERTIARY)
    osm.way(2009, [(0.0, y - 10), (0.0, y + 10)], {"waterway": "river"})
    # A span of two bridge ways joined at a node no other way uses.
    y = ROWS["multi"]
    a, m, b = osm.node(-SPAN_X, y), osm.node(2.0, y), osm.node(SPAN_X, y)
    osm.way(1011, [(-120.0, y), a], TERTIARY)
    osm.way(MULTI_A, [a, m], TAGGED)
    osm.way(MULTI_B, [m, b], TAGGED)
    osm.way(1014, [b, (120.0, y)], TERTIARY)
    # AC13a: a waterway line, a water-area way and a water multipolygon.
    y = ROWS["river"]
    _crossing_road(osm, RIVER_ROAD, y, TERTIARY)
    osm.way(2001, [(0.0, y - 10), (0.0, y + 10)], {"waterway": "river"})
    y = ROWS["lake_way"]
    _crossing_road(osm, LAKE_WAY_ROAD, y, TERTIARY)
    ring = [(-6.0, y - 7), (6.0, y - 7), (6.0, y + 7), (-6.0, y + 7)]
    first = osm.node(*ring[0])
    osm.way(2002, [first, *ring[1:], first], {"natural": "water"})
    y = ROWS["lake_relation"]
    _crossing_road(osm, LAKE_REL_ROAD, y, TERTIARY)
    c0, c1, c2, c3 = (
        osm.node(x, yy) for x, yy in ((-6, y - 7), (6, y - 7), (6, y + 7), (-6, y + 7))
    )
    osm.way(2003, [c0, c1, c2], {})
    osm.way(2004, [c2, c3, c0], {})
    osm.relations[2101] = (
        [(2003, "outer"), (2004, "outer")],
        {"type": "multipolygon", "natural": "water"},
    )
    # A water relation that can't be closed: a member way is missing (left out, reported).
    osm.way(2010, [(130.0, -145.0), (140.0, -145.0)], {})
    osm.relations[BROKEN_REL] = (
        [(2010, "outer"), (2999, "outer")],
        {"type": "multipolygon", "natural": "water"},
    )
    # Excluded crossings: culvert, negative layer, ford node, ford way.
    y = ROWS["culvert"]
    _crossing_road(osm, CULVERT_ROAD, y, TERTIARY)
    osm.way(2005, [(0.0, y - 10), (0.0, y + 10)], {"waterway": "ditch", "tunnel": "culvert"})
    y = ROWS["negative_layer"]
    _crossing_road(osm, NEG_LAYER_ROAD, y, TERTIARY)
    osm.way(2006, [(0.0, y - 10), (0.0, y + 10)], {"waterway": "stream", "layer": "-1"})
    y = ROWS["ford_node"]
    ford = osm.node(0.0, y, {"ford": "yes"})
    osm.way(FORD_NODE_ROAD, [(-120.0, y), (-SPAN_X, y), ford, (SPAN_X, y), (120.0, y)], TERTIARY)
    osm.way(2007, [(0.0, y - 10), ford, (0.0, y + 10)], {"waterway": "river"})
    y = ROWS["ford_way"]
    _crossing_road(osm, FORD_WAY_ROAD, y, {**TERTIARY, "ford": "yes"})
    osm.way(2008, [(0.0, y - 10), (0.0, y + 10)], {"waterway": "river"})
    # AC13: a tunnel under the hill.
    y = ROWS["tunnel"]
    a, b = osm.node(60.0, y), osm.node(120.0, y)
    osm.way(1080, [(30.0, y), a], TERTIARY)
    osm.way(TUNNEL, [a, (HILL[0], y), b], {"highway": "tertiary", "tunnel": "yes", "layer": "-1"})
    osm.way(1082, [b, (140.0, y)], TERTIARY)
    # AC12: a bridge on flat ground (its deck is below min_clearance_m).
    y = ROWS["low_bridge"]
    a, b = osm.node(-120.0, y), osm.node(-60.0, y)
    osm.way(1100, [(-145.0, y), a], TERTIARY)
    osm.way(LOW_BRIDGE, [a, b], TAGGED)
    osm.way(1102, [b, (-40.0, y)], TERTIARY)
    # Tagged both bridge and tunnel, no ground road at either end.
    osm.way(BOTH_TAGS, [(-140.0, ROWS["both_tags"]), (-100.0, ROWS["both_tags"])],
            {"highway": "tertiary", "bridge": "yes", "tunnel": "yes"})  # fmt: skip
    # AC11: a bridge cut by the area edge (x = 150).
    y = ROWS["cut_bridge"]
    a = osm.node(110.0, y)
    osm.way(1110, [(60.0, y), a], TERTIARY)
    osm.way(CUT_BRIDGE, [a, (140.0, y), (170.0, y)], TAGGED)
    # A footway from the north joining a bridge at a node in the middle of its span.
    y = ROWS["mid_join"]
    a, m, b = osm.node(-SPAN_X, y), osm.node(MID_X, y), osm.node(SPAN_X, y)
    osm.way(1130, [(-60.0, y), a], TERTIARY)
    osm.way(MID_BRIDGE, [a, m, b], TAGGED)
    osm.way(1133, [b, (60.0, y)], TERTIARY)
    osm.way(MID_PATH, [(MID_X, y + 16.0), m], {"highway": "footway"})
    return osm


@pytest.fixture(scope="module")
def bridges(tmp_path_factory: pytest.TempPathFactory) -> SnippetBuild:
    return build_snippets(tmp_path_factory.mktemp("bridges"), _bridges_osm(), dem=valley_z)


@pytest.fixture(scope="module")
def bridge_checks(bridges: SnippetBuild) -> RoadSummary:
    summary = check_pack(bridges.pack, bridges.work, bridges.config.roads)
    assert summary.roads is not None
    return summary.roads


def _tiles(build: SnippetBuild) -> dict[str, Any]:
    manifest = json.loads((build.pack / "manifest.json").read_text(encoding="utf-8"))
    return {t["id"]: read_tile(build.pack / t["file"]) for t in manifest["tiles"]}


def _terrain(build: SnippetBuild, xy: np.ndarray) -> np.ndarray:
    """Terrain mesh height (not the DEM) at local points."""
    manifest = json.loads((build.pack / "manifest.json").read_text(encoding="utf-8"))
    result = np.full(len(xy), np.nan)
    for tile in manifest["tiles"]:
        min_x, min_y, max_x, max_y = tile["bounds_local"]
        todo = np.isnan(result) & (xy[:, 0] >= min_x) & (xy[:, 0] <= max_x)
        todo &= (xy[:, 1] >= min_y) & (xy[:, 1] <= max_y)
        if todo.any():
            sampler = TriangleSampler(*read_tile(build.pack / tile["file"]).nodes["terrain-col"])
            result[todo] = sampler.height(xy[todo])
    return result


def test_ac11_snippet_bridges_pass_the_deck_checks(
    bridges: SnippetBuild, bridge_checks: RoadSummary
) -> None:
    spans = sorted(sorted(c.ways) for c in bridge_checks.chains)
    assert spans == sorted(
        [[TAGGED_BRIDGE], [MULTI_A, MULTI_B], [RIVER_ROAD], [LAKE_WAY_ROAD], [LAKE_REL_ROAD],
         [LOW_BRIDGE], [BOTH_TAGS], [CUT_BRIDGE], [MID_BRIDGE]]
    )  # fmt: skip
    # Decks crossing the tile edge at x = 0 are split into parts with a suffix.
    names = bridge_checks.deck_nodes
    assert (
        names[f"bridge_osm_way_{TAGGED_BRIDGE}_p0-col"]
        != names[f"bridge_osm_way_{TAGGED_BRIDGE}_p1-col"]
    )
    assert f"bridge_osm_way_{MULTI_B}-col" in names  # x = 2..30: one tile only
    assert f"bridge_osm_way_{LOW_BRIDGE}-col" in names
    low, high = bridge_checks.deck_rise_m
    print(f"\nsnippet decks: driving surface minus line {low * 1e3:.4f}..{high * 1e3:.4f} mm")


def test_ac11_multi_way_span_is_one_straight_line_over_the_valley(bridges: SnippetBuild) -> None:
    a, b = bridges.edge(MULTI_A), bridges.edge(MULTI_B)
    assert a["to"] == b["from"]
    joint = local_points(a["points"][-1])[0]
    start, end = local_points(a["points"][0])[0], local_points(b["points"][-1])[0]
    t = (joint[0] - start[0]) / (end[0] - start[0])
    assert joint[2] == pytest.approx(start[2] + t * (end[2] - start[2]), abs=1e-5)
    terrain = _terrain(bridges, joint[None, :2])[0]
    # The joint is on the line high above the valley floor, not on the terrain.
    assert joint[2] - terrain - OFFSET > VALLEY_DEPTH / 2


def test_ac11_bridge_cut_by_the_area_edge_ends_on_the_terrain_at_the_cut(
    bridges: SnippetBuild,
) -> None:
    (edge,) = bridges.edges(CUT_BRIDGE)
    assert edge["to"].startswith("cut:way/")
    end = local_points(edge["points"][-1])[0]
    assert end[0] == pytest.approx(150.0, abs=1e-6)
    assert end[2] == pytest.approx(_terrain(bridges, end[None, :2])[0] + OFFSET, abs=1e-3)


def test_a_way_joining_mid_span_ends_on_the_deck_line(bridges: SnippetBuild) -> None:
    """The node is inside the span, so its height is on the span's line for both ways."""
    first, second = bridges.edges(MID_BRIDGE)
    (path,) = bridges.edges(MID_PATH)
    joint = first["to"]
    assert second["from"] == joint and path["to"] == joint and not path["bridge"]
    nodes = {n["id"]: n for n in bridges.graph["nodes"]}
    assert nodes[joint]["kind"] == "junction"
    position = np.array(nodes[joint]["position"])
    assert np.array_equal(np.array(path["points"][-1]), position)
    assert np.array_equal(np.array(first["points"][-1]), position)
    start = local_points(first["points"][0])[0]
    end = local_points(second["points"][-1])[0]
    (at,) = local_points(nodes[joint]["position"])
    t = (at[0] - start[0]) / (end[0] - start[0])
    assert at[2] == pytest.approx(start[2] + t * (end[2] - start[2]), abs=1e-5)
    terrain = _terrain(bridges, at[None, :2])[0]
    assert at[2] - terrain - OFFSET > VALLEY_DEPTH / 4  # on the deck, not the valley floor
    # The footway's other end is on the ground surface.
    (far,) = local_points(path["points"][0])
    assert far[2] == pytest.approx(_terrain(bridges, far[None, :2])[0] + OFFSET, abs=1e-3)


def test_ac12_low_deck_gives_a_clearance_warning_and_the_build_succeeds(
    bridges: SnippetBuild,
) -> None:
    assert bridges.report.road_edges == len(bridges.graph["edges"])
    warnings = {tuple(w["osm_ways"]): w for w in bridges.roads_report["clearance_warnings"]}
    warning = warnings[(f"osm:way/{LOW_BRIDGE}",)]
    assert warning["min_clearance_m"] < BRIDGE["min_clearance_m"]
    x, y = warning["position_local"]
    assert -120.0 <= x <= -60.0 and y == pytest.approx(ROWS["low_bridge"], abs=0.01)
    # The valley bridges clear the valley floor: no warning.
    for way in (TAGGED_BRIDGE, RIVER_ROAD, LAKE_WAY_ROAD, LAKE_REL_ROAD):
        assert (f"osm:way/{way}",) not in warnings
    assert bridges.report.clearance_warnings == len(warnings)


def test_ac13_tunnel_is_in_the_graph_only_with_linear_heights(bridges: SnippetBuild) -> None:
    edges = bridges.edges(TUNNEL)
    assert edges and all(e["tunnel"] and not e["bridge"] for e in edges)
    (edge,) = edges
    p = local_points(edge["points"])
    s = np.concatenate([[0], np.cumsum(np.linalg.norm(np.diff(p[:, :2], axis=0), axis=1))])
    line = p[0, 2] + (p[-1, 2] - p[0, 2]) * s / s[-1]
    assert np.abs(p[:, 2] - line).max() <= 1e-5
    # Under the hill top: well below the terrain.
    assert _terrain(bridges, p[1:2, :2])[0] - p[1, 2] > HILL[2] / 2
    width = CLASSES["tertiary"]["width_m"]
    inner = LineString(p[:, :2]).buffer(width / 2 - 0.01, cap_style="flat")
    # Leave out the round caps of the ground roads that meet the tunnel's ends.
    inner = inner.intersection(shapely.box(p[0, 0] + width, -150, p[-1, 0] - width, 150))
    for tile_id, tile in _tiles(bridges).items():
        assert not any(str(TUNNEL) in name for name in tile.nodes), tile_id
        for name in ROAD_SURFACES:
            if name in tile.nodes:
                v = tile.nodes[name][0]
                assert not shapely.contains_xy(inner, v[:, 0], v[:, 1]).any(), (tile_id, name)


def test_bridge_and_tunnel_tags_make_a_counted_bridge(bridges: SnippetBuild) -> None:
    (edge,) = bridges.edges(BOTH_TAGS)
    assert edge["bridge"] and edge["bridge_source"] == "osm" and not edge["tunnel"]
    assert bridges.roads_report["ways"]["bridge_and_tunnel_tagged"] == 1


SUSPECTED = {RIVER_ROAD: RIVER, LAKE_WAY_ROAD: LAKE_WAY, LAKE_REL_ROAD: LAKE_REL}
SUSPECTED_ROWS = {
    RIVER_ROAD: ROWS["river"],
    LAKE_WAY_ROAD: ROWS["lake_way"],
    LAKE_REL_ROAD: ROWS["lake_relation"],
}


def test_ac13a_crossings_without_a_bridge_tag_are_inferred_bridges(bridges: SnippetBuild) -> None:
    entries = {e["osm_way"]: e for e in bridges.roads_report["suspected_missing_bridges"]}
    assert set(entries) == {f"osm:way/{w}" for w in SUSPECTED}
    assert bridges.report.suspected_missing_bridges == len(SUSPECTED)
    origin = np.array(FIXTURE_ORIGIN)
    for way_id, water in SUSPECTED.items():
        entry = entries[f"osm:way/{way_id}"]
        assert entry["water"] == water and entry["class"] == "tertiary"
        assert {"name", "name_sv", "ref"} <= set(entry)
        gx, gy, gz = entry["position_local"]
        x, y = gx, -gz
        assert x == pytest.approx(0.0, abs=0.01)
        assert y == pytest.approx(SUSPECTED_ROWS[way_id], abs=0.01)
        wx, wy, wz = entry["position_world"]
        assert (wx, wy) == pytest.approx((x + origin[0], y + origin[1]), abs=1e-3)
        assert wz == pytest.approx(gy, abs=1e-3)
        assert entry["crs"] == CRS
        lon, lat = map_to_wgs84(wx, wy, CRS)
        assert entry["position_wgs84"]["lat"] == pytest.approx(lat, abs=1e-6)
        assert entry["position_wgs84"]["lon"] == pytest.approx(lon, abs=1e-6)
        # Three edges: ground, the inferred bridge, ground.
        edges = bridges.edges(way_id)
        assert [e["bridge_source"] for e in edges] == [None, "inferred", None]
        bridge = edges[1]
        ends = local_points(bridge["points"])[[0, -1]]
        assert np.allclose(np.abs(ends[:, 0]), SPAN_X, atol=0.01)
        # The reported height is the deck line's at the crossing.
        t = (x - ends[0, 0]) / (ends[1, 0] - ends[0, 0])
        assert gy == pytest.approx(ends[0, 2] + t * (ends[1, 2] - ends[0, 2]), abs=1e-3)


def test_ac13a_no_ground_surface_under_any_deck(
    bridges: SnippetBuild, bridge_checks: RoadSummary
) -> None:
    """Nothing runs under the snippet bridges, so no road or path vertex is inside a deck
    footprint (the ground surfaces stop at the span ends with flat caps)."""
    tiles = _tiles(bridges)
    for chain in bridge_checks.chains:
        width = max(e["width_m"] for e in chain.edges)
        inner = LineString(chain.points[:, :2]).buffer(width / 2 - 0.01, cap_style="flat")
        inner = inner.buffer(-0.01)
        for tile_id, tile in tiles.items():
            for name in ROAD_SURFACES:
                if name in tile.nodes:
                    v = tile.nodes[name][0]
                    inside = shapely.contains_xy(inner, v[:, 0], v[:, 1])
                    assert not inside.any(), (sorted(chain.ways), tile_id, name, v[inside][:3])


def test_ac13a_excluded_crossings_are_counted_and_give_no_bridge(bridges: SnippetBuild) -> None:
    report = bridges.roads_report
    excluded = {(e["osm_way"], e["water"], e["reason"]) for e in report["excluded_crossings"]}
    assert excluded == {
        (f"osm:way/{CULVERT_ROAD}", "osm:way/2005", "culvert"),
        (f"osm:way/{NEG_LAYER_ROAD}", "osm:way/2006", "negative_layer"),
        (f"osm:way/{FORD_NODE_ROAD}", "osm:way/2007", "ford_node"),
        (f"osm:way/{FORD_WAY_ROAD}", "osm:way/2008", "ford_way"),
    }
    counts = report["excluded_crossing_counts"]
    assert set(counts) == {"culvert", "negative_layer", "ford_node", "ford_way"}
    assert sum(counts.values()) == len(report["excluded_crossings"])
    for way_id in (CULVERT_ROAD, NEG_LAYER_ROAD, FORD_NODE_ROAD, FORD_WAY_ROAD):
        assert all(not e["bridge"] for e in bridges.edges(way_id)), way_id
    # The tagged bridge over a river isn't inferred again.
    assert all(e["bridge_source"] == "osm" for e in bridges.edges(TAGGED_BRIDGE))
    left_out = {r["id"] for r in report["water"]["relations_left_out"]}
    assert left_out == {f"osm:relation/{BROKEN_REL}"}


def test_ac18_bridges_use_this_maps_roads_block(
    bridges: SnippetBuild, bridge_checks: RoadSummary
) -> None:
    """The deck checks classify vertices by this map's thickness and railing height and
    fail on any other value; here also the widths and the inferred waterway list."""
    assert bridge_checks.max_surface_error_m <= 1e-3  # measured against OFFSET = 0.07
    for way in (TAGGED_BRIDGE, CULVERT_ROAD):
        assert bridges.edges(way)[0]["width_m"] == CLASSES["tertiary"]["width_m"]
        assert bridges.edges(way)[0]["speed_kmh"] == CLASSES["tertiary"]["speed_kmh"]
    assert bridges.roads_report["water"]["inferred_waterways"] == BRIDGE["inferred_waterways"]
    tiles = _tiles(bridges)
    name = f"bridge_osm_way_{LOW_BRIDGE}-col"
    (v,) = [t.nodes[name][0] for t in tiles.values() if name in t.nodes]
    edge = bridges.edge(LOW_BRIDGE)
    half = edge["width_m"] / 2
    assert np.abs(v[:, 1] - ROWS["low_bridge"]).max() == pytest.approx(half, abs=1e-3)
    z = local_points(edge["points"])[:, 2]
    top = z.max()
    assert v[:, 2].max() == pytest.approx(top + BRIDGE["railing_height_m"], abs=1e-3)
    assert v[:, 2].min() == pytest.approx(z.min() - BRIDGE["deck_thickness_m"], abs=1e-3)
