"""Terrain UVs and the debug orthophoto textures (`debug/orthophoto/<tile_id>.jpg`).

Synthetic GeoTIFFs over the testtown test area check orientation, coverage and missing
sources; the committed Somero test-area fixture (a real orthophoto cut) checks the real
JPEG 2000 path and the credits. Nothing here touches the network.
"""

import json
import shutil
import warnings
from dataclasses import dataclass
from pathlib import Path
from typing import Any

import numpy as np
import pytest
import rasterio
import yaml
from rasterio.errors import NotGeoreferencedWarning
from rasterio.transform import from_origin

from conftest import (
    FIXED_NOW,
    FIXTURE_ORIGIN,
    FIXTURE_YAML,
    TmpRepo,
    _entry,
    glb_primitive,
    write_fixture_raw,
)
from mapgen.build import BuildError, run_build
from mapgen.build.area import Tile
from mapgen.build.export_glb import TERRAIN_NODE, ExportError, MeshNode, export_glb
from mapgen.build.heightfield import Heightfield
from mapgen.build.orthophoto import (
    GDIGNORE,
    encode_jpeg,
    terrain_uv,
    texture_path,
    texture_size,
)
from mapgen.build.terrain import MeshTerrainBuilder
from mapgen.config import ConfigError, MapConfig
from mapgen.geo import BBox
from mapgen.sources.base import LoadContext
from mapgen.sources.mml import MmlOrthophotoAdapter, OrthophotoOptions

SOMERO_FIXTURE = Path(__file__).resolve().parent / "fixtures" / "raw" / "somero-test-area"
ORTHO_ATTRIBUTION = "Contains data from the National Land Survey of Finland Orthophoto, 10/2026"
TEST_TILES = ("tile_x-1_y-1", "tile_x0_y-1", "tile_x-1_y0", "tile_x0_y0")
# The testtown test area: 300 m centred on the origin, so four 150 m x 150 m tiles.
AREA_WORLD = BBox(
    FIXTURE_ORIGIN[0] - 150,
    FIXTURE_ORIGIN[1] - 150,
    FIXTURE_ORIGIN[0] + 150,
    FIXTURE_ORIGIN[1] + 150,
)
RED, GREEN, BLUE = 0, 1, 2


def _json(path: Path) -> Any:
    return json.loads(path.read_text(encoding="utf-8"))


def _read_jpeg(path: Path) -> np.ndarray:
    with warnings.catch_warnings():
        warnings.simplefilter("ignore", NotGeoreferencedWarning)
        with rasterio.open(path) as src:
            assert src.driver == "JPEG"
            return src.read()


def write_quadrant_ortho(path: Path, bounds: BBox, res: float = 0.5) -> Path:
    """An RGB GeoTIFF over `bounds`: red where north of the origin, blue south of it,
    green east of it (so north-east is yellow)."""
    width, height = round(bounds.width / res), round(bounds.height / res)
    xs = bounds.min_x + (np.arange(width) + 0.5) * res - FIXTURE_ORIGIN[0]
    ys = bounds.max_y - (np.arange(height) + 0.5) * res - FIXTURE_ORIGIN[1]
    gx, gy = np.meshgrid(xs, ys)
    data = np.zeros((3, height, width), dtype=np.uint8)
    data[RED][gy > 0] = 255
    data[BLUE][gy < 0] = 255
    data[GREEN][gx > 0] = 255
    path.parent.mkdir(parents=True, exist_ok=True)
    with rasterio.open(
        path,
        "w",
        driver="GTiff",
        width=width,
        height=height,
        count=3,
        dtype="uint8",
        crs="EPSG:3067",
        transform=from_origin(bounds.min_x, bounds.max_y, res, res),
    ) as dst:
        dst.write(data)
    return path


def _config(root: Path, ortho: dict[str, Any] | None = None, enabled: bool = True) -> MapConfig:
    data = yaml.safe_load(FIXTURE_YAML.read_text(encoding="utf-8"))
    data["sources"]["mml_orthophoto"]["enabled"] = enabled
    data["sources"]["mml_orthophoto"]["options"].update(ortho or {})
    return TmpRepo(root).load(data)


def _raw_with_ortho(root: Path, bounds: BBox | None = AREA_WORLD) -> Path:
    """The testtown raw folder plus a synthetic orthophoto over `bounds` (None: no file)."""
    raw = write_fixture_raw(TmpRepo(root).raw_dir("testtown"))
    if bounds is not None:
        rel = "mml/orthophoto/synthetic.tif"
        write_quadrant_ortho(raw / rel, bounds)
        manifest = _json(raw / "download-manifest.json")
        manifest["files"].append(
            _entry(rel, "mml_orthophoto", "Orthophoto", "CC BY 4.0", ORTHO_ATTRIBUTION)
        )
        (raw / "download-manifest.json").write_text(json.dumps(manifest), encoding="utf-8")
    return raw


@dataclass
class Built:
    pack: Path
    work: Path
    log: list[str]


def _build(root: Path, config: MapConfig, raw: Path) -> Built:
    pack, work = root / "game" / "maps" / "testtown", root / "work"
    log: list[str] = []
    run_build(config, "test", raw, pack, work, now=lambda: FIXED_NOW, log=log.append)
    return Built(pack, work, log)


@pytest.fixture(scope="module")
def ortho_build(tmp_path_factory: pytest.TempPathFactory) -> Built:
    root = tmp_path_factory.mktemp("ortho")
    return _build(root, _config(root), _raw_with_ortho(root))


# Terrain UVs


def test_uv_corners_map_north_west_to_0_0_and_south_east_to_1_1() -> None:
    bounds = BBox(-150.0, 20.0, -50.0, 60.0)  # an edge tile: clipped, not square
    corners = np.array(
        [
            [bounds.min_x, bounds.max_y, 0.0],  # north-west: the image's top-left
            [bounds.max_x, bounds.max_y, 0.0],  # north-east
            [bounds.min_x, bounds.min_y, 0.0],  # south-west
            [bounds.max_x, bounds.min_y, 0.0],  # south-east: bottom-right
            [-100.0, 30.0, 5.0],
        ]
    )
    uv = terrain_uv(corners, bounds)
    np.testing.assert_allclose(uv[:4], [[0, 0], [1, 0], [0, 1], [1, 1]])
    np.testing.assert_allclose(uv[4], [0.5, 0.75])


def _tile_uv(path: Path) -> tuple[np.ndarray, np.ndarray]:
    stored = glb_primitive(path, TERRAIN_NODE)
    return stored["POSITION"], stored["TEXCOORD_0"]


def test_written_uvs_span_each_tile_with_north_at_v0(ortho_build: Built) -> None:
    manifest = _json(ortho_build.pack / "manifest.json")
    for tile in manifest["tiles"]:
        min_x, min_y, max_x, max_y = tile["bounds_local"]
        positions, uv = _tile_uv(ortho_build.pack / tile["file"])
        east, north = positions[:, 0], -positions[:, 2]  # glTF X = east, Z = -north
        assert uv.min() == 0.0 and uv.max() == 1.0
        nw = (east == min_x) & (north == max_y)
        se = (east == max_x) & (north == min_y)
        assert nw.sum() == 1 and se.sum() == 1, tile["id"]
        np.testing.assert_array_equal(uv[nw][0], [0.0, 0.0])
        np.testing.assert_array_equal(uv[se][0], [1.0, 1.0])
        np.testing.assert_allclose(uv[:, 0], (east - min_x) / (max_x - min_x), atol=1e-6)
        np.testing.assert_allclose(uv[:, 1], (max_y - north) / (max_y - min_y), atol=1e-6)


def test_clipped_edge_tile_uvs_follow_its_own_bounds(tmp_path: Path) -> None:
    hf = Heightfield(np.zeros((35, 65)), x0=-10.0, y0=-10.0, dx=2.0, dy=2.0)
    tile = Tile(0, 0, BBox(0.0, 0.0, 100.0, 37.0))  # clipped: 100 m x 37 m
    chunk = MeshTerrainBuilder().build_tile(hf, tile)
    path = tmp_path / "t.glb"
    uv = terrain_uv(chunk.vertices, tile.bounds)
    export_glb(path, [MeshNode(TERRAIN_NODE, chunk.vertices, chunk.faces, chunk.normals, uv)])
    positions, stored = _tile_uv(path)
    north = -positions[:, 2]
    assert np.all(stored[north == 37.0, 1] == 0.0)  # north edge: top row
    assert np.all(stored[north == 0.0, 1] == 1.0)
    assert np.all(stored[positions[:, 0] == 100.0, 0] == 1.0)
    # Normals are still the gradient normals given (flat field: straight up).
    np.testing.assert_allclose(glb_primitive(path, TERRAIN_NODE)["NORMAL"][:, 1], 1.0)


def test_buildings_have_no_uvs_and_node_names_survive(ortho_build: Built) -> None:
    from mapgen.build.export_glb import read_glb_json

    for tile_id in TEST_TILES:
        path = ortho_build.pack / "tiles" / f"{tile_id}.glb"
        gltf = read_glb_json(path)
        for node in gltf["nodes"]:
            if "mesh" not in node:
                continue
            attributes = gltf["meshes"][node["mesh"]]["primitives"][0]["attributes"]
            if node["name"] == TERRAIN_NODE:
                assert "TEXCOORD_0" in attributes and "NORMAL" in attributes
            else:  # buildings, road and path surfaces and bridge decks have no UVs
                name = node["name"]
                assert name.startswith(("bld_", "bridge_osm_")) or name in ("road-col", "path-col")
                assert "TEXCOORD_0" not in attributes


def test_invalid_uvs_are_rejected(tmp_path: Path) -> None:
    vertices = np.array([[0.0, 0, 0], [1, 0, 0], [0, 1, 0]])
    node = MeshNode("x", vertices, np.array([[0, 1, 2]]), uv=np.zeros((2, 2)))
    with pytest.raises(ExportError, match="invalid UVs"):
        export_glb(tmp_path / "x.glb", [node])


# Debug textures


def test_texture_size_rounds_width_and_height() -> None:
    assert texture_size(BBox(0, 0, 256, 256), 0.5) == (512, 512)
    assert texture_size(BBox(0, 0, 256, 100), 1.0) == (256, 100)
    assert texture_size(BBox(0, 0, 150, 37), 0.7) == (214, 53)  # 214.3, 52.9


def test_each_tile_has_a_texture_of_its_size(ortho_build: Built) -> None:
    manifest = _json(ortho_build.pack / "manifest.json")
    assert sorted(t["id"] for t in manifest["tiles"]) == sorted(TEST_TILES)
    for tile in manifest["tiles"]:
        image = _read_jpeg(ortho_build.pack / texture_path(tile["id"]))
        assert image.shape == (3, 300, 300)  # 150 m at 0.5 m/px
    files = sorted(p.name for p in (ortho_build.pack / "debug" / "orthophoto").iterdir())
    assert files == sorted(f"{t}.jpg" for t in TEST_TILES)


def test_textures_are_north_up(ortho_build: Built) -> None:
    """The synthetic raster is red north of the origin, blue south, green east of it."""
    north_west = _read_jpeg(ortho_build.pack / texture_path("tile_x-1_y0"))
    south_east = _read_jpeg(ortho_build.pack / texture_path("tile_x0_y-1"))
    north_east = _read_jpeg(ortho_build.pack / texture_path("tile_x0_y0"))
    assert north_west[RED].mean() > 240 and north_west[BLUE].mean() < 15
    assert north_west[GREEN].mean() < 15
    assert south_east[BLUE].mean() > 240 and south_east[RED].mean() < 15
    assert south_east[GREEN].mean() > 240
    assert north_east[RED].mean() > 240 and north_east[GREEN].mean() > 240


def test_texture_rows_follow_v(tmp_path: Path) -> None:
    """A raster split at a northing inside one tile: rows above the split (small v) are red."""
    root = tmp_path / "repo"
    raw = _raw_with_ortho(root, None)
    rel = "mml/orthophoto/split.tif"
    write_quadrant_ortho(raw / rel, AREA_WORLD)
    # Move the red/blue split 50 m north of the origin: red only in the top 100 of 150 m.
    with rasterio.open(raw / rel, "r+") as dst:
        data = dst.read()
        split_row = round(100 / 0.5)
        data[RED, split_row:, :] = 0
        data[BLUE, split_row:, :] = 255
        data[BLUE, :split_row, :] = 0
        dst.write(data)
    manifest = _json(raw / "download-manifest.json")
    manifest["files"].append(_entry(rel, "mml_orthophoto", "Orthophoto", "CC BY 4.0", "x"))
    (raw / "download-manifest.json").write_text(json.dumps(manifest), encoding="utf-8")
    built = _build(root, _config(root), raw)
    image = _read_jpeg(built.pack / texture_path("tile_x0_y0"))  # local y 0..150
    # 100 m from the north edge (v = 2/3) is row 200 of 300.
    assert image[RED, :195].mean() > 240 and image[BLUE, :195].mean() < 15
    assert image[BLUE, 205:].mean() > 240 and image[RED, 205:].mean() < 15
    positions, uv = _tile_uv(built.pack / "tiles" / "tile_x0_y0.glb")
    near_split = -positions[:, 2] == 51.0  # DEM nodes sit at odd local metres
    assert near_split.any()
    np.testing.assert_allclose(uv[near_split, 1], 99 / 150, atol=1e-6)


def test_gdignore_keeps_debug_out_of_godot(ortho_build: Built) -> None:
    gdignore = ortho_build.pack / "debug" / GDIGNORE
    assert gdignore.is_file() and gdignore.read_bytes() == b""
    assert not (ortho_build.pack / "debug" / "orthophoto" / GDIGNORE).exists()


def test_manifest_credits_the_orthophoto_and_does_not_list_textures(ortho_build: Built) -> None:
    manifest = _json(ortho_build.pack / "manifest.json")
    (credit,) = [c for c in manifest["credits"] if c["source"] == "mml_orthophoto"]
    assert credit["attribution"] == ORTHO_ATTRIBUTION and credit["licence"] == "CC BY 4.0"
    assert "debug" not in json.dumps(manifest)
    assert any("debug orthophoto textures" in line for line in ortho_build.log)


def test_resolution_option_changes_the_texture_size(tmp_path: Path) -> None:
    root = tmp_path / "repo"
    built = _build(root, _config(root, {"debug_texture_m_per_px": 0.7}), _raw_with_ortho(root))
    image = _read_jpeg(built.pack / texture_path("tile_x0_y0"))
    assert image.shape == (3, 214, 214)  # round(150 / 0.7)


@pytest.mark.parametrize("value", [0.4, 1.1, 0.0])
def test_resolution_option_is_validated(tmp_path: Path, value: float) -> None:
    with pytest.raises(ConfigError, match="debug_texture_m_per_px"):
        _config(tmp_path, {"debug_texture_m_per_px": value})


def test_resolution_option_defaults_to_half_a_metre() -> None:
    assert OrthophotoOptions().debug_texture_m_per_px == 0.5


# Missing or partial source


def _assert_no_debug(built: Built) -> None:
    assert (built.pack / "manifest.json").is_file()
    assert not (built.pack / "debug").exists()
    sources = {c["source"] for c in _json(built.pack / "manifest.json")["credits"]}
    assert "mml_orthophoto" not in sources


def test_disabled_source_builds_without_debug_folder(tmp_path: Path) -> None:
    root = tmp_path / "repo"
    built = _build(root, _config(root, enabled=False), _raw_with_ortho(root))
    _assert_no_debug(built)
    assert any("no enabled source provides imagery" in line for line in built.log)


def test_missing_files_build_without_debug_folder(tmp_path: Path) -> None:
    root = tmp_path / "repo"
    built = _build(root, _config(root), _raw_with_ortho(root, None))
    _assert_no_debug(built)
    assert any("no orthophoto files in" in line for line in built.log)
    assert any("no debug/orthophoto/ textures" in line for line in built.log)


def test_partly_covered_tile_fails_and_keeps_the_old_pack(tmp_path: Path) -> None:
    root = tmp_path / "repo"
    config = _config(root)
    raw = _raw_with_ortho(root, None)
    old = _build(root, config, raw)  # a previous pack without debug textures
    before = (old.pack / "manifest.json").read_bytes()
    # Covers everything but the area's eastmost 20 m: the two east tiles are partial.
    partial = BBox(
        AREA_WORLD.min_x - 10, AREA_WORLD.min_y - 10, AREA_WORLD.max_x - 20, AREA_WORLD.max_y + 10
    )
    rel = "mml/orthophoto/partial.tif"
    write_quadrant_ortho(raw / rel, partial)
    manifest = _json(raw / "download-manifest.json")
    manifest["files"].append(_entry(rel, "mml_orthophoto", "Orthophoto", "CC BY 4.0", "x"))
    (raw / "download-manifest.json").write_text(json.dumps(manifest), encoding="utf-8")
    with pytest.raises(BuildError, match=r"don't cover 2 tile\(s\).*tile_x0_y-1, tile_x0_y0"):
        _build(root, config, raw)
    assert (old.pack / "manifest.json").read_bytes() == before
    assert not (old.pack / "debug").exists()


def test_two_files_together_cover_a_tile(tmp_path: Path) -> None:
    """Like the two Somero sheets: a seam through the tiles at the origin's northing."""
    root = tmp_path / "repo"
    raw = _raw_with_ortho(root, None)
    o = FIXTURE_ORIGIN
    halves = {
        "south.tif": BBox(AREA_WORLD.min_x, AREA_WORLD.min_y, AREA_WORLD.max_x, o[1] - 30),
        "north.tif": BBox(AREA_WORLD.min_x, o[1] - 30, AREA_WORLD.max_x, AREA_WORLD.max_y),
    }
    manifest = _json(raw / "download-manifest.json")
    for name, bounds in halves.items():
        write_quadrant_ortho(raw / "mml" / "orthophoto" / name, bounds)
        manifest["files"].append(
            _entry(f"mml/orthophoto/{name}", "mml_orthophoto", "Orthophoto", "CC BY 4.0", "x")
        )
    (raw / "download-manifest.json").write_text(json.dumps(manifest), encoding="utf-8")
    built = _build(root, _config(root), raw)
    image = _read_jpeg(built.pack / texture_path("tile_x0_y-1"))  # local y -150..0
    # Rows near the seam (local y -30, row 240) come from both files without a gap.
    assert image[BLUE, 230:250].mean() > 240 and image[GREEN, 230:250].mean() > 240


def test_adapter_lists_only_overlapping_image_files(
    fixture_config: MapConfig, tmp_path: Path
) -> None:
    folder = tmp_path / "mml" / "orthophoto"
    write_quadrant_ortho(folder / "in.tif", AREA_WORLD)
    far = BBox(AREA_WORLD.min_x + 5000, AREA_WORLD.min_y, AREA_WORLD.max_x + 5000, AREA_WORLD.max_y)
    write_quadrant_ortho(folder / "far.tif", far)
    (folder / "in.tif.ovr").write_bytes(b"not an image")  # overviews are not listed
    ctx = LoadContext(
        map_config=fixture_config,
        source_name="photo",
        options=OrthophotoOptions(debug_texture_m_per_px=0.8),
        raw_dir=tmp_path,
        target_crs="EPSG:3067",
        bounds=AREA_WORLD,
        log=lambda _: None,
    )
    data = MmlOrthophotoAdapter().load(ctx)
    assert data.files == [folder / "in.tif"]
    assert data.imagery is not None and data.imagery.resolution_m == 0.8


def test_jpeg_encoding_round_trips_colours(tmp_path: Path) -> None:
    rgb = np.zeros((3, 32, 48), dtype=np.uint8)
    rgb[RED, :16] = 255
    path = tmp_path / "x.jpg"
    path.write_bytes(encode_jpeg(rgb))
    assert path.read_bytes()[:3] == b"\xff\xd8\xff"
    image = _read_jpeg(path)
    assert image.shape == (3, 32, 48)
    assert image[RED, :14].mean() > 240 and image[RED, 18:].mean() < 15
    assert sorted(p.name for p in tmp_path.iterdir()) == ["x.jpg"]  # no sidecar files


# The committed Somero test-area fixture (real orthophoto cut, JPEG 2000)


@pytest.fixture(scope="module")
def somero_ortho_build(tmp_path_factory: pytest.TempPathFactory) -> Built:
    root = tmp_path_factory.mktemp("somero-ortho")
    config = _config(root)
    raw = TmpRepo(root).raw_dir("testtown")
    shutil.copytree(SOMERO_FIXTURE, raw)
    (raw / "osm" / "somero.osm").rename(raw / "osm" / "testtown.osm")
    manifest = _json(raw / "download-manifest.json")
    for entry in manifest["files"]:
        entry["path"] = entry["path"].replace("osm/somero.osm", "osm/testtown.osm")
    (raw / "download-manifest.json").write_text(json.dumps(manifest), encoding="utf-8")
    return _build(root, config, raw)


def test_fixture_orthophoto_is_small_and_credited_in_its_notice() -> None:
    (cut,) = list((SOMERO_FIXTURE / "mml" / "orthophoto").glob("*.jp2"))
    assert cut.stat().st_size < 1_000_000
    assert ORTHO_ATTRIBUTION in (SOMERO_FIXTURE / "NOTICE.md").read_text(encoding="utf-8")


def test_fixture_build_writes_real_textures_and_credits(somero_ortho_build: Built) -> None:
    pack = somero_ortho_build.pack
    for tile_id in TEST_TILES:
        image = _read_jpeg(pack / texture_path(tile_id))
        assert image.shape == (3, 300, 300)
        assert image.std() > 10  # real imagery, not a flat fill
    credits = _json(pack / "manifest.json")["credits"]
    (credit,) = [c for c in credits if c["source"] == "mml_orthophoto"]
    assert credit == {
        "source": "mml_orthophoto",
        "dataset": "Orthophoto",
        "licence": "CC BY 4.0",
        "attribution": ORTHO_ATTRIBUTION,
        "retrieved_at": "2026-10-27T14:36:09Z",
    }
    assert (pack / "debug" / GDIGNORE).is_file()
