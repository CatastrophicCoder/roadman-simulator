"""Roads and the road graph: focused unit tests of the geometry, and a fixture build.

Spec: docs/specs/roads-and-road-graph.md. The acceptance criteria are covered in more
depth elsewhere; these tests pin down the tricky geometry (clipping, spans, draping,
decks) on small synthetic inputs.
"""

import json
import math
from pathlib import Path
from typing import Any

import jsonschema
import numpy as np
import pytest
import shapely
from shapely.geometry import LineString, Point, Polygon

from conftest import SOMERO_YAML, SomeroArea, TmpRepo, mesh_height_at, read_tile
from mapgen.build import run_build
from mapgen.build.area import BuildArea, Tile
from mapgen.build.heightfield import Heightfield
from mapgen.build.roads.bridges import (
    WaterIndex,
    deck_mesh,
    infer_spans,
    set_span_heights,
    tagged_spans,
)
from mapgen.build.roads.graph import Piece, build_graph, clip_segment, clip_way
from mapgen.build.roads.model import committed_schema, generated_schema
from mapgen.build.roads.surface import (
    EDGE_EXTENSION_MARGIN_M,
    MAX_EDGE_EXTENSION_WIDTHS,
    OFF_TERRAIN_WIDTHS,
    OffTerrainError,
    TerrainSurface,
    drape_tile,
    edge_extension,
    ground_runs,
    insert_tile_crossings,
    merged_surfaces,
    off_terrain_limit,
    plane_z,
)
from mapgen.build.roads.tags import (
    name_fields,
    parse_lanes,
    parse_maxspeed,
    parse_oneway,
    resolve_attributes,
    road_class,
)
from mapgen.build.stage1_load import LocalRoadWay, LocalWater
from mapgen.build.terrain import MeshTerrainBuilder
from mapgen.config import ConfigError, MapConfig, RoadsConfig, load_map_config
from mapgen.geo import BBox

SOMERO_FIXTURE = Path(__file__).resolve().parent / "fixtures" / "raw" / "somero-test-area"


def _roads() -> RoadsConfig:
    config = load_map_config(SOMERO_YAML)
    assert config.roads is not None
    return config.roads


# Config


def test_somero_and_testtown_roads_blocks_differ(fixture_config: MapConfig) -> None:
    somero, testtown = _roads(), fixture_config.roads
    assert testtown is not None
    assert somero.surface_offset_m == 0.05 and testtown.surface_offset_m != 0.05
    assert somero.classes["primary"].width_m != testtown.classes["primary"].width_m


@pytest.mark.parametrize(
    ("path", "value"),
    [
        (("unknown",), 1),
        (("surface_offset_m",), 0),
        (("default_speed_kmh",), -5),
        (("bridge", "deck_thickness_m"), 0),
        (("bridge", "min_clearance_m"), -0.1),
        (("bridge", "walk_rim_search_m"), 0),
        (("bridge", "walk_rim_search_m"), 10.0),  # below rim_search_m
        (("bridge", "max_ramp_grade"), 0),
        (("bridge", "max_ramp_grade"), 1.0),
        (("bridge", "max_ramp_grade"), -0.08),
        (("bridge", "inferred_waterways"), "river"),
        (("classes", "primary", "width_m"), 0),
        (("classes", "primary", "lanes"), 0),
        (("classes", "primary", "speed_kmh"), 0),
        (("classes", "primary", "colour"), "red"),
    ],
)
def test_bad_roads_block_is_rejected(
    tmp_repo: TmpRepo, fixture_data: dict[str, Any], path: tuple[str, ...], value: Any
) -> None:
    target = fixture_data["roads"]
    for key in path[:-1]:
        target = target[key]
    target[path[-1]] = value
    with pytest.raises(ConfigError):
        tmp_repo.load(fixture_data)


def test_roads_block_is_optional(tmp_repo: TmpRepo, fixture_data: dict[str, Any]) -> None:
    del fixture_data["roads"]
    assert tmp_repo.load(fixture_data).roads is None


# Tags


def test_class_and_values() -> None:
    roads = _roads()
    assert road_class({"highway": "service"}) == "service"
    assert road_class({"highway": "service", "service": "driveway"}) == "driveway"
    assert road_class({"highway": "service", "service": "parking_aisle"}) == "parking_aisle"
    assert parse_maxspeed("50") == 50 and parse_maxspeed("30 mph") == 48
    assert parse_maxspeed("FI:urban") is None and parse_maxspeed("0") is None
    assert parse_oneway({"oneway": "-1"}) == -1
    assert parse_oneway({"junction": "roundabout"}) == 1
    assert parse_oneway({"junction": "roundabout", "oneway": "no"}) == 0
    for value in ("reversible", "false", "0"):  # anything but "no" keeps it one-way
        assert parse_oneway({"junction": "roundabout", "oneway": value}) == 1, value
        assert parse_oneway({"oneway": value}) == 0, value
    assert parse_oneway({"junction": "roundabout", "oneway": "-1"}) == -1
    for value in ("²", "2;3", "two", "0", ""):
        assert parse_lanes(value) is None, value
    superscript = resolve_attributes({"highway": "primary", "lanes": "²"}, "primary", roads)
    assert superscript.lanes == roads.classes["primary"].lanes
    assert superscript.lanes_source == "default" and superscript.invalid == ("lanes",)
    cycle = resolve_attributes({"highway": "cycleway", "maxspeed": "20"}, "cycleway", roads)
    assert cycle.speed_kmh is None and cycle.speed_source is None
    private = resolve_attributes(
        {"highway": "residential", "motor_vehicle": "private"}, "residential", roads
    )
    assert not private.drivable and private.drivable_source == "osm"
    assert private.walkable and private.walkable_source == "default"
    living = resolve_attributes({"highway": "track", "maxspeed": "none"}, "track", roads)
    assert living.speed_kmh == roads.default_speed_kmh and living.invalid == ("maxspeed",)
    assert name_fields({"name": "  Härkälä ", "ref": ""}) == {
        "name": "Härkälä",
        "name_sv": None,
        "ref": None,
    }


# Clipping


def _way(osm_id: int, xy: list[tuple[float, float]], node_ids: list[int], **tags: str) -> Any:
    return LocalRoadWay(osm_id, tuple(node_ids), np.array(xy, dtype=float), dict(tags))


def test_clip_segment_reports_the_limiting_edges() -> None:
    b = BBox(0, 0, 10, 10)
    assert clip_segment(np.array([-5.0, 5]), np.array([5.0, 5]), b) == (0.5, 1.0, 0, None)
    assert clip_segment(np.array([-5.0, 20]), np.array([5.0, 20]), b) is None


def test_way_leaving_and_reentering_becomes_two_pieces_with_exact_cuts() -> None:
    b = BBox(0, 0, 10, 10)
    way = _way(7, [(2, 2), (2, 13), (7, 13), (7.3, 2)], [1, 2, 3, 4])
    clipped = clip_way(way, b)
    assert clipped.nodes_inside == 2
    assert len(clipped.pieces) == 2
    (xy0, keys0, ids0), (xy1, keys1, ids1) = clipped.pieces
    assert keys0 == ["osm:node/1", "cut:way/7:0"] and ids0 == [1, None]
    assert keys1 == ["cut:way/7:1", "osm:node/4"]
    assert xy0[1][1] == 10.0 and xy1[0][1] == 10.0  # exactly on the edge


def test_way_leaving_through_a_node_on_the_area_edge_ends_at_an_area_edge_node() -> None:
    roads = _roads()
    b = BBox(0, 0, 10, 10)
    leaving = _way(5, [(4, 5), (10, 5), (14, 5)], [1, 2, 3], highway="residential")
    ending = _way(6, [(4, 8), (10, 8)], [4, 5], highway="residential")  # ends on the edge
    pieces = []
    for way in (leaving, ending):
        clipped = clip_way(way, b)
        ((xy, keys, ids),) = clipped.pieces
        piece = _piece(way, roads)
        piece.xy, piece.keys, piece.node_ids = xy, keys, ids
        piece.z = np.zeros(len(xy))
        piece.edge_nodes = clipped.edge_nodes[0]
        pieces.append(piece)
    assert pieces[0].keys == ["osm:node/1", "osm:node/2"] and pieces[0].edge_nodes == {1}
    assert pieces[1].edge_nodes == frozenset()
    graph = build_graph(pieces)
    assert graph.nodes["osm:node/2"].kind == "area_edge"
    assert graph.nodes["osm:node/2"].osm_node == 2
    assert graph.nodes["osm:node/5"].kind == "dead_end"


# Clipping by a custom boundary polygon (docs/specs/world-edge-pipeline.md, slice (b)).

# A 10 x 10 square with a notch x 4..6, n 6..10 cut into its north side (CCW).
NOTCHED = Polygon([(0, 0), (10, 0), (10, 10), (6, 10), (6, 6), (4, 6), (4, 10), (0, 10)])


@pytest.mark.parametrize(
    "xy",
    [
        [(2, 2), (2, 13), (7, 13), (7.3, 2)],  # leaves and re-enters
        [(-3, 5), (5, 5), (14, 5)],  # through, cut twice
        [(4, 5), (10, 5), (14, 5)],  # leaves through a node on the edge
        [(1, 1), (9, 9)],  # inside
        [(-1, -1), (-1, 20)],  # outside
        [(10, -5), (10, 15)],  # along the edge
        [(-5, 10), (12, 10)],  # along the edge, round two corners
        [(5, 5), (5, 5), (5, 15)],  # a zero-length segment, then out
    ],
)
def test_clip_way_by_a_rectangle_polygon_matches_the_box(xy: list[tuple[float, float]]) -> None:
    way = _way(7, xy, list(range(1, len(xy) + 1)))
    by_box = clip_way(way, BBox(0, 0, 10, 10))
    by_polygon = clip_way(way, shapely.box(0, 0, 10, 10))
    assert by_polygon.nodes_inside == by_box.nodes_inside
    assert by_polygon.touches_area == by_box.touches_area
    assert by_polygon.edge_nodes == by_box.edge_nodes
    assert len(by_polygon.pieces) == len(by_box.pieces)
    for (xy_p, keys_p, ids_p), (xy_b, keys_b, ids_b) in zip(
        by_polygon.pieces, by_box.pieces, strict=True
    ):
        assert keys_p == keys_b and ids_p == ids_b
        assert np.allclose(xy_p, xy_b, atol=1e-12)


def test_clip_way_by_a_notched_polygon_cuts_on_the_notch() -> None:
    """A way along n = 8 crosses the notch: two pieces, cut on its sides x = 4 and 6."""
    way = _way(9, [(-2, 8), (12, 8)], [1, 2])
    clipped = clip_way(way, NOTCHED)
    assert clipped.nodes_inside == 0 and clipped.touches_area
    assert [keys for _, keys, _ in clipped.pieces] == [
        ["cut:way/9:0", "cut:way/9:1"],
        ["cut:way/9:2", "cut:way/9:3"],
    ]
    cuts = [p for xy, _, _ in clipped.pieces for p in xy]
    assert np.allclose(cuts, [(0, 8), (4, 8), (6, 8), (10, 8)], atol=1e-12)
    for x, y in cuts:
        assert NOTCHED.exterior.distance(Point(x, y)) <= 1e-12


def test_clip_way_by_a_polygon_cuts_slanted_edges_on_the_ring() -> None:
    """Cut points on a slanted polygon edge lie on that edge (1 nm), not only on the way."""
    triangle = Polygon([(0, 0), (100, 3), (37, 91)])
    way = _way(3, [(-50, 40), (30, 41), (150, 47)], [1, 2, 3])
    clipped = clip_way(way, triangle)
    ((xy, keys, _),) = clipped.pieces
    assert keys == ["cut:way/3:0", "osm:node/2", "cut:way/3:1"]
    for point in (xy[0], xy[-1]):
        assert triangle.exterior.distance(Point(point)) <= 1e-9


def test_clip_way_touching_a_polygon_corner_in_a_point_has_no_piece() -> None:
    corner = _way(5, [(-5, 5), (0, 0), (5, -5)], [1, 2, 3])  # touches (0, 0) only
    clipped = clip_way(corner, NOTCHED)
    assert clipped.pieces == [] and clipped.touches_area


# Surfaces at area-edge cuts (docs/specs/world-edge-pipeline.md: the road surface reaches
# the boundary across its whole width).


def _clipped_pieces(way: LocalRoadWay, b: BBox, roads: RoadsConfig) -> list[Piece]:
    clipped = clip_way(way, b)
    attrs = resolve_attributes(way.tags, road_class(way.tags) or "", roads)
    pieces = []
    for index, (xy, keys, ids) in enumerate(clipped.pieces):
        piece = Piece(way, attrs, index, xy, keys, ids)
        piece.edge_nodes = clipped.edge_nodes[index]
        pieces.append(piece)
    return pieces


def _on_line(surface: Any, line: LineString) -> float:
    """The length of `line` within 1 µm of `surface` (longer by 2 µm / sin θ at a crossing
    angle θ)."""
    return float(line.intersection(surface.buffer(1e-6)).length)


@pytest.mark.parametrize("degrees", [90.0, 45.0, 20.0, 10.0])
def test_an_area_edge_road_reaches_the_boundary_across_its_whole_width(degrees: float) -> None:
    roads = _roads()
    b = BBox(0, 0, 100, 100)
    a = math.radians(degrees)
    # From inside the area across the south edge at (50, 0), `degrees` to the edge.
    inside = (50 + 40 * math.cos(a), 40 * math.sin(a))
    outside = (50 - 20 * math.cos(a), -20 * math.sin(a))
    way = _way(1, [inside, outside], [1, 2], highway="residential")
    (piece,) = _clipped_pieces(way, b, roads)
    assert piece.is_cut(len(piece.xy) - 1)
    runs = ground_runs([piece], set())
    assert runs[0].edge_end and not runs[0].edge_start
    clip = shapely.box(0, 0, 100, 100)
    road, _ = merged_surfaces(runs, clip=clip)
    width = piece.attrs.width_m
    south = LineString([(0, 0), (100, 0)])
    assert _on_line(road, south) == pytest.approx(width / math.sin(a), abs=1e-4)
    assert clip.buffer(1e-9).covers(road)
    # The graph's cut node stays on the boundary.
    assert piece.xy[-1][1] == 0.0
    # Without the clip polygon the end keeps its flat cap at the node: at an angle one
    # side falls short of the boundary.
    unclipped, _ = merged_surfaces(runs)
    if degrees < 90:
        assert _on_line(unclipped.intersection(clip), south) < width / math.sin(a) - 0.1


def test_the_extension_is_capped_at_a_few_road_widths() -> None:
    clip = shapely.box(0, 0, 100, 100)
    half = 2.0
    # At 2° the far side would need half / tan(2°) ≈ 57 m; the cap is 4 widths (16 m).
    a = math.radians(2.0)
    d = np.array([-math.cos(a), -math.sin(a)])
    extension = edge_extension(np.array([50.0, 0.0]), d, half, clip)
    assert extension == pytest.approx(MAX_EDGE_EXTENSION_WIDTHS * 2 * half)
    # At 90° only the margin.
    straight = edge_extension(np.array([50.0, 0.0]), np.array([0.0, -1.0]), half, clip)
    assert straight == pytest.approx(EDGE_EXTENSION_MARGIN_M)


def test_an_area_edge_extension_doesnt_re_enter_the_boundary_beyond_a_notch() -> None:
    """A road leaving through the west wall of a 2 m notch at 20°: neither its extension
    nor its own buffer (whose cap already reaches across the notch) may reappear on the
    far side, and the far side doesn't lengthen the extension."""
    roads = _roads()
    clip = Polygon([(0, 0), (10, 0), (10, 50), (12, 50), (12, 0), (100, 0), (100, 100), (0, 100)])
    a = math.radians(20.0)
    end = np.array([10.0, 21.0])
    start = end - 20 * np.array([math.sin(a), math.cos(a)])
    way = _way(1, [tuple(start), tuple(end)], [1, 2], highway="residential")
    attrs = resolve_attributes(way.tags, "residential", roads)
    piece = Piece(way, attrs, 0, way.xy, ["osm:node/1", "cut:way/1:0"], [1, None])
    half = attrs.width_m / 2
    assert edge_extension(end, end - start, half, clip) == pytest.approx(
        half / math.tan(a) + EDGE_EXTENSION_MARGIN_M
    )
    road, _ = merged_surfaces(ground_runs([piece], set()), clip=clip)
    assert road.intersection(shapely.box(12, 0, 100, 50)).area == 0.0
    wall = LineString([(10, 0), (10, 50)])
    assert _on_line(road, wall) == pytest.approx(attrs.width_m / math.sin(a), abs=1e-4)


# Graph


def _piece(way: LocalRoadWay, roads: RoadsConfig, index: int = 0) -> Piece:
    attrs = resolve_attributes(way.tags, road_class(way.tags) or "", roads)
    keys = [f"osm:node/{n}" for n in way.node_ids]
    piece = Piece(way, attrs, index, way.xy, keys, list(way.node_ids))
    piece.z = np.zeros(len(way.xy))
    return piece


def test_graph_nodes_only_at_shared_nodes_and_reversed_oneway() -> None:
    roads = _roads()
    a = _piece(_way(1, [(0, 0), (10, 0), (20, 0)], [1, 2, 3], highway="residential"), roads)
    b = _piece(_way(2, [(10, 10), (10, 0)], [4, 2], highway="residential", oneway="-1"), roads)
    # Way 3 crosses way 1 without a shared node: it must not connect.
    c = _piece(_way(3, [(15, -5), (15, 5)], [5, 6], highway="cycleway"), roads)
    graph = build_graph([a, b, c])
    assert set(graph.nodes) == {f"osm:node/{n}" for n in (1, 2, 3, 4, 5, 6)}
    assert graph.nodes["osm:node/2"].kind == "junction"
    assert graph.nodes["osm:node/5"].kind == "dead_end"
    (oneway,) = [e for e in graph.edges if e.piece.way_id == 2]
    assert (oneway.from_key, oneway.to_key) == ("osm:node/2", "osm:node/4")
    assert [e.id for e in graph.edges if e.piece.way_id == 1] == ["osm:way/1#0", "osm:way/1#1"]


# Bridges


def test_dual_carriageway_and_ground_joint_make_separate_spans() -> None:
    roads = _roads()
    tags = {"highway": "primary", "bridge": "yes"}
    first = _piece(_way(1, [(0, 0), (10, 0)], [1, 2], **tags), roads)
    second = _piece(_way(2, [(20, 0), (10, 0)], [3, 2], **tags), roads)  # reversed, joined
    other = _piece(_way(3, [(0, 5), (20, 5)], [4, 5], **tags), roads)  # other carriageway
    spans = tagged_spans([first, second, other], "bridge")
    assert sorted(s.way_ids for s in spans) == [[1, 2], [3]]
    chained = next(s for s in spans if len(s.parts) == 2)
    assert {chained.end_keys()[0], chained.end_keys()[1]} == {"osm:node/1", "osm:node/3"}
    ground = _piece(_way(4, [(10, 0), (10, -10)], [2, 6], highway="residential"), roads)
    spans = tagged_spans([first, second, other, ground], "bridge")
    assert sorted(s.way_ids for s in spans) == [[1], [2], [3]]


def _valley_terrain(tmp_size: float = 64.0) -> tuple[BuildArea, list[Tile], TerrainSurface]:
    """Two tiles of a valley along y (lowest at x = 0), 2 m grid."""
    xs = np.arange(-tmp_size, tmp_size + 1, 2.0)
    gx, gy = np.meshgrid(xs, xs)
    z = 100.0 - 8.0 * np.exp(-(gx**2) / 200.0) + 0.01 * gy
    heightfield = Heightfield(z=z, x0=float(xs[0]), y0=float(xs[0]), dx=2.0, dy=2.0)
    area = BuildArea("test", BBox(-60, -60, 60, 60), 64.0)
    tiles = area.tiles()
    builder = MeshTerrainBuilder()
    chunks = {t.id: builder.build_tile(heightfield, t) for t in tiles}
    return area, tiles, TerrainSurface(area, tiles, chunks, max_off_terrain_m=VALLEY_LIMIT_M)


VALLEY_LIMIT_M = 5.0  # off-terrain limit of the valley surfaces


def test_off_terrain_limit_is_twice_the_edge_extension_cap() -> None:
    assert OFF_TERRAIN_WIDTHS == 2 * MAX_EDGE_EXTENSION_WIDTHS
    assert off_terrain_limit(7.0) == 2 * MAX_EDGE_EXTENSION_WIDTHS * 7.0


def test_height_in_a_tile_not_written_is_the_closest_terrain_point_not_its_plane() -> None:
    """Tile (0, 0) (x 0..60, n 0..60) isn't written, as if outside a custom boundary.
    A point 2 m east of the written tile (-1, 0) takes the height at its closest terrain
    point (0, n), not the nearest triangle's plane extended 2 m down the valley side."""
    area, tiles, full = _valley_terrain()
    written = [t for t in tiles if (t.i, t.j) != (0, 0)]
    xs = np.arange(-64.0, 65.0, 2.0)
    gx, gy = np.meshgrid(xs, xs)
    z = 100.0 - 8.0 * np.exp(-(gx**2) / 200.0) + 0.01 * gy
    heightfield = Heightfield(z=z, x0=-64.0, y0=-64.0, dx=2.0, dy=2.0)
    chunks = {t.id: MeshTerrainBuilder().build_tile(heightfield, t) for t in written}
    surface = TerrainSurface(area, written, chunks, max_off_terrain_m=VALLEY_LIMIT_M)
    (h,) = surface.height(np.array([[2.0, 31.0]]))
    (expected,) = full.height(np.array([[0.0, 31.0]]))
    assert h == pytest.approx(expected, abs=1e-12)
    # The nearest triangle's plane, extended to x = 2, is 0.16 m lower: the old answer.
    mesh = surface.meshes[(-1, 0)]
    t = int(mesh.tree.query_nearest(Point(2.0, 31.0))[0])
    extended = plane_z(mesh.triangles[t], np.array([[2.0, 31.0]]))[0]
    assert expected - extended > 0.1
    # Within the limit diagonally too: (3, -3) is 3 m from tile (0, -1) and (-1, 0).
    assert np.isfinite(surface.height(np.array([[3.0, 3.0]]))).all()


@pytest.mark.parametrize("point", [(5.5, 30.0), (30.0, 30.0), (60.0, 60.0)])
def test_height_too_far_from_written_terrain_fails_naming_the_point(
    point: tuple[float, float],
) -> None:
    area, tiles, _ = _valley_terrain()
    written = [t for t in tiles if (t.i, t.j) != (0, 0)]
    xs = np.arange(-64.0, 65.0, 2.0)
    gx, gy = np.meshgrid(xs, xs)
    heightfield = Heightfield(z=100.0 + 0.0 * gx + 0.0 * gy, x0=-64.0, y0=-64.0, dx=2.0, dy=2.0)
    chunks = {t.id: MeshTerrainBuilder().build_tile(heightfield, t) for t in written}
    surface = TerrainSurface(area, written, chunks, max_off_terrain_m=VALLEY_LIMIT_M)
    with pytest.raises(OffTerrainError, match=rf"\({point[0]:.3f}, {point[1]:.3f}\).*limit is 5 m"):
        surface.height(np.array([point]))


def test_height_with_no_written_terrain_at_all_fails() -> None:
    area, tiles, _ = _valley_terrain()
    surface = TerrainSurface(area, [], {}, max_off_terrain_m=VALLEY_LIMIT_M)
    with pytest.raises(OffTerrainError, match="no terrain"):
        surface.height(np.array([[0.0, 0.0]]))


def test_deck_on_a_bend_stays_on_or_just_above_the_straight_line() -> None:
    roads = _roads()
    _, _, terrain = _valley_terrain()
    xy = [(-30, -10), (-5, -3), (5, 3), (30, 25)]
    piece = _piece(_way(9, xy, [1, 2, 3, 4], highway="primary", bridge="yes"), roads)
    (span,) = tagged_spans([piece], "bridge")
    set_span_heights(span, terrain, roads.surface_offset_m)
    assert span.z0 == pytest.approx(terrain.height(np.array([xy[0]]))[0] + 0.05)
    mesh = deck_mesh(span, span.xy, span.s, piece.attrs.width_m, roads.bridge)
    line = LineString(span.xy)
    top = mesh.vertices[mesh.top]
    on_line = np.array([float(span.line_z(line.project(Point(v[:2])))) for v in top])
    diff = top[:, 2] - on_line
    assert diff.min() >= -1e-9 and diff.max() <= 0.05
    # Graph heights are exactly on the line.
    assert np.allclose(piece.z, span.line_z(span.s))


def test_inferred_bridge_over_a_river_and_exclusions() -> None:
    roads = _roads()
    _, _, terrain = _valley_terrain()
    river = LocalWater("osm:way/50", LineString([(0, -60), (0, 60)]), {"waterway": "river"})
    culvert = LocalWater(
        "osm:way/51",
        LineString([(-20, -60), (-20, 60)]),
        {"waterway": "ditch", "tunnel": "culvert"},
    )
    water = WaterIndex.build([], [river, culvert], roads.bridge.inferred_waterways)
    road = _piece(
        _way(8, [(-40, 0), (-10, 0), (10, 0), (40, 0)], [1, 2, 3, 4], highway="tertiary"), roads
    )
    road.z = terrain.height(road.xy) + 0.05
    spans, crossings = infer_spans([road], water)
    assert road.inferred == [(1, 2)]
    (span,) = spans
    assert span.water == ["osm:way/50"] and span.source == "inferred"
    assert [c.reason for c in crossings] == ["culvert", None]
    set_span_heights(span, terrain, 0.05)
    road.decks = [(a, b, "inferred") for a, b in road.inferred]  # as `decks.plan_decks` sets
    graph = build_graph([road])
    bridge_edges = [e for e in graph.edges if e.bridge_source == "inferred"]
    assert len(bridge_edges) == 1 and bridge_edges[0].points[0, 2] == pytest.approx(span.z0)


def test_inferred_bridge_through_a_water_area_spans_nodes_inside() -> None:
    roads = _roads()
    lake = LocalWater("osm:relation/7", Polygon([(-5, -5), (5, -5), (5, 5), (-5, 5)]), {})
    water = WaterIndex.build([lake], [], roads.bridge.inferred_waterways)
    xy = [(-20, 0), (-8, 0), (0, 0), (8, 0), (20, 0)]
    road = _piece(_way(8, xy, [1, 2, 3, 4, 5], highway="tertiary"), roads)
    spans, _ = infer_spans([road], water)
    assert road.inferred == [(1, 3)] and spans[0].water == ["osm:relation/7"]
    ford = _piece(_way(9, xy, [1, 2, 3, 4, 5], highway="tertiary", ford="yes"), roads)
    spans, crossings = infer_spans([ford], water)
    assert spans == [] and {c.reason for c in crossings} == {"ford_way"}


# Draping


def test_plane_z_is_identical_on_shared_axis_aligned_edges() -> None:
    left = np.array([[0.0, 0, 1.0], [2.0, 0, 3.0], [2.0, 2, 7.0]])
    right = np.array([[2.0, 2, 7.0], [2.0, 0, 3.0], [4.0, 0, 5.0]])
    pts = np.array([[2.0, 0.37], [2.0, 1.91]])
    assert np.array_equal(plane_z(left, pts), plane_z(right, pts))
    assert plane_z(left, np.array([[2.0, 2.0]]))[0] == 7.0


def test_draped_surface_is_offset_above_the_terrain_and_matches_at_tile_edges() -> None:
    area, tiles, terrain = _valley_terrain()
    road = LineString([(-50, -37.3), (50, 41.1)]).buffer(4.0)
    road = insert_tile_crossings(road, area)
    meshes = {t.id: drape_tile(road, t, terrain, 0.05) for t in tiles}
    on_edge: dict[str, set[tuple[float, float, float]]] = {}
    for tile in tiles:
        mesh = meshes[tile.id]
        assert mesh is not None
        chunk_tris = terrain.mesh(tile).triangles
        vertices = chunk_tris.reshape(-1, 3)
        faces = np.arange(len(vertices)).reshape(-1, 3)
        h = mesh_height_at(vertices, faces, mesh.vertices[:, :2])
        assert np.abs(mesh.vertices[:, 2] - h - 0.05).max() < 1e-6
        for axis in (0, 1):
            edge = mesh.vertices[mesh.vertices[:, axis] == 0.0]
            on_edge[f"{axis}:{tile.id}"] = {tuple(v) for v in edge}
    assert on_edge["0:tile_x-1_y-1"] == on_edge["0:tile_x0_y-1"]
    assert on_edge["0:tile_x-1_y0"] == on_edge["0:tile_x0_y0"]
    assert on_edge["0:tile_x-1_y0"]  # the road does cross that edge


# Schema


def test_committed_road_graph_schema_matches_the_model() -> None:
    assert committed_schema() == generated_schema(), (
        "regenerate: uv run python -c "
        '"from mapgen.build.roads.model import write_schema; write_schema()"'
    )
    jsonschema.Draft202012Validator.check_schema(committed_schema())


# Builds


def test_map_without_roads_writes_an_empty_road_graph(tmp_path: Path) -> None:
    import yaml

    from conftest import FIXTURE_YAML, write_fixture_raw

    data = yaml.safe_load(FIXTURE_YAML.read_text(encoding="utf-8"))
    del data["roads"]
    repo = TmpRepo(tmp_path)
    config = repo.load(data)
    raw = write_fixture_raw(repo.raw_dir("testtown"))
    logs: list[str] = []
    run_build(
        config,
        "test",
        raw,
        tmp_path / "pack",
        tmp_path / "work",
        map_dir=repo.map_dir("testtown"),
        log=logs.append,
    )
    document = json.loads((tmp_path / "pack" / "road_graph.json").read_text(encoding="utf-8"))
    jsonschema.validate(document, committed_schema())
    assert document["nodes"] == [] and document["edges"] == []
    assert (document["map_id"], document["area"]) == ("testtown", "test")
    assert any("no roads block" in line for line in logs)
    for tile in (tmp_path / "pack" / "tiles").glob("*.glb"):
        names = read_tile(tile).nodes
        assert not {"road-col", "path-col"} & set(names)
        assert not [n for n in names if n.startswith("bridge_osm_way_")]


def test_testtown_graph_uses_its_own_roads_block(fixture_build: Any) -> None:
    document = json.loads((fixture_build.pack_dir / "road_graph.json").read_text("utf-8"))
    jsonschema.validate(document, committed_schema())
    (edge,) = document["edges"]  # the fixture's one residential way
    # testtown's values (tests/fixtures/maps/testtown/map.yaml), not Somero's.
    assert (edge["class"], edge["width_m"], edge["lanes"]) == ("residential", 6.5, 1)
    assert (edge["speed_kmh"], edge["speed_source"]) == (60, "default")


def test_somero_fixture_origin_bridge(somero_area: SomeroArea) -> None:
    pack, work = somero_area.pack, somero_area.work
    graph = json.loads((pack / "road_graph.json").read_text(encoding="utf-8"))
    edges = graph["edges"]
    bridge = [e for e in edges if e["osm_way"] in (88027326, 813370683)]
    cycleway = [e for e in edges if e["osm_way"] == 88027331]
    assert bridge and cycleway
    assert all(e["bridge"] and e["bridge_source"] == "osm" for e in bridge)
    bridge_nodes = {k for e in bridge for k in (e["from"], e["to"])}
    assert not bridge_nodes & {k for e in cycleway for k in (e["from"], e["to"])}
    names = set()
    for tile in json.loads((pack / "manifest.json").read_text(encoding="utf-8"))["tiles"]:
        names |= set(read_tile(pack / tile["file"]).nodes)
    assert {"bridge_osm_way_88027326-col", "bridge_osm_way_813370683-col"} <= names
    assert {"road-col", "path-col"} <= names
    report = json.loads((work / "04-roads" / "report.json").read_text(encoding="utf-8"))
    assert report["suspected_missing_bridges"] == []
    cut = json.loads((SOMERO_FIXTURE / "download-manifest.json").read_text(encoding="utf-8"))
    (osm,) = [e for e in cut["files"] if e["source"] == "osm"]
    highway_ways = osm["request"]["test_area_cut"]["highway_ways"]
    assert report["ways"]["included"] + report["ways"]["skipped"] == highway_ways
    assert (work / "07-road-graph" / "graph.geojson").is_file()


def test_somero_fixture_has_road_ways_without_metadata() -> None:
    text = (SOMERO_FIXTURE / "osm" / "somero.osm").read_text(encoding="utf-8")
    assert '<way id="88027326">' in text and '<way id="88027331">' in text
    assert " user=" not in text and " timestamp=" not in text


def test_jokioistentie_crossing_the_area_edge_is_clipped_not_dropped(
    somero_area: SomeroArea,
) -> None:
    """Way 88027328 has fewer than 2 nodes in the test area but runs through it."""
    from mapgen.build.pack_checks import TriangleSampler, local_points, read_glb_meshes

    pack, work = somero_area.pack, somero_area.work
    graph = json.loads((pack / "road_graph.json").read_text(encoding="utf-8"))
    edges = [e for e in graph["edges"] if e["osm_way"] == 88027328]
    assert edges
    assert any(k.startswith("cut:way/88027328:") for e in edges for k in (e["from"], e["to"]))
    points = np.concatenate([local_points(e["points"]) for e in edges])
    covered = np.zeros(len(points), dtype=bool)
    for tile in json.loads((pack / "manifest.json").read_text(encoding="utf-8"))["tiles"]:
        nodes = read_glb_meshes(pack / tile["file"]).nodes
        if "road-col" in nodes:
            h = TriangleSampler(*nodes["road-col"]).height(points[:, :2], distance=1e-4)
            covered |= ~np.isnan(h)
    assert covered.all()  # road surface along the whole clipped way
    report = json.loads((work / "04-roads" / "report.json").read_text(encoding="utf-8"))
    assert "outside_area" not in report["skipped_reasons"]


def test_somero_fixture_ramps_have_no_step(somero_area: SomeroArea) -> None:
    from mapgen.build.pack_checks import check_pack

    pack, work = somero_area.pack, somero_area.work
    summary = check_pack(pack, work, _roads())
    assert summary.roads is not None
    assert summary.roads.ramp_ends == 4  # both ends of both origin bridge decks
    assert summary.roads.max_step_m <= 0.02


# Ramps


def test_ramp_field_meets_the_deck_end_and_never_goes_below_the_terrain() -> None:
    from mapgen.build.roads.ramps import Ramp, RampField

    ramp = Ramp("n", (0.0, 0.0), (0.0, 1.0), 4.0, 10.0, 5.0)  # deck end at y = 0, road to +y
    field = RampField([ramp])
    edge = np.column_stack([np.linspace(-4, 4, 9), np.zeros(9)])
    assert np.array_equal(field.apply(edge, np.full(9, 9.5)), np.full(9, 10.0))
    # Halfway: halfway between the deck end and the base; L away: the base.
    assert field.apply(np.array([[0.0, 2.5]]), np.array([9.0]))[0] == pytest.approx(9.5)
    assert field.apply(np.array([[0.0, 5.0]]), np.array([9.0]))[0] == 9.0
    # Terrain above the blend: the surface follows the terrain + offset.
    assert field.apply(np.array([[0.0, 2.5]]), np.array([12.0]))[0] == 12.0
    # Continuous: no jump anywhere near the end (beside it, behind it, across corners).
    g = np.linspace(-9, 9, 181)
    gx, gy = np.meshgrid(g, g)
    pts = np.column_stack([gx.ravel(), gy.ravel()])
    z = field.apply(pts, np.full(len(pts), 9.0)).reshape(gx.shape)
    assert np.abs(np.diff(z, axis=0)).max() <= 0.1 * 1.0 / 5.0 + 1e-9
    assert np.abs(np.diff(z, axis=1)).max() <= 0.1 * 1.0 / 5.0 + 1e-9


def test_overlapping_ramps_are_each_exact_at_their_own_end_edge() -> None:
    from mapgen.build.roads.ramps import Ramp, RampField

    a = Ramp("a", (0.0, 0.0), (0.0, 1.0), 4.0, 10.0, 5.0)
    b = Ramp("b", (6.0, 1.0), (0.0, 1.0), 4.0, 11.0, 5.0)
    field = RampField([a, b])
    edge_a = np.array([[-4.0, 0.0], [0.0, 0.0], [1.0, 0.0]])
    edge_b = np.array([[3.0, 1.0], [6.0, 1.0], [10.0, 1.0]])
    assert np.array_equal(field.apply(edge_a, np.full(3, 9.0)), np.full(3, 10.0))
    assert np.array_equal(field.apply(edge_b, np.full(3, 9.0)), np.full(3, 11.0))


# Pack checks and read-back in the build


def test_failed_pack_check_fails_the_build_and_keeps_the_previous_pack(
    tmp_path: Path, monkeypatch: pytest.MonkeyPatch
) -> None:
    import mapgen.build as build
    from conftest import FIXTURE_YAML, write_fixture_raw
    from mapgen.build import BuildError
    from mapgen.build.pack_checks import PackCheckError

    repo = TmpRepo(tmp_path)
    import yaml

    config = repo.load(yaml.safe_load(FIXTURE_YAML.read_text(encoding="utf-8")))
    raw = write_fixture_raw(repo.raw_dir("testtown"))
    pack, work = tmp_path / "pack", tmp_path / "work"
    run_build(config, "test", raw, pack, work, map_dir=repo.map_dir("testtown"), log=lambda _: None)
    before = {p: p.read_bytes() for p in sorted(pack.rglob("*")) if p.is_file()}

    def failing(*_: Any, **__: Any) -> None:
        raise PackCheckError("criterion 9: test")

    monkeypatch.setattr(build, "check_pack", failing)
    with pytest.raises(BuildError, match="pack check failed: criterion 9"):
        run_build(
            config, "test", raw, pack, work, map_dir=repo.map_dir("testtown"), log=lambda _: None
        )
    assert {p: p.read_bytes() for p in sorted(pack.rglob("*")) if p.is_file()} == before
    assert not list(tmp_path.glob("pack.*"))  # no staging folder left behind


def test_glb_read_back_detects_a_mismatch(tmp_path: Path) -> None:
    from mapgen.build.export_glb import ExportError, MeshNode, check_written, export_glb

    vertices = np.array([[0.0, 0, 0], [1, 0, 0], [0, 1, 0]])
    faces = np.array([[0, 1, 2]])
    path = export_glb(tmp_path / "t.glb", [MeshNode("a-col", vertices, faces)])
    moved = vertices + [0, 0, 0.5]
    with pytest.raises(ExportError, match="positions differ"):
        check_written(path, [MeshNode("a-col", moved, faces)])
    with pytest.raises(ExportError, match="indices differ"):
        check_written(path, [MeshNode("a-col", vertices, faces[:, [0, 2, 1]])])
