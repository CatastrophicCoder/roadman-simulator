"""Mesh terrain: a regular grid at the DEM resolution, cut to the tile.

Vertices are the heightfield nodes strictly inside the tile plus lines at the tile's
edges, all sampled from the heightfield. Neighbouring tiles therefore share identical
edge vertices and normals (normals come from the heightfield gradient, not from the
tile's own faces), and every quad stays within one DEM cell.
"""

import numpy as np

from mapgen.build.area import Tile
from mapgen.build.heightfield import Heightfield
from mapgen.build.terrain.base import TerrainChunk


def grid_lines(nodes: np.ndarray, lo: float, hi: float, spacing: float) -> np.ndarray:
    """`lo`, the nodes strictly inside (lo, hi), and `hi`; nodes very close to an edge drop."""
    eps = spacing * 1e-3
    inside = nodes[(nodes > lo + eps) & (nodes < hi - eps)]
    return np.concatenate(([lo], inside, [hi]))


def grid_faces(nx: int, ny: int) -> np.ndarray:
    """Two counter-clockwise triangles per quad of an nx x ny vertex grid (row-major, x fast)."""
    i, j = np.meshgrid(np.arange(nx - 1), np.arange(ny - 1))
    v00 = (j * nx + i).ravel()
    v10 = v00 + 1
    v01 = v00 + nx
    v11 = v01 + 1
    return np.concatenate(
        [np.stack([v00, v10, v11], axis=1), np.stack([v00, v11, v01], axis=1)]
    ).astype(np.int64)


class MeshTerrainBuilder:
    """The interim terrain implementation (ADR 0004 is still open)."""

    def build_tile(self, heightfield: Heightfield, tile: Tile) -> TerrainChunk:
        b = tile.bounds
        xs = grid_lines(heightfield.xs, b.min_x, b.max_x, heightfield.dx)
        ys = grid_lines(heightfield.ys, b.min_y, b.max_y, heightfield.dy)
        gx, gy = np.meshgrid(xs, ys)
        gz = heightfield.sample(gx, gy)
        vertices = np.stack([gx.ravel(), gy.ravel(), gz.ravel()], axis=1)
        normals = heightfield.normals(gx.ravel(), gy.ravel())
        return TerrainChunk(vertices=vertices, faces=grid_faces(len(xs), len(ys)), normals=normals)
