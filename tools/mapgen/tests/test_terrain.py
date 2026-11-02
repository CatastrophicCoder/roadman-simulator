import numpy as np
import pytest
import shapely
from shapely.geometry import Polygon

from conftest import mesh_height_at
from mapgen.build.area import BuildArea
from mapgen.build.heightfield import Heightfield
from mapgen.build.terrain import MeshTerrainBuilder
from mapgen.build.terrain.clip import clip_chunk
from mapgen.build.terrain.mesh import grid_lines
from mapgen.geo import BBox


def field() -> Heightfield:
    rng = np.random.default_rng(3)
    return Heightfield(z=rng.uniform(80, 90, (60, 60)), x0=-59.0, y0=-59.0, dx=2.0, dy=2.0)


def test_grid_lines_keep_tile_edges_and_inner_nodes() -> None:
    nodes = np.arange(-5, 6) * 2.0 + 1.0
    lines = grid_lines(nodes, -4.0, 4.0, 2.0)
    assert lines.tolist() == [-4.0, -3.0, -1.0, 1.0, 3.0, 4.0]
    assert grid_lines(np.array([0.0, 2.0, 4.0]), 0.0, 4.0, 2.0).tolist() == [0.0, 2.0, 4.0]


def test_tile_vertices_stay_in_bounds_and_faces_point_up() -> None:
    hf = field()
    area = BuildArea("test", BBox(-50, -50, 50, 50), 32.0)
    for tile in area.tiles():
        chunk = MeshTerrainBuilder().build_tile(hf, tile)
        v, f = chunk.vertices, chunk.faces
        b = tile.bounds
        assert v[:, 0].min() >= b.min_x - 1e-6 and v[:, 0].max() <= b.max_x + 1e-6
        assert v[:, 1].min() >= b.min_y - 1e-6 and v[:, 1].max() <= b.max_y + 1e-6
        assert np.all(np.isfinite(v))
        normal_z = np.cross(v[f[:, 1]] - v[f[:, 0]], v[f[:, 2]] - v[f[:, 0]])[:, 2]
        assert np.all(normal_z > 0)


def test_neighbouring_tiles_share_identical_edge_vertices() -> None:
    hf = field()
    tiles = {t.id: t for t in BuildArea("test", BBox(-50, -50, 50, 50), 32.0).tiles()}
    builder = MeshTerrainBuilder()
    west = builder.build_tile(hf, tiles["tile_x-1_y0"]).vertices
    east = builder.build_tile(hf, tiles["tile_x0_y0"]).vertices
    south = builder.build_tile(hf, tiles["tile_x0_y-1"]).vertices
    west_edge = west[west[:, 0] == 0.0]
    east_edge = east[east[:, 0] == 0.0]
    assert len(west_edge) > 2
    assert np.array_equal(west_edge[np.lexsort(west_edge.T)], east_edge[np.lexsort(east_edge.T)])
    north_edge = south[south[:, 1] == 0.0]
    east_bottom = east[east[:, 1] == 0.0]
    assert np.array_equal(
        north_edge[np.lexsort(north_edge.T)], east_bottom[np.lexsort(east_bottom.T)]
    )


def _edge(chunk_vertices: np.ndarray, values: np.ndarray, axis: int) -> np.ndarray:
    """Rows of `values` for vertices on the line axis == 0, sorted along the other axis."""
    on = chunk_vertices[:, axis] == 0.0
    order = np.argsort(chunk_vertices[on, 1 - axis])
    return values[on][order]


def _check_shared_edges(hf: Heightfield) -> None:
    tiles = {t.id: t for t in BuildArea("test", BBox(-50, -50, 50, 50), 32.0).tiles()}
    builder = MeshTerrainBuilder()
    west = builder.build_tile(hf, tiles["tile_x-1_y0"])
    east = builder.build_tile(hf, tiles["tile_x0_y0"])
    south = builder.build_tile(hf, tiles["tile_x0_y-1"])
    for chunk in (west, east, south):
        assert chunk.normals is not None and chunk.normals.shape == chunk.vertices.shape
    assert west.normals is not None and east.normals is not None and south.normals is not None
    assert np.array_equal(
        _edge(west.vertices, west.vertices, 0), _edge(east.vertices, east.vertices, 0)
    )
    assert np.array_equal(
        _edge(west.vertices, west.normals, 0), _edge(east.vertices, east.normals, 0)
    )
    assert np.array_equal(
        _edge(south.vertices, south.normals, 1), _edge(east.vertices, east.normals, 1)
    )


def test_shared_edge_normals_match_between_tiles() -> None:
    _check_shared_edges(field())  # nodes at odd metres: the tile edge falls between nodes


def test_shared_edges_match_when_nodes_lie_on_the_tile_edge() -> None:
    rng = np.random.default_rng(5)
    hf = Heightfield(z=rng.uniform(80, 90, (60, 60)), x0=-60.0, y0=-60.0, dx=2.0, dy=2.0)
    assert 0.0 in hf.xs and 0.0 in hf.ys
    _check_shared_edges(hf)
    tile = BuildArea("test", BBox(-50, -50, 50, 50), 32.0).tiles()[0]
    xs = np.unique(MeshTerrainBuilder().build_tile(hf, tile).vertices[:, 0])
    assert np.all(np.diff(xs) > 1.0)  # no sliver columns next to the edge nodes


def test_normals_follow_the_slope() -> None:
    xs = np.arange(-10, 11) * 2.0
    gx, gy = np.meshgrid(xs, xs)
    hf = Heightfield(z=0.5 * gx, x0=-20.0, y0=-20.0, dx=2.0, dy=2.0)
    normal = hf.normals(np.array([0.0]), np.array([3.3]))[0]
    assert np.allclose(normal, np.array([-0.5, 0.0, 1.0]) / np.sqrt(1.25))


# Cutting along a custom boundary polygon (docs/specs/world-edge-pipeline.md, slice (b)).

# A concave polygon crossing the four tiles of `field()`'s area at slanted angles.
CUT = Polygon([(-45.3, -40.1), (41.7, -47.9), (30.2, 44.4), (3.3, 5.1), (-38.6, 37.7)])


def test_cut_terrain_lies_inside_the_polygon_and_covers_it() -> None:
    hf = field()
    area = BuildArea("full", BBox(-50, -50, 50, 50), 64.0)
    builder = MeshTerrainBuilder()
    covered = 0.0
    for tile in area.tiles():
        chunk, stats = clip_chunk(builder.build_tile(hf, tile), CUT, hf)
        v, f = chunk.vertices, chunk.faces
        assert stats.kept + stats.cut + stats.removed == stats.grid
        assert len(f) == stats.kept + stats.pieces
        if len(f) == 0:
            continue
        tris = shapely.polygons(v[f][:, :, :2])
        # No triangle reaches outside the polygon (1 mm).
        assert shapely.covers(CUT.buffer(1e-3), tris).all()
        normal_z = np.cross(v[f[:, 1]] - v[f[:, 0]], v[f[:, 2]] - v[f[:, 0]])[:, 2]
        assert np.all(normal_z > 0)
        covered += float(shapely.area(tris).sum())
        # Every vertex is on the uncut grid terrain (the cut only removes, never moves).
        uncut = builder.build_tile(hf, tile)
        expected = mesh_height_at(uncut.vertices, uncut.faces, v[:, :2])
        assert np.allclose(v[:, 2], expected, atol=1e-9)
        # Normals come from the heightfield gradient, like every terrain vertex.
        assert chunk.normals is not None
        assert np.allclose(chunk.normals, hf.normals(v[:, 0], v[:, 1]), atol=1e-12)
        assert chunk.rows is not None and np.array_equal(
            chunk.rows, np.unique(uncut.vertices[:, 1])
        )
    assert covered == pytest.approx(CUT.area, abs=1e-6)


def test_a_tile_outside_the_polygon_has_no_terrain_left() -> None:
    hf = field()
    tile = BuildArea("full", BBox(-50, -50, 50, 50), 64.0).tiles()[0]  # x, n -50..0
    small = Polygon([(10, 10), (40, 10), (40, 40)])
    chunk, stats = clip_chunk(MeshTerrainBuilder().build_tile(hf, tile), small, hf)
    assert len(chunk.faces) == 0 and stats.removed == stats.grid


def test_triangles_only_touching_the_polygon_are_removed_not_cut() -> None:
    """A box on the grid lines (odd metres): the triangles outside share an edge or a
    corner with it but have no piece inside, so nothing is cut."""
    hf = field()
    tile = BuildArea("full", BBox(-50, -50, 50, 50), 64.0).tiles()[0]  # x, n -50..0
    on_grid = shapely.box(-21.0, -21.0, -1.0, -1.0)
    grid = MeshTerrainBuilder().build_tile(hf, tile)
    chunk, stats = clip_chunk(grid, on_grid, hf)
    assert (stats.cut, stats.pieces) == (0, 0)
    assert stats.kept == len(chunk.faces) == 2 * 10 * 10  # 10 x 10 cells of 2 m
    assert stats.removed == stats.grid - stats.kept
    # The touching triangles are there to be miscounted: they meet the box.
    tris = shapely.polygons(grid.vertices[grid.faces][:, :, :2])
    touching = shapely.touches(on_grid, tris)
    assert touching.sum() > 0


def _boundary_edges(faces: np.ndarray) -> np.ndarray:
    """The edges (vertex index pairs) used by exactly one face."""
    edges = np.sort(np.concatenate([faces[:, [0, 1]], faces[:, [1, 2]], faces[:, [2, 0]]]), axis=1)
    unique, counts = np.unique(edges, axis=0, return_counts=True)
    return unique[counts == 1]


def test_cut_pieces_share_their_cut_vertices() -> None:
    """Neighbouring pieces share the vertices on their common edges: every vertex is
    distinct (1e-9 m), and every edge used by one face only lies on the polygon's edge or
    the tile's edge, so the cut terrain has no cracks inside."""
    hf = field()
    area = BuildArea("full", BBox(-50, -50, 50, 50), 64.0)
    for tile in area.tiles():
        chunk, stats = clip_chunk(MeshTerrainBuilder().build_tile(hf, tile), CUT, hf)
        if stats.cut == 0:
            continue
        v, f = chunk.vertices, chunk.faces
        keys = {(round(x / 1e-9), round(y / 1e-9)) for x, y in v[:, :2]}
        assert len(keys) == len(v)
        b = tile.bounds
        outline = shapely.union_all([CUT.exterior, shapely.box(*b.as_list()).exterior])
        for i, j in _boundary_edges(np.asarray(f)):
            mid = shapely.Point((v[i, :2] + v[j, :2]) / 2)
            assert outline.distance(mid) <= 1e-9, (tile.id, v[i, :2], v[j, :2])


def test_cut_stats_count_only_triangles_that_produced_a_piece() -> None:
    hf = field()
    area = BuildArea("full", BBox(-50, -50, 50, 50), 64.0)
    for tile in area.tiles():
        grid = MeshTerrainBuilder().build_tile(hf, tile)
        chunk, stats = clip_chunk(grid, CUT, hf)
        tris = shapely.polygons(grid.vertices[grid.faces][:, :, :2])
        inside_area = shapely.area(shapely.intersection(tris, CUT))
        partial = (inside_area > 1e-9) & ~shapely.contains(CUT, tris)
        assert stats.cut == int(partial.sum())
        assert stats.pieces == len(chunk.faces) - stats.kept
