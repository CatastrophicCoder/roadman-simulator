"""Bridge deck heights and extents, and bridges inferred over underpasses.

Every bridge span (tagged, inferred over water, inferred over an underpass) gets one
straight line (spec: docs/specs/roads-and-road-graph.md, "Deck heights", "Deck extent",
"Underpasses"):

- **Lidar fit.** The `lidar_fit.classes` points inside the deck footprint are binned every
  `bin_m` along the span and the top point of each bin is kept (points under the deck,
  e.g. a cycleway in a cutting, are lower and drop out). The line through two bins that
  most bins agree with (within `band_m`) is refitted by least squares to those bins. It is
  used with at least `min_bins` agreeing bins that are at least `min_inlier_share` of all.
  The deck surface is the fitted line + `surface_offset_m`.
- **Geometric fallback.** Tagged and water spans: each end's height is its rim, the
  highest road surface (terrain mesh + offset, across the deck width) from the end out to
  `rim_search_m` along the one connected ground way (at a junction or a dead end: at the
  end itself). Underpasses: the line through the highest road surface within
  `rim_search_m` on each side of the crossing.
- **Extent.** "Road surface" at a point is the highest terrain + offset across the deck
  width there, so a deck end is never below the road beside it. From each OSM end the deck
  extends along the one connected ground way until its line meets the road surface (at
  most `rim_search_m`, never past a node another way uses); a `span_end` node is inserted
  there. Where the line is below the road surface at an OSM end, that end is raised to
  the road surface (the deck keeps covering the whole OSM way, and meets the road there).
  Junction ends and ends where the line doesn't meet the road stay at the OSM node, and
  their ramp takes up the difference.
- **Underpasses.** A way without a bridge or tunnel tag that crosses a tunnel way, or a
  way with a lower `layer`, without a shared node, gets a span around the crossing: from
  where its line comes out of the road surface on one side to the other, both ends
  `span_end` nodes. If the road doesn't dip there, no bridge is added.
- A span end at an area-edge cut, or with no connected ground road, takes the terrain
  mesh height + offset there; the other end keeps the line's height.

All decisions are made on the pieces as clipped, then the `span_end` vertices are
inserted, the final `Span`s are built and each piece's `decks` ranges are set.
"""

from collections import Counter
from collections.abc import Callable, Sequence
from dataclasses import dataclass, field
from typing import Any

import numpy as np
import shapely
import shapely.ops
from shapely.geometry import LineString

from mapgen.build.roads.bridges import Span, SpanPart, set_centre_line
from mapgen.build.roads.graph import BridgeSource, Piece, arc_lengths, insert_vertices, span_key
from mapgen.build.roads.ramps import GradedRamp, Ramp
from mapgen.build.roads.surface import TerrainSurface
from mapgen.config import BridgeConfig
from mapgen.sources.base import PointCloud

SAMPLE_M = 0.5  # spacing of the road-surface samples along a search
GRADED_SAMPLE_M = 0.25  # spacing of the samples along a graded ramp's path
MAX_PATHS = 64  # branching searches (walk-only decks) follow at most this many paths
BISECT_M = 1e-4  # where the line meets the road surface, to this precision


# Positions along pieces.


def point_at(piece: Piece, pos: float) -> np.ndarray:
    arc = arc_lengths(piece.xy)
    k = int(np.clip(np.searchsorted(arc, pos, side="right") - 1, 0, len(arc) - 2))
    t = 0.0 if arc[k + 1] == arc[k] else (pos - arc[k]) / (arc[k + 1] - arc[k])
    return piece.xy[k] + t * (piece.xy[k + 1] - piece.xy[k])


@dataclass
class Axis:
    """A path along pieces: segments (piece, pos0, pos1) in order, each within one piece."""

    segments: list[tuple[Piece, float, float]]
    xy: np.ndarray = field(init=False)
    u: np.ndarray = field(init=False)

    def __post_init__(self) -> None:
        pts = [point_at(self.segments[0][0], self.segments[0][1])]
        pts += [point_at(p, b) for p, _, b in self.segments]
        self.xy = np.array(pts)
        self.u = arc_lengths(self.xy)

    @property
    def length(self) -> float:
        return float(self.u[-1])

    def segment_of(self, u: float, prefer_end: bool = False) -> int:
        """The segment holding distance u (at a vertex: the later one, or the earlier one
        with `prefer_end`)."""
        k = int(np.searchsorted(self.u, u, side="left" if prefer_end else "right") - 1)
        return int(np.clip(k, 0, len(self.segments) - 1))

    def point(self, u: float) -> np.ndarray:
        k = self.segment_of(u)
        a, b = self.xy[k], self.xy[k + 1]
        span = self.u[k + 1] - self.u[k]
        t = 0.0 if span == 0 else (u - self.u[k]) / span
        return a + np.clip(t, 0.0, 1.0) * (b - a)

    def direction(self, u: float, prefer_end: bool = False) -> np.ndarray:
        k = self.segment_of(u, prefer_end)
        d = self.xy[k + 1] - self.xy[k]
        n = float(np.hypot(*d))
        return d / n if n > 0 else np.array([1.0, 0.0])

    def locate(self, u: float) -> tuple[Piece, float]:
        """The piece and the position along it at distance u."""
        k = self.segment_of(u)
        piece, p0, p1 = self.segments[k]
        span = self.u[k + 1] - self.u[k]
        t = 0.0 if span == 0 else (u - self.u[k]) / span
        return piece, p0 + np.clip(t, 0.0, 1.0) * (p1 - p0)

    def width(self, u: float) -> float:
        return self.segments[self.segment_of(u)][0].attrs.width_m


def part_axis_segments(part: SpanPart) -> list[tuple[Piece, float, float]]:
    arc = arc_lengths(part.piece.xy)
    idx = part.indices()
    return [
        (part.piece, float(arc[a]), float(arc[b])) for a, b in zip(idx[:-1], idx[1:], strict=True)
    ]


# The road surface across a deck.


class RoadSurface:
    """The highest terrain mesh + offset across a deck width, perpendicular to a direction."""

    def __init__(self, terrain: TerrainSurface, offset: float) -> None:
        self.terrain, self.offset = terrain, offset

    def across(self, centre: np.ndarray, direction: np.ndarray, width: float) -> float:
        n = np.array([-direction[1], direction[0]]) * width / 2
        return self.terrain.max_along(centre + n, centre - n) + self.offset

    def on_axis(self, axis: Axis, u: float, prefer_end: bool = False) -> float:
        return self.across(axis.point(u), axis.direction(u, prefer_end), axis.width(u))


@dataclass(frozen=True)
class Line:
    """z = a + b * u along an axis."""

    a: float
    b: float

    def z(self, u: float | np.ndarray) -> Any:
        return self.a + self.b * np.asarray(u, dtype=float)

    @classmethod
    def through(cls, u0: float, z0: float, u1: float, z1: float) -> "Line":
        b = 0.0 if u1 == u0 else (z1 - z0) / (u1 - u0)
        return cls(z0 - b * u0, b)


def first_meeting(f: Callable[[float], float], u_from: float, u_to: float) -> float | None:
    """The first u from `u_from` toward `u_to` where f(u) >= 0, refined to `BISECT_M` and
    returned on the side where f < 0. None if f stays < 0. f(u_from) must be < 0."""
    direction = 1.0 if u_to >= u_from else -1.0
    total = abs(u_to - u_from)
    steps = max(int(np.ceil(total / SAMPLE_M)), 1)
    prev = u_from
    for k in range(1, steps + 1):
        u = u_from + direction * min(k * SAMPLE_M, total)
        if f(u) >= 0:
            lo, hi = prev, u  # f(lo) < 0 <= f(hi)
            while abs(hi - lo) > BISECT_M:
                mid = (lo + hi) / 2
                if f(mid) >= 0:
                    hi = mid
                else:
                    lo = mid
            return lo
        prev = u
    return None


# Lidar fit.


@dataclass
class Fit:
    line: Line  # deck surface (lidar + offset) along the axis
    points: int
    bins: int
    agreeing: int
    max_residual: float


def lidar_fit(
    axis: Axis,
    u_lo: float,
    u_hi: float,
    points: PointCloud | None,
    cfg: BridgeConfig,
    offset: float,
) -> tuple[Fit | None, dict[str, Any]]:
    """The consensus line of the top points per bin, or None (with the counts)."""
    info: dict[str, Any] = {"points": 0, "bins": 0, "bins_agreeing": 0, "max_residual_m": None}
    if points is None or len(points) == 0 or u_hi - u_lo < cfg.lidar_fit.bin_m:
        return None, info
    line = LineString(axis.xy)
    part = shapely.ops.substring(line, u_lo, u_hi)
    width = max(p.attrs.width_m for p, _, _ in axis.segments)
    poly = part.buffer(width / 2, cap_style="flat")
    min_x, min_y, max_x, max_y = poly.bounds
    m = (points.x >= min_x) & (points.x <= max_x) & (points.y >= min_y) & (points.y <= max_y)
    m &= np.isin(points.classification, np.array(cfg.lidar_fit.classes, dtype=np.uint8))
    x, y, z = points.x[m], points.y[m], points.z[m]
    inside = shapely.contains_xy(poly, x, y)
    x, y, z = x[inside], y[inside], z[inside]
    info["points"] = int(len(z))
    if len(z) == 0:
        return None, info
    s = shapely.line_locate_point(line, shapely.points(np.column_stack([x, y])))
    bins = np.floor((s - u_lo) / cfg.lidar_fit.bin_m).astype(int)
    order = np.lexsort((-z, bins))
    first = np.concatenate([[True], bins[order][1:] != bins[order][:-1]])
    top_s, top_z = s[order][first], z[order][first]
    n = len(top_s)
    info["bins"] = n
    if n < cfg.lidar_fit.min_bins:
        return None, info
    band = cfg.lidar_fit.band_m
    best: np.ndarray | None = None
    for i in range(n):
        for j in range(i + 1, n):
            if top_s[j] - top_s[i] < 1.0:
                continue
            slope = (top_z[j] - top_z[i]) / (top_s[j] - top_s[i])
            agree = np.abs(top_z - (top_z[i] + slope * (top_s - top_s[i]))) <= band
            if best is None or agree.sum() > best.sum():
                best = agree
    if best is None:
        return None, info
    b, a = np.polyfit(top_s[best], top_z[best], 1)
    residual = np.abs(top_z[best] - (a + b * top_s[best]))
    info["bins_agreeing"] = int(best.sum())
    info["max_residual_m"] = round(float(residual.max()), 3)
    if best.sum() < cfg.lidar_fit.min_bins or best.sum() < cfg.lidar_fit.min_inlier_share * n:
        return None, info
    fit = Fit(
        Line(float(a) + offset, float(b)), int(len(z)), n, int(best.sum()), float(residual.max())
    )
    return fit, info


# Plans.


@dataclass
class EndPlan:
    key: str  # the node where the deck ends (span_end keys are made when inserted)
    kind: str  # "span_end", "osm" (deck ends at the OSM node), "cut", "dead_end"
    reason: str | None = None  # osm ends: "junction", "no_meeting", "met_at_node"
    extension_m: float = 0.0  # + outward along the road, - shortened
    u: float = 0.0  # the final end along the plan's axis
    anchor: tuple[Piece, float] | None = None  # where a span_end vertex goes
    walk: list[tuple[Piece, float, float]] = field(default_factory=list)  # added deck
    raised_m: float = 0.0  # how far this end was raised from the line to the road
    searched_m: float | None = None  # how far along the road the meeting was searched
    search_stop: str | None = None  # why the search stopped: limit, node, end, deck
    walk_only: bool = False
    end_info: dict[str, Any] | None = None  # the search state (walk-only graded ramps)
    graded: dict[str, Any] | None = None  # a graded ramp along the path, or why it can't fit


@dataclass
class Plan:
    source: BridgeSource
    axis: Axis  # the OSM span (tagged, water) or the window around the crossing
    core: tuple[float, float]  # OSM ends (tagged, water) or the crossings (underpass)
    line: Line | None = None
    method: str = "geometric"
    fallback_reason: str | None = None
    lidar: dict[str, Any] = field(default_factory=dict)
    lidar_ends: list[float] | None = None
    geometric_ends: list[float] = field(default_factory=list)
    ends: list[EndPlan] = field(default_factory=list)
    water: list[str] = field(default_factory=list)
    crossing: tuple[float, float] | None = None
    lower_ways: list[int] = field(default_factory=list)
    span: Span | None = None  # the final span

    @property
    def way_ids(self) -> list[int]:
        seen: list[int] = []
        for piece, _, _ in self.axis.segments:
            if piece.way_id not in seen:
                seen.append(piece.way_id)
        return seen


class DeckPlanner:
    def __init__(
        self,
        pieces: Sequence[Piece],
        terrain: TerrainSurface,
        cfg: BridgeConfig,
        offset: float,
        points: PointCloud | None,
    ) -> None:
        self.pieces = sorted(pieces, key=lambda p: (p.way_id, p.index))
        self.terrain, self.cfg, self.offset, self.points = terrain, cfg, offset, points
        self.surface = RoadSurface(terrain, offset)
        self.uses: Counter[str] = Counter(k for p in self.pieces for k in p.keys)
        # Deck intervals already claimed per piece (by id): walks stop at them.
        self.claimed: dict[int, list[tuple[float, float]]] = {}

    # Helpers.

    def claim(self, piece: Piece, a: float, b: float) -> None:
        self.claimed.setdefault(id(piece), []).append((min(a, b), max(a, b)))

    def is_claimed(self, piece: Piece, pos: float) -> bool:
        return any(lo < pos < hi for lo, hi in self.claimed.get(id(piece), []))

    def stops_walk(self, piece: Piece, i: int) -> bool:
        """A vertex the deck never extends past: another way's node, a cut, an edge node."""
        return self.uses[piece.keys[i]] > 1 or piece.at_area_edge(i)

    def continuations(self, key: str) -> list[tuple[Piece, int, int]]:
        """Ground (piece, vertex, direction) leaving the node `key`, not along a deck."""
        result = []
        for piece in self.pieces:
            if piece.attrs.bridge or piece.attrs.tunnel or key not in piece.keys:
                continue
            arc = arc_lengths(piece.xy)
            for i, k in enumerate(piece.keys):
                if k != key:
                    continue
                for d in (1, -1):
                    j = i + d
                    if 0 <= j < len(piece.keys) and not self.is_claimed(
                        piece, (arc[i] + arc[j]) / 2
                    ):
                        result.append((piece, i, d))
        return result

    def walk(self, piece: Piece, i: int, d: int, limit: float) -> tuple[Axis, str]:
        """The ground road from vertex i in direction d, at most `limit` long.

        It follows the road across joints (a node where exactly one other ground way goes
        on) and stops at a junction or another way's node (included), a cut, a deck or the
        road's end.
        """
        start_piece, start_arc = piece, arc_lengths(piece.xy)[i]
        segments: list[tuple[Piece, float, float]] = []
        total, j, stop = 0.0, i, "limit"
        visited = {id(piece)}
        arc = arc_lengths(piece.xy)
        while True:
            k = j + d
            if not (0 <= k < len(piece.keys)):
                key = piece.keys[j]
                onward = [c for c in self.continuations(key) if id(c[0]) not in visited]
                if (
                    key.startswith(("cut:", "span:"))
                    or piece.at_area_edge(j)
                    or len(onward) != 1
                    or self.uses[key] != 2
                ):
                    stop = "end" if not onward else "node"
                    break
                piece, j, d = onward[0]
                visited.add(id(piece))
                arc = arc_lengths(piece.xy)
                continue
            step = abs(arc[k] - arc[j])
            mid = (arc[j] + arc[k]) / 2
            if self.is_claimed(piece, mid):
                stop = "deck"
                break
            if total + step >= limit:
                frac = (limit - total) / step if step > 0 else 0.0
                segments.append((piece, arc[j], arc[j] + (arc[k] - arc[j]) * frac))
                break
            segments.append((piece, arc[j], arc[k]))
            total += step
            j = k
            at_piece_end = not (0 <= j + d < len(piece.keys))
            if self.stops_walk(piece, j) and not (at_piece_end and self.uses[piece.keys[j]] == 2):
                stop = "node"
                break
        if not segments:
            return Axis([(start_piece, start_arc, start_arc)]), stop
        return Axis(segments), stop

    def branch_paths(self, piece: Piece, i: int, d: int, limit: float) -> list[tuple[Axis, str]]:
        """Every ground path from vertex i in direction d, up to `limit` long, branching at
        joints and junctions (walk-only decks).

        Depth first, in a fixed order (the current way first, then the other ways in way
        order, direction +1 before -1), at most `MAX_PATHS` paths. A path stops at a cut,
        an area-edge node, a deck, the road's end or `limit`; it never revisits a piece.
        """
        found: list[tuple[Axis, str]] = []

        def emit(segments: list[tuple[Piece, float, float]], stop: str) -> None:
            if segments and len(found) < MAX_PATHS:
                found.append((Axis(segments), stop))

        def go(
            piece: Piece,
            j: int,
            d: int,
            total: float,
            segments: list[tuple[Piece, float, float]],
            visited: frozenset[int],
        ) -> None:
            arc = arc_lengths(piece.xy)
            while True:
                if len(found) >= MAX_PATHS:
                    return
                k = j + d
                if not (0 <= k < len(piece.keys)):
                    break
                step = abs(arc[k] - arc[j])
                if self.is_claimed(piece, (arc[j] + arc[k]) / 2):
                    emit(segments, "deck")
                    return
                if total + step >= limit:
                    frac = (limit - total) / step if step > 0 else 0.0
                    emit(segments + [(piece, arc[j], arc[j] + (arc[k] - arc[j]) * frac)], "limit")
                    return
                segments = segments + [(piece, arc[j], arc[k])]
                total += step
                j = k
                key = piece.keys[j]
                if key.startswith(("cut:", "span:")) or piece.at_area_edge(j):
                    emit(segments, "cut")
                    return
                if self.uses[key] > 1:
                    break  # a joint or junction: branch below
            key = piece.keys[j]
            options: list[tuple[Piece, int, int]] = []
            if 0 <= j + d < len(piece.keys):
                options.append((piece, j, d))
            options += [c for c in self.continuations(key) if id(c[0]) not in visited]
            if not options:
                emit(segments, "end")
                return
            for q, jj, dd in options:
                go(q, jj, dd, total, segments, visited | {id(q)})

        go(piece, i, d, 0.0, [], frozenset({id(piece)}))
        return found

    # Lines.

    def rim_beyond(
        self, end_axis: Axis | None, end_point: np.ndarray, end_dir: np.ndarray, width: float
    ) -> float:
        """The highest road surface from an end out along its continuation (or at it)."""
        best = self.surface.across(end_point, end_dir, width)
        if end_axis is not None and end_axis.length > 0:
            reach = min(end_axis.length, self.cfg.rim_search_m)  # rims within rim_search_m
            for u in np.arange(SAMPLE_M, reach + 1e-9, SAMPLE_M):
                best = max(best, self.surface.on_axis(end_axis, min(u, reach), True))
        return best

    def choose_line(self, plan: Plan, geometric: Line, u_lo: float, u_hi: float) -> None:
        fit, info = lidar_fit(plan.axis, u_lo, u_hi, self.points, self.cfg, self.offset)
        plan.lidar = info
        if fit is not None:
            plan.line, plan.method = fit.line, "lidar"
        else:
            plan.line, plan.method = geometric, "geometric"
            plan.fallback_reason = "no point cloud" if self.points is None else "fit rejected"

    # Tagged and water spans.

    def plan_span(self, span: Span) -> Plan:
        segments = [s for part in span.parts for s in part_axis_segments(part)]
        axis = Axis(segments)
        plan = Plan(span.source, axis, (0.0, axis.length), water=span.water, crossing=span.crossing)
        for piece, a, b in segments:
            self.claim(piece, a, b)
        length = axis.length
        start_key, end_key = span.end_keys()
        walk_only = not any(part.piece.attrs.drivable for part in span.parts)
        ends: list[dict[str, Any]] = []
        for key, u0, outward_sign in ((start_key, 0.0, -1.0), (end_key, length, 1.0)):
            point = axis.point(u0)
            direction = axis.direction(u0, prefer_end=u0 > 0) * outward_sign
            width = axis.width(u0)
            piece_at = axis.locate(u0)[0]
            idx = piece_at.keys.index(key) if key in piece_at.keys else 0
            # The span's own segments are claimed, so they are never a continuation.
            conts = [] if key.startswith("cut:") else self.continuations(key)
            walk_axis, stop = (None, "none")
            paths: list[tuple[Axis, str]] = []
            if walk_only:  # across junctions too, the longer search
                for cont in conts:
                    paths += self.branch_paths(*cont, self.cfg.walk_rim_search_m)
            elif len(conts) == 1:
                walk_axis, stop = self.walk(*conts[0], self.cfg.rim_search_m)
                paths = [(walk_axis, stop)]
            ends.append(
                {
                    "key": key,
                    "u": u0,
                    "sign": outward_sign,
                    "point": point,
                    "dir": direction,
                    "width": width,
                    "conts": conts,
                    "walk": walk_axis,
                    "stop": stop,
                    "paths": paths,
                    "walk_only": walk_only,
                    "cut": key.startswith("cut:") or piece_at.at_area_edge(idx),
                }
            )
        rims = [
            (
                self.terrain.height(e["point"][None])[0] + self.offset
                if e["cut"] or not e["conts"]
                else max(
                    [self.rim_beyond(None, e["point"], e["dir"], e["width"])]
                    + [
                        self.rim_beyond(path, e["point"], e["dir"], e["width"])
                        for path, _ in e["paths"]
                    ]
                )
            )
            for e in ends
        ]
        geometric = Line.through(0.0, rims[0], length, rims[1])
        plan.geometric_ends = [round(float(r), 3) for r in rims]
        self.choose_line(plan, geometric, 0.0, length)
        if plan.method == "lidar":
            assert plan.line is not None
            plan.lidar_ends = [
                round(float(plan.line.z(0.0)), 3),
                round(float(plan.line.z(length)), 3),
            ]
        plan.line = self.pin_free_ends(plan.line, ends, length)
        plan.line, raised = self.raise_ends(plan.line, ends, length)
        results = self.place_ends(plan, ends)
        assert results is not None
        for end, lift in zip(results, raised, strict=True):
            end.raised_m = lift
        plan.ends = results
        for end in results:
            if end.walk_only and end.reason == "no_meeting" and end.end_info is not None:
                end.graded = self.plan_graded(end.end_info, float(plan.line.z(end.u)))
        return plan

    def plan_graded(self, e: dict[str, Any], z_end: float) -> dict[str, Any] | None:
        """A gradient-limited ramp along the path from a walk-only deck end that meets no
        path: the path (of every branch from the end, in search order) on which the
        descent at `max_ramp_grade` meets terrain + offset first. None if the deck end
        isn't above the road there."""
        grade = self.cfg.max_ramp_grade
        base0 = float(self.terrain.height(e["point"][None])[0]) + self.offset
        dz = z_end - base0
        if dz <= 0:
            return None
        nominal = dz / grade
        limit = nominal + self.cfg.walk_rim_search_m
        edge = Ramp(
            e["key"],
            (float(e["point"][0]), float(e["point"][1])),
            (float(e["dir"][0]), float(e["dir"][1])),
            e["width"] / 2,
            z_end,
            self.cfg.ramp_length_m,
        )
        probe = GradedRamp(edge, grade, ((0.0, 0.0), (1.0, 0.0)), 0.0, 0.0)
        paths: list[tuple[Axis, str]] = []
        for cont in e["conts"]:
            paths += self.branch_paths(*cont, limit)
        best: dict[str, Any] | None = None
        for path, _ in paths:
            us = np.arange(0.0, path.length + 1e-9, GRADED_SAMPLE_M)
            xy = np.array([path.point(min(u, path.length)) for u in us])
            above = probe.height(xy) > self.terrain.height(xy) + self.offset
            if above.all():
                continue  # the descent doesn't reach the terrain on this path
            fit = float(us[int(np.argmin(above))])
            if best is not None and fit >= best["length_m"]:
                continue
            last = float(us[np.flatnonzero(above)[-1]]) if above.any() else 0.0
            half = path.segments[0][0].attrs.width_m / 2
            best = {
                "fits": True,
                "length_m": round(fit, 2),
                "nominal_length_m": round(nominal, 2),
                "height_difference_m": round(dz, 3),
                "grade": grade,
                "path": tuple((float(x), float(y)) for x, y in path.xy),
                "path_half_width_m": half,
                "zone_length_m": min(last + GRADED_SAMPLE_M + half, path.length),
                "edge": edge,
            }
        if best is None:
            longest = max((p.length for p, _ in paths), default=0.0)
            stops = sorted({stop for _, stop in paths}) or ["none"]
            return {
                "fits": False,
                "reason": f"the path ends before the ramp meets the terrain ({', '.join(stops)})",
                "height_difference_m": round(dz, 3),
                "available_m": round(longest, 2),
                "needed_grade": round(dz / longest, 3) if longest > 0 else None,
            }
        return best

    def raise_ends(
        self, line: Line, ends: list[dict[str, Any]], length: float
    ) -> tuple[Line, list[float]]:
        """Where the line is below the road surface at an OSM end (connected to a ground
        road), raise that end to the road surface, so the deck covers the whole OSM way and
        meets the road there without a step. Returns the line and how far each end rose."""
        z = [float(line.z(0.0)), float(line.z(length))]
        raised = [0.0, 0.0]
        for _ in range(2):  # raising one end changes the line at the other
            current = Line.through(0.0, z[0], length, z[1])
            for k, e in enumerate(ends):
                if e["cut"] or not e["conts"]:
                    continue
                road = self.surface.across(e["point"], e["dir"], e["width"])
                if road > float(current.z(e["u"])):
                    raised[k] += road - z[k]
                    z[k] = road
        return Line.through(0.0, z[0], length, z[1]), [round(r, 3) for r in raised]

    def pin_free_ends(self, line: Line | None, ends: list[dict[str, Any]], length: float) -> Line:
        """Ends at a cut or without a connected ground road take terrain + offset."""
        assert line is not None
        z = [float(line.z(0.0)), float(line.z(length))]
        for k, e in enumerate(ends):
            if e["cut"] or not e["conts"]:
                z[k] = float(self.terrain.height(e["point"][None])[0]) + self.offset
        return Line.through(0.0, z[0], length, z[1])

    def place_ends(self, plan: Plan, ends: list[dict[str, Any]]) -> list[EndPlan]:
        assert plan.line is not None
        line = plan.line
        results: list[EndPlan] = []
        for e in ends:
            if e["cut"]:
                results.append(EndPlan(e["key"], "cut", u=e["u"]))
                continue
            if not e["conts"]:
                results.append(EndPlan(e["key"], "dead_end", u=e["u"]))
                continue

            def at_end(u_out: float, e: dict[str, Any] = e) -> float:  # u_out >= 0 outward
                return e["u"] + e["sign"] * u_out

            f0 = self.surface.across(e["point"], e["dir"], e["width"]) - float(line.z(e["u"]))
            if f0 >= -BISECT_M:  # the deck meets the road at the OSM end (raised there)
                results.append(EndPlan(e["key"], "osm", "met_at_node", u=e["u"]))
                continue
            if len(e["conts"]) != 1 and not e["walk_only"]:
                results.append(EndPlan(e["key"], "osm", "junction", u=e["u"]))
                continue
            u_line0 = e["u"]
            # The nearest meeting point along any path (the first path wins a tie).
            best: tuple[float, Axis] | None = None
            for walk, _ in e["paths"]:

                def f(s: float, walk: Axis = walk, e: dict[str, Any] = e, f0: float = f0) -> float:
                    if s <= 0:
                        return f0
                    return self.surface.on_axis(walk, s, prefer_end=True) - float(
                        line.z(e["u"] + e["sign"] * s)
                    )

                limit = walk.length if best is None else min(walk.length, best[0])
                found = first_meeting(f, 0.0, limit) if limit > 0 else None
                if found is not None and (best is None or found < best[0]):
                    best = (found, walk)
            if best is None:
                end = EndPlan(e["key"], "osm", "no_meeting", u=u_line0)
                longest = max((w for w, _ in e["paths"]), key=lambda w: w.length, default=None)
                end.searched_m = round(longest.length, 2) if longest is not None else 0.0
                end.search_stop = ",".join(sorted({stop for _, stop in e["paths"]})) or "none"
                end.walk_only = e["walk_only"]
                end.end_info = e
                results.append(end)
                continue
            s_star, walk = best
            piece, pos = walk.locate(s_star)
            done = [(p, a, b) for p, a, b in walk.segments]
            added = _cut_segments(done, walk.u, s_star)
            for p, a, b in added:
                self.claim(p, a, b)
            results.append(
                EndPlan(
                    "",
                    "span_end",
                    None,
                    s_star,
                    u=e["u"] + e["sign"] * s_star,
                    anchor=(piece, pos),
                    walk=added,
                )
            )
        return results

    # Underpasses.

    def find_underpasses(self) -> list[tuple[Piece, float, list[int], tuple[float, float]]]:
        """(upper piece, crossing position along it, lower way ids, point) per crossing."""
        lines = [LineString(p.xy) for p in self.pieces]
        tree = shapely.STRtree(lines)
        found: list[tuple[Piece, float, list[int], tuple[float, float]]] = []
        for i, upper in enumerate(self.pieces):
            if upper.attrs.bridge or upper.attrs.tunnel:
                continue
            arc = arc_lengths(upper.xy)
            for j in sorted(tree.query(lines[i], predicate="intersects")):
                lower = self.pieces[j]
                if lower is upper or lower.attrs.bridge:
                    continue
                if not (lower.attrs.tunnel or lower.attrs.layer < upper.attrs.layer):
                    continue
                hit = lines[i].intersection(lines[j])
                pts = [g for g in getattr(hit, "geoms", [hit]) if g.geom_type == "Point"]
                shared = {k for k in upper.keys if not k.startswith("cut:")} & set(lower.keys)
                shared_xy = [upper.xy[upper.keys.index(k)] for k in shared]
                for pt in pts:
                    xy = np.array([pt.x, pt.y])
                    if any(np.hypot(*(xy - s)) < 1e-6 for s in shared_xy):
                        continue
                    pos = float(lines[i].project(pt))
                    if self.is_claimed(upper, pos) or pos <= 0 or pos >= arc[-1]:
                        continue
                    found.append((upper, pos, [lower.way_id], (float(pt.x), float(pt.y))))
        return found

    def plan_underpasses(self) -> tuple[list[Plan], list[dict[str, Any]]]:
        by_piece: dict[int, list[tuple[Piece, float, list[int], tuple[float, float]]]] = {}
        for item in self.find_underpasses():
            by_piece.setdefault(id(item[0]), []).append(item)
        plans: list[Plan] = []
        skipped: list[dict[str, Any]] = []
        R = self.cfg.rim_search_m
        for items in by_piece.values():
            items.sort(key=lambda t: t[1])
            groups: list[list[Any]] = []
            for item in items:  # crossings closer than 2 R share one span
                if groups and item[1] - groups[-1][-1][1] < 2 * R:
                    groups[-1].append(item)
                else:
                    groups.append([item])
            for group in groups:
                plan = self.plan_underpass(group)
                if plan is None:
                    skipped.append(
                        {
                            "osm_way": f"osm:way/{group[0][0].way_id}",
                            "under": [f"osm:way/{w}" for g in group for w in g[2]],
                            "reason": "no dip at the crossing",
                        }
                    )
                else:
                    plans.append(plan)
        return plans, skipped

    def window(self, piece: Piece, pos: float, sign: int) -> tuple[float, str]:
        """How far the search may go from pos in direction sign: R, a node another way
        uses, a claimed deck or the piece end."""
        arc = arc_lengths(piece.xy)
        limit = pos + sign * self.cfg.rim_search_m
        order = range(len(arc)) if sign > 0 else range(len(arc) - 1, -1, -1)
        for i in order:
            beyond = arc[i] > pos if sign > 0 else arc[i] < pos
            if not beyond:
                continue
            if (sign > 0 and arc[i] >= limit) or (sign < 0 and arc[i] <= limit):
                return limit, "limit"
            if self.stops_walk(piece, i):
                return float(arc[i]), "node"
            if self.is_claimed(piece, (arc[i] + pos) / 2):
                return float(arc[i]), "deck"
        end = float(arc[-1]) if sign > 0 else 0.0
        if (sign > 0 and end >= limit) or (sign < 0 and end <= limit):
            return limit, "limit"
        return end, "end"

    def plan_underpass(self, group: list[Any]) -> Plan | None:
        piece = group[0][0]
        first, last = group[0][1], group[-1][1]
        lo, lo_stop = self.window(piece, first, -1)
        hi, hi_stop = self.window(piece, last, 1)
        axis = Axis([(piece, lo, hi)] if lo < hi else [(piece, lo, lo)])
        if axis.length <= 0:
            return None
        # Re-split the axis at the piece's vertices, so directions follow the way.
        arc = arc_lengths(piece.xy)
        cuts = [lo, *[float(a) for a in arc if lo < a < hi], hi]
        axis = Axis([(piece, a, b) for a, b in zip(cuts[:-1], cuts[1:], strict=True)])
        u_first, u_last = first - lo, last - lo

        def surface(u: float) -> float:
            return self.surface.on_axis(axis, u)

        us = np.arange(0.0, axis.length + 1e-9, SAMPLE_M)
        heights = np.array([surface(min(u, axis.length)) for u in us])
        left, right = us <= u_first, us >= u_last
        i = int(np.argmax(np.where(left, heights, -np.inf)))
        j = int(np.argmax(np.where(right, heights, -np.inf)))
        geometric = Line.through(float(us[i]), float(heights[i]), float(us[j]), float(heights[j]))
        plan = Plan("inferred_underpass", axis, (u_first, u_last))
        plan.lower_ways = sorted({w for g in group for w in g[2]})
        plan.crossing = group[0][3]
        self.choose_line(plan, geometric, 0.0, axis.length)
        if plan.line is None:
            return None
        for line in [plan.line, geometric] if plan.method == "lidar" else [geometric]:
            ends = self.underpass_ends(axis, line, u_first, u_last, (lo_stop, hi_stop), piece, lo)
            if ends is not None:
                if line is geometric and plan.method == "lidar":
                    plan.method, plan.fallback_reason = "geometric", "lidar line below the road"
                plan.line = line
                plan.ends = ends
                break
        else:
            return None
        plan.geometric_ends = [round(float(geometric.z(e.u)), 3) for e in plan.ends]
        if plan.method == "lidar":
            plan.lidar_ends = [round(float(plan.line.z(e.u)), 3) for e in plan.ends]
        a = min(e.u for e in plan.ends) + lo
        b = max(e.u for e in plan.ends) + lo
        self.claim(piece, a, b)
        return plan

    def underpass_ends(
        self,
        axis: Axis,
        line: Line,
        u_first: float,
        u_last: float,
        stops: tuple[str, str],
        piece: Piece,
        lo: float,
    ) -> list[EndPlan] | None:
        def f(u: float) -> float:
            return self.surface.on_axis(axis, u) - float(line.z(u))

        if f(u_first) >= 0 or f(u_last) >= 0:
            return None  # no dip under the line at the crossing
        ends: list[EndPlan] = []
        for u_from, u_to, stop in ((u_first, 0.0, stops[0]), (u_last, axis.length, stops[1])):
            u = first_meeting(f, u_from, u_to)
            if u is not None:
                ends.append(
                    EndPlan("", "span_end", None, abs(u - u_from), u=u, anchor=(piece, lo + u))
                )
            elif stop in ("node", "end", "deck"):
                p, pos = axis.locate(u_to)
                arc = arc_lengths(piece.xy)
                i = int(np.argmin(np.abs(arc - pos)))
                kind = "cut" if piece.at_area_edge(i) else "osm"
                ends.append(EndPlan(piece.keys[i], kind, "no_meeting", abs(u_to - u_from), u=u_to))
            else:
                ends.append(
                    EndPlan(
                        "",
                        "span_end",
                        "no_meeting",
                        abs(u_to - u_from),
                        u=u_to,
                        anchor=(piece, lo + u_to),
                    )
                )
        return ends

    # Building the final spans.

    def finish(self, plans: Sequence[Plan]) -> None:
        """Insert the span_end vertices, build the final spans and set the pieces' decks."""
        requests: dict[int, list[tuple[float, EndPlan]]] = {}
        piece_of: dict[int, Piece] = {}
        for plan in plans:
            for end in plan.ends:
                if end.anchor is not None:
                    piece, pos = end.anchor
                    requests.setdefault(id(piece), []).append((pos, end))
                    piece_of[id(piece)] = piece
        # Positions of every axis segment, to re-resolve after the insertions.
        counters: Counter[int] = Counter()
        for piece in self.pieces:  # way order: stable span_end ids
            if id(piece) not in requests:
                continue
            items = sorted(requests[id(piece)], key=lambda t: t[0])
            keys = []
            for _ in items:
                keys.append(span_key(piece.way_id, counters[piece.way_id]))
                counters[piece.way_id] += 1
            insert_vertices(piece, [pos for pos, _ in items], keys)
            arc = arc_lengths(piece.xy)
            for pos, end in items:
                end.key = piece.keys[int(np.argmin(np.abs(arc - pos)))]
        for plan in plans:
            plan.span = self.final_span(plan)

    def final_span(self, plan: Plan) -> Span:
        start, end = plan.ends
        u_start, u_end = start.u, end.u
        segments: list[tuple[Piece, float, float]] = []
        # Before the axis start (an extension at the start, walked outward: reversed).
        segments += [(p, b, a) for p, a, b in reversed(start.walk)]
        axis = plan.axis
        u_lo, u_hi = (
            (max(u_start, 0.0) if not start.walk else 0.0),
            (min(u_end, axis.length) if not end.walk else axis.length),
        )
        for k, (piece, p0, p1) in enumerate(axis.segments):
            a_u, b_u = axis.u[k], axis.u[k + 1]
            lo, hi = max(a_u, u_lo), min(b_u, u_hi)
            if hi - lo <= 1e-9 and not (a_u == b_u):
                continue
            t0 = (lo - a_u) / (b_u - a_u) if b_u > a_u else 0.0
            t1 = (hi - a_u) / (b_u - a_u) if b_u > a_u else 0.0
            segments.append((piece, p0 + t0 * (p1 - p0), p0 + t1 * (p1 - p0)))
        segments += list(end.walk)
        parts: list[SpanPart] = []
        for piece, a, b in segments:
            arc = arc_lengths(piece.xy)
            i0 = int(np.argmin(np.abs(arc - a)))
            i1 = int(np.argmin(np.abs(arc - b)))
            if i0 == i1:
                continue
            if parts and parts[-1].piece is piece and parts[-1].forward == (i1 > i0):
                last = parts[-1]
                if last.forward and last.end == i0:
                    last.end = i1
                    continue
                if not last.forward and last.start == i0:
                    last.start = i1
                    continue
            parts.append(SpanPart(piece, min(i0, i1), max(i0, i1), i1 > i0))
        span = Span("bridge", plan.source, parts)
        span.water, span.crossing = plan.water, plan.crossing
        set_centre_line(span)
        assert plan.line is not None
        span.fixed_z = (float(plan.line.z(u_start)), float(plan.line.z(u_end)))
        for part in parts:
            part.piece.decks.append((part.start, part.end, plan.source))
        return span


def _cut_segments(
    segments: list[tuple[Piece, float, float]], u: np.ndarray, s: float
) -> list[tuple[Piece, float, float]]:
    """The segments of a walk up to distance s."""
    result = []
    for k, (piece, a, b) in enumerate(segments):
        if u[k] >= s:
            break
        if u[k + 1] <= s:
            result.append((piece, a, b))
        else:
            t = (s - u[k]) / (u[k + 1] - u[k])
            result.append((piece, a, a + t * (b - a)))
    return result


def plan_decks(
    pieces: Sequence[Piece],
    tagged: Sequence[Span],
    water: Sequence[Span],
    terrain: TerrainSurface,
    cfg: BridgeConfig,
    offset: float,
    points: PointCloud | None,
) -> tuple[list[Plan], list[dict[str, Any]]]:
    """All bridge spans with their lines and ends; inserts span_end vertices and sets the
    pieces' `decks`. Returns the plans (with `.span`) and the underpasses left out."""
    planner = DeckPlanner(pieces, terrain, cfg, offset, points)
    for span in [*tagged, *water]:  # claim the OSM spans first, so walks stop at them
        for part in span.parts:
            for piece, a, b in part_axis_segments(part):
                planner.claim(piece, a, b)
    plans = [planner.plan_span(span) for span in [*tagged, *water]]
    underpasses, skipped = planner.plan_underpasses()
    plans += underpasses
    planner.finish(plans)
    # Tunnel pieces that pass under a deck are draped like ground ways.
    footprints = [deck_outline(p.span) for p in plans if p.span is not None]
    if footprints:
        decks = shapely.union_all(footprints)
        for piece in pieces:
            if piece.attrs.tunnel and LineString(piece.xy).intersects(decks):
                piece.under_deck = True
    return plans, skipped


def deck_outline(span: Span) -> Any:
    width = max(part.piece.attrs.width_m for part in span.parts)
    return LineString(span.xy).buffer(width / 2, cap_style="flat")


# Dip report.


def _in_deck(piece: Piece, pos: float) -> bool:
    arc = arc_lengths(piece.xy)
    return any(arc[a] - 1e-9 <= pos <= arc[b] + 1e-9 for a, b, _ in piece.decks)


def dip_depth(
    piece: Piece, pos: float, terrain: TerrainSurface, offset: float, search: float
) -> float | None:
    """How far the road surface dips below the line between its rims (the highest surface
    within `search` on each side of `pos`, along the piece), or None near the piece ends."""
    arc = arc_lengths(piece.xy)
    lo, hi = max(0.0, pos - search), min(float(arc[-1]), pos + search)
    if pos - lo < 1.0 or hi - pos < 1.0:
        return None
    us = np.unique(np.concatenate([np.arange(lo, hi, 1.0), [pos, hi]]))
    xy = np.array([point_at(piece, u) for u in us])
    h = terrain.height(xy) + offset
    left, right = us <= pos, us >= pos
    i = int(np.argmax(np.where(left, h, -np.inf)))
    j = int(np.argmax(np.where(right, h, -np.inf)))
    if us[j] - us[i] <= 0:
        return None
    k = (us >= us[i]) & (us <= us[j])
    line = h[i] + (h[j] - h[i]) * (us[k] - us[i]) / (us[j] - us[i])
    return float((line - h[k]).max())


def suspected_dips(
    pieces: Sequence[Piece], terrain: TerrainSurface, offset: float, cfg: BridgeConfig
) -> list[dict[str, Any]]:
    """Crossings without a shared node where a way that isn't on a deck (and isn't under
    one) dips more than `dip_report_m` below the line between its rims."""
    ordered = sorted(pieces, key=lambda p: (p.way_id, p.index))
    lines = [LineString(p.xy) for p in ordered]
    tree = shapely.STRtree(lines)
    found: list[dict[str, Any]] = []
    for i, a in enumerate(ordered):
        for j in sorted(tree.query(lines[i], predicate="intersects")):
            if j <= i:
                continue
            b = ordered[j]
            hit = lines[i].intersection(lines[j])
            pts = [g for g in getattr(hit, "geoms", [hit]) if g.geom_type == "Point"]
            shared = set(a.keys) & set(b.keys)
            shared_xy = [a.xy[a.keys.index(k)] for k in shared]
            for pt in pts:
                xy = np.array([pt.x, pt.y])
                if any(np.hypot(*(xy - s)) < 1e-6 for s in shared_xy):
                    continue
                pos = {id(a): float(lines[i].project(pt)), id(b): float(lines[j].project(pt))}
                for way, other in ((a, b), (b, a)):
                    if way.tunnel_span or other.tunnel_span:
                        continue
                    if _in_deck(way, pos[id(way)]) or _in_deck(other, pos[id(other)]):
                        continue
                    depth = dip_depth(way, pos[id(way)], terrain, offset, cfg.rim_search_m)
                    if depth is not None and depth > cfg.dip_report_m:
                        found.append(
                            {
                                "way": way,
                                "other": other,
                                "depth_m": round(depth, 2),
                                "point": (float(pt.x), float(pt.y)),
                            }
                        )
    return found
