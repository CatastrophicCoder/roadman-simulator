"""Bridge and tunnel spans, inferred bridges over water, bridge decks and the clearance check.

- **Spans.** A span is a chain of bridge (or tunnel) way pieces joined at OSM nodes that
  exactly two of the chain's piece ends use and no other included way uses. The two
  carriageways of a dual carriageway are therefore separate spans.
- **Straight line.** A span's heights run linearly, by horizontal distance along its
  centre line, from the road surface height at one end to that at the other end. The deck
  never follows the terrain under it.
- **Inferred bridges.** A way without a `bridge` or `tunnel` tag whose segments cross a
  water area's interior or an inferred waterway line gets a span from the last node before
  the water to the first node after it (or to the area-edge cut). Culverts (waterways with
  a `tunnel` tag), waterways with a negative `layer`, crossings at `ford=yes` nodes and
  `ford=yes` ways are excluded and counted.
- **Decks.** A slab of the way's width and `deck_thickness_m`, one quad per centre-line
  segment (with wedges at bends), and a box railing along each long edge. The driving
  surface's vertices are on the straight line; where a bend makes the projection onto the
  centre line ambiguous, a vertex is raised to the higher of the two line heights.
- **Clearance.** The deck underside is compared with the terrain mesh along the centre
  line and both edges. The ends of a deck always meet the ground, so only the part between
  the first and the last sample with enough clearance is checked; a deck that never gets
  `min_clearance_m` above the terrain is reported with its smallest clearance.
"""

from collections import defaultdict
from collections.abc import Mapping, Sequence
from dataclasses import dataclass, field
from typing import Any, Literal

import numpy as np
import shapely
from shapely.geometry import LineString, Point
from shapely.geometry.base import BaseGeometry

from mapgen.build.area import BuildArea
from mapgen.build.roads.graph import Piece
from mapgen.build.roads.ramps import Ramp, RampField
from mapgen.build.roads.surface import TerrainSurface
from mapgen.build.roads.tags import is_tunnel, parse_layer
from mapgen.build.stage1_load import LocalWater
from mapgen.config import BridgeConfig

SpanKind = Literal["bridge", "tunnel"]


@dataclass
class SpanPart:
    """Vertices `start..end` (inclusive, in way order) of a piece, walked forward or back."""

    piece: Piece
    start: int
    end: int
    forward: bool

    def indices(self) -> list[int]:
        idx = list(range(self.start, self.end + 1))
        return idx if self.forward else idx[::-1]


@dataclass
class Span:
    kind: SpanKind
    source: Literal["osm", "inferred"]
    parts: list[SpanPart]
    xy: np.ndarray = field(default_factory=lambda: np.zeros((0, 2)))  # centre line
    s: np.ndarray = field(default_factory=lambda: np.zeros(0))  # distance along it
    z0: float = 0.0
    z1: float = 0.0
    water: list[str] = field(default_factory=list)  # inferred: the water features crossed
    crossing: tuple[float, float] | None = None  # inferred: where it crosses first

    @property
    def length(self) -> float:
        return float(self.s[-1]) if len(self.s) else 0.0

    def line_z(self, s: np.ndarray | float) -> np.ndarray:
        """The straight line's height at distances `s` along the centre line."""
        s = np.asarray(s, dtype=float)
        if self.length <= 0:
            return np.full(s.shape, self.z0)
        return self.z0 + (self.z1 - self.z0) * np.clip(s / self.length, 0.0, 1.0)

    @property
    def way_ids(self) -> list[int]:
        seen: list[int] = []
        for part in self.parts:
            if part.piece.way_id not in seen:
                seen.append(part.piece.way_id)
        return seen

    def end_keys(self) -> tuple[str, str]:
        first, last = self.parts[0], self.parts[-1]
        return first.piece.keys[first.indices()[0]], last.piece.keys[last.indices()[-1]]


def _centre_line(parts: Sequence[SpanPart]) -> np.ndarray:
    points: list[np.ndarray] = []
    for k, part in enumerate(parts):
        xy = part.piece.xy[part.indices()]
        points.append(xy if k == 0 else xy[1:])
    return np.concatenate(points)


def tagged_spans(pieces: Sequence[Piece], kind: SpanKind) -> list[Span]:
    """Chains of bridge (or tunnel) pieces, joined at nodes no other included way uses."""

    def wanted(p: Piece) -> bool:
        return p.attrs.bridge if kind == "bridge" else p.attrs.tunnel

    units = sorted((p for p in pieces if wanted(p)), key=lambda p: (p.way_id, p.index))
    edge_nodes = {p.keys[i] for p in pieces for i in p.edge_nodes}
    uses: defaultdict[str, int] = defaultdict(int)
    for piece in pieces:
        for key in piece.keys:
            uses[key] += 1
    ends: defaultdict[str, list[tuple[int, bool]]] = defaultdict(list)  # key -> (unit, at_end)
    for u, piece in enumerate(units):
        ends[piece.keys[0]].append((u, False))
        ends[piece.keys[-1]].append((u, True))

    def joinable(key: str) -> bool:
        if key.startswith("cut:") or key in edge_nodes:
            return False
        return len(ends[key]) == 2 and uses[key] == 2

    used: set[int] = set()
    spans: list[Span] = []

    def walk(start_unit: int, forward: bool) -> list[SpanPart]:
        parts: list[SpanPart] = []
        unit, fwd = start_unit, forward
        while unit not in used:
            used.add(unit)
            piece = units[unit]
            parts.append(SpanPart(piece, 0, len(piece.xy) - 1, fwd))
            exit_key = piece.keys[-1] if fwd else piece.keys[0]
            if not joinable(exit_key):
                break
            others = [e for e in ends[exit_key] if e[0] != unit]
            if not others:
                break  # a closed bridge way
            unit, fwd = others[0][0], not others[0][1]  # entered at its end: walk backwards
        return parts

    # Start at units with an end that can't be joined, then whatever is left (loops).
    for u, piece in enumerate(units):
        if u in used:
            continue
        if not joinable(piece.keys[0]):
            spans.append(Span(kind, "osm", walk(u, True)))
        elif not joinable(piece.keys[-1]):
            spans.append(Span(kind, "osm", walk(u, False)))
    for u in range(len(units)):
        if u not in used:
            spans.append(Span(kind, "osm", walk(u, True)))
    return spans


# Inferred bridges.


@dataclass
class WaterIndex:
    """The water features that count as crossings, and the excluded ones (with a reason)."""

    features: list[LocalWater]
    reasons: list[str | None]  # None: counts; else "culvert" or "negative_layer"
    tree: shapely.STRtree

    @classmethod
    def build(
        cls, areas: Sequence[LocalWater], lines: Sequence[LocalWater], waterways: Sequence[str]
    ) -> "WaterIndex":
        features = list(areas) + [w for w in lines if w.tags.get("waterway") in waterways]
        features.sort(key=lambda w: w.id)
        reasons: list[str | None] = []
        for w in features:
            if is_tunnel(w.tags):
                reasons.append("culvert")
            elif parse_layer(w.tags) < 0:
                reasons.append("negative_layer")
            else:
                reasons.append(None)
        return cls(features, reasons, shapely.STRtree([w.geometry for w in features]))


def _is_area(feature: LocalWater) -> bool:
    return feature.geometry.geom_type in ("Polygon", "MultiPolygon")


@dataclass
class Crossing:
    way_id: int
    water: str
    position: tuple[float, float]
    reason: str | None  # None: an inferred bridge; else why it was excluded


def _segment_crossings(
    piece: Piece, water: WaterIndex
) -> list[tuple[int, int, tuple[float, float], str | None]]:
    """(segment, feature, position, exclusion reason) for each segment that meets water."""
    xy = piece.xy
    if len(water.features) == 0 or len(xy) < 2:
        return []
    segments = shapely.linestrings(np.stack([xy[:-1], xy[1:]], axis=1))
    pairs = water.tree.query(segments, predicate="intersects")
    fords = {
        i
        for i, n in enumerate(piece.node_ids)
        if n is not None and piece.way.node_tags.get(n, {}).get("ford") == "yes"
    }
    way_ford = piece.way.tags.get("ford") == "yes"
    last = len(xy) - 1
    result = []
    for seg, f in sorted(zip(pairs[0].tolist(), pairs[1].tolist(), strict=True)):
        feature = water.features[f]
        segment = segments[seg]
        if _is_area(feature):
            if not segment.relate_pattern(feature.geometry, "1********"):
                continue  # only touches the boundary
            inside = shapely.intersection(segment, feature.geometry)
            point = inside.interpolate(0.5, normalized=True)
            at_ford = any(
                i in fords and feature.geometry.covers(Point(xy[i])) for i in (seg, seg + 1)
            )
        else:
            hit = shapely.intersection(segment, feature.geometry)
            hits = [np.asarray(g.coords)[0] for g in getattr(hit, "geoms", [hit])]
            hits = [h for h in hits if len(h)]
            if not hits:
                continue
            # A way that ends on the water line only touches it.
            ends = [i for i in (seg, seg + 1) if i in (0, last) and not piece.at_area_edge(i)]
            if all(any((h == xy[i]).all() for i in ends) for h in hits):
                continue
            point = Point(hits[0])
            at_ford = all(
                any((h == xy[i]).all() for i in (seg, seg + 1) if i in fords) for h in hits
            )
        reason = water.reasons[f]
        if reason is None and way_ford:
            reason = "ford_way"
        elif reason is None and at_ford:
            reason = "ford_node"
        result.append((seg, f, (float(point.x), float(point.y)), reason))
    return result


def infer_spans(pieces: Sequence[Piece], water: WaterIndex) -> tuple[list[Span], list[Crossing]]:
    """Inferred bridge spans (also stored in each piece's `inferred`), and every crossing.

    Only pieces without a bridge or tunnel tag are checked.
    """
    spans: list[Span] = []
    crossings: list[Crossing] = []
    for piece in sorted(pieces, key=lambda p: (p.way_id, p.index)):
        if piece.attrs.bridge or piece.attrs.tunnel:
            continue
        wet: dict[int, list[int]] = defaultdict(list)
        first_hit: dict[int, tuple[float, float]] = {}
        for seg, f, position, reason in _segment_crossings(piece, water):
            crossings.append(Crossing(piece.way_id, water.features[f].id, position, reason))
            if reason is None:
                wet[seg].append(f)
                first_hit.setdefault(seg, position)
        segs = sorted(wet)
        runs: list[list[int]] = []
        for seg in segs:
            if runs and runs[-1][-1] == seg - 1:
                runs[-1].append(seg)
            else:
                runs.append([seg])
        for run in runs:
            start, end = run[0], run[-1] + 1
            piece.inferred.append((start, end))
            features = sorted({water.features[f].id for seg in run for f in wet[seg]})
            span = Span("bridge", "inferred", [SpanPart(piece, start, end, True)])
            span.water = features
            span.crossing = first_hit[run[0]]
            spans.append(span)
    return spans, crossings


# Heights.


def set_span_heights(
    span: Span,
    terrain: TerrainSurface,
    offset: float,
    fixed: Mapping[str, float] | None = None,
    field: RampField | None = None,
) -> None:
    """The span's centre line, its end heights (road surface) and its vertices' heights.

    An end's height is the road surface there: terrain + offset, with the ramp `field`
    applied (a deck end with a ramp gets the ramp's `z_end`). An end node in `fixed` (a
    node inside another span) takes that height instead.
    """
    set_centre_line(span)
    ends = terrain.height(span.xy[[0, -1]]) + offset
    if field is not None:
        ends = field.apply(span.xy[[0, -1]], ends)
    fixed = fixed or {}
    start, end = span.end_keys()
    span.z0 = float(fixed.get(start, ends[0]))
    span.z1 = float(fixed.get(end, ends[1]))
    k = 0
    for n, part in enumerate(span.parts):
        idx = part.indices()
        if n > 0:
            k -= 1  # the joint vertex is shared with the previous part
        part.piece.z[idx] = span.line_z(span.s[k : k + len(idx)])
        k += len(idx)


def interior_heights(span: Span) -> dict[str, float]:
    """Height on the span's line of each node inside it (not at its two ends)."""
    result: dict[str, float] = {}
    first, last = span.end_keys()
    k = 0
    for n, part in enumerate(span.parts):
        idx = part.indices()
        if n > 0:
            k -= 1
        for j, i in enumerate(idx):
            key = part.piece.keys[i]
            if key not in (first, last):
                result.setdefault(key, float(span.line_z(span.s[k + j])))
        k += len(idx)
    return result


def interior_heights_keys(span: Span) -> list[str]:
    """The keys of the nodes inside the span (not at its two ends), in order."""
    first, last = span.end_keys()
    keys: list[str] = []
    for part in span.parts:
        for i in part.indices():
            key = part.piece.keys[i]
            if key not in (first, last) and key not in keys:
                keys.append(key)
    return keys


def terrain_above_deck(
    span: Span, terrain: TerrainSurface, spacing: float = 0.25
) -> dict[str, Any]:
    """Where the terrain mesh rises above the deck's driving surface inside its footprint.

    Sampled on a `spacing` grid: the highest rise, the area where the terrain is above the
    surface, and the distance of the highest rise from the nearer deck end.
    """
    footprint = deck_footprint(span)
    min_x, min_y, max_x, max_y = footprint.bounds
    gx, gy = np.meshgrid(
        np.arange(min_x + spacing / 2, max_x, spacing),
        np.arange(min_y + spacing / 2, max_y, spacing),
    )
    pts = np.column_stack([gx.ravel(), gy.ravel()])
    pts = pts[shapely.contains_xy(footprint, pts[:, 0], pts[:, 1])]
    entry: dict[str, Any] = {"osm_ways": [f"osm:way/{w}" for w in span.way_ids]}
    if len(pts) == 0:
        return {**entry, "max_rise_m": None, "area_m2": 0.0, "from_end_m": None}
    s = shapely.line_locate_point(LineString(span.xy), shapely.points(pts))
    rise = terrain.height(pts) - span.line_z(s)
    above = rise > 0
    worst = int(np.argmax(rise))
    return {
        **entry,
        "max_rise_m": round(float(rise[worst]), 3),
        "area_m2": round(float(above.sum()) * spacing * spacing, 2),
        "from_end_m": round(float(min(s[worst], span.length - s[worst])), 2),
    }


def settle_span_heights(
    spans: Sequence[Span],
    pieces: Sequence[Piece],
    terrain: TerrainSurface,
    offset: float,
    field: RampField | None = None,
) -> dict[str, float]:
    """Heights of every span, and of every piece vertex at a node inside a span.

    The heights of nodes inside a span are on its line, for every way that uses them
    (e.g. a path joining a bridge in its middle), so each edge ends exactly at its node.
    A span that ends at a node inside another span starts from that span's line. The
    first span (in list order) that contains a node decides its height. Returns the
    heights of the span-interior nodes.
    """
    for span in spans:
        set_span_heights(span, terrain, offset, field=field)
    owner: dict[str, int] = {}
    for n, span in enumerate(spans):
        for key in interior_heights(span):
            owner.setdefault(key, n)
    for n, span in enumerate(spans):
        fixed = {
            key: interior_heights(spans[owner[key]])[key]
            for key in span.end_keys()
            if key in owner and owner[key] != n
        }
        if fixed:
            set_span_heights(span, terrain, offset, fixed, field)
    interior = {key: interior_heights(spans[n])[key] for key, n in owner.items()}
    for piece in pieces:
        for i, key in enumerate(piece.keys):
            if key in interior:
                piece.z[i] = interior[key]
    return interior


def deck_footprint(span: Span) -> BaseGeometry:
    """The span's deck outline from above (centre line buffered flat to half its width)."""
    width = max(part.piece.attrs.width_m for part in span.parts)
    return LineString(span.xy).buffer(width / 2, cap_style="flat")


def set_centre_line(span: Span) -> None:
    """The span's centre line and the distances along it (no heights)."""
    span.xy = _centre_line(span.parts)
    steps = np.linalg.norm(np.diff(span.xy, axis=0), axis=1)
    span.s = np.concatenate([[0.0], np.cumsum(steps)])


def build_ramps(
    bridges: Sequence[Span],
    ramp_ends: set[str],
    terrain: TerrainSurface,
    offset: float,
    length: float,
) -> list[Ramp]:
    """A ramp at every bridge span end in `ramp_ends` (ends that a ground way meets).

    The end edge is perpendicular to the span's end segment and as wide as the way at
    that end; its height is the highest terrain + offset along it (see `ramps`).
    """
    ramps: list[Ramp] = []
    for span in bridges:
        set_centre_line(span)
        if len(span.xy) < 2:
            continue
        start, end = span.end_keys()
        for key, k, inward, part in (
            (start, 0, 1, span.parts[0]),
            (end, len(span.xy) - 1, len(span.xy) - 2, span.parts[-1]),
        ):
            if key not in ramp_ends:
                continue
            e = span.xy[k]
            u = e - span.xy[inward]
            norm = float(np.hypot(*u))
            if norm <= 0:
                continue
            u = u / norm
            half = part.piece.attrs.width_m / 2
            n = np.array([-u[1], u[0]])
            z_end = terrain.max_along(e + n * half, e - n * half) + offset
            ramps.append(
                Ramp(
                    key, (float(e[0]), float(e[1])), (float(u[0]), float(u[1])), half, z_end, length
                )
            )
    return ramps


# Decks.


@dataclass
class DeckMesh:
    vertices: np.ndarray  # (n, 3) local
    faces: np.ndarray  # (m, 3)
    top: np.ndarray  # indices of the driving surface's vertices


@dataclass
class _Builder:
    vertices: list[np.ndarray] = field(default_factory=list)
    faces: list[tuple[int, int, int]] = field(default_factory=list)
    top: list[int] = field(default_factory=list)

    def add(self, point: np.ndarray, top: bool = False) -> int:
        self.vertices.append(np.asarray(point, dtype=float))
        if top:
            self.top.append(len(self.vertices) - 1)
        return len(self.vertices) - 1

    def tri(self, a: int, b: int, c: int, up: np.ndarray | None = None) -> None:
        """A triangle; with `up`, oriented so its normal points along `up`."""
        if up is not None:
            va, vb, vc = (self.vertices[i] for i in (a, b, c))
            if float(np.dot(np.cross(vb - va, vc - va), up)) < 0:
                b, c = c, b
        self.faces.append((a, b, c))

    def quad(self, a: int, b: int, c: int, d: int, out: np.ndarray) -> None:
        self.tri(a, b, c, out)
        self.tri(a, c, d, out)

    def box(self, corners: np.ndarray) -> None:
        """A closed box from 8 corners: bottom 0-3 and top 4-7, both counter-clockwise."""
        idx = [self.add(c) for c in corners]
        centre = corners.mean(axis=0)

        def face(a: int, b: int, c: int, d: int) -> None:
            mid = corners[[a, b, c, d]].mean(axis=0)
            self.quad(idx[a], idx[b], idx[c], idx[d], mid - centre)

        face(0, 1, 2, 3)
        face(4, 5, 6, 7)
        for k in range(4):
            face(k, (k + 1) % 4, 4 + (k + 1) % 4, 4 + k)

    def mesh(self) -> DeckMesh:
        return DeckMesh(
            np.array(self.vertices), np.array(self.faces, dtype=np.int64), np.array(self.top)
        )


UP = np.array([0.0, 0.0, 1.0])


def turn_of(d0: np.ndarray, d1: np.ndarray) -> float:
    """The z of d0 x d1 (> 0: a left turn), 0 for (nearly) straight joints."""
    turn = float(d0[0] * d1[1] - d0[1] * d1[0])
    return 0.0 if abs(turn) < 1e-9 else turn


def _top_z(span: Span, line: LineString, xy: np.ndarray, own_s: float) -> float:
    """The deck top at a point: the line at its own distance, or higher where the nearest
    point of the centre line gives a higher line height (at bends)."""
    projected = line.project(Point(xy))
    return float(max(span.line_z(own_s), span.line_z(projected)))


def deck_mesh(
    span: Span, xy: np.ndarray, s: np.ndarray, width: float, cfg: BridgeConfig
) -> DeckMesh:
    """A deck along the centre-line strip `xy` (distances `s` along the span's centre line)."""
    builder = _Builder()
    line = LineString(span.xy) if len(span.xy) >= 2 else None
    half = width / 2
    t, rh, rw = cfg.deck_thickness_m, cfg.railing_height_m, cfg.railing_width_m
    rw = min(rw, half)

    def z_at(point: np.ndarray, own: float) -> float:
        return _top_z(span, line, point, own) if line is not None else float(span.line_z(own))

    previous: tuple[int, int, np.ndarray] | None = None  # (top-left, top-right, direction)
    n = len(xy) - 1
    for k in range(n):
        p, q = xy[k], xy[k + 1]
        d = q - p
        length = float(np.hypot(*d))
        if length <= 0:
            continue
        d = d / length
        normal = np.array([-d[1], d[0]])
        corners_2d = [p + normal * half, p - normal * half, q - normal * half, q + normal * half]
        own = [s[k], s[k], s[k + 1], s[k + 1]]
        zs = [z_at(c, o) for c, o in zip(corners_2d, own, strict=True)]
        top = [
            builder.add(np.array([*c, z]), top=True) for c, z in zip(corners_2d, zs, strict=True)
        ]
        bottom = [builder.add(np.array([*c, z - t])) for c, z in zip(corners_2d, zs, strict=True)]
        builder.quad(top[0], top[1], top[2], top[3], UP)
        builder.quad(bottom[0], bottom[1], bottom[2], bottom[3], -UP)
        side_left = np.array([*normal, 0.0])
        builder.quad(top[3], top[0], bottom[0], bottom[3], side_left)
        builder.quad(top[1], top[2], bottom[2], bottom[1], -side_left)
        if previous is None:
            builder.quad(top[0], top[1], bottom[1], bottom[0], np.array([*-d, 0.0]))
        elif turn_of(previous[2], d) != 0.0:
            # Fill the outer gap of a bend with flat wedges at the joint's height.
            prev_left, prev_right, prev_d = previous
            turn = turn_of(prev_d, d)
            centre_z = z_at(p, s[k])
            centre = builder.add(np.array([*p, centre_z]), top=True)
            centre_b = builder.add(np.array([*p, centre_z - t]))
            outer_prev, outer_now = (prev_right, top[1]) if turn > 0 else (prev_left, top[0])
            builder.tri(centre, outer_prev, outer_now, UP)
            ob_prev = builder.add(builder.vertices[outer_prev] - [0, 0, t])
            ob_now = builder.add(builder.vertices[outer_now] - [0, 0, t])
            builder.tri(centre_b, ob_prev, ob_now, -UP)
            mid = (builder.vertices[outer_prev] + builder.vertices[outer_now]) / 2
            out = np.array([*(mid[:2] - p), 0.0])
            builder.quad(outer_prev, outer_now, ob_now, ob_prev, out)
        if k == n - 1:
            builder.quad(top[2], top[3], bottom[3], bottom[2], np.array([*d, 0.0]))
        previous = (top[3], top[2], d)  # (left end, right end, direction)
        # Railings: a box along each long edge, standing on the deck.
        for side in (1.0, -1.0):
            outer = half * side
            inner = (half - rw) * side
            pts = [p + normal * outer, p + normal * inner, q + normal * inner, q + normal * outer]
            own_r = [s[k], s[k], s[k + 1], s[k + 1]]
            base = [z_at(c, o) for c, o in zip(pts, own_r, strict=True)]
            corners = np.array(
                [[*c, z] for c, z in zip(pts, base, strict=True)]
                + [[*c, z + rh] for c, z in zip(pts, base, strict=True)]
            )
            if side < 0:  # keep both rings counter-clockwise from above
                corners = corners[[0, 3, 2, 1, 4, 7, 6, 5]]
            builder.box(corners)
    return builder.mesh()


def split_at_lines(
    xy: np.ndarray, s: np.ndarray, xs: np.ndarray, ys: np.ndarray
) -> tuple[np.ndarray, np.ndarray]:
    """The centre line with a vertex added where it crosses a line x = xs[i] or y = ys[j]."""
    out_xy: list[np.ndarray] = [xy[0]]
    out_s: list[float] = [float(s[0])]
    for k in range(len(xy) - 1):
        p, q = xy[k], xy[k + 1]
        ts: list[tuple[float, int, float]] = []
        for axis, lines in ((0, xs), (1, ys)):
            lo, hi = sorted((p[axis], q[axis]))
            for value in lines[(lines > lo) & (lines < hi)]:
                ts.append(((value - p[axis]) / (q[axis] - p[axis]), axis, float(value)))
        for t, axis, value in sorted(ts):
            point = p + t * (q - p)
            point[axis] = value
            out_xy.append(point)
            out_s.append(float(s[k] + t * (s[k + 1] - s[k])))
        out_xy.append(q)
        out_s.append(float(s[k + 1]))
    return np.array(out_xy), np.array(out_s)


@dataclass
class DeckPart:
    """The deck of one way in one tile."""

    way_id: int
    tile: tuple[int, int]
    mesh: DeckMesh
    spans: list[int]  # indices into the span list


def _part_strips(span: Span) -> list[tuple[int, np.ndarray, np.ndarray]]:
    """(way id, centre-line xy, distances) for each part of the span."""
    result = []
    k = 0
    for n, part in enumerate(span.parts):
        count = len(part.indices())
        if n > 0:
            k -= 1
        result.append((part.piece.way_id, span.xy[k : k + count], span.s[k : k + count]))
        k += count
    return result


def build_decks(spans: Sequence[Span], area: BuildArea, cfg: BridgeConfig) -> list[DeckPart]:
    """Deck meshes per (way, tile), split where the centre line crosses a tile edge."""
    tiles = area.tiles()
    xs = np.array(sorted({t.bounds.min_x for t in tiles} | {t.bounds.max_x for t in tiles}))
    ys = np.array(sorted({t.bounds.min_y for t in tiles} | {t.bounds.max_y for t in tiles}))
    grouped: dict[tuple[int, tuple[int, int]], list[tuple[DeckMesh, int]]] = defaultdict(list)
    for index, span in enumerate(spans):
        if span.kind != "bridge":
            continue
        widths = {part.piece.way_id: part.piece.attrs.width_m for part in span.parts}
        for way_id, xy, s in _part_strips(span):
            if len(xy) < 2:
                continue
            xy2, s2 = split_at_lines(xy, s, xs, ys)
            mids = (xy2[:-1] + xy2[1:]) / 2
            tile_keys = [area.tile_index(float(x), float(y)) for x, y in mids]
            start = 0
            for k in range(1, len(mids) + 1):
                if k == len(mids) or tile_keys[k] != tile_keys[start]:
                    key = tile_keys[start]
                    if key is not None:
                        mesh = deck_mesh(
                            span, xy2[start : k + 1], s2[start : k + 1], widths[way_id], cfg
                        )
                        grouped[(way_id, key)].append((mesh, index))
                    start = k
    parts = []
    for (way_id, tile), meshes in sorted(grouped.items()):
        vertices, faces, top = [], [], []
        offset = 0
        for mesh, _ in meshes:
            vertices.append(mesh.vertices)
            faces.append(mesh.faces + offset)
            top.append(mesh.top + offset)
            offset += len(mesh.vertices)
        merged = DeckMesh(np.concatenate(vertices), np.concatenate(faces), np.concatenate(top))
        parts.append(DeckPart(way_id, tile, merged, sorted({i for _, i in meshes})))
    return parts


def deck_node_names(parts: Sequence[DeckPart]) -> dict[tuple[int, tuple[int, int]], str]:
    """`bridge_osm_way_<id>-col`, or `..._p<n>-col` per tile when a way spans several tiles.

    A way's parts are numbered in the order of their tile index (i, then j), not along
    the way.
    """
    by_way: defaultdict[int, list[tuple[int, int]]] = defaultdict(list)
    for part in parts:
        by_way[part.way_id].append(part.tile)
    names = {}
    for way_id, tiles in by_way.items():
        if len(tiles) == 1:
            names[(way_id, tiles[0])] = f"bridge_osm_way_{way_id}-col"
        else:
            for n, tile in enumerate(tiles):
                names[(way_id, tile)] = f"bridge_osm_way_{way_id}_p{n}-col"
    return names


# Clearance.


def clearance_check(
    span: Span, terrain: TerrainSurface, cfg: BridgeConfig, spacing: float
) -> dict[str, Any] | None:
    """A warning when the deck underside gets too close to the terrain mesh, else None."""
    if span.length <= 0:
        return None
    width = max(part.piece.attrs.width_m for part in span.parts)
    samples = np.unique(np.concatenate([np.arange(0.0, span.length, spacing), span.s]))
    line = LineString(span.xy)
    centre = np.array([np.asarray(line.interpolate(v).coords)[0] for v in samples])
    k = np.clip(np.searchsorted(span.s, samples, side="right") - 1, 0, len(span.xy) - 2)
    d = span.xy[k + 1] - span.xy[k]
    d = d / np.maximum(np.linalg.norm(d, axis=1, keepdims=True), 1e-12)
    normal = np.column_stack([-d[:, 1], d[:, 0]])
    ground = np.max(
        np.stack(
            [
                terrain.height(centre),
                terrain.height(centre + normal * width / 2),
                terrain.height(centre - normal * width / 2),
            ]
        ),
        axis=0,
    )
    clearance = span.line_z(samples) - cfg.deck_thickness_m - ground
    clear = np.flatnonzero(clearance >= cfg.min_clearance_m)
    if len(clear) == 0:
        checked = np.arange(len(samples))
        never = True
    else:
        checked = np.arange(clear[0], clear[-1] + 1)
        never = False
        if clearance[checked].min() >= cfg.min_clearance_m:
            return None
    worst = int(checked[np.argmin(clearance[checked])])
    x, y = centre[worst]
    return {
        "osm_ways": [f"osm:way/{w}" for w in span.way_ids],
        "source": span.source,
        "min_clearance_m": round(float(clearance[worst]), 3),
        "never_clears": never,
        "position_local": [round(float(x), 2), round(float(y), 2)],
    }
