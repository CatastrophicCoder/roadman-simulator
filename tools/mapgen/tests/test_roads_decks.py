"""Deck heights, deck extents, underpasses, the dip report and the ground-above-deck warning.

Spec: docs/specs/roads-and-road-graph.md, "Deck heights", "Deck extent", "Underpasses",
"Dip report", "Tunnels", criteria 4, 13c-13f and 20 (the reader contract fields). Snippet
maps on a synthetic DEM with cuttings, a dip and a hump; one with a point cloud (lidar fit)
and one without (geometric line). 13c runs on the full Somero build when its raw data is
present.
"""

import copy
import json
import shutil
from pathlib import Path
from typing import Any

import numpy as np
import pytest
import yaml

from conftest import (
    FIXED_NOW,
    FIXTURE_ORIGIN,
    FIXTURE_YAML,
    REPO_ROOT,
    SOMERO_YAML,
    TmpRepo,
    lidar_entry,
    write_fixture_raw,
    write_laz,
)
from mapgen import paths
from mapgen.build import run_build
from mapgen.build.pack_checks import TriangleSampler, check_pack, local_points, read_glb_meshes
from mapgen.build.roads.model import committed_schema
from mapgen.config import load_map_config
from test_roads_snippets import SNIPPET_ROADS, Osm, write_dem

MAP_ID = "snippetville"
OFFSET = SNIPPET_ROADS["surface_offset_m"]
CUT_Y = 60.0  # the cuttings run along x at this y
ROAD_TUNNEL, CYCLE_TUNNEL = 3001, 3002  # road over a tunnel=yes cycleway in a cutting
ROAD_LAYER, FOOT_LAYER = 3011, 3012  # road over a layer=-1 footway in a cutting
ROAD_DIP, FOOT_DIP = 3021, 3022  # road crossing an untagged footway in a 2 m dip
HUMP_BRIDGE = 3032  # a bridge with ground 0.2 m above its deck
X_TUNNEL, X_LAYER, X_DIP, X_HUMP = -90.0, 10.0, 100.0, -90.0
# Walk-only (cycleway) bridges over a valley at x 20..40, with low ground east of them:
# A meets the path again at x ~90, beyond a junction at x 60 and beyond rim_search_m;
# B never meets it (flat low ground to the path's end) and gets a graded ramp.
WALK_A, WALK_A_EAST, WALK_A_SPUR = 3041, 3042, 3043
WALK_B, WALK_B_EAST = 3051, 3052
Y_A, Y_B = -20.0, -45.0
X_JUNCTION = 60.0
B_LOW = 97.4  # the flat ground east of bridge B


def _window(x: np.ndarray, x0: float, x1: float) -> np.ndarray:
    """1 between x0 and x1, falling off smoothly over 10 m outside."""
    below = np.clip((x - (x0 - 10.0)) / 10.0, 0.0, 1.0)
    above = np.clip(((x1 + 10.0) - x) / 10.0, 0.0, 1.0)
    return np.minimum(below, above)


def snippet_z(x: np.ndarray, y: np.ndarray) -> np.ndarray:
    """A plane with a 4 m cutting along y = 60 (x -140..40), a 2 m dip along y = 60
    (x 70..130) and a 0.2 m hump at (-90, -90)."""
    plane = 100.0 + 0.01 * x
    profile = np.exp(-(((y - CUT_Y) / 4.0) ** 2))
    cutting = 4.0 * profile * _window(x, -140.0, 40.0)
    dip = 2.0 * profile * _window(x, 70.0, 130.0)
    hump = 0.2 * np.exp(-(((x - X_HUMP) ** 2 + (y + 90.0) ** 2) / 6.0**2))
    band_a = _window(y, Y_A - 6.0, Y_A + 6.0)
    band_b = _window(y, Y_B - 6.0, Y_B + 6.0)
    valley = 6.0 * _window(x, 32.0, 28.0) * np.maximum(band_a, band_b)  # x 22..38
    low_a = 3.0 * _window(x, 50.0, 70.0) * band_a  # x 40..80, back up by x 90
    low_b = (3.0 + 0.01 * (x - 40.0)) * np.clip((x - 28.0) / 10.0, 0.0, 1.0) * band_b
    return plane - cutting - dip + hump - valley - low_a - low_b


def snippet_osm() -> Osm:
    osm = Osm()
    for road, low, x, tags in (
        (
            ROAD_TUNNEL,
            CYCLE_TUNNEL,
            X_TUNNEL,
            {"highway": "cycleway", "tunnel": "yes", "layer": "-1"},
        ),
        (ROAD_LAYER, FOOT_LAYER, X_LAYER, {"highway": "footway", "layer": "-1"}),
        (ROAD_DIP, FOOT_DIP, X_DIP, {"highway": "footway"}),
    ):
        osm.way(road, [(x, 20.0), (x, 100.0)], {"highway": "tertiary", "name": f"Road {road}"})
        osm.way(low, [(x - 40.0, CUT_Y), (x + 30.0, CUT_Y)], tags)
    a, b = osm.node(X_HUMP, -120.0), osm.node(X_HUMP, -60.0)
    osm.way(3031, [(X_HUMP, -140.0), a], {"highway": "tertiary"})
    osm.way(HUMP_BRIDGE, [a, b], {"highway": "tertiary", "bridge": "yes", "layer": "1"})
    osm.way(3033, [b, (X_HUMP, -40.0)], {"highway": "tertiary"})
    cycle = {"highway": "cycleway"}
    bridge = {"highway": "cycleway", "bridge": "yes", "layer": "1"}
    for way, y in ((WALK_A, Y_A), (WALK_B, Y_B)):
        w, e = osm.node(20.0, y), osm.node(40.0, y)
        osm.way(way - 1, [(-20.0, y), w], cycle)
        osm.way(way, [w, e], bridge)
        if way == WALK_A:
            j = osm.node(X_JUNCTION, y)
            osm.way(WALK_A_EAST, [e, j, (100.0, y), (140.0, y)], cycle)
            osm.way(WALK_A_SPUR, [j, (X_JUNCTION, y - 5.0)], {"highway": "footway"})
        else:  # nodes every 10 m, so the graph samples the graded ramp
            osm.way(WALK_B_EAST, [e, *[(x, y) for x in np.arange(50.0, 141.0, 10.0)]], cycle)
    return osm


def deck_height(x: float) -> float:
    """The real road surface over the cuttings: level with the plane (no cutting)."""
    return 100.0 + 0.01 * x + OFFSET


def write_snippet_lidar(raw: Path) -> None:
    """Class 2 points: the road over the tunnel cutting at the plane's height (its deck),
    and the cycleway in the cutting below it (lower, must be ignored by the fit)."""
    ox, oy = FIXTURE_ORIGIN
    ys = np.arange(30.0, 90.0, 0.5)
    xs = np.arange(X_TUNNEL - 3.0, X_TUNNEL + 3.01, 1.0)
    gx, gy = np.meshgrid(xs, ys)
    road = np.column_stack([gx.ravel(), gy.ravel(), 100.0 + 0.01 * gx.ravel()])
    cx = np.arange(X_TUNNEL - 30.0, X_TUNNEL + 30.0, 0.5)
    cycle = np.column_stack([cx, np.full(len(cx), CUT_Y), snippet_z(cx, np.full(len(cx), CUT_Y))])
    decks = []
    for y in (Y_A, Y_B):  # the cycleway decks, level with the plane
        gx, gy = np.meshgrid(np.arange(20.5, 40.0, 0.5), np.arange(y - 1.0, y + 1.01, 0.5))
        decks.append(np.column_stack([gx.ravel(), gy.ravel(), 100.0 + 0.01 * gx.ravel()]))
    pts = np.vstack([road, cycle, *decks])
    write_laz(
        raw / "mml" / "lidar_05p" / "snippet.laz",
        pts[:, 0] + ox,
        pts[:, 1] + oy,
        pts[:, 2],
        classification=2,
    )
    path = raw / "download-manifest.json"
    document = json.loads(path.read_text(encoding="utf-8"))
    document["files"].append(lidar_entry("mml/lidar_05p/snippet.laz"))
    path.write_text(json.dumps(document), encoding="utf-8")


def build(root: Path, lidar: bool) -> dict[str, Any]:
    data = yaml.safe_load(FIXTURE_YAML.read_text(encoding="utf-8"))
    data["id"] = MAP_ID
    data["roads"] = copy.deepcopy(SNIPPET_ROADS)
    if lidar:
        data["sources"]["mml_lidar"] = {"adapter": "mml_lidar", "enabled": True, "options": {}}
    repo = TmpRepo(root)
    (root / "tools" / "mapgen").mkdir(parents=True, exist_ok=True)
    config = repo.load(data)
    raw = write_fixture_raw(repo.raw_dir(MAP_ID), MAP_ID)
    snippet_osm().write(raw / "osm" / f"{MAP_ID}.osm")
    write_dem(raw / "mml" / "dem_2m" / "plane.tif", snippet_z)
    if lidar:
        write_snippet_lidar(raw)
    logs: list[str] = []
    pack, work = root / "pack", root / "work"
    run_build(config, "test", raw, pack, work, now=lambda: FIXED_NOW, log=logs.append)
    return {
        "config": config,
        "pack": pack,
        "work": work,
        "logs": logs,
        "graph": json.loads((pack / "road_graph.json").read_text(encoding="utf-8")),
        "report": json.loads((work / "04-roads" / "report.json").read_text(encoding="utf-8")),
    }


@pytest.fixture(scope="module")
def geometric(tmp_path_factory: pytest.TempPathFactory) -> dict[str, Any]:
    return build(tmp_path_factory.mktemp("decks-geometric"), lidar=False)


@pytest.fixture(scope="module")
def lidar(tmp_path_factory: pytest.TempPathFactory) -> dict[str, Any]:
    return build(tmp_path_factory.mktemp("decks-lidar"), lidar=True)


def _span(build_: dict[str, Any], way: int) -> dict[str, Any]:
    (span,) = [s for s in build_["report"]["bridges"] if f"osm:way/{way}" in s["osm_ways"]]
    return span


def _nodes(build_: dict[str, Any]) -> dict[str, dict[str, Any]]:
    return {n["id"]: n for n in build_["graph"]["nodes"]}


def _surface(build_: dict[str, Any], name: str, xy: np.ndarray) -> np.ndarray:
    manifest = json.loads((build_["pack"] / "manifest.json").read_text(encoding="utf-8"))
    h = np.full(len(xy), np.nan)
    for tile in manifest["tiles"]:
        nodes = read_glb_meshes(build_["pack"] / tile["file"]).nodes
        if name in nodes:
            h = np.where(np.isnan(h), TriangleSampler(*nodes[name]).height(xy, 1e-4), h)
    return h


# 13d: underpasses.


@pytest.mark.parametrize(
    ("road", "low", "tunnel"), [(ROAD_TUNNEL, CYCLE_TUNNEL, True), (ROAD_LAYER, FOOT_LAYER, False)]
)
def test_13d_road_over_a_lower_way_gets_an_underpass_deck_with_span_end_nodes(
    geometric: dict[str, Any], road: int, low: int, tunnel: bool
) -> None:
    edges = [e for e in geometric["graph"]["edges"] if e["osm_way"] == road]
    deck = [e for e in edges if e["bridge"]]
    assert deck and all(e["bridge_source"] == "inferred_underpass" for e in deck)
    nodes = _nodes(geometric)
    ends = {deck[0]["from"], deck[-1]["to"]}
    assert all(nodes[k]["kind"] == "span_end" and nodes[k]["osm_node"] is None for k in ends)
    assert all(k.startswith(f"span:way/{road}:") for k in ends)
    span = _span(geometric, road)
    assert span["source"] == "inferred_underpass" and span["lower_ways"] == [f"osm:way/{low}"]
    # A straight deck, level with the road beyond the cutting (the plane's height).
    pts = np.concatenate([local_points(e["points"]) for e in deck])
    assert np.abs(pts[:, 2] - deck_height(pts[0, 0])).max() < 0.05
    # The way underneath follows the terrain, has a surface with collision under the deck
    # and keeps its tunnel flag.
    (under,) = [e for e in geometric["graph"]["edges"] if e["osm_way"] == low]
    assert under["tunnel"] is tunnel and not under["bridge"]
    p = local_points(under["points"])
    surface = _surface(geometric, "path-col", np.array([[pts[0, 0], CUT_Y]]))
    assert not np.isnan(surface).any()
    assert surface[0] < deck_height(pts[0, 0]) - 3.0  # down in the cutting, not on the deck
    assert np.all(p[:, 2] < deck_height(pts[0, 0]) + 0.01)
    if tunnel:
        assert f"osm:way/{low}" in geometric["report"]["tunnels_under_decks"]


def test_geometric_line_without_a_point_cloud(geometric: dict[str, Any]) -> None:
    span = _span(geometric, ROAD_TUNNEL)
    assert span["method"] == "geometric" and span["fallback_reason"] == "no point cloud"
    assert span["lidar_end_heights"] is None
    assert len(span["geometric_end_heights"]) == 2


def test_span_end_nodes_meet_the_road_and_pass_the_pack_checks(geometric: dict[str, Any]) -> None:
    summary = check_pack(geometric["pack"], geometric["work"], geometric["config"].roads)
    assert summary.roads is not None and summary.roads.max_step_m <= 0.02
    span_ends = [n for n in geometric["graph"]["nodes"] if n["kind"] == "span_end"]
    assert len(span_ends) >= 4
    ends = {e["node"]: e for s in geometric["report"]["bridges"] for e in s["ends"]}
    for node in span_ends:
        assert node["id"] in ends and ends[node["id"]]["ramp"]


# 13e, 13f.


def test_13e_dip_without_tags_is_reported(geometric: dict[str, Any]) -> None:
    dips = geometric["report"]["suspected_dips"]
    (dip,) = [d for d in dips if d["osm_way"] == f"osm:way/{ROAD_DIP}"]
    assert dip["crossing_way"] == f"osm:way/{FOOT_DIP}"
    assert dip["class"] == "tertiary" and dip["crossing_class"] == "footway"
    assert dip["name"] == f"Road {ROAD_DIP}" and dip["name_sv"] is None and dip["ref"] is None
    assert dip["dip_m"] > SNIPPET_ROADS["bridge"]["dip_report_m"]
    assert len(dip["position_local"]) == 3 and len(dip["position_world"]) == 3
    assert set(dip["position_wgs84"]) == {"lat", "lon"}
    assert dip["position_local"][0] == pytest.approx(X_DIP, abs=1e-3)
    # The road isn't turned into a bridge, and the underpasses aren't dips.
    assert not any(e["bridge"] for e in geometric["graph"]["edges"] if e["osm_way"] == ROAD_DIP)
    assert {d["osm_way"] for d in dips} == {f"osm:way/{ROAD_DIP}"}


def test_13f_ground_above_a_deck_is_a_warning_not_a_failure(geometric: dict[str, Any]) -> None:
    warnings = geometric["report"]["warnings"]
    (warning,) = [w for w in warnings if f"osm:way/{HUMP_BRIDGE}" in w["osm_ways"]]
    assert warning["warning"] == "ground_above_deck"
    assert warning["max_rise_m"] > SNIPPET_ROADS["bridge"]["ground_above_deck_warning_m"]
    assert any("ground" in line and str(HUMP_BRIDGE) in line for line in geometric["logs"])


def test_ramp_report_gives_the_difference_and_length(geometric: dict[str, Any]) -> None:
    ramps = geometric["report"]["ramps"]
    assert ramps
    for ramp in ramps:
        assert ramp["length_m"] == SNIPPET_ROADS["bridge"]["ramp_length_m"]
        assert isinstance(ramp["height_difference_m"], float)


# Lidar fit.


def test_lidar_fit_takes_the_deck_and_ignores_the_way_below(lidar: dict[str, Any]) -> None:
    span = _span(lidar, ROAD_TUNNEL)
    assert span["method"] == "lidar", span
    assert span["lidar"]["bins_agreeing"] >= SNIPPET_ROADS["bridge"]["lidar_fit"]["min_bins"]
    assert span["lidar"]["max_residual_m"] <= SNIPPET_ROADS["bridge"]["lidar_fit"]["band_m"]
    # The top point per bin: the synthetic road rises 0.01 m/m across x, up to x + 3.
    expected = deck_height(X_TUNNEL + 3.0)
    assert all(abs(z - expected) < 0.02 for z in span["end_heights"])
    load = json.loads((lidar["work"] / "01-load" / "report.json").read_text(encoding="utf-8"))
    assert load["point_cloud"]["deck_points"] > 0
    # The geometric line is reported too; the other spans have no points: geometric.
    assert len(span["geometric_end_heights"]) == 2
    assert _span(lidar, ROAD_LAYER)["method"] == "geometric"


# The reader contract (criterion 20) in the committed schema.


def test_schema_has_span_end_nodes_and_underpass_bridges() -> None:
    schema = committed_schema()
    defs = schema["$defs"]
    assert "span_end" in defs["RoadNode"]["properties"]["kind"]["enum"]
    sources = defs["RoadEdge"]["properties"]["bridge_source"]["anyOf"]
    assert any("inferred_underpass" in s.get("enum", []) for s in sources)


# 13c on the full Somero build.

UNDERPASSES = {  # way: (crossing local x, y), lidar line ends measured over +-15 m
    813373044: ((35.3, -423.3), (91.31, 91.28)),
    813373046: ((33.7, -421.9), (91.27, 91.31)),
    813373050: ((-166.6, 404.0), (90.71, 90.83)),
    813373051: ((-157.2, 407.1), (90.63, 90.54)),
}


@pytest.fixture(scope="module")
def somero_full(tmp_path_factory: pytest.TempPathFactory) -> dict[str, Any]:
    raw = paths.raw_dir(REPO_ROOT, "somero")
    if not (raw / "osm" / "somero.osm").is_file() or not list(
        (raw / "mml" / "lidar_05p").glob("*.laz")
    ):
        pytest.skip("raw data for somero not downloaded")
    root = tmp_path_factory.mktemp("somero-full")
    shutil.copytree(SOMERO_YAML.parent, paths.map_dir(root, "somero"))
    link = paths.raw_dir(root, "somero")
    link.parent.mkdir(parents=True)
    link.symlink_to(raw, target_is_directory=True)
    config = load_map_config(paths.map_config_path(root, "somero"))
    logs: list[str] = []
    pack, work = root / "pack", root / "work"
    run_build(config, "full", link, pack, work, log=logs.append)
    return {
        "graph": json.loads((pack / "road_graph.json").read_text(encoding="utf-8")),
        "report": json.loads((work / "04-roads" / "report.json").read_text(encoding="utf-8")),
        "logs": logs,
    }


@pytest.mark.rawdata
def test_13c_somero_underpass_decks_follow_the_lidar(somero_full: dict[str, Any]) -> None:
    report = somero_full["report"]
    for span in report["bridges"]:
        assert span["method"] in ("lidar", "geometric") and "bins_agreeing" in span["lidar"]
    for way, ((x, y), (z0, z1)) in UNDERPASSES.items():
        deck = [
            local_points(e["points"])
            for e in somero_full["graph"]["edges"]
            if e["osm_way"] == way and e["bridge_source"] == "inferred_underpass"
        ]
        assert deck, way
        pts = np.concatenate(deck)
        d = np.hypot(pts[:, 0] - x, pts[:, 1] - y)
        order = np.argsort(np.hypot(pts[:, 0] - x, pts[:, 1] - y))
        assert d.min() < 15.0
        # The deck within 15 m of the crossing is within 0.3 m of the measured lidar line.
        near = pts[d <= 15.0] if (d <= 15.0).sum() >= 2 else pts[order[:2]]
        lo, hi = min(z0, z1), max(z0, z1)
        assert np.all(near[:, 2] >= lo - 0.3) and np.all(near[:, 2] <= hi + 0.3), (way, near[:, 2])
    limit = 0.05
    warned = {tuple(w["osm_ways"]) for w in report["warnings"]}
    for above in report["terrain_above_deck"]:
        if above["max_rise_m"] is not None and above["max_rise_m"] > limit:
            assert tuple(above["osm_ways"]) in warned
    assert report["suspected_dips"] == []


# Walk-only decks: the longer search across junctions, and graded ramps.


def test_walk_only_deck_meets_the_path_beyond_a_junction(lidar: dict[str, Any]) -> None:
    span = _span(lidar, WALK_A)
    assert span["method"] == "lidar"
    east = [e for e in span["ends"] if e["kind"] == "span_end"]
    (end,) = east
    cfg = SNIPPET_ROADS["bridge"]
    # Beyond the junction (20 m from the deck end) and beyond the drivable search.
    assert cfg["rim_search_m"] < end["extension_m"] <= cfg["walk_rim_search_m"]
    assert end["ramp"] and end["reason"] is None
    # The junction is on the deck now: the spur joins it at the deck's height.
    nodes = _nodes(lidar)
    (spur,) = [e for e in lidar["graph"]["edges"] if e["osm_way"] == WALK_A_SPUR]
    joint = local_points(nodes[spur["from"]]["position"])[0]
    assert joint[0] == pytest.approx(X_JUNCTION, abs=1e-3)
    assert joint[2] == pytest.approx(100.0 + 0.01 * X_JUNCTION + OFFSET, abs=0.02)
    deck = [e for e in lidar["graph"]["edges"] if e["osm_way"] == WALK_A_EAST and e["bridge"]]
    assert deck and spur["from"] in {k for e in deck for k in (e["from"], e["to"])}


def test_walk_only_deck_end_without_a_meeting_gets_a_graded_ramp(lidar: dict[str, Any]) -> None:
    cfg = SNIPPET_ROADS["bridge"]
    grade = cfg["max_ramp_grade"]
    span = _span(lidar, WALK_B)
    (end,) = [e for e in span["ends"] if e["reason"] == "no_meeting"]
    graded = end["graded_ramp"]
    assert graded["fits"] and not end["ramp"]  # no standard ramp at this end
    z_end = 100.0 + 0.01 * 40.0 + OFFSET
    dz = z_end - (B_LOW + OFFSET)
    assert graded["height_difference_m"] == pytest.approx(dz, abs=0.01)
    # Flat ground beyond: length = height difference / grade (within the 0.25 m samples).
    assert graded["nominal_length_m"] == pytest.approx(dz / grade, abs=0.01)
    assert graded["length_m"] == pytest.approx(dz / grade, abs=0.3)
    (entry,) = lidar["report"]["graded_ramps"]
    assert entry["grade"] == grade
    # The pack checks: grade along the ramp, the step at the deck end, nothing below the
    # terrain (criterion 9 with the graded ramp).
    summary = check_pack(lidar["pack"], lidar["work"], lidar["config"].roads)
    assert summary.roads is not None
    assert 0 < summary.roads.max_graded_grade <= grade + 0.005
    assert summary.roads.max_step_m <= 0.02
    # Graph points on the ramp are on the descent.
    pts = np.concatenate(
        [local_points(e["points"]) for e in lidar["graph"]["edges"] if e["osm_way"] == WALK_B_EAST]
    )
    s = pts[:, 0] - 40.0
    on = s < dz / grade - 1.0
    assert on.sum() >= 4
    assert np.allclose(pts[on, 2], z_end - grade * s[on], atol=1e-3)
    assert np.all(pts[~on, 2] >= B_LOW + OFFSET - 1e-3)
