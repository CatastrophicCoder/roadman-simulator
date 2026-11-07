"""The minimap background image (docs/specs/minimap.md, criteria 1, 2, 3 and the pipeline
part of 20).

Three kinds of input:

- unit tests of `render_minimap` on hand-made inputs: a square ring inside a larger grid,
  so the outside and the clipping can be seen;
- `minimap town` builds: the edgeville repo of test_world_edge_acceptance.py with its own
  OSM data and MML GeoPackage (one OSM building, one MML-only building, land use
  farmland, residential and one value not in the table, an OSM lake and stream, an MML
  lake, river area and stream), once in MML water mode and once in OSM water mode;
- the Somero test area built from the committed fixture (`somero_area`).

The minimap town's layout is in the constants below (local x, n; its test-area ring is
the square -150..150, the union of its tiles).
"""

import json
import math
import re
import struct
from collections.abc import Sequence
from dataclasses import dataclass, field
from pathlib import Path
from typing import Any

import numpy as np
import pytest
import rasterio
import shapely
from shapely.geometry import LineString, Point, Polygon, box, shape
from shapely.geometry.base import BaseGeometry

from conftest import FIXTURE_ORIGIN, REPO_ROOT, SomeroArea
from mapgen import paths
from mapgen.build.area import build_area
from mapgen.build.minimap import (
    IMPORT_CONTENT,
    LANDUSE_GROUPS,
    LANDUSE_ORDER,
    MINIMAP_IMPORT,
    MINIMAP_JSON,
    MINIMAP_PNG,
    MINIMAP_SCHEMA,
    PALETTE,
    STAGE_DIR,
    LandUse,
    MinimapError,
    MinimapGrid,
    MinimapInputs,
    minimap_inputs,
    render_minimap,
)
from mapgen.build.pack_checks import PackCheckError, check_minimap
from mapgen.build.stage1_load import run_load
from mapgen.build.world_edge import boundary_for, water_setup
from mapgen.build.world_edge.water import WaterFeature, edge_stream_lines
from mapgen.config import ConfigError
from mapgen.geo import BBox
from test_roads_snippets import Osm
from test_world_edge_acceptance import EdgeMap, _world, build_edge_map, setup_edge_map

# The PNG has no georeferencing on purpose (no sidecar, no metadata).
pytestmark = pytest.mark.filterwarnings("ignore::rasterio.errors.NotGeoreferencedWarning")

COLOURS = {name: np.array(rgba, dtype=np.uint8) for name, rgba in PALETTE.items()}
WORD_LIST_SOURCE = REPO_ROOT / "game" / "Roadman.Core.Tests" / "Items" / "ItemCatalogTests.cs"

# Minimap town (local x, n). The spawn area of the testtown markers (x 40..120,
# n 95..130) stays clear of everything.
OSM_BUILDING = 10
OSM_BUILDING_BOX = (60.0, -70.0, 80.0, -50.0)  # inside the residential area
FARMLAND = 11
FARMLAND_BOX = (-170.0, -120.0, -20.0, -20.0)  # crosses the west edge
RESIDENTIAL = 12
RESIDENTIAL_BOX = (10.0, -120.0, 120.0, -20.0)
UNLISTED = 13
UNLISTED_VALUE = "quarry"
UNLISTED_BOX = (-10.0, -145.0, 5.0, -130.0)
OSM_LAKE = 14
OSM_LAKE_BOX = (-120.0, 40.0, -80.0, 80.0)
OSM_STREAM = 15
OSM_STREAM_X = 50.3
MML_LAKE_BOX = (-40.0, 40.0, 0.0, 80.0)  # jarvi
MML_RIVER_BOX = (-140.0, 110.0, -100.0, 130.0)  # virtavesialue
MML_STREAM_X = -90.3  # virtavesikapea
STREAM_N = (-10.0, 30.0)
MML_ONLY_BOX = (-60.0, -10.0, -45.0, 5.0)  # a building-use record no OSM building matches


def _closed(osm: Osm, way_id: int, bounds: tuple[float, float, float, float], tags: dict) -> None:
    x0, y0, x1, y1 = bounds
    ids = [osm.node(x, y) for x, y in ((x0, y0), (x1, y0), (x1, y1), (x0, y1))]
    osm.way(way_id, [*ids, ids[0]], tags)


def town_osm() -> Osm:
    osm = Osm()
    _closed(osm, OSM_BUILDING, OSM_BUILDING_BOX, {"building": "yes"})
    _closed(osm, FARMLAND, FARMLAND_BOX, {"landuse": "farmland"})
    _closed(osm, RESIDENTIAL, RESIDENTIAL_BOX, {"landuse": "residential"})
    _closed(osm, UNLISTED, UNLISTED_BOX, {"landuse": UNLISTED_VALUE})
    _closed(osm, OSM_LAKE, OSM_LAKE_BOX, {"natural": "water"})
    osm.way(OSM_STREAM, [(OSM_STREAM_X, STREAM_N[0]), (OSM_STREAM_X, STREAM_N[1])],
            {"waterway": "stream"})  # fmt: skip
    return osm


def town_water() -> dict[str, list[tuple[int, Any]] | None]:
    stream = LineString([(MML_STREAM_X, STREAM_N[0]), (MML_STREAM_X, STREAM_N[1])])
    return {
        "jarvi": [(1, _world(box(*MML_LAKE_BOX)))],
        "virtavesialue": [(2, _world(box(*MML_RIVER_BOX)))],
        "virtavesikapea": [(3, _world(stream))],
    }


def _town_options(mml: bool) -> dict[str, Any]:
    return {
        "topographic": mml,
        "osm": town_osm(),
        "water": town_water(),
        "rakennus": [MML_ONLY_BOX],
    }


def build_town(root: Path, *, mml: bool, **extra: Any) -> EdgeMap:
    return build_edge_map(root, **_town_options(mml), **extra)


@pytest.fixture(scope="module")
def town_mml(tmp_path_factory: pytest.TempPathFactory) -> EdgeMap:
    return build_town(tmp_path_factory.mktemp("minimap-mml"), mml=True)


@pytest.fixture(scope="module")
def town_osm_build(tmp_path_factory: pytest.TempPathFactory) -> EdgeMap:
    return build_town(tmp_path_factory.mktemp("minimap-osm"), mml=False)


def _json(path: Path) -> Any:
    return json.loads(path.read_text(encoding="utf-8"))


@dataclass
class Image:
    """A pack's minimap.png with its minimap.json, read back."""

    rgba: np.ndarray  # (4, height, width)
    doc: dict[str, Any]
    report: dict[str, Any] = field(default_factory=dict)

    @classmethod
    def read(cls, pack: Path) -> "Image":
        with rasterio.open(pack / MINIMAP_PNG) as src:
            return cls(src.read(), _json(pack / MINIMAP_JSON))

    @property
    def m(self) -> float:
        return float(self.doc["m_per_px"])

    def pixel(self, x: float, n: float) -> tuple[int, int]:
        """(row, col) of the pixel containing local (x, n)."""
        b = self.doc["bounds_local"]
        return int(math.floor((-n - b["min_z"]) / self.m)), int(
            math.floor((x - b["min_x"]) / self.m)
        )

    def colour_at(self, x: float, n: float) -> str:
        row, col = self.pixel(x, n)
        return _name(self.rgba[:, row, col])

    def centres(self) -> tuple[np.ndarray, np.ndarray]:
        b = self.doc["bounds_local"]
        xs = b["min_x"] + (np.arange(self.doc["width"]) + 0.5) * self.m
        ns = -b["min_z"] - (np.arange(self.doc["height"]) + 0.5) * self.m
        return np.meshgrid(xs, ns)

    def is_colour(self, name: str) -> np.ndarray:
        return (self.rgba == COLOURS[name][:, None, None]).all(axis=0)


def _name(rgba: Sequence[int]) -> str:
    for name, colour in PALETTE.items():
        if tuple(int(v) for v in rgba) == colour:
            return name
    raise AssertionError(f"colour {tuple(rgba)} is not in the palette")


def _palette_only(rgba: np.ndarray) -> None:
    known = np.zeros(rgba.shape[1:], dtype=bool)
    for colour in COLOURS.values():
        known |= (rgba == colour[:, None, None]).all(axis=0)
    assert known.all(), f"{int((~known).sum())} pixels outside the palette"


def _valid_geometry(geometry: BaseGeometry) -> BaseGeometry:
    return geometry if geometry.is_valid else shapely.make_valid(geometry)


def _centre(bounds: tuple[float, float, float, float]) -> tuple[float, float]:
    x0, y0, x1, y1 = bounds
    return ((x0 + x1) / 2, (y0 + y1) / 2)


# --- unit tests on hand-made inputs ---------------------------------------------------

RING = box(20.0, 20.0, 180.0, 180.0)  # inside a 200 x 200 m grid at 0..200
GRID_TILES = [BBox(0.0, 0.0, 200.0, 200.0)]


def _inputs(**changes: Any) -> MinimapInputs:
    values: dict[str, Any] = {
        "ring": RING,
        "landuse": [
            LandUse("osm:way/1", "farmland", box(0, 30, 90, 90)),  # crosses the ring
            LandUse("osm:way/2", "residential", box(100, 30, 170, 90)),
            LandUse("osm:way/3", UNLISTED_VALUE, box(30, 150, 60, 170)),
            LandUse("osm:way/4", UNLISTED_VALUE, box(0, 0, 10, 10)),  # outside: not counted
        ],
        "water_areas": [WaterFeature("w:1", box(100, 120, 190, 160))],  # crosses the ring
        "stream_lines": [WaterFeature("s:1", LineString([(40.3, 100), (40.3, 140)]))],
        "buildings": [("osm:way/9", box(120, 50, 140, 70))],
        "water_mode": "water_areas",
        "water_source": "topo",
    }
    values.update(changes)
    return MinimapInputs(**values)


def _render(m_per_px: float = 1.0, stream_width_m: float = 2.0, **changes: Any) -> Image:
    grid = MinimapGrid.covering(GRID_TILES, m_per_px)
    image = render_minimap(grid, _inputs(**changes), stream_width_m)
    doc = {"m_per_px": m_per_px, "bounds_local": grid.bounds_local(), "width": grid.width,
           "height": grid.height}  # fmt: skip
    return Image(image.rgba(), doc, image.report)


def test_ac2_unit_alpha_follows_the_ring_by_pixel_centre() -> None:
    image = _render()
    gx, gn = image.centres()
    inside = shapely.contains_xy(RING, gx, gn)
    assert (image.rgba[3][inside] == 255).all()
    assert (image.rgba[3][~inside] == 0).all()
    assert (image.rgba[:3][:, ~inside] == 0).all()  # fully transparent, no stray colour
    _palette_only(image.rgba)


def test_ac2_unit_every_layer_has_its_colour_and_is_clipped_to_the_ring() -> None:
    image = _render()
    assert image.colour_at(50, 60) == "green_land"  # farmland
    assert image.colour_at(110, 40) == "built_land"  # residential, away from the building
    assert image.colour_at(130, 60) == "building"  # building over residential land
    assert image.colour_at(45, 160) == "ground"  # unlisted value
    assert image.colour_at(150, 140) == "water"
    assert image.colour_at(40.3, 120) == "water"  # the stream
    assert image.colour_at(100, 100) == "ground"
    for x, n in ((10, 60), (195, 140), (5, 5)):  # the layers' parts outside the ring
        row, col = image.pixel(x, n)
        assert image.rgba[3, row, col] == 0
    assert image.report["landuse_unlisted"] == {UNLISTED_VALUE: 1}


def test_ac2_unit_later_layers_cover_earlier_ones() -> None:
    # A building over a stream over water over residential land over farmland.
    image = _render(
        landuse=[
            LandUse("a", "farmland", box(30, 30, 170, 170)),
            LandUse("b", "residential", box(30, 30, 170, 170)),
        ],
        water_areas=[WaterFeature("w", box(50, 50, 150, 150))],
        stream_lines=[WaterFeature("s", LineString([(40, 40), (160, 160)]))],
        buildings=[("bld", box(90, 90, 110, 110))],
    )
    assert image.colour_at(35, 160) == "built_land"
    assert image.colour_at(60, 140) == "water"
    assert image.colour_at(45, 45) == "water"  # the stream over residential land
    assert image.colour_at(100, 100) == "building"


def test_ac2_unit_no_stream_lines_without_lines() -> None:
    image = _render(stream_lines=[])
    assert image.colour_at(40.3, 120) == "ground"


@pytest.mark.parametrize("m_per_px", [0.5, 1.0, 2.0])
@pytest.mark.parametrize("width_m", [1.4, 2.0, 2.7])
@pytest.mark.parametrize("angle_deg", [0.0, 17.0, 45.0, 90.0, 133.0])
def test_ac2_unit_stream_width_across_its_direction(
    m_per_px: float, width_m: float, angle_deg: float
) -> None:
    """The painted width across the line is within one pixel of `stream_width_m`.

    Measured on every pixel row (a steep line) or column (a flat one) crossing the line
    away from its ends: the painted run along that row times the sine of the angle
    between the row and the line is the width across the line. The run holds the pixel
    centres of an interval of length `w / sin`, so it is that length within one pixel,
    and the width across is `w` within `px * sin`, at most one pixel. The mean width
    (painted area over the stretch's length) is checked too."""
    a = math.radians(angle_deg)
    direction = np.array([math.cos(a), math.sin(a)])
    centre = np.array([100.3, 99.7])
    half_length = 60.0
    line = LineString([centre - half_length * direction, centre + half_length * direction])
    image = _render(
        m_per_px,
        width_m,
        landuse=[],
        water_areas=[],
        buildings=[],
        stream_lines=[WaterFeature("s", line)],
    )
    water = image.is_colour("water")
    gx, gn = image.centres()
    steep = abs(direction[1]) >= abs(direction[0])
    sine = abs(direction[1]) if steep else abs(direction[0])  # row (or column) vs the line
    # Pixel centres whose projection on the line is well inside it (away from the caps).
    along = (gx - centre[0]) * direction[0] + (gn - centre[1]) * direction[1]
    middle = np.abs(along) <= half_length - 2 * width_m
    lines = range(water.shape[0]) if steep else range(water.shape[1])
    checked = 0
    for k in lines:
        cut = (water[k, :] & middle[k, :]) if steep else (water[:, k] & middle[:, k])
        full = middle[k, :] if steep else middle[:, k]
        span = along[k, :] if steep else along[:, k]
        # Only rows whose whole crossing lies in the middle stretch.
        crossing = np.abs(span) <= half_length - 2 * width_m - 3 * width_m / sine
        if not (full & crossing).any() or not (cut & crossing).any():
            continue
        across = int(cut.sum()) * m_per_px * sine
        assert width_m - m_per_px <= across <= width_m + m_per_px, (
            f"{'row' if steep else 'column'} {k}: {across:.3f} m across"
        )
        checked += 1
    assert checked >= 20
    stretch = water & middle
    mean = stretch.sum() * m_per_px**2 / (2 * (half_length - 2 * width_m))
    assert width_m - m_per_px <= mean <= width_m + m_per_px


def test_ac1_unit_grid_is_anchored_at_the_north_west_corner() -> None:
    grid = MinimapGrid.covering([BBox(-10, -20, 5, 0), BBox(5, -20, 30.5, 13.2)], 0.7)
    assert (grid.width, grid.height) == (math.ceil(40.5 / 0.7), math.ceil(33.2 / 0.7))
    b = grid.bounds_local()
    assert (b["min_x"], b["min_z"]) == (-10.0, -13.2)
    assert b["max_x"] - b["min_x"] == pytest.approx(grid.width * 0.7, abs=1e-12)
    assert b["max_z"] - b["min_z"] == pytest.approx(grid.height * 0.7, abs=1e-12)
    assert b["max_x"] >= 30.5 and b["max_z"] >= 20.0
    exact = MinimapGrid.covering([BBox(-150, -150, 150, 150)], 1.0)
    assert (exact.width, exact.height) == (300, 300)
    assert exact.bounds_local() == {"min_x": -150.0, "max_x": 150.0, "min_z": -150.0,
                                    "max_z": 150.0}  # fmt: skip


def test_ac2_unit_rendering_is_deterministic() -> None:
    first, second = _render(), _render()
    assert np.array_equal(first.rgba, second.rgba)


# --- minimap town builds (criterion 2 on whole builds) --------------------------------


@pytest.fixture(params=["mml", "osm"])
def town(request: pytest.FixtureRequest) -> EdgeMap:
    return request.getfixturevalue("town_mml" if request.param == "mml" else "town_osm_build")


def _world_edge_water(edge: EdgeMap) -> list[tuple[str, Any]]:
    """The water areas the world-edge stage wrote to `10-world-edge/boundary.geojson`."""
    features = _json(edge.work / "10-world-edge" / "boundary.geojson")["features"]
    origin = np.array(FIXTURE_ORIGIN)
    return [
        (f["properties"]["id"], shapely.transform(shape(f["geometry"]), lambda c: c - origin))
        for f in features
        if f["properties"].get("kind") == "water_area"
    ]


def _building_footprints(edge: EdgeMap) -> dict[str, Any]:
    features = _json(edge.work / "03-buildings" / "buildings.geojson")["features"]
    origin = np.array(FIXTURE_ORIGIN)
    return {
        f["properties"]["id"]: shapely.transform(shape(f["geometry"]), lambda c: c - origin)
        for f in features
    }


def test_ac1_town_build_writes_the_three_files(town: EdgeMap) -> None:
    for name in (MINIMAP_PNG, MINIMAP_JSON, MINIMAP_IMPORT):
        assert (town.pack / name).is_file()
    assert (town.pack / MINIMAP_IMPORT).read_bytes() == b'[remap]\n\nimporter="keep"\n'
    assert IMPORT_CONTENT == '[remap]\n\nimporter="keep"\n'
    assert town.report is not None and town.report.minimap_size == (300, 300)
    assert "minimap" in town.report.timings_s


def test_ac2_town_layer_colours(town: EdgeMap) -> None:
    mml = _json(town.work / STAGE_DIR / "report.json")["water"]["mode"] == "water_areas"
    image = Image.read(town.pack)
    _palette_only(image.rgba)
    assert (image.rgba[3] == 255).all()  # the test-area ring is the tiles' union
    assert image.colour_at(*_centre(OSM_BUILDING_BOX)) == "building"
    assert image.colour_at(-100, -70) == "green_land"  # farmland
    assert image.colour_at(30, -100) == "built_land"  # residential, away from the building
    assert image.colour_at(*_centre(UNLISTED_BOX)) == "ground"
    assert image.colour_at(0, 0) == "ground"
    assert image.colour_at(OSM_STREAM_X, 10) == "ground"  # OSM stream lines are never drawn
    if mml:
        assert image.colour_at(*_centre(MML_ONLY_BOX)) == "building"
        assert image.colour_at(*_centre(MML_LAKE_BOX)) == "water"
        assert image.colour_at(*_centre(MML_RIVER_BOX)) == "water"
        assert image.colour_at(MML_STREAM_X, 10) == "water"
        assert image.colour_at(*_centre(OSM_LAKE_BOX)) == "ground"
    else:
        assert image.colour_at(*_centre(OSM_LAKE_BOX)) == "water"
        assert image.colour_at(*_centre(MML_LAKE_BOX)) == "ground"
        assert image.colour_at(*_centre(MML_RIVER_BOX)) == "ground"
        assert image.colour_at(MML_STREAM_X, 10) == "ground"


def test_ac2_mml_mode_uses_jarvi_and_virtavesialue_and_paints_the_stream(
    town_mml: EdgeMap,
) -> None:
    report = _json(town_mml.work / STAGE_DIR / "report.json")
    assert (report["water"]["mode"], report["water"]["source"]) == (
        "water_areas",
        "mml_topographic",
    )
    assert report["water"]["areas"] == 2 and report["water"]["stream_lines"] == 1
    ids = sorted(i for i, _ in _world_edge_water(town_mml))
    assert ids == ["mml:jarvi/1", "mml:virtavesialue/2"]
    image = Image.read(town_mml.pack)
    water = image.is_colour("water")
    width_px = _json(town_mml.work / STAGE_DIR / "report.json")["water"]["stream_width_m"] / image.m
    for n in np.arange(STREAM_N[0] + 3, STREAM_N[1] - 3, 1.0):
        row, _ = image.pixel(MML_STREAM_X, n)
        across = int(water[row].sum())
        assert width_px - 1 <= across <= width_px + 1, f"{across} px across at n {n}"
    assert image.colour_at(MML_STREAM_X + 3, 10) == "ground"


def test_ac2_osm_mode_uses_the_osm_areas_and_paints_no_line(town_osm_build: EdgeMap) -> None:
    report = _json(town_osm_build.work / STAGE_DIR / "report.json")
    assert (report["water"]["mode"], report["water"]["source"]) == ("osm", "osm")
    assert report["water"]["stream_lines"] == 0
    assert [i for i, _ in _world_edge_water(town_osm_build)] == [f"osm:way/{OSM_LAKE}"]


def test_ac2_water_pixels_are_exactly_the_world_edge_water_areas(town: EdgeMap) -> None:
    """Every pixel centre in one of the world edge's water areas (and in no building) is
    water, and every water pixel is in one of them or on a stream line."""
    image = Image.read(town.pack)
    areas = shapely.union_all([g for _, g in _world_edge_water(town)])
    buildings = shapely.union_all(list(_building_footprints(town).values()))
    width = _json(town.work / STAGE_DIR / "report.json")["water"]["stream_width_m"]
    streams = (
        LineString([(MML_STREAM_X, STREAM_N[0]), (MML_STREAM_X, STREAM_N[1])]).buffer(width / 2)
        if _json(town.work / STAGE_DIR / "report.json")["water"]["mode"] == "water_areas"
        else Polygon()
    )
    _assert_water_matches(image, areas, buildings, streams)


def _assert_water_matches(
    image: Image, areas: BaseGeometry, buildings: BaseGeometry, streams: BaseGeometry
) -> None:
    """Every pixel centre in `areas` (the world edge's water) and in no building is water,
    and every water pixel is in `areas` or `streams` (the buffered stream lines)."""
    gx, gn = image.centres()
    water = image.is_colour("water")
    none = np.zeros_like(water)
    in_water = shapely.contains_xy(areas, gx, gn) if not areas.is_empty else none
    in_building = shapely.intersects_xy(buildings, gx, gn) if not buildings.is_empty else none
    assert water[in_water & ~in_building].all()
    in_stream = shapely.intersects_xy(streams, gx, gn) if not streams.is_empty else none
    assert not (water & ~in_water & ~in_stream).any()


def test_ac2_minimap_water_is_the_world_edge_water_objects(tmp_path: Path) -> None:
    """The minimap takes the world-edge stage's water features: the same geometry objects,
    not copies, in MML and in OSM mode."""
    for mml in (True, False):
        edge = setup_edge_map(tmp_path / ("mml" if mml else "osm"), **_town_options(mml))
        area = build_area(edge.config, "test")
        boundary = boundary_for(edge.config, area)
        loaded = run_load(edge.config, area, edge.raw, edge.work / "01-load", log=lambda _: None)
        _, edge_areas, _ = water_setup(loaded, boundary.ring, edge.config.world_edge)
        inputs = minimap_inputs(loaded, boundary.polygon(), [])
        assert [a.id for a in inputs.water_areas] == [a.id for a in edge_areas]
        assert inputs.water_areas, "the town has water in both modes"
        for ours, theirs in zip(inputs.water_areas, edge_areas, strict=True):
            assert ours.geometry is theirs.geometry
        lines = [w.geometry for w in loaded.edge_water_lines]
        if mml:
            ours = [s.geometry for s in inputs.stream_lines]
            assert len(ours) == len(lines) == 1
            assert all(a is b for a, b in zip(ours, lines, strict=True))
        else:
            assert inputs.stream_lines == []


def test_ac2_building_pixels_cover_every_building_in_buildings_json(town: EdgeMap) -> None:
    buildings = _json(town.pack / "buildings.json")["buildings"]
    footprints = _building_footprints(town)
    image = Image.read(town.pack)
    for building in buildings:
        point = footprints[building["id"]].buffer(-1.0).representative_point()
        assert image.colour_at(point.x, point.y) == "building", building["id"]
    ids = {b["id"] for b in buildings}
    report = _json(town.work / STAGE_DIR / "report.json")
    assert report["features"]["buildings"] == len(ids)
    if report["water"]["mode"] == "water_areas":
        assert any(i.startswith("mml:") for i in ids), "an MML-only building is in the pack"


def test_ac2_unlisted_landuse_is_counted_in_the_log_and_report(town: EdgeMap) -> None:
    report = _json(town.work / STAGE_DIR / "report.json")
    assert report["landuse_unlisted"] == {UNLISTED_VALUE: 1}
    assert report["features"]["landuse"] == {"green_land": 1, "built_land": 1}
    assert any(
        line.startswith("[minimap] landuse values not in the table") and UNLISTED_VALUE in line
        for line in town.logs
    )
    assert sum(report["pixels"].values()) == 300 * 300


# --- the Somero test area (criteria 1 and 2) ------------------------------------------


def test_ac1_somero_test_area_minimap(somero_area: SomeroArea) -> None:
    doc = _json(somero_area.pack / MINIMAP_JSON)
    MINIMAP_SCHEMA.validate(doc, MINIMAP_JSON)
    manifest = _json(somero_area.pack / "manifest.json")
    assert (doc["map_id"], doc["area"]) == ("somero", "test")
    assert (doc["schema_version"], doc["image"], doc["m_per_px"]) == (1, "minimap.png", 1.0)
    assert (doc["width"], doc["height"]) == (300, 300)
    b = doc["bounds_local"]
    assert b["max_x"] - b["min_x"] == doc["width"] * doc["m_per_px"]
    assert b["max_z"] - b["min_z"] == doc["height"] * doc["m_per_px"]
    for tile in manifest["tiles"]:
        min_x, min_n, max_x, max_n = tile["bounds_local"]
        assert b["min_x"] <= min_x and max_x <= b["max_x"]
        assert b["min_z"] <= -max_n and -min_n <= b["max_z"]
    with rasterio.open(somero_area.pack / MINIMAP_PNG) as src:
        assert (src.width, src.height, src.count) == (300, 300, 4)
        assert src.dtypes == ("uint8",) * 4
    assert (somero_area.pack / MINIMAP_IMPORT).read_text(encoding="utf-8") == IMPORT_CONTENT


def test_ac2_somero_test_area_content(somero_area: SomeroArea, tmp_path: Path) -> None:
    image = Image.read(somero_area.pack)
    _palette_only(image.rgba)
    ring = [(p[0], -p[1]) for p in _json(somero_area.pack / "boundary.json")["boundary"]["ring"]]
    gx, gn = image.centres()
    inside = shapely.contains_xy(Polygon(ring), gx, gn)
    assert (image.rgba[3][inside] == 255).all() and (image.rgba[3][~inside] == 0).all()
    features = _json(somero_area.work / "03-buildings" / "buildings.geojson")["features"]
    origin = np.array([somero_area.config.origin.x, somero_area.config.origin.y])
    checked = 0
    for f in features:
        inner = shapely.transform(shape(f["geometry"]), lambda c: c - origin).buffer(-1.0)
        if inner.is_empty:
            continue
        point = inner.representative_point()
        assert image.colour_at(point.x, point.y) == "building", f["properties"]["id"]
        checked += 1
    assert checked > 0
    report = _json(somero_area.work / STAGE_DIR / "report.json")
    assert report["water"]["mode"] == "water_areas"  # Somero uses the MML water
    assert report["pixels"]["building"] > 0

    # Land use and water, from the test-area load the build made.
    config = somero_area.config
    area = build_area(config, "test")
    boundary = boundary_for(config, area)
    raw = paths.raw_dir(somero_area.root, config.id)
    loaded = run_load(config, area, raw, tmp_path / "01-load", log=lambda _: None)
    ring_polygon = boundary.polygon()
    buildings = shapely.union_all(
        [shapely.transform(shape(f["geometry"]), lambda c: c - origin) for f in features]
    )
    water_areas = shapely.union_all(
        [shapely.transform(shape(f["geometry"]), lambda c: c - origin) for f in
         _json(somero_area.work / "10-world-edge" / "boundary.geojson")["features"]
         if f["properties"].get("kind") == "water_area"]
    )  # fmt: skip
    streams = shapely.union_all(
        [line.geometry.buffer(report["water"]["stream_width_m"] / 2)
         for line in edge_stream_lines(loaded)]
    )  # fmt: skip
    _assert_water_matches(image, water_areas, buildings, streams)

    listed = [a for a in loaded.landuse if a.tags.get("landuse") in LANDUSE_GROUPS]
    later = {
        group: shapely.union_all(
            [_valid_geometry(a.geometry) for a in listed
             if LANDUSE_ORDER.index(LANDUSE_GROUPS[a.tags["landuse"]])
             > LANDUSE_ORDER.index(group)]
        )
        for group in LANDUSE_ORDER
    }  # fmt: skip
    covering = shapely.union_all([buildings, water_areas, streams]).buffer(1.0)
    landuse_checked = 0
    for land in listed:
        group = LANDUSE_GROUPS[land.tags["landuse"]]
        free = (
            _valid_geometry(land.geometry)
            .intersection(ring_polygon)
            .difference(covering)
            .difference(later[group].buffer(1.0))
            .buffer(-1.0)
        )
        if free.is_empty:
            continue
        point = free.representative_point()
        assert image.colour_at(point.x, point.y) == group, (land.id, land.tags["landuse"])
        landuse_checked += 1
    assert landuse_checked > 0, "no listed land use area of the test area was checked"


# --- determinism and config (criterion 3) ---------------------------------------------


def _chunks(png: bytes) -> list[str]:
    assert png[:8] == b"\x89PNG\r\n\x1a\n"
    names, i = [], 8
    while i < len(png):
        (length,) = struct.unpack(">I", png[i : i + 4])
        names.append(png[i + 4 : i + 8].decode("ascii"))
        i += 12 + length
    return names


def test_ac3_two_builds_write_identical_minimap_files(tmp_path: Path, town_mml: EdgeMap) -> None:
    again = build_town(tmp_path, mml=True)
    for name in (MINIMAP_PNG, MINIMAP_JSON, MINIMAP_IMPORT):
        assert (again.pack / name).read_bytes() == (town_mml.pack / name).read_bytes(), name
    # No metadata, time or text chunk, and no sidecar file.
    assert _chunks((again.pack / MINIMAP_PNG).read_bytes()) == ["IHDR", "IDAT", "IEND"]
    assert not list(again.pack.glob("*.aux.xml")) and not list(again.pack.glob("*.wld"))


def test_ac3_m_per_px_and_stream_width_change_the_image(tmp_path: Path) -> None:
    edge = build_town(
        tmp_path,
        mml=True,
        data_update={"minimap": {"m_per_px": 0.5, "stream_width_m": 2.7}},
    )
    doc = _json(edge.pack / MINIMAP_JSON)
    assert (doc["m_per_px"], doc["width"], doc["height"]) == (0.5, 600, 600)
    report = _json(edge.work / STAGE_DIR / "report.json")
    assert report["water"]["stream_width_m"] == 2.7


@pytest.mark.parametrize(
    ("block", "key"),
    [
        ({"m_per_px": 0.49}, "minimap.m_per_px"),
        ({"m_per_px": 2.01}, "minimap.m_per_px"),
        ({"m_per_px": 0}, "minimap.m_per_px"),
        ({"stream_width_m": 1.39}, "minimap.stream_width_m"),
        ({"stream_width_m": 2.71}, "minimap.stream_width_m"),
        ({"colour": "red"}, "minimap.colour"),
    ],
)
def test_ac3_config_range_errors_name_the_key(
    tmp_repo: Any, fixture_data: dict[str, Any], block: dict[str, Any], key: str
) -> None:
    fixture_data["minimap"] = block
    with pytest.raises(ConfigError, match=re.escape(key)):
        tmp_repo.load(fixture_data)


@pytest.mark.parametrize(
    "block",
    [{}, {"m_per_px": 0.5}, {"m_per_px": 2.0}, {"stream_width_m": 1.4}, {"stream_width_m": 2.7}],
)
def test_ac3_config_limits_are_allowed(
    tmp_repo: Any, fixture_data: dict[str, Any], block: dict[str, Any]
) -> None:
    fixture_data["minimap"] = block
    config = tmp_repo.load(fixture_data)
    assert config.minimap.m_per_px == block.get("m_per_px", 1.0)
    assert config.minimap.stream_width_m == block.get("stream_width_m", 2.0)


# --- schema and pack checks -----------------------------------------------------------


def test_committed_minimap_schema_matches_the_model() -> None:
    import jsonschema

    assert MINIMAP_SCHEMA.committed() == MINIMAP_SCHEMA.generated(), (
        "minimap-v1.schema.json is out of date; regenerate it with: uv run python -c "
        '"from mapgen.build.minimap import MINIMAP_SCHEMA; MINIMAP_SCHEMA.write()"'
    )
    jsonschema.Draft202012Validator.check_schema(MINIMAP_SCHEMA.committed())


@pytest.mark.parametrize(
    "change",
    [
        {"schema_version": 2},
        {"extra": 1},
        {"image": "map.png"},
        {"width": 0},
        {"area": "half"},
    ],
)
def test_the_schema_rejects_bad_documents(somero_area: SomeroArea, change: dict) -> None:
    doc = {**_json(somero_area.pack / MINIMAP_JSON), **change}
    with pytest.raises(MinimapError):
        MINIMAP_SCHEMA.validate(doc, MINIMAP_JSON)


@dataclass
class PackCopy:
    pack: Path
    manifest: dict[str, Any] = field(default_factory=dict)


@pytest.fixture
def pack_copy(somero_area: SomeroArea, tmp_path: Path) -> PackCopy:
    import shutil

    pack = tmp_path / "pack"
    pack.mkdir()
    for name in (MINIMAP_PNG, MINIMAP_JSON, MINIMAP_IMPORT, "boundary.json", "manifest.json"):
        shutil.copyfile(somero_area.pack / name, pack / name)
    copy = PackCopy(pack, _json(pack / "manifest.json"))
    check_minimap(copy.pack, copy.manifest)  # the untouched copy passes
    return copy


def _rewrite_png(pack: Path, edit: Any) -> None:
    from mapgen.build.minimap import write_png

    with rasterio.open(pack / MINIMAP_PNG) as src:
        rgba = src.read()
    write_png(pack / MINIMAP_PNG, edit(rgba))


def test_pack_check_fails_on_a_wrong_import_file(pack_copy: PackCopy) -> None:
    (pack_copy.pack / MINIMAP_IMPORT).write_text('[remap]\n\nimporter="texture"\n')
    with pytest.raises(PackCheckError, match="minimap.png.import"):
        check_minimap(pack_copy.pack, pack_copy.manifest)
    (pack_copy.pack / MINIMAP_IMPORT).unlink()
    with pytest.raises(PackCheckError, match="missing"):
        check_minimap(pack_copy.pack, pack_copy.manifest)


def test_pack_check_fails_on_a_size_mismatch(pack_copy: PackCopy) -> None:
    _rewrite_png(pack_copy.pack, lambda rgba: rgba[:, :-1, :])
    with pytest.raises(PackCheckError, match="300 x 299"):
        check_minimap(pack_copy.pack, pack_copy.manifest)


def test_pack_check_fails_on_alpha_inside_the_ring(pack_copy: PackCopy) -> None:
    def clear(rgba: np.ndarray) -> np.ndarray:
        rgba = rgba.copy()
        rgba[:, 150, 150] = 0
        return rgba

    _rewrite_png(pack_copy.pack, clear)
    with pytest.raises(PackCheckError, match="wrong alpha"):
        check_minimap(pack_copy.pack, pack_copy.manifest)


def test_pack_check_fails_on_a_colour_outside_the_palette(pack_copy: PackCopy) -> None:
    def tint(rgba: np.ndarray) -> np.ndarray:
        rgba = rgba.copy()
        rgba[0, 10, 10] = 1
        return rgba

    _rewrite_png(pack_copy.pack, tint)
    with pytest.raises(PackCheckError, match="not in the palette"):
        check_minimap(pack_copy.pack, pack_copy.manifest)


def test_pack_check_fails_on_bounds_not_covering_a_tile(pack_copy: PackCopy) -> None:
    doc = _json(pack_copy.pack / MINIMAP_JSON)
    doc["bounds_local"] = {k: v + 1.0 for k, v in doc["bounds_local"].items()}
    (pack_copy.pack / MINIMAP_JSON).write_text(json.dumps(doc), encoding="utf-8")
    with pytest.raises(PackCheckError, match="doesn't cover"):
        check_minimap(pack_copy.pack, pack_copy.manifest)


def test_pack_check_fails_on_bounds_not_matching_the_size(pack_copy: PackCopy) -> None:
    doc = _json(pack_copy.pack / MINIMAP_JSON)
    doc["bounds_local"]["max_x"] += 0.5
    (pack_copy.pack / MINIMAP_JSON).write_text(json.dumps(doc), encoding="utf-8")
    with pytest.raises(PackCheckError, match="max_x - min_x"):
        check_minimap(pack_copy.pack, pack_copy.manifest)


def test_pack_check_fails_on_another_maps_minimap(pack_copy: PackCopy) -> None:
    doc = _json(pack_copy.pack / MINIMAP_JSON)
    doc["area"] = "full"
    (pack_copy.pack / MINIMAP_JSON).write_text(json.dumps(doc), encoding="utf-8")
    with pytest.raises(PackCheckError, match="map_id/area"):
        check_minimap(pack_copy.pack, pack_copy.manifest)


def test_pack_check_judges_alpha_against_a_ring_inside_the_image(pack_copy: PackCopy) -> None:
    """A ring smaller than the image (as a custom boundary polygon gives): pixels outside
    it must be transparent."""
    from mapgen.build.minimap import write_png

    boundary = _json(pack_copy.pack / "boundary.json")
    boundary["boundary"]["ring"] = [[-100.0, 100.0], [100.0, 100.0], [100.0, -100.0],
                                    [-100.0, -100.0]]  # fmt: skip
    (pack_copy.pack / "boundary.json").write_text(json.dumps(boundary), encoding="utf-8")
    with pytest.raises(PackCheckError, match="outside"):
        check_minimap(pack_copy.pack, pack_copy.manifest)
    grid = MinimapGrid.covering([BBox(-150, -150, 150, 150)], 1.0)
    image = render_minimap(grid, MinimapInputs(box(-100, -100, 100, 100), [], [], [], []), 2.0)
    write_png(pack_copy.pack / MINIMAP_PNG, image.rgba())
    check_minimap(pack_copy.pack, pack_copy.manifest)


# --- other games' names (criterion 20, ADR 0019) --------------------------------------


def brand_and_game_words() -> list[str]:
    """The ADR 0019 word list, read from the one place it is written
    (`ItemCatalogTests.BrandAndGameWords`), so the pipeline checks the same names."""
    text = WORD_LIST_SOURCE.read_text(encoding="utf-8")
    match = re.search(r"BrandAndGameWords\s*=\s*\[(.*?)\];", text, re.S)
    assert match, f"BrandAndGameWords not found in {WORD_LIST_SOURCE}"
    body = "\n".join(line.split("//", 1)[0] for line in match.group(1).splitlines())
    words = re.findall(r'"((?:[^"\\]|\\.)*)"', body)
    assert len(words) > 50 and "Grand Theft Auto" in words
    return words


def _word_pattern(words: Sequence[str]) -> re.Pattern[str]:
    alternatives = "|".join(re.escape(w) for w in words)
    return re.compile(rf"(?<![^\W_])({alternatives})(?![^\W_])", re.IGNORECASE)


def test_ac20_word_list_matches_whole_words_only() -> None:
    pattern = _word_pattern(brand_and_game_words())
    assert pattern.search("a map of grand theft auto")
    assert pattern.search("built_land_gta")  # '_' counts as a word break
    assert not pattern.search("bicycle ground filament")  # "bic", "fila" inside words


def test_ac20_no_other_games_names_in_the_minimap_step(somero_area: SomeroArea) -> None:
    import mapgen.build.minimap as module

    pattern = _word_pattern(brand_and_game_words())
    texts = {
        "minimap.py": Path(module.__file__).read_text(encoding="utf-8"),
        "palette": " ".join(PALETTE),
        "landuse groups": " ".join([*LANDUSE_GROUPS, *LANDUSE_GROUPS.values(), *LANDUSE_ORDER]),
        "names": " ".join([STAGE_DIR, MINIMAP_PNG, MINIMAP_JSON, MINIMAP_IMPORT, IMPORT_CONTENT]),
        "minimap.json": (somero_area.pack / MINIMAP_JSON).read_text(encoding="utf-8"),
        "report.json": (somero_area.work / STAGE_DIR / "report.json").read_text(encoding="utf-8"),
        "schema": MINIMAP_SCHEMA.path().read_text(encoding="utf-8"),
    }
    for what, text in texts.items():
        found = pattern.findall(text)
        assert not found, f"{what} contains {found}"


def test_ac2_point_helpers_agree() -> None:
    image = _render()
    row, col = image.pixel(20.5, 179.5)
    gx, gn = image.centres()
    assert (gx[row, col], gn[row, col]) == (20.5, 179.5)
    assert Point(20.5, 179.5).within(RING)
