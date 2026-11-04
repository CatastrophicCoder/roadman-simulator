"""MML-only buildings: records of the building-use source that no OSM building matched.

Criterion numbers (`acN`) refer to docs/specs/mml-only-buildings.md. Two layers:

- Unit tests call `mapgen.build.mml_only` on shapes in local coordinates, a steep sloped
  `Heightfield` and point arrays (no files).
- One snippet map ("mmlville") is built end to end: a hand-written OSM file, a `rakennus`
  GeoPackage written with sqlite3, the sloped plane DEM, a synthetic LAZ and an override
  file. Every case sits at its own place in the test area (300 m square around the
  origin): y = 100 selection (criterion 1), y = 60 cutting (2), y = 15 lidar (3),
  y = -35 categories (5), y = -100 a road.

The Somero test area built from the committed fixture is checked for criterion 12.
Expected values come from the snippet layout and the spec. Nothing touches the network.
"""

import copy
import json
import math
import shutil
from collections import Counter
from dataclasses import dataclass, replace
from pathlib import Path
from typing import Any

import jsonschema
import numpy as np
import pytest
import shapely
import yaml
from shapely.geometry import MultiPolygon, Polygon, box, shape

from conftest import (
    FIXED_NOW,
    FIXTURE_ORIGIN,
    FIXTURE_YAML,
    SomeroArea,
    TmpRepo,
    lidar_entry,
    plane_z,
    read_tile,
    write_fixture_raw,
    write_laz,
    write_rakennus_gpkg,
)
from mapgen import paths
from mapgen.build import BuildError, BuildReport, run_build
from mapgen.build.area import BuildArea
from mapgen.build.buildings import HEIGHT_SOURCES, resolve_buildings
from mapgen.build.heightfield import Heightfield
from mapgen.build.lidar import REJECT_REASONS, LidarParams, PointIndex, lidar_height
from mapgen.build.mml_only import (
    CONTRADICTION_HEIGHT_M,
    MmlOnlySelection,
    compactness,
    cut_record,
    ground_check,
    is_sliver,
    mml_only_report,
    opened_share,
    road_overlaps,
    select_mml_only,
)
from mapgen.build.pack_checks import check_mml_only
from mapgen.build.places.categories import CATEGORIES, CATEGORY_SOURCES
from mapgen.build.places.matching import match_building_use
from mapgen.build.places.model import BUILDINGS_SCHEMA, BUILDINGS_SCHEMA_VERSION, OWN_WORK
from mapgen.build.stage1_load import LocalBuildingUse, LocalFootprint
from mapgen.config import MapConfig
from mapgen.geo import BBox
from mapgen.sources.base import PointCloud
from mapgen.sources.mml import rakennus_rule
from pack_checks import PackCheckError, check_pack
from test_roads_snippets import Osm

CRS = "EPSG:3067"
REC = "mml:rakennus/"

# Unit-test helpers: a 200 m area and a steep slope (0.3 m per m east), so a ground check
# against one height for the whole footprint would be off by metres.

AREA = BuildArea("test", BBox(-100, -100, 100, 100), 50.0)
SLOPE = (100.0, 0.3, 0.1)  # z = a + b * x + c * y


def slope_z(x: Any, y: Any) -> Any:
    a, b, c = SLOPE
    return a + b * np.asarray(x, dtype=float) + c * np.asarray(y, dtype=float)


def _heightfield() -> Heightfield:
    g = np.arange(-110.0, 110.1, 2.0)
    gx, gy = np.meshgrid(g, g)
    return Heightfield(z=slope_z(gx, gy), x0=-110.0, y0=-110.0, dx=2.0, dy=2.0)


HF = _heightfield()


def rec(
    number: int, geometry: Polygon | MultiPolygon, feature_class: int = 42211
) -> LocalBuildingUse:
    use_class = {42211: 1, 42221: 2, 42261: 6}.get(feature_class)
    return LocalBuildingUse(
        f"{REC}{number}", geometry, feature_class, use_class, rakennus_rule(feature_class)
    )


def osm(way: int, geometry: Polygon | MultiPolygon) -> LocalFootprint:
    return LocalFootprint(f"osm:way/{way}", geometry, {"building": "yes"})


def cloud(xy: Any, z: Any, classification: int = 1) -> PointCloud:
    xy = np.atleast_2d(np.asarray(xy, dtype=float))
    n = len(xy)
    z = np.broadcast_to(np.asarray(z, dtype=float), n).copy()
    return PointCloud(
        xy[:, 0].copy(),
        xy[:, 1].copy(),
        z,
        np.full(n, classification, dtype=np.uint8),
        np.ones(n, dtype=np.uint8),
        np.ones(n, dtype=np.uint8),
        CRS,
    )


def select(
    records: list[LocalBuildingUse],
    footprints: list[LocalFootprint] = (),  # type: ignore[assignment]
    points: PointCloud | None = None,
    ground_points: PointCloud | None = None,
) -> MmlOnlySelection:
    footprints = list(footprints)
    matches = match_building_use(footprints, records)
    return select_mml_only(
        records, matches, footprints, AREA, HF, points, ground_points, "mml_topographic"
    )


def outcomes(selection: MmlOnlySelection) -> dict[str, str]:
    return {r.id: r.outcome for r in selection.records}


def l_shape(record: tuple[float, float, float, float], w: float, h: float) -> Polygon:
    """An OSM footprint covering `record` and more, except a w x h piece in its north-east
    corner (the cut leaves exactly that piece). No edge of it runs along the record's."""
    x0, y0, x1, y1 = record
    return Polygon(
        [
            (x0 - 5, y0 - 10),
            (x1 + 5, y0 - 10),
            (x1 + 5, y1 - h),
            (x1 - w, y1 - h),
            (x1 - w, y1 + 10),
            (x0 - 5, y1 + 10),
        ]
    )


# AC1: which records become buildings


def test_ac1_record_with_no_osm_building_near_is_added_whole() -> None:
    record = rec(1, box(0, 0, 10, 10))
    selection = select([record], [osm(1, box(40, 40, 50, 50))])
    (only,) = selection.records
    assert (only.outcome, only.is_cut, only.cut_by) == ("added", False, ())
    assert only.kept is not None and only.kept.equals(record.geometry)
    (footprint,) = selection.footprints()
    assert (footprint.id, dict(footprint.tags)) == (f"{REC}1", {})
    assert selection.records_by_id() == {f"{REC}1": record}


def test_ac1_matched_record_is_not_added_or_considered() -> None:
    record = rec(2, box(-0.5, -0.5, 10.5, 10.5))
    selection = select([record], [osm(2, box(0, 0, 10, 10))])
    assert selection.records == []
    assert (selection.records_in_area, selection.records_matched) == (1, 1)


def test_ac1_record_with_its_centroid_outside_the_area_is_outside() -> None:
    selection = select([rec(3, box(96, 0, 106, 10))])  # overlaps the area, centroid x = 101
    assert outcomes(selection) == {f"{REC}3": "outside"}
    assert selection.added == []


def test_ac1_record_whose_kept_part_has_its_centroid_outside_the_area_is_outside() -> None:
    """The record's centroid (x = 98) is inside the area (max x = 100), but the OSM
    footprint cuts away its west part, so the kept part (x 99-108, centroid x = 103.5)
    would belong to no tile: `outside`, not added."""
    record = rec(5, box(88, 0, 108, 10))
    footprint = osm(5, box(80, -5, 99, 15))  # shares 110 m², under half of its 380 m²
    selection = select([record], [footprint])
    (only,) = selection.records
    assert only.outcome == "outside"
    assert only.cut_by == ("osm:way/5",)
    assert only.kept is not None and only.kept.equals(box(99, 0, 108, 10))
    assert not AREA.contains(only.kept.centroid.x, only.kept.centroid.y)
    assert AREA.contains(record.geometry.centroid.x, record.geometry.centroid.y)
    assert selection.added == [] and selection.footprints() == []
    report = mml_only_report(selection, [])
    assert report["not_added"]["outside"] == 1
    assert report["not_added_ids"]["outside"] == [f"{REC}5"]


@pytest.mark.parametrize(
    ("size", "outcome"), [(0.9, "too_small"), (1.1, "added")], ids=["0.9_m2", "1.1_m2"]
)
def test_ac1_records_under_one_square_metre_are_too_small(size: float, outcome: str) -> None:
    selection = select([rec(4, box(0, 0, size, 1.0))])
    assert outcomes(selection) == {f"{REC}4": outcome}


def test_ac1_records_are_looked_at_in_record_number_order() -> None:
    records = [rec(n, box(20 * k, 0, 20 * k + 10, 10)) for k, n in enumerate((300, 20, 1000))]
    assert [r.id for r in select(records).records] == [f"{REC}{n}" for n in (20, 300, 1000)]


def test_ac1_without_a_building_use_source_nothing_is_selected() -> None:
    selection = select_mml_only(None, {}, [], AREA, HF)
    assert (selection.source, selection.records, selection.footprints()) == (None, [], [])


# AC2: cutting


def test_ac2_half_covered_record_keeps_its_uncovered_half() -> None:
    cover = box(-20, 0, 10, 10)  # 300 m², a third of it on the record: not matched
    selection = select([rec(5, box(0, 0, 20, 10))], [osm(5, cover)])
    (only,) = selection.records
    assert only.outcome == "added" and only.is_cut
    assert only.cut_by == ("osm:way/5",)
    assert only.kept is not None
    assert only.kept.area == pytest.approx(100.0)
    assert only.kept.symmetric_difference(box(10, 0, 20, 10)).area < 1e-9
    assert only.kept.intersection(cover).area < 0.01
    assert only.record_area_m2 == pytest.approx(200.0)


# Pieces for the sliver rule (criterion 2): area under 4 m², or less than half of the
# area left after an opening by 0.75 m (`buffer(-0.75).buffer(0.75)`).


def l_strip(width: float, arm: float) -> Polygon:
    """An L-shaped strip `width` wide, each arm `arm` long from the outer corner."""
    return Polygon([(0, 0), (arm, 0), (arm, width), (width, width), (width, arm), (0, arm)])


SLIVER_CASES: dict[str, tuple[Polygon, bool]] = {
    "3.9_m2_rectangle": (box(0, 0, 1.95, 2.0), True),  # by area, though 1.95 m wide
    "4.1_m2_rectangle_1.6_m_wide": (box(0, 0, 1.6, 2.5625), False),  # opened share ~0.88
    "straight_strip_1.4_m_10_m2": (box(0, 0, 1.4, 10 / 1.4), True),
    "straight_strip_1.6_m_10_m2": (box(0, 0, 1.6, 10 / 1.6), False),
    "l_strip_1.4_m_short_arms": (l_strip(1.4, 7.0), True),
    "l_strip_1.4_m_20_m_arms": (l_strip(1.4, 20.0), True),
    "l_strip_1.6_m_20_m_arms": (l_strip(1.6, 20.0), False),
    "compact_annex_3_x_2.5_m": (box(0, 0, 3.0, 2.5), False),
}


@pytest.mark.parametrize("case", list(SLIVER_CASES))
def test_ac2_is_sliver_by_area_or_opened_share(case: str) -> None:
    piece, sliver = SLIVER_CASES[case]
    assert is_sliver(piece) is sliver
    opened = piece.buffer(-0.75).buffer(0.75).area / piece.area
    assert opened_share(piece) == pytest.approx(opened)
    assert (piece.area < 4.0 or opened < 0.5) is sliver  # the spec's rule, written out


def test_ac2_the_4_1_m2_rectangle_keeps_about_88_percent_after_the_opening() -> None:
    piece = box(0, 0, 1.6, 2.5625)
    assert piece.area == pytest.approx(4.1)
    assert opened_share(piece) == pytest.approx(rect_opened_share(1.6, 2.5625), abs=1e-3)
    assert opened_share(piece) == pytest.approx(0.88, abs=0.01)


def rect_opened_share(w: float, h: float) -> float:
    """A w x h rectangle (both over 1.5 m) loses (1 - pi/4) r² at each corner."""
    return (w * h - 4 * (1 - math.pi / 4) * 0.75**2) / (w * h)


def cut_leaving(piece: Polygon) -> tuple[Polygon, Polygon]:
    """A record and an OSM footprint whose difference is exactly `piece`, turned by 180°
    so that an L's outer corner is the record's north-east corner. The OSM footprint
    covers the rest of the record and reaches past it on every side (the piece is swept
    10 m north-east out of it), so no OSM edge runs along a record edge; it is big enough
    not to match the record."""
    turned = shapely.affinity.rotate(piece, 180, origin=(0, 0))
    min_x, min_y, max_x, max_y = turned.bounds
    size = max(max_x - min_x, max_y - min_y) + 3.0
    placed = shapely.affinity.translate(turned, size - max_x, size - max_y)
    record = box(0, 0, size, size)
    steps = np.arange(0.0, 10.01, 0.5)
    reach = shapely.union_all(
        [shapely.affinity.translate(placed, dx, dy) for dx in steps for dy in steps]
    )
    cover = box(-30, -30, size + 5, size + 5).difference(reach)
    assert isinstance(cover, Polygon)
    assert record.difference(cover).symmetric_difference(placed).area < 1e-6
    return record, cover


@pytest.mark.parametrize("case", list(SLIVER_CASES))
def test_ac2_cut_piece_is_kept_or_discarded_by_the_sliver_rule(case: str) -> None:
    piece, sliver = SLIVER_CASES[case]
    record, cover = cut_leaving(piece)
    selection = select([rec(6, record)], [osm(6, cover)])
    (only,) = selection.records
    assert only.cut_by == ("osm:way/6",)
    if sliver:
        assert (only.outcome, only.kept, only.pieces, only.pieces_discarded) == (
            "slivers",
            None,
            (),
            1,
        )
    else:
        assert only.outcome == "added"
        assert only.kept is not None and only.kept.area == pytest.approx(piece.area)
        assert (len(only.pieces), only.pieces_discarded) == (1, 0)
        assert only.kept.intersection(cover).area < 0.01


def test_ac2_record_leaving_two_kept_pieces_is_one_building_with_two_parts() -> None:
    # An OSM bar across the record (4 m x 40 m, a quarter of it on the record).
    selection = select([rec(7, box(0, 0, 20, 10))], [osm(7, box(7, -15, 11, 25))])
    (only,) = selection.records
    assert only.outcome == "added"
    assert isinstance(only.kept, MultiPolygon)
    assert [p.area for p in only.pieces] == pytest.approx([90.0, 70.0])  # largest first
    assert [p.area for p in only.kept.geoms] == pytest.approx([90.0, 70.0])
    (building,) = resolve_buildings(selection.footprints(), HF, AREA).buildings
    assert building.id == f"{REC}7"
    assert building.node == "bld_mml_rakennus_7"
    assert building.collision_nodes == (
        "bld_mml_rakennus_7_c0-colonly",
        "bld_mml_rakennus_7_c1-colonly",
    )
    assert len(building.collision_meshes()) == 2


def test_ac2_record_leaving_only_slivers_is_discarded_as_slivers() -> None:
    # Two strips, 1 m wide, left on both sides of the OSM footprint.
    selection = select([rec(8, box(0, 0, 10, 10))], [osm(8, box(1, -20, 9, 30))])
    (only,) = selection.records
    assert (only.outcome, only.pieces_discarded, only.cut_by) == ("slivers", 2, ("osm:way/8",))
    assert selection.footprints() == []


def test_ac2_record_covered_completely_leaves_nothing_and_is_slivers() -> None:
    """Edge case: the difference has no polygon (nothing, or lines and points only)."""
    selection = select([rec(9, box(0, 0, 10, 10))], [osm(9, box(-10, -10, 20, 20))])
    (only,) = selection.records
    assert (only.outcome, only.pieces_discarded) == ("slivers", 0)


def test_ac2_record_touching_an_osm_footprint_only_along_an_edge_is_added_whole() -> None:
    record = rec(10, box(0, 0, 10, 10))
    selection = select([record], [osm(10, box(10, 0, 20, 10))])
    (only,) = selection.records
    assert (only.outcome, only.is_cut, only.pieces_discarded) == ("added", False, 0)
    assert only.kept is not None and only.kept.equals(record.geometry)


def test_ac2_two_mml_only_records_sharing_a_wall_are_both_added() -> None:
    selection = select([rec(11, box(0, 0, 10, 10)), rec(12, box(10, 0, 20, 10))])
    assert outcomes(selection) == {f"{REC}11": "added", f"{REC}12": "added"}
    buildings = resolve_buildings(selection.footprints(), HF, AREA).buildings
    assert [len(b.collision_nodes) for b in buildings] == [1, 1]


def test_ac2_cut_record_reports_the_osm_footprints_sharing_area_only() -> None:
    record = box(0, 0, 20, 10)
    result = cut_record(
        record,
        [
            ("osm:way/3", box(15, -10, 30, 20)),  # shares area
            ("osm:way/2", box(-20, 0, 0, 10)),  # touches the west edge only
            ("osm:way/1", box(-10, 2, 4, 4)),  # shares area
        ],
    )
    assert result.cut_by == ("osm:way/1", "osm:way/3")
    assert len(result.kept) == 1 and result.kept[0].area == pytest.approx(150.0 - 8.0)


# Compactness (report `cut[].pieces[].compactness`)


@pytest.mark.parametrize(
    ("polygon", "expected"),
    [
        (box(0, 0, 10, 10), math.pi / 4),  # a square: about 0.785
        (box(0, 0, 50, 0.5), 4 * math.pi * 25 / 101**2),  # a long thin strip: about 0.031
        (shapely.Point(0, 0).buffer(10, quad_segs=256), 1.0),  # nearly a circle
    ],
    ids=["square", "strip", "circle"],
)
def test_compactness_is_4_pi_area_over_perimeter_squared(polygon: Polygon, expected: float) -> None:
    assert compactness(polygon) == pytest.approx(expected, abs=1e-3)


def test_compactness_of_a_strip_is_near_zero_and_of_a_square_near_0_785() -> None:
    assert compactness(box(0, 0, 10, 10)) == pytest.approx(0.785, abs=0.001)
    assert compactness(box(0, 0, 100, 0.2)) < 0.01


# AC3: lidar contradiction


def _ground_cloud(xy: np.ndarray, above: np.ndarray, classification: int) -> PointCloud:
    return cloud(xy, slope_z(xy[:, 0], xy[:, 1]) + above, classification)


def _points_in_box(n: int) -> np.ndarray:
    """n distinct points inside box(0, 0, 10, 10), spread over its whole width (where the
    steep slope's ground differs by up to 3 m)."""
    return np.column_stack([0.5 + 9.0 * np.arange(n) / max(n - 1, 1), np.full(n, 5.0)])


@pytest.mark.parametrize(
    ("near", "high", "outcome"),
    [(9, 1, "lidar_contradicts"), (8, 2, "added"), (9, 0, "added")],
    ids=["10_points_90_percent_removed", "10_points_80_percent_added", "9_points_added"],
)
def test_ac3_footprint_whose_points_are_mostly_on_the_ground_is_removed(
    near: int, high: int, outcome: str
) -> None:
    xy = _points_in_box(near + high)
    # Ground points (class 2) on the slope, 0 to 1.4 m off it; the others 4 m up (class 1).
    offsets = np.linspace(-0.4, 1.4, near)
    ground = _ground_cloud(xy[:near], offsets, 2)
    roof = _ground_cloud(xy[near:], np.full(high, 4.0), 1)
    selection = select([rec(20, box(0, 0, 10, 10))], points=roof, ground_points=ground)
    (only,) = selection.records
    assert only.outcome == outcome
    assert only.ground is not None
    assert (only.ground.points, only.ground.within) == (near + high, near)
    assert only.ground.max_above_m == pytest.approx(4.0 if high else 1.4)


def test_ac3_the_ground_is_sampled_at_each_point_of_a_sloped_dem() -> None:
    """Ground points on a 3 m slope across the footprint all count as within 1.5 m."""
    xy = _points_in_box(10)
    ground = _ground_cloud(xy, np.zeros(10), 2)
    check = ground_check(box(0, 0, 10, 10), PointIndex(ground), HF)
    assert (check.points, check.within, check.contradicts) == (10, 10, True)
    assert check.max_above_m == pytest.approx(0.0, abs=1e-9)
    # One ground height for the whole footprint would put some points over 1.5 m off.
    assert slope_z(9.5, 5) - slope_z(0.5, 5) > CONTRADICTION_HEIGHT_M


def test_ac3_points_below_the_ground_count_by_their_absolute_difference() -> None:
    xy = _points_in_box(10)
    below = _ground_cloud(xy, np.full(10, -1.6), 2)
    check = ground_check(box(0, 0, 10, 10), PointIndex(below), HF)
    assert (check.points, check.within, check.contradicts) == (10, 0, False)


def test_ac3_points_outside_the_kept_footprint_do_not_count() -> None:
    # The record is cut in half; ten ground points lie on the cut-away half.
    xy = np.column_stack([np.linspace(0.5, 9.5, 10), np.full(10, 5.0)])
    ground = _ground_cloud(xy, np.zeros(10), 2)
    selection = select(
        [rec(21, box(0, 0, 20, 10))], [osm(21, box(-20, 0, 10, 10))], ground_points=ground
    )
    (only,) = selection.records
    assert only.outcome == "added"
    assert only.ground is not None and only.ground.points == 0


def test_ac3_without_points_the_lidar_step_is_skipped() -> None:
    (only,) = select([rec(22, box(0, 0, 10, 10))]).records
    assert (only.outcome, only.ground) == ("added", None)


# AC4: height


def _roof(polygon: Polygon, z: float, spacing: float = 0.5) -> PointCloud:
    min_x, min_y, max_x, max_y = polygon.bounds
    gx, gy = np.meshgrid(
        np.arange(min_x + spacing / 2, max_x, spacing),
        np.arange(min_y + spacing / 2, max_y, spacing),
    )
    xy = np.column_stack([gx.ravel(), gy.ravel()])
    xy = xy[shapely.contains_xy(polygon, xy[:, 0], xy[:, 1])]
    return cloud(xy, np.full(len(xy), z), 5)


def test_ac4_added_building_gets_the_lidar_height_of_its_kept_footprint() -> None:
    record = box(0, 0, 20, 10)
    cover = box(-20, 0, 10, 10)
    # The cut-away half is 6 m higher than the kept half.
    points = PointCloud.concatenate(
        [_roof(box(10, 0, 20, 10), 110.0), _roof(box(0, 0, 10, 10), 116.0)], CRS
    )
    selection = select([rec(30, record)], [osm(30, cover)], points=points)
    params = LidarParams()
    (building,) = resolve_buildings(
        selection.footprints(), HF, AREA, points=points, lidar_params=params
    ).buildings
    (only,) = selection.records
    assert only.kept is not None
    expected = lidar_height(only.kept, PointIndex(points), HF.min_under(only.kept), params)
    assert expected.ok
    assert building.height_source == "mml_lidar"
    assert building.lidar == expected
    assert building.height_m == pytest.approx(expected.height_m)
    assert expected.roof_z_median == pytest.approx(110.0)


def test_ac4_added_building_without_roof_points_gets_the_default_height() -> None:
    empty = PointCloud.empty(CRS)
    selection = select([rec(31, box(0, 0, 10, 10))], points=empty, ground_points=empty)
    (building,) = resolve_buildings(
        selection.footprints(), HF, AREA, points=PointCloud.empty(CRS)
    ).buildings
    assert (building.height_m, building.height_source) == (6.0, "default")
    assert building.lidar is not None and building.lidar.rejected == "few_points"


def test_ac3_an_empty_point_cloud_without_ground_points_is_a_cloud_with_no_points() -> None:
    """`points` given but empty, `ground_points` None: the lidar step runs and finds no
    points (an empty `PointCloud` is falsy, which must not make it look missing)."""
    selection = select([rec(32, box(0, 0, 10, 10))], points=PointCloud.empty(CRS))
    (only,) = selection.records
    assert only.outcome == "added"
    assert only.ground is not None and only.ground.points == 0


# Road overlaps (report `road_overlaps`)


def test_road_overlaps_lists_buildings_sharing_area_with_a_surface() -> None:
    road = shapely.LineString([(-50, 0), (50, 0)]).buffer(3, cap_style="flat")
    buildings = [
        ("b", box(0, 2, 10, 12)),  # 1 m on the road
        ("a", box(20, -5, 30, 5)),  # across it
        ("c", box(0, 3, 10, 13)),  # touching its edge only
        ("d", box(0, 40, 10, 50)),  # far away
    ]
    assert road_overlaps(buildings, [road]) == ["a", "b"]
    assert road_overlaps(buildings, []) == []


# AC8: the MML-only pack checks


def _pack_building(bid: str, **fields: Any) -> dict[str, Any]:
    own = bid if bid.startswith("mml:") else None
    return {"id": bid, "mml_building": own, "mml_overlap": 1.0 if own else None, **fields}


@pytest.mark.parametrize(
    ("dx", "fails"),
    [(0.0, False), (0.0009, False), (0.002, True), (1.0, True)],
    ids=["touching", "0.009_m2", "0.02_m2", "10_m2"],
)
def test_ac8_check_mml_only_fails_an_overlap_of_0_01_m2_or_more(dx: float, fails: bool) -> None:
    buildings = [_pack_building("osm:way/1"), _pack_building(f"{REC}2")]
    geometries = {"osm:way/1": box(0, 0, 10, 10), f"{REC}2": box(10 - dx, 0, 20, 10)}
    if fails:
        with pytest.raises(PackCheckError, match=f"{REC}2 overlaps osm:way/1"):
            check_mml_only(buildings, geometries)
    else:
        assert check_mml_only(buildings, geometries) == 1


@pytest.mark.parametrize(
    ("field", "value", "message"),
    [
        ("mml_building", f"{REC}1", "isn't the building's own id"),
        ("mml_building", None, "isn't the building's own id"),
        ("mml_overlap", 0.5, "mml_overlap isn't 1.0"),
    ],
    ids=["other_record", "no_record", "overlap_0.5"],
)
def test_ac8_check_mml_only_fails_mml_fields_other_than_the_own_record(
    field: str, value: Any, message: str
) -> None:
    building = {**_pack_building(f"{REC}2"), field: value}
    with pytest.raises(PackCheckError, match=message):
        check_mml_only([building], {f"{REC}2": box(0, 0, 1, 1)})


# The snippet map, built end to end.

MAP_ID = "mmlville"
TOPO_FILE = "mml/topographic/rakennus.gpkg"
LAZ_FILE = "mml/lidar_05p/synthetic.laz"
OWN = {"source": "playtest 2026-11-02", "licence": OWN_WORK}
OVERRIDES: dict[str, dict[str, Any]] = {
    f"{REC}2030": {"category": "public", "name": "Talo", **OWN},
    f"{REC}2004": {"category": "retail", **OWN},  # a too_small record: never added
    f"{REC}99999": {"category": "retail", **OWN},  # no such record
}


@dataclass(frozen=True)
class Rec:
    """An MML record (local box), its feature class, and what the spec says becomes of it."""

    bounds: tuple[float, float, float, float]
    feature_class: int
    outcome: str  # matched / outside / too_small / slivers / lidar_contradicts / added
    kept_area: float | None = None  # for cut buildings
    category: tuple[str, str | None] | None = None  # added buildings: (category, source)
    height: str = "default"  # added buildings: height source


def _box(x: float, y: float, w: float, h: float) -> tuple[float, float, float, float]:
    return (x, y, x + w, y + h)


RES = ("residential", "mml_use_class")
RECORDS: dict[int, Rec] = {
    # y = 100: criterion 1.
    2001: Rec(_box(-130, 100, 10, 10), 42211, "added", category=RES, height="mml_lidar"),
    2002: Rec((-100.5, 99.5, -89.5, 110.5), 42211, "matched"),
    2003: Rec(_box(146, 100, 10, 8), 42211, "outside"),  # centroid x = 151
    2004: Rec(_box(-70, 100, 0.9, 1.0), 42211, "too_small"),
    2005: Rec(_box(-50, 100, 1.1, 1.0), 42211, "added", category=RES),
    # y = 60: criterion 2.
    2006: Rec(_box(-130, 60, 20, 10), 42211, "added", 100.0, RES, "mml_lidar"),  # half
    2007: Rec(_box(-95, 60, 10, 10), 42211, "slivers"),  # leaves 3.9 m²
    2008: Rec(_box(-65, 60, 10, 10), 42211, "added", 4.1, RES),  # 1.6 x 2.5625 m
    2009: Rec(_box(-35, 60, 10, 10), 42211, "slivers"),  # leaves a 1.4 m strip
    2010: Rec(_box(-5, 60, 10, 10), 42211, "added", 10.0, RES),  # leaves a 1.6 m strip
    2011: Rec(_box(25, 60, 20, 10), 42211, "added", 160.0, RES),  # two pieces
    2012: Rec(_box(60, 60, 10, 10), 42211, "slivers"),  # covered completely
    # y = 15: criterion 3.
    2020: Rec(_box(-130, 10, 10, 10), 42211, "lidar_contradicts"),
    2021: Rec(_box(-100, 10, 10, 10), 42211, "added", category=RES),
    2022: Rec(_box(-70, 10, 10, 10), 42211, "added", category=RES),
    # y = -35: criterion 5.
    2030: Rec(_box(-130, -40, 10, 10), 42211, "added", category=("public", "override")),
    2031: Rec(_box(-100, -40, 10, 10), 42261, "added", category=("retail", "osm_poi")),
    # Off the school area's centre, so the area's representative point isn't on it.
    2032: Rec(_box(-74, -44, 8, 8), 42261, "added", category=("education", "osm_poi_area")),
    2033: Rec(_box(-40, -40, 6.5, 6), 42261, "added", category=("garage_or_shed", "mml_use_class")),
    2034: Rec(_box(-10, -40, 8.2, 5), 42261, "added", category=("residential", "osm_landuse")),
    2035: Rec(
        _box(20, -40, 10, 6), 42261, "added", 39.0, ("garage_or_shed", "mml_use_class")
    ),  # 60 m² cut to 39 m²
    2036: Rec(_box(55, -40, 10, 10), 42221, "added", category=("industrial", "osm_landuse")),
    2037: Rec(_box(85, -40, 10, 10), 42221, "added", category=("commercial", "mml_use_class")),
    2038: Rec(_box(110, -40, 10, 10), 42290, "added", category=("unknown", None)),
    # y = -100: on a road.
    2040: Rec(_box(-5, -104, 10, 8), 42211, "added", category=RES),
}
USE_CLASS = {42211: 1, 42221: 2, 42261: 6, 42290: 9}
ADDED = {n for n, r in RECORDS.items() if r.outcome == "added"}
CUT = {n for n, r in RECORDS.items() if r.kept_area is not None}
NOT_ADDED = ("outside", "too_small", "slivers", "lidar_contradicts")

# OSM buildings: way id -> polygon (local).
OSM_BUILDINGS: dict[int, Polygon] = {
    3002: box(-100, 100, -90, 110),  # matches 2002
    3006: box(-145, 55, -120, 75),  # a third of it on 2006
    3007: l_shape(RECORDS[2007].bounds, 1.95, 2.0),
    3008: l_shape(RECORDS[2008].bounds, 1.6, 2.5625),
    3009: l_shape(RECORDS[2009].bounds, 1.4, 10 / 1.4),
    3010: l_shape(RECORDS[2010].bounds, 1.6, 10 / 1.6),
    3011: box(32, 45, 36, 85),  # a bar across 2011: 70 m² west, 90 m² east
    3012: box(55, 50, 85, 80),
    3035: box(26.5, -50, 40, -24),
}
CUT_BY = {2006: 3006, 2007: 3007, 2008: 3008, 2009: 3009, 2010: 3010, 2011: 3011, 2012: 3012}
CUT_BY[2035] = 3035
BAKERY = (-95.0, -35.0, {"shop": "bakery", "name": "Leipomo"})
SCHOOL = (4032, (-75, -45, -55, -25), {"amenity": "school", "name": "Koulu"})
LANDUSE = {
    5034: ((-15, -45, 3, -25), {"landuse": "residential"}),
    5036: ((50, -45, 70, -25), {"landuse": "industrial"}),
}
ROAD_Y = -100.0
ROOF_ABOVE = 5.0  # 2001 and the kept half of 2006: roof this far above the plane DEM
COVERED_ROOF_ABOVE = 11.0  # 2006: roof on the part cut away (under OSM way 3006)


def _rect(o: Osm, way: int, bounds: tuple[float, float, float, float], tags: dict) -> None:
    x0, y0, x1, y1 = bounds
    refs = [o.node(x, y) for x, y in [(x0, y0), (x1, y0), (x1, y1), (x0, y1)]]
    o.ways[way] = ([*refs, refs[0]], tags)


def snippet_osm() -> tuple[Osm, int]:
    """The OSM snippet and the bakery node's id."""
    o = Osm()
    for way, polygon in sorted(OSM_BUILDINGS.items()):
        refs = [o.node(x, y) for x, y in list(polygon.exterior.coords)[:-1]]
        o.ways[way] = ([*refs, refs[0]], {"building": "yes"})
    bakery = o.node(*BAKERY)
    _rect(o, SCHOOL[0], SCHOOL[1], SCHOOL[2])  # type: ignore[arg-type]
    for way, (bounds, tags) in LANDUSE.items():
        _rect(o, way, bounds, tags)  # type: ignore[arg-type]
    o.way(7001, [(-140.0, ROAD_Y), (140.0, ROAD_Y)], {"highway": "residential"})
    return o, bakery


def _grid(bounds: tuple[float, float, float, float], spacing: float = 0.7) -> np.ndarray:
    x0, y0, x1, y1 = bounds
    gx, gy = np.meshgrid(np.arange(x0 + 0.35, x1, spacing), np.arange(y0 + 0.35, y1, spacing))
    return np.column_stack([gx.ravel(), gy.ravel()])


def snippet_points() -> tuple[np.ndarray, np.ndarray, np.ndarray]:
    """Local x/y, z and class of every lidar point of the snippet."""
    xy: list[np.ndarray] = []
    z: list[np.ndarray] = []
    cls: list[np.ndarray] = []

    def add(points: np.ndarray, above: np.ndarray | float, classification: int) -> None:
        xy.append(points)
        z.append(plane_z(points[:, 0], points[:, 1]) + above)
        cls.append(np.full(len(points), classification))

    # Flat roofs: z is the plane at the footprint's centre plus ROOF_ABOVE.
    for bounds, above in (
        (RECORDS[2001].bounds, ROOF_ABOVE),
        ((-120.0, 60.0, -110.0, 70.0), ROOF_ABOVE),  # 2006, the kept half
        ((-130.0, 60.0, -120.0, 70.0), COVERED_ROOF_ABOVE),  # 2006, under OSM 3006
    ):
        points = _grid(bounds)
        cx, cy = (bounds[0] + bounds[2]) / 2, (bounds[1] + bounds[3]) / 2
        xy.append(points)
        z.append(np.full(len(points), plane_z(cx, cy) + above))
        cls.append(np.full(len(points), 5))

    def row(bounds: tuple[float, float, float, float], n: int) -> np.ndarray:
        x0, y0, _, _ = bounds
        return np.column_stack([x0 + 0.5 + np.arange(n), np.full(n, y0 + 5.0)])

    # 2020: 9 ground points and 1 point 5 m up (90 % within 1.5 m).
    pts = row(RECORDS[2020].bounds, 10)
    add(pts[:9], np.linspace(-0.3, 1.2, 9), 2)
    add(pts[9:], 5.0, 1)
    # 2021: 8 ground points and 2 points 5 m up (80 %).
    pts = row(RECORDS[2021].bounds, 10)
    add(pts[:8], 0.2, 2)
    add(pts[8:], 5.0, 1)
    # 2022: 9 ground points and a noise point (class 7) on the ground, which never counts.
    pts = row(RECORDS[2022].bounds, 10)
    add(pts[:9], 0.0, 2)
    add(pts[9:], 0.0, 7)
    return np.vstack(xy), np.concatenate(z), np.concatenate(cls)


def mml_rows() -> list[tuple[int, Any, int | None, int | None]]:
    ox, oy = FIXTURE_ORIGIN
    return [
        (
            n,
            shapely.affinity.translate(box(*r.bounds), ox, oy),
            r.feature_class,
            USE_CLASS[r.feature_class],
        )
        for n, r in sorted(RECORDS.items())
    ]


@dataclass
class MmlBuild:
    root: Path
    config: MapConfig
    raw: Path
    map_dir: Path
    pack: Path
    work: Path
    report: BuildReport
    logs: list[str]
    bakery: int

    def json(self, rel: str) -> Any:
        base = self.work if rel.startswith("0") else self.pack
        return json.loads((base / rel).read_text(encoding="utf-8"))

    def buildings(self) -> dict[str, dict[str, Any]]:
        return {b["id"]: b for b in self.json("buildings.json")["buildings"]}

    def block(self) -> dict[str, Any]:
        return self.json("03-buildings/report.json")["mml_only"]

    def footprints(self) -> dict[str, Any]:
        """The 03-buildings footprints by id, in local coordinates."""
        ox, oy = FIXTURE_ORIGIN
        features = self.json("03-buildings/buildings.geojson")["features"]
        return {
            f["properties"]["id"]: shapely.affinity.translate(shape(f["geometry"]), -ox, -oy)
            for f in features
        }


def _add_manifest_entry(raw: Path, entry: dict[str, Any]) -> None:
    path = raw / "download-manifest.json"
    document = json.loads(path.read_text(encoding="utf-8"))
    document["files"].append(entry)
    path.write_text(json.dumps(document), encoding="utf-8")


def setup_snippets(root: Path, *, topographic: bool = True) -> tuple[MapConfig, Path, Path, int]:
    """The repo for a snippet build: config, raw folder, map folder, bakery node id."""
    data = yaml.safe_load(FIXTURE_YAML.read_text(encoding="utf-8"))
    data["id"] = MAP_ID
    data["sources"]["mml_topographic"]["enabled"] = topographic
    data["sources"]["mml_lidar"] = {"adapter": "mml_lidar", "enabled": True, "options": {}}
    repo = TmpRepo(root)
    (root / "tools" / "mapgen").mkdir(parents=True, exist_ok=True)
    config = repo.load(copy.deepcopy(data))
    raw = write_fixture_raw(repo.raw_dir(MAP_ID), MAP_ID)
    o, bakery = snippet_osm()
    o.write(raw / "osm" / f"{MAP_ID}.osm")
    xy, z, cls = snippet_points()
    ox, oy = FIXTURE_ORIGIN
    write_laz(raw / LAZ_FILE, xy[:, 0] + ox, xy[:, 1] + oy, z, classification=cls)
    _add_manifest_entry(raw, lidar_entry(LAZ_FILE))
    if topographic:
        write_rakennus_gpkg(raw / TOPO_FILE, mml_rows())
        entry = lidar_entry(TOPO_FILE)
        entry.update(
            source="mml_topographic",
            dataset="Topographic Database",
            attribution="Contains data from the National Land Survey of Finland "
            "Topographic Database",
        )
        _add_manifest_entry(raw, entry)
    map_dir = paths.map_dir(root, MAP_ID)
    path = map_dir / "overrides" / "buildings.json"
    path.parent.mkdir(parents=True)
    path.write_text(json.dumps({"schema_version": 1, "buildings": OVERRIDES}), encoding="utf-8")
    return config, raw, map_dir, bakery


def build(root: Path, *, topographic: bool = True) -> MmlBuild:
    config, raw, map_dir, bakery = setup_snippets(root, topographic=topographic)
    logs: list[str] = []
    pack, work = paths.pack_dir(root, MAP_ID), paths.work_dir(root, MAP_ID, "test")
    report = run_build(
        config, "test", raw, pack, work, map_dir=map_dir, now=lambda: FIXED_NOW, log=logs.append
    )
    return MmlBuild(root, config, raw, map_dir, pack, work, report, logs, bakery)


@pytest.fixture(scope="module")
def mml(tmp_path_factory: pytest.TempPathFactory) -> MmlBuild:
    """The snippet map (treat as read-only)."""
    return build(tmp_path_factory.mktemp("mml-only"))


def _id(n: int) -> str:
    return f"{REC}{n}"


def test_snippet_pack_passes_the_pack_checks(mml: MmlBuild) -> None:
    summary = check_pack(mml.pack, mml.work, mml.config.roads)
    osm_count = mml.json("01-load/report.json")["footprints"]["in_area"]
    assert osm_count == len(OSM_BUILDINGS)
    assert summary.building_count == osm_count + len(ADDED)
    assert mml.report.mml_only_added == len(ADDED)


# AC1 and AC2 in the build


def test_ac1_ac2_ac3_each_record_has_the_outcome_of_the_spec(mml: MmlBuild) -> None:
    buildings = mml.buildings()
    assert {b for b in buildings if b.startswith("mml:")} == {_id(n) for n in ADDED}
    ids = mml.block()["not_added_ids"]
    for reason in NOT_ADDED:
        expected = [_id(n) for n, r in sorted(RECORDS.items()) if r.outcome == reason]
        assert ids[reason] == expected, reason


def test_ac2_cut_footprints_keep_only_the_part_outside_osm(mml: MmlBuild) -> None:
    footprints = mml.footprints()
    for n in sorted(CUT & ADDED):
        kept = footprints[_id(n)]
        assert kept.area == pytest.approx(RECORDS[n].kept_area, abs=0.01), n
        cover = footprints[f"osm:way/{CUT_BY[n]}"]
        assert kept.intersection(cover).area < 0.01, n
    half = footprints[_id(2006)]
    assert half.symmetric_difference(box(-120, 60, -110, 70)).area < 0.01
    for n in sorted(ADDED - CUT):  # whole: the record's own polygon
        assert footprints[_id(n)].symmetric_difference(box(*RECORDS[n].bounds)).area < 1e-6, n


def test_ac2_two_kept_pieces_make_one_building_with_two_collision_meshes(mml: MmlBuild) -> None:
    building = mml.buildings()[_id(2011)]
    assert building["node"] == "bld_mml_rakennus_2011"
    assert building["collision_nodes"] == [
        "bld_mml_rakennus_2011_c0-colonly",
        "bld_mml_rakennus_2011_c1-colonly",
    ]
    footprint = mml.footprints()[_id(2011)]
    assert isinstance(footprint, MultiPolygon)
    assert sorted(p.area for p in footprint.geoms) == pytest.approx([70.0, 90.0], abs=0.01)
    tile = read_tile(mml.pack / "tiles" / f"{building['tile']}.glb")
    names = [n for n in tile.nodes if n.startswith("bld_mml_rakennus_2011")]
    assert sorted(names) == [
        "bld_mml_rakennus_2011",
        "bld_mml_rakennus_2011_c0-colonly",
        "bld_mml_rakennus_2011_c1-colonly",
    ]


# AC3 in the build


def test_ac3_ground_and_noise_points_in_the_build(mml: MmlBuild) -> None:
    (entry,) = mml.block()["lidar_contradicts"]
    assert entry["id"] == _id(2020)
    assert (entry["points_inside"], entry["share_near_ground"]) == (10, 0.9)
    assert entry["max_above_ground_m"] == pytest.approx(ROOF_ABOVE, abs=0.01)
    without = {e["id"]: e for e in mml.block()["without_lidar_height_buildings"]}
    assert (without[_id(2021)]["points_inside"], without[_id(2021)]["share_near_ground"]) == (
        10,
        0.8,
    )
    # The noise point isn't read: 9 ground points, fewer than 10, so 2022 stays.
    assert (without[_id(2022)]["points_inside"], without[_id(2022)]["share_near_ground"]) == (
        9,
        1.0,
    )


# AC4 in the build


def test_ac4_lidar_height_is_measured_on_the_kept_footprint(mml: MmlBuild) -> None:
    buildings = mml.buildings()
    half = buildings[_id(2006)]
    assert half["height_source"] == "mml_lidar"
    # Only the kept half's roof counts, not the higher roof under OSM way 3006.
    assert half["roof_z_median"] == pytest.approx(plane_z(-115, 65) + ROOF_ABOVE, abs=0.002)
    assert half["height_m"] == pytest.approx(half["roof_z_median"] - half["base_z"], abs=0.002)
    whole = buildings[_id(2001)]
    assert whole["height_source"] == "mml_lidar"
    assert whole["roof_z_median"] == pytest.approx(plane_z(-125, 105) + ROOF_ABOVE, abs=0.002)


def test_ac4_buildings_without_roof_points_get_the_default_height(mml: MmlBuild) -> None:
    buildings = mml.buildings()
    for n in sorted(ADDED):
        b = buildings[_id(n)]
        assert b["height_source"] == RECORDS[n].height, n
        if RECORDS[n].height == "default":
            assert b["height_m"] == 6.0, n
            assert b["lidar_rejected"] == "few_points", n


# AC5: place information


def _raw(n: int, **values: Any) -> dict[str, Any]:
    fc = RECORDS[n].feature_class
    return {
        "osm_building": None,
        "osm_poi": [],
        "osm_poi_area": [],
        "mml_feature_class": fc,
        "mml_use_class": USE_CLASS[fc],
        "osm_landuse": None,
        "override": None,
        **values,
    }


RAW_VALUES = {
    2030: _raw(2030, override="public"),
    2031: _raw(2031, osm_poi=["shop=bakery"]),
    2032: _raw(2032, osm_poi_area=["amenity=school"]),
    2034: _raw(2034, osm_landuse="residential"),
    2036: _raw(2036, osm_landuse="industrial"),
}


@pytest.mark.parametrize("n", sorted(ADDED))
def test_ac5_category_follows_the_place_information_order(mml: MmlBuild, n: int) -> None:
    b = mml.buildings()[_id(n)]
    assert (b["category"], b["category_source"]) == RECORDS[n].category
    assert b["category_raw"] == RAW_VALUES.get(n, _raw(n))
    assert b["category_raw"]["osm_building"] is None


def test_ac5_the_40_m2_rule_uses_the_kept_footprint(mml: MmlBuild) -> None:
    footprints = mml.footprints()
    assert footprints[_id(2033)].area == pytest.approx(39.0)
    assert footprints[_id(2034)].area == pytest.approx(41.0)
    assert box(*RECORDS[2035].bounds).area == pytest.approx(60.0)  # the record
    assert footprints[_id(2035)].area == pytest.approx(39.0, abs=0.01)  # kept
    b = mml.buildings()
    assert b[_id(2035)]["category"] == "garage_or_shed"


@pytest.mark.parametrize("n", sorted(ADDED))
def test_ac5_names_address_and_mml_fields(mml: MmlBuild, n: int) -> None:
    b = mml.buildings()[_id(n)]
    names = (b["name"], b["name_sv"], b["ref"], b["name_source"])
    expected = {
        2030: ("Talo", None, None, "override"),
        2031: ("Leipomo", None, None, "osm_poi"),
    }.get(n, (None, None, None, None))
    assert names == expected
    assert b["name_source"] != "osm"
    assert (b["address"], b["address_source"]) == ({"street": None, "housenumber": None}, None)
    assert (b["mml_building"], b["mml_overlap"]) == (_id(n), 1.0)


# AC6: POI links and overrides


def test_ac6_poi_node_inside_links_both_ways(mml: MmlBuild) -> None:
    node = f"osm:node/{mml.bakery}"
    assert mml.buildings()[_id(2031)]["poi_ids"] == [node]
    pois = {p["id"]: p for p in mml.json("pois.json")["pois"]}
    assert pois[node]["building_id"] == _id(2031)


def test_ac6_poi_area_covering_the_building_links_both_ways(mml: MmlBuild) -> None:
    area = f"osm:way/{SCHOOL[0]}"
    assert mml.buildings()[_id(2032)]["area_poi_ids"] == [area]
    pois = {p["id"]: p for p in mml.json("pois.json")["pois"]}
    assert pois[area]["contains_building_ids"] == [_id(2032)]


def test_ac6_mml_keyed_override_applies_and_is_not_unmatched(mml: MmlBuild) -> None:
    b = mml.buildings()[_id(2030)]
    entry = OVERRIDES[_id(2030)]
    assert b["overrides"] == [
        {
            "id": _id(2030),
            "source": entry["source"],
            "licence": entry["licence"],
            "fields": ["category", "name"],
        }
    ]
    overrides = mml.json("03-buildings/report.json")["overrides"]
    unmatched = {u["id"] for u in overrides["unmatched"]}
    assert _id(2030) not in unmatched
    assert unmatched == {_id(2004), _id(99999)}  # 2004 is too small, so never a building
    applied = {a["id"]: a for a in overrides["applied"]}
    assert applied[_id(2030)]["buildings"] == [_id(2030)]


# AC7: the report block


def _counts(values: list[Any], keys: tuple[Any, ...]) -> dict[str, int]:
    counter = Counter(values)
    return {str(k): counter.get(k, 0) for k in keys}


def test_ac7_report_block_counts_are_exact(mml: MmlBuild) -> None:
    block = mml.block()
    added = [RECORDS[n] for n in sorted(ADDED)]
    assert block["source"] == "mml_topographic"
    assert block["limits"] == {
        "min_area_m2": 1.0,
        "sliver_area_m2": 4.0,
        "sliver_opening_radius_m": 0.75,
        "sliver_min_surviving_share": 0.5,
        "contradiction_min_points": 10,
        "contradiction_share": 0.9,
        "contradiction_height_m": 1.5,
    }
    assert block["records_in_area"] == len(RECORDS)  # 2003 overlaps the area too
    assert block["records_matched"] == 1
    assert block["records_considered"] == len(RECORDS) - 1
    assert block["added"] == len(ADDED)
    assert block["added_whole"] == len(ADDED - CUT)
    assert block["added_cut"] == len(ADDED & CUT)
    assert block["not_added"] == {
        reason: sum(1 for r in RECORDS.values() if r.outcome == reason) for reason in NOT_ADDED
    }
    assert block["not_added"] == {
        "outside": 1,
        "too_small": 1,
        "slivers": 3,
        "lidar_contradicts": 1,
    }
    assert block["added_by_feature_class"] == dict(
        sorted(Counter(str(r.feature_class) for r in added).items())
    )
    heights = [r.height for r in added]
    assert block["added_by_height_source"] == _counts(heights, HEIGHT_SOURCES)
    rejected = ["few_points" for r in added if r.height == "default"]
    assert block["added_lidar_rejected"] == _counts(rejected, REJECT_REASONS)
    assert block["added_by_category"] == _counts([r.category[0] for r in added], CATEGORIES)  # type: ignore[index]
    sources = [r.category[1] for r in added]  # type: ignore[index]
    expected_sources = _counts(sources, CATEGORY_SOURCES)
    expected_sources["none"] = sources.count(None)
    assert block["added_by_category_source"] == expected_sources
    assert block["with_lidar_height"] == heights.count("mml_lidar")
    assert block["without_lidar_height"] == heights.count("default")
    assert block["road_overlaps"] == [_id(2040)]


def test_ac7_cut_entries_are_sorted_by_kept_area_with_their_pieces(mml: MmlBuild) -> None:
    cut = mml.block()["cut"]
    assert [e["id"] for e in cut] == [_id(n) for n in (2011, 2006, 2035, 2010, 2008)]
    by_id = {e["id"]: e for e in cut}
    for n in (2011, 2006, 2035, 2010, 2008):
        entry = by_id[_id(n)]
        assert entry["record_area_m2"] == pytest.approx(box(*RECORDS[n].bounds).area, abs=1e-3)
        assert entry["kept_area_m2"] == pytest.approx(RECORDS[n].kept_area, abs=0.01)
        assert entry["osm_buildings"] == [f"osm:way/{CUT_BY[n]}"]
        assert entry["pieces_discarded"] == 0
        assert sum(p["area_m2"] for p in entry["pieces"]) == pytest.approx(
            entry["kept_area_m2"], abs=0.002
        )
    two = by_id[_id(2011)]
    assert two["pieces_kept"] == 2
    assert [p["area_m2"] for p in two["pieces"]] == pytest.approx([90.0, 70.0], abs=0.01)
    # 9 x 10 and 7 x 10 rectangles: 4 pi A / P^2.
    assert [p["compactness"] for p in two["pieces"]] == pytest.approx(
        [4 * math.pi * 90 / 38**2, 4 * math.pi * 70 / 34**2], abs=0.002
    )
    assert [p["opened_share"] for p in two["pieces"]] == pytest.approx(
        [rect_opened_share(9, 10), rect_opened_share(7, 10)], abs=0.002
    )
    half = by_id[_id(2006)]
    assert half["pieces"][0]["compactness"] == pytest.approx(0.785, abs=0.002)
    assert half["pieces"][0]["opened_share"] == pytest.approx(rect_opened_share(10, 10), abs=0.002)
    small = by_id[_id(2008)]
    assert small["pieces"][0]["opened_share"] == pytest.approx(
        rect_opened_share(1.6, 2.5625), abs=0.002
    )
    strip = by_id[_id(2010)]
    assert strip["pieces"][0]["compactness"] == pytest.approx(
        4 * math.pi * 10 / (2 * (1.6 + 6.25)) ** 2, abs=0.002
    )
    assert strip["pieces"][0]["opened_share"] == pytest.approx(
        rect_opened_share(1.6, 6.25), abs=0.002
    )
    for entry in cut:
        assert set(entry["pieces"][0]) == {"area_m2", "compactness", "opened_share"}


def test_ac7_buildings_without_lidar_height_are_listed(mml: MmlBuild) -> None:
    listed = mml.block()["without_lidar_height_buildings"]
    defaults = sorted(_id(n) for n in ADDED if RECORDS[n].height == "default")
    assert sorted(e["id"] for e in listed) == defaults
    footprints = mml.footprints()
    for entry in listed:
        assert entry["kept_area_m2"] == pytest.approx(footprints[entry["id"]].area, abs=0.002)
        assert entry["lidar_rejected"] == "few_points"
    empty = {e["id"]: e for e in listed}[_id(2030)]
    assert (empty["points_inside"], empty["share_near_ground"], empty["max_above_ground_m"]) == (
        0,
        None,
        None,
    )


def test_ac7_existing_counts_include_the_added_buildings(mml: MmlBuild) -> None:
    report = mml.json("03-buildings/report.json")
    total = len(OSM_BUILDINGS) + len(ADDED)
    assert report["building_count"] == total
    assert sum(report["category_counts"].values()) == total
    assert sum(report["height_source_counts"].values()) == total
    assert report["building_use"]["unmatched_records"] == len(RECORDS) - 1  # all but 2002


def test_ac7_buildings_geojson_holds_the_added_buildings(mml: MmlBuild) -> None:
    assert {_id(n) for n in ADDED} <= set(mml.footprints())


# AC8: pack checks on the built pack, and a failing build


def _copy(mml: MmlBuild, tmp_path: Path) -> tuple[Path, Path]:
    pack, work = tmp_path / "pack", tmp_path / "work"
    shutil.copytree(mml.pack, pack)
    shutil.copytree(mml.work, work)
    return pack, work


def _edit_json(path: Path, change: Any) -> None:
    document = json.loads(path.read_text(encoding="utf-8"))
    change(document)
    path.write_text(json.dumps(document, ensure_ascii=False), encoding="utf-8")


@pytest.mark.parametrize(
    ("field", "value", "message"),
    [
        ("mml_building", _id(2001), "isn't the building's own id"),
        ("mml_overlap", 0.9, "mml_overlap isn't 1.0"),
    ],
    ids=["mml_building_other_id", "mml_overlap_0.9"],
)
def test_ac8_pack_check_fails_wrong_mml_fields(
    mml: MmlBuild, tmp_path: Path, field: str, value: Any, message: str
) -> None:
    pack, work = _copy(mml, tmp_path)

    def change(document: dict[str, Any]) -> None:
        (building,) = [b for b in document["buildings"] if b["id"] == _id(2005)]
        building[field] = value

    _edit_json(pack / "buildings.json", change)
    with pytest.raises(PackCheckError, match=message):
        check_pack(pack, work, mml.config.roads)


@pytest.mark.parametrize(
    ("east_m", "fails"), [(0.001, False), (0.5, True)], ids=["0.006_m2", "3_m2"]
)
def test_ac8_pack_check_fails_an_mml_footprint_overlapping_an_osm_one(
    mml: MmlBuild, tmp_path: Path, east_m: float, fails: bool
) -> None:
    """2035's kept footprint ends where OSM way 3035 starts; widen it east (uphill, so the
    base check still passes) over the OSM footprint."""
    pack, work = _copy(mml, tmp_path)
    ox, oy = FIXTURE_ORIGIN
    x0, y0, _, y1 = RECORDS[2035].bounds
    widened = box(x0 + ox, y0 + oy, 26.5 + east_m + ox, y1 + oy)

    def change(document: dict[str, Any]) -> None:
        (feature,) = [f for f in document["features"] if f["properties"]["id"] == _id(2035)]
        feature["geometry"] = shapely.geometry.mapping(widened)

    _edit_json(work / "03-buildings" / "buildings.geojson", change)
    if fails:
        with pytest.raises(PackCheckError, match=f"{_id(2035)} overlaps osm:way/3035"):
            check_pack(pack, work, mml.config.roads)
    else:
        check_pack(pack, work, mml.config.roads)


def _tree(folder: Path) -> dict[str, bytes]:
    return {
        p.relative_to(folder).as_posix(): p.read_bytes() for p in folder.rglob("*") if p.is_file()
    }


def test_ac8_failed_mml_only_check_fails_the_build_and_leaves_the_previous_pack(
    tmp_path: Path, monkeypatch: pytest.MonkeyPatch
) -> None:
    import mapgen.build as build_module

    previous = build(tmp_path)
    before = _tree(previous.pack)
    real = build_module.select_mml_only

    def uncut(*args: Any, **kwargs: Any) -> MmlOnlySelection:
        selection = real(*args, **kwargs)
        selection.records = [
            replace(r, kept=r.record.geometry) if r.outcome == "added" and r.is_cut else r
            for r in selection.records
        ]
        return selection

    monkeypatch.setattr(build_module, "select_mml_only", uncut)
    with pytest.raises(BuildError, match="pack check failed: .* overlaps osm:way/"):
        run_build(
            previous.config,
            "test",
            previous.raw,
            previous.pack,
            previous.work,
            map_dir=previous.map_dir,
            now=lambda: FIXED_NOW,
            log=lambda _: None,
        )
    assert _tree(previous.pack) == before
    assert [p.name for p in previous.pack.parent.iterdir()] == [previous.pack.name]


# AC9: building_use disabled


def test_ac9_without_the_building_use_source_no_mml_building_is_added(tmp_path: Path) -> None:
    result = build(tmp_path, topographic=False)
    assert not [b for b in result.buildings() if not b.startswith("osm:")]
    assert result.report.mml_only_added == 0
    assert any(
        "no enabled source provides building_use: no MML-only buildings are added" in line
        for line in result.logs
    )
    block = result.block()
    assert (block["source"], block["records_considered"], block["added"]) == (None, 0, 0)
    check_pack(result.pack, result.work, result.config.roads)


# AC10: schema


def test_ac10_buildings_json_stays_at_version_2_and_validates(mml: MmlBuild) -> None:
    document = mml.json("buildings.json")
    assert document["schema_version"] == BUILDINGS_SCHEMA_VERSION == 2
    BUILDINGS_SCHEMA.validate_file(mml.pack / "buildings.json")
    jsonschema.validate(document, BUILDINGS_SCHEMA.committed())


# AC11: determinism (the Somero test area: test_places_acceptance.py, ac12)


def test_ac11_two_snippet_builds_write_identical_buildings_and_tiles(
    mml: MmlBuild, tmp_path: Path
) -> None:
    again = build(tmp_path)
    first = (mml.pack / "buildings.json").read_bytes()
    assert first == (again.pack / "buildings.json").read_bytes()
    tiles = sorted(p.name for p in (mml.pack / "tiles").iterdir())
    assert tiles == sorted(p.name for p in (again.pack / "tiles").iterdir())
    for name in tiles:
        assert (mml.pack / "tiles" / name).read_bytes() == (
            again.pack / "tiles" / name
        ).read_bytes(), name
    name = "03-buildings/report.json"
    assert (mml.work / name).read_bytes() == (again.work / name).read_bytes()


# AC12: the Somero test area from the committed fixture

SOMERO_CUT = "mml:rakennus/339002621"
SOMERO_SLIVERS = ["mml:rakennus/339002477", "mml:rakennus/339002489", "mml:rakennus/2227061378"]


def test_ac12_test_area_adds_one_cut_building_and_three_slivers(somero_area: SomeroArea) -> None:
    report = json.loads((somero_area.work / "03-buildings" / "report.json").read_text("utf-8"))
    block = report["mml_only"]
    assert (block["added"], block["added_whole"], block["added_cut"]) == (1, 0, 1)
    assert block["not_added_ids"]["slivers"] == SOMERO_SLIVERS
    # Records crossing the area edge with their centroid outside are `outside`.
    assert {k: v for k, v in block["not_added"].items() if k != "outside"} == {
        "too_small": 0,
        "slivers": 3,
        "lidar_contradicts": 0,
    }
    assert block["not_added"]["outside"] == len(block["not_added_ids"]["outside"])
    assert block["records_considered"] == 1 + sum(block["not_added"].values())
    (cut,) = block["cut"]
    assert cut["id"] == SOMERO_CUT
    assert cut["osm_buildings"] == ["osm:way/464652853"]
    assert round(cut["record_area_m2"], 1) == 144.4  # spec "Data": 144.4 -> 80.1 m²
    assert round(cut["kept_area_m2"], 1) == 80.1
    buildings = json.loads((somero_area.pack / "buildings.json").read_text("utf-8"))["buildings"]
    (building,) = [b for b in buildings if b["id"] == SOMERO_CUT]
    assert building["node"] == "bld_mml_rakennus_339002621"
    assert (building["mml_building"], building["mml_overlap"]) == (SOMERO_CUT, 1.0)
