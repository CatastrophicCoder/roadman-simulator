"""The void trigger: convex prisms around the outside of the boundary.

Footprints are convex quadrilaterals in local (x, n) (a triangle is written as a quad
with its last point repeated) that together cover the band between `inner` and
`inner + width` outside the ring with no gaps:

- one strip per ring segment, from `inner` to `inner + width` outward and as long as the
  segment;
- at a convex corner (the band opens up), two quads filling the gap between the two
  segments' strips (their outer side runs through the outer offset lines' meeting point,
  their inner side along the chord between the inner offsets, which stays `inner * cos`
  of half the turn away from the corner).

Every point of the band is in one of these: its nearest ring point is either inside a
segment (the point is in that segment's strip) or a convex corner (in its fill). A strip
can still reach over the playable area or come closer to another segment than `inner`
(at a concave corner, or across a narrow inlet of a custom polygon). Each piece that
overlaps the inner zone (the playable area, every segment's strip from 0 to `inner`, and
the triangle inside each convex corner's chord) is replaced by its part outside that
zone, split into convex pieces: kept whole when it is a convex polygon of at most four
corners, otherwise triangulated. The inner zone holds no band point, so the cut leaves no
gap. All prisms share `bottom_y` and `top_y`.
"""

from collections.abc import Sequence

import numpy as np
import shapely
from shapely.geometry import Polygon
from shapely.geometry.base import BaseGeometry

from mapgen.build.world_edge.ring import Ring

Quad = list[tuple[float, float]]  # 4 local (x, n) points
CLIP_AREA_M2 = 1e-6  # a piece overlapping the inner zone by more than this is clipped
COLLINEAR = 1e-12
MIN_WIDTH_M = 1e-6  # pieces thinner than this (2 area / perimeter) are dropped: they are
# below boundary.json's 1 µm resolution and would not survive its rounding
CONVEX_TOLERANCE = 1e-12  # relative: a corner turning the other way by less is straight


def _turn(ring: Ring, i: int) -> float:
    """z of (direction into vertex i) x (direction out of it): > 0 convex (CCW ring)."""
    d0, d1 = ring.direction(i - 1), ring.direction(i)
    return float(d0[0] * d1[1] - d0[1] * d1[0])


def _miter(ring: Ring, i: int, offset: float) -> np.ndarray:
    """Where the two segments' offset lines (at `offset` outward) meet at vertex i."""
    n0, n1 = ring.normal(i - 1), ring.normal(i)
    m = n0 + n1
    m /= np.hypot(*m)
    cos_half = float(np.dot(m, n0))
    return np.asarray(ring.xy[i] + m * offset / max(cos_half, 1e-9))


def _inner_zone(ring: Ring, inner: float) -> BaseGeometry:
    """The playable area and everything closer to the ring than `inner` that no prism
    may hold: each segment's strip from 0 to `inner` outward, and at each convex corner
    the triangle between the corner and the chord of the two strips' inner corners."""
    parts: list[BaseGeometry] = [ring.polygon()]
    for i in range(ring.n):
        j = (i + 1) % ring.n
        a, b = ring.xy[i], ring.xy[j]
        normal = ring.normal(i)
        parts.append(Polygon([a, b, b + normal * inner, a + normal * inner]))
        if _turn(ring, j) > COLLINEAR:
            parts.append(Polygon([b, b + normal * inner, b + ring.normal(j) * inner]))
    return shapely.union_all([part for part in parts if part.area > 0])


def void_quads(ring: Ring, inner: float, width: float) -> list[Quad]:
    """The footprints covering the band from `inner` to `inner + width` outside `ring`,
    in ring order (each segment's pieces, then the fill at its end vertex). Every one is
    a convex quadrilateral or a triangle with its last point repeated."""
    outer = inner + width
    pieces: list[list[np.ndarray]] = []
    for i in range(ring.n):
        j = (i + 1) % ring.n
        a, b = ring.xy[i], ring.xy[j]
        normal = ring.normal(i)
        pieces.append(
            [a + normal * inner, b + normal * inner, b + normal * outer, a + normal * outer]
        )
        if _turn(ring, j) > COLLINEAR:  # convex end vertex: fill the opening
            n0, n1 = normal, ring.normal(j)
            first_in, first_out = b + n0 * inner, b + n0 * outer
            next_in, next_out = b + n1 * inner, b + n1 * outer
            tip = _miter(ring, j, outer)
            chord = (first_in + next_in) / 2
            pieces.append([first_in, first_out, tip, chord])
            pieces.append([chord, tip, next_out, next_in])
    return clip_to_outside(
        _inner_zone(ring, inner),
        [[(float(x), float(y)) for x, y in piece] for piece in pieces],
    )


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
    corners whole, anything else as its constrained Delaunay triangles."""
    result: list[Quad] = []
    for part in shapely.get_parts(geometry):
        if part.geom_type == "GeometryCollection" or part.geom_type.startswith("Multi"):
            result.extend(_convex_parts(part))
            continue
        if part.geom_type != "Polygon" or not _thick_enough(part):
            continue
        corners = _corners(list(part.exterior.coords))
        if not part.interiors and len(corners) <= 4 and _is_convex(corners):
            result.append(_as_quad(corners))
            continue
        for triangle in shapely.get_parts(shapely.constrained_delaunay_triangles(part)):
            if _thick_enough(triangle):
                result.append(_as_quad(list(triangle.exterior.coords)[:3]))
    return result


def clip_to_outside(polygon: BaseGeometry, quads: Sequence[Quad]) -> list[Quad]:
    """`quads` as convex footprints outside `polygon`, each of four points. A convex
    polygon of at most four corners that doesn't overlap `polygon` is kept (a triangle
    with its last point repeated); any other one, including an invalid or non-convex
    one, is replaced by the convex parts of its area outside `polygon`. Pieces thinner
    than `MIN_WIDTH_M` are dropped."""
    result: list[Quad] = []
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
            and shape.intersection(polygon).area <= CLIP_AREA_M2
        ):
            result.append(_as_quad(corners))
            continue
        result.extend(_convex_parts(shapely.make_valid(shape).difference(polygon)))
    return result


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


def band_samples(ring: Ring, inner: float, width: float, count: int, seed: int = 0) -> np.ndarray:
    """`count` random points (x, n) of the band outside the ring at distances strictly
    between `inner` and `inner + width` (1 µm away from both), deterministic for a seed."""
    rng = np.random.default_rng(seed)
    polygon = ring.polygon()
    line = ring.line()
    min_x, min_y, max_x, max_y = polygon.bounds
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
        geoms = shapely.points(candidates)
        distance = shapely.distance(geoms, line)
        keep = (distance > inner + 1e-6) & (distance < reach - 1e-6)
        keep &= ~shapely.contains(polygon, geoms)
        picked = candidates[keep]
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
