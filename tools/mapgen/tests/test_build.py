"""The full pipeline on the testtown fixture (test area, 2 x 2 tiles), built once."""

import json
import re
import shutil
from collections.abc import Callable
from pathlib import Path
from typing import Any

import jsonschema
import numpy as np
import pytest
from shapely.affinity import translate
from shapely.geometry import Polygon, shape
from shapely.ops import unary_union

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
from mapgen.build.pack_checks import check_import_names, import_name, read_glb_meshes
from mapgen.build.pack_manifest import committed_schema, generated_schema
from mapgen.cli import EXIT_FAILED, EXIT_OK, main
from mapgen.config import load_map_config
from pack_checks import PackCheckError, check_pack


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
        run_build(
            config,
            "test",
            raw,
            tmp_path / "pack",
            tmp_path / "work",
            map_dir=tmp_repo.map_dir("testtown"),
            log=lambda _: None,
        )


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
    assert buildings["osm:way/101"]["collision_nodes"] == ["bld_osm_way_101_c0-colonly"]


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


# Collision node names (pack checks on corrupted packs)


def _rename_glb_nodes(path: Path, renames: dict[str, str]) -> None:
    """Rewrite node names in a GLB's JSON chunk (the binary chunk stays as is)."""
    data = path.read_bytes()
    json_len = int.from_bytes(data[12:16], "little")
    gltf = json.loads(data[20 : 20 + json_len])
    for node in gltf["nodes"]:
        node["name"] = renames.get(node.get("name", ""), node.get("name", ""))
    chunk = json.dumps(gltf, separators=(",", ":")).encode("utf-8")
    chunk += b" " * (-len(chunk) % 4)
    rest = data[20 + json_len :]
    body = len(chunk).to_bytes(4, "little") + b"JSON" + chunk + rest
    path.write_bytes(data[:8] + (12 + len(body)).to_bytes(4, "little") + body)


def _corrupt_collisions(
    fixture_build: FixtureBuild, tmp_path: Path, building_id: str, new: list[str]
) -> Path:
    """A copy of the pack whose building has collision nodes `new` in its tile and json."""
    pack = tmp_path / "pack"
    shutil.copytree(fixture_build.pack_dir, pack)
    document = _json(pack / "buildings.json")
    building = next(b for b in document["buildings"] if b["id"] == building_id)
    old = building["collision_nodes"]
    assert len(old) == len(new)
    _rename_glb_nodes(pack / "tiles" / f"{building['tile']}.glb", dict(zip(old, new, strict=True)))
    building["collision_nodes"] = new
    (pack / "buildings.json").write_text(json.dumps(document), encoding="utf-8")
    return pack


def _check(fixture_build: FixtureBuild, pack: Path) -> None:
    config = load_map_config(fixture_build.repo / "maps" / "testtown" / "map.yaml")
    check_pack(pack, fixture_build.work_dir, config.roads)


def test_every_building_has_c_numbered_collision_nodes(fixture_build: FixtureBuild) -> None:
    for building in _json(fixture_build.pack_dir / "buildings.json")["buildings"]:
        node, collisions = building["node"], building["collision_nodes"]
        assert collisions == [f"{node}_c{k}-colonly" for k in range(len(collisions))]


def test_renamed_pack_copy_still_passes(fixture_build: FixtureBuild, tmp_path: Path) -> None:
    """The GLB rewriter itself keeps a pack valid (same names in, same names out)."""
    pack = _corrupt_collisions(
        fixture_build, tmp_path, "osm:way/101", ["bld_osm_way_101_c0-colonly"]
    )
    _check(fixture_build, pack)


@pytest.mark.parametrize(
    ("building_id", "new", "message"),
    [
        # the old one-part name: its body would import as "bld_osm_way_101", like the visual
        ("osm:way/101", ["bld_osm_way_101-colonly"], "both import as 'bld_osm_way_101'"),
        ("osm:way/101", ["bld_osm_way_101_c1-colonly"], "aren't"),
        (
            "osm:relation/205",
            ["bld_osm_relation_205_c0-colonly", "bld_osm_relation_205_c2-colonly"],
            "aren't",
        ),
        (
            "osm:relation/205",
            ["bld_osm_relation_205_c1-colonly", "bld_osm_relation_205_c0-colonly"],
            "aren't",
        ),
    ],
)
def test_pack_checks_reject_bad_collision_names(
    fixture_build: FixtureBuild, tmp_path: Path, building_id: str, new: list[str], message: str
) -> None:
    pack = _corrupt_collisions(fixture_build, tmp_path, building_id, new)
    with pytest.raises(PackCheckError, match=message):
        _check(fixture_build, pack)


# Collision geometry: each footprint part extruded (ADR 0006 amendment 2026-11-04)


def _roof_outline(vertices: np.ndarray, faces: np.ndarray) -> Any:
    """The union of a mesh's up-facing triangles seen from above."""
    t = vertices[faces]
    up = np.cross(t[:, 1] - t[:, 0], t[:, 2] - t[:, 0])[:, 2] > 0
    return unary_union([Polygon(tri[:, :2]) for tri in t[up]])


def test_collision_meshes_are_the_extruded_footprint_parts(fixture_build: FixtureBuild) -> None:
    manifest = _json(fixture_build.pack_dir / "manifest.json")
    origin = np.array([manifest["origin"]["x"], manifest["origin"]["y"]])
    features = _json(fixture_build.work_dir / "03-buildings" / "buildings.geojson")["features"]
    footprints = {f["properties"]["id"]: shape(f["geometry"]) for f in features}
    tiles = {
        t["id"]: read_glb_meshes(fixture_build.pack_dir / t["file"]) for t in manifest["tiles"]
    }
    checked = 0
    for building in _json(fixture_build.pack_dir / "buildings.json")["buildings"]:
        meshes = tiles[building["tile"]]
        world = footprints[building["id"]]
        parts = list(getattr(world, "geoms", [world]))
        assert len(parts) == len(building["collision_nodes"])
        top = building["base_z"] + building["height_m"]
        for part, name in zip(parts, building["collision_nodes"], strict=True):
            vertices, faces = meshes.nodes[name]
            local = translate(part, -origin[0], -origin[1])
            outline = _roof_outline(vertices, faces)
            # The same polygon as the footprint part, holes included (float32 slack).
            assert outline.symmetric_difference(local).area < 1e-3, name
            assert vertices[:, 2].min() == pytest.approx(building["base_z"], abs=1e-3)
            assert vertices[:, 2].max() == pytest.approx(top, abs=1e-3)
            checked += 1
    assert checked >= FIXTURE_TEST_AREA_BUILDINGS


def test_courtyard_is_open_in_the_collision(fixture_build: FixtureBuild) -> None:
    """osm:relation/201 has an inner ring: its collision leaves the courtyard open, as the
    visual mesh does, instead of filling it."""
    building = next(
        b
        for b in _json(fixture_build.pack_dir / "buildings.json")["buildings"]
        if b["id"] == "osm:relation/201"
    )
    meshes = read_glb_meshes(fixture_build.pack_dir / "tiles" / f"{building['tile']}.glb")
    (name,) = building["collision_nodes"]
    outline = _roof_outline(*meshes.nodes[name])
    assert isinstance(outline, Polygon) and len(outline.interiors) == 1
    courtyard = Polygon(outline.interiors[0])
    assert courtyard.area > 1.0
    assert not outline.contains(courtyard.representative_point())
    assert _roof_outline(*meshes.nodes[building["node"]]).equals(outline)


def _grow_glb_node(path: Path, node_name: str, factor: float) -> None:
    """Give a node's mesh its own copy of its positions, scaled horizontally about their
    mean (glTF X and Z). The copy is appended to the binary chunk as a new accessor,
    because the exporter shares identical buffers (a one-part building's collision mesh
    and its visual mesh store the same positions)."""
    data = path.read_bytes()
    json_len = int.from_bytes(data[12:16], "little")
    gltf = json.loads(data[20 : 20 + json_len])
    bin_start = 20 + json_len + 8
    binary = data[bin_start:]
    node = next(n for n in gltf["nodes"] if n.get("name") == node_name)
    attributes = gltf["meshes"][node["mesh"]]["primitives"][0]["attributes"]
    accessor = gltf["accessors"][attributes["POSITION"]]
    view = gltf["bufferViews"][accessor["bufferView"]]
    offset = view.get("byteOffset", 0) + accessor.get("byteOffset", 0)
    count = accessor["count"]
    positions = np.frombuffer(binary, dtype="<f4", count=3 * count, offset=offset).reshape(-1, 3)
    grown = positions.astype(float)
    centre = grown.mean(axis=0)
    grown[:, [0, 2]] = centre[[0, 2]] + factor * (grown[:, [0, 2]] - centre[[0, 2]])
    stored = grown.astype("<f4")
    gltf["bufferViews"].append(
        {"buffer": 0, "byteOffset": len(binary), "byteLength": stored.nbytes}
    )
    gltf["accessors"].append(
        {
            "bufferView": len(gltf["bufferViews"]) - 1,
            "componentType": 5126,
            "count": count,
            "type": "VEC3",
            "min": stored.min(axis=0).tolist(),
            "max": stored.max(axis=0).tolist(),
        }
    )
    attributes["POSITION"] = len(gltf["accessors"]) - 1
    binary += stored.tobytes()
    gltf["buffers"][0]["byteLength"] = len(binary)
    chunk = json.dumps(gltf, separators=(",", ":")).encode("utf-8")
    chunk += b" " * (-len(chunk) % 4)
    body = (
        len(chunk).to_bytes(4, "little")
        + b"JSON"
        + chunk
        + len(binary).to_bytes(4, "little")
        + b"BIN\x00"
        + binary
    )
    path.write_bytes(data[:8] + (12 + len(body)).to_bytes(4, "little") + body)


def test_pack_checks_reject_collision_beyond_the_footprint(
    fixture_build: FixtureBuild, tmp_path: Path
) -> None:
    pack = tmp_path / "pack"
    shutil.copytree(fixture_build.pack_dir, pack)
    building = next(
        b for b in _json(pack / "buildings.json")["buildings"] if b["id"] == "osm:way/101"
    )
    tile = pack / "tiles" / f"{building['tile']}.glb"
    _grow_glb_node(tile, building["collision_nodes"][0], 1.2)
    meshes = read_glb_meshes(tile)  # the visual mesh is unchanged
    assert np.array_equal(
        meshes.nodes[building["node"]][0],
        read_glb_meshes(fixture_build.pack_dir / "tiles" / tile.name).nodes[building["node"]][0],
    )
    with pytest.raises(PackCheckError, match="bld_osm_way_101_c0-colonly: collision reaches"):
        _check(fixture_build, pack)


def _stray_renamed(pack: Path) -> None:
    """A building's mesh node renamed to one no building in buildings.json has."""
    building = _json(pack / "buildings.json")["buildings"][0]
    _rename_glb_nodes(
        pack / "tiles" / f"{building['tile']}.glb", {building["node"]: "bld_osm_way_999999"}
    )


def _stray_left_out(pack: Path) -> None:
    """A building removed from buildings.json (and its POI links) whose mesh and collision
    nodes stay in its tile, as a building left out of the pack must not."""
    document = _json(pack / "buildings.json")
    building = document["buildings"].pop(0)
    (pack / "buildings.json").write_text(json.dumps(document), encoding="utf-8")
    pois = _json(pack / "pois.json")
    for poi in pois["pois"]:
        if poi["building_id"] == building["id"]:
            poi["building_id"] = None
        poi["contains_building_ids"] = [
            i for i in poi["contains_building_ids"] if i != building["id"]
        ]
    (pack / "pois.json").write_text(json.dumps(pois), encoding="utf-8")


@pytest.mark.parametrize(
    ("mutate", "stray"),
    [(_stray_renamed, "bld_osm_way_999999"), (_stray_left_out, None)],
    ids=["renamed-mesh", "left-out-building"],
)
def test_pack_checks_reject_a_bld_node_of_no_building(
    fixture_build: FixtureBuild,
    tmp_path: Path,
    mutate: Callable[[Path], None],
    stray: str | None,
) -> None:
    pack = tmp_path / "pack"
    shutil.copytree(fixture_build.pack_dir, pack)
    first = _json(pack / "buildings.json")["buildings"][0]
    mutate(pack)
    # Left out: the first stray name (sorted) is the mesh, before its _c<k> bodies.
    name = re.escape(stray if stray is not None else first["node"])
    message = rf"{first['tile']}: bld_ node\(s\) of no building in buildings.json: \['{name}"
    with pytest.raises(PackCheckError, match=message):
        _check(fixture_build, pack)


def test_pack_checks_reject_names_that_clash_after_import(
    fixture_build: FixtureBuild, tmp_path: Path
) -> None:
    pack = tmp_path / "pack"
    shutil.copytree(fixture_build.pack_dir, pack)
    tile = _json(pack / "buildings.json")["buildings"][0]["tile"]
    glb = pack / "tiles" / f"{tile}.glb"
    node = next(n for n in read_glb_node_names(glb) if n.startswith("bld_") and "-" not in n)
    _rename_glb_nodes(glb, {TERRAIN_NODE: f"{node}-convcolonly"})
    with pytest.raises(PackCheckError, match="both import as"):
        check_import_names(tile, read_glb_meshes(glb).names)


@pytest.mark.parametrize(
    "names",
    [
        ["a", "a"],
        ["a", "a-col"],
        ["a-col", "a-colonly"],
        ["a", "a-convcolonly"],
        ["bld_x", "bld_x-colonly"],
        ["bld_x_c0", "bld_x_c0-colonly"],
    ],
)
def test_import_name_clashes_are_found(names: list[str]) -> None:
    with pytest.raises(PackCheckError, match="both import as"):
        check_import_names("t", names)


def test_distinct_import_names_pass() -> None:
    check_import_names(
        "t",
        ["terrain-col", "road-col", "path-col", "bld_x", "bld_x_c0-colonly", "bld_x_c1-colonly"],
    )
    assert import_name("bld_x_c0-colonly") == "bld_x_c0"
    assert import_name("bridge_osm_way_1_p0-col") == "bridge_osm_way_1_p0"
    assert import_name("hull-convcolonly") == "hull"


def test_empty_node_names_are_not_import_clashes() -> None:
    check_import_names("t", ["", "", "terrain-col", ""])
    with pytest.raises(PackCheckError, match="both import as"):
        check_import_names("t", ["", "a", "a-col"])
