import numpy as np

from mapgen.build.area import BuildArea
from mapgen.build.heightfield import Heightfield
from mapgen.build.terrain import MeshTerrainBuilder
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
