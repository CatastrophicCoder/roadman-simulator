"""Builds with the `mml_lidar` source: a synthetic LAZ over the testtown fixture, and the
committed Somero test-area fixture (real MML and OSM data cut to the test area).

Criterion numbers refer to the acceptance criteria in docs/specs/building-heights.md.
Nothing here touches the network.
"""

import json
import math
import re
import shutil
import socket
import time
from dataclasses import dataclass
from pathlib import Path
from typing import Any

import laspy
import numpy as np
import pytest
import yaml

from conftest import (
    FIXED_NOW,
    FIXTURE_YAML,
    OSM_FIXTURE,
    TmpRepo,
    lidar_entry,
    plane_z,
    read_tile,
    write_fixture_raw,
    write_laz,
)
from mapgen.build import BuildError, run_build
from mapgen.build.area import build_area, to_world
from mapgen.build.pack_manifest import validate_manifest_file
from mapgen.build.stage1_load import LoadResult, run_load, to_local
from mapgen.config import MapConfig
from mapgen.geo import BBox
from mapgen.sources.base import LoadContext, SourceLoadError
from mapgen.sources.mml import DROPPED_CLASSES, LidarOptions, MmlLidarAdapter
from mapgen.sources.osm import read_osm_buildings
from pack_checks import check_pack

LIDAR_DATASET = "Laser scanning data 0,5 p"
SOMERO_FIXTURE = Path(__file__).resolve().parent / "fixtures" / "raw" / "somero-test-area"
SOMERO_LAZ = SOMERO_FIXTURE / "mml" / "lidar_05p" / "L4212A1.laz"
NO_LIDAR = "osm:way/101"  # no points at all: keeps its OSM height
TOO_LOW = "osm:way/102"  # roof points below the ground: lidar rejected, OSM height wins
LIDAR_SOURCE = {"adapter": "mml_lidar", "enabled": True, "options": {}}


def _json(path: Path) -> Any:
    return json.loads(path.read_text(encoding="utf-8"))


def _fixture_data(lidar: dict[str, Any] | None = LIDAR_SOURCE) -> dict[str, Any]:
    data = yaml.safe_load(FIXTURE_YAML.read_text(encoding="utf-8"))
    if lidar is not None:
        data["sources"]["mml_lidar"] = json.loads(json.dumps(lidar))
    return data


def _write_config(root: Path, data: dict[str, Any]) -> MapConfig:
    return TmpRepo(root).load(data)


def _add_manifest_entry(raw: Path, entry: dict[str, Any]) -> None:
    path = raw / "download-manifest.json"
    document = _json(path)
    document["files"].append(entry)
    path.write_text(json.dumps(document), encoding="utf-8")


def _grid_in(geometry: Any, spacing: float) -> np.ndarray:
    import shapely

    min_x, min_y, max_x, max_y = geometry.bounds
    gx, gy = np.meshgrid(
        np.arange(min_x + spacing / 2, max_x, spacing),
        np.arange(min_y + spacing / 2, max_y, spacing),
    )
    xy = np.column_stack([gx.ravel(), gy.ravel()])
    return xy[shapely.contains_xy(geometry, xy[:, 0], xy[:, 1])]


def write_synthetic_lidar(raw: Path, config: MapConfig, shift_m: float = 0.0) -> dict[str, float]:
    """A LAZ over the fixture's footprints; returns the roof elevation written per building.

    Every footprint except `NO_LIDAR` gets flat roof points (class 5) on a 0.7 m grid, and
    as many ground points (class 2) at terrain height under them, so a loader that kept
    ground would halve the roof median. `TOO_LOW` gets its roof below the ground. A sparse
    ground grid covers the whole test area. `shift_m` moves every point east.
    """
    origin = np.array([config.origin.x, config.origin.y])
    roofs: dict[str, float] = {}
    xs: list[np.ndarray] = []
    zs: list[np.ndarray] = []
    classes: list[np.ndarray] = []
    footprints = sorted(read_osm_buildings(OSM_FIXTURE).footprints, key=lambda f: f.id)
    for k, footprint in enumerate(footprints):
        if footprint.id == NO_LIDAR:
            continue
        local = to_local(footprint, config).geometry
        centroid = local.centroid
        ground = float(plane_z(centroid.x, centroid.y))
        roof_z = ground - 5.0 if footprint.id == TOO_LOW else ground + 4.0 + 0.5 * k
        roofs[footprint.id] = roof_z
        xy = _grid_in(local, 0.7)
        for z, cls in ((np.full(len(xy), roof_z), 5), (plane_z(xy[:, 0], xy[:, 1]), 2)):
            xs.append(xy)
            zs.append(np.asarray(z, dtype=float))
            classes.append(np.full(len(xy), cls))
    g = np.arange(-160.0, 160.0, 4.0)
    gx, gy = np.meshgrid(g, g)
    ground_xy = np.column_stack([gx.ravel(), gy.ravel()])
    xs.append(ground_xy)
    zs.append(plane_z(ground_xy[:, 0], ground_xy[:, 1]))
    classes.append(np.full(len(ground_xy), 2))
    xy = np.vstack(xs) + origin + [shift_m, 0.0]
    write_laz(
        raw / "mml" / "lidar_05p" / "synthetic.laz",
        xy[:, 0],
        xy[:, 1],
        np.concatenate(zs),
        classification=np.concatenate(classes),
    )
    _add_manifest_entry(raw, lidar_entry("mml/lidar_05p/synthetic.laz"))
    return roofs


@dataclass
class LidarBuild:
    config: MapConfig
    raw: Path
    pack: Path
    work: Path
    roofs: dict[str, float]
    loaded: LoadResult


def _build(root: Path, config: MapConfig, raw: Path) -> tuple[Path, Path]:
    pack, work = root / "pack", root / "work"
    run_build(config, "test", raw, pack, work, now=lambda: FIXED_NOW, log=lambda _: None)
    return pack, work


@pytest.fixture(scope="module")
def lidar_build(tmp_path_factory: pytest.TempPathFactory) -> LidarBuild:
    """The testtown test area with the sloped plane DEM and a synthetic LAZ (read-only)."""
    root = tmp_path_factory.mktemp("lidar")
    config = _write_config(root, _fixture_data())
    raw = write_fixture_raw(TmpRepo(root).raw_dir("testtown"))
    roofs = write_synthetic_lidar(raw, config)
    pack, work = _build(root, config, raw)
    loaded = run_load(config, build_area(config, "test"), raw, log=lambda _: None)
    return LidarBuild(config, raw, pack, work, roofs, loaded)


def _buildings(pack: Path) -> dict[str, dict[str, Any]]:
    return {b["id"]: b for b in _json(pack / "buildings.json")["buildings"]}


def _credits(pack: Path) -> list[dict[str, Any]]:
    return _json(pack / "manifest.json")["credits"]


# AC4, AC11: geometry from lidar on a sloped DEM


def test_ac4_lidar_buildings_use_the_measured_roof(lidar_build: LidarBuild) -> None:
    buildings = _buildings(lidar_build.pack)
    lidar = {k for k, b in buildings.items() if b["height_source"] == "mml_lidar"}
    assert lidar == set(buildings) - {NO_LIDAR, TOO_LOW}
    for building_id in lidar:
        building = buildings[building_id]
        assert building["roof_z_median"] == pytest.approx(lidar_build.roofs[building_id], abs=1e-3)
        assert building["roof_z_p90"] >= building["roof_z_median"]  # AC8
        assert building["height_m"] == pytest.approx(
            building["roof_z_median"] - building["base_z"], abs=2e-3
        )
        assert building["lidar_returns"] == "all"
        assert building["lidar_points"] >= 5
        assert building["lidar_buffer_m"] in (1.0, 0.5, 0.0)


def test_ac4_base_is_the_lowest_terrain_under_the_footprint(lidar_build: LidarBuild) -> None:
    buildings = _buildings(lidar_build.pack)
    heightfield = lidar_build.loaded.heightfield
    footprints = {f.id: f for f in lidar_build.loaded.footprints}
    assert set(footprints) == set(buildings)
    for building_id, building in buildings.items():
        expected = heightfield.min_under(footprints[building_id].geometry)
        assert building["base_z"] == pytest.approx(expected, abs=1e-3)


def test_ac4_roof_top_in_the_mesh_is_the_roof_median(lidar_build: LidarBuild) -> None:
    buildings = _buildings(lidar_build.pack)
    checked = 0
    for tile in _json(lidar_build.pack / "manifest.json")["tiles"]:
        meshes = read_tile(lidar_build.pack / tile["file"])
        for node in meshes.buildings():
            (building,) = [b for b in buildings.values() if b["node"] == node]
            vertices, _ = meshes.nodes[node]
            assert vertices[:, 2].min() == pytest.approx(building["base_z"], abs=0.01)
            top = building["base_z"] + building["height_m"]
            assert vertices[:, 2].max() == pytest.approx(top, abs=0.01)
            if building["height_source"] == "mml_lidar":
                assert vertices[:, 2].max() == pytest.approx(building["roof_z_median"], abs=0.01)
                checked += 1
    assert checked == len(buildings) - 2


def test_ac11_collision_boxes_span_base_to_roof(lidar_build: LidarBuild) -> None:
    buildings = _buildings(lidar_build.pack)
    by_collision = {c: b for b in buildings.values() for c in b["collision_nodes"]}
    seen = 0
    for tile in _json(lidar_build.pack / "manifest.json")["tiles"]:
        meshes = read_tile(lidar_build.pack / tile["file"])
        for name, (vertices, _) in meshes.nodes.items():
            if not name.endswith("-colonly"):
                continue
            building = by_collision[name]
            assert vertices[:, 2].min() == pytest.approx(building["base_z"], abs=0.01)
            top = building["base_z"] + building["height_m"]
            assert vertices[:, 2].max() == pytest.approx(top, abs=0.01)
            seen += 1
    assert seen == len(by_collision)


def test_ac11_no_building_has_an_invalid_height(lidar_build: LidarBuild) -> None:
    for building in _buildings(lidar_build.pack).values():
        assert math.isfinite(building["height_m"]) and building["height_m"] > 0
        assert math.isfinite(building["base_z"])


def test_ac7_rejected_building_falls_back_with_reason(lidar_build: LidarBuild) -> None:
    buildings = _buildings(lidar_build.pack)
    assert buildings[TOO_LOW]["height_source"] == "osm_height"
    assert buildings[TOO_LOW]["lidar_rejected"] == "too_low"
    assert buildings[NO_LIDAR]["height_source"] == "osm_height"
    assert buildings[NO_LIDAR]["lidar_rejected"] == "few_points"
    features = _json(lidar_build.work / "03-buildings" / "buildings.geojson")["features"]
    by_id = {f["properties"]["id"]: f["properties"] for f in features}
    assert by_id[TOO_LOW]["lidar_rejected"] == "too_low"
    assert by_id[TOO_LOW]["height_source"] == "osm_height"


def test_ac2_reports_count_per_source_reason_and_buffer(lidar_build: LidarBuild) -> None:
    report = _json(lidar_build.work / "03-buildings" / "report.json")
    total = report["building_count"]
    assert report["height_source_counts"]["mml_lidar"] == total - 2
    assert sum(report["height_source_counts"].values()) == total
    lidar = report["lidar"]
    assert lidar["buildings_checked"] == total
    assert lidar["rejected_counts"] == {
        "few_points": 1,
        "tree_cover": 0,
        "too_low": 1,
        "too_high": 0,
    }
    assert sum(lidar["buffer_counts"].values()) == total - 2
    assert set(lidar["buffer_counts"]) <= {"1.0", "0.5", "0.0"}
    assert lidar["single_returns"] == 0


def test_loader_drops_ground_points(lidar_build: LidarBuild) -> None:
    points = lidar_build.loaded.points
    assert points is not None and len(points) > 0
    assert set(np.unique(points.classification).tolist()) == {5}
    load = _json(lidar_build.work / "01-load" / "report.json")
    assert load["sources"]["point_cloud"] == "mml_lidar"
    assert load["point_cloud"]["points"] == len(points)
    assert load["point_cloud"]["params"] == {
        "inner_buffers_m": [1.0, 0.5, 0.0],
        "min_points": 5,
        "tree_spread_m": 8.0,
        "min_height_m": 1.0,
        "max_height_m": 40.0,
    }
    assert "mml/lidar_05p/synthetic.laz" in load["files_read"]


def test_lidar_build_passes_the_shared_pack_checks(lidar_build: LidarBuild) -> None:
    summary = check_pack(lidar_build.pack, lidar_build.work)
    assert summary.tile_count == 4


# AC10: credits only when lidar files were read


def _lidar_credits(pack: Path) -> list[dict[str, Any]]:
    return [c for c in _credits(pack) if c["dataset"] == LIDAR_DATASET]


def test_ac10_lidar_credit_when_the_build_read_lidar(lidar_build: LidarBuild) -> None:
    (credit,) = _lidar_credits(lidar_build.pack)
    assert credit["source"] == "mml_lidar"
    assert credit["licence"] == "CC BY 4.0"
    assert credit["attribution"] == (
        "Contains data from the National Land Survey of Finland Laser scanning data 0,5 p, 10/2026"
    )
    validate_manifest_file(lidar_build.pack / "manifest.json")


def test_ac10_no_lidar_credit_when_the_source_is_disabled(tmp_path: Path) -> None:
    disabled = {**LIDAR_SOURCE, "enabled": False}
    config = _write_config(tmp_path, _fixture_data(disabled))
    raw = write_fixture_raw(TmpRepo(tmp_path).raw_dir("testtown"))
    write_synthetic_lidar(raw, config)
    pack, work = _build(tmp_path, config, raw)
    assert _lidar_credits(pack) == []
    validate_manifest_file(pack / "manifest.json")
    report = _json(work / "03-buildings" / "report.json")
    assert report["height_source_counts"]["mml_lidar"] == 0
    assert report["lidar"]["buildings_checked"] == 0
    assert _json(work / "01-load" / "report.json")["sources"]["point_cloud"] is None
    assert all("lidar_rejected" not in b for b in _buildings(pack).values())


def test_ac10_no_lidar_credit_when_no_lidar_file_covers_the_area(tmp_path: Path) -> None:
    config = _write_config(tmp_path, _fixture_data())
    raw = write_fixture_raw(TmpRepo(tmp_path).raw_dir("testtown"))
    write_synthetic_lidar(raw, config, shift_m=5000.0)
    pack, work = _build(tmp_path, config, raw)
    assert _lidar_credits(pack) == []
    validate_manifest_file(pack / "manifest.json")
    report = _json(work / "03-buildings" / "report.json")
    assert report["lidar"]["rejected_counts"]["few_points"] == report["building_count"]
    assert (
        "mml/lidar_05p/synthetic.laz" not in _json(work / "01-load" / "report.json")["files_read"]
    )


def test_ac10_no_lidar_read_when_the_area_has_no_footprints(tmp_path: Path) -> None:
    config = _write_config(tmp_path, _fixture_data())
    raw = write_fixture_raw(TmpRepo(tmp_path).raw_dir("testtown"))
    write_synthetic_lidar(raw, config)
    (raw / "osm" / "testtown.osm").write_text(
        "<?xml version='1.0' encoding='UTF-8'?>\n<osm version=\"0.6\"></osm>\n", encoding="utf-8"
    )
    pack, work = _build(tmp_path, config, raw)
    assert _lidar_credits(pack) == []
    validate_manifest_file(pack / "manifest.json")
    load = _json(work / "01-load" / "report.json")
    assert load["point_cloud"]["points"] == 0 and load["point_cloud"]["bounds_world"] is None


def test_lidar_file_missing_from_the_download_manifest_fails_the_build(tmp_path: Path) -> None:
    config = _write_config(tmp_path, _fixture_data())
    raw = write_fixture_raw(TmpRepo(tmp_path).raw_dir("testtown"))
    write_synthetic_lidar(raw, config)
    write_fixture_raw(raw)  # rewrites the manifest without the lidar entry
    with pytest.raises(BuildError, match="not in the download manifest"):
        _build(tmp_path, config, raw)


# Adapter load (the committed fixture LAZ and synthetic files)


def _load_ctx(config: MapConfig, raw: Path, bounds: BBox) -> LoadContext:
    return LoadContext(
        map_config=config,
        source_name="mml_lidar",
        options=LidarOptions(),
        raw_dir=raw,
        target_crs="EPSG:3067",
        bounds=bounds,
        log=lambda _: None,
    )


def _test_area_world(config: MapConfig) -> BBox:
    return to_world(build_area(config, "test").bounds, config)


def test_adapter_load_crops_and_drops_ground_and_noise(fixture_config: MapConfig) -> None:
    with laspy.open(SOMERO_LAZ) as reader:
        raw_classes = set(np.unique(reader.read().classification).tolist())
    assert 2 in raw_classes  # the fixture still has ground points to drop
    bounds = _test_area_world(fixture_config)
    data = MmlLidarAdapter().load(_load_ctx(fixture_config, SOMERO_FIXTURE, bounds))
    assert data.files == [SOMERO_LAZ]
    points = data.points
    assert points is not None and len(points) > 1000
    assert not set(np.unique(points.classification).tolist()) & set(DROPPED_CLASSES)
    assert points.x.min() >= bounds.min_x and points.x.max() <= bounds.max_x
    assert points.y.min() >= bounds.min_y and points.y.max() <= bounds.max_y
    assert points.crs == "EPSG:3067"
    assert np.all(points.number_of_returns >= 1)


def test_adapter_load_skips_files_outside_the_bounds(fixture_config: MapConfig) -> None:
    far = _test_area_world(fixture_config)
    far = BBox(far.min_x + 50_000, far.min_y, far.max_x + 50_000, far.max_y)
    data = MmlLidarAdapter().load(_load_ctx(fixture_config, SOMERO_FIXTURE, far))
    assert data.files == []
    assert data.points is not None and len(data.points) == 0


def test_adapter_drops_every_noise_class(fixture_config: MapConfig, tmp_path: Path) -> None:
    classes = np.array([1, 2, 3, 4, 5, 6, 7, 9, 15, 16, 17, 18])
    n = len(classes)
    x0, y0 = fixture_config.origin.x, fixture_config.origin.y
    write_laz(
        tmp_path / "mml" / "lidar_05p" / "a.laz",
        x0 + np.arange(n, dtype=float),
        np.full(n, y0),
        np.full(n, 110.0),
        classification=classes,
        number_of_returns=np.array([1, 2] * (n // 2)),
    )
    bounds = BBox(x0 - 10, y0 - 10, x0 + 20, y0 + 10)
    data = MmlLidarAdapter().load(_load_ctx(fixture_config, tmp_path, bounds))
    assert data.points is not None
    assert data.points.classification.tolist() == [1, 3, 4, 5, 6, 9, 15, 16, 17]
    assert data.points.number_of_returns.tolist() == [1, 1, 2, 1, 2, 2, 1, 2, 1]


@pytest.mark.parametrize(
    ("crs", "message"), [(None, "has no CRS"), ("EPSG:3857", "reprojecting point clouds")]
)
def test_adapter_refuses_a_file_without_the_map_crs(
    fixture_config: MapConfig, tmp_path: Path, crs: str | None, message: str
) -> None:
    x0, y0 = fixture_config.origin.x, fixture_config.origin.y
    write_laz(tmp_path / "mml" / "lidar_05p" / "a.laz", [x0], [y0], [110.0], crs=crs)
    bounds = BBox(x0 - 10, y0 - 10, x0 + 10, y0 + 10)
    with pytest.raises(SourceLoadError, match=message):
        MmlLidarAdapter().load(_load_ctx(fixture_config, tmp_path, bounds))


def test_adapter_refuses_a_wrong_crs_file_outside_the_bounds(
    fixture_config: MapConfig, tmp_path: Path
) -> None:
    # The CRS is checked before the extent, so a wrong-CRS file is never skipped silently.
    x0, y0 = fixture_config.origin.x, fixture_config.origin.y
    write_laz(tmp_path / "mml" / "lidar_05p" / "a.laz", [0.0], [0.0], [110.0], crs="EPSG:3857")
    bounds = BBox(x0 - 10, y0 - 10, x0 + 10, y0 + 10)
    with pytest.raises(SourceLoadError, match="not supported"):
        MmlLidarAdapter().load(_load_ctx(fixture_config, tmp_path, bounds))


def test_adapter_without_laz_files_says_how_to_download(
    fixture_config: MapConfig, tmp_path: Path
) -> None:
    (tmp_path / "mml" / "lidar_05p").mkdir(parents=True)
    (tmp_path / "mml" / "lidar_05p" / "notes.txt").write_text("x")
    with pytest.raises(SourceLoadError, match="mapgen download testtown --only mml_lidar"):
        MmlLidarAdapter().load(_load_ctx(fixture_config, tmp_path, BBox(0, 0, 1, 1)))


# AC2, AC12: the committed Somero test-area fixture


def _no_network(*args: Any, **kwargs: Any) -> Any:
    raise AssertionError("the test tried to open a network connection")


@dataclass
class SomeroBuild:
    pack: Path
    work: Path
    seconds: float


@pytest.fixture(scope="module")
def somero_fixture_build(tmp_path_factory: pytest.TempPathFactory) -> SomeroBuild:
    """The committed fixture built offline as the testtown map (same bbox, origin, area).

    The fixture is laid out like `data/raw/somero/`; the OSM file and manifest are renamed
    to the testtown id, so no Somero config is needed.
    """
    root = tmp_path_factory.mktemp("somero-fixture")
    config = _write_config(root, _fixture_data())
    raw = TmpRepo(root).raw_dir("testtown")
    shutil.copytree(SOMERO_FIXTURE, raw)
    (raw / "osm" / "somero.osm").rename(raw / "osm" / "testtown.osm")
    manifest = _json(raw / "download-manifest.json")
    manifest["map_id"] = "testtown"
    for entry in manifest["files"]:
        entry["path"] = entry["path"].replace("osm/somero.osm", "osm/testtown.osm")
    (raw / "download-manifest.json").write_text(json.dumps(manifest), encoding="utf-8")
    with pytest.MonkeyPatch.context() as patch:
        patch.setattr(socket.socket, "connect", _no_network)
        patch.setattr(socket, "create_connection", _no_network)
        start = time.perf_counter()
        pack, work = _build(root, config, raw)
        seconds = time.perf_counter() - start
    return SomeroBuild(pack, work, seconds)


def test_ac2_fixture_area_gets_lidar_heights_for_95_percent(
    somero_fixture_build: SomeroBuild,
) -> None:
    buildings = list(_buildings(somero_fixture_build.pack).values())
    cut = _json(SOMERO_FIXTURE / "download-manifest.json")["files"]
    (osm,) = [e for e in cut if e["source"] == "osm"]
    assert len(buildings) == osm["request"]["test_area_cut"]["footprints"]
    lidar = [b for b in buildings if b["height_source"] == "mml_lidar"]
    assert len(lidar) >= 0.95 * len(buildings)
    for building in buildings:
        assert math.isfinite(building["height_m"]) and building["height_m"] > 0
    for building in lidar:
        assert building["roof_z_p90"] >= building["roof_z_median"]
        assert building["height_m"] == pytest.approx(
            building["roof_z_median"] - building["base_z"], abs=2e-3
        )


def test_ac2_fixture_report_lists_sources_and_every_reason(
    somero_fixture_build: SomeroBuild,
) -> None:
    report = _json(somero_fixture_build.work / "03-buildings" / "report.json")
    assert set(report["height_source_counts"]) == {
        "mml_lidar",
        "mml_3d",
        "osm_height",
        "osm_levels",
        "default",
    }
    assert sum(report["height_source_counts"].values()) == report["building_count"]
    lidar = report["lidar"]
    assert set(lidar["rejected_counts"]) == {"few_points", "tree_cover", "too_low", "too_high"}
    assert lidar["buildings_checked"] == report["building_count"]
    assert sum(lidar["buffer_counts"].values()) == report["height_source_counts"]["mml_lidar"]
    # Reference counts of the committed fixture (60 footprints, no rejections).
    assert report["building_count"] == 60
    assert report["height_source_counts"]["mml_lidar"] == 60
    assert lidar["rejected_counts"] == dict.fromkeys(
        ("few_points", "tree_cover", "too_low", "too_high"), 0
    )
    assert lidar["buffer_counts"] == {"1.0": 56, "0.5": 3, "0.0": 1}
    assert lidar["single_returns"] == 0


def test_ac12_fixture_pack_passes_checks_and_credits_lidar(
    somero_fixture_build: SomeroBuild,
) -> None:
    check_pack(somero_fixture_build.pack, somero_fixture_build.work)
    validate_manifest_file(somero_fixture_build.pack / "manifest.json")
    datasets = {c["dataset"] for c in _credits(somero_fixture_build.pack)}
    assert datasets == {"Elevation model 2 m", LIDAR_DATASET, "OpenStreetMap"}


def test_ac12_fixture_build_is_fast(somero_fixture_build: SomeroBuild) -> None:
    assert somero_fixture_build.seconds < 10.0  # AC12: about 10 s; ~0.4 s locally


def test_ac12_fixture_has_attribution_next_to_it() -> None:
    notice = (SOMERO_FIXTURE / "NOTICE.md").read_text(encoding="utf-8")
    for text in (
        "National Land Survey of Finland",
        "Laser scanning data 0,5 p",
        "CC BY 4.0",
        "OpenStreetMap",
        "ODbL",
    ):
        assert text in notice, text


def test_ac12_fixture_osm_has_no_contributor_metadata() -> None:
    text = (SOMERO_FIXTURE / "osm" / "somero.osm").read_text(encoding="utf-8")
    for attribute in ("user", "uid", "timestamp", "changeset"):
        assert not re.search(rf"\s{attribute}=", text), attribute


def test_fixture_manifest_has_no_scanner_metadata() -> None:
    manifest = _json(SOMERO_FIXTURE / "download-manifest.json")
    for entry in manifest["files"]:
        result = entry["request"].get("result", {})
        for key in ("scanner", "project", "pointCloudID", "flightAltitude"):
            assert key not in result, (entry["path"], key)


def test_ac12_fixture_is_small() -> None:
    total = sum(p.stat().st_size for p in SOMERO_FIXTURE.rglob("*") if p.is_file())
    assert total < 1_500_000
