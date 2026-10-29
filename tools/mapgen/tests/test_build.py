"""The full pipeline on the testtown fixture (test area, 2 x 2 tiles), built once."""

import json
import shutil
from pathlib import Path
from typing import Any

import jsonschema
import numpy as np
import pytest
from shapely.geometry import shape

from conftest import (
    FIXTURE_TEST_AREA_BUILDINGS,
    FixtureBuild,
    TmpRepo,
    glb_primitive,
    plane_z,
    write_fixture_raw,
)
from mapgen.build import BuildError, run_build
from mapgen.build.export_glb import TERRAIN_NODE, read_glb_node_names
from mapgen.build.pack_manifest import committed_schema, generated_schema
from mapgen.cli import EXIT_FAILED, EXIT_OK, main
from mapgen.config import load_map_config
from pack_checks import check_pack


def _json(path: Path) -> Any:
    return json.loads(path.read_text(encoding="utf-8"))


def test_pack_passes_the_shared_checks(fixture_build: FixtureBuild) -> None:
    config = load_map_config(fixture_build.repo / "maps" / "testtown" / "map.yaml")
    summary = check_pack(fixture_build.pack_dir, fixture_build.work_dir, config.roads)
    assert summary.tile_count == 4
    assert summary.building_count == FIXTURE_TEST_AREA_BUILDINGS


def test_stage_output_exists(fixture_build: FixtureBuild) -> None:
    work = fixture_build.work_dir
    for rel in (
        "01-load/footprints.geojson",
        "01-load/dem.tif",
        "01-load/report.json",
        "02-terrain/stats.json",
        "03-buildings/buildings.geojson",
        "03-buildings/report.json",
    ):
        assert (work / rel).is_file(), rel
    terrain = sorted(p.name for p in (work / "02-terrain").glob("*.glb"))
    assert terrain == [f"{t}.glb" for t in sorted(_tile_ids(fixture_build))]
    for path in (work / "02-terrain").glob("*.glb"):
        assert read_glb_node_names(path) == [TERRAIN_NODE]


def _tile_ids(build: FixtureBuild) -> list[str]:
    return [t["id"] for t in _json(build.pack_dir / "manifest.json")["tiles"]]


def test_geojson_is_in_world_coordinates_with_crs(fixture_build: FixtureBuild) -> None:
    document = _json(fixture_build.work_dir / "03-buildings" / "buildings.geojson")
    assert document["crs"]["properties"]["name"] == "urn:ogc:def:crs:EPSG::3067"
    centroid = shape(document["features"][0]["geometry"]).centroid
    assert abs(centroid.x - 309657) < 200 and abs(centroid.y - 6726616) < 200


def test_load_report_lists_skips_with_reasons(fixture_build: FixtureBuild) -> None:
    report = _json(fixture_build.work_dir / "01-load" / "report.json")
    assert report["footprints"] == {
        "loaded": 13,
        "in_area": 12,
        "outside_area": 1,
        "skipped": 3,
        "partial": 2,
    }
    reasons = {s["id"]: s["reason"] for s in report["skipped"]}
    assert reasons["osm:way/108"] == "way is not closed"
    assert reasons["osm:way/301"].startswith("duplicate")
    assert reasons["osm:relation/202"].startswith("incomplete multipolygon")
    assert {p["id"] for p in report["partial"]} == {"osm:relation/205", "osm:relation/206"}
    assert report["dem"]["warped_from"] == []


def test_heights_follow_the_priority(fixture_build: FixtureBuild) -> None:
    buildings = {b["id"]: b for b in _json(fixture_build.pack_dir / "buildings.json")["buildings"]}
    expected = {
        "osm:way/101": (12.0, "osm_height"),
        "osm:way/102": (7.5, "osm_height"),
        "osm:way/103": (12.0, "osm_levels"),
        "osm:way/104": (9.0, "osm_levels"),  # invalid height, levels win
        "osm:way/105": (6.0, "default"),
        "osm:way/106": (6.0, "default"),  # height <= 0
        "osm:way/109": (6.0, "default"),
        "osm:relation/201": (10.0, "osm_height"),
        "osm:relation/203": (6.0, "osm_levels"),
        "osm:relation/205": (6.0, "default"),
        "osm:relation/206": (8.0, "osm_height"),
        "osm:relation/207": (3.0, "osm_levels"),
    }
    assert {k: (b["height_m"], b["height_source"]) for k, b in buildings.items()} == expected
    assert buildings["osm:way/109"]["tile"] == "tile_x0_y0"  # crosses x = 0, centroid east
    report = _json(fixture_build.work_dir / "03-buildings" / "report.json")
    assert report["height_source_counts"] == {
        "mml_lidar": 0,
        "mml_3d": 0,
        "osm_height": 4,
        "osm_levels": 4,
        "default": 4,
    }


def test_base_is_close_below_the_sloped_ground(fixture_build: FixtureBuild) -> None:
    features = _json(fixture_build.work_dir / "03-buildings" / "buildings.geojson")["features"]
    for feature in features:
        geometry = shape(feature["geometry"])
        parts = getattr(geometry, "geoms", [geometry])
        xy = np.vstack([np.asarray(p.exterior.coords) for p in parts]) - [309657, 6726616]
        exact = float(plane_z(xy[:, 0], xy[:, 1]).min())
        base = feature["properties"]["base_z"]
        assert exact - 0.15 - 1e-3 <= base <= exact + 1e-3  # within one 2 m cell's rise


def test_manifest_contents(fixture_build: FixtureBuild) -> None:
    manifest = _json(fixture_build.pack_dir / "manifest.json")
    assert manifest["schema_version"] == 1
    assert manifest["map_id"] == "testtown" and manifest["area"] == "test"
    assert manifest["origin"] == {"x": 309657.0, "y": 6726616.0}
    assert manifest["axes"] == {"x": "east", "y": "up", "z": "-north"}
    assert manifest["generated_at"] == "2026-10-27T12:00:00Z"
    assert {c["source"] for c in manifest["credits"]} == {"mml_dem_2m", "osm"}  # no orthophoto
    assert sum(t["building_count"] for t in manifest["tiles"]) == FIXTURE_TEST_AREA_BUILDINGS


def test_committed_schema_matches_the_model() -> None:
    assert committed_schema() == generated_schema(), (
        "regenerate: uv run python -c "
        '"from mapgen.build.pack_manifest import write_schema; write_schema()"'
    )
    jsonschema.Draft202012Validator.check_schema(committed_schema())


def _copy_repo(build: FixtureBuild, tmp_path: Path) -> Path:
    root = tmp_path / "repo"
    shutil.copytree(build.repo / "maps", root / "maps")
    shutil.copytree(build.raw_dir, root / "tools" / "mapgen" / "data" / "raw" / "testtown")
    return root


def test_cli_build_writes_the_pack_and_clears_stale_tiles(
    fixture_build: FixtureBuild, tmp_path: Path, capsys: pytest.CaptureFixture[str]
) -> None:
    root = _copy_repo(fixture_build, tmp_path)
    stale = root / "game" / "maps" / "testtown" / "tiles" / "tile_x9_y9.glb"
    stale.parent.mkdir(parents=True)
    stale.write_bytes(b"old")
    (stale.parent.parent / "manifest.json").write_text("{}", encoding="utf-8")  # an old pack
    assert main(["--repo-root", str(root), "build", "testtown", "--area", "test"]) == EXIT_OK
    assert not stale.exists()
    assert len(list(stale.parent.glob("*.glb"))) == 4
    assert (root / "tools" / "mapgen" / "build" / "testtown" / "test" / "01-load").is_dir()
    assert "tiles: 4, buildings: 12" in capsys.readouterr().out


def test_cli_build_out_option(fixture_build: FixtureBuild, tmp_path: Path) -> None:
    root = _copy_repo(fixture_build, tmp_path)
    out = tmp_path / "elsewhere"
    args = ["--repo-root", str(root), "build", "testtown", "--area", "test", "--out", str(out)]
    assert main(args) == EXIT_OK
    assert (out / "manifest.json").is_file() and not (root / "game").exists()


def test_build_fails_when_a_read_file_has_no_licence(
    tmp_repo: TmpRepo, fixture_data: dict, tmp_path: Path
) -> None:
    config = tmp_repo.load(fixture_data)
    raw = write_fixture_raw(tmp_repo.raw_dir("testtown"))
    (raw / "download-manifest.json").unlink()
    with pytest.raises(BuildError, match="not in the download manifest"):
        run_build(config, "test", raw, tmp_path / "pack", tmp_path / "work", log=lambda _: None)


def test_build_without_raw_data_exits_with_failure(
    tmp_repo: TmpRepo, fixture_data: dict, capsys: pytest.CaptureFixture[str]
) -> None:
    tmp_repo.write_map(fixture_data)
    args = ["--repo-root", str(tmp_repo.root), "build", "testtown", "--area", "test"]
    assert main(args) == EXIT_FAILED
    assert "mapgen download testtown" in capsys.readouterr().err


def test_multi_part_building_has_a_box_per_part(fixture_build: FixtureBuild) -> None:
    buildings = {b["id"]: b for b in _json(fixture_build.pack_dir / "buildings.json")["buildings"]}
    assert buildings["osm:relation/205"]["collision_nodes"] == [
        "bld_osm_relation_205_c0-colonly",
        "bld_osm_relation_205_c1-colonly",
    ]
    assert buildings["osm:way/101"]["collision_nodes"] == ["bld_osm_way_101-colonly"]


def test_written_terrain_normals_match_across_tile_edges(fixture_build: FixtureBuild) -> None:
    tiles = fixture_build.pack_dir / "tiles"

    def edge(tile: str, axis: int) -> tuple[np.ndarray, np.ndarray]:
        stored = glb_primitive(tiles / f"{tile}.glb", TERRAIN_NODE)
        positions, normals = stored["POSITION"], stored["NORMAL"]
        on = positions[:, axis] == 0.0  # glTF X = east (axis 0), Z = -north (axis 2)
        order = np.argsort(positions[on, 2 - axis])
        return positions[on][order], normals[on][order]

    west, east = edge("tile_x-1_y0", 0), edge("tile_x0_y0", 0)
    assert len(west[0]) > 10
    assert np.array_equal(west[0], east[0]) and np.array_equal(west[1], east[1])
    south, north = edge("tile_x0_y-1", 2), edge("tile_x0_y0", 2)
    assert np.array_equal(south[0], north[0]) and np.array_equal(south[1], north[1])
    assert np.all(west[1][:, 1] > 0.99)  # gentle fixture slope: normals point up
