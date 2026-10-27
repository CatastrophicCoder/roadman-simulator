"""Building heights from lidar points (spec `docs/specs/building-heights.md`).

For each footprint:

1. Pick the non-ground points inside the footprint shrunk by each of `inner_buffers_m` in
   turn, and stop at the first buffer that leaves at least `min_points`. A shrink that
   leaves no polygon falls through to the next buffer.
2. Tree check: if the 90th minus 10th percentile of those points' z is over
   `tree_spread_m`, keep only single returns; fewer than `min_points` left is `tree_cover`.
3. `roof_z_median` and `roof_z_p90` of the selected points (absolute heights).
4. `height_m = roof_z_median - base_z`.
5. Below `min_height_m` is `too_low`, above `max_height_m` is `too_high`.
6. Fewer than `min_points` at every buffer is `few_points`.

Points and footprints share one coordinate system (local, for the build). The functions
take plain numpy arrays through `PointCloud`, so tests need no LAZ files.
"""

import math
from dataclasses import dataclass
from typing import Any, Literal

import numpy as np
import shapely
from shapely.geometry import MultiPolygon, Polygon

from mapgen.sources.base import PointCloud

RejectReason = Literal["few_points", "tree_cover", "too_low", "too_high"]
REJECT_REASONS: tuple[RejectReason, ...] = ("few_points", "tree_cover", "too_low", "too_high")
Returns = Literal["all", "single"]
INDEX_CELL_M = 16.0  # grid cell size of `PointIndex`


@dataclass(frozen=True)
class LidarParams:
    """The thresholds of the lidar height rule. Defaults match the spec."""

    inner_buffers_m: tuple[float, ...] = (1.0, 0.5, 0.0)
    min_points: int = 5
    tree_spread_m: float = 8.0
    min_height_m: float = 1.0
    max_height_m: float = 40.0

    def __post_init__(self) -> None:
        if not self.inner_buffers_m:
            raise ValueError("inner_buffers_m needs at least one buffer")
        if any(not math.isfinite(b) or b < 0 for b in self.inner_buffers_m):
            raise ValueError("inner_buffers_m values must be finite and >= 0")
        if self.min_points < 1:
            raise ValueError("min_points must be at least 1")
        if not self.tree_spread_m > 0:
            raise ValueError("tree_spread_m must be positive")
        if not 0 < self.min_height_m < self.max_height_m:
            raise ValueError("need 0 < min_height_m < max_height_m")

    @classmethod
    def from_options(cls, options: Any) -> "LidarParams":
        """The thresholds from a point cloud source's options; missing ones use defaults."""
        default = cls()
        return cls(
            inner_buffers_m=tuple(
                float(b) for b in getattr(options, "inner_buffers_m", default.inner_buffers_m)
            ),
            min_points=int(getattr(options, "min_points", default.min_points)),
            tree_spread_m=float(getattr(options, "tree_spread_m", default.tree_spread_m)),
            min_height_m=float(getattr(options, "min_height_m", default.min_height_m)),
            max_height_m=float(getattr(options, "max_height_m", default.max_height_m)),
        )

    def as_dict(self) -> dict[str, Any]:
        return {
            "inner_buffers_m": list(self.inner_buffers_m),
            "min_points": self.min_points,
            "tree_spread_m": self.tree_spread_m,
            "min_height_m": self.min_height_m,
            "max_height_m": self.max_height_m,
        }


@dataclass(frozen=True)
class LidarResult:
    """The lidar height of one footprint, or why there is none.

    Exactly one of `height_m` and `rejected` is set. `points`, `buffer_m`, `returns` and
    the roof elevations describe the selected points when there were enough of them (also
    for `too_low` and `too_high`); for `few_points` they are 0 / None.
    """

    height_m: float | None
    rejected: RejectReason | None
    points: int = 0
    buffer_m: float | None = None
    returns: Returns | None = None
    roof_z_median: float | None = None
    roof_z_p90: float | None = None

    @property
    def ok(self) -> bool:
        return self.height_m is not None


class PointIndex:
    """A grid index over a point cloud for fast lookups by bounding box.

    Points are sorted by grid cell (row-major), so the points of one row of cells form a
    contiguous slice.
    """

    def __init__(self, cloud: PointCloud, cell_m: float = INDEX_CELL_M) -> None:
        if not cell_m > 0:
            raise ValueError("cell_m must be positive")
        self.cell_m = cell_m
        n = len(cloud)
        if n == 0:
            self.x0 = self.y0 = 0.0
            self.nx = self.ny = 1
            self.cloud = cloud
            self._starts = np.zeros(2, dtype=np.int64)
            return
        self.x0 = float(cloud.x.min())
        self.y0 = float(cloud.y.min())
        ix = ((cloud.x - self.x0) // cell_m).astype(np.int64)
        iy = ((cloud.y - self.y0) // cell_m).astype(np.int64)
        self.nx = int(ix.max()) + 1
        self.ny = int(iy.max()) + 1
        keys = iy * self.nx + ix
        order = np.argsort(keys, kind="stable")
        self.cloud = cloud.select(order)
        # _starts[k] is the first sorted point of cell k; _starts[nx * ny] == n.
        self._starts = np.searchsorted(keys[order], np.arange(self.nx * self.ny + 1))

    def __len__(self) -> int:
        return len(self.cloud)

    def in_bounds(self, min_x: float, min_y: float, max_x: float, max_y: float) -> np.ndarray:
        """Indices into `self.cloud` of the points in the cells touching the box.

        A superset of the points inside the box; callers filter further.
        """
        if len(self.cloud) == 0:
            return np.zeros(0, dtype=np.int64)
        c = self.cell_m
        ix0 = max(int((min_x - self.x0) // c), 0)
        ix1 = min(int((max_x - self.x0) // c), self.nx - 1)
        iy0 = max(int((min_y - self.y0) // c), 0)
        iy1 = min(int((max_y - self.y0) // c), self.ny - 1)
        if ix0 > ix1 or iy0 > iy1:
            return np.zeros(0, dtype=np.int64)
        rows = np.arange(iy0, iy1 + 1) * self.nx
        starts = self._starts[rows + ix0]
        stops = self._starts[rows + ix1 + 1]
        slices = [np.arange(a, b) for a, b in zip(starts, stops, strict=True) if b > a]
        return np.concatenate(slices) if slices else np.zeros(0, dtype=np.int64)


def _inside(geometry: Polygon | MultiPolygon, x: np.ndarray, y: np.ndarray) -> np.ndarray:
    """Boolean mask of the points strictly inside `geometry` (boundary points excluded)."""
    if len(x) == 0:
        return np.zeros(0, dtype=bool)
    return np.asarray(shapely.contains_xy(geometry, x, y), dtype=bool)


def _shrunk(geometry: Polygon | MultiPolygon, buffer_m: float) -> Polygon | MultiPolygon | None:
    if buffer_m == 0:
        return geometry
    shrunk = geometry.buffer(-buffer_m)
    if shrunk.is_empty or not shrunk.area > 0:
        return None
    if not isinstance(shrunk, Polygon | MultiPolygon):
        parts = [g for g in getattr(shrunk, "geoms", []) if isinstance(g, Polygon)]
        if not parts:
            return None
        shrunk = MultiPolygon(parts)
    return shrunk


def lidar_height(
    footprint: Polygon | MultiPolygon,
    points: PointIndex,
    base_z: float,
    params: LidarParams | None = None,
) -> LidarResult:
    """The lidar height of `footprint` above `base_z`, following the steps in the module doc.

    `points` must hold non-ground points only (the loader drops ground and noise classes)
    in the same coordinates as `footprint`.
    """
    p = params or LidarParams()
    if not math.isfinite(base_z):
        raise ValueError("base_z must be finite")
    candidates = points.in_bounds(*footprint.bounds)
    cloud = points.cloud
    cx, cy = cloud.x[candidates], cloud.y[candidates]
    if len(candidates):
        candidates = candidates[_inside(footprint, cx, cy)]
        cx, cy = cloud.x[candidates], cloud.y[candidates]

    selected: np.ndarray | None = None
    buffer_used = 0.0
    for buffer_m in p.inner_buffers_m:
        shape = _shrunk(footprint, buffer_m)
        if shape is None:
            continue
        chosen = candidates if buffer_m == 0 else candidates[_inside(shape, cx, cy)]
        if len(chosen) >= p.min_points:
            selected, buffer_used = chosen, float(buffer_m)
            break
    if selected is None:
        return LidarResult(height_m=None, rejected="few_points")

    z = cloud.z[selected]
    returns: Returns = "all"
    p10, p90 = np.percentile(z, [10, 90])
    if p90 - p10 > p.tree_spread_m:
        single = cloud.number_of_returns[selected] == 1
        returns = "single"
        if int(single.sum()) < p.min_points:
            return LidarResult(
                height_m=None,
                rejected="tree_cover",
                points=int(single.sum()),
                buffer_m=buffer_used,
                returns=returns,
            )
        z = z[single]

    roof_median, roof_p90 = (float(v) for v in np.percentile(z, [50, 90]))
    height = roof_median - base_z
    rejected: RejectReason | None = None
    if not height >= p.min_height_m:  # also catches NaN
        rejected = "too_low"
    elif height > p.max_height_m:
        rejected = "too_high"
    return LidarResult(
        height_m=None if rejected else height,
        rejected=rejected,
        points=len(z),
        buffer_m=buffer_used,
        returns=returns,
        roof_z_median=roof_median,
        roof_z_p90=roof_p90,
    )
