"""Unit tests of the world edge (docs/specs/world-edge-pipeline.md), slice (a): config,
markers, the ring, roadblock spans, fences, the void prisms, the spawn check and the MML
water tables. The acceptance tests on whole builds live elsewhere."""

import json
import re
from pathlib import Path
from typing import Any

import jsonschema
import numpy as np
import pytest
import shapely
from shapely.geometry import LineString, Polygon, box

from conftest import FIXTURE_MARKERS, TmpRepo, write_markers, write_rakennus_gpkg
from mapgen.build.heightfield import Heightfield
from mapgen.build.markers import MARKERS_SCHEMA, MarkersError, parse_markers, read_markers
from mapgen.build.world_edge import fence_record, roadblock_record, yaw_degrees
from mapgen.build.world_edge.fences import FenceSegment, fence_segments, max_deviation
from mapgen.build.world_edge.model import BOUNDARY_SCHEMA
from mapgen.build.world_edge.narrow import narrow_features
from mapgen.build.world_edge.ring import ON_RING_M, Ring
from mapgen.build.world_edge.roadblocks import (
    EdgeNode,
    RoadblockSpan,
    edge_nodes,
    roadblock_spans,
    touched_stretches,
)
from mapgen.build.world_edge.spawn import Obstacle, crossing_buildings, spawn_check
from mapgen.build.world_edge.void import (
    ARC_EXCESS_M,
    band_samples,
    footprint_problems,
    point_in_quads,
    quads_overlapping,
    ring_distances,
    void_quads,
)
from mapgen.build.world_edge.water import (
    WaterCrossing,
    WaterFeature,
    disagreements,
    water_crossings,
)
from mapgen.config import ConfigError, SpawnConfig
from mapgen.sources.mml import GpkgError, read_water

# Schemas


@pytest.mark.parametrize("schema", [BOUNDARY_SCHEMA, MARKERS_SCHEMA], ids=lambda s: s.file)
def test_committed_schema_matches_the_model(schema: Any) -> None:
    assert schema.committed() == schema.generated(), (
        f"regenerate {schema.file} (see the module docstring)"
    )
    jsonschema.Draft202012Validator.check_schema(schema.committed())


# Config


def test_world_edge_block_is_optional_with_the_spec_defaults(
    tmp_repo: TmpRepo, fixture_data: dict[str, Any]
) -> None:
    edge = tmp_repo.load(fixture_data).world_edge
    assert edge.boundary_wgs84 is None
    assert edge.building_clearance_m == 2.0
    assert edge.min_feature_width_m == 3.0
    assert (edge.fence.max_segment_m, edge.fence.max_terrain_deviation_m) == (20.0, 0.25)
    assert (edge.roadblock.clearance_m, edge.roadblock.merge_distance_m) == (1.0, 15.0)
    assert (edge.cliff.ledge_m, edge.cliff.depth_m) == (2.0, 30.0)
    assert (edge.void.inner_offset_m, edge.void.width_m, edge.void.top_margin_m) == (
        1.0,
        20.0,
        20.0,
    )
    assert edge.spawn == SpawnConfig(
        obstacle_buffer_m=2.0,
        min_open_area_m2=500.0,
        min_clear_radius_m=5.0,
        min_boundary_distance_m=10.0,
    )


def test_an_override_changes_only_its_value(
    tmp_repo: TmpRepo, fixture_data: dict[str, Any]
) -> None:
    fixture_data["world_edge"] = {"fence": {"max_terrain_deviation_m": 0.1}}
    edge = tmp_repo.load(fixture_data).world_edge
    assert edge.fence.max_terrain_deviation_m == 0.1
    assert edge.fence.max_segment_m == 20.0
    assert edge.cliff.depth_m == 30.0


@pytest.mark.parametrize("value", [-1.0, "wide"])
def test_min_feature_width_must_be_a_non_negative_number(
    tmp_repo: TmpRepo, fixture_data: dict[str, Any], value: Any
) -> None:
    fixture_data["world_edge"] = {"min_feature_width_m": value}
    with pytest.raises(ConfigError, match="min_feature_width_m"):
        tmp_repo.load(fixture_data)


def test_unknown_world_edge_keys_are_errors(
    tmp_repo: TmpRepo, fixture_data: dict[str, Any]
) -> None:
    fixture_data["world_edge"] = {"cliff": {"height_m": 3}}
    with pytest.raises(ConfigError, match="height_m"):
        tmp_repo.load(fixture_data)


def _latlon(points: list[tuple[float, float]]) -> list[dict[str, float]]:
    from mapgen.geo import map_to_wgs84

    result = []
    for x, y in points:
        lon, lat = map_to_wgs84(x + 309657.0, y + 6726616.0, "EPSG:3067")
        result.append({"lat": lat, "lon": lon})
    return result


@pytest.mark.parametrize(
    ("points", "reason"),
    [
        ([(0, 0), (100, 0)], "at least 3 points"),
        ([(0, 0), (100, 100), (100, 0), (0, 100)], "isn't simple"),
        ([(0, 0), (5000, 0), (0, 100)], "outside the build area"),
        ([(0, 0), (100, 0), (0, 100), (0, 0)], "repeats the first"),
    ],
)
def test_invalid_boundary_polygons_fail_validation(
    tmp_repo: TmpRepo, fixture_data: dict[str, Any], points: Any, reason: str
) -> None:
    fixture_data["world_edge"] = {"boundary_wgs84": _latlon(points)}
    with pytest.raises(ConfigError, match=reason):
        tmp_repo.load(fixture_data)


def test_a_valid_boundary_polygon_in_either_winding_loads(
    tmp_repo: TmpRepo, fixture_data: dict[str, Any]
) -> None:
    ring = [(-500.0, -500.0), (500.0, -500.0), (0.0, 600.0)]
    for points in (ring, ring[::-1]):
        fixture_data["world_edge"] = {"boundary_wgs84": _latlon(points)}
        config = tmp_repo.load(fixture_data)
        local = config.boundary_local()
        assert local is not None and len(local) == 3


def test_boundary_for_uses_the_custom_polygon_in_full_builds_and_the_square_in_test_builds(
    tmp_repo: TmpRepo, fixture_data: dict[str, Any]
) -> None:
    from mapgen.build.area import build_area
    from mapgen.build.world_edge import boundary_for

    # A trapezoid: a triangle's corners would be sharper than 60°, a narrow spike.
    fixture_data["world_edge"] = {
        "boundary_wgs84": _latlon(
            [(-500.0, -500.0), (500.0, -500.0), (300.0, 600.0), (-300.0, 600.0)]
        )
    }
    config = tmp_repo.load(fixture_data)
    full = boundary_for(config, build_area(config, "full"))
    assert full.source == "custom"
    local = config.boundary_local()
    assert local is not None
    assert sorted(map(tuple, full.ring.xy.tolist())) == sorted(local)
    test = boundary_for(config, build_area(config, "test"))
    assert test.source == "test_area"
    assert test.polygon().equals(box(*build_area(config, "test").bounds.as_list()))


def test_boundary_points_are_local_metres_rounded_to_1_um(
    tmp_repo: TmpRepo, fixture_data: dict[str, Any]
) -> None:
    fixture_data["world_edge"] = {
        "boundary_wgs84": _latlon([(-500.123, -500.0), (500.0, -500.0), (0.0, 600.0)])
    }
    local = tmp_repo.load(fixture_data).boundary_local()
    assert local is not None
    for x, y in local:
        assert (x, y) == (round(x, 6), round(y, 6))
    assert local[0] == pytest.approx((-500.123, -500.0), abs=1e-6)


@pytest.mark.parametrize(("side", "outside_m"), [("max_x", 0.03), ("min_y", 0.049)])
def test_boundary_points_up_to_5_cm_outside_snap_onto_the_build_area_edge(
    tmp_repo: TmpRepo, fixture_data: dict[str, Any], side: str, outside_m: float
) -> None:
    """A point meant to lie on the build area's edge, a few mm to cm off it after the
    7-decimal WGS84 rounding, is moved onto the edge (BOUNDARY_SNAP_M = 0.05 m)."""
    from mapgen.config import BOUNDARY_SNAP_M

    area = tmp_repo.load(fixture_data).full_area_local
    assert outside_m <= BOUNDARY_SNAP_M
    if side == "max_x":
        off = (area.max_x + outside_m, 0.0)
    else:
        off = (0.0, area.min_y - outside_m)
    fixture_data["world_edge"] = {"boundary_wgs84": _latlon([off, (-300.0, 300.0), (300.0, 300.0)])}
    local = tmp_repo.load(fixture_data).boundary_local()
    assert local is not None
    if side == "max_x":
        assert local[0][0] == area.max_x and local[0][1] == pytest.approx(0.0, abs=1e-6)
    else:
        assert local[0][1] == area.min_y and local[0][0] == pytest.approx(0.0, abs=1e-6)


def test_a_boundary_point_more_than_5_cm_outside_the_build_area_fails(
    tmp_repo: TmpRepo, fixture_data: dict[str, Any]
) -> None:
    area = tmp_repo.load(fixture_data).full_area_local
    fixture_data["world_edge"] = {
        "boundary_wgs84": _latlon([(area.max_x + 0.06, 0.0), (-300.0, 300.0), (300.0, 300.0)])
    }
    with pytest.raises(ConfigError, match=r"outside the build area .* by more than 0.05 m"):
        tmp_repo.load(fixture_data)


# markers.json


def test_fixture_markers_load(tmp_repo: TmpRepo, fixture_data: dict[str, Any]) -> None:
    config = tmp_repo.load(fixture_data)
    _, polygon = read_markers(FIXTURE_MARKERS, config)
    assert polygon.is_valid and polygon.area == pytest.approx(80 * 35, rel=1e-3)


@pytest.mark.parametrize(
    ("document", "reason"),
    [
        ({"schema_version": 1}, "spawn_area_wgs84"),
        ({"schema_version": 2, "spawn_area_wgs84": []}, "schema_version|markers"),
        ({"schema_version": 1, "spawn_area_wgs84": [], "parking": []}, "parking|markers"),
    ],
)
def test_invalid_markers_documents_fail(document: dict[str, Any], reason: str) -> None:
    with pytest.raises(MarkersError, match=reason):
        parse_markers(json.dumps(document).encode(), "markers.json")


@pytest.mark.parametrize(
    ("ring", "reason"),
    [
        ([(0, 0), (40, 40), (40, 0), (0, 40)], "isn't simple"),
        ([(100, 100), (200, 100), (200, 200), (100, 200)], "outside"),
    ],
)
def test_invalid_spawn_polygons_fail(
    tmp_repo: TmpRepo, fixture_data: dict[str, Any], ring: Any, reason: str
) -> None:
    config = tmp_repo.load(fixture_data)
    path = write_markers(tmp_repo.root / "other", ring)
    with pytest.raises(MarkersError, match=reason):
        read_markers(path, config)


def test_missing_markers_file_fails(tmp_repo: TmpRepo, fixture_data: dict[str, Any]) -> None:
    config = tmp_repo.load(fixture_data)
    with pytest.raises(MarkersError, match="not found"):
        read_markers(tmp_repo.root / "nowhere" / "markers.json", config)


# Ring


def test_ring_is_ccw_from_the_lowest_x_then_lowest_n() -> None:
    ring = Ring.from_coords([(0, 10), (10, 10), (10, 0), (0, 0)])  # clockwise, starts NW
    assert ring.xy.tolist() == [[0, 0], [10, 0], [10, 10], [0, 10]]
    assert ring.length == 40
    assert ring.outward_at(5).tolist() == [0, -1]  # south edge points south
    assert ring.outward_at(0) == pytest.approx([-(0.5**0.5), -(0.5**0.5)])


def test_intervals_join_over_the_ring_start() -> None:
    ring = Ring.rectangle(0, 0, 100, 100)
    (interval,) = ring.intervals(box(-5, -5, 10, 10))
    assert interval == pytest.approx((390.0, 410.0))


def test_intervals_count_an_edge_on_the_ring_within_floating_point_noise() -> None:
    """A surface whose flat cap lies on the ring, half 1 µm outside and half 1 µm inside
    it, covers the whole cap: the edge counts as on the ring (ON_RING_M)."""
    ring = Ring.rectangle(0, 0, 100, 100)
    tilted = Polygon([(40, -1e-6), (50, 1e-6), (50, 30), (40, 30)])
    (interval,) = ring.intervals(tilted)
    # Each end widens by up to ON_RING_M / sin θ (here 90°: ON_RING_M).
    assert interval == pytest.approx((40.0, 50.0), abs=1.01 * ON_RING_M)
    # Without the tolerance only the half where the edge dips below the ring counts.
    (strict,) = ring.intervals(tilted, tolerance=0.0)
    assert strict == pytest.approx((40.0, 45.0), abs=1e-9)
    # A geometry farther from the ring than the tolerance doesn't reach it.
    assert ring.intervals(box(40, 2 * ON_RING_M, 50, 30)) == []


# Roadblocks


def _node(name: str, x: float, n: float, width: float = 4.0, drivable: bool = True) -> EdgeNode:
    return EdgeNode(name, f"e:{name}", x, n, 0.0, drivable, True, "residential", width, "default")


def _road(x: float, width: float = 4.0) -> Any:
    return LineString([(x, -10), (x, 50)]).buffer(width / 2, cap_style="flat")


def test_two_crossings_4_m_apart_become_one_roadblock() -> None:
    ring = Ring.rectangle(0, 0, 200, 100)
    nodes = [_node("a", 50, 0), _node("b", 58, 0)]  # surfaces 4 m apart
    surface = _road(50).union(_road(58))
    (span,) = roadblock_spans(ring, nodes, surface, 1.0, 15.0)
    assert [n.node for n in span.nodes] == ["a", "b"]
    assert (span.a, span.b) == pytest.approx((47.0, 61.0))
    assert span.parts == 2


def test_crossings_20_m_apart_stay_apart() -> None:
    ring = Ring.rectangle(0, 0, 200, 100)
    nodes = [_node("a", 50, 0), _node("b", 74, 0)]  # fence gap 24 - 4 - 2 = 18 m
    spans = roadblock_spans(ring, nodes, _road(50).union(_road(74)), 1.0, 15.0)
    assert len(spans) == 2


def test_a_road_touching_the_boundary_without_crossing_gets_no_roadblock() -> None:
    ring = Ring.rectangle(0, 0, 200, 100)
    touching = LineString([(100, 1), (150, 1)]).buffer(2.0)
    spans = roadblock_spans(ring, [_node("a", 50, 0)], _road(50).union(touching), 1.0, 15.0)
    assert len(spans) == 1
    stretches = ring.intervals(_road(50).union(touching))
    (touched,) = touched_stretches(ring, [_node("a", 50, 0)], stretches)
    assert touched[0] == pytest.approx(100 - 3**0.5, abs=0.01)  # the buffer's polygon arc


def test_a_span_over_the_ring_start_wraps() -> None:
    ring = Ring.rectangle(0, 0, 200, 100)
    # The road runs along the west edge (x -1..3) for n 0..50, so its surface covers
    # s 550..600 there and 0..3 on the south edge: one span over the ring's start.
    (span,) = roadblock_spans(ring, [_node("a", 1, 0)], _road(1), 1.0, 15.0)
    assert (span.a, span.b) == pytest.approx((549.0, 604.0))


# Fences


def _bumpy() -> Heightfield:
    xs = np.arange(-20, 221, 2.0)
    ys = np.arange(-20, 121, 2.0)
    zz = 100 + np.sin(xs[None, :] / 7.0) * 2 + np.cos(ys[:, None] / 5.0)
    return Heightfield(zz, -20.0, -20.0, 2.0, 2.0)


def test_fences_tile_the_free_ring_and_follow_the_terrain() -> None:
    ring = Ring.rectangle(0, 0, 200, 100)
    heightfield = _bumpy()
    covered = [(47.0, 61.0)]
    water = [WaterCrossing(250.0, 280.0, ("mml:jarvi/1",), "mml_topographic")]
    fences = fence_segments(ring, covered, water, heightfield, 20.0, 0.25)
    total = sum(f.length for f in fences) + 14.0
    assert total == pytest.approx(ring.length)
    for fence in fences:
        assert fence.length <= 20.0 + 1e-9
        if fence.type == "land":
            deviation, _ = max_deviation(
                heightfield, ring.point_at(fence.s_start), ring.point_at(fence.s_end)
            )
            assert deviation <= 0.25 + 1e-9
    water_fences = [f for f in fences if f.type == "water"]
    assert sum(f.length for f in water_fences) == pytest.approx(30.0)
    assert all(250 - 1e-9 <= f.s_start and f.s_end <= 280 + 1e-9 for f in water_fences)


def test_a_free_stretch_over_the_ring_start_restarts_at_zero() -> None:
    ring = Ring.rectangle(0, 0, 200, 100)
    fences = fence_segments(ring, [(20.0, 30.0), (550.0, 560.0)], [], _bumpy(), 20.0, 0.25)
    assert fences[0].s_start == 0.0
    assert all(0 <= f.s_start < ring.length for f in fences)
    assert fences[-1].s_end == pytest.approx(ring.length)


def test_max_deviation_matches_dense_sampling() -> None:
    heightfield = _bumpy()
    p0, p1 = (3.3, 7.1), (41.7, 18.9)
    exact, _ = max_deviation(heightfield, p0, p1)
    t = np.linspace(0, 1, 20001)
    x, y = p0[0] + (p1[0] - p0[0]) * t, p0[1] + (p1[1] - p0[1]) * t
    z0, z1 = heightfield.sample(np.array([p0[0], p1[0]]), np.array([p0[1], p1[1]]))
    dense = np.abs(heightfield.sample(x, y) - (z0 + (z1 - z0) * t)).max()
    assert exact == pytest.approx(dense, abs=1e-4)
    assert exact >= dense - 1e-9  # never below what sampling finds


# Void prisms


@pytest.mark.parametrize(
    "coords",
    [
        [(0, 0), (200, 0), (200, 100), (0, 100)],
        [(0, 0), (100, 0), (100, 40), (40, 40), (40, 100), (0, 100)],  # L: one concave corner
        [(0, 0), (100, 0), (50, 30)],  # sharp convex corners
    ],
    ids=["rectangle", "l-shape", "triangle"],
)
def test_void_prisms_cover_the_band_and_stay_outside(coords: Any) -> None:
    ring = Ring.from_coords(coords)
    quads = void_quads(ring, 1.0, 20.0)
    samples = band_samples(ring, 1.0, 20.0, 10_000)
    inside = point_in_quads(quads, samples)
    assert inside.all(), samples[~inside][:3]
    assert quads_overlapping(ring.polygon(), quads) == []
    for quad in quads:
        assert len(quad) == 4


def _random_star(seed: int) -> list[tuple[float, float]]:
    """A simple star-shaped ring with 5-39 points at 20-150 m: many concave corners and
    short segments next to them."""
    rng = np.random.default_rng(seed)
    count = int(rng.integers(5, 40))
    angles = np.sort(rng.uniform(0, 2 * np.pi, count))
    radii = rng.uniform(20, 150, count)
    return [
        (float(r * np.cos(a)), float(r * np.sin(a))) for a, r in zip(angles, radii, strict=True)
    ]


def _random_comb(seed: int) -> list[tuple[float, float]]:
    """A 400 x 100 m rectangle with inlets 1-15 m wide and 5-90 m deep cut into its top
    side, 1-30 m apart."""
    rng = np.random.default_rng(seed)
    points = [(0.0, 0.0), (400.0, 0.0), (400.0, 100.0)]
    x = 400.0
    while True:
        gap, inlet, depth = rng.uniform(1, 30), rng.uniform(1, 15), rng.uniform(5, 90)
        if x - gap - inlet < 5:
            break
        points += [(x - gap, 100.0), (x - gap, 100.0 - depth)]
        points += [(x - gap - inlet, 100.0 - depth), (x - gap - inlet, 100.0)]
        x -= gap + inlet
    return [*points, (0.0, 100.0)]


VOID_FUZZ = [("star", seed) for seed in range(12)] + [("comb", seed) for seed in range(6)]


@pytest.mark.parametrize("kind,seed", VOID_FUZZ, ids=[f"{k}-{s}" for k, s in VOID_FUZZ])
def test_void_prisms_are_convex_cover_the_band_and_stay_outside_on_random_rings(
    kind: str, seed: int
) -> None:
    coords = _random_star(seed) if kind == "star" else _random_comb(seed)
    assert Polygon(coords).is_valid
    ring = Ring.from_coords(coords)
    quads = void_quads(ring, 1.0, 20.0)
    assert footprint_problems(quads, tolerance=1e-9) == []
    samples = band_samples(ring, 1.0, 20.0, 5_000, seed)
    inside = point_in_quads(quads, samples)
    assert inside.all(), samples[~inside][:3]
    assert quads_overlapping(ring.polygon(), quads) == []
    # No prism holds a point closer than 1 m to a segment on its outward side.
    for quad in quads:
        shape = Polygon(quad)
        for i in range(ring.n):
            a, b = ring.segment(i)
            normal = ring.normal(i)
            strip = Polygon([a, b, b + normal, a + normal])
            assert shape.intersection(strip).area <= 1e-6, (quad, i)


def test_void_prisms_fill_a_narrow_inlet_and_stop_short_of_its_walls() -> None:
    # A 10 m inlet 70 m deep, narrower than the band: each wall's 20 m strip reaches
    # across the inlet into the playable area and must be cut 1 m short of the other
    # wall (the old construction made a bow-tie here and passed it through unclipped).
    ring = Ring.from_coords(
        [(0, 0), (200, 0), (200, 100), (110, 100), (110, 30), (100, 30), (100, 100), (0, 100)]
    )
    quads = void_quads(ring, 1.0, 20.0)
    assert footprint_problems(quads, tolerance=1e-9) == []
    # Everything in the inlet at least 1 m from its walls and end is covered (its
    # corners are concave, so no arc slack applies), and no prism comes closer to them.
    uncovered = box(101, 31, 109, 100).difference(shapely.union_all([Polygon(q) for q in quads]))
    assert uncovered.area == pytest.approx(0.0, abs=1e-6)
    assert ring_distances(ring, quads).min() >= 1.0 - 1e-9


def test_a_concave_region_splits_into_convex_pieces_of_the_same_area() -> None:
    from mapgen.build.world_edge.void import _convex_parts

    dart = Polygon([(20.0, 0.0), (30.0, 5.0), (20.0, 10.0), (25.0, 5.0)])  # reflex at (25, 5)
    pieces = _convex_parts(dart)
    assert len(pieces) == 2
    assert footprint_problems(pieces, tolerance=1e-9) == []
    assert sum(Polygon(p).area for p in pieces) == pytest.approx(dart.area)


def test_clipped_regions_split_into_few_convex_quads() -> None:
    from mapgen.build.world_edge.void import _convex_parts

    hexagon = Polygon([(2, 0), (1, 1.7), (-1, 1.7), (-2, 0), (-1, -1.7), (1, -1.7)])
    parts = _convex_parts(hexagon)
    assert len(parts) == 2 and all(len(set(p)) == 4 for p in parts)
    assert sum(Polygon(p).area for p in parts) == pytest.approx(hexagon.area)
    ell = Polygon([(0, 0), (10, 0), (10, 2), (2, 2), (2, 10), (0, 10)])
    parts = _convex_parts(ell)
    assert footprint_problems(parts, tolerance=1e-9) == []
    assert len(parts) == 2  # its 4 triangles merge into two convex quads
    assert shapely.union_all([Polygon(p) for p in parts]).equals(ell)


def test_void_prisms_are_deterministic() -> None:
    ring = Ring.from_coords(_random_comb(3))
    assert void_quads(ring, 1.0, 20.0) == void_quads(ring, 1.0, 20.0)


def test_footprint_problems_names_bow_ties_reflex_quads_and_wrong_lengths() -> None:
    good = [(0.0, 0.0), (1.0, 0.0), (1.0, 1.0), (0.0, 1.0)]
    bow_tie = [(0.0, 0.0), (1.0, 1.0), (1.0, 0.0), (0.0, 1.0)]
    dart = [(0.0, 0.0), (10.0, 5.0), (0.0, 10.0), (5.0, 5.0)]
    triangle = [(0.0, 0.0), (1.0, 0.0), (0.0, 1.0)]
    problems = footprint_problems([good, bow_tie, dart, triangle])
    assert [p.split(" isn't")[0] for p in problems] == [
        "footprint 1",
        "footprint 2",
        "footprint 3",
    ]
    assert "convex" in problems[1]


# Void prisms keep `inner` from the whole ring (ADR 0015 amendment of 2026-11-04)

NOTCH_15 = [(0, 0), (200, 0), (200, 100), (100.75, 100), (100.75, 70), (99.25, 70), (99.25, 100)]
NOTCH_15 = [*NOTCH_15, (0, 100)]  # a 1.5 m wide, 30 m deep notch in the top side
SPIKE_15 = [(0, 0), (200, 0), (200, 100), (100.75, 100), (100.75, 130), (99.25, 130)]
SPIKE_15 = [*SPIKE_15, (99.25, 100), (0, 100)]  # a 1.5 m wide, 30 m long spike
SLIVER = [(0, 0), (200, 0), (200, 100), (60, 100), (60, 99.4), (140, 99.4), (140, 60)]
SLIVER = [*SLIVER, (40, 60), (40, 100), (0, 100)]  # 0.6 m between the notch and the edge


@pytest.mark.parametrize(
    "coords", [NOTCH_15, SPIKE_15, SLIVER], ids=["notch-1.5m", "spike-1.5m", "sliver-0.6m"]
)
def test_void_prisms_stay_inner_offset_from_the_whole_ring_in_narrow_features(
    coords: Any,
) -> None:
    ring = Ring.from_coords(coords)
    assert ring.polygon().is_valid
    quads = void_quads(ring, 1.0, 20.0)
    assert footprint_problems(quads, tolerance=1e-9) == []
    distances = ring_distances(ring, quads)
    assert distances.min() >= 1.0 - 1e-3, (distances.min(), quads[int(distances.argmin())])
    samples = band_samples(ring, 1.0, 20.0, 10_000)
    inside = point_in_quads(quads, samples)
    assert inside.all(), samples[~inside][:3]
    assert quads_overlapping(ring.polygon(), quads) == []


def test_void_prisms_round_a_convex_corner_from_outside_and_cut_a_concave_one() -> None:
    # An L: five convex corners and one concave corner at (40, 40).
    ring = Ring.from_coords([(0, 0), (100, 0), (100, 40), (40, 40), (40, 100), (0, 100)])
    quads = void_quads(ring, 1.0, 20.0)
    assert footprint_problems(quads, tolerance=1e-9) == []
    distances = ring_distances(ring, quads)
    assert distances.min() >= 1.0 - 1e-9  # the arcs are drawn around the circle
    samples = band_samples(ring, 1.0, 20.0, 10_000)
    assert point_in_quads(quads, samples).all()
    # At the convex corner (100, 0), the prisms reach to within ARC_EXCESS_M of the 1 m
    # circle all round the corner: every point at 1 m + ARC_EXCESS_M + 1 µm is covered.
    angles = np.linspace(-np.pi / 2, 0.0, 721)
    radius = 1.0 + ARC_EXCESS_M + 1e-6
    arc = np.column_stack([100 + radius * np.cos(angles), radius * np.sin(angles)])
    assert point_in_quads(quads, arc).all()
    # Near the concave corner nothing comes within 1 m of either segment: the old corner
    # fills reached 0.707 m (the chord), and the strips cut each other's 1 m zone.
    near = [Polygon(q) for q in quads if Polygon(q).distance(shapely.Point(40, 40)) < 5]
    assert near and min(p.distance(ring.line()) for p in near) >= 1.0 - 1e-9


def test_void_prisms_at_right_angle_corners_are_exactly_inner_from_the_ring() -> None:
    # The chord-based corner fill of the old construction reached 0.707 m at a right
    # angle; the polygon around the circle never reaches inside it.
    ring = Ring.rectangle(0, 0, 50, 50)
    quads = void_quads(ring, 1.0, 20.0)
    assert ring_distances(ring, quads).min() == pytest.approx(1.0, abs=1e-9)
    # 4 strips and a fan of quads at each corner, all convex quads (no triangles).
    assert all(len(set(q)) == 4 for q in quads)


# Narrow spikes and notches (world_edge.min_feature_width_m)

NOTCH_2 = [(0, 0), (200, 0), (200, 100), (101, 100), (101, 80), (99, 80), (99, 100), (0, 100)]
SPIKE_2 = [(0, 0), (200, 0), (200, 100), (101, 100), (101, 120), (99, 120), (99, 100), (0, 100)]
NOTCH_4 = [(0, 0), (200, 0), (200, 100), (102, 100), (102, 80), (98, 80), (98, 100), (0, 100)]
L_RING = [(0, 0), (100, 0), (100, 40), (40, 40), (40, 100), (0, 100)]


def test_a_2_m_notch_is_reported_with_its_width_and_place() -> None:
    ring = Ring.from_coords(NOTCH_2)
    [feature] = narrow_features(ring, 3.0)
    assert feature.kind == "notch"
    assert feature.width_m == pytest.approx(2.0, abs=1e-6)
    assert 99 <= feature.point[0] <= 101 and 80 <= feature.point[1] <= 100
    assert feature.angle_deg is None
    s_notch = [ring.project(x, n) for x, n in [(101, 100), (101, 80), (99, 80), (99, 100)]]
    assert feature.s_start <= min(s_notch) + 1e-6 and feature.s_end >= max(s_notch) - 1e-6


def test_a_2_m_spike_is_reported_as_a_spike() -> None:
    ring = Ring.from_coords(SPIKE_2)
    [feature] = narrow_features(ring, 3.0)
    assert feature.kind == "spike"
    assert feature.width_m == pytest.approx(2.0, abs=1e-6)
    assert 99 <= feature.point[0] <= 101 and 100 <= feature.point[1] <= 120


@pytest.mark.parametrize(
    "coords",
    [
        NOTCH_4,
        L_RING,
        [(0, 0), (300, 0), (300, 300), (0, 300)],
        [(0, 0), (100, 0), (100, 50), (100.5, 50), (100.5, 100), (0, 100)],  # a 0.5 m jog
        [(0, 0), (100, 0), (100, 80), (99.3, 80.7), (0, 100)],  # a chamfered corner
        [(0, 0), (100, 0), (100 - 100 / np.tan(np.radians(62)), 100), (0, 100)],  # 62°
    ],
    ids=["notch-4m", "l-ring", "square", "jog", "chamfer", "corner-62deg"],
)
def test_ordinary_shapes_and_wide_notches_have_no_narrow_feature(coords: Any) -> None:
    assert narrow_features(Ring.from_coords(coords), 3.0) == []


def test_a_corner_sharper_than_60_degrees_is_a_spike_or_notch() -> None:
    # A 55° corner of the playable area at (100, 0); the others are 90° and 125°.
    spike = Ring.from_coords([(0, 0), (100, 0), (100 - 50 / np.tan(np.radians(55)), 50), (0, 50)])
    [feature] = narrow_features(spike, 3.0)
    assert (feature.kind, feature.point) == ("spike", (100.0, 0.0))
    assert feature.angle_deg == pytest.approx(55.0, abs=1e-6)
    # A V notch whose outside wedge is 50° at its bottom (50, 78.56...).
    depth = 10 / np.tan(np.radians(25))
    notch = Ring.from_coords(
        [(0, 0), (100, 0), (100, 100), (60, 100), (50, 100 - depth), (40, 100), (0, 100)]
    )
    [feature] = narrow_features(notch, 3.0)
    assert feature.kind == "notch"
    assert feature.angle_deg == pytest.approx(50.0, abs=1e-6)
    assert feature.point == pytest.approx((50.0, 100 - depth))


def test_a_narrow_feature_over_the_ring_start_is_one_feature() -> None:
    # A 2 m wide spike pointing west: its tip holds the ring's start vertex (lowest x,
    # then lowest n), so the gaps across the spike have stretches over s = 0.
    ring = Ring.from_coords(
        [(0, 0), (100, 0), (100, 100), (0, 100), (0, 51), (-20, 51), (-20, 49), (0, 49)]
    )
    assert tuple(ring.xy[0]) == (-20.0, 49.0)
    [feature] = narrow_features(ring, 3.0)
    assert feature.kind == "spike"
    assert feature.width_m == pytest.approx(2.0, abs=1e-6)
    assert feature.s_start < ring.length < feature.s_end  # over the start
    # The whole spike's sides are in the stretch.
    assert feature.s_end - feature.s_start >= 2 * 20 + 2 - 1e-6


def test_min_feature_width_0_turns_the_check_off() -> None:
    assert narrow_features(Ring.from_coords(NOTCH_2), 0.0) == []


def test_boundary_for_fails_on_a_narrow_notch_and_says_where(
    tmp_repo: TmpRepo, fixture_data: dict[str, Any]
) -> None:
    from mapgen.build.area import build_area
    from mapgen.build.world_edge import WorldEdgeError, boundary_for

    notch = [(-500.0, -500.0), (500.0, -500.0), (500.0, 500.0), (1.0, 500.0), (1.0, 480.0)]
    notch += [(-1.0, 480.0), (-1.0, 500.0), (-500.0, 500.0)]  # 2 m wide, 20 m deep
    fixture_data["world_edge"] = {"boundary_wgs84": _latlon(notch)}
    config = tmp_repo.load(fixture_data)
    with pytest.raises(WorldEdgeError) as caught:
        boundary_for(config, build_area(config, "full"))
    message = str(caught.value)
    assert "min_feature_width_m (3 m)" in message
    match = re.search(
        r"notch at x (-?[\d.]+), z (-?[\d.]+) \(Godot local\), lat ([\d.]+), lon ([\d.]+): "
        r"([\d.]+) m wide, ring s ([\d.]+)\.\.([\d.]+)",
        message,
    )
    assert match, message
    x, z = float(match[1]), float(match[2])
    assert -1 <= x <= 1 and -500 <= z <= -480  # Godot z is -north
    assert float(match[5]) == pytest.approx(2.0, abs=1e-3)
    from mapgen.geo import map_to_wgs84

    lon, lat = map_to_wgs84(x + config.origin.x, -z + config.origin.y, config.crs)
    assert (float(match[3]), float(match[4])) == pytest.approx((lat, lon), abs=1e-6)
    # The test build uses the test-area square, which passes.
    assert boundary_for(config, build_area(config, "test")).source == "test_area"


# Spawn check and buildings


def test_spawn_check_measures_open_ground_and_names_overlaps() -> None:
    boundary = box(-150, -150, 150, 150)
    spawn = box(0, 0, 60, 40)
    building = Obstacle("building", "osm:way/1", box(10, 10, 20, 20))
    result = spawn_check(spawn, boundary, [building], SpawnConfig())
    assert result.passed
    grown = 10 * 10 + 4 * 10 * 2 + np.pi * 2**2  # a 10 m square grown by 2 m (round)
    assert result.open_area_m2 == pytest.approx(60 * 40 - grown, rel=1e-3)
    assert result.overlaps[0]["id"] == "osm:way/1"
    blocked = spawn_check(
        spawn, boundary, [Obstacle("road", "road:0", box(-5, -5, 65, 45))], SpawnConfig()
    )
    assert not blocked.passed
    assert "road road:0" in blocked.failure(SpawnConfig())


def test_spawn_check_cuts_the_strip_along_the_boundary() -> None:
    boundary = box(-150, -150, 150, 150)
    spawn = box(120, 120, 150, 150)  # 30 x 30 in the corner: 20 x 20 left after 10 m
    result = spawn_check(spawn, boundary, [], SpawnConfig())
    assert result.open_area_m2 == pytest.approx(400.0)
    assert not result.passed  # under 500 m²


def test_buildings_crossing_or_near_the_boundary() -> None:
    boundary = box(0, 0, 100, 100)
    footprints = [
        ("in", box(10, 10, 20, 20)),
        ("near", box(1, 50, 5, 60)),  # 1 m from the line
        ("over", box(-5, 50, 5, 60)),
    ]
    crossing = crossing_buildings(boundary, footprints, 2.0)
    assert [c["id"] for c in crossing] == ["near", "over"]
    assert crossing[1]["area_outside_m2"] == pytest.approx(50.0)


# Water


def test_water_crossings_join_touching_areas() -> None:
    ring = Ring.rectangle(0, 0, 200, 100)
    lake = WaterFeature("mml:jarvi/1", box(-10, 20, 30, 60))
    river = WaterFeature("mml:virtavesialue/2", box(-10, 60, 5, 70))
    (crossing,) = water_crossings(ring, [lake, river], "mml_topographic")
    assert crossing.length == pytest.approx(50.0)
    assert crossing.water_ids == ("mml:jarvi/1", "mml:virtavesialue/2")


def test_read_water_reads_the_three_tables(tmp_path: Path) -> None:
    path = write_rakennus_gpkg(
        tmp_path / "t.gpkg",
        [],
        water={
            "jarvi": [(7, Polygon([(0, 0), (10, 0), (10, 10)]))],
            "virtavesikapea": [(9, LineString([(0, 0), (5, 5)]))],
        },
    )
    areas, lines, skipped = read_water(path)
    assert [a.id for a in areas] == ["mml:jarvi/7"]
    assert [line.id for line in lines] == ["mml:virtavesikapea/9"]
    assert skipped == []


def test_read_water_names_a_missing_table(tmp_path: Path) -> None:
    path = write_rakennus_gpkg(tmp_path / "t.gpkg", [], water={"virtavesialue": None})
    with pytest.raises(GpkgError, match="virtavesialue"):
        read_water(path)


# boundary.json records


def test_yaw_rounds_before_it_is_normalised() -> None:
    """A tangent a hair south of due west is -179.99999994° unrounded: it must come out
    as 180.0, never as -180.0 (the schema's lower bound is exclusive)."""
    assert yaw_degrees((-1.0, -1e-9)) == 180.0
    assert yaw_degrees((-1.0, 1e-9)) == 180.0
    assert yaw_degrees((-1.0, -0.5)) == pytest.approx(-153.434949, abs=1e-6)
    # Through a roadblock on the top edge of a ring whose top runs west, dipping south.
    ring = Ring.from_coords([(0, 0), (100, 0), (100, 100), (0, 100 - 1e-7)])
    span = RoadblockSpan(245.0, 255.0, [_node("a", 50, 100)])
    record = roadblock_record(ring, span, _bumpy(), 0.05, [])
    assert record["yaw_deg"] == 180.0


def test_s_start_that_rounds_to_the_ring_length_restarts_at_zero() -> None:
    ring = Ring.rectangle(0, 0, 200, 100)
    a = ring.length - 3e-7  # rounds to 600.0 at 1 µm
    span = RoadblockSpan(a, a + 10.0, [_node("a", 5, 0)])
    record = roadblock_record(ring, span, _bumpy(), 0.05, [])
    assert 0.0 <= record["s_start"] < ring.length
    assert record["s_start"] == 0.0
    assert record["s_end"] - record["s_start"] == pytest.approx(10.0, abs=1e-6)
    fence = fence_record(ring, 0, FenceSegment(a, a + 5.0, "land"), _bumpy())
    assert fence["s_start"] == 0.0
    assert fence["s_end"] == pytest.approx(5.0, abs=1e-6)
    # A value that doesn't round up to the length is kept.
    kept = fence_record(ring, 0, FenceSegment(599.5, 604.5, "land"), _bumpy())
    assert (kept["s_start"], kept["s_end"]) == (599.5, 604.5)


def _deck_node(name: str, x: float, n: float, y: float, bridge: bool) -> EdgeNode:
    return EdgeNode(name, f"e:{name}", x, n, y, True, True, "residential", 4.0, "default", bridge)


def test_a_roadblock_touching_water_stays_on_the_road_surface_unless_on_a_deck() -> None:
    """A ground road whose span only reaches water through its clearance is over_water
    but keeps terrain + offset; only a node on a bridge deck edge puts it at deck height."""
    ring = Ring.rectangle(0, 0, 200, 100)
    heightfield = _bumpy()
    offset = 0.05
    water = [WaterCrossing(60.5, 80.0, ("mml:jarvi/1",), "mml_topographic")]
    span = RoadblockSpan(47.0, 61.0, [_deck_node("a", 54, 0, 150.0, bridge=False)])
    record = roadblock_record(ring, span, heightfield, offset, water)
    assert record["over_water"] is True
    for key, s in (("start", 47.0), ("end", 61.0), ("position", 54.0)):
        x, n = ring.point_at(s)
        assert record[key][1] == pytest.approx(float(heightfield.sample(x, n)) + offset)
    deck = RoadblockSpan(
        47.0,
        61.0,
        [_deck_node("a", 52, 0, 150.0, bridge=True), _deck_node("b", 56, 0, 149.0, False)],
    )
    record = roadblock_record(ring, deck, heightfield, offset, water)
    assert [record[k][1] for k in ("start", "end", "position")] == [150.0] * 3
    assert record["over_water"] is True
    dry = roadblock_record(ring, deck, heightfield, offset, [])
    assert dry["over_water"] is False


def test_edge_nodes_carry_the_bridge_flag() -> None:
    graph = {
        "nodes": [
            {"id": "cut:way/1:0", "kind": "area_edge", "position": [1.0, 2.0, -3.0]},
            {"id": "cut:way/2:0", "kind": "area_edge", "position": [4.0, 5.0, -6.0]},
            {"id": "osm:node/9", "kind": "junction", "position": [0.0, 0.0, 0.0]},
        ],
        "edges": [
            {
                "id": f"osm:way/{k}#0",
                "from": f"cut:way/{k}:0",
                "to": "osm:node/9",
                "drivable": True,
                "walkable": True,
                "class": "residential",
                "width_m": 5.5,
                "width_source": "class_default",
                "bridge": k == 2,
            }
            for k in (1, 2)
        ],
    }
    assert [(n.node, n.bridge) for n in edge_nodes(graph)] == [
        ("cut:way/1:0", False),
        ("cut:way/2:0", True),
    ]


def test_disagreements_measure_ends_around_the_ring_start() -> None:
    ring = Ring.rectangle(0, 0, 200, 100)  # length 600
    used = [WaterCrossing(599.8, 610.0, ("mml:jarvi/1",), "mml_topographic")]
    close = [WaterCrossing(0.1, 10.2, ("osm:way/5",), "osm")]
    assert disagreements(ring, used, close) == []
    far = [WaterCrossing(1.5, 10.0, ("osm:way/5",), "osm")]
    (item,) = disagreements(ring, used, far)
    assert item["kind"] == "ends_differ"
    assert item["start_difference_m"] == pytest.approx(1.7)
    assert item["end_difference_m"] == pytest.approx(0.0, abs=1e-9)


def test_roads_geojson_bridge_features_are_the_deck_footprint() -> None:
    """Stage 10 takes `deck_footprint(span)` as a deck's surface and the pack check reads
    the bridge features of roads.geojson: both must be the same shape."""
    from types import SimpleNamespace

    from mapgen.build.roads import bridge_features
    from mapgen.build.roads.bridges import Span, SpanPart, deck_footprint

    def piece(way: int, width: float) -> Any:
        attrs = SimpleNamespace(width_m=width, road_class="secondary", layer=1)
        return SimpleNamespace(way_id=way, attrs=attrs)

    span = Span(
        "bridge",
        "osm",
        [SpanPart(piece(7, 6.0), 0, 1, True), SpanPart(piece(8, 8.0), 0, 1, True)],
    )
    span.xy = np.array([[0.0, 0.0], [20.0, 5.0], [40.0, 5.0]])
    ((geometry, props),) = bridge_features([span])
    assert geometry.equals_exact(deck_footprint(span), 0.0)
    assert props["surface"] == "bridge"
    assert props["osm_ways"] == ["osm:way/7", "osm:way/8"]
