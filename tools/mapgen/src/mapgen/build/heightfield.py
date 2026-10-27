"""The terrain height sampler shared by terrain, buildings and (later) road draping.

Heights sit on a regular grid of nodes in local coordinates: node (r, c) is at
(x0 + c*dx, y0 + r*dy), with rows running south to north. Between nodes heights are
bilinear; outside the grid the nearest edge value is used.
"""

from dataclasses import dataclass

import numpy as np
import shapely
from shapely.geometry import MultiPolygon, Polygon

from mapgen.sources.base import ElevationRaster

FAR = 1e12  # edge cells reach this far, so clamped samples outside the grid are covered


@dataclass(frozen=True)
class Heightfield:
    z: np.ndarray  # shape (rows, cols), rows south to north, no NaN
    x0: float
    y0: float
    dx: float
    dy: float

    def __post_init__(self) -> None:
        if self.z.ndim != 2 or self.z.shape[0] < 2 or self.z.shape[1] < 2:
            raise ValueError("a heightfield needs at least 2 x 2 nodes")
        if self.dx <= 0 or self.dy <= 0:
            raise ValueError("node spacing must be positive")
        if not np.all(np.isfinite(self.z)):
            raise ValueError("heightfield contains NaN or infinite values")

    @property
    def xs(self) -> np.ndarray:
        return self.x0 + np.arange(self.z.shape[1]) * self.dx

    @property
    def ys(self) -> np.ndarray:
        return self.y0 + np.arange(self.z.shape[0]) * self.dy

    def sample(self, x: np.ndarray | float, y: np.ndarray | float) -> np.ndarray:
        """Bilinear heights at the given local points (arrays broadcast together)."""
        rows, cols = self.z.shape
        fx = np.clip((np.asarray(x, dtype=float) - self.x0) / self.dx, 0, cols - 1)
        fy = np.clip((np.asarray(y, dtype=float) - self.y0) / self.dy, 0, rows - 1)
        c = np.minimum(np.floor(fx).astype(int), cols - 2)
        r = np.minimum(np.floor(fy).astype(int), rows - 2)
        tx, ty = fx - c, fy - r
        z = self.z
        south = z[r, c] * (1 - tx) + z[r, c + 1] * tx
        north = z[r + 1, c] * (1 - tx) + z[r + 1, c + 1] * tx
        return np.asarray(south * (1 - ty) + north * ty)

    def normals(self, x: np.ndarray, y: np.ndarray) -> np.ndarray:
        """Unit surface normals (x east, y north, z up) at local points, shape (n, 3).

        Central differences of `sample` one node spacing apart, so a point gets the same
        normal whichever tile it belongs to.
        """
        x = np.asarray(x, dtype=float).ravel()
        y = np.asarray(y, dtype=float).ravel()
        dzdx = (self.sample(x + self.dx, y) - self.sample(x - self.dx, y)) / (2 * self.dx)
        dzdy = (self.sample(x, y + self.dy) - self.sample(x, y - self.dy)) / (2 * self.dy)
        n = np.column_stack([-dzdx, -dzdy, np.ones_like(dzdx)])
        return n / np.linalg.norm(n, axis=1, keepdims=True)

    def min_under(self, footprint: Polygon | MultiPolygon) -> float:
        """A height at or below the terrain everywhere under `footprint`.

        The lowest node of every grid cell the footprint touches. Inside a cell, both the
        bilinear surface and any terrain mesh built from samples of this heightfield (with
        triangles that stay within one cell) lie between the cell's lowest and highest
        node, so a building base at this height never floats above the ground. This is at
        most one cell's height difference below the exact minimum.
        """
        xs, ys = self.xs, self.ys
        edges_x = np.concatenate(([-FAR], xs[1:-1], [FAR]))
        edges_y = np.concatenate(([-FAR], ys[1:-1], [FAR]))
        min_x, min_y, max_x, max_y = footprint.bounds
        c0 = max(int(np.searchsorted(edges_x, min_x, side="right")) - 1, 0)
        c1 = min(int(np.searchsorted(edges_x, max_x, side="left")), len(xs) - 1)
        r0 = max(int(np.searchsorted(edges_y, min_y, side="right")) - 1, 0)
        r1 = min(int(np.searchsorted(edges_y, max_y, side="left")), len(ys) - 1)
        cols = np.arange(c0, max(c1, c0 + 1))
        rows = np.arange(r0, max(r1, r0 + 1))
        cc, rr = np.meshgrid(cols, rows)
        cells = shapely.box(edges_x[cc], edges_y[rr], edges_x[cc + 1], edges_y[rr + 1])
        shapely.prepare(footprint)
        hit = shapely.intersects(footprint, cells)
        if not np.any(hit):  # degenerate footprint: fall back to its vertices' cells
            hit = np.ones_like(hit, dtype=bool)
        r, c = rr[hit], cc[hit]
        z = self.z
        corners = np.stack([z[r, c], z[r, c + 1], z[r + 1, c], z[r + 1, c + 1]])
        return float(corners.min())


def fill_nodata(data: np.ndarray, max_passes: int | None = None) -> tuple[np.ndarray, int]:
    """Fill NaN cells with the mean of their valid 4-neighbours, growing inward pass by pass.

    Returns the filled copy and the number of filled cells. Raises if every cell is NaN.
    """
    filled = np.array(data, dtype=float)
    missing = ~np.isfinite(filled)
    count = int(missing.sum())
    if count == 0:
        return filled, 0
    if count == filled.size:
        raise ValueError("the elevation data has no valid cells in the build area")
    passes = max_passes if max_passes is not None else sum(filled.shape)
    for _ in range(passes):
        missing = ~np.isfinite(filled)
        if not missing.any():
            break
        valid = np.where(missing, 0.0, filled)
        weight = (~missing).astype(float)
        total = np.zeros_like(filled)
        n = np.zeros_like(filled)
        total[1:, :] += valid[:-1, :]
        n[1:, :] += weight[:-1, :]
        total[:-1, :] += valid[1:, :]
        n[:-1, :] += weight[1:, :]
        total[:, 1:] += valid[:, :-1]
        n[:, 1:] += weight[:, :-1]
        total[:, :-1] += valid[:, 1:]
        n[:, :-1] += weight[:, 1:]
        grow = missing & (n > 0)
        filled[grow] = total[grow] / n[grow]
    return filled, count


def heightfield_from_raster(
    raster: ElevationRaster, origin_x: float, origin_y: float
) -> tuple[Heightfield, int]:
    """A local heightfield with nodes at the raster's pixel centres, and the filled cell count.

    Nodata cells are filled from their neighbours.
    """
    t = raster.transform
    if t.b != 0 or t.d != 0 or t.e >= 0:
        raise ValueError("only north-up rasters are supported")
    data, filled = fill_nodata(raster.data)
    rows = data.shape[0]
    dx, dy = abs(t.a), abs(t.e)
    x0 = t.c + dx / 2 - origin_x
    y0 = t.f - (rows - 0.5) * dy - origin_y  # the southernmost row's centre
    return Heightfield(z=np.flipud(data).copy(), x0=x0, y0=y0, dx=dx, dy=dy), filled
