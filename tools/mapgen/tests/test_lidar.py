"""Lidar building heights (`mapgen.build.lidar`) on synthetic point arrays, no LAZ files.

Criterion numbers refer to the acceptance criteria in docs/specs/building-heights.md.
"""

import math
from typing import Any

import numpy as np
import pytest
import shapely
from shapely.geometry import Polygon, box

from mapgen.build.area import BuildArea
from mapgen.build.buildings import Building, resolve_buildings, resolve_height
from mapgen.build.heightfield import Heightfield
from mapgen.build.lidar import REJECT_REASONS, LidarParams, PointIndex, lidar_height
from mapgen.build.stage1_load import LocalFootprint
from mapgen.geo import BBox
from mapgen.sources.base import PointCloud
from mapgen.sources.mml import LidarOptions

BASE_Z = 100.0
CRS = "EPSG:3067"


def cloud(x: Any, y: Any, z: Any, number_of_returns: Any = 1) -> PointCloud:
    """Non-ground (class 5) points; `number_of_returns` a scalar or per-point array."""
    x, y, z = (np.atleast_1d(np.asarray(a, dtype=np.float64)) for a in (x, y, z))
    n = len(x)
    returns = np.broadcast_to(np.asarray(number_of_returns, dtype=np.uint8), n).copy()
    return PointCloud(
        x, y, z, np.full(n, 5, dtype=np.uint8), np.ones(n, dtype=np.uint8), returns, CRS
    )


def grid_in(polygon: Polygon, spacing: float = 0.5) -> np.ndarray:
    """Grid points strictly inside `polygon`, offset half a cell from its bounds."""
    min_x, min_y, max_x, max_y = polygon.bounds
    gx, gy = np.meshgrid(
        np.arange(min_x + spacing / 2, max_x, spacing),
        np.arange(min_y + spacing / 2, max_y, spacing),
    )
    xy = np.column_stack([gx.ravel(), gy.ravel()])
    return xy[shapely.contains_xy(polygon, xy[:, 0], xy[:, 1])]


def roof(polygon: Polygon, z: float, spacing: float = 0.5) -> PointCloud:
    xy = grid_in(polygon, spacing)
    return cloud(xy[:, 0], xy[:, 1], np.full(len(xy), z))


def height_of(footprint: Polygon, points: PointCloud, **params: Any) -> Any:
    return lidar_height(footprint, PointIndex(points), BASE_Z, LidarParams(**params))


# AC5: buffer ladder


def test_ac5_large_footprint_uses_the_1m_buffer() -> None:
    footprint = box(0, 0, 10, 10)
    result = height_of(footprint, roof(footprint, 107.5))
    assert result.ok and result.rejected is None
    assert result.buffer_m == 1.0
    assert result.height_m == pytest.approx(7.5)
    assert result.points == len(grid_in(footprint.buffer(-1.0)))


def test_ac5_too_few_points_inside_1m_falls_back_to_half_metre() -> None:
    footprint = box(0, 0, 10, 10)
    # 7 points in the band 0.5-1.0 m inside the west wall, 2 in the 1 m core: 1.0 has 2 < 5.
    edge = cloud([0.75] * 7, np.arange(2.0, 9.0), [106.0] * 7)
    core = cloud([5.0, 5.5], [5.0, 5.0], [106.0, 106.0])
    result = height_of(footprint, PointCloud.concatenate([edge, core], CRS))
    assert result.ok
    assert result.buffer_m == 0.5
    assert result.points == 9


def test_ac5_footprint_too_narrow_for_1m_uses_half_metre() -> None:
    footprint = box(0, 0, 1.8, 10)  # a 1 m shrink leaves no polygon
    points = cloud([0.9] * 9, np.arange(1.0, 10.0), [104.0] * 9)
    result = height_of(footprint, points)
    assert (result.buffer_m, result.height_m) == (0.5, pytest.approx(4.0))


def test_ac5_footprint_too_narrow_for_half_metre_uses_no_buffer() -> None:
    footprint = box(0, 0, 0.9, 10)  # both shrinks leave no polygon
    points = cloud([0.45] * 9, np.arange(1.0, 10.0), [103.0] * 9)
    result = height_of(footprint, points)
    assert (result.buffer_m, result.height_m) == (0.0, pytest.approx(3.0))


def test_ac5_buffer_used_is_recorded_in_provenance() -> None:
    narrow = LocalFootprint("osm:way/1", box(0, 0, 0.9, 10))
    wide = LocalFootprint("osm:way/2", box(20, 0, 30, 10))
    points = PointCloud.concatenate(
        [cloud([0.45] * 9, np.arange(1.0, 10.0), [103.0] * 9), roof(wide.geometry, 108.0)], CRS
    )
    result = resolve_buildings([narrow, wide], *_flat_ground(), points=points)
    provenance = {b.id: b.provenance() for b in result.buildings}
    assert provenance["osm:way/1"]["lidar_buffer_m"] == 0.0
    assert provenance["osm:way/2"]["lidar_buffer_m"] == 1.0
    assert result.report()["lidar"]["buffer_counts"] == {"1.0": 1, "0.0": 1}


def test_points_on_the_footprint_edge_are_not_inside() -> None:
    footprint = box(0, 0, 0.9, 10)
    points = cloud([0.0] * 9, np.arange(1.0, 10.0), [103.0] * 9)  # on the west wall
    assert height_of(footprint, points).rejected == "few_points"


# AC6: tree check


def _roof_and_canopy(roof_singles: int, canopy: int) -> PointCloud:
    """Single-return roof points near 106 m and multi-return canopy points at 116-122 m."""
    rng = np.random.default_rng(1)
    rx, ry = rng.uniform(2, 8, roof_singles), rng.uniform(2, 8, roof_singles)
    roof_z = 106.0 + np.linspace(0.0, 0.4, roof_singles)
    cx, cy = rng.uniform(2, 8, canopy), rng.uniform(2, 8, canopy)
    canopy_z = np.linspace(116.0, 122.0, canopy)
    return PointCloud.concatenate([cloud(rx, ry, roof_z, 1), cloud(cx, cy, canopy_z, 2)], CRS)


def test_ac6_canopy_over_roof_uses_only_single_returns() -> None:
    result = height_of(box(0, 0, 10, 10), _roof_and_canopy(roof_singles=20, canopy=20))
    assert result.ok
    assert result.returns == "single"
    assert result.points == 20
    expected = float(np.median(106.0 + np.linspace(0.0, 0.4, 20)))
    assert result.roof_z_median == pytest.approx(expected)
    assert result.height_m == pytest.approx(expected - BASE_Z)


def test_ac6_too_few_single_returns_is_tree_cover() -> None:
    result = height_of(box(0, 0, 10, 10), _roof_and_canopy(roof_singles=4, canopy=30))
    assert not result.ok
    assert result.rejected == "tree_cover"
    assert result.returns == "single" and result.points == 4


def _spread_points(spread: float, number_of_returns: int) -> PointCloud:
    """50 points whose p90 - p10 (numpy linear percentiles) equals `spread`."""
    z = np.linspace(106.0, 106.0 + spread / 0.8, 50)
    rng = np.random.default_rng(2)
    return cloud(rng.uniform(2, 8, 50), rng.uniform(2, 8, 50), z, number_of_returns)


def test_ac6_spread_just_under_the_gate_uses_all_returns() -> None:
    points = _spread_points(7.9, number_of_returns=2)
    p10, p90 = np.percentile(points.z, [10, 90])
    assert p90 - p10 == pytest.approx(7.9)
    result = height_of(box(0, 0, 10, 10), points)
    assert result.ok and result.returns == "all" and result.points == 50


def test_ac6_spread_just_over_the_gate_checks_single_returns() -> None:
    result = height_of(box(0, 0, 10, 10), _spread_points(8.1, number_of_returns=2))
    assert result.rejected == "tree_cover"


def test_ac6_tree_spread_is_configurable() -> None:
    points = _spread_points(7.9, number_of_returns=2)
    assert height_of(box(0, 0, 10, 10), points, tree_spread_m=6.0).rejected == "tree_cover"


# AC7: limits and few points


@pytest.mark.parametrize(
    ("roof_z", "rejected"),
    [
        (BASE_Z + 0.99, "too_low"),
        (BASE_Z - 0.5, "too_low"),  # roof points below the base
        (BASE_Z + 1.0, None),
        (BASE_Z + 40.0, None),
        (BASE_Z + 40.5, "too_high"),
    ],
)
def test_ac7_height_limits(roof_z: float, rejected: str | None) -> None:
    footprint = box(0, 0, 10, 10)
    result = height_of(footprint, roof(footprint, roof_z))
    assert result.rejected == rejected
    assert result.ok is (rejected is None)
    if rejected:
        assert result.height_m is None
        assert result.roof_z_median == pytest.approx(roof_z)  # still reported
    else:
        assert result.height_m == pytest.approx(roof_z - BASE_Z)


def test_ac7_limits_are_configurable() -> None:
    footprint = box(0, 0, 10, 10)
    points = roof(footprint, BASE_Z + 1.5)
    assert height_of(footprint, points, min_height_m=2.0).rejected == "too_low"
    assert height_of(footprint, points, max_height_m=1.2).rejected == "too_high"


def test_ac7_fewer_than_min_points_at_every_buffer_is_few_points() -> None:
    footprint = box(0, 0, 10, 10)
    points = cloud([5, 5.5, 6, 6.5], [5, 5, 5, 5], [106.0] * 4)
    result = height_of(footprint, points)
    assert result.rejected == "few_points"
    assert (result.height_m, result.points, result.buffer_m) == (None, 0, None)
    assert height_of(footprint, points, min_points=4).ok


def test_ac7_points_outside_the_footprint_do_not_count() -> None:
    footprint = box(0, 0, 10, 10)
    outside = roof(box(12, 0, 22, 10), 106.0)
    assert height_of(footprint, outside).rejected == "few_points"


def test_ac7_empty_cloud_is_few_points() -> None:
    result = height_of(box(0, 0, 10, 10), PointCloud.empty(CRS))
    assert result.rejected == "few_points"


def _flat_ground() -> tuple[Heightfield, BuildArea]:
    xs = np.arange(-25, 26) * 2.0
    gx, _ = np.meshgrid(xs, xs)
    hf = Heightfield(z=0 * gx + BASE_Z, x0=-50.0, y0=-50.0, dx=2.0, dy=2.0)
    return hf, BuildArea("test", BBox(-50, -50, 50, 50), 32.0)


def test_ac7_rejected_lidar_falls_back_to_the_next_source() -> None:
    low = LocalFootprint("osm:way/1", box(-40, -40, -30, -30), {"height": "9"})
    high = LocalFootprint("osm:way/2", box(-20, -40, -10, -30), {"building:levels": "4"})
    sparse = LocalFootprint("osm:way/3", box(0, -40, 10, -30))
    trees = LocalFootprint("osm:way/4", box(20, -40, 30, -30), {"height": "5 m"})
    canopy = _roof_and_canopy(roof_singles=4, canopy=30)
    points = PointCloud.concatenate(
        [
            roof(low.geometry, BASE_Z + 0.5),
            roof(high.geometry, BASE_Z + 45.0),
            cloud([5.0, 5.5], [-35.0, -35.0], [106.0, 106.0]),
            canopy.offset(20.0, -40.0),
        ],
        CRS,
    )
    result = resolve_buildings([low, high, sparse, trees], *_flat_ground(), points=points)
    got = {b.id: b.provenance() for b in result.buildings}
    expected = {
        "osm:way/1": (9.0, "osm_height", "too_low"),
        "osm:way/2": (12.0, "osm_levels", "too_high"),
        "osm:way/3": (6.0, "default", "few_points"),
        "osm:way/4": (5.0, "osm_height", "tree_cover"),
    }
    assert {
        k: (p["height_m"], p["height_source"], p["lidar_rejected"]) for k, p in got.items()
    } == expected
    for provenance in got.values():
        assert "roof_z_median" not in provenance and "lidar_points" not in provenance
    report = result.report()
    assert report["lidar"]["buildings_checked"] == 4
    assert report["lidar"]["rejected_counts"] == {reason: 1 for reason in REJECT_REASONS}
    assert report["height_source_counts"]["mml_lidar"] == 0


# AC8: roof elevations


def test_ac8_roof_p90_is_at_least_the_median_and_both_are_stored() -> None:
    footprint = LocalFootprint("osm:way/1", box(0, 0, 10, 10), {"height": "3"})
    xy = grid_in(footprint.geometry.buffer(-1.0))
    z = BASE_Z + 4.0 + 2.0 * (xy[:, 0] - 1.0) / 8.0  # a 2 m slope across the roof
    result = resolve_buildings([footprint], *_flat_ground(), points=cloud(xy[:, 0], xy[:, 1], z))
    (building,) = result.buildings
    lidar = building.lidar
    assert lidar is not None and lidar.ok
    assert lidar.roof_z_median == pytest.approx(float(np.median(z)))
    assert lidar.roof_z_p90 == pytest.approx(float(np.percentile(z, 90)))
    assert lidar.roof_z_p90 > lidar.roof_z_median
    provenance = building.provenance()
    assert provenance["height_source"] == "mml_lidar"
    assert provenance["roof_z_median"] == round(lidar.roof_z_median, 3)
    assert provenance["roof_z_p90"] == round(lidar.roof_z_p90, 3)
    assert provenance["roof_z_p90"] >= provenance["roof_z_median"]
    assert provenance["lidar_points"] == len(xy)
    assert provenance["lidar_returns"] == "all"
    assert "lidar_rejected" not in provenance
    assert provenance["height_m"] == pytest.approx(provenance["roof_z_median"] - BASE_Z, abs=2e-3)


def test_ac8_flat_roof_has_equal_median_and_p90() -> None:
    footprint = box(0, 0, 10, 10)
    result = height_of(footprint, roof(footprint, 105.0))
    assert result.roof_z_p90 == result.roof_z_median == pytest.approx(105.0)


# AC3: height priority


def test_ac3_resolve_height_priority_with_lidar() -> None:
    tags = {"height": "9", "building:levels": "5"}
    assert resolve_height(tags, measured_m=14.0, lidar_m=7.2).source == "mml_lidar"
    assert resolve_height(tags, measured_m=14.0, lidar_m=7.2).height_m == 7.2
    for bad in (None, float("nan"), 0.0, -3.0, float("inf")):
        assert resolve_height(tags, measured_m=14.0, lidar_m=bad).source == "mml_3d"
    assert resolve_height(tags, lidar_m=float("nan")).source == "osm_height"


def test_ac3_each_source_wins_exactly_where_it_should() -> None:
    hf, area = _flat_ground()
    tags_all = {"height": "9", "building:levels": "5"}
    lidar = LocalFootprint("osm:way/1", box(-40, -40, -30, -30), tags_all)
    measured = LocalFootprint("osm:way/2", box(-20, -40, -10, -30), tags_all)
    height = LocalFootprint("osm:way/3", box(0, -40, 10, -30), tags_all)
    levels = LocalFootprint("osm:way/4", box(20, -40, 30, -30), {"building:levels": "5"})
    default = LocalFootprint("osm:way/5", box(-40, 0, -30, 10))
    points = PointCloud.concatenate(
        [
            roof(lidar.geometry, BASE_Z + 7.25),
            roof(measured.geometry, BASE_Z + 0.2),  # rejected: too_low
            roof(height.geometry, BASE_Z + 0.2),
            roof(levels.geometry, BASE_Z + 0.2),
            roof(default.geometry, BASE_Z + 0.2),
        ],
        CRS,
    )
    result = resolve_buildings(
        [lidar, measured, height, levels, default],
        hf,
        area,
        measured_heights={"osm:way/1": 14.0, "osm:way/2": 14.0},
        points=points,
    )
    got = {b.id: (b.height_m, b.height_source) for b in result.buildings}
    assert got == {
        "osm:way/1": (pytest.approx(7.25), "mml_lidar"),
        "osm:way/2": (14.0, "mml_3d"),
        "osm:way/3": (9.0, "osm_height"),
        "osm:way/4": (15.0, "osm_levels"),
        "osm:way/5": (6.0, "default"),
    }
    assert result.report()["height_source_counts"] == {
        "mml_lidar": 1,
        "mml_3d": 1,
        "osm_height": 1,
        "osm_levels": 1,
        "default": 1,
    }


def test_ac3_without_a_point_cloud_no_lidar_is_checked() -> None:
    footprint = LocalFootprint("osm:way/1", box(0, 0, 10, 10))
    result = resolve_buildings([footprint], *_flat_ground())
    (building,) = result.buildings
    assert building.lidar is None
    assert "lidar_rejected" not in building.provenance()
    assert result.report()["lidar"]["buildings_checked"] == 0


# AC11: no invalid heights


@pytest.mark.parametrize("height", [float("nan"), 0.0, -1.0, float("inf")])
def test_ac11_building_refuses_invalid_heights(height: float) -> None:
    with pytest.raises(ValueError, match="height must be finite and positive"):
        Building(
            id="osm:way/1",
            tile="tile_x0_y0",
            node="bld_osm_way_1",
            collision_nodes=("bld_osm_way_1-colonly",),
            footprint=box(0, 0, 1, 1),
            height_m=height,
            height_source="default",
            base_z=0.0,
        )


def test_lidar_height_refuses_a_nan_base() -> None:
    footprint = box(0, 0, 10, 10)
    with pytest.raises(ValueError, match="base_z"):
        lidar_height(footprint, PointIndex(roof(footprint, 105.0)), math.nan)


# Parameters and point clouds


@pytest.mark.parametrize(
    "kwargs",
    [
        {"inner_buffers_m": ()},
        {"inner_buffers_m": (1.0, -0.5)},
        {"inner_buffers_m": (math.nan,)},
        {"inner_buffers_m": (math.inf,)},
        {"min_points": 0},
        {"tree_spread_m": 0.0},
        {"tree_spread_m": -1.0},
        {"min_height_m": 0.0},
        {"min_height_m": 50.0},
        {"max_height_m": 1.0},
    ],
)
def test_lidar_params_refuse_bad_values(kwargs: dict[str, Any]) -> None:
    with pytest.raises(ValueError):
        LidarParams(**kwargs)


@pytest.mark.parametrize(
    "kwargs",
    [
        {"inner_buffers_m": []},
        {"inner_buffers_m": [1.0, -0.5]},
        {"min_points": 0},
        {"tree_spread_m": 0.0},
        {"min_height_m": 0.0},
        {"min_height_m": 50.0},
        {"max_height_m": 1.0},
    ],
)
def test_lidar_options_refuse_bad_values(kwargs: dict[str, Any]) -> None:
    with pytest.raises(ValueError):
        LidarOptions(**kwargs)


def test_lidar_params_defaults_match_the_spec_and_options() -> None:
    params = LidarParams()
    assert params.as_dict() == {
        "inner_buffers_m": [1.0, 0.5, 0.0],
        "min_points": 5,
        "tree_spread_m": 8.0,
        "min_height_m": 1.0,
        "max_height_m": 40.0,
    }
    assert LidarParams.from_options(LidarOptions()) == params
    custom = LidarOptions(inner_buffers_m=[0.5], min_points=3, max_height_m=60.0)
    assert LidarParams.from_options(custom) == LidarParams(
        inner_buffers_m=(0.5,), min_points=3, max_height_m=60.0
    )


def test_point_cloud_arrays_must_match() -> None:
    f, u = np.zeros(3), np.zeros(3, dtype=np.uint8)
    with pytest.raises(ValueError):
        PointCloud(f, np.zeros(2), f, u, u, u, CRS)
    with pytest.raises(ValueError):
        PointCloud(np.zeros((3, 1)), f, f, u, u, u, CRS)
    assert len(PointCloud(f, f, f, u, u, u, CRS)) == 3
    assert len(PointCloud.empty(CRS)) == 0


def test_point_index_finds_every_point_in_a_box() -> None:
    rng = np.random.default_rng(3)
    points = cloud(rng.uniform(-100, 100, 5000), rng.uniform(-50, 80, 5000), np.zeros(5000))
    index = PointIndex(points, cell_m=7.0)
    for min_x, min_y, max_x, max_y in [(-10, -10, 10, 10), (90, 70, 120, 90), (-300, 0, -200, 5)]:
        found = index.cloud.select(index.in_bounds(min_x, min_y, max_x, max_y))
        inside = (
            (points.x >= min_x) & (points.x <= max_x) & (points.y >= min_y) & (points.y <= max_y)
        )
        got = (found.x >= min_x) & (found.x <= max_x) & (found.y >= min_y) & (found.y <= max_y)
        assert int(got.sum()) == int(inside.sum())
    assert len(PointIndex(PointCloud.empty(CRS)).in_bounds(0, 0, 1, 1)) == 0
