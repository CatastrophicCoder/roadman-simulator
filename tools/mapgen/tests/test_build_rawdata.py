"""Builds the real map's test area from the downloaded raw data (skipped without it).

Everything is written under tmp_path: the map config is copied into a temporary repo and
the raw folder is linked into it read-only. Run only these with `uv run pytest -m rawdata`.
"""

import json
import shutil
import socket
import xml.etree.ElementTree as ET
from collections import Counter
from dataclasses import dataclass
from pathlib import Path
from typing import Any

import pytest
import rasterio
from rasterio.errors import NotGeoreferencedWarning

from conftest import REPO_ROOT, SOMERO_YAML
from mapgen import paths
from mapgen.build import run_build
from mapgen.build.area import build_area
from mapgen.cli import EXIT_OK, main
from mapgen.config import MapConfig, load_map_config
from mapgen.geo import wgs84_to_map
from pack_checks import check_pack

pytestmark = [
    pytest.mark.rawdata,
    pytest.mark.filterwarnings(
        f"ignore::{NotGeoreferencedWarning.__module__}.NotGeoreferencedWarning"
    ),
]

MAP_ID = SOMERO_YAML.parent.name
RAW = paths.raw_dir(REPO_ROOT, MAP_ID)


def _require_raw_data() -> None:
    needed = [RAW / "download-manifest.json", RAW / "osm" / f"{MAP_ID}.osm"]
    folders = {"dem_2m": "*.tif", "lidar_05p": "*.laz"}
    has_mml = all(list((RAW / "mml" / name).glob(pattern)) for name, pattern in folders.items())
    if not all(p.is_file() for p in needed) or not has_mml:
        pytest.skip(f"raw data for {MAP_ID} not downloaded (run `mapgen download {MAP_ID}`)")


def _tmp_repo(tmp_path: Path) -> Path:
    """A repo with a copy of the map's config folder and a link to its raw data."""
    root = tmp_path / "repo"
    shutil.copytree(SOMERO_YAML.parent, paths.map_dir(root, MAP_ID))
    raw = paths.raw_dir(root, MAP_ID)
    raw.parent.mkdir(parents=True)
    raw.symlink_to(RAW, target_is_directory=True)
    return root


def test_raw_test_area_build(tmp_path: Path) -> None:
    _require_raw_data()
    root = _tmp_repo(tmp_path)
    config = load_map_config(paths.map_config_path(root, MAP_ID))
    raw = paths.raw_dir(root, MAP_ID)
    run_build(
        config,
        "test",
        raw,
        tmp_path / "pack",
        tmp_path / "work",
        map_dir=paths.map_dir(root, MAP_ID),
        log=lambda _: None,
    )
    summary = check_pack(tmp_path / "pack", tmp_path / "work", config.roads)
    assert summary.tile_count == 4
    assert summary.building_count > 0


def test_raw_cli_build(tmp_path: Path) -> None:
    _require_raw_data()
    root = _tmp_repo(tmp_path)
    args = ["--repo-root", str(root), "build", MAP_ID, "--area", "test"]
    assert main(args) == EXIT_OK
    assert (paths.pack_dir(root, MAP_ID) / "manifest.json").is_file()
    assert (paths.work_dir(root, MAP_ID, "test") / "01-load" / "report.json").is_file()


@dataclass
class FullBuild:
    config: MapConfig
    pack: Path
    work: Path


@pytest.fixture(scope="module")
def full_build(tmp_path_factory: pytest.TempPathFactory) -> FullBuild:
    """The whole map built once from the raw data (read-only)."""
    _require_raw_data()
    tmp_path = tmp_path_factory.mktemp("full")
    root = _tmp_repo(tmp_path)
    config = load_map_config(paths.map_config_path(root, MAP_ID))
    raw = paths.raw_dir(root, MAP_ID)
    pack, work = tmp_path / "pack", tmp_path / "work"
    run_build(
        config, "full", raw, pack, work, map_dir=paths.map_dir(root, MAP_ID), log=lambda _: None
    )
    return FullBuild(config, pack, work)


def test_raw_full_build_gets_lidar_heights_for_95_percent(full_build: FullBuild) -> None:
    """Spec docs/specs/building-heights.md, acceptance criteria 2 and 11 on the whole map."""
    import json
    import math

    config, pack, work = full_build.config, full_build.pack, full_build.work
    buildings = json.loads((pack / "buildings.json").read_text(encoding="utf-8"))["buildings"]
    assert buildings
    for building in buildings:
        assert math.isfinite(building["height_m"]) and building["height_m"] > 0, building["id"]
    lidar = [b for b in buildings if b["height_source"] == "mml_lidar"]
    assert len(lidar) >= 0.95 * len(buildings), f"{len(lidar)} of {len(buildings)}"
    assert all(b["roof_z_p90"] >= b["roof_z_median"] for b in lidar)
    report = json.loads((work / "03-buildings" / "report.json").read_text(encoding="utf-8"))
    assert report["height_source_counts"]["mml_lidar"] == len(lidar)
    assert set(report["lidar"]["rejected_counts"]) == {
        "few_points",
        "tree_cover",
        "too_low",
        "too_high",
    }
    manifest = json.loads((pack / "manifest.json").read_text(encoding="utf-8"))
    credits = manifest["credits"]
    assert "Laser scanning data 0,5 p" in {c["dataset"] for c in credits}
    textures = _check_debug_textures(config, pack, manifest)
    print(
        f"\n{MAP_ID} full: {len(lidar)}/{len(buildings)} mml_lidar, "
        f"sources {report['height_source_counts']}, lidar {report['lidar']}, "
        f"debug textures {textures}"
    )


def _check_debug_textures(config: Any, pack: Path, manifest: dict[str, Any]) -> str:
    """With the orthophoto downloaded, every tile has a texture of its size (else none)."""
    from mapgen.build.orthophoto import texture_path, texture_size
    from mapgen.geo import BBox
    from mapgen.sources.registry import enabled_sources_providing

    imagery = enabled_sources_providing(config, "imagery")
    if not imagery or not list((RAW / "mml" / "orthophoto").glob("*.jp2")):
        assert not (pack / "debug").exists()
        return "none"
    m_per_px = imagery[0].options.debug_texture_m_per_px
    assert (pack / "debug" / ".gdignore").is_file()
    assert "Orthophoto" in {c["dataset"] for c in manifest["credits"]}
    total = 0
    for tile in manifest["tiles"]:
        path = pack / texture_path(tile["id"])
        with rasterio.open(path) as src:
            assert (src.width, src.height) == texture_size(BBox(*tile["bounds_local"]), m_per_px)
        total += path.stat().st_size
    return f"{len(manifest['tiles'])} files, {total / 1e6:.1f} MB at {m_per_px} m/px"


# docs/specs/roads-and-road-graph.md


def test_ac15_full_build_largest_drivable_component_has_95_percent(full_build: FullBuild) -> None:
    """Criterion 15 on the whole map; the report lists the other components."""
    from pack_checks import check_connectivity_report

    graph = json.loads((full_build.pack / "road_graph.json").read_text(encoding="utf-8"))
    report = json.loads(
        (full_build.work / "07-road-graph" / "report.json").read_text(encoding="utf-8")
    )
    check_connectivity_report(graph, report)  # recomputed from the graph's drivable edges
    weak = report["weak_components"]
    drivable = report["drivable_edges"]
    assert weak["edge_counts"][0] >= 0.95 * drivable, weak["edge_counts"]
    assert report["threshold"] == 0.95 and report["meets_threshold"] is True
    print(
        f"\n{MAP_ID} full: {len(graph['edges'])} edges, {drivable} drivable, largest weak "
        f"component {weak['largest_share']:.2%}, components {weak['edge_counts']}"
    )


def test_ac13a_full_build_lists_its_suspected_missing_bridges(full_build: FullBuild) -> None:
    """The report lists them; the spec's 2026-10-29 data has none ("Data")."""
    report = json.loads((full_build.work / "04-roads" / "report.json").read_text("utf-8"))
    suspected = report["suspected_missing_bridges"]
    print(f"\n{MAP_ID} full: suspected missing bridges {suspected}")
    print(f"excluded crossings {report['excluded_crossing_counts']}, water {report['water']}")
    assert suspected == []


def _highway_ways(path: Path, config: MapConfig) -> set[int]:
    """Highway ways whose line meets the test area, read without mapgen's OSM reader."""
    import shapely
    from shapely.geometry import LineString

    root = ET.parse(path).getroot()
    nodes = {
        int(n.attrib["id"]): (float(n.attrib["lon"]), float(n.attrib["lat"]))
        for n in root.iter("node")
    }
    b = build_area(config, "test").bounds
    box = shapely.box(b.min_x, b.min_y, b.max_x, b.max_y)
    result = set()
    for way in root.iter("way"):
        if not any(t.attrib["k"] == "highway" for t in way.iter("tag")):
            continue
        points = []
        for nd in way.iter("nd"):
            x, y = wgs84_to_map(*nodes[int(nd.attrib["ref"])], config.crs)
            points.append((x - config.origin.x, y - config.origin.y))
        if len(points) >= 2 and LineString(points).intersects(box):
            result.add(int(way.attrib["id"]))
    return result


def _no_network(*args: object, **kwargs: object) -> None:
    raise AssertionError("cut_test_area.py tried to use the network")


def test_ac19_fixture_has_every_highway_way_touching_the_test_area(tmp_path: Path) -> None:
    """Criterion 19: the committed fixture's highway ways are exactly the raw data's ways
    whose line meets the test area, and cut_test_area.py recreates the OSM file offline."""
    import importlib.util

    _require_raw_data()
    config = load_map_config(SOMERO_YAML)
    fixture = Path(__file__).resolve().parent / "fixtures" / "raw" / f"{MAP_ID}-test-area"
    expected = _highway_ways(RAW / "osm" / f"{MAP_ID}.osm", config)
    root = ET.parse(fixture / "osm" / f"{MAP_ID}.osm").getroot()
    in_fixture = {
        int(w.attrib["id"])
        for w in root.iter("way")
        if any(t.attrib["k"] == "highway" for t in w.iter("tag"))
    }
    assert in_fixture == expected

    script = Path(__file__).resolve().parents[1] / "scripts" / "cut_test_area.py"
    spec = importlib.util.spec_from_file_location("cut_test_area", script)
    assert spec is not None and spec.loader is not None
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    out = tmp_path / "cut"
    with pytest.MonkeyPatch.context() as patch:
        patch.setattr(socket.socket, "connect", _no_network)
        patch.setattr(socket, "create_connection", _no_network)
        module.cut_test_area(config, RAW, out)
    recut = (out / "osm" / f"{MAP_ID}.osm").read_bytes()
    assert recut == (fixture / "osm" / f"{MAP_ID}.osm").read_bytes()


# docs/specs/place-information.md

# The full build's counts as the implementation reported them (place-information spec,
# criterion 5; compared there with the "Expected categories" preview).
# Since the world edge (docs/specs/world-edge-pipeline.md) the full build keeps only the
# buildings whose centroid is inside Somero's boundary polygon B (maps/somero/map.yaml),
# which is notched around every building near the build area's edge: 1835 OSM buildings
# (6 lie outside B) and 105 MML-only ones, 1940 in all, against 1944 with the build area
# rectangle (which dropped the 37 buildings crossing its edge) and more in the place
# information spec (1795 matched; 1501 residential, 279 garage_or_shed, ...).
FULL_MATCHED = 1791  # OSM buildings matched to an MML record
# All buildings, the MML-only ones included (docs/specs/mml-only-buildings.md).
FULL_CATEGORY_COUNTS = {
    "residential": 1473,
    "garage_or_shed": 272,
    "industrial": 77,
    "unknown": 51,
    "commercial": 29,
    "retail": 22,
    "agricultural": 5,
    "public": 4,
    "religious": 4,
    "education": 3,
    "other": 0,
}
FULL_SOURCE_COUNTS = {
    "mml_use_class": 1220,
    "osm_landuse": 612,
    "osm_building": 41,
    "osm_poi": 15,
    "osm_poi_area": 1,
    "override": 0,
    "none": 51,
}


def test_ac5_full_build_reports_its_mml_matches_and_category_counts(
    request: pytest.FixtureRequest,
) -> None:
    """Place information, criterion 5 on the whole map (local raw data only)."""
    if not list((RAW / "mml" / "topographic").glob("*.gpkg")):
        pytest.skip(f"Topographic Database for {MAP_ID} not downloaded")
    full_build: FullBuild = request.getfixturevalue("full_build")
    report = json.loads((full_build.work / "03-buildings" / "report.json").read_text("utf-8"))
    assert report["building_use"]["source"] == "mml_topographic"
    assert report["building_use"]["matched_buildings"] == FULL_MATCHED
    assert report["category_counts"] == FULL_CATEGORY_COUNTS
    assert report["category_source_counts"] == FULL_SOURCE_COUNTS
    buildings = json.loads((full_build.pack / "buildings.json").read_text("utf-8"))["buildings"]
    osm = [b for b in buildings if b["id"].startswith("osm:")]
    assert sum(1 for b in osm if b["mml_building"] is not None) == FULL_MATCHED
    assert sum(FULL_CATEGORY_COUNTS.values()) == len(buildings)


# docs/specs/mml-only-buildings.md

# Criterion 13: "The rules applied to Somero" (computed with the inward-shrink sliver test;
# the spec asks the implementation report to explain any difference with the opening rule).
# With boundary B the 42 records whose centroid lies outside the polygon (the build area
# rectangle gave 140 added, 120 whole and 20 cut, and not added 0 outside, 4 too small and
# 23 slivers) are `outside`; the centroid test comes before the other rules.
FULL_MML_ONLY = {
    "added": 105,
    "added_whole": 86,
    "added_cut": 19,
    "not_added": {"outside": 42, "too_small": 3, "slivers": 21, "lidar_contradicts": 1},
}
# Heights of the added buildings (inside B; with the rectangle, 32 of 140 crossed its edge
# and were left out: 103 lidar; before the world edge: 133 lidar, 5 few points).
FULL_MML_ONLY_LIDAR = {"mml_lidar": 100, "few_points": 3, "tree_cover": 2}


def test_mml_only_ac13_full_build_reports_the_spec_counts(
    request: pytest.FixtureRequest,
) -> None:
    """MML-only buildings, criterion 13 on the whole map (local raw data only)."""
    if not list((RAW / "mml" / "topographic").glob("*.gpkg")):
        pytest.skip(f"Topographic Database for {MAP_ID} not downloaded")
    full_build: FullBuild = request.getfixturevalue("full_build")
    report = json.loads((full_build.work / "03-buildings" / "report.json").read_text("utf-8"))
    block = report["mml_only"]
    print(
        f"\n{MAP_ID} full mml_only: added {block['added']} ({block['added_whole']} whole, "
        f"{block['added_cut']} cut), not added {block['not_added']}, "
        f"heights {block['added_by_height_source']}, rejected {block['added_lidar_rejected']}"
    )
    for entry in block["cut"][:10]:
        print(
            f"  {entry['id']}: {entry['record_area_m2']} -> {entry['kept_area_m2']} m² "
            f"{entry['osm_buildings']}"
        )
    assert block["not_added_ids"]["lidar_contradicts"] == ["mml:rakennus/3000145173"]
    buildings = json.loads((full_build.pack / "buildings.json").read_text("utf-8"))["buildings"]
    ids = {b["id"] for b in buildings}
    edge = json.loads((full_build.work / "10-world-edge" / "report.json").read_text("utf-8"))
    dropped = {d["id"] for d in edge["dropped_buildings"]}
    # Its wall lies on the build area's edge: boundary B is notched round it, so its
    # centroid is outside B and it isn't added (with the rectangle it was dropped).
    assert "mml:rakennus/339041253" in block["not_added_ids"]["outside"]
    assert "mml:rakennus/339041253" not in ids
    assert "mml:rakennus/3000145173" not in ids
    mml_dropped = sum(1 for i in dropped if i.startswith("mml:"))
    assert sum(1 for i in ids if i.startswith("mml:")) == block["added"] - mml_dropped
    assert {k: block[k] for k in FULL_MML_ONLY} == FULL_MML_ONLY
    assert block["added_by_height_source"]["mml_lidar"] == FULL_MML_ONLY_LIDAR["mml_lidar"]
    rejected = block["added_lidar_rejected"]
    assert (rejected["few_points"], rejected["tree_cover"]) == (
        FULL_MML_ONLY_LIDAR["few_points"],
        FULL_MML_ONLY_LIDAR["tree_cover"],
    )


# docs/specs/world-edge-pipeline.md: Somero's boundary B in the full build.

# "Somero: boundary candidates" and "Somero: spawn area candidates": B's ring, water on the
# boundary, the spawn check, and the heights of "Config" (cliff.depth_m). Roadblocks, fences
# and touched road surfaces are the measured counts of this build (the spec's estimate was
# about 39-41 roadblocks, 33-35 drivable; see its 2026-11-02 amendment for slice (b)).
B_RING_M = 10_560
B_WATER_M = 320.7
B_LAKE_M = 255.8
B_LAKE_ID = "mml:jarvi/2026919564"
B_COUNTS = {
    "roadblocks": 41,
    "roadblocks_drivable": 34,
    "fences_land": 747,
    "fences_water": 17,
    "touched_without_crossing": 2,
}
B_SPAWN = {"open_area_m2": 5308, "boundary_distance_m": 1017}
B_HEIGHTS = {"boundary_min": 81.8, "boundary_max": 133.4, "cliff_bottom_y": 51.8}


def test_ac16_somero_mml_records_outside_b_are_not_in_the_pack(
    request: pytest.FixtureRequest,
) -> None:
    """MML building-use records whose centroid lies outside B (42, two of them 1.81 and
    1.72 m from it, inside building_clearance_m) have no entry in buildings.json, no POI
    link and no mesh or collision node in any tile."""
    if not list((RAW / "mml" / "topographic").glob("*.gpkg")):
        pytest.skip(f"Topographic Database for {MAP_ID} not downloaded")
    from mapgen.build.buildings import node_name
    from mapgen.build.export_glb import read_glb_node_names

    full_build: FullBuild = request.getfixturevalue("full_build")
    report = json.loads((full_build.work / "03-buildings" / "report.json").read_text("utf-8"))
    outside = set(report["mml_only"]["not_added_ids"]["outside"])
    assert len(outside) == FULL_MML_ONLY["not_added"]["outside"]
    buildings = json.loads((full_build.pack / "buildings.json").read_text("utf-8"))["buildings"]
    assert not outside & {b["id"] for b in buildings}
    pois = json.loads((full_build.pack / "pois.json").read_text("utf-8"))["pois"]
    links = {p["building_id"] for p in pois} | {i for p in pois for i in p["contains_building_ids"]}
    assert not outside & links
    nodes = {node_name(i) for i in outside}
    for glb in (full_build.pack / "tiles").glob("*.glb"):
        names = read_glb_node_names(glb)
        bases = {n.removesuffix("-colonly").rsplit("_c", 1)[0] for n in names if "-" in n}
        assert not nodes & ({n for n in names if "-" not in n} | bases), glb.name


def test_ac16_somero_full_build_uses_boundary_b_with_the_spec_figures(
    full_build: FullBuild,
) -> None:
    from shapely.geometry import Polygon

    from mapgen.build.world_edge.spawn import crossing_buildings

    doc = json.loads((full_build.pack / "boundary.json").read_text("utf-8"))
    report = json.loads((full_build.work / "10-world-edge" / "report.json").read_text("utf-8"))
    assert doc["boundary"]["source"] == "custom"
    assert round(doc["boundary"]["length_m"]) == B_RING_M
    assert len(doc["boundary"]["ring"]) == len(full_build.config.world_edge.boundary_wgs84 or [])
    water = report["water"]
    assert round(water["total_m"], 1) == B_WATER_M
    lake = [c for c in water["crossings"] if c["water_ids"] == [B_LAKE_ID]]
    assert [round(c["length_m"], 1) for c in lake] == [B_LAKE_M]
    counts = report["counts"]
    assert {k: counts[k] for k in B_COUNTS} == B_COUNTS
    assert len(report["touched_without_crossing"]) == B_COUNTS["touched_without_crossing"]
    assert len(doc["roadblocks"]) == B_COUNTS["roadblocks"]
    fence_types = Counter(f["type"] for f in doc["fences"])
    assert fence_types == {"land": B_COUNTS["fences_land"], "water": B_COUNTS["fences_water"]}
    assert counts["dropped_buildings"] == 0
    spawn = report["spawn"]
    assert spawn["passed"]
    assert {k: round(spawn[k]) for k in B_SPAWN} == B_SPAWN
    assert {k: round(report["heights"][k], 1) for k in B_HEIGHTS} == B_HEIGHTS
    # No building crosses B (criterion 16).
    origin = (full_build.config.origin.x, full_build.config.origin.y)
    features = json.loads(
        (full_build.work / "03-buildings" / "buildings.geojson").read_text("utf-8")
    )["features"]
    footprints = [(f["properties"]["id"], _local_polygon(f["geometry"], origin)) for f in features]
    ring = Polygon([(p[0], -p[1]) for p in doc["boundary"]["ring"]])
    clearance = full_build.config.world_edge.building_clearance_m
    assert crossing_buildings(ring, footprints, clearance) == []


def _local_polygon(geometry: dict[str, Any], origin: tuple[float, float]) -> Any:
    import numpy as np
    import shapely
    from shapely.geometry import shape

    return shapely.transform(shape(geometry), lambda c: c - np.array(origin))
