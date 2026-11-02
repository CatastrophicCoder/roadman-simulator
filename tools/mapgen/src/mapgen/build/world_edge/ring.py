"""The boundary ring and distances along it (docs/specs/world-edge-pipeline.md).

Local coordinates throughout: x east, n north (metres from the map origin). The ring is
counter-clockwise seen from above and starts at its vertex with the lowest x, then the
lowest n. `s` is the distance along the ring from that vertex, in [0, length).

Stretches of the ring are circular intervals `(a, b)` with `0 <= a < length` and
`a < b <= a + length`: a stretch over the ring's start has `b > length`. Nothing here
knows about the rectangle; every function works for any simple ring.
"""

import math
from collections.abc import Iterable, Sequence
from dataclasses import dataclass, field

import numpy as np
import shapely
from shapely.geometry import LineString, Point, Polygon
from shapely.geometry.base import BaseGeometry

Interval = tuple[float, float]
EPS = 1e-9  # metres: s values closer than this are the same point
# Ring points this close to a geometry count as inside it (`Ring.intervals`): a surface
# edge lying on the ring within floating-point noise (the WGS84 round trip and GEOS
# intersections leave about 1 µm) covers the ring there. Far below the 1 mm checks; at a
# crossing angle θ it widens a stretch by ON_RING_M / sin θ at each end.
ON_RING_M = 1e-5


@dataclass(frozen=True)
class Ring:
    """A simple closed ring, normalised (CCW, start vertex), with its `s` distances."""

    xy: np.ndarray  # (n, 2) vertices, not closed
    cum: np.ndarray = field(repr=False)  # (n + 1,) s at each vertex; cum[n] == length

    @classmethod
    def from_coords(cls, coords: Iterable[Sequence[float]]) -> "Ring":
        """The ring through `coords` (closed or not, any winding), counter-clockwise and
        starting at the vertex with the lowest x, then the lowest n."""
        points = np.asarray([(float(c[0]), float(c[1])) for c in coords], dtype=float)
        if len(points) >= 2 and np.array_equal(points[0], points[-1]):
            points = points[:-1]
        if len(points) < 3:
            raise ValueError("a ring needs at least 3 points")
        if _signed_area(points) < 0:
            points = points[::-1]
        start = min(range(len(points)), key=lambda i: (points[i, 0], points[i, 1]))
        points = np.roll(points, -start, axis=0)
        closed = np.vstack([points, points[:1]])
        lengths = np.hypot(*np.diff(closed, axis=0).T)
        if np.any(lengths <= 0):
            raise ValueError("a ring can't repeat a point")
        cum = np.concatenate([[0.0], np.cumsum(lengths)])
        return cls(points, cum)

    @classmethod
    def rectangle(cls, min_x: float, min_y: float, max_x: float, max_y: float) -> "Ring":
        return cls.from_coords([(min_x, min_y), (max_x, min_y), (max_x, max_y), (min_x, max_y)])

    @property
    def n(self) -> int:
        return len(self.xy)

    @property
    def length(self) -> float:
        return float(self.cum[-1])

    @property
    def vertex_s(self) -> np.ndarray:
        """s of every vertex (the first is 0)."""
        return self.cum[:-1]

    def polygon(self) -> Polygon:
        return Polygon(self.xy)

    def line(self) -> LineString:
        """The ring as a closed line."""
        return LineString(np.vstack([self.xy, self.xy[:1]]))

    def segment(self, i: int) -> tuple[np.ndarray, np.ndarray]:
        i %= self.n
        return self.xy[i], self.xy[(i + 1) % self.n]

    def wrap(self, s: float) -> float:
        """`s` moved into [0, length)."""
        value = math.fmod(s, self.length)
        if value < 0:
            value += self.length
        return 0.0 if value >= self.length else value

    def segment_index(self, s: float) -> int:
        """The segment containing `s` (a vertex belongs to the segment it starts)."""
        s = self.wrap(s)
        i = int(np.searchsorted(self.cum, s, side="right")) - 1
        return min(max(i, 0), self.n - 1)

    def direction(self, i: int) -> np.ndarray:
        a, b = self.segment(i)
        d = b - a
        return np.asarray(d / np.hypot(*d))

    def normal(self, i: int) -> np.ndarray:
        """The outward unit normal of segment i (right of the direction, CCW ring)."""
        d = self.direction(i)
        return np.array([d[1], -d[0]])

    def point_at(self, s: float) -> np.ndarray:
        s = self.wrap(s)
        i = self.segment_index(s)
        a, _ = self.segment(i)
        return np.asarray(a + self.direction(i) * (s - self.cum[i]))

    def points_at(self, s: np.ndarray) -> np.ndarray:
        return np.array([self.point_at(float(v)) for v in np.asarray(s, dtype=float)])

    def vertex_at(self, s: float, tolerance: float = 1e-6) -> int | None:
        """The vertex at `s` (within `tolerance`), or None."""
        s = self.wrap(s)
        for i, v in enumerate(self.vertex_s):
            if abs(s - v) <= tolerance or abs(s - v - self.length) <= tolerance:
                return i
        return None

    def outward_at(self, s: float) -> np.ndarray:
        """The unit vector pointing out of the polygon at `s`: the segment's normal, or at
        a vertex the bisector of the two segments' normals."""
        vertex = self.vertex_at(s)
        if vertex is None:
            return self.normal(self.segment_index(s))
        n = self.normal(vertex - 1) + self.normal(vertex)
        norm = float(np.hypot(*n))
        if norm < 1e-12:  # a full turn back: no bisector
            return self.normal(vertex)
        return np.asarray(n / norm)

    def tangent_at(self, s: float) -> np.ndarray:
        """The unit direction of the ring at `s` (at a vertex: perpendicular to
        `outward_at`, so the two stay consistent)."""
        out = self.outward_at(s)
        return np.array([-out[1], out[0]])

    def project(self, x: float, y: float) -> float:
        """s of the point on the ring nearest to (x, y)."""
        p = np.array([x, y], dtype=float)
        a = self.xy
        b = np.roll(self.xy, -1, axis=0)
        d = b - a
        lengths2 = (d**2).sum(axis=1)
        t = np.clip(((p - a) * d).sum(axis=1) / lengths2, 0.0, 1.0)
        nearest = a + d * t[:, None]
        dist = np.hypot(*(nearest - p).T)
        i = int(np.argmin(dist))
        return self.wrap(float(self.cum[i] + t[i] * math.sqrt(lengths2[i])))

    def path(self, a: float, b: float) -> np.ndarray:
        """The points of the ring from s = a to s = b (b > a, may pass the start), with
        every vertex in between."""
        inner = sorted(
            float(v + k * self.length)
            for k in range(-1, 3)
            for v in self.vertex_s
            if a + EPS < v + k * self.length < b - EPS
        )
        return np.array([self.point_at(v) for v in (a, *inner, b)])

    def path_line(self, a: float, b: float) -> LineString:
        return LineString(self.path(a, b))

    def intervals(
        self, geometry: BaseGeometry, min_length: float = 0.0, tolerance: float = ON_RING_M
    ) -> list[Interval]:
        """The stretches of the ring inside `geometry` or within `tolerance` of it (closed:
        its boundary counts), as merged circular intervals. Stretches shorter than
        `min_length` are dropped.

        The tolerance makes an edge of `geometry` that lies on the ring, but zigzags across
        it by floating-point noise, cover the ring along its whole length.
        """
        if geometry.is_empty:
            return []
        shapely.prepare(geometry)
        raw: list[Interval] = []
        for i in range(self.n):
            a, b = self.segment(i)
            seg = LineString([a, b])
            if tolerance > 0:
                if not geometry.dwithin(seg, tolerance):
                    continue
                # Only the part of `geometry` near the segment is grown, so this stays cheap.
                near = geometry.intersection(seg.buffer(2 * tolerance))
                hit = seg.intersection(near.buffer(tolerance))
            else:
                if not geometry.intersects(seg):
                    continue
                hit = seg.intersection(geometry)
            seg_len = float(self.cum[i + 1] - self.cum[i])
            for part in _lines(hit):
                coords = np.asarray(part.coords)
                t = [seg.project(Point(c)) for c in (coords[0], coords[-1])]
                lo, hi = min(t), max(t)
                lo, hi = max(lo, 0.0), min(hi, seg_len)
                if hi > lo:
                    raw.append((float(self.cum[i] + lo), float(self.cum[i] + hi)))
        merged = merge_touching(raw, self.length)
        return [iv for iv in merged if iv[1] - iv[0] >= min_length]

    def contains_s(self, interval: Interval, s: float, tolerance: float = 0.0) -> bool:
        """Whether `s` lies in the circular interval (widened by `tolerance`)."""
        a, b = interval
        offset = self.wrap(s - a + tolerance)
        return offset <= b - a + 2 * tolerance

    def complement(self, intervals: Sequence[Interval]) -> list[Interval]:
        """The stretches not covered by `intervals` (which must not overlap)."""
        if not intervals:
            return [(0.0, self.length)]
        ordered = sorted(intervals)
        result: list[Interval] = []
        for (_, b), (a_next, _) in zip(
            ordered, [*ordered[1:], (ordered[0][0] + self.length, 0.0)], strict=True
        ):
            if a_next - b > EPS:
                start = self.wrap(b)
                result.append((start, start + (a_next - b)))
        return sorted(result)


def _signed_area(points: np.ndarray) -> float:
    x, y = points[:, 0], points[:, 1]
    return 0.5 * float(np.dot(x, np.roll(y, -1)) - np.dot(np.roll(x, -1), y))


def _lines(geometry: BaseGeometry) -> list[LineString]:
    if geometry.is_empty:
        return []
    if isinstance(geometry, LineString):
        return [geometry] if geometry.length > 0 else []
    return [g for part in getattr(geometry, "geoms", []) for g in _lines(part)]


def merge_touching(
    intervals: Sequence[Interval], length: float, gap: float = EPS
) -> list[Interval]:
    """Linear intervals in [0, length] merged where they overlap or are `gap` apart, then
    joined over the ring's start when one starts at 0 and another ends at `length`."""
    if not intervals:
        return []
    ordered = sorted(intervals)
    merged: list[list[float]] = [list(ordered[0])]
    for a, b in ordered[1:]:
        if a <= merged[-1][1] + gap:
            merged[-1][1] = max(merged[-1][1], b)
        else:
            merged.append([a, b])
    if len(merged) > 1 and merged[0][0] <= gap and merged[-1][1] >= length - gap:
        first = merged.pop(0)
        merged[-1][1] = first[1] + length
    elif len(merged) == 1 and merged[0][0] <= gap and merged[0][1] >= length - gap:
        return [(0.0, length)]
    return [(float(a), float(b)) for a, b in merged]


def interval_gap(first: Interval, second: Interval, length: float) -> float:
    """The distance along the ring from the end of `first` forward to the start of
    `second` (negative when they overlap)."""
    a1, b1 = first
    a2, _ = second
    start2 = a2
    while start2 < a1 - EPS:
        start2 += length
    return start2 - b1


def merge_two(first: Interval, second: Interval, length: float) -> Interval:
    """The stretch from `first`'s start forward over `second`'s end."""
    a1, b1 = first
    a2, b2 = second
    shift = 0.0
    while a2 + shift < a1 - EPS:
        shift += length
    end = max(b1, b2 + shift)
    return (a1, min(end, a1 + length))
