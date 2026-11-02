"""Unit tests of the world edge (docs/specs/world-edge-pipeline.md), slice (a): config,
markers, the ring, roadblock spans, fences, the void prisms, the spawn check and the MML
water tables. The acceptance tests on whole builds live elsewhere."""

import json
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
    band_samples,
    clip_to_outside,
    footprint_problems,
    point_in_quads,
    quads_overlapping,
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


def test_full_build_with_a_custom_boundary_fails_before_the_pack_is_touched(
    tmp_repo: TmpRepo, fixture_data: dict[str, Any], tmp_path: Path
) -> None:
    from mapgen.build import BuildError, run_build

    fixture_data["world_edge"] = {
        "boundary_wgs84": _latlon([(-500.0, -500.0), (500.0, -500.0), (0.0, 600.0)])
    }
    config = tmp_repo.load(fixture_data)
    with pytest.raises(BuildError, match="custom boundary polygons are not built yet"):
        run_build(
            config,
            "full",
            tmp_path / "raw",
            tmp_path / "pack",
            tmp_path / "work",
            map_dir=tmp_repo.map_dir("testtown"),
            log=lambda _: None,
        )
    assert not (tmp_path / "work").exists()


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
    in_inlet = [Polygon(q) for q in quads if Polygon(q).within(box(100, 30, 110, 100))]
    assert in_inlet, "no prism inside the inlet"
    # Everything in the inlet at least 1 m from its walls and end is covered.
    uncovered = box(101, 31, 109, 100).difference(shapely.union_all(in_inlet))
    assert uncovered.area == pytest.approx(0.0, abs=1e-6)


def test_clip_to_outside_never_passes_an_invalid_quad_through() -> None:
    # The bow-tie a narrow inlet used to produce: its crossing edges enclose two
    # triangles, one of them over the playable area.
    polygon = Polygon(
        [(0, 0), (200, 0), (200, 100), (110, 100), (110, 30), (100, 30), (100, 100), (0, 100)]
    )
    bow_tie = [(109.0, 31.0), (101.0, 31.0), (121.0, 51.0), (89.0, 51.0)]
    assert not Polygon(bow_tie).is_valid
    pieces = clip_to_outside(polygon, [bow_tie])
    assert pieces
    assert footprint_problems(pieces, tolerance=1e-9) == []
    assert quads_overlapping(polygon, pieces) == []
    assert all(len(piece) == 4 for piece in pieces)


def test_clip_to_outside_keeps_a_convex_quad_and_splits_a_concave_one() -> None:
    polygon = box(0, 0, 10, 10)
    outside = [(20.0, 0.0), (30.0, 0.0), (30.0, 10.0), (20.0, 10.0)]
    assert clip_to_outside(polygon, [outside]) == [outside]
    dart = [(20.0, 0.0), (30.0, 5.0), (20.0, 10.0), (25.0, 5.0)]  # reflex at (25, 5)
    pieces = clip_to_outside(polygon, [dart])
    assert len(pieces) == 2
    assert footprint_problems(pieces, tolerance=1e-9) == []
    assert sum(Polygon(p).area for p in pieces) == pytest.approx(Polygon(dart).area)
    triangle = [(20.0, 0.0), (30.0, 0.0), (20.0, 10.0)]
    assert clip_to_outside(polygon, [triangle]) == [[*triangle, triangle[-1]]]


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
