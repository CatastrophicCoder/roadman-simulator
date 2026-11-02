"""The cliff edge geometry of stage 10 (docs/specs/world-edge-pipeline.md, "Cliff edge
geometry").

Local coordinates throughout (x east, n north, z up); the exporter turns them into glTF.

- **Pieces.** The ring is cut at every roadblock and fence end of `boundary.json` (the
  break points), at its own vertices and where it crosses a tile grid line. Each piece is
  straight, and its height runs linearly from its item's start height to its end height,
  so the ledge's inner edge is at the fence and roadblock heights.
- **Ledge.** Each piece is extruded horizontally outward by `ledge_m` (a planar quad
  sloping only along the ring). A convex corner gets a horizontal kite reaching the
  mitre point, at the height of the piece that ends there; where the mitre would reach
  further than `MITRE_LIMIT` ledge widths, the kite is cut square to the bisector at that
  distance (a clipped mitre), so it still covers every point within `ledge_m` of the
  corner. At a concave corner the two
  neighbouring quads overlap; the line from the corner to the inner mitre point (the
  bisector) splits the overlap. All shapes, these bisectors and the ring are noded into
  one planar arrangement (snap-rounded to `GRID_M`), so neighbouring parts share exact
  edges. Each cell outside the ring takes the shape that contains it whose ring piece or
  corner is nearest to the cell's representative point, so the ledge neither overlaps
  itself nor reaches over the playable area, and its cells cover the band without gaps.
  A cell that is nearer to another covering part at one of its vertices (in an inlet
  narrower than the ledge, one cell spans both walls) is split along the line where the
  two are equally near, and the arrangement is noded again (up to `SPLIT_PASSES` times),
  so each wall's ledge vertices on the ring are at that wall's height.
- **Walls.** Every arrangement edge of a ledge cell becomes a vertical wall, flat and
  planar: to the void, the face down to `bottom_y`; to another ledge cell of a different
  height, a seam closing the step (where a fence meets a roadblock at road height, or at
  a corner); on the ring, a skirt between the ledge's inner edge and the terrain mesh's
  own edge (the build area or tile edge of a rectangle, the cut edge of a custom
  polygon), so the terrain meets the cliff mesh exactly, although a fence's straight
  line is up to `fence.max_terrain_deviation_m` from it. A wall faces its lower side.
- **Bottom cap.** The playable area plus the ledge, flat at `bottom_y`, facing down, cut
  into the tile grid cells (the outer cells reach past the build area's edge, under the
  ledge).
- **Flat-shaded.** Faces share no vertices, and every vertex normal is its face normal.
- **Tiles.** Each piece belongs to the tile on the playable side of its stretch of the
  ring (`stretch_tile`); a corner belongs to the piece ending at it, every wall to its
  ledge cell. A piece or cap part whose tile isn't written goes to the nearest written
  tile that the boundary passes through. Node `edge_cliff-col` (visual and collision)
  holds the ledge and walls, `edge_bottom` (visual only) the cap.
"""

import math
from collections import Counter
from collections.abc import Mapping, Sequence
from dataclasses import dataclass, field
from typing import Any

import numpy as np
import shapely
from shapely.geometry import LineString, Point, Polygon
from shapely.geometry.base import BaseGeometry

from mapgen.build.export_glb import MeshNode, flat_shaded
from mapgen.build.roads.surface import triangulate
from mapgen.build.terrain.base import TerrainChunk
from mapgen.build.world_edge.ring import ON_RING_M, Ring, merge_touching
from mapgen.geo import BBox

__all__ = [
    "BOTTOM_NODE",
    "CLIFF_MATERIAL",
    "CLIFF_NODE",
    "MITRE_LIMIT",
    "Cliff",
    "CliffError",
    "RingItem",
    "Tiling",
    "build_cliff",
    "ring_items",
    "ring_profile",
    "ring_tile_ids",
    "stretch_tile",
]

CLIFF_NODE = "edge_cliff-col"
BOTTOM_NODE = "edge_bottom"
CLIFF_MATERIAL = "edge_cliff"
# A convex corner is mitred while its mitre point lies at most this many ledge widths from
# the corner (turns up to 120°); a sharper corner's mitre is clipped at that distance.
MITRE_LIMIT = 2.0
GRID_M = 1e-9  # the ledge linework is snap-rounded to this grid, so cells share edges exactly
SNAP_S_M = 1e-5  # break points closer than this to a ring vertex (or grid crossing) are one
PROFILE_GAP_M = 1e-3  # a ring stretch longer than this without a terrain edge fails the build
SPLIT_PASSES = 4  # how often cells nearer to two parts are split and the ledge noded again
NEARER_M = 1e-4  # a part nearer than the cell's owner by more than this splits the cell
SPLIT_POINTS = 8  # the equally-near curve across a cell is traced at this many points
INWARD_M = 1e-4  # a stretch's midpoint moved this far into the playable area finds its tile
COLLINEAR = 1e-12  # a corner turning by less than this (cross product) is straight
MIN_AREA_M2 = 1e-12  # smaller triangles and cells are dropped
MIN_STEP_M = 1e-6  # walls lower than this are dropped
GAP_M = 1e-6  # ring left without ledge (summed) beyond this fails the build

PLAY = -1  # cell labels: inside the ring
OUT = -2  # outside every ledge shape (the void)


class CliffError(Exception):
    """The cliff can't be built. The message is shown to the user."""


@dataclass(frozen=True)
class RingItem:
    """A roadblock or fence of `boundary.json`, as the cliff uses it."""

    id: str
    s_start: float
    s_end: float
    y_start: float
    y_end: float


def ring_items(document: Mapping[str, Any]) -> list[RingItem]:
    """The roadblocks and fences of a `boundary.json` document, sorted by `s_start`."""
    items = [
        RingItem(str(i["id"]), float(i["s_start"]), float(i["s_end"]), i["start"][1], i["end"][1])
        for i in [*document["roadblocks"], *document["fences"]]
    ]
    return sorted(items, key=lambda item: item.s_start)


@dataclass(frozen=True)
class Tiling:
    """The tile grid: every tile of the build area (`cells`) and the written ones."""

    tile_size: float
    cells: Mapping[str, BBox]  # every tile of the area, by id (bounds clipped to the area)
    written: frozenset[str]

    def area_bounds(self) -> BBox:
        boxes = list(self.cells.values())
        return BBox(
            min(b.min_x for b in boxes),
            min(b.min_y for b in boxes),
            max(b.max_x for b in boxes),
            max(b.max_y for b in boxes),
        )


def _tile_id(i: int, j: int) -> str:
    return f"tile_x{i}_y{j}"


def grid_crossings(ring: Ring, tile_size: float) -> np.ndarray:
    """The `s` of every point where the ring crosses a tile grid line (x or n a multiple of
    `tile_size`) inside a segment, sorted."""
    result: list[float] = []
    for i in range(ring.n):
        a, b = ring.segment(i)
        length = float(ring.cum[i + 1] - ring.cum[i])
        for axis in (0, 1):
            lo, hi = sorted((float(a[axis]), float(b[axis])))
            for k in range(math.ceil(lo / tile_size), math.floor(hi / tile_size) + 1):
                value = k * tile_size
                if lo < value < hi:
                    t = (value - float(a[axis])) / (float(b[axis]) - float(a[axis]))
                    result.append(float(ring.cum[i]) + t * length)
    return np.array(sorted(result))


def stretch_tile(ring: Ring, s0: float, s1: float, tile_size: float) -> str:
    """The tile of the ring stretch s0..s1 (straight, s1 > s0): the grid cell containing
    its midpoint moved `INWARD_M` into the playable area, so a stretch on a grid line or
    on the build area's edge belongs to the tile on its playable side."""
    mid = (s0 + s1) / 2
    point = ring.point_at(mid) - ring.normal(ring.segment_index(mid)) * INWARD_M
    return _tile_id(math.floor(point[0] / tile_size), math.floor(point[1] / tile_size))


def ring_stretches(ring: Ring, tile_size: float) -> list[tuple[float, float]]:
    """The ring cut at its vertices and its tile grid crossings."""
    cuts = np.unique(np.concatenate([ring.vertex_s, grid_crossings(ring, tile_size)]))
    ends = [*cuts[1:], ring.length]
    return [(float(a), float(b)) for a, b in zip(cuts, ends, strict=True) if b - a > SNAP_S_M]


def ring_tile_ids(ring: Ring, tile_size: float, written: set[str] | frozenset[str]) -> list[str]:
    """The written tiles the boundary passes through: those holding a stretch of the ring
    by `stretch_tile`, sorted. Every one of them has an `edge_cliff-col` node."""
    ids = {stretch_tile(ring, a, b, tile_size) for a, b in ring_stretches(ring, tile_size)}
    return sorted(ids & set(written))


def _box_distance(point: np.ndarray, bounds: BBox) -> float:
    dx = max(bounds.min_x - point[0], 0.0, point[0] - bounds.max_x)
    dy = max(bounds.min_y - point[1], 0.0, point[1] - bounds.max_y)
    return math.hypot(dx, dy)


def _tile_order(tile_id: str) -> tuple[int, int]:
    i, j = (int(v) for v in tile_id.removeprefix("tile_x").split("_y"))
    return (j, i)


def nearest_tile(point: np.ndarray, candidates: Sequence[str], cells: Mapping[str, BBox]) -> str:
    """The candidate tile nearest to `point` (2D distance to its bounds; ties in manifest
    order, by row, then column)."""
    return min(candidates, key=lambda t: (_box_distance(point, cells[t]), _tile_order(t)))


# The terrain's edge along the ring.


@dataclass(frozen=True)
class Profile:
    """The terrain mesh's height along the ring: piecewise linear in `s` through the
    vertices of the terrain edges that lie on the ring. `ring_profile` makes sure terrain
    edges cover the whole ring, so consecutive points are always joined by one."""

    s: np.ndarray  # sorted, padded by one point on each side over the ring's start
    z: np.ndarray
    length: float

    def at(self, s: np.ndarray | float) -> np.ndarray:
        if len(self.s) == 0:
            raise CliffError("the terrain profile along the boundary is empty")
        return np.interp(np.mod(np.asarray(s, dtype=float), self.length), self.s, self.z)

    def breaks(self, a: float, b: float) -> np.ndarray:
        """The profile's vertices strictly between a and b (a < b, unwrapped)."""
        if len(self.s) == 0:
            return np.zeros(0)
        result = []
        for k in (-1, 0, 1):
            s = self.s + k * self.length
            result.append(s[(s > a + SNAP_S_M) & (s < b - SNAP_S_M)])
        return np.unique(np.concatenate(result))


def ring_profile(ring: Ring, chunks: Sequence[TerrainChunk]) -> Profile:
    """The terrain edge along the ring: every terrain mesh edge used by one triangle whose
    ends and midpoint lie on the ring (within `ON_RING_M`).

    The terrain always reaches the boundary (the build area's edge, the test-area square
    or the cut along a custom polygon), so a stretch of the ring longer than
    `PROFILE_GAP_M` that no terrain edge covers is an error rather than a stretch to
    interpolate the skirt over."""
    line = ring.line()
    shapely.prepare(line)
    s_values: list[float] = []
    z_values: list[float] = []
    covered: list[tuple[float, float]] = []
    for chunk in chunks:
        v = np.asarray(chunk.vertices, dtype=float)
        f = np.asarray(chunk.faces, dtype=np.int64)
        if len(f) == 0:
            continue
        edges = np.sort(np.concatenate([f[:, [0, 1]], f[:, [1, 2]], f[:, [2, 0]]]), axis=1)
        unique, counts = np.unique(edges, axis=0, return_counts=True)
        border = unique[counts == 1]
        ends = np.unique(border)
        on = np.zeros(len(v), dtype=bool)
        on[ends] = shapely.distance(shapely.points(v[ends, :2]), line) <= ON_RING_M
        border = border[on[border[:, 0]] & on[border[:, 1]]]
        if len(border) == 0:
            continue
        mid = (v[border[:, 0], :2] + v[border[:, 1], :2]) / 2
        border = border[shapely.distance(shapely.points(mid), line) <= ON_RING_M]
        projected = {
            int(index): ring.project(float(v[index, 0]), float(v[index, 1]))
            for index in np.unique(border)
        }
        for index, s_index in projected.items():
            s_values.append(s_index)
            z_values.append(float(v[index, 2]))
        for a, b in border:
            covered += _circular_span(projected[int(a)], projected[int(b)], ring.length)
    gaps = [
        (a, b)
        for a, b in ring.complement(merge_touching(covered, ring.length))
        if b - a > PROFILE_GAP_M
    ]
    if gaps:
        described = [
            f"s {a:.3f}..{b:.3f} from {np.round(ring.point_at(a), 3).tolist()}" for a, b in gaps[:5]
        ]
        raise CliffError(
            f"no terrain edge along {len(gaps)} stretch(es) of the boundary "
            f"({sum(b - a for a, b in gaps):.3f} m), so the skirt can't follow the terrain "
            f"there: {described}"
        )
    s = np.asarray(s_values)
    z = np.asarray(z_values)
    order = np.lexsort((z, s))
    s, z = s[order], z[order]
    keep = np.concatenate([[True], np.diff(s) > 1e-12])
    s, z = s[keep], z[keep]
    s = np.concatenate([[s[-1] - ring.length], s, [s[0] + ring.length]])
    z = np.concatenate([[z[-1]], z, [z[0]]])
    return Profile(s, z, ring.length)


def _circular_span(s0: float, s1: float, length: float) -> list[tuple[float, float]]:
    """The shorter way along the ring between two `s` values, as intervals in
    [0, length] (two of them when it passes the ring's start)."""
    lo, hi = sorted((s0, s1))
    if hi - lo <= length / 2:
        return [(lo, hi)]
    return [(hi, length), (0.0, lo)]


# Ledge shapes.


@dataclass(frozen=True)
class Part:
    """A ledge shape: a piece's quad or a corner's kite (or clipped mitre), with its plane
    (height = base + gradient . (p - origin)), the ring geometry it grows from (for the
    nearest-part rule) and its tile."""

    kind: str  # "piece" or "corner"
    shape: Polygon
    origin: tuple[float, float]
    base: float
    gradient: tuple[float, float]
    source: BaseGeometry
    tile: str
    s0: float = 0.0  # pieces: the stretch of the ring (unwrapped)
    s1: float = 0.0

    def height(self, xy: np.ndarray) -> np.ndarray:
        p = np.asarray(xy, dtype=float).reshape(-1, 2)
        return (
            self.base
            + (p[:, 0] - self.origin[0]) * self.gradient[0]
            + (p[:, 1] - self.origin[1]) * self.gradient[1]
        )

    def normal(self) -> np.ndarray:
        n = np.array([-self.gradient[0], -self.gradient[1], 1.0])
        return np.asarray(n / np.linalg.norm(n))


def _turn(ring: Ring, i: int) -> float:
    """z of (direction into vertex i) x (direction out of it): > 0 convex (CCW ring)."""
    d0, d1 = ring.direction(i - 1), ring.direction(i)
    return float(d0[0] * d1[1] - d0[1] * d1[0])


def _snap_to_vertex(ring: Ring, s: float) -> float:
    """`s` moved onto a ring vertex (or the ring's end) when within `SNAP_S_M` of it."""
    base = math.floor(s / ring.length) * ring.length
    for v in (*ring.vertex_s, ring.length):
        if abs(s - base - v) <= SNAP_S_M:
            return base + float(v)
    return s


def _on_segment(ring: Ring, segment: int, u: float) -> np.ndarray:
    """The point `u` metres along segment `segment`: exactly its start or end vertex when
    within `SNAP_S_M` of it."""
    a, b = ring.segment(segment)
    length = float(ring.cum[segment + 1] - ring.cum[segment])
    if u <= SNAP_S_M:
        return np.array(a, dtype=float)
    if u >= length - SNAP_S_M:
        return np.array(b, dtype=float)
    return np.asarray(a + ring.direction(segment) * u)


def _pieces(
    ring: Ring, items: Sequence[RingItem], tiling: Tiling, ledge_m: float
) -> tuple[list[Part], list[str]]:
    """The ledge quads, one per piece, and the ids of the pieces moved to another tile."""
    if not items:
        raise CliffError("the boundary has no roadblocks or fences to build the cliff from")
    crossings = grid_crossings(ring, tiling.tile_size)
    candidates = ring_tile_ids(ring, tiling.tile_size, tiling.written)
    if not candidates:
        raise CliffError("no written tile holds a stretch of the boundary")
    starts = [_snap_to_vertex(ring, item.s_start) for item in items]
    parts: list[Part] = []
    moved: list[str] = []
    for k, item in enumerate(items):
        a = starts[k]
        b = starts[k + 1] if k + 1 < len(items) else starts[0] + ring.length
        if b - a <= SNAP_S_M:
            continue
        inner = [
            float(v + m * ring.length)
            for m in (0, 1, 2)
            for v in (*ring.vertex_s, *crossings)
            if a + SNAP_S_M < v + m * ring.length < b - SNAP_S_M
        ]
        cuts = [a, *sorted(set(inner)), b]
        for s0, s1 in zip(cuts[:-1], cuts[1:], strict=True):
            y0 = item.y_start + (item.y_end - item.y_start) * (s0 - a) / (b - a)
            y1 = item.y_start + (item.y_end - item.y_start) * (s1 - a) / (b - a)
            middle = (s0 + s1) / 2
            segment = ring.segment_index(middle)
            offset = math.floor(middle / ring.length) * ring.length + float(ring.cum[segment])
            p0 = _on_segment(ring, segment, s0 - offset)
            p1 = _on_segment(ring, segment, s1 - offset)
            normal = ring.normal(segment)
            outer0, outer1 = p0 + normal * ledge_m, p1 + normal * ledge_m
            tile = stretch_tile(ring, s0, s1, tiling.tile_size)
            if tile not in tiling.written:
                moved.append(f"{item.id} s {s0:.3f}..{s1:.3f}: {tile}")
                tile = nearest_tile((p0 + p1) / 2, candidates, tiling.cells)
            span = float(np.hypot(*(p1 - p0)))
            slope = (y1 - y0) / span if span > 0 else 0.0
            direction = ring.direction(segment)
            parts.append(
                Part(
                    "piece",
                    Polygon([p0, p1, outer1, outer0]),
                    (float(p0[0]), float(p0[1])),
                    float(y0),
                    (float(direction[0] * slope), float(direction[1] * slope)),
                    LineString([p0, p1]),
                    tile,
                    s0,
                    s1,
                )
            )
    return parts, moved


def _corners(
    ring: Ring, pieces: Sequence[Part], ledge_m: float
) -> tuple[list[Part], list[LineString], Counter[str]]:
    """The kite (or clipped mitre) at each convex corner, at the height of the piece ending
    there, the bisector line of each concave corner, and how many of each kind."""
    ending: dict[int, Part] = {}
    for piece in pieces:
        vertex = ring.vertex_at(piece.s1, SNAP_S_M)
        if vertex is not None:
            ending[vertex] = piece
    corners: list[Part] = []
    bisectors: list[LineString] = []
    counts: Counter[str] = Counter()
    for v in range(ring.n):
        turn = _turn(ring, v)
        corner = ring.xy[v]
        n0, n1 = ring.normal(v - 1), ring.normal(v)
        m = n0 + n1
        norm = float(np.hypot(*m))
        if abs(turn) <= COLLINEAR and float(np.dot(n0, n1)) > 0:
            counts["straight"] += 1
            continue
        if norm < 1e-9:  # turning straight back: the corner's band is a half disc ahead
            m, reach = ring.direction(v - 1), math.inf
        else:
            m = m / norm
            reach = ledge_m / max(float(np.dot(m, n0)), 1e-12)
        if turn < 0:
            counts["concave"] += 1
            if math.isfinite(reach):  # a simple ring never turns straight back inwards
                bisectors.append(LineString([corner, corner + m * reach]))
            continue
        piece = ending.get(v)
        if piece is None:
            raise CliffError(f"no cliff piece ends at boundary vertex {v} {corner.tolist()}")
        first, second = corner + n0 * ledge_m, corner + n1 * ledge_m
        if reach <= MITRE_LIMIT * ledge_m:
            counts["mitre"] += 1
            outline = [corner, first, corner + m * reach, second]
        else:
            # Clipped mitre: the kite cut square to the bisector at MITRE_LIMIT ledge widths,
            # so it still covers every point within ledge_m of the corner.
            counts["clipped_mitre"] += 1
            limit = MITRE_LIMIT * ledge_m
            d0, d1 = ring.direction(v - 1), ring.direction(v)
            along0 = (limit - float(np.dot(first - corner, m))) / float(np.dot(d0, m))
            along1 = (limit - float(np.dot(second - corner, m))) / float(-np.dot(d1, m))
            outline = [corner, first, first + d0 * along0, second - d1 * along1, second]
        height = float(piece.height(np.array([corner]))[0])
        corners.append(
            Part(
                "corner",
                Polygon(outline),
                (float(corner[0]), float(corner[1])),
                height,
                (0.0, 0.0),
                Point(corner),
                piece.tile,
            )
        )
    return corners, bisectors, counts


# Walls.


def _oriented(triangle: np.ndarray, normal: np.ndarray) -> np.ndarray:
    """`triangle` (3, 3) wound so its geometric normal points along `normal`."""
    cross = np.cross(triangle[1] - triangle[0], triangle[2] - triangle[0])
    return triangle if float(np.dot(cross, normal)) >= 0 else triangle[[0, 2, 1]]


def _area(triangle: np.ndarray) -> float:
    cross = np.cross(triangle[1] - triangle[0], triangle[2] - triangle[0])
    return 0.5 * float(np.linalg.norm(cross))


def wall(
    p: np.ndarray, q: np.ndarray, f: tuple[float, float], g: tuple[float, float], nu: np.ndarray
) -> list[tuple[np.ndarray, np.ndarray]]:
    """The vertical wall over the segment p -> q (2D) between the heights `f` (at p, q; the
    surface on the side the unit 2D vector `nu` points to) and `g` (the other side), as
    (triangle, normal) pairs. Where f is lower the wall faces `nu`, where g is lower it
    faces `-nu`; where they cross it is split there."""
    d_p, d_q = g[0] - f[0], g[1] - f[1]
    if abs(d_p) <= MIN_STEP_M and abs(d_q) <= MIN_STEP_M:
        return []
    facing = np.array([nu[0], nu[1], 0.0])

    def tri(*corners: tuple[np.ndarray, float], sign: float) -> list[tuple[np.ndarray, np.ndarray]]:
        triangle = np.array([[c[0][0], c[0][1], c[1]] for c in corners], dtype=float)
        if _area(triangle) <= MIN_AREA_M2:
            return []
        normal = facing * sign
        return [(_oriented(triangle, normal), normal)]

    if d_p * d_q < 0:
        t = d_p / (d_p - d_q)
        c = p + (q - p) * t
        h = f[0] + (f[1] - f[0]) * t
        return tri((p, f[0]), (p, g[0]), (c, h), sign=1.0 if d_p > 0 else -1.0) + tri(
            (c, h), (q, f[1]), (q, g[1]), sign=1.0 if d_q > 0 else -1.0
        )
    sign = 1.0 if d_p + d_q > 0 else -1.0
    return tri((p, f[0]), (q, f[1]), (q, g[1]), sign=sign) + tri(
        (p, f[0]), (q, g[1]), (p, g[0]), sign=sign
    )


@dataclass
class Cliff:
    """The cliff meshes per tile and the numbers for the report."""

    nodes: dict[str, list[MeshNode]]
    report: dict[str, Any] = field(default_factory=dict)


def _label_cells(
    cells: Sequence[Polygon], parts: Sequence[Part], polygon: Polygon
) -> tuple[list[int], list[BaseGeometry]]:
    """Each cell's owner: `PLAY` inside the ring, else the part containing it whose ring
    geometry is nearest to the cell's representative point (ties: the first part), else
    `OUT`; and the lines splitting the ledge cells that another covering part of a
    different height or tile is nearer to at one of their vertices (`_split_line`)."""
    shapes = [part.shape for part in parts]
    tree = shapely.STRtree(shapes)
    shapely.prepare(polygon)
    labels = []
    splits: list[BaseGeometry] = []
    for cell in cells:
        point = cell.point_on_surface()
        if polygon.contains(point):
            labels.append(PLAY)
            continue
        hits = sorted(int(k) for k in tree.query(point, predicate="intersects"))
        hits = [k for k in hits if shapes[k].covers(point)]
        if not hits:
            labels.append(OUT)
            continue
        owner = min(hits, key=lambda k: (parts[k].source.distance(point), k))
        labels.append(owner)
        if len(hits) > 1:
            split = _split_line(cell, parts[owner], [parts[k] for k in hits if k != owner])
            if split is not None:
                splits.append(split)
    return labels, splits


def _split_line(cell: Polygon, owner: Part, others: Sequence[Part]) -> BaseGeometry | None:
    """The line splitting `cell` between its owner and the other covering part that is
    nearest at one of the cell's vertices by more than `NEARER_M` (and differs in height
    or tile there), or None. The line runs where the two are equally near, between the two
    points of the cell's outline furthest apart where they are (found by bisection on the
    outline's edges), through `SPLIT_POINTS` points of that curve in between (found by
    bisection square to the chord): exact where both ring geometries are segments or both
    points (the curve is straight), a polyline on the parabola between a corner and a
    segment, refined by the next pass."""
    outline = [np.asarray(r.coords)[:-1] for r in (cell.exterior, *cell.interiors)]
    vertices = np.concatenate(outline)
    points = shapely.points(vertices)
    own = shapely.distance(owner.source, points)
    best: tuple[float, Part] | None = None
    for other in others:
        lead = float((own - shapely.distance(other.source, points)).max())
        if lead <= NEARER_M:
            continue
        step = np.abs(owner.height(vertices) - other.height(vertices)).max()
        if other.tile == owner.tile and step <= MIN_STEP_M:
            continue
        if best is None or lead > best[0]:
            best = (lead, other)
    if best is None:
        return None
    other = best[1]

    def f(p: np.ndarray) -> float:
        q = Point(p)
        return float(owner.source.distance(q) - other.source.distance(q))

    crossings: list[np.ndarray] = []
    for ring in outline:
        values = [f(p) for p in ring]
        for k in range(len(ring)):
            a, b = ring[k], ring[(k + 1) % len(ring)]
            fa, fb = values[k], values[(k + 1) % len(ring)]
            if abs(fa) <= NEARER_M:
                crossings.append(a)
            elif fa * fb < 0 and abs(fb) > NEARER_M:
                lo, hi, f_lo = a, b, fa
                for _ in range(60):
                    mid = (lo + hi) / 2
                    f_mid = f(mid)
                    if (f_mid < 0) == (f_lo < 0):
                        lo, f_lo = mid, f_mid
                    else:
                        hi = mid
                crossings.append((lo + hi) / 2)
    if len(crossings) < 2:
        return None
    pts = np.array(crossings)
    spread = np.hypot(*(pts[:, None, :] - pts[None, :, :]).transpose(2, 0, 1))
    i, j = np.unravel_index(int(np.argmax(spread)), spread.shape)
    if spread[i, j] <= NEARER_M:
        return None
    start, end = pts[i], pts[j]
    chord = end - start
    across = np.array([-chord[1], chord[0]]) / float(np.hypot(*chord))
    reach = float(np.hypot(*(np.ptp(vertices, axis=0))))
    curve = [start]
    for t in np.arange(1, SPLIT_POINTS + 1) / (SPLIT_POINTS + 1):
        c = start + chord * t
        f_c = f(c)
        point = c
        for sign in (1.0, -1.0):
            far = c + across * sign * reach
            if abs(f_c) > NEARER_M and f(far) * f_c < 0:
                lo, hi = c, far
                for _ in range(60):
                    mid = (lo + hi) / 2
                    if (f(mid) < 0) == (f_c < 0):
                        lo = mid
                    else:
                        hi = mid
                point = (lo + hi) / 2
                break
        curve.append(point)
    curve.append(end)
    line = shapely.intersection(LineString(curve), cell, grid_size=GRID_M)
    parts = [g for g in shapely.get_parts(line) if g.geom_type == "LineString" and g.length > 0]
    return shapely.union_all(parts) if parts else None


def _directed_edges(cell: Polygon) -> list[tuple[tuple[float, float], tuple[float, float]]]:
    """The cell's ring segments, each with the cell on its left (exterior counter-
    clockwise, holes clockwise)."""
    oriented = shapely.orient_polygons(cell)
    result = []
    for ring in (oriented.exterior, *oriented.interiors):
        coords = [(float(x), float(y)) for x, y in ring.coords]
        result += [(a, b) for a, b in zip(coords[:-1], coords[1:], strict=True) if a != b]
    return result


def build_cliff(
    ring: Ring,
    items: Sequence[RingItem],
    ledge_m: float,
    bottom_y: float,
    profile: Profile,
    tiling: Tiling,
) -> Cliff:
    """The cliff nodes of every written tile (`edge_cliff-col` and `edge_bottom`)."""
    if ledge_m <= 0:
        raise CliffError("cliff.ledge_m must be positive to build the cliff")
    pieces, moved = _pieces(ring, items, tiling, ledge_m)
    corners, bisectors, corner_counts = _corners(ring, pieces, ledge_m)
    parts = [*pieces, *corners]
    polygon = ring.polygon()

    linework: list[BaseGeometry] = [part.shape.exterior for part in parts] + bisectors
    linework.append(ring.line())
    splits: list[BaseGeometry] = []
    for passes in range(SPLIT_PASSES + 1):
        noded = shapely.union_all([*linework, *splits], grid_size=GRID_M)
        cells = [
            c for c in shapely.get_parts(shapely.polygonize(shapely.get_parts(noded))) if c.area > 0
        ]
        labels, unsplit = _label_cells(cells, parts, polygon)
        if not unsplit or passes == SPLIT_PASSES:
            break
        splits += unsplit

    triangles: dict[str, list[tuple[np.ndarray, np.ndarray]]] = {}
    counts: Counter[str] = Counter()

    def add(tile: str, kind: str, items_: list[tuple[np.ndarray, np.ndarray]]) -> None:
        if items_:
            triangles.setdefault(tile, []).extend(items_)
            counts[kind] += len(items_)

    owner_of: dict[tuple[tuple[float, float], tuple[float, float]], int] = {}
    for cell, label in zip(cells, labels, strict=True):
        for edge in _directed_edges(cell):
            owner_of[edge] = label
    ledge_cells = 0
    for cell, label in zip(cells, labels, strict=True):
        if label < 0:
            continue
        part = parts[label]
        ledge_cells += 1
        xy, local = triangulate(cell)
        normal = part.normal()
        z = part.height(xy)
        lifted = np.column_stack([xy, z])[local]
        add(
            part.tile,
            "ledge",
            [(_oriented(t, normal), normal) for t in lifted if _area(t) > MIN_AREA_M2],
        )

    gap = 0.0
    for (a, b), left in owner_of.items():
        right = owner_of.get((b, a), OUT)
        p, q = np.array(a), np.array(b)
        d = q - p
        length = float(np.hypot(*d))
        nu = np.array([d[1], -d[0]]) / length  # towards the right side
        if left == PLAY and right == OUT:
            gap += length
        if left < 0:
            continue
        part = parts[left]
        h = part.height(np.array([p, q]))
        if right == OUT:  # the face, down to the bottom
            add(part.tile, "face", wall(p, q, (bottom_y, bottom_y), (h[0], h[1]), nu))
        elif right == PLAY:  # the skirt, down (or up) to the terrain's edge
            add(part.tile, "skirt", _skirt(ring, profile, part, p, q, nu))
        elif right > left:  # a seam between two ledge cells of different heights
            other = parts[right].height(np.array([p, q]))
            add(part.tile, "seam", wall(p, q, (other[0], other[1]), (h[0], h[1]), nu))
    if gap > GAP_M:
        raise CliffError(f"the cliff ledge leaves {gap:.6f} m of the boundary uncovered")

    caps, cap_moved = _caps(polygon, cells, labels, tiling, bottom_y, ledge_m)
    down = np.array([0.0, 0.0, -1.0])
    nodes: dict[str, list[MeshNode]] = {}
    for tile in sorted(set(triangles) | set(caps), key=_tile_order):
        result: list[MeshNode] = []
        if tile in triangles:
            tris = np.array([t for t, _ in triangles[tile]])
            normals = np.array([n for _, n in triangles[tile]])
            result.append(flat_shaded(CLIFF_NODE, tris, normals, CLIFF_MATERIAL))
        if tile in caps:
            tris = np.array(caps[tile])
            counts["cap"] += len(tris)
            result.append(
                flat_shaded(BOTTOM_NODE, tris, np.tile(down, (len(tris), 1)), CLIFF_MATERIAL)
            )
        nodes[tile] = result
    missing = sorted(set(tiling.written) - set(caps))
    if missing:
        raise CliffError(f"no bottom cap under written tile(s) {missing[:5]}")
    total = sum(v for k, v in counts.items() if k != "cap")
    report = {
        "ledge_m": ledge_m,
        "bottom_y": round(bottom_y, 6),
        "mitre_limit": MITRE_LIMIT,
        "pieces": len(pieces),
        "corners": dict(sorted(corner_counts.items())),
        "ledge_cells": ledge_cells,
        "split_lines": len(splits),
        "cells_left_unsplit": len(unsplit),
        "triangles": {
            "ledge": counts["ledge"],
            "face": counts["face"],
            "seam": counts["seam"],
            "skirt": counts["skirt"],
            "edge_cliff": total,
            "edge_bottom": counts["cap"],
        },
        "pieces_moved_to_another_tile": moved,
        "caps_moved_to_another_tile": cap_moved,
        "tiles_with_cliff": sorted(triangles, key=_tile_order),
        "profile_points": max(len(profile.s) - 2, 0),
    }
    return Cliff(nodes, report)


def _skirt(
    ring: Ring, profile: Profile, part: Part, p: np.ndarray, q: np.ndarray, nu: np.ndarray
) -> list[tuple[np.ndarray, np.ndarray]]:
    """The wall on the ring segment p -> q between the terrain's edge (the profile, on the
    playable side `nu`) and the ledge, split at every profile vertex in between."""
    s_p, s_q = ring.project(*p), ring.project(*q)
    if s_q - s_p > ring.length / 2:
        s_p += ring.length
    elif s_p - s_q > ring.length / 2:
        s_q += ring.length
    lo, hi = sorted((s_p, s_q))
    inner = profile.breaks(lo, hi)
    if s_q < s_p:
        inner = inner[::-1]
    s = np.concatenate([[s_p], inner, [s_q]])
    t = (s - s_p) / (s_q - s_p) if s_q != s_p else np.zeros(len(s))
    points = p + (q - p) * t[:, None]
    terrain = profile.at(s)
    ledge = part.height(points)
    result = []
    for k in range(len(s) - 1):
        result += wall(
            points[k],
            points[k + 1],
            (float(terrain[k]), float(terrain[k + 1])),
            (float(ledge[k]), float(ledge[k + 1])),
            nu,
        )
    return result


def _caps(
    polygon: Polygon,
    cells: Sequence[Polygon],
    labels: Sequence[int],
    tiling: Tiling,
    bottom_y: float,
    ledge_m: float,
) -> tuple[dict[str, list[np.ndarray]], list[str]]:
    """The bottom cap's triangles per tile (facing down) and the cap parts moved to another
    tile: the playable area plus the ledge, cut into the grid cells, the outer ones grown
    past the build area's edge."""
    slab = shapely.union_all(
        [polygon, *(c for c, label in zip(cells, labels, strict=True) if label >= 0)],
        grid_size=GRID_M,
    )
    outer = tiling.area_bounds()
    reach = 2 * MITRE_LIMIT * ledge_m + 1.0
    candidates = sorted(tiling.written, key=_tile_order)
    caps: dict[str, list[np.ndarray]] = {}
    moved: list[str] = []
    for tile_id in sorted(tiling.cells, key=_tile_order):
        b = tiling.cells[tile_id]
        cell = shapely.box(
            b.min_x - (reach if b.min_x <= outer.min_x else 0.0),
            b.min_y - (reach if b.min_y <= outer.min_y else 0.0),
            b.max_x + (reach if b.max_x >= outer.max_x else 0.0),
            b.max_y + (reach if b.max_y >= outer.max_y else 0.0),
        )
        piece = shapely.intersection(slab, cell, grid_size=GRID_M)
        polygons = [p for p in shapely.get_parts(piece) if p.geom_type == "Polygon" and p.area > 0]
        if not polygons:
            continue
        owner = tile_id
        if tile_id not in tiling.written:
            centre = np.array(shapely.union_all(polygons).representative_point().coords[0])
            owner = nearest_tile(centre, candidates, tiling.cells)
            moved.append(f"{tile_id} -> {owner}")
        for part in polygons:
            xy, local = triangulate(part)
            flat = np.column_stack([xy, np.full(len(xy), bottom_y)])[local]
            caps.setdefault(owner, []).extend(t[[0, 2, 1]] for t in flat if _area(t) > MIN_AREA_M2)
    return caps, moved
