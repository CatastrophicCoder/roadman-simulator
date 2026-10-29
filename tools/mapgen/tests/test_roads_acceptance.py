"""Roads and road graph: the acceptance criteria on the Somero test area and the config.

Criterion numbers (`acN`) refer to docs/specs/roads-and-road-graph.md. The Somero test
area is built from the committed fixture the way CI builds it (`somero_area` in
conftest.py). Expected values come from the fixture's OSM file, the map config or the
built pack, never from guessed ranges. The OSM snippet builds are in
test_roads_snippets.py; the full-map checks in test_build_rawdata.py.
"""

import json
import re
import xml.etree.ElementTree as ET
from pathlib import Path
from typing import Any

import jsonschema
import numpy as np
import pytest
import shapely
import yaml
from shapely.geometry import LineString

from conftest import (
    REPO_ROOT,
    SOMERO_FIXTURE_RAW,
    SomeroArea,
    TmpRepo,
    build_somero_fixture,
    glb_primitive,
)
from mapgen.build.area import build_area
from mapgen.build.roads.model import committed_schema, generated_schema
from mapgen.config import ConfigError, RoadsConfig
from mapgen.geo import wgs84_to_map
from pack_checks import BRIDGE_NODE, ROAD_SURFACES, check_pack, local_points

SPEC = REPO_ROOT / "docs" / "specs" / "roads-and-road-graph.md"
FIXTURE_OSM = SOMERO_FIXTURE_RAW / "osm" / "somero.osm"
ORIGIN_BRIDGE = (88027326, 813370683)  # spec "Data": the origin bridge's two primary ways
HEIKINTIE_CYCLEWAY = 88027331  # passes under the origin bridge in a cutting
SKIP_REASONS = {"class_not_configured", "area", "outside_area", "no_access"}


def _json(path: Path) -> Any:
    return json.loads(path.read_text(encoding="utf-8"))


def _osm(path: Path) -> tuple[dict[int, tuple[float, float]], dict[int, tuple[list[int], dict]]]:
    """Nodes (lon, lat) and ways (refs, tags) of an .osm file, read independently of mapgen."""
    root = ET.parse(path).getroot()
    nodes = {
        int(n.attrib["id"]): (float(n.attrib["lon"]), float(n.attrib["lat"]))
        for n in root.iter("node")
    }
    ways = {
        int(w.attrib["id"]): (
            [int(nd.attrib["ref"]) for nd in w.iter("nd")],
            {t.attrib["k"]: t.attrib["v"] for t in w.iter("tag")},
        )
        for w in root.iter("way")
    }
    return nodes, ways


def _local(lonlat: tuple[float, float], area: SomeroArea) -> np.ndarray:
    x, y = wgs84_to_map(lonlat[0], lonlat[1], area.config.crs)
    return np.array([x - area.config.origin.x, y - area.config.origin.y])


def _inside(xy: np.ndarray, area: SomeroArea) -> bool:
    b = build_area(area.config, "test").bounds
    return bool(b.min_x <= xy[0] <= b.max_x and b.min_y <= xy[1] <= b.max_y)


# AC1


def test_ac1_somero_test_area_graph_validates_against_the_committed_schema(
    somero_area: SomeroArea,
) -> None:
    assert committed_schema() == generated_schema()
    graph = _json(somero_area.pack / "road_graph.json")
    jsonschema.validate(graph, committed_schema())
    assert (graph["schema_version"], graph["map_id"], graph["area"]) == (1, "somero", "test")
    assert graph["edges"] and graph["nodes"]


# AC2


def _spec_roads_block() -> dict[str, Any]:
    """The `roads:` YAML block under "Config" in the spec, as written there."""
    text = SPEC.read_text(encoding="utf-8")
    blocks = re.findall(r"```yaml\n(.*?)```", text, flags=re.DOTALL)
    (block,) = [b for b in blocks if b.startswith("roads:")]
    return yaml.safe_load(block)["roads"]


def test_ac2_the_roads_block_in_the_spec_is_accepted(
    tmp_repo: TmpRepo, fixture_data: dict[str, Any]
) -> None:
    block = _spec_roads_block()
    fixture_data["roads"] = block
    roads = tmp_repo.load(fixture_data).roads
    assert isinstance(roads, RoadsConfig)
    assert set(roads.classes) == set(block["classes"])
    assert roads.bridge.inferred_waterways == block["bridge"]["inferred_waterways"]
    for name, values in block["classes"].items():
        assert roads.classes[name].model_dump(exclude_none=True) == values, name


@pytest.mark.parametrize(
    ("path", "value"),
    [
        (("bridge", "railing_height_m"), 0),
        (("bridge", "railing_width_m"), -0.2),
        (("bridge", "deck_thickness_m"), -1),
        (("bridge", "colour"), "grey"),
        (("bridge", "inferred_waterways"), {"river": True}),
        (("classes", "residential", "width_m"), -5.5),
        (("classes", "residential", "lanes"), -1),
        (("classes", "residential", "speed_kmh"), -40),
        (("default_speed_kmh",), 0),
        (("surface_offset_m",), -0.05),
    ],
)
def test_ac2_more_bad_roads_values_are_rejected(
    tmp_repo: TmpRepo, fixture_data: dict[str, Any], path: tuple[str, ...], value: Any
) -> None:
    target = fixture_data["roads"]
    for key in path[:-1]:
        target = target[key]
    target[path[-1]] = value
    with pytest.raises(ConfigError):
        tmp_repo.load(fixture_data)


def test_ac2_zero_min_clearance_is_accepted(
    tmp_repo: TmpRepo, fixture_data: dict[str, Any]
) -> None:
    fixture_data["roads"]["bridge"]["min_clearance_m"] = 0
    roads = tmp_repo.load(fixture_data).roads
    assert roads is not None and roads.bridge.min_clearance_m == 0


# AC3


def test_ac3_every_highway_way_in_the_test_area_is_an_edge_or_skipped_with_a_reason(
    somero_area: SomeroArea,
) -> None:
    nodes, ways = _osm(FIXTURE_OSM)
    highway = {w: refs for w, (refs, tags) in ways.items() if "highway" in tags}
    b = build_area(somero_area.config, "test").bounds
    box = shapely.box(b.min_x, b.min_y, b.max_x, b.max_y)
    inside_length = {
        w: LineString([_local(nodes[r], somero_area) for r in refs]).intersection(box).length
        for w, refs in highway.items()
    }
    # The fixture keeps the highway ways whose line meets the test area (spec "Test-area
    # fixture").
    lines = {
        w: LineString([_local(nodes[r], somero_area) for r in refs]) for w, refs in highway.items()
    }
    assert all(line.intersects(box) for line in lines.values())
    graph = _json(somero_area.pack / "road_graph.json")
    report = _json(somero_area.work / "04-roads" / "report.json")
    included = {e["osm_way"] for e in graph["edges"]}
    skipped = {int(s["osm_way"].split("/")[1]): s["reason"] for s in report["skipped"]}
    assert not included & set(skipped)
    assert included | set(skipped) == set(highway)
    assert report["ways"]["included"] == len(included)
    assert report["ways"]["skipped"] == len(skipped)
    assert report["ways"]["highway_ways_in_area"] == len(highway)
    assert len(included) + len(skipped) == len(highway)
    assert set(skipped.values()) <= SKIP_REASONS
    # Ways are clipped by their line: every way with a part of its line in the area is in.
    for way_id in included:
        assert inside_length[way_id] > 0, way_id
    for way_id, reason in skipped.items():
        if reason == "outside_area":
            assert inside_length[way_id] == 0, way_id


# AC4


def test_ac4_graph_nodes_are_osm_nodes_or_area_edge_cuts(somero_area: SomeroArea) -> None:
    osm_nodes, ways = _osm(FIXTURE_OSM)
    graph = _json(somero_area.pack / "road_graph.json")
    edge_ends: dict[str, int] = {}
    for edge in graph["edges"]:
        for key in (edge["from"], edge["to"]):
            edge_ends[key] = edge_ends.get(key, 0) + 1
        (first,) = local_points(edge["points"][0])
        (last,) = local_points(edge["points"][-1])
        nodes = {n["id"]: local_points(n["position"])[0] for n in graph["nodes"]}
        assert np.allclose(first, nodes[edge["from"]], atol=1e-6), edge["id"]
        assert np.allclose(last, nodes[edge["to"]], atol=1e-6), edge["id"]
    way_refs = {w: set(refs) for w, (refs, _) in ways.items()}
    for node in graph["nodes"]:
        ends = edge_ends[node["id"]]
        if node["kind"] == "area_edge" and node["osm_node"] is None:  # a cut
            way_id = int(re.fullmatch(r"cut:way/(\d+):\d+", node["id"]).group(1))
            assert way_id in way_refs
            continue
        osm_id = node["osm_node"]
        assert node["id"] == f"osm:node/{osm_id}" and osm_id in osm_nodes
        (xyz,) = local_points(node["position"])
        assert np.allclose(xyz[:2], _local(osm_nodes[osm_id], somero_area), atol=1e-3), node["id"]
        if node["kind"] == "area_edge":  # an OSM node on the edge, the way goes on outside
            continue
        kind = "junction" if ends >= 3 else "joint" if ends == 2 else "dead_end"
        assert node["kind"] == kind, node["id"]


def test_ac4_origin_bridge_never_connects_to_the_cycleway_under_it(
    somero_area: SomeroArea,
) -> None:
    _, ways = _osm(FIXTURE_OSM)
    graph = _json(somero_area.pack / "road_graph.json")
    cycle_refs = set(ways[HEIKINTIE_CYCLEWAY][0])
    cycle_nodes = {
        k
        for e in graph["edges"]
        if e["osm_way"] == HEIKINTIE_CYCLEWAY
        for k in (e["from"], e["to"])
    }
    for way_id in ORIGIN_BRIDGE:
        refs, tags = ways[way_id]
        assert tags.get("bridge") == "yes"
        assert not set(refs) & cycle_refs  # spec "Data": no shared OSM node
        edges = [e for e in graph["edges"] if e["osm_way"] == way_id]
        assert edges and all(e["bridge"] and e["bridge_source"] == "osm" for e in edges)
        assert not {k for e in edges for k in (e["from"], e["to"])} & cycle_nodes


# AC8, AC9, AC10, AC11, AC13b, AC15 (report), AC16


def test_ac8_to_ac11_ac13b_somero_test_area_passes_the_road_pack_checks(
    somero_area: SomeroArea,
) -> None:
    summary = check_pack(somero_area.pack, somero_area.work, somero_area.config.roads)
    roads = summary.roads
    assert roads is not None
    graph = _json(somero_area.pack / "road_graph.json")
    assert (roads.edges, roads.nodes) == (len(graph["edges"]), len(graph["nodes"]))
    # Both origin bridge ways are separate spans with their own deck node.
    spans = sorted(sorted(c.ways) for c in roads.chains)
    assert spans == [[w] for w in sorted(ORIGIN_BRIDGE)]
    decks = {int(BRIDGE_NODE.match(n).group(1)) for n in roads.deck_nodes}
    assert decks == set(ORIGIN_BRIDGE)
    print(
        f"\nsomero test area: surface max error {roads.max_surface_error_m * 1e3:.4f} mm, "
        f"graph max error {roads.max_graph_error_m * 1e3:.4f} mm, deck minus line "
        f"{roads.deck_rise_m[0] * 1e3:.4f}..{roads.deck_rise_m[1] * 1e3:.4f} mm"
    )


def test_ac15_test_area_report_gives_the_share_without_a_threshold(
    somero_area: SomeroArea,
) -> None:
    report = _json(somero_area.work / "07-road-graph" / "report.json")
    weak = report["weak_components"]
    assert report["threshold"] is None and report["meets_threshold"] is None
    assert weak["largest_share"] == round(weak["edge_counts"][0] / report["drivable_edges"], 4)
    assert [len(c) for c in weak["other_components"]] == weak["edge_counts"][1:]
    assert "strong_components" in report and "per_class" in report


def test_ac16_stage_files_are_written_in_world_coordinates(somero_area: SomeroArea) -> None:
    work = somero_area.work
    origin = np.array([somero_area.config.origin.x, somero_area.config.origin.y])
    graph = _json(somero_area.pack / "road_graph.json")
    geojson = _json(work / "07-road-graph" / "graph.geojson")
    assert "EPSG::3067" in geojson["crs"]["properties"]["name"]
    nodes = {n["id"]: local_points(n["position"])[0] for n in graph["nodes"]}
    points = [f for f in geojson["features"] if f["geometry"]["type"] == "Point"]
    assert {f["properties"]["id"] for f in points} == set(nodes)
    for feature in points:
        xy = np.array(feature["geometry"]["coordinates"]) - origin
        assert np.allclose(xy, nodes[feature["properties"]["id"]][:2], atol=1e-3)
    lines = [f for f in geojson["features"] if f["geometry"]["type"] == "LineString"]
    assert {f["properties"]["id"] for f in lines} == {e["id"] for e in graph["edges"]}
    roads_geojson = _json(work / "04-roads" / "roads.geojson")
    surfaces = {f["properties"]["surface"] for f in roads_geojson["features"]}
    assert surfaces == {"road", "path", "bridge"}
    for name in ("04-roads", "07-road-graph"):
        assert isinstance(_json(work / name / "report.json"), dict)


# AC17


def _road_nodes(pack: Path) -> dict[tuple[str, str], dict[str, bytes]]:
    """Raw GLB arrays of every road, path and bridge node, by (tile file, node name)."""
    from mapgen.build.export_glb import read_glb_node_names

    result = {}
    for tile in sorted((pack / "tiles").glob("*.glb")):
        for name in read_glb_node_names(tile):
            if name in ROAD_SURFACES or BRIDGE_NODE.match(name):
                arrays = glb_primitive(tile, name)
                result[(tile.name, name)] = {k: v.tobytes() for k, v in arrays.items()}
    return result


def test_ac17_two_builds_write_identical_road_graph_and_road_meshes(
    somero_area: SomeroArea, tmp_path: Path
) -> None:
    again = build_somero_fixture(tmp_path)
    first = (somero_area.pack / "road_graph.json").read_bytes()
    assert first == (again.pack / "road_graph.json").read_bytes()
    meshes = _road_nodes(somero_area.pack)
    assert meshes and meshes == _road_nodes(again.pack)
    for name in ("04-roads/report.json", "07-road-graph/report.json"):
        assert (somero_area.work / name).read_bytes() == (again.work / name).read_bytes()


# AC19


def test_ac19_fixture_has_the_highway_ways_with_all_their_nodes_and_no_metadata() -> None:
    nodes, ways = _osm(FIXTURE_OSM)
    highway = {w: refs for w, (refs, tags) in ways.items() if "highway" in tags}
    report = _json(SOMERO_FIXTURE_RAW / "download-manifest.json")
    (osm,) = [e for e in report["files"] if e["source"] == "osm"]
    assert osm["request"]["test_area_cut"]["highway_ways"] == len(highway)
    for way_id, refs in highway.items():
        assert all(r in nodes for r in refs), f"way {way_id} lost nodes in the cut"
    root = ET.parse(FIXTURE_OSM).getroot()
    for element in root:
        for attribute in ("user", "uid", "timestamp", "changeset", "version"):
            assert attribute not in element.attrib, (element.tag, element.attrib.get("id"))
    notice = (SOMERO_FIXTURE_RAW / "NOTICE.md").read_text(encoding="utf-8")
    assert "highway" in notice
