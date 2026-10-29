"""Road and path surfaces: buffered ground ways, merged, draped on the terrain mesh triangles.

Every included way that is neither a bridge nor a tunnel (and the parts of a way outside
its inferred bridge spans) is buffered to `width_m / 2`. `road` is the union of the
drivable ones, `path` the union of the others minus `road`. Ends that meet a bridge deck
get flat caps, so the ground surface doesn't run onto the deck; other ends are round.

Draping cuts the merged polygons along the actual triangles of each tile's terrain chunk
(not an assumed grid). Each piece is triangulated with earcut and lies in that triangle's
plane plus `surface_offset_m`, so the surface is parallel to the terrain mesh everywhere.
Points where the polygons cross tile edges are inserted once, before the per-tile cut, and
points on axis-aligned triangle edges take their height from that edge alone, so the
vertices on a shared tile edge are identical in both tiles.
"""

from collections.abc import Mapping, Sequence
from dataclasses import dataclass

import mapbox_earcut as earcut
import numpy as np
import shapely
from shapely.geometry import LineString, MultiPolygon, Point, Polygon, box
from shapely.geometry.base import BaseGeometry

from mapgen.build.area import BuildArea, Tile
from mapgen.build.heightfield import Heightfield
from mapgen.build.roads.graph import Piece
from mapgen.build.roads.ramps import RampField
from mapgen.build.terrain import TerrainChunk

QUAD_SEGS = 4  # buffer segments per quarter circle (low-poly round caps and joins)
MIN_AREA = 1e-9  # m²; smaller polygon pieces and triangles are dropped


def plane_z(tri: np.ndarray, pts: np.ndarray) -> np.ndarray:
    """Heights of 2D points `pts` (k, 2) in the plane of the 3D triangle `tri` (3, 3).

    A point equal to a corner gets the corner's height. A point on an axis-aligned edge
    (same x, or same y, as both its ends) is interpolated along that edge only, with its
    ends in a fixed order, so neighbouring triangles (and tiles) give identical heights.
    """
    pts = np.asarray(pts, dtype=float).reshape(-1, 2)
    a, b, c = tri[0], tri[1], tri[2]
    det = (b[0] - a[0]) * (c[1] - a[1]) - (c[0] - a[0]) * (b[1] - a[1])
    if det == 0:
        z = np.full(len(pts), float(tri[:, 2].mean()))
    else:
        w1 = ((pts[:, 0] - a[0]) * (c[1] - a[1]) - (c[0] - a[0]) * (pts[:, 1] - a[1])) / det
        w2 = ((b[0] - a[0]) * (pts[:, 1] - a[1]) - (pts[:, 0] - a[0]) * (b[1] - a[1])) / det
        z = (1 - w1 - w2) * a[2] + w1 * b[2] + w2 * c[2]
    for p, q in ((a, b), (b, c), (c, a)):
        p, q = (p, q) if (p[0], p[1]) <= (q[0], q[1]) else (q, p)
        if p[0] == q[0] and p[1] != q[1]:
            on = (pts[:, 0] == p[0]) & (pts[:, 1] >= p[1]) & (pts[:, 1] <= q[1])
            t = (pts[on, 1] - p[1]) / (q[1] - p[1])
        elif p[1] == q[1] and p[0] != q[0]:
            on = (pts[:, 1] == p[1]) & (pts[:, 0] >= p[0]) & (pts[:, 0] <= q[0])
            t = (pts[on, 0] - p[0]) / (q[0] - p[0])
        else:
            continue
        z[on] = p[2] + (q[2] - p[2]) * t
    for corner in tri:
        z[(pts[:, 0] == corner[0]) & (pts[:, 1] == corner[1])] = corner[2]
    return z


@dataclass
class _TileMesh:
    tile: Tile
    triangles: np.ndarray  # (m, 3, 3)
    polygons: np.ndarray  # (m,) shapely triangles
    tree: shapely.STRtree


class TerrainSurface:
    """Heights of the terrain meshes (the tiles' actual triangles) at local points."""

    def __init__(self, area: BuildArea, tiles: Sequence[Tile], chunks: dict[str, TerrainChunk]):
        self.area = area
        self.meshes: dict[tuple[int, int], _TileMesh] = {}
        for tile in tiles:
            chunk = chunks[tile.id]
            tris = np.asarray(chunk.vertices, dtype=float)[np.asarray(chunk.faces)]
            polygons = shapely.polygons(tris[:, :, :2])
            self.meshes[(tile.i, tile.j)] = _TileMesh(
                tile, tris, polygons, shapely.STRtree(polygons)
            )

    def mesh(self, tile: Tile) -> _TileMesh:
        return self.meshes[(tile.i, tile.j)]

    def height(self, xy: np.ndarray) -> np.ndarray:
        """Terrain mesh heights at (n, 2) local points (clamped into the build area)."""
        b = self.area.bounds
        pts = np.asarray(xy, dtype=float).reshape(-1, 2).copy()
        pts[:, 0] = np.clip(pts[:, 0], b.min_x, b.max_x)
        pts[:, 1] = np.clip(pts[:, 1], b.min_y, b.max_y)
        result = np.full(len(pts), np.nan)
        tile_of = [self.area.tile_index(float(x), float(y)) for x, y in pts]
        for key in sorted({t for t in tile_of if t is not None}):
            mesh = self.meshes[key]
            idx = np.array([i for i, t in enumerate(tile_of) if t == key])
            sub = pts[idx]
            hits = mesh.tree.query(shapely.points(sub), predicate="intersects")
            chosen = np.full(len(idx), -1)
            for p, t in zip(hits[0], hits[1], strict=True):
                if chosen[p] < 0 or t < chosen[p]:
                    chosen[p] = t
            for p in np.flatnonzero(chosen < 0):  # numerically between triangles
                chosen[p] = int(mesh.tree.query_nearest(shapely.points(sub[p]))[0])
            for p, t in enumerate(chosen):
                result[idx[p]] = plane_z(mesh.triangles[t], sub[p : p + 1])[0]
        return result

    def max_along(self, p: np.ndarray, q: np.ndarray) -> float:
        """The highest terrain mesh height on the segment p-q (exact: at its crossings with
        the triangle edges and its ends), clipped to the build area."""
        b = self.area.bounds
        segment = shapely.intersection(LineString([p, q]), box(b.min_x, b.min_y, b.max_x, b.max_y))
        if segment.is_empty:
            return float(self.height(np.array([p, q])).max())
        best = -np.inf
        for mesh in self.meshes.values():
            candidates = mesh.tree.query(segment, predicate="intersects")
            if len(candidates) == 0:
                continue
            candidates = np.sort(candidates)
            for t, part in zip(
                candidates, shapely.intersection(segment, mesh.polygons[candidates]), strict=True
            ):
                coords = shapely.get_coordinates(part)
                if len(coords):
                    best = max(best, float(plane_z(mesh.triangles[t], coords).max()))
        return best if np.isfinite(best) else float(self.height(np.array([p, q])).max())


# Ground surfaces.


@dataclass(frozen=True)
class GroundRun:
    """A ground part of a piece: vertices `start..end`, with a flat or round cap per end."""

    piece: Piece
    start: int
    end: int
    flat_start: bool
    flat_end: bool

    @property
    def xy(self) -> np.ndarray:
        return self.piece.xy[self.start : self.end + 1]


def ground_runs(pieces: Sequence[Piece], bridge_end_keys: set[str]) -> list[GroundRun]:
    """The ground parts of every piece that is neither a bridge nor a tunnel.

    An end gets a flat cap when it meets a bridge span or is an area-edge cut.
    """
    runs: list[GroundRun] = []
    for piece in pieces:
        if piece.attrs.bridge or piece.attrs.tunnel:
            continue
        n = len(piece.xy)
        bounds = [0]
        for s, e in sorted(piece.inferred):
            bounds += [s, e]
        bounds.append(n - 1)
        flat = [piece.at_area_edge(i) or key in bridge_end_keys for i, key in enumerate(piece.keys)]
        for a, b in zip(bounds[0::2], bounds[1::2], strict=True):
            if b > a:
                runs.append(GroundRun(piece, a, b, flat[a], flat[b]))
    return runs


def buffer_run(run: GroundRun, decks: Mapping[str, BaseGeometry] | None = None) -> BaseGeometry:
    """The run buffered to half its width, with its caps.

    A run that ends at a node inside a bridge span (`decks`: node key -> the footprint of
    the decks there) loses the part under that deck, so it doesn't run beneath it.
    """
    half = run.piece.attrs.width_m / 2
    xy = run.xy
    shapes = [LineString(xy).buffer(half, cap_style="flat", quad_segs=QUAD_SEGS)]
    for flat_cap, point in ((run.flat_start, xy[0]), (run.flat_end, xy[-1])):
        if not flat_cap:
            shapes.append(Point(point).buffer(half, quad_segs=QUAD_SEGS))
    shape = shapely.union_all(shapes)
    for i in (run.start, run.end):
        deck = (decks or {}).get(run.piece.keys[i])
        if deck is not None:
            shape = shape.difference(deck)
    return shape


def merged_surfaces(
    runs: Sequence[GroundRun],
    decks: Mapping[str, BaseGeometry] | None = None,
    extra_road: Sequence[BaseGeometry] = (),
    extra_path: Sequence[BaseGeometry] = (),
) -> tuple[BaseGeometry, BaseGeometry]:
    """(road, path): the union of the drivable runs, and of the others minus road.

    `decks` is passed to `buffer_run`. `extra_road` / `extra_path` (the ramp zones beyond
    drivable / other deck ends) are added to the unions.
    """
    ordered = sorted(runs, key=lambda r: (r.piece.way_id, r.piece.index, r.start))
    road = shapely.union_all(
        [buffer_run(r, decks) for r in ordered if r.piece.attrs.drivable] + list(extra_road)
    )
    path = shapely.union_all(
        [buffer_run(r, decks) for r in ordered if not r.piece.attrs.drivable] + list(extra_path)
    )
    path = path.difference(road)
    return _polygonal(road), _polygonal(path)


def _polygonal(geom: BaseGeometry) -> BaseGeometry:
    polygons = [p for p in _polygons(geom) if p.area > MIN_AREA]
    if not polygons:
        return MultiPolygon()
    return MultiPolygon(polygons) if len(polygons) > 1 else polygons[0]


def _polygons(geom: BaseGeometry) -> list[Polygon]:
    if geom.is_empty:
        return []
    if isinstance(geom, Polygon):
        return [geom]
    return [p for g in getattr(geom, "geoms", []) for p in _polygons(g)]


# Tile edges.


def _insert_crossings(coords: np.ndarray, xs: np.ndarray, ys: np.ndarray) -> np.ndarray:
    """A ring's coordinates with a vertex added wherever a segment crosses a line x = xs[i]
    or y = ys[j]. The added vertex has that coordinate exactly."""
    out: list[np.ndarray] = [coords[0]]
    for p, q in zip(coords[:-1], coords[1:], strict=True):
        ts: list[tuple[float, int, float]] = []
        for axis, lines in ((0, xs), (1, ys)):
            lo, hi = sorted((p[axis], q[axis]))
            for value in lines[(lines > lo) & (lines < hi)]:
                ts.append(((value - p[axis]) / (q[axis] - p[axis]), axis, float(value)))
        for t, axis, value in sorted(ts):
            point = p + t * (q - p)
            point[axis] = value
            out.append(point)
        out.append(q)
    return np.array(out)


def insert_tile_crossings(geom: BaseGeometry, area: BuildArea) -> BaseGeometry:
    """`geom` with vertices on every tile edge line it crosses (see `_insert_crossings`)."""
    tiles = area.tiles()
    xs = np.array(sorted({t.bounds.min_x for t in tiles} | {t.bounds.max_x for t in tiles}))
    ys = np.array(sorted({t.bounds.min_y for t in tiles} | {t.bounds.max_y for t in tiles}))
    result = []
    for polygon in _polygons(geom):
        shell = _insert_crossings(np.asarray(polygon.exterior.coords), xs, ys)
        holes = [_insert_crossings(np.asarray(r.coords), xs, ys) for r in polygon.interiors]
        result.append(Polygon(shell, holes))
    if not result:
        return MultiPolygon()
    return MultiPolygon(result) if len(result) > 1 else result[0]


# Draping.


@dataclass
class SurfaceMesh:
    vertices: np.ndarray  # (n, 3) local x, y, z
    faces: np.ndarray  # (m, 3), counter-clockwise from above

    @property
    def area(self) -> float:
        v = self.vertices[self.faces]
        d1, d2 = v[:, 1, :2] - v[:, 0, :2], v[:, 2, :2] - v[:, 0, :2]
        return float(np.abs(d1[:, 0] * d2[:, 1] - d1[:, 1] * d2[:, 0]).sum() / 2)


def _triangulate(polygon: Polygon) -> tuple[np.ndarray, np.ndarray]:
    """(2D vertices, counter-clockwise triangles) of a polygon with holes, by earcut."""
    rings = [np.asarray(polygon.exterior.coords)[:-1, :2]]
    rings += [np.asarray(r.coords)[:-1, :2] for r in polygon.interiors]
    vertices = np.concatenate(rings)
    ends = np.cumsum([len(r) for r in rings]).astype(np.uint32)
    tris = np.asarray(earcut.triangulate_float64(vertices, ends), dtype=np.int64).reshape(-1, 3)
    if len(tris) == 0:
        return vertices, tris
    v = vertices[tris]
    cross = (v[:, 1, 0] - v[:, 0, 0]) * (v[:, 2, 1] - v[:, 0, 1]) - (v[:, 2, 0] - v[:, 0, 0]) * (
        v[:, 1, 1] - v[:, 0, 1]
    )
    tris = tris[np.abs(cross) / 2 > MIN_AREA]
    flip = cross[np.abs(cross) / 2 > MIN_AREA] < 0
    tris[flip] = tris[flip][:, [0, 2, 1]]
    return vertices, tris


def drape_tile(
    surface: BaseGeometry,
    tile: Tile,
    terrain: TerrainSurface,
    offset: float,
    field: RampField | None = None,
) -> SurfaceMesh | None:
    """The part of `surface` (already with tile-edge vertices) in the tile, draped.

    Each vertex is at its terrain triangle's plane + `offset`, then the ramp `field` is
    applied (it leaves heights outside ramp zones unchanged).

    The polygon is cut into horizontal strips at the terrain vertices' y values first, so
    each triangle intersection works on a small piece.
    """
    b = tile.bounds
    clipped = shapely.intersection(surface, box(b.min_x, b.min_y, b.max_x, b.max_y))
    if clipped.is_empty or clipped.area <= MIN_AREA:
        return None
    mesh = terrain.mesh(tile)
    ys = np.unique(mesh.triangles[:, :, 1])
    ys = ys[(ys >= b.min_y) & (ys <= b.max_y)]
    if len(ys) < 2 or ys[0] > b.min_y or ys[-1] < b.max_y:
        ys = np.unique(np.concatenate([ys, [b.min_y, b.max_y]]))
    shapely.prepare(clipped)
    strips = shapely.box(b.min_x, ys[:-1], b.max_x, ys[1:])
    hit = shapely.intersects(clipped, strips)
    vertices: list[np.ndarray] = []
    faces: list[np.ndarray] = []
    count = 0
    for strip in strips[hit]:
        part = shapely.intersection(clipped, strip)
        if part.is_empty or part.area <= MIN_AREA:
            continue
        candidates = mesh.tree.query(part, predicate="intersects")
        if len(candidates) == 0:
            continue
        candidates = np.sort(candidates)
        pieces = shapely.intersection(part, mesh.polygons[candidates])
        for tri_index, piece in zip(candidates, pieces, strict=True):
            for polygon in _polygons(piece):
                if polygon.area <= MIN_AREA:
                    continue
                xy, tris = _triangulate(polygon)
                if len(tris) == 0:
                    continue
                z = plane_z(mesh.triangles[tri_index], xy) + offset
                vertices.append(np.column_stack([xy, z]))
                faces.append(tris + count)
                count += len(xy)
    if not faces:
        return None
    all_vertices = np.concatenate(vertices)
    all_faces = np.concatenate(faces)
    if field is not None:
        all_vertices[:, 2] = field.apply(all_vertices[:, :2], all_vertices[:, 2])
    unique, inverse = np.unique(all_vertices, axis=0, return_inverse=True)
    return SurfaceMesh(unique, inverse.reshape(-1)[all_faces])


def surface_normals(mesh: SurfaceMesh, heightfield: Heightfield) -> np.ndarray:
    """Vertex normals from the heightfield gradient, as for the terrain (no seams)."""
    return heightfield.normals(mesh.vertices[:, 0], mesh.vertices[:, 1])
