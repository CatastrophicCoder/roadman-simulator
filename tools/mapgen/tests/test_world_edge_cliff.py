"""World edge cliff geometry on synthetic rings (`build/world_edge/cliff.py`).

The acceptance criteria on whole builds (criterion 14) are in
test_world_edge_acceptance.py (`test_ac14_*`). Here: corners (mitre, clipped mitre,
concave trim), short segments and narrow inlets, height steps between pieces, the skirt down to
the terrain's edge, tile assignment and the bottom cap, on rings small enough to reason
about by hand.
"""

import math
from collections.abc import Sequence

import numpy as np
import pytest
import shapely
from shapely.geometry import LineString, Point, Polygon

from mapgen.build.terrain.base import TerrainChunk
from mapgen.build.world_edge.cliff import (
    BOTTOM_NODE,
    CLIFF_MATERIAL,
    CLIFF_NODE,
    MITRE_LIMIT,
    Cliff,
    CliffError,
    Profile,
    RingItem,
    Tiling,
    build_cliff,
    nearest_tile,
    ring_profile,
    ring_tile_ids,
    stretch_tile,
    wall,
)
from mapgen.build.world_edge.ring import Ring
from mapgen.geo import BBox

LEDGE = 2.0
BOTTOM = -30.0
SQUARE = [(0.0, 0.0), (100.0, 0.0), (100.0, 100.0), (0.0, 100.0)]
NOTCH = [(0, 0), (100, 0), (100, 40), (60, 40), (60, 60), (100, 60), (100, 100), (0, 100)]
SPIKE = [(0, 0), (100, 0), (100, 100), (95, 30), (0, 100)]  # a 4.1° sliver at (100, 100)
STEP = [(0, 0), (50, 0), (50, -0.5), (100, -0.5), (100, 100), (0, 100)]  # a 0.5 m step
INLET = [(0, 0), (100, 0), (100, 100), (51.5, 100), (51.5, 40), (48.5, 40), (48.5, 100), (0, 100)]
# An inlet 1.5 m wide, narrower than the 2 m ledge: one wall's quad spans the whole inlet.
NARROW = [
    (0, 0),
    (100, 0),
    (100, 100),
    (50.75, 100),
    (50.75, 40),
    (49.25, 40),
    (49.25, 100),
    (0, 100),
]


def flat_profile(ring: Ring, z: float = 0.0) -> Profile:
    s = np.array([-ring.length, 0.0, ring.length, 2 * ring.length])
    return Profile(s, np.full(len(s), z), ring.length)


def items_every(ring: Ring, spacing: float, height: float = 0.0) -> list[RingItem]:
    """Fences cut at every vertex and every `spacing` metres, all at `height`."""
    cuts = sorted(
        {*(float(v) for v in ring.vertex_s), *np.arange(0.0, ring.length, spacing).tolist()}
    )
    ends = [*cuts[1:], ring.length]
    return [
        RingItem(f"fence:{k}", a, b, height, height)
        for k, (a, b) in enumerate(zip(cuts, ends, strict=True))
    ]


def one_tile(coords: Sequence[tuple[float, float]], size: float = 1000.0) -> Tiling:
    xs, ys = zip(*coords, strict=True)
    bounds = BBox(min(xs), min(ys), max(xs), max(ys))
    return Tiling(size, {"tile_x0_y0": bounds}, frozenset({"tile_x0_y0"}))


def build(
    coords: Sequence[tuple[float, float]],
    items: Sequence[RingItem] | None = None,
    profile: Profile | None = None,
    tiling: Tiling | None = None,
) -> tuple[Ring, Cliff]:
    ring = Ring.from_coords(coords)
    cliff = build_cliff(
        ring,
        items if items is not None else items_every(ring, 10.0),
        LEDGE,
        BOTTOM,
        profile if profile is not None else flat_profile(ring),
        tiling or one_tile(coords),
    )
    return ring, cliff


def triangles(cliff: Cliff, name: str = CLIFF_NODE) -> tuple[np.ndarray, np.ndarray]:
    """All triangles (m, 3, 3) and vertex normals (m, 3, 3) of a node, over every tile."""
    tris, normals = [], []
    for nodes in cliff.nodes.values():
        for node in nodes:
            if node.name == name:
                tris.append(node.vertices[node.faces])
                normals.append(node.normals[node.faces])
    return np.concatenate(tris), np.concatenate(normals)


def ledge_of(cliff: Cliff) -> np.ndarray:
    tris, normals = triangles(cliff)
    return tris[normals[:, 0, 2] > 0.5]


def union_2d(tris: np.ndarray) -> shapely.Geometry:
    return shapely.union_all(shapely.polygons(tris[:, :, :2]))


def slab_outline(ring: Ring, cliff: Cliff) -> shapely.Geometry:
    """The outline of the playable area plus the ledge: the union's exterior and its real
    holes (the ledge is snap-rounded to 1e-9 m, so the union with the exact polygon has
    sliver holes along slanted edges)."""
    slab = shapely.union_all([ring.polygon(), union_2d(ledge_of(cliff))])
    rings = [slab.exterior, *(r for r in slab.interiors if Polygon(r).area > 1e-6)]
    return shapely.union_all(rings)


def test_node_names_material_and_flat_shading() -> None:
    _, cliff = build(SQUARE)
    (tile,) = cliff.nodes
    names = [node.name for node in cliff.nodes[tile]]
    assert names == [CLIFF_NODE, BOTTOM_NODE]
    for node in cliff.nodes[tile]:
        assert node.material == CLIFF_MATERIAL and node.uv is None
        assert np.array_equal(node.faces.ravel(), np.arange(len(node.vertices)))
        tri = node.vertices[node.faces]
        geometric = np.cross(tri[:, 1] - tri[:, 0], tri[:, 2] - tri[:, 0])
        geometric /= np.linalg.norm(geometric, axis=1, keepdims=True)
        normals = node.normals[node.faces]
        assert np.allclose(normals, normals[:, :1])  # one normal per face
        assert np.allclose(normals[:, 0], geometric, atol=1e-9)


def test_a_square_gets_mitred_corners_and_a_ledge_of_exactly_ledge_m() -> None:
    ring, cliff = build(SQUARE)
    ledge = union_2d(ledge_of(cliff))
    expected = shapely.box(-LEDGE, -LEDGE, 100 + LEDGE, 100 + LEDGE).difference(ring.polygon())
    assert ledge.symmetric_difference(expected).area < 1e-9
    assert cliff.report["corners"] == {"mitre": 4}


def test_the_ledge_never_overlaps_itself_or_reaches_over_the_playable_area() -> None:
    for coords in (NOTCH, SPIKE, STEP, INLET, NARROW):
        ring, cliff = build(coords)
        tris = ledge_of(cliff)
        union = union_2d(tris)
        total = float(shapely.area(shapely.polygons(tris[:, :, :2])).sum())
        assert total == pytest.approx(union.area, abs=1e-6), coords
        assert union.intersection(ring.polygon()).area < 1e-6, coords


def test_the_ledge_covers_the_band_within_ledge_m_of_the_ring() -> None:
    """Every point outside the ring and closer to it than ledge_m is on the ledge, also
    at concave corners, beside a 0.5 m step and in 3 m and 1.5 m inlets."""
    rng = np.random.default_rng(1)
    for coords in (NOTCH, SPIKE, STEP, INLET, NARROW):
        ring, cliff = build(coords)
        ledge = union_2d(ledge_of(cliff)).buffer(1e-9)
        points = shapely.points(rng.uniform(-5, 105, (20_000, 2)))
        distance = shapely.distance(points, ring.line())
        band = (distance < LEDGE - 1e-6) & ~shapely.contains(ring.polygon(), points)
        assert band.sum() > 100
        assert shapely.covers(ledge, points[band]).all(), coords


def test_a_concave_corner_is_trimmed_along_its_bisector() -> None:
    """At the notch's concave corner (60, 60) the quads of its two edges overlap; they are
    split along the bisector, from the corner to the inner mitre point (62, 58)."""
    _, cliff = build(NOTCH)
    tris = ledge_of(cliff)
    vertices = tris.reshape(-1, 3)[:, :2]
    corner, tip = np.array([60.0, 60.0]), np.array([60.0 + LEDGE, 60.0 - LEDGE])
    assert np.hypot(*(vertices - tip).T).min() < 1e-9
    # Every ledge triangle touching the bisector lies on one side of it.
    bisector = LineString([corner, tip])
    for tri in tris:
        assert not shapely.relate_pattern(Polygon(tri[:, :2]), bisector, "T********")
    assert cliff.report["corners"] == {"concave": 2, "mitre": 6}


def test_a_sharp_corner_gets_a_mitre_clipped_at_the_mitre_limit() -> None:
    """The spike's tip (100, 100) turns by 176° and its corner (0, 100) by 126°: both
    mitres would reach more than MITRE_LIMIT ledge widths, so they are clipped there."""
    ring, cliff = build(SPIKE)
    assert cliff.report["corners"]["clipped_mitre"] == 2
    vertices = ledge_of(cliff).reshape(-1, 3)[:, :2]
    reach = shapely.distance(shapely.points(vertices), ring.line()).max()
    assert reach <= math.hypot(LEDGE, LEDGE * MITRE_LIMIT) + 1e-9
    # The clip line lies MITRE_LIMIT ledge widths from the tip along the bisector.
    tip = np.array([100.0, 100.0])
    n0 = ring.normal(1)
    n1 = ring.normal(2)
    m = (n0 + n1) / np.hypot(*(n0 + n1))
    along = (vertices - tip) @ m
    assert along.max() == pytest.approx(LEDGE * MITRE_LIMIT)


def test_the_face_runs_down_to_bottom_y_along_the_whole_outline() -> None:
    for coords in (SQUARE, NOTCH, SPIKE, STEP, INLET):
        ring, cliff = build(coords)
        tris, normals = triangles(cliff)
        assert tris[:, :, 2].min() == pytest.approx(BOTTOM)
        vertical = np.abs(normals[:, 0, 2]) < 1e-9
        bottom_edges = [
            LineString([t[a, :2], t[b, :2]])
            for t in tris[vertical]
            for a, b in ((0, 1), (1, 2), (2, 0))
            if abs(t[a, 2] - BOTTOM) < 1e-9 and abs(t[b, 2] - BOTTOM) < 1e-9
        ]
        outline = slab_outline(ring, cliff)
        edges = shapely.union_all(bottom_edges)
        assert edges.length == pytest.approx(outline.length, abs=1e-6), coords
        assert outline.buffer(1e-6).covers(edges), coords


def test_faces_point_out_of_the_slab() -> None:
    ring, cliff = build(NOTCH)
    tris, normals = triangles(cliff)
    slab = shapely.union_all([ring.polygon(), union_2d(ledge_of(cliff))])
    faces = (np.abs(normals[:, 0, 2]) < 1e-9) & (tris[:, :, 2].min(axis=1) <= BOTTOM + 1e-9)
    for tri, normal in zip(tris[faces], normals[faces, 0], strict=True):
        centre = tri[:, :2].mean(axis=0)
        assert not slab.contains(Point(centre + normal[:2] * 1e-3))
        assert slab.buffer(1e-6).contains(Point(centre - normal[:2] * 1e-3))


def test_a_step_between_a_fence_and_a_roadblock_is_closed_by_a_seam() -> None:
    """A roadblock 0.05 m above the fences on the south edge: seams close the step at
    both of its ends, facing the lower fence side, and the ledge inner edge is at the
    roadblock's height along its span."""
    ring = Ring.from_coords(SQUARE)
    items = [
        RingItem("fence:0", 0.0, 40.0, 0.0, 0.0),
        RingItem("roadblock:a", 40.0, 60.0, 0.05, 0.05),
        RingItem("fence:1", 60.0, ring.length, 0.0, 0.0),
    ]
    _, cliff = build(SQUARE, items)
    tris, normals = triangles(cliff)
    assert cliff.report["triangles"]["seam"] == 4
    seams = [
        (t, n[0])
        for t, n in zip(tris, normals, strict=True)
        if abs(n[0, 2]) < 1e-9
        and t[:, 2].min() >= -1e-9
        and t[:, 2].max() <= 0.05 + 1e-9
        and np.ptp(t[:, 0]) < 1e-9
    ]
    assert len(seams) == 4
    for tri, normal in seams:
        x = tri[0, 0]
        assert x in (40.0, 60.0)
        assert normal[0] == (-1.0 if x == 40.0 else 1.0)  # towards the fence, the lower side
        assert tri[:, 1].min() == pytest.approx(-LEDGE) and tri[:, 1].max() == pytest.approx(0)
    ledge = ledge_of(cliff)
    on_span = ledge[(ledge[:, :, 1].max(axis=1) <= 1e-9) & (ledge[:, :, 0].min(axis=1) >= 40)]
    on_span = on_span[on_span[:, :, 0].max(axis=1) <= 60]
    assert len(on_span) and np.allclose(on_span[:, :, 2], 0.05)


def test_the_skirt_closes_the_gap_to_the_terrain_edge() -> None:
    """Terrain along the south edge bulges 0.2 m over the straight ledge line of one fence
    0..100: the skirt fills between the two, facing out where the terrain is higher."""
    ring = Ring.from_coords(SQUARE)
    s = np.array([-10.0, 0.0, 25.0, 50.0, 75.0, 100.0, ring.length, ring.length + 25.0])
    z = np.array([0.0, 0.0, 0.1, 0.2, 0.1, 0.0, 0.0, 0.1])
    items = [
        RingItem("fence:0", 0.0, 100.0, 0.0, 0.0),
        *items_every(ring, 400.0)[1:],
    ]
    _, cliff = build(SQUARE, items, Profile(s, z, ring.length))
    tris, normals = triangles(cliff)
    skirt = [
        (t, n[0])
        for t, n in zip(tris, normals, strict=True)
        if np.abs(t[:, 1]).max() < 1e-9 and abs(n[0, 2]) < 1e-9 and t[:, 2].min() >= -1e-9
    ]
    assert skirt
    for _tri, normal in skirt:
        assert normal[1] == -1.0  # terrain above the ledge: faces out (south), the lower side
    cover = shapely.union_all([Polygon(t[:, [0, 2]]) for t, _ in skirt])
    terrain = Polygon([(0, 0), *zip(s[1:6], z[1:6], strict=True), (100, 0)])
    assert cover.symmetric_difference(terrain).area < 1e-9


def test_wall_splits_where_the_two_heights_cross() -> None:
    p, q = np.array([0.0, 0.0]), np.array([10.0, 0.0])
    nu = np.array([0.0, -1.0])
    parts = wall(p, q, (0.0, 1.0), (1.0, 0.0), nu)
    assert len(parts) == 2
    (first, n_first), (second, n_second) = parts
    assert n_first.tolist() == [0.0, -1.0, 0.0]  # f lower at p: faces nu
    assert n_second.tolist() == [0.0, 1.0, 0.0]
    for tri, normal in parts:
        cross = np.cross(tri[1] - tri[0], tri[2] - tri[0])
        assert np.dot(cross, normal) > 0
    assert wall(p, q, (0.0, 0.0), (0.0, 0.0), nu) == []


def test_pieces_split_at_tile_edges_and_belong_to_the_tile_on_the_playable_side() -> None:
    """A 100 m square on 50 m tiles: each edge is cut at x or n = 50, every piece goes to
    the tile on its inside, and every tile gets a cap."""
    cells = {
        f"tile_x{i}_y{j}": BBox(50.0 * i, 50.0 * j, 50.0 * (i + 1), 50.0 * (j + 1))
        for i in (0, 1)
        for j in (0, 1)
    }
    tiling = Tiling(50.0, cells, frozenset(cells))
    ring, cliff = build(SQUARE, items_every(Ring.from_coords(SQUARE), 400.0), tiling=tiling)
    assert sorted(cliff.nodes) == sorted(cells)
    for tile, nodes in cliff.nodes.items():
        bounds = cells[tile]
        (cliff_node,) = [n for n in nodes if n.name == CLIFF_NODE]
        on_ring = cliff_node.vertices[
            shapely.distance(shapely.points(cliff_node.vertices[:, :2]), ring.line()) < 1e-9
        ]
        assert (on_ring[:, 0] >= bounds.min_x - 1e-9).all()
        assert (on_ring[:, 0] <= bounds.max_x + 1e-9).all()
        assert (on_ring[:, 1] >= bounds.min_y - 1e-9).all()
        assert (on_ring[:, 1] <= bounds.max_y + 1e-9).all()
    assert ring_tile_ids(ring, 50.0, set(cells)) == sorted(cells)


def test_a_stretch_on_a_grid_line_belongs_to_the_tile_inside() -> None:
    ring = Ring.from_coords([(0.0, 0.0), (50.0, 0.0), (50.0, 50.0), (0.0, 50.0)])
    # The east edge lies on x = 50, a grid line of 50 m tiles: inside is tile_x0.
    s0, s1 = 50.0, 100.0
    assert stretch_tile(ring, s0, s1, 50.0) == "tile_x0_y0"


def test_a_piece_of_an_unwritten_tile_goes_to_the_nearest_written_tile() -> None:
    cells = {
        "tile_x0_y0": BBox(0.0, 0.0, 50.0, 100.0),
        "tile_x1_y0": BBox(50.0, 0.0, 100.0, 100.0),
    }
    tiling = Tiling(50.0, cells, frozenset({"tile_x0_y0"}))
    _, cliff = build(SQUARE, tiling=tiling)
    assert sorted(cliff.nodes) == ["tile_x0_y0"]
    assert cliff.report["pieces_moved_to_another_tile"]
    assert cliff.report["caps_moved_to_another_tile"] == ["tile_x1_y0 -> tile_x0_y0"]
    assert nearest_tile(np.array([75.0, 10.0]), ["tile_x0_y0"], cells) == "tile_x0_y0"


def test_the_cap_covers_the_slab_and_faces_down() -> None:
    for coords in (SQUARE, NOTCH, INLET):
        ring, cliff = build(coords)
        caps, normals = triangles(cliff, BOTTOM_NODE)
        assert np.allclose(caps[:, :, 2], BOTTOM)
        assert np.allclose(normals, [0.0, 0.0, -1.0])
        cross = np.cross(caps[:, 1] - caps[:, 0], caps[:, 2] - caps[:, 0])
        assert (cross[:, 2] < 0).all()
        slab = shapely.union_all([ring.polygon(), union_2d(ledge_of(cliff))])
        assert union_2d(caps).symmetric_difference(slab).area < 1e-6


def test_ring_profile_reads_the_terrain_edge_on_the_ring() -> None:
    """A two-triangle terrain square: its four border edges are on the ring, the diagonal
    isn't."""
    ring = Ring.from_coords([(0.0, 0.0), (10.0, 0.0), (10.0, 10.0), (0.0, 10.0)])
    vertices = np.array([[0, 0, 1.0], [10, 0, 2.0], [10, 10, 3.0], [0, 10, 4.0]])
    chunk = TerrainChunk(vertices, np.array([[0, 1, 2], [0, 2, 3]]))
    profile = ring_profile(ring, [chunk])
    assert profile.at(np.array([0.0, 5.0, 10.0, 15.0, 35.0])).tolist() == [1.0, 1.5, 2.0, 2.5, 2.5]
    assert profile.breaks(0.0, 40.0).tolist() == [10.0, 20.0, 30.0]


def _ring_edges(tris: np.ndarray, ring: Ring) -> np.ndarray:
    """Every triangle edge (k, 2, 3) with both ends on the ring (1e-9)."""
    edges = np.concatenate([tris[:, [0, 1]], tris[:, [1, 2]], tris[:, [2, 0]]])
    line = ring.line()
    on = (shapely.distance(shapely.points(edges[:, 0, :2]), line) < 1e-9) & (
        shapely.distance(shapely.points(edges[:, 1, :2]), line) < 1e-9
    )
    return edges[on]


def _distance_to_segments(point: np.ndarray, segments: np.ndarray) -> float:
    a, b = segments[:, 0], segments[:, 1]
    d = b - a
    t = np.clip(((point - a) * d).sum(axis=1) / np.maximum((d**2).sum(axis=1), 1e-300), 0, 1)
    return float(np.linalg.norm(a + d * t[:, None] - point, axis=1).min())


def test_the_skirt_follows_the_terrain_profile_vertices() -> None:
    """Profile vertices every 2 m along the square, the terrain rising and falling by up
    to 0.3 m around the flat fences: the cliff mesh has an edge through every one of
    them, so the terrain meets it exactly."""
    ring = Ring.from_coords(SQUARE)
    s = np.arange(-2.0, ring.length + 4.0, 2.0)
    profile = Profile(s, np.sin(s / 7.0) * 0.3, ring.length)
    _, cliff = build(SQUARE, items_every(ring, 20.0), profile)
    edges = _ring_edges(triangles(cliff)[0], ring)
    for sv in np.arange(0.0, ring.length, 2.0):
        point = np.array([*ring.point_at(sv), float(profile.at(sv))])
        assert _distance_to_segments(point, edges) < 1e-9, sv


def test_no_items_or_no_ledge_fails() -> None:
    ring = Ring.from_coords(SQUARE)
    with pytest.raises(CliffError, match="no roadblocks or fences"):
        build_cliff(ring, [], LEDGE, BOTTOM, flat_profile(ring), one_tile(SQUARE))
    with pytest.raises(CliffError, match="ledge_m must be positive"):
        build_cliff(
            ring, items_every(ring, 10.0), 0.0, BOTTOM, flat_profile(ring), one_tile(SQUARE)
        )


def test_each_wall_of_an_inlet_narrower_than_the_ledge_gets_its_own_height() -> None:
    """In the 1.5 m inlet the east wall's fence is 1 m above the west wall's. Without
    splitting, one cell spanning the inlet would take one wall's height for both; split
    along the midline, every ledge vertex on each wall is at that wall's height, and a
    seam on the midline closes the step."""
    ring = Ring.from_coords(NARROW)
    s = [ring.project(x, n) for x, n in NARROW]  # vertex order is the ring's
    east, bottom, west = s[3], s[4], s[5]
    items = [
        RingItem("fence:outer-a", 0.0, east, 0.0, 1.0),  # rises to the east wall's 1 m
        RingItem("fence:east", east, bottom, 1.0, 1.0),
        RingItem("fence:bottom", bottom, west, 1.0, 0.0),
        RingItem("fence:west", west, s[6], 0.0, 0.0),
        RingItem("fence:outer-b", s[6], ring.length, 0.0, 0.0),
    ]
    _, cliff = build(NARROW, items)
    assert cliff.report["split_lines"] > 0 and cliff.report["cells_left_unsplit"] == 0
    ledge = ledge_of(cliff).reshape(-1, 3)
    for x, expected in ((50.75, 1.0), (49.25, 0.0)):
        on_wall = (np.abs(ledge[:, 0] - x) < 1e-9) & (ledge[:, 1] > 40.0 + 1e-6)
        on_wall &= ledge[:, 1] < 100.0 - 1e-6
        assert on_wall.sum() >= 2, x
        assert np.allclose(ledge[on_wall, 2], expected, atol=1e-9), x
    # Every fence end has a ledge vertex at its height.
    for item in items:
        for s_end, height in ((item.s_start, item.y_start), (item.s_end, item.y_end)):
            point = ring.point_at(s_end % ring.length)
            near = np.hypot(*(ledge[:, :2] - point).T) < 1e-9
            assert near.any() and np.abs(ledge[near, 2] - height).min() < 1e-9, item.id
    # The midline is a seam: a vertical wall at x = 50 between heights 0 and 1.
    tris, normals = triangles(cliff)
    vertical = np.abs(normals[:, 0, 2]) < 1e-9
    seam = vertical & (np.abs(tris[:, :, 0] - 50.0).max(axis=1) < 1e-9)
    assert seam.any()
    assert tris[seam][:, :, 2].min() == pytest.approx(0.0)
    assert tris[seam][:, :, 2].max() == pytest.approx(1.0)


def test_ring_profile_fails_on_a_stretch_without_a_terrain_edge() -> None:
    """Terrain covering only the west half of a 10 m square: its east edge has no
    terrain edge on the ring, so the skirt would be interpolated over 10 m; that fails,
    naming the stretch."""
    ring = Ring.from_coords([(0.0, 0.0), (10.0, 0.0), (10.0, 10.0), (0.0, 10.0)])
    vertices = np.array([[0, 0, 1.0], [5, 0, 2.0], [5, 10, 3.0], [0, 10, 4.0]])
    chunk = TerrainChunk(vertices, np.array([[0, 1, 2], [0, 2, 3]]))
    with pytest.raises(CliffError, match=r"no terrain edge along 1 stretch\(es\)"):
        ring_profile(ring, [chunk])
    with pytest.raises(CliffError, match="no terrain edge along"):
        ring_profile(ring, [])


def test_a_profile_gap_up_to_1_mm_is_allowed() -> None:
    """Terrain edges stopping 0.3 mm short of a corner on both sides (a 0.6 mm gap)
    still cover the ring."""
    ring = Ring.from_coords([(0.0, 0.0), (10.0, 0.0), (10.0, 10.0), (0.0, 10.0)])
    vertices = np.array(
        [[0, 0, 1.0], [9.9997, 0, 2.0], [10, 0.0003, 2.0], [10, 10, 3.0], [0, 10, 4.0]]
    )
    faces = np.array([[0, 1, 2], [0, 2, 3], [0, 3, 4]])
    profile = ring_profile(ring, [TerrainChunk(vertices, faces)])
    assert profile.at(np.array([5.0]))[0] == pytest.approx(1.5, abs=1e-3)
