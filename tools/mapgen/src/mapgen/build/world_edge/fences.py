"""Fence segments along the parts of the ring no roadblock covers.

The uncovered stretches are split at ring vertices and water crossing ends, then into
equal parts of at most `max_segment_m`. A land segment is split further, at the point of
its largest deviation, until the terrain (`Heightfield.sample`) between its ends is within
`max_terrain_deviation_m` of its straight line. A segment inside a water crossing is
`water`, any other `land`; a segment is never partly over water.
"""

import math
from collections.abc import Sequence
from dataclasses import dataclass
from typing import Literal

import numpy as np

from mapgen.build.heightfield import Heightfield
from mapgen.build.world_edge.ring import EPS, Interval, Ring
from mapgen.build.world_edge.water import WaterCrossing

FenceType = Literal["land", "water"]
MIN_SPLIT_M = 1e-3  # a land segment shorter than this isn't split further


@dataclass(frozen=True)
class FenceSegment:
    s_start: float
    s_end: float
    type: FenceType
    water_ids: tuple[str, ...] = ()

    @property
    def length(self) -> float:
        return self.s_end - self.s_start


def max_deviation(
    heightfield: Heightfield, p0: Sequence[float], p1: Sequence[float]
) -> tuple[float, float]:
    """The largest |terrain - line| between p0 and p1 (local x, n) and the fraction t
    (0..1) along the segment where it occurs. The line runs between the terrain heights at
    the two ends.

    Exact for the bilinear heightfield: along a straight line the terrain is quadratic in
    t inside each grid cell, so the maximum is at a cell boundary or a parabola's vertex.
    """
    x0, y0 = float(p0[0]), float(p0[1])
    x1, y1 = float(p1[0]), float(p1[1])
    ts = [0.0, 1.0]
    for start, end, origin, step, count in (
        (x0, x1, heightfield.x0, heightfield.dx, heightfield.z.shape[1]),
        (y0, y1, heightfield.y0, heightfield.dy, heightfield.z.shape[0]),
    ):
        if end == start:
            continue
        lo, hi = sorted(((start - origin) / step, (end - origin) / step))
        k = np.arange(max(math.ceil(lo), 0), min(math.floor(hi), count - 1) + 1)
        ts += list((origin + k * step - start) / (end - start))
    t = np.unique(np.clip(np.asarray(ts, dtype=float), 0.0, 1.0))
    z0, z1 = (float(v) for v in heightfield.sample(np.array([x0, x1]), np.array([y0, y1])))

    def deviation(tv: np.ndarray) -> np.ndarray:
        x = x0 + (x1 - x0) * tv
        y = y0 + (y1 - y0) * tv
        return np.asarray(heightfield.sample(x, y) - (z0 + (z1 - z0) * tv))

    ta, tb = t[:-1], t[1:]
    tm = (ta + tb) / 2
    fa, fm, fb = deviation(ta), deviation(tm), deviation(tb)
    # The parabola through the three samples of each piece, and its vertex.
    h = (tb - ta) / 2
    curvature = fa - 2 * fm + fb  # = 2 * c * h**2 for f = c t^2 + ...
    with np.errstate(divide="ignore", invalid="ignore"):
        offset = np.where(np.abs(curvature) > 1e-15, h * (fa - fb) / (2 * curvature), 0.0)
    tv = np.clip(tm + offset, ta, tb)
    candidates = np.concatenate([t, tm, tv])
    values = np.abs(deviation(candidates))
    k = int(np.argmax(values))
    return float(values[k]), float(candidates[k])


def _split_points(ring: Ring, a: float, b: float, cuts: Sequence[float]) -> list[float]:
    """a, every cut strictly inside (a, b) (cuts are ring positions), and b."""
    inner = sorted(
        {
            float(c + k * ring.length)
            for c in cuts
            for k in range(-1, 3)
            if a + EPS < c + k * ring.length < b - EPS
        }
    )
    return [a, *inner, b]


def _water_of(crossings: Sequence[WaterCrossing], ring: Ring, s: float) -> WaterCrossing | None:
    for crossing in crossings:
        if ring.contains_s(crossing.interval, s):
            return crossing
    return None


def fence_segments(
    ring: Ring,
    covered: Sequence[Interval],
    crossings: Sequence[WaterCrossing],
    heightfield: Heightfield,
    max_segment_m: float,
    max_terrain_deviation_m: float,
) -> list[FenceSegment]:
    """The fence segments over every stretch `covered` (the roadblock spans) leaves free,
    sorted by `s_start`."""
    cuts = [*ring.vertex_s.tolist()]
    for crossing in crossings:
        cuts += [ring.wrap(crossing.s_start), ring.wrap(crossing.s_end)]
    result: list[FenceSegment] = []
    for a, b in ring.complement(covered):
        points = _split_points(ring, a, b, cuts)
        for s0, s1 in zip(points[:-1], points[1:], strict=True):
            water = _water_of(crossings, ring, (s0 + s1) / 2)
            count = max(1, math.ceil((s1 - s0) / max_segment_m - 1e-9))
            edges = [s0 + (s1 - s0) * k / count for k in range(count)] + [s1]
            for e0, e1 in zip(edges[:-1], edges[1:], strict=True):
                if water is not None:
                    result.append(FenceSegment(e0, e1, "water", water.water_ids))
                else:
                    result += _land(ring, heightfield, e0, e1, max_terrain_deviation_m)
    # A free stretch over the ring's start is cut at vertex 0; its pieces after the start
    # move back into [0, length).
    result = [
        FenceSegment(f.s_start - ring.length, f.s_end - ring.length, f.type, f.water_ids)
        if f.s_start >= ring.length - EPS
        else f
        for f in result
    ]
    return sorted(result, key=lambda f: f.s_start)


def _land(
    ring: Ring, heightfield: Heightfield, s0: float, s1: float, limit: float
) -> list[FenceSegment]:
    """A land stretch within one ring segment, split until the terrain follows each piece."""
    stack = [(s0, s1)]
    result: list[FenceSegment] = []
    while stack:
        a, b = stack.pop()
        deviation, t = max_deviation(heightfield, ring.point_at(a), ring.point_at(b))
        if deviation <= limit or b - a < MIN_SPLIT_M:
            result.append(FenceSegment(a, b, "land"))
            continue
        if not 0.0 < t < 1.0:
            t = 0.5
        middle = a + (b - a) * t
        stack += [(middle, b), (a, middle)]
    return result
