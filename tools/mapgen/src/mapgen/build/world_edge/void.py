"""The void trigger: convex prisms around the outside of the boundary.

Footprints are convex quadrilaterals in local (x, n) (a triangle is written as a quad
with its last point repeated) that together cover the band between `inner` and
`inner + width` outside the ring, measured from the whole ring, and keep at least
`inner` from every part of it:

- one strip per ring segment, from `inner` to `inner + width` outward and as long as the
  segment;
- at a convex corner (the band opens up), a fan of quads around the corner: the circle
  of radius `inner` around it is replaced by a polygon drawn around it (`_tangents`: its
  edges touch the circle, the first and last at the two segments' strips, and no corner
  of it is more than `ARC_EXCESS_M` outside the circle), and each fan quad runs from one
  of its edges out to the same edge of the polygon around the outer circle. The half
  edges at both ends lie on the neighbouring strips' lines, so those strips take them.

Every piece that comes closer to the whole ring than `inner` (at a concave corner, or
across a notch or spike of the ring) or reaches into the playable area is cut: all such
pieces are merged and the near zone `_near_zone` is taken out of them (the playable
area, every segment's rectangle of half-width `inner` and every vertex's polygon around
the circle of radius `inner`). The zone's edges are on or outside the true offset, so no
footprint point comes closer to the ring than `inner` (up to floating-point noise), and
the zone lies within `inner + ARC_EXCESS_M` of the ring. What is left is split into
convex quads: a convex polygon of at most four corners whole; otherwise its constrained
Delaunay triangles merged into convex polygons (Hertel-Mehlhorn), each fanned into quads
(a triangle, with its last point repeated, where a corner is left over).

So the prisms cover every band point more than `inner` from the ring, except near ring
vertices: within `inner + ARC_EXCESS_M` of a vertex (convex or concave; in practice only
at convex corners, where the fans run around the circles), the cover starts at
`inner + ARC_EXCESS_M` (`in_band`, which `band_samples` samples). All prisms share
`bottom_y` and `top_y`.
"""

import math
from collections.abc import Sequence

import numpy as np
import shapely
from shapely.geometry import Polygon
from shapely.geometry.base import BaseGeometry

from mapgen.build.world_edge.ring import Ring

Quad = list[tuple[float, float]]  # 4 local (x, n) points
CLIP_AREA_M2 = 1e-6  # a piece overlapping the playable area by more than this is clipped
COLLINEAR = 1e-12
MIN_WIDTH_M = 1e-6  # pieces thinner than this (2 area / perimeter) are dropped: they are
# below boundary.json's 1 µm resolution and would not survive its rounding
CONVEX_TOLERANCE = 1e-12  # relative: a corner turning the other way by less is straight
# How far the polygons drawn around the circles of radius `inner` (at the vertices) may
# reach outside them: the void band is covered from `inner + ARC_EXCESS_M` outward. Sets
# the number of fan quads at a convex corner (2 at a right angle for inner 1 m). 5 cm
# (decided 2026-11-04): fewer convex shapes in the game's void Area3D, and the slack near
# convex corners is far below one physics tick of travel; the safety net below the slab
# catches anything that slips past.
ARC_EXCESS_M = 0.05
MAX_ARC_STEP = math.pi / 2  # the widest angle one polygon edge around a circle covers
KEEP_SLACK_M = 1e-7  # a piece at least `inner` minus this from the ring is kept whole


def _turn(ring: Ring, i: int) -> float:
    """z of (direction into vertex i) x (direction out of it): > 0 convex (CCW ring)."""
    d0, d1 = ring.direction(i - 1), ring.direction(i)
    return float(d0[0] * d1[1] - d0[1] * d1[0])


def arc_step(inner: float) -> float:
    """The widest angle between two touching points of a polygon around a circle of
    radius `inner` whose corners stay within `ARC_EXCESS_M` of the circle."""
    if inner <= 0:
        return MAX_ARC_STEP
    return min(MAX_ARC_STEP, 2 * math.acos(inner / (inner + ARC_EXCESS_M)))


def _angle(v: np.ndarray) -> float:
    return math.atan2(float(v[1]), float(v[0]))


def _tangents(ring: Ring, i: int, step: float) -> list[float]:
    """The angles (radians, increasing, one full turn from the angle of the normal of
    the segment into vertex i) at which the polygon around vertex i's circle touches it:
    the four directions of the two segments' normals (so its edges line up with the
    segments' rectangles) and as many in between as keep each gap within `step`."""
    first = _angle(ring.normal(i - 1))
    base = [first, _angle(ring.normal(i)), first + math.pi, _angle(ring.normal(i)) + math.pi]
    turned = sorted({round((a - first) % (2 * math.pi), 12) for a in base})
    turned = [a for a in turned if a < 2 * math.pi - 1e-12]
    result: list[float] = []
    for k, a in enumerate(turned):
        b = turned[k + 1] if k + 1 < len(turned) else 2 * math.pi
        parts = max(1, math.ceil((b - a) / step - 1e-9))
        result.extend(first + a + (b - a) * p / parts for p in range(parts))
    return result


def _unit(angle: float) -> np.ndarray:
    return np.array([math.cos(angle), math.sin(angle)])


def _around(centre: np.ndarray, radius: float, angles: Sequence[float]) -> list[np.ndarray]:
    """The corners of the polygon around the circle (`centre`, `radius`) that touches it
    at `angles` (increasing; each consecutive pair less than pi apart): one corner
    between each pair, at the meeting point of the two tangent lines."""
    corners = []
    for a, b in zip(angles, angles[1:], strict=False):
        half = (b - a) / 2
        corners.append(centre + _unit(a + half) * radius / math.cos(half))
    return corners


def _vertex_polygon(ring: Ring, i: int, inner: float, step: float) -> Polygon:
    """The polygon around vertex i's circle of radius `inner`."""
    angles = _tangents(ring, i, step)
    return Polygon(_around(ring.xy[i], inner, [*angles, angles[0] + 2 * math.pi]))


def _near_zone(ring: Ring, inner: float, step: float) -> list[BaseGeometry]:
    """The parts of the zone no prism may hold: the playable area, each segment's
    rectangle of half-width `inner` and each vertex's polygon around its circle of
    radius `inner`. Together they hold every point closer to the ring than `inner`, and
    none more than `inner + ARC_EXCESS_M` from it."""
    parts: list[BaseGeometry] = [ring.polygon()]
    if inner <= 0:
        return parts
    for i in range(ring.n):
        a, b = ring.segment(i)
        normal = ring.normal(i) * inner
        parts.append(Polygon([a - normal, b - normal, b + normal, a + normal]))
        parts.append(_vertex_polygon(ring, i, inner, step))
    return parts


def _fan(
    ring: Ring, j: int, inner: float, outer: float, step: float
) -> tuple[list[np.ndarray], list[np.ndarray]]:
    """The corners of the polygons around convex vertex j's circles of radius `inner`
    and `outer` between its two segments' normals, in ring order. The first corners lie
    on the lines of the strip before the vertex (its inner and outer edges, extended),
    the last ones on the lines of the strip after it."""
    b = ring.xy[j]
    angles = _tangents(ring, j, step)
    span = (_angle(ring.normal(j)) - angles[0]) % (2 * math.pi)
    arc = [a for a in angles if a - angles[0] < span - 1e-12] + [angles[0] + span]
    return _around(b, inner, arc), _around(b, outer, arc)


def void_quads(ring: Ring, inner: float, width: float) -> list[Quad]:
    """The footprints covering the band from `inner` to `inner + width` outside `ring`,
    in ring order (each segment's strip, then the fan at its end vertex). Every one is a
    convex quadrilateral or a triangle with its last point repeated, and none comes
    closer to the ring than `inner`.

    A strip next to a convex vertex reaches on to the fan's first (or last) corners,
    which lie on its own inner and outer lines, so the fan holds one quad per pair of
    consecutive corners."""
    outer = inner + width
    step = arc_step(inner)
    fans = {
        j: _fan(ring, j, inner, outer, step) for j in range(ring.n) if _turn(ring, j) > COLLINEAR
    }
    pieces: list[list[np.ndarray]] = []
    for i in range(ring.n):
        j = (i + 1) % ring.n
        a, b = ring.xy[i], ring.xy[j]
        normal = ring.normal(i)
        if i in fans:
            start_in, start_out = fans[i][0][-1], fans[i][1][-1]
        else:
            start_in, start_out = a + normal * inner, a + normal * outer
        if j in fans:
            end_in, end_out = fans[j][0][0], fans[j][1][0]
        else:
            end_in, end_out = b + normal * inner, b + normal * outer
        pieces.append([start_in, end_in, end_out, start_out])
        if j in fans:
            corners_in, corners_out = fans[j]
            pieces.extend(
                [corners_in[k], corners_in[k + 1], corners_out[k + 1], corners_out[k]]
                for k in range(len(corners_in) - 1)
            )
    return clip_to_ring(
        ring,
        inner,
        [[(float(x), float(y)) for x, y in piece] for piece in pieces],
        _near_zone(ring, inner, step),
    )


def clip_to_ring(
    ring: Ring, inner: float, quads: Sequence[Quad], zone: Sequence[BaseGeometry]
) -> list[Quad]:
    """`quads` as convex footprints at least `inner` from `ring` and outside its polygon.
    A convex polygon of at most four corners that is that far from the ring (within
    `KEEP_SLACK_M`) and doesn't overlap the playable area by more than `CLIP_AREA_M2` is
    kept (a triangle with its last point repeated). All the others are merged, and the
    convex parts of their area outside `zone` (`_near_zone`) replace them."""
    line = ring.line()
    polygon = ring.polygon()
    shapely.prepare(line)
    shapely.prepare(polygon)
    result: list[Quad] = []
    clipped: list[BaseGeometry] = []
    for quad in quads:
        corners = _corners(quad)
        if len(corners) < 3:  # no area
            continue
        shape = Polygon(corners)
        if (
            len(corners) <= 4
            and _is_convex(corners)
            and shape.is_valid
            and _thick_enough(shape)
            and shape.distance(line) >= inner - KEEP_SLACK_M
            and shape.intersection(polygon).area <= CLIP_AREA_M2
        ):
            result.append(_as_quad(corners))
        else:
            clipped.append(shapely.make_valid(shape))
    if clipped:
        # One cut for all of them, so overlapping pieces (strips and fans reaching into
        # the same notch) give one region, cut and split once, instead of one set of
        # prisms per piece. The result can still overlap the kept pieces.
        region = shapely.union_all(clipped)
        tree = shapely.STRtree(list(zone))
        near = shapely.union_all([zone[k] for k in tree.query(region, predicate="intersects")])
        result.extend(_convex_parts(region.difference(near)))
    return result


def _corners(points: Sequence[tuple[float, float]]) -> list[tuple[float, float]]:
    """`points` without repeated (consecutive or closing) points and straight corners."""
    distinct: list[tuple[float, float]] = []
    for p in points:
        if not distinct or p != distinct[-1]:
            distinct.append(p)
    while len(distinct) > 1 and distinct[0] == distinct[-1]:
        distinct.pop()
    changed = True
    while changed and len(distinct) >= 3:
        changed = False
        for k in range(len(distinct)):
            p, q, s = distinct[k - 1], distinct[k], distinct[(k + 1) % len(distinct)]
            cross = (q[0] - p[0]) * (s[1] - q[1]) - (q[1] - p[1]) * (s[0] - q[0])
            scale = np.hypot(q[0] - p[0], q[1] - p[1]) * np.hypot(s[0] - q[0], s[1] - q[1])
            if abs(cross) <= CONVEX_TOLERANCE * scale:
                del distinct[k]
                changed = True
                break
    return distinct


def _is_convex(corners: Sequence[tuple[float, float]]) -> bool:
    """Whether `corners` (no repeated points or straight corners) turn the same way at
    every corner. With at most four corners this also means the polygon is simple."""
    if len(corners) < 3:
        return False
    signs = set()
    for k in range(len(corners)):
        p, q, s = corners[k - 1], corners[k], corners[(k + 1) % len(corners)]
        cross = (q[0] - p[0]) * (s[1] - q[1]) - (q[1] - p[1]) * (s[0] - q[0])
        signs.add(cross > 0)
    return len(signs) == 1


def _thick_enough(shape: Polygon) -> bool:
    return shape.length > 0 and 2 * shape.area / shape.length >= MIN_WIDTH_M


def _as_quad(corners: Sequence[tuple[float, float]]) -> Quad:
    quad = [(float(x), float(y)) for x, y in corners]
    return quad if len(quad) == 4 else [*quad, quad[-1]]


def _convex_parts(geometry: BaseGeometry) -> list[Quad]:
    """The polygons of `geometry` as convex footprints: a convex polygon of at most four
    corners whole; any other one cut into convex polygons (`_hertel_mehlhorn`, from its
    constrained Delaunay triangles) and each of those into quads (`_fan_quads`)."""
    result: list[Quad] = []
    for part in shapely.get_parts(geometry):
        if part.geom_type == "GeometryCollection" or part.geom_type.startswith("Multi"):
            result.extend(_convex_parts(part))
            continue
        if part.geom_type != "Polygon" or not _thick_enough(part):
            continue
        corners = _corners(list(part.exterior.coords))
        if not part.interiors and len(corners) >= 3 and _is_convex(corners):
            result.extend(_fan_quads(corners))
            continue
        triangles = [
            [(float(x), float(y)) for x, y in list(triangle.exterior.coords)[:3]]
            for triangle in shapely.get_parts(shapely.constrained_delaunay_triangles(part))
            if _thick_enough(triangle)
        ]
        for convex in _hertel_mehlhorn(triangles):
            result.extend(_fan_quads(convex))
    return result


def _ccw(points: Sequence[tuple[float, float]]) -> list[tuple[float, float]]:
    """`points` in counter-clockwise order."""
    count = len(points)
    area = sum(
        points[k][0] * points[(k + 1) % count][1] - points[(k + 1) % count][0] * points[k][1]
        for k in range(count)
    )
    return list(points) if area > 0 else list(points)[::-1]


def _hertel_mehlhorn(triangles: Sequence[Quad]) -> list[list[tuple[float, float]]]:
    """`triangles` (3 points each, sharing exact corner coordinates along common edges)
    merged into convex polygons: each shared edge, in order, is removed when the polygons
    on its two sides make a convex polygon together (the Hertel-Mehlhorn heuristic)."""
    polygons: dict[int, list[tuple[float, float]]] = {
        k: _ccw(tri) for k, tri in enumerate(triangles)
    }
    owner = list(range(len(triangles)))

    def root(k: int) -> int:
        while owner[k] != k:
            owner[k] = owner[owner[k]]
            k = owner[k]
        return k

    Edge = tuple[tuple[float, float], tuple[float, float]]
    by_edge: dict[Edge, list[int]] = {}
    for k, tri in polygons.items():
        for e in range(3):
            p, q = tri[e], tri[(e + 1) % 3]
            by_edge.setdefault((min(p, q), max(p, q)), []).append(k)
    for (p, q), sides in by_edge.items():
        if len(sides) != 2:
            continue
        a, b = root(sides[0]), root(sides[1])
        if a == b:
            continue
        merged = _join(polygons[a], polygons[b], p, q)
        if merged is None:
            continue
        corners = _corners(merged)
        if len(corners) >= 3 and _is_convex(corners):
            owner[b] = a
            polygons[a] = corners
            del polygons[b]
    return [polygons[k] for k in sorted(polygons)]


def _join(
    first: list[tuple[float, float]],
    second: list[tuple[float, float]],
    p: tuple[float, float],
    q: tuple[float, float],
) -> list[tuple[float, float]] | None:
    """Two CCW polygons sharing the edge p-q (one runs p -> q, the other q -> p) as one
    CCW polygon without it, or None when the edge isn't on both outlines."""

    def walk(points: list[tuple[float, float]], start: tuple[float, float]) -> list:
        k = points.index(start)
        return points[k:] + points[:k]

    if p not in first or q not in first or p not in second or q not in second:
        return None
    a = walk(first, q)  # first runs p -> q: from q it ends at p
    if a[-1] != p:
        p, q = q, p  # first runs q -> p instead
        a = walk(first, q)
        if a[-1] != p:
            return None
    b = walk(second, p)  # second runs q -> p: from p it ends at q
    if b[-1] != q:
        return None
    return a + b[1:-1]


def _fan_quads(corners: Sequence[tuple[float, float]]) -> list[Quad]:
    """A convex polygon (no repeated points or straight corners) as convex quads fanned
    from its first corner: [c0, c1, c2, c3], [c0, c3, c4, c5], ...; the last one a
    triangle (last point repeated) when the corner count is odd. Quads thinner than
    `MIN_WIDTH_M` are dropped."""
    points = [(float(x), float(y)) for x, y in corners]
    parts = [points] if len(points) <= 4 else []
    k = 1
    while len(points) > 4 and k + 1 < len(points):
        parts.append([points[0], *points[k : k + 3]])
        k += 2
    # Like the triangles, pieces thinner than MIN_WIDTH_M are dropped: below the 1 µm
    # resolution of boundary.json they would round to invalid footprints.
    return [_as_quad(part) for part in parts if _thick_enough(Polygon(part))]


def footprint_problems(quads: Sequence[Quad], tolerance: float = 1e-6) -> list[str]:
    """Every footprint that isn't a valid convex quadrilateral (or triangle with a
    repeated point), allowing its points to be off by up to `tolerance` (rounding)."""
    problems: list[str] = []
    for k, quad in enumerate(quads):
        shape = Polygon(quad)
        if len(quad) != 4 or not shape.is_valid or shape.area <= 0:
            problems.append(f"footprint {k} isn't a valid polygon: {list(quad)}")
        elif shape.convex_hull.area - shape.area > 2 * tolerance * shape.length:
            problems.append(f"footprint {k} isn't convex: {list(quad)}")
    return problems


def point_in_quads(
    quads: Sequence[Quad], points: np.ndarray, tolerance: float = 1e-6
) -> np.ndarray:
    """For each (x, n) point, whether it lies in some quad (closed, within `tolerance`)."""
    pts = np.asarray(points, dtype=float).reshape(-1, 2)
    inside = np.zeros(len(pts), dtype=bool)
    shapes = [Polygon(q).buffer(tolerance, join_style="mitre") for q in quads]
    tree = shapely.STRtree(shapes)
    geoms = shapely.points(pts)
    hit_points, _ = tree.query(geoms, predicate="intersects")
    inside[np.unique(hit_points)] = True
    return inside


def in_band(ring: Ring, inner: float, width: float, points: np.ndarray) -> np.ndarray:
    """For each (x, n) point, whether it lies in the band the prisms cover: outside the
    ring, less than `inner + width` from it (measured from the whole ring), and more than
    `inner` from it, or more than `inner + ARC_EXCESS_M` within `inner + ARC_EXCESS_M`
    of a ring vertex (where the polygons around the vertices' circles reach outside the
    circles). 1 µm away from every bound."""
    pts = np.asarray(points, dtype=float).reshape(-1, 2)
    geoms = shapely.points(pts)
    distance = shapely.distance(geoms, ring.line())
    near_vertex = shapely.distance(geoms, shapely.multipoints(ring.xy)) < (
        inner + ARC_EXCESS_M + 1e-6
    )
    start = np.where(near_vertex, inner + ARC_EXCESS_M, inner)
    keep = (distance > start + 1e-6) & (distance < inner + width - 1e-6)
    return keep & ~shapely.contains(ring.polygon(), geoms)


def band_samples(ring: Ring, inner: float, width: float, count: int, seed: int = 0) -> np.ndarray:
    """`count` random points (x, n) of the band the prisms cover (`in_band`),
    deterministic for a seed."""
    rng = np.random.default_rng(seed)
    min_x, min_y, max_x, max_y = ring.polygon().bounds
    reach = inner + width
    chosen: list[np.ndarray] = []
    total = 0
    while total < count:
        candidates = np.column_stack(
            [
                rng.uniform(min_x - reach, max_x + reach, 200_000),
                rng.uniform(min_y - reach, max_y + reach, 200_000),
            ]
        )
        picked = candidates[in_band(ring, inner, width, candidates)]
        chosen.append(picked)
        total += len(picked)
    return np.vstack(chosen)[:count]


def quads_overlapping(polygon: Polygon, quads: Sequence[Quad]) -> list[int]:
    """Indexes of quads whose interior meets the polygon's interior by more than
    `CLIP_AREA_M2`."""
    return [
        k
        for k, quad in enumerate(quads)
        if Polygon(quad).buffer(0).intersection(polygon).area > CLIP_AREA_M2
    ]


def ring_distances(ring: Ring, quads: Sequence[Quad]) -> np.ndarray:
    """For each footprint, the distance of its closest point to the ring (exact polygon
    distance; 0 when it touches or crosses the ring)."""
    if not quads:
        return np.empty(0)
    return np.asarray(shapely.distance(shapely.polygons(np.asarray(quads)), ring.line()))
