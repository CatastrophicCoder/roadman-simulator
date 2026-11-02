"""Cutting a tile's terrain mesh along a custom boundary polygon
(docs/specs/world-edge-pipeline.md, "The boundary").

The terrain is cut the way road surfaces are cut along the terrain triangles: every grid
triangle inside the polygon is kept as it is, every triangle the polygon's edge passes
through is replaced by its part inside the polygon (triangulated with earcut, each vertex
in the original triangle's plane), and every triangle outside is removed. So no terrain
lies outside the boundary, and what is left has exactly the grid terrain's heights.

Vertices the cut adds get their normals from the heightfield gradient, like every other
terrain vertex (ADR 0006), so no lighting seam appears along the cut. The default
boundary (the build area rectangle) never calls this: its terrain is unchanged.
"""

from dataclasses import dataclass

import numpy as np
import shapely
from shapely.geometry import Polygon

from mapgen.build.heightfield import Heightfield
from mapgen.build.roads.surface import MIN_AREA, plane_z, polygons_of, triangulate
from mapgen.build.terrain.base import TerrainChunk

__all__ = ["MERGE_M", "CutStats", "clip_chunk"]

# Vertices are merged by their (x, y) rounded to this grid, so the cut points two pieces
# share on a cut edge (computed once per triangle) become one vertex even when the two
# intersections differ in the last bits. Far below any real distance between vertices.
MERGE_M = 1e-9


def _key(x: float, y: float) -> tuple[int, int]:
    return (round(x / MERGE_M), round(y / MERGE_M))


@dataclass(frozen=True)
class CutStats:
    """What the cut did to one tile's grid triangles."""

    grid: int  # triangles of the uncut grid
    kept: int  # inside the polygon, kept unchanged
    cut: int  # crossed by the polygon's edge, replaced by their inside part (1+ pieces)
    removed: int  # outside, or only touching the polygon (no piece inside): removed
    pieces: int  # triangles that replaced the cut ones

    def as_dict(self) -> dict[str, int]:
        return {
            "grid_triangles": self.grid,
            "kept": self.kept,
            "cut": self.cut,
            "removed": self.removed,
            "pieces": self.pieces,
        }


def clip_chunk(
    chunk: TerrainChunk, polygon: Polygon, heightfield: Heightfield
) -> tuple[TerrainChunk, CutStats]:
    """`chunk` cut to `polygon` (local coordinates), and what the cut did.

    Kept vertices keep their order and normals; the vertices the cut adds follow them, in
    the order of the triangles they come from. A corner of a cut triangle inside the
    polygon reuses its original vertex, and the cut points neighbouring pieces share are
    one vertex (merged by (x, y) rounded to `MERGE_M`). The result can have no faces (a
    tile entirely outside the polygon). A triangle that only touches the polygon (no
    piece of positive area inside it) counts as removed.
    """
    vertices = np.asarray(chunk.vertices, dtype=float)
    faces = np.asarray(chunk.faces, dtype=np.int64)
    tris = vertices[faces]
    shapes = shapely.polygons(tris[:, :, :2])
    shapely.prepare(polygon)
    whole = shapely.contains(polygon, shapes)
    crossed = shapely.intersects(polygon, shapes) & ~whole

    used = np.unique(faces[whole]) if whole.any() else np.zeros(0, dtype=np.int64)
    # Original vertices by their rounded (x, y), so a cut piece reuses the corners it keeps.
    by_xy = {
        _key(float(vertices[i, 0]), float(vertices[i, 1])): int(i) for i in range(len(vertices))
    }
    new_index: dict[int, int] = {int(old): k for k, old in enumerate(used)}
    out_xyz: list[np.ndarray] = [vertices[used]]
    out_normals: list[np.ndarray] = []
    if chunk.normals is not None:
        out_normals.append(np.asarray(chunk.normals, dtype=float)[used])
    added: dict[tuple[int, int], int] = {}
    added_xyz: list[tuple[float, float, float]] = []
    count = len(used)
    piece_faces: list[np.ndarray] = []

    def index_of(x: float, y: float, z: float) -> int:
        nonlocal count
        key = _key(x, y)
        original = by_xy.get(key)
        if original is not None:
            if original not in new_index:
                new_index[original] = count
                added_xyz.append(tuple(vertices[original]))
                count += 1
            return new_index[original]
        if key not in added:
            added[key] = count
            added_xyz.append((x, y, z))
            count += 1
        return added[key]

    pieces = 0
    cut = 0
    for t in np.flatnonzero(crossed):
        produced = 0
        part = shapely.intersection(shapes[t], polygon)
        for piece in polygons_of(part):
            if piece.area <= MIN_AREA:
                continue
            xy, local = triangulate(piece)
            if len(local) == 0:
                continue
            z = plane_z(tris[t], xy)
            ids = np.array(
                [index_of(float(p[0]), float(p[1]), float(h)) for p, h in zip(xy, z, strict=True)],
                dtype=np.int64,
            )
            faces_k = ids[local]
            # Corners merged into one vertex leave a degenerate triangle: dropped.
            distinct = (faces_k[:, 0] != faces_k[:, 1]) & (faces_k[:, 1] != faces_k[:, 2])
            faces_k = faces_k[distinct & (faces_k[:, 0] != faces_k[:, 2])]
            piece_faces.append(faces_k)
            produced += len(faces_k)
        pieces += produced
        cut += produced > 0

    kept_faces = np.vectorize(new_index.__getitem__, otypes=[np.int64])(faces[whole])
    all_faces = np.concatenate([kept_faces.reshape(-1, 3), *piece_faces]).astype(np.int64)
    extra = np.array(added_xyz, dtype=float).reshape(-1, 3)
    all_vertices = np.concatenate([*out_xyz, extra])
    normals = None
    if chunk.normals is not None:
        normals = np.concatenate([*out_normals, heightfield.normals(extra[:, 0], extra[:, 1])])
        # Original vertices reused by a cut piece keep their own normals.
        originals = [old for old, k in new_index.items() if k >= len(used)]
        for old in originals:
            normals[new_index[old]] = np.asarray(chunk.normals, dtype=float)[old]
    stats = CutStats(
        grid=len(faces),
        kept=int(whole.sum()),
        cut=cut,
        removed=int(len(faces) - whole.sum() - cut),
        pieces=pieces,
    )
    rows = chunk.rows if chunk.rows is not None else np.unique(vertices[:, 1])
    return TerrainChunk(all_vertices, all_faces, normals, rows), stats
