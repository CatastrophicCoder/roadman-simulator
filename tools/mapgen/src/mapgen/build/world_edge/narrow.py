"""Narrow notches and spikes of the boundary ring (`world_edge.min_feature_width_m`).

A boundary can be routed so that the playable area (a spike) or the outside (a notch)
gets very narrow near the ring: two fence lines a few decimetres apart, or a wedge
between a notch wall and the build area edge. There, barrier pieces on both sides
collide, and the void trigger, which starts `void.inner_offset_m` outside the whole ring,
can't fill the gap. So the build refuses such a boundary before the pack is touched.

The rule, for W = `min_feature_width_m`: the boundary has a narrow feature where two of
its points p and q are less than W apart while the ring between them (the shorter way
round) is more than `DETOUR` (2) times as long as |pq|. The ratio |pq| / ring length is
the chord of a corner over its two legs: for a corner of angle θ it is at least
sin(θ / 2), so a corner of 60° or more never fires, and neither does any stretch whose
direction turns by 120° or less in total (a straight jog, a chamfered corner). A spike or
notch narrower than W and deeper than about half its width always fires; a 4 m wide
notch with W = 3 m doesn't (its walls are 4 m apart). Where the segment pq runs through
the playable area the feature is a spike, else a notch. Corners sharper than 60° (in the
playable area or outside) are spikes or notches of their own.

Pairs on the same segment can't fire. Pairs on adjacent segments fire exactly when the
corner between them is sharper than 60°. Other segment pairs less than W apart are
sampled every `SAMPLE_M` (at most `MAX_SAMPLES` points per segment, ends included) on the
part of each segment within W of the other. Firing pairs whose ring stretches overlap are
one feature, reported at its narrowest pair.
"""

import math
from dataclasses import dataclass
from typing import Literal

import numpy as np
import shapely
from shapely.geometry import LineString

from mapgen.build.world_edge.ring import Ring

DETOUR = 2.0  # ring length over gap: 1 / sin(60° / 2)
MIN_CORNER_DEG = 60.0  # corners at least this wide never fire (DETOUR's angle)
SAMPLE_M = 0.01
MAX_SAMPLES = 2000
FeatureKind = Literal["spike", "notch"]


@dataclass(frozen=True)
class NarrowFeature:
    """A narrow spike (of the playable area) or notch (of the outside) of the ring."""

    kind: FeatureKind
    width_m: float  # the narrowest gap found (0 for a sharp corner: see `angle_deg`)
    point: tuple[float, float]  # local (x, n): the gap's midpoint, or the corner
    a: tuple[float, float]  # the gap's ends on the ring (both the corner for a corner)
    b: tuple[float, float]
    s_start: float  # the ring stretch of the feature (may pass the ring's start)
    s_end: float
    angle_deg: float | None = None  # a corner sharper than MIN_CORNER_DEG: its angle


def _interior_angle(ring: Ring, i: int) -> float:
    """The playable area's angle at vertex i in degrees (0..360, CCW ring)."""
    d0, d1 = ring.direction(i - 1), ring.direction(i)
    turn = math.degrees(math.atan2(d0[0] * d1[1] - d0[1] * d1[0], float(d0 @ d1)))
    return 180.0 - turn


def _samples(a: np.ndarray, b: np.ndarray, near: LineString, width: float) -> np.ndarray:
    """Distances along segment a-b of sample points on its part within `width` of `near`
    (ends of that part included)."""
    seg = LineString([a, b])
    part = seg.intersection(near.buffer(width, quad_segs=16))
    if part.is_empty:
        return np.empty(0)
    coords = shapely.get_coordinates(part)
    t = np.array([seg.project(shapely.Point(c)) for c in coords])
    lo, hi = float(t.min()), float(t.max())
    count = int(min(MAX_SAMPLES, max(2, math.ceil((hi - lo) / SAMPLE_M) + 1)))
    return np.linspace(lo, hi, count)


def _ring_between(ring: Ring, s0: float, s1: float) -> tuple[float, float]:
    """The shorter stretch between two s values, as (start, end) with end > start (end
    may pass the ring's length)."""
    lo, hi = min(s0, s1), max(s0, s1)
    if hi - lo <= ring.length - (hi - lo):
        return lo, hi
    return hi, lo + ring.length


@dataclass
class _Hit:
    width: float
    p: np.ndarray
    q: np.ndarray
    s_start: float
    s_end: float
    angle: float | None = None


def _pair_hit(ring: Ring, i: int, j: int, width: float) -> _Hit | None:
    """The narrowest firing pair between segments i and j (not adjacent), or None."""
    a_i, b_i = ring.segment(i)
    a_j, b_j = ring.segment(j)
    line_i, line_j = LineString([a_i, b_i]), LineString([a_j, b_j])
    t = _samples(a_i, b_i, line_j, width)
    u = _samples(a_j, b_j, line_i, width)
    if not len(t) or not len(u):
        return None
    p = a_i + np.outer(t, ring.direction(i))
    q = a_j + np.outer(u, ring.direction(j))
    gap = np.hypot(p[:, None, 0] - q[None, :, 0], p[:, None, 1] - q[None, :, 1])
    forward = (ring.cum[j] + u)[None, :] - (ring.cum[i] + t)[:, None]
    forward = np.mod(forward, ring.length)
    along = np.minimum(forward, ring.length - forward)
    fire = (gap < width) & (along > DETOUR * gap)
    if not fire.any():
        return None
    gap = np.where(fire, gap, np.inf)
    k, m = np.unravel_index(int(np.argmin(gap)), gap.shape)
    s0, s1 = _ring_between(ring, ring.cum[i] + t[k], ring.cum[j] + u[m])
    return _Hit(float(gap[k, m]), p[k], q[m], s0, s1)


def narrow_features(ring: Ring, min_width_m: float) -> list[NarrowFeature]:
    """Every narrow spike and notch of `ring` (see the module docstring), in ring order.
    None when `min_width_m` is 0."""
    if min_width_m <= 0:
        return []
    hits: list[_Hit] = []
    for i in range(ring.n):
        angle = _interior_angle(ring, i)
        sharp = min(angle, 360.0 - angle)
        if sharp < MIN_CORNER_DEG:
            # The legs are closer than W up to this far from the corner.
            reach = min_width_m / (2 * math.sin(math.radians(max(sharp, 1e-9) / 2)))
            previous = ring.cum[i] - ring.cum[i - 1] if i > 0 else ring.length - ring.cum[-2]
            before = min(reach, float(previous))
            after = min(reach, float(ring.cum[i + 1] - ring.cum[i]))
            v = ring.xy[i]
            s = float(ring.cum[i])
            start = s - before if s - before >= 0 else s - before + ring.length
            hits.append(_Hit(0.0, v, v, start, start + before + after, angle))
    segments = [LineString(ring.segment(i)) for i in range(ring.n)]
    tree = shapely.STRtree(segments)
    left, right = tree.query(segments, predicate="dwithin", distance=min_width_m)
    for i, j in zip(left.tolist(), right.tolist(), strict=True):
        if j <= i or (j - i) % ring.n in (1, ring.n - 1):
            continue  # each pair once; same or adjacent segments are handled above
        hit = _pair_hit(ring, i, j, min_width_m)
        if hit is not None:
            hits.append(hit)
    return [_feature(ring, group) for group in _groups(ring, hits)]


def _groups(ring: Ring, hits: list[_Hit]) -> list[list[_Hit]]:
    """Hits whose ring stretches overlap (or touch), as groups in ring order."""
    order = sorted(hits, key=lambda h: (h.s_start, h.s_end))
    groups: list[list[_Hit]] = []
    end = -math.inf
    for hit in order:
        if groups and hit.s_start <= end + 1e-9:
            groups[-1].append(hit)
            end = max(end, hit.s_end)
        else:
            groups.append([hit])
            end = hit.s_end
    # A group over the ring's start joins the first one.
    if len(groups) > 1 and end - ring.length >= groups[0][0].s_start - 1e-9:
        groups[0] = groups.pop() + groups[0]
    return groups


def _feature(ring: Ring, group: list[_Hit]) -> NarrowFeature:
    best = min(group, key=lambda h: (h.width, h.s_start))
    # The group starts at its first hit; hits after a join over the ring's start (lower
    # s_start) are moved one ring length on.
    start = group[0].s_start
    end = max(h.s_end + (ring.length if h.s_start < start - 1e-9 else 0.0) for h in group)
    if best.angle is not None:
        kind: FeatureKind = "spike" if best.angle < 180.0 else "notch"
        angle = best.angle if best.angle < 180.0 else 360.0 - best.angle
    else:
        middle = (best.p + best.q) / 2
        kind = "spike" if ring.polygon().contains(shapely.Point(middle)) else "notch"
        angle = None
    middle = (best.p + best.q) / 2
    return NarrowFeature(
        kind=kind,
        width_m=best.width,
        point=(float(middle[0]), float(middle[1])),
        a=(float(best.p[0]), float(best.p[1])),
        b=(float(best.q[0]), float(best.q[1])),
        s_start=float(start),
        s_end=float(end),
        angle_deg=angle,
    )
