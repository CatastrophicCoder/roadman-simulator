"""Checks on a built map pack, run on the staged pack before every swap (and by the tests).

Any failed check raises `PackCheckError`; the build then fails and the previous pack stays
untouched. The checks read the staged files back (tiles, manifest, road graph) and the
stage output in the work folder, so they test what the game will load.

- Tiles and buildings: tile files match the manifest; vertices are finite; terrain stays
  in its tile; collision nodes match `buildings.json`; every building's centroid is in its
  tile; no building base floats above the terrain mesh.
- Roads (docs/specs/roads-and-road-graph.md, criterion numbers in messages): the graph
  matches its schema and its report; nodes and points are finite and in the area;
  `length_m` is the polyline length; every edge starts and ends exactly at its nodes.
- Criterion 9 with ramps: every road and path vertex, and every graph point on a ground
  edge, is at `F(x, y)` within 1 mm, where `F` is the ramp field of `04-roads/report.json`
  (`build/roads/ramps.py`) applied to terrain mesh + `surface_offset_m`. Outside ramp zones
  `F` is exactly terrain + offset. Inside them `F` lies between terrain + offset and the
  highest deck-end height of the ramps there, and on a deck end edge it equals that
  deck end's height. No vertex is below terrain + offset (1 mm).
- Criterion 10: surfaces inside their tile, `road-col` / `path-col` where the merged
  surfaces reach a tile, identical vertices on shared tile edges, road/path overlap under
  0.01 m² per tile.
- Criterion 11: straight decks (driving surface between the line and 0.05 m above it,
  0.01 mm float32 slack), underside and railings, deck ends at the road surface height.
- Ramps: no vertical step over 2 cm between a deck end edge and the ramp surface beyond it,
  at the corners and every 0.1 m along the edge.
- Criterion 13b: terrain vertices inside deck footprints are the DEM-only terrain.

Heights are sampled on the actual triangles (the tiles' terrain meshes), not a grid.
"""

import json
import re
import struct
from dataclasses import dataclass
from pathlib import Path
from typing import Any

import jsonschema
import numpy as np
import shapely
from shapely.geometry import LineString, shape
from shapely.geometry.base import BaseGeometry

from mapgen.build.export_glb import TERRAIN_NODE, from_gltf
from mapgen.build.pack_manifest import committed_schema
from mapgen.config import RoadsConfig

TOLERANCE = 1e-6
MM = 1e-3  # criteria 8, 9, 10, 11: 1 mm
UM = 1e-6  # road_graph.json coordinates are rounded to 1 µm
FLOAT32_SLACK = 1e-5  # criterion 11: 0.01 mm, float32 rounding at ~100 m is under 0.008 mm
DECK_RISE = 0.05  # criterion 11: the driving surface is at most 0.05 m above the line
OVERLAP_M2 = 0.01  # criterion 10: road and path intersection area per tile
MAX_STEP_M = 0.02  # ramps: vertical gap between a deck end edge and the ramp surface
GRADE_SPACING_M = 0.25  # graded ramps: samples along the path
# Graded ramps: along a straight path the descent is linear, so the meshes reproduce it
# exactly; what is left is float32 rounding of heights near 100 m (under 8 µm per value,
# about 6e-5 over the 0.25 m samples) and the slight curvature of the descent where a path
# bends. 0.001 allows both with margin.
GRADE_TOL = 0.001
STEP_SPACING_M = 0.1  # ramps: samples along each deck end edge
ROAD_SURFACES = ("road-col", "path-col")
BRIDGE_NODE = re.compile(r"^bridge_osm_way_(\d+)(?:_p\d+)?-col$")
SPAN_END_ID = re.compile(r"^span:way/(\d+):(\d+)$")

_GLB_DTYPES = {5121: "u1", 5123: "<u2", 5125: "<u4", 5126: "<f4"}
_GLB_WIDTH = {"SCALAR": 1, "VEC2": 2, "VEC3": 3, "VEC4": 4}


class PackCheckError(Exception):
    """A built pack failed a check. The message names the check and the place."""


def _require(condition: Any, message: Any) -> None:
    if not condition:
        raise PackCheckError(str(message))


# Reading tiles.


@dataclass
class TileMeshes:
    """A tile .glb read back: node name -> (local vertices, faces)."""

    nodes: dict[str, tuple[np.ndarray, np.ndarray]]

    def buildings(self) -> list[str]:
        return [n for n in self.nodes if n.startswith("bld_") and not n.endswith("-colonly")]


def read_glb_meshes(path: Path) -> TileMeshes:
    """Every named mesh node of a GLB: positions (local x east, y north, z up) and faces.

    Reads the buffers directly. Node transforms are applied (translation and matrix).
    """
    data = path.read_bytes()
    _require(len(data) >= 20 and data[:4] == b"glTF", f"{path} is not a GLB file")
    json_len = struct.unpack_from("<I", data, 12)[0]
    gltf = json.loads(data[20 : 20 + json_len])
    bin_start = 20 + json_len + 8

    def accessor(index: int) -> np.ndarray:
        acc = gltf["accessors"][index]
        view = gltf["bufferViews"][acc["bufferView"]]
        _require("byteStride" not in view, f"{path}: strided buffers aren't supported")
        width = _GLB_WIDTH[acc["type"]]
        start = bin_start + view.get("byteOffset", 0) + acc.get("byteOffset", 0)
        array = np.frombuffer(
            data, dtype=_GLB_DTYPES[acc["componentType"]], count=acc["count"] * width, offset=start
        )
        return array.reshape(acc["count"], width) if width > 1 else array

    nodes: dict[str, tuple[np.ndarray, np.ndarray]] = {}
    for node in gltf.get("nodes", []):
        if "mesh" not in node:
            continue
        primitive = gltf["meshes"][node["mesh"]]["primitives"][0]
        vertices = accessor(primitive["attributes"]["POSITION"]).astype(float)
        if "matrix" in node:
            m = np.array(node["matrix"], dtype=float).reshape(4, 4).T
            vertices = vertices @ m[:3, :3].T + m[:3, 3]
        _require(
            "rotation" not in node and "scale" not in node, f"{path}: unexpected node transform"
        )
        if "translation" in node:
            vertices = vertices + np.array(node["translation"], dtype=float)
        faces = accessor(primitive["indices"]).astype(np.int64).reshape(-1, 3)
        nodes[str(node.get("name", ""))] = (from_gltf(vertices), faces)
    return TileMeshes(nodes)


class TriangleSampler:
    """Heights of a z-up triangle mesh at 2D points: the triangles themselves, not a grid."""

    def __init__(self, vertices: np.ndarray, faces: np.ndarray) -> None:
        self.triangles = np.asarray(vertices, dtype=float)[np.asarray(faces)]
        self.polygons = shapely.polygons(self.triangles[:, :, :2])
        self.tree = shapely.STRtree(self.polygons)

    def height(self, xy: np.ndarray, distance: float = 1e-6) -> np.ndarray:
        """Heights at (n, 2) points; NaN where no triangle is within `distance`."""
        pts = np.asarray(xy, dtype=float).reshape(-1, 2)
        result = np.full(len(pts), np.nan)
        if len(pts) == 0 or len(self.triangles) == 0:
            return result
        hits = self.tree.query(shapely.points(pts), predicate="dwithin", distance=distance)
        if hits.shape[1] == 0:
            return result
        # The first triangle (lowest index) per point.
        order = np.lexsort((hits[1], hits[0]))
        p_idx, t_idx = hits[0][order], hits[1][order]
        first = np.concatenate([[True], p_idx[1:] != p_idx[:-1]])
        p_idx, t_idx = p_idx[first], t_idx[first]
        tri = self.triangles[t_idx]
        a, b, c = tri[:, 0], tri[:, 1], tri[:, 2]
        q = pts[p_idx]
        det = (b[:, 0] - a[:, 0]) * (c[:, 1] - a[:, 1]) - (c[:, 0] - a[:, 0]) * (b[:, 1] - a[:, 1])
        det = np.where(det == 0, 1e-300, det)
        w1 = (q[:, 0] - a[:, 0]) * (c[:, 1] - a[:, 1]) - (c[:, 0] - a[:, 0]) * (q[:, 1] - a[:, 1])
        w2 = (b[:, 0] - a[:, 0]) * (q[:, 1] - a[:, 1]) - (q[:, 0] - a[:, 0]) * (b[:, 1] - a[:, 1])
        w1, w2 = w1 / det, w2 / det
        result[p_idx] = (1 - w1 - w2) * a[:, 2] + w1 * b[:, 2] + w2 * c[:, 2]
        return result


def local_points(godot: Any) -> np.ndarray:
    """Godot [x, y_up, z] (z = -north) to local (east, north, up)."""
    p = np.asarray(godot, dtype=float).reshape(-1, 3)
    return np.column_stack([p[:, 0], -p[:, 2], p[:, 1]])


def footprint_samples(geometry: BaseGeometry, spacing: float = 1.0) -> np.ndarray:
    """Footprint vertices plus interior points on a `spacing` grid (world coordinates)."""
    polygons = [geometry] if geometry.geom_type == "Polygon" else list(geometry.geoms)
    points = [np.asarray(p.exterior.coords)[:, :2] for p in polygons]
    min_x, min_y, max_x, max_y = geometry.bounds
    gx, gy = np.meshgrid(np.arange(min_x, max_x, spacing), np.arange(min_y, max_y, spacing))
    grid = np.column_stack([gx.ravel(), gy.ravel()])
    points.append(grid[shapely.contains_xy(geometry, grid[:, 0], grid[:, 1])])
    return np.vstack(points)


def _json(path: Path) -> Any:
    return json.loads(path.read_text(encoding="utf-8"))


@dataclass
class PackSummary:
    tile_count: int
    building_count: int
    roads: "RoadSummary | None" = None


def check_pack(pack_dir: Path, work_dir: Path, roads: RoadsConfig | None = None) -> PackSummary:
    """Every check. A map with a `roads` block also gets the road checks (`check_roads`);
    without one, `road_graph.json` must be empty and the tiles have no road meshes."""
    manifest = _json(pack_dir / "manifest.json")
    try:
        jsonschema.validate(manifest, committed_schema())
    except jsonschema.ValidationError as exc:
        raise PackCheckError(f"manifest.json: {exc.message}") from exc
    buildings = _json(pack_dir / "buildings.json")["buildings"]
    load_report = _json(work_dir / "01-load" / "report.json")
    features = _json(work_dir / "03-buildings" / "buildings.geojson")["features"]
    origin = np.array([manifest["origin"]["x"], manifest["origin"]["y"]])
    geometries = {f["properties"]["id"]: shape(f["geometry"]) for f in features}
    by_node = {b["node"]: b for b in buildings}

    _require(
        {p.name for p in (pack_dir / "tiles").iterdir()}
        == {Path(t["file"]).name for t in manifest["tiles"]},
        "tile files don't match the manifest",
    )
    tiles = {t["id"]: (t, read_glb_meshes(pack_dir / t["file"])) for t in manifest["tiles"]}
    samplers = {tid: TriangleSampler(*m.nodes[TERRAIN_NODE]) for tid, (_, m) in tiles.items()}
    glb_buildings = 0
    for tid, (tile, meshes) in tiles.items():
        min_x, min_y, max_x, max_y = tile["bounds_local"]
        for name, (vertices, _) in meshes.nodes.items():
            _require(np.all(np.isfinite(vertices)), f"{tid}/{name} has NaN or inf")
        terrain_v, _ = meshes.nodes[TERRAIN_NODE]
        _require(
            terrain_v[:, 0].min() >= min_x - TOLERANCE
            and terrain_v[:, 0].max() <= max_x + TOLERANCE
            and terrain_v[:, 1].min() >= min_y - TOLERANCE
            and terrain_v[:, 1].max() <= max_y + TOLERANCE,
            f"{tid}: terrain outside the tile",
        )
        names = meshes.buildings()
        colonly = {n for n in meshes.nodes if n.endswith("-colonly")}
        expected = {c for n in names for c in by_node[n]["collision_nodes"]}
        _require(colonly == expected, f"{tid}: collision nodes don't match buildings.json")
        _require(len(names) == tile["building_count"], f"{tid}: building count")
        glb_buildings += len(names)
        samples: list[np.ndarray] = []
        owners: list[np.ndarray] = []
        for k, name in enumerate(names):
            building = by_node[name]
            collisions = building["collision_nodes"]
            _require(collisions, f"{name} has no -colonly partner")
            for collision in collisions:
                _require(
                    collision.startswith(name) and collision.endswith("-colonly"),
                    f"{name}: collision node {collision}",
                )
                _require(collision in meshes.nodes, f"{collision} missing from {tid}")
            _require(building["tile"] == tid, f"{name}: tile {building['tile']} != {tid}")
            world = geometries[building["id"]]
            cx, cy = world.centroid.x - origin[0], world.centroid.y - origin[1]
            _require(min_x <= cx <= max_x and min_y <= cy <= max_y, f"{name} centroid off tile")
            pts = footprint_samples(world) - origin
            inside = (
                (pts[:, 0] >= min_x)
                & (pts[:, 0] <= max_x)
                & (pts[:, 1] >= min_y)
                & (pts[:, 1] <= max_y)
            )
            samples.append(pts[inside])
            owners.append(np.full(int(inside.sum()), k))
        if names:
            # The base never floats: terrain under the footprint is at or above base_z.
            pts, owner = np.concatenate(samples), np.concatenate(owners)
            z = samplers[tid].height(pts, distance=1e-9)
            base = np.array([by_node[n]["base_z"] for n in names])[owner]
            low = ~np.isnan(z) & (z < base - 1e-3)
            _require(not low.any(), f"{names[int(owner[low][0])] if low.any() else ''} floats")
    _require(
        load_report["footprints"]["in_area"] == glb_buildings == len(buildings),
        "building counts differ between the load report, the tiles and buildings.json",
    )
    road_summary = None
    if roads is not None:
        road_summary = check_roads(pack_dir, work_dir, roads, tiles=tiles, samplers=samplers)
    else:
        graph = _json(pack_dir / "road_graph.json")
        _require(not graph["nodes"] and not graph["edges"], "no roads block: graph not empty")
        for tid, (_, meshes) in tiles.items():
            extra = [n for n in meshes.nodes if n in ROAD_SURFACES or BRIDGE_NODE.match(n)]
            _require(not extra, f"no roads block, but {tid} has {extra}")
    return PackSummary(len(manifest["tiles"]), glb_buildings, road_summary)


# Roads.


@dataclass
class Chain:
    """Bridge edges joined into one span: centre line (local x, y, z) and its distances."""

    edges: list[dict[str, Any]]
    points: np.ndarray
    s: np.ndarray
    end_nodes: tuple[str, str]

    @property
    def ways(self) -> set[int]:
        return {e["osm_way"] for e in self.edges}

    @property
    def length(self) -> float:
        return float(self.s[-1])

    def line_z(self, s: np.ndarray) -> np.ndarray:
        z0, z1 = self.points[0, 2], self.points[-1, 2]
        return z0 + (z1 - z0) * np.clip(np.asarray(s) / self.length, 0.0, 1.0)

    def project(self, xy: np.ndarray) -> tuple[np.ndarray, np.ndarray]:
        """(distance along, signed distance to the left) of 2D points."""
        line = LineString(self.points[:, :2])
        s = shapely.line_locate_point(line, shapely.points(xy))
        k = np.clip(np.searchsorted(self.s, s, side="right") - 1, 0, len(self.s) - 2)
        d = self.points[k + 1, :2] - self.points[k, :2]
        d /= np.linalg.norm(d, axis=1, keepdims=True)
        foot = self.points[k, :2] + d * (s - self.s[k])[:, None]
        rel = xy - foot
        return s, d[:, 0] * rel[:, 1] - d[:, 1] * rel[:, 0]


def bridge_chains(graph: dict[str, Any]) -> list[Chain]:
    """The bridge spans of a road graph, rebuilt from the edges alone.

    Edges of one way that meet at a node belong to one span. Tagged bridge edges of
    different ways join at a node where exactly their two ends meet (no other edge).
    """
    nodes = {n["id"]: n for n in graph["nodes"]}
    ends: dict[str, list[int]] = {}
    for i, e in enumerate(graph["edges"]):
        for key in (e["from"], e["to"]):
            ends.setdefault(key, []).append(i)
    bridge = [i for i, e in enumerate(graph["edges"]) if e["bridge"]]
    parent = {i: i for i in bridge}

    def find(i: int) -> int:
        while parent[i] != i:
            parent[i] = parent[parent[i]]
            i = parent[i]
        return i

    for key, at in ends.items():
        on_bridge = [i for i in at if i in parent]
        if len(on_bridge) != 2 or nodes[key]["kind"] == "area_edge":
            continue
        a, b = (graph["edges"][i] for i in on_bridge)
        same_way = a["osm_way"] == b["osm_way"]
        # Two decks joined at a node nothing else uses: one span (a bridge of several
        # ways, or a deck extended along the road beyond its OSM end).
        only_pair = len(at) == 2
        if same_way or only_pair:
            parent[find(on_bridge[0])] = find(on_bridge[1])
    groups: dict[int, list[int]] = {}
    for i in bridge:
        groups.setdefault(find(i), []).append(i)

    chains = []
    for members in groups.values():
        edges = [graph["edges"][i] for i in members]
        count: dict[str, int] = {}
        for e in edges:
            for key in (e["from"], e["to"]):
                count[key] = count.get(key, 0) + 1
        free = sorted(k for k, n in count.items() if n == 1)
        _require(len(free) == 2, f"bridge span {[e['id'] for e in edges]} is not a simple chain")
        start = free[0]
        remaining = list(edges)
        pts: list[np.ndarray] = []
        key = start
        while remaining:
            (edge,) = [e for e in remaining if key in (e["from"], e["to"])]
            remaining.remove(edge)
            p = local_points(edge["points"])
            if edge["from"] != key:
                p = p[::-1]
            pts.append(p if not pts else p[1:])
            key = edge["to"] if edge["from"] == key else edge["from"]
        points = np.concatenate(pts)
        steps = np.linalg.norm(np.diff(points[:, :2], axis=0), axis=1)
        s = np.concatenate([[0.0], np.cumsum(steps)])
        chains.append(Chain(edges, points, s, (start, key)))
    return chains


@dataclass
class RoadSummary:
    edges: int
    nodes: int
    max_surface_error_m: float  # |surface vertex - expected surface (terrain + offset, ramps)|
    max_graph_error_m: float  # |ground graph point - expected surface|
    deck_rise_m: tuple[float, float]  # min and max of driving surface minus the line
    chains: list[Chain]
    deck_nodes: dict[str, str]  # deck node -> tile id
    max_step_m: float = 0.0  # largest gap between a deck end edge and the ramp surface
    ramp_ends: int = 0
    max_graded_grade: float = 0.0  # steepest measured along any graded ramp


def _terrain_at(
    xy: np.ndarray, tiles: dict[str, Any], samplers: dict[str, TriangleSampler]
) -> np.ndarray:
    """Terrain mesh heights at local points, from the first tile whose bounds hold each point."""
    result = np.full(len(xy), np.nan)
    for tid, (tile, _) in tiles.items():
        min_x, min_y, max_x, max_y = tile["bounds_local"]
        todo = np.isnan(result) & (xy[:, 0] >= min_x) & (xy[:, 0] <= max_x)
        todo &= (xy[:, 1] >= min_y) & (xy[:, 1] <= max_y)
        if todo.any():
            result[todo] = samplers[tid].height(xy[todo])
    return result


def _weak_components(edges: list[dict[str, Any]]) -> list[list[str]]:
    parent: dict[str, str] = {}

    def find(x: str) -> str:
        parent.setdefault(x, x)
        while parent[x] != x:
            parent[x] = parent[parent[x]]
            x = parent[x]
        return x

    for e in edges:
        parent[find(e["from"])] = find(e["to"])
    groups: dict[str, list[str]] = {}
    for e in edges:
        groups.setdefault(find(e["from"]), []).append(e["id"])
    return sorted((sorted(g) for g in groups.values()), key=lambda g: (-len(g), g[0]))


def check_connectivity_report(graph: dict[str, Any], report: dict[str, Any]) -> None:
    """Criterion 15 (every build): the report's components match the graph's drivable edges."""
    drivable = [e for e in graph["edges"] if e["drivable"]]
    components = _weak_components(drivable)
    weak = report["weak_components"]
    _require(report["drivable_edges"] == len(drivable), "15: drivable edge count")
    _require(
        report["edges"] == len(graph["edges"]) and report["nodes"] == len(graph["nodes"]),
        "15: edge or node count",
    )
    _require(weak["count"] == len(components), "15: component count")
    _require(weak["edge_counts"] == [len(c) for c in components], "15: component sizes")
    share = round(len(components[0]) / len(drivable), 4) if drivable else None
    _require(weak["largest_share"] == share, "15: largest share")
    _require(
        sorted(map(sorted, weak["other_components"])) == sorted(components[1:]),
        "15: other components",
    )


def _overlap_area(a: tuple[np.ndarray, np.ndarray], b: tuple[np.ndarray, np.ndarray]) -> float:
    """The area where two triangle meshes overlap seen from above."""
    pa = shapely.polygons(a[0][a[1]][:, :, :2])
    pb = shapely.polygons(b[0][b[1]][:, :, :2])
    if len(pa) == 0 or len(pb) == 0:
        return 0.0
    i, j = shapely.STRtree(pb).query(pa, predicate="intersects")
    if len(i) == 0:
        return 0.0
    return float(shapely.area(shapely.intersection(pa[i], pb[j])).sum())


def _check_tile_edges(tiles: dict[str, Any]) -> int:
    """Criterion 10: surface vertices on a shared tile edge match the neighbour's (1 mm)."""
    shared = 0
    ids = sorted(tiles)
    for a in ids:
        for b in ids:
            ba, bb = tiles[a][0]["bounds_local"], tiles[b][0]["bounds_local"]
            for axis in (0, 1):
                other = 1 - axis
                if ba[axis + 2] != bb[axis]:
                    continue  # b isn't the neighbour on a's max side along this axis
                lo, hi = max(ba[other], bb[other]), min(ba[other + 2], bb[other + 2])
                if hi <= lo:
                    continue
                value = ba[axis + 2]
                for name in ROAD_SURFACES:
                    sides = []
                    for tid in (a, b):
                        nodes = tiles[tid][1].nodes
                        v = nodes[name][0] if name in nodes else np.zeros((0, 3))
                        on = (np.abs(v[:, axis] - value) <= 1e-6) & (v[:, other] >= lo - MM)
                        sides.append(v[on & (v[:, other] <= hi + MM)])
                    va, vb = sides
                    _require(
                        len(va) == len(vb) == 0 or (len(va) and len(vb)),
                        f"{name}: {a}|{b} at {'xy'[axis]}={value}: vertices on one side only",
                    )
                    for x, y in ((va, vb), (vb, va)):
                        if len(x):
                            gaps = np.linalg.norm(x[:, None, :] - y[None, :, :], axis=2).min(1)
                            _require(
                                gaps.max() <= MM,
                                f"10: {name} {a}|{b}: a vertex has no partner ({gaps.max()} m)",
                            )
                    shared += len(va)
    return shared


class _DemTerrain:
    """Criterion 13b: tile terrain vertices built from the stage-1 DEM alone (float32)."""

    def __init__(self, work_dir: Path, origin: tuple[float, float]) -> None:
        self.work_dir, self.origin = work_dir, origin
        self._heightfield: Any = None

    def tile_vertices(self, tile: dict[str, Any]) -> np.ndarray:
        from mapgen.build.area import Tile
        from mapgen.build.terrain import MeshTerrainBuilder
        from mapgen.geo import BBox

        if self._heightfield is None:
            import rasterio

            from mapgen.build.heightfield import heightfield_from_raster
            from mapgen.sources.base import ElevationRaster

            with rasterio.open(self.work_dir / "01-load" / "dem.tif") as src:
                raster = ElevationRaster(src.read(1), src.transform, str(src.crs))
            self._heightfield, _ = heightfield_from_raster(raster, *self.origin)
        i, j = (int(v) for v in re.findall(r"-?\d+", tile["id"]))
        chunk = MeshTerrainBuilder().build_tile(
            self._heightfield, Tile(i, j, BBox(*tile["bounds_local"]))
        )
        return chunk.vertices.astype(np.float32).astype(float)


def check_roads(
    pack_dir: Path,
    work_dir: Path,
    roads: RoadsConfig,
    *,
    tiles: dict[str, Any] | None = None,
    samplers: dict[str, TriangleSampler] | None = None,
) -> RoadSummary:
    """Criteria 1, 8, 9, 10, 11, 13b, 15 (report), 16 and the ramp steps."""
    from mapgen.build.roads.model import committed_schema as road_schema
    from mapgen.build.roads.ramps import GradedRamp, Ramp, RampField

    for rel in (
        "04-roads/roads.geojson",
        "04-roads/report.json",
        "07-road-graph/graph.geojson",
        "07-road-graph/report.json",
    ):
        _require((work_dir / rel).is_file(), f"criterion 16: {rel} missing")
    manifest = _json(pack_dir / "manifest.json")
    graph = _json(pack_dir / "road_graph.json")
    try:
        jsonschema.validate(graph, road_schema())
    except jsonschema.ValidationError as exc:
        raise PackCheckError(f"criterion 1: road_graph.json: {exc.message}") from exc
    report = _json(work_dir / "04-roads" / "report.json")
    check_connectivity_report(graph, _json(work_dir / "07-road-graph" / "report.json"))
    offset = roads.surface_offset_m
    _require(report["surface_offset_m"] == offset, "04-roads report: surface offset")
    # Vertices are float32 in the tiles: points within 0.1 mm of a deck end edge are on it.
    # Graded ramp zones: the same 0.1 mm, so their boundary vertices count as inside.
    graded = [GradedRamp.from_dict(g) for g in report.get("graded_ramps", [])]
    field = RampField(
        [Ramp.from_dict(r) for r in report.get("ramps", [])],
        tol=0.1 * MM,
        graded=graded,
        zone_tol=0.1 * MM,
    )
    deck_edges = [*field.ramps, *(g.edge for g in field.graded)]
    origin = (manifest["origin"]["x"], manifest["origin"]["y"])
    if tiles is None:
        tiles = {t["id"]: (t, read_glb_meshes(pack_dir / t["file"])) for t in manifest["tiles"]}
    if samplers is None:
        samplers = {tid: TriangleSampler(*m.nodes[TERRAIN_NODE]) for tid, (_, m) in tiles.items()}
    bounds = np.array([t["bounds_local"] for t, _ in tiles.values()])
    area = (bounds[:, 0].min(), bounds[:, 1].min(), bounds[:, 2].max(), bounds[:, 3].max())

    # 8: finite points inside the area, length_m is the 3D polyline length.
    for node in graph["nodes"]:
        (p,) = local_points(node["position"])
        _require(np.all(np.isfinite(p)), f"8: {node['id']} not finite")
        _require(
            area[0] - MM <= p[0] <= area[2] + MM and area[1] - MM <= p[1] <= area[3] + MM,
            f"8: {node['id']} outside the area",
        )
        if node["kind"] == "area_edge":  # a cut, or an OSM node on the edge
            if node["osm_node"] is None:
                _require(node["id"].startswith("cut:way/"), f"4: {node['id']}")
            else:
                _require(node["id"] == f"osm:node/{node['osm_node']}", f"4: {node['id']}")
            on_edge = min(abs(p[0] - area[0]), abs(p[0] - area[2]))
            on_edge = min(on_edge, abs(p[1] - area[1]), abs(p[1] - area[3]))
            _require(on_edge <= 1e-6, f"4: {node['id']} isn't on the area edge")
        elif node["kind"] == "span_end":
            _require(
                node["osm_node"] is None and SPAN_END_ID.match(node["id"]),
                f"4: span_end node {node['id']}",
            )
        else:
            _require(node["id"] == f"osm:node/{node['osm_node']}", f"4: {node['id']}")
    positions = {n["id"]: np.array(n["position"], dtype=float) for n in graph["nodes"]}
    for edge in graph["edges"]:
        p = local_points(edge["points"])
        _require(np.all(np.isfinite(p)), f"8: {edge['id']} not finite")
        _require(
            np.all((p[:, 0] >= area[0] - MM) & (p[:, 0] <= area[2] + MM))
            and np.all((p[:, 1] >= area[1] - MM) & (p[:, 1] <= area[3] + MM)),
            f"8: {edge['id']} outside the area",
        )
        length = float(np.linalg.norm(np.diff(p, axis=0), axis=1).sum())
        _require(abs(edge["length_m"] - length) <= MM, f"8: {edge['id']} length_m")
        # Every edge starts and ends exactly at its nodes (1 µm: the file's rounding).
        for point, key in ((edge["points"][0], edge["from"]), (edge["points"][-1], edge["to"])):
            gap = float(np.abs(np.array(point, dtype=float) - positions[key]).max())
            _require(gap <= UM, f"{edge['id']} ends {gap} m from its node {key}")

    # 9: surfaces at terrain + offset with the ramps; never below terrain + offset.
    max_surface = 0.0
    surfaces: dict[str, dict[str, tuple[np.ndarray, np.ndarray]]] = {}
    deck_nodes: dict[str, str] = {}
    for tid, (tile, meshes) in tiles.items():
        min_x, min_y, max_x, max_y = tile["bounds_local"]
        for name in ROAD_SURFACES:
            if name not in meshes.nodes:
                continue
            v, f = meshes.nodes[name]
            h = samplers[tid].height(v[:, :2])
            _require(not np.isnan(h).any(), f"9: {tid}/{name}: vertices off the terrain")
            base = h + offset
            _require((v[:, 2] >= base - MM).all(), f"9: {tid}/{name} below terrain + offset")
            err = float(np.abs(v[:, 2] - field.apply(v[:, :2], base)).max())
            _require(err <= MM, f"criterion 9: {tid}/{name} is {err} m off its surface height")
            max_surface = max(max_surface, err)
            # 10: inside the tile.
            _require(
                v[:, 0].min() >= min_x - MM
                and v[:, 0].max() <= max_x + MM
                and v[:, 1].min() >= min_y - MM
                and v[:, 1].max() <= max_y + MM,
                f"10: {tid}/{name} outside its tile",
            )
            surfaces.setdefault(tid, {})[name] = (v, f)
        for name in meshes.nodes:
            if BRIDGE_NODE.match(name):
                _require(name not in deck_nodes, f"{name} in {deck_nodes.get(name)} and {tid}")
                deck_nodes[name] = tid
    # Nodes inside a bridge or tunnel span (two span edges meet there) are on the span's
    # line, also as the end of a ground edge that joins the span there.
    draped_tunnels = set(report.get("tunnels_under_decks", []))

    def graph_only_tunnel(edge: dict[str, Any]) -> bool:
        return bool(edge["tunnel"]) and f"osm:way/{edge['osm_way']}" not in draped_tunnels

    span_ends: dict[str, int] = {}
    for edge in graph["edges"]:
        if edge["bridge"] or graph_only_tunnel(edge):
            for key in (edge["from"], edge["to"]):
                span_ends[key] = span_ends.get(key, 0) + 1
    in_span = {key for key, count in span_ends.items() if count >= 2}
    ground: list[np.ndarray] = []
    owner: list[str] = []
    for edge in graph["edges"]:
        if edge["bridge"]:
            continue
        if graph_only_tunnel(edge):
            continue  # heights linear between its ends
        p = local_points(edge["points"])
        keep = np.ones(len(p), dtype=bool)
        keep[0] = edge["from"] not in in_span
        keep[-1] = edge["to"] not in in_span
        ground.append(p[keep])
        owner += [edge["id"]] * int(keep.sum())
    max_graph = 0.0
    if ground:
        p = np.concatenate(ground)
        if len(p):
            expected = field.apply(p[:, :2], _terrain_at(p[:, :2], tiles, samplers) + offset)
            err = np.abs(p[:, 2] - expected)
            worst = int(np.argmax(err))
            _require(err[worst] <= MM, f"criterion 9: {owner[worst]} is {err[worst]} m off")
            max_graph = float(err.max())

    # 10: road-col / path-col where the merged surfaces reach a tile, no road/path overlap.
    features = _json(work_dir / "04-roads" / "roads.geojson")["features"]
    merged: dict[str, list[BaseGeometry]] = {}
    for feature in features:
        kind = feature["properties"]["surface"]
        if kind in ("road", "path"):
            geom = shapely.transform(shape(feature["geometry"]), lambda c: c - np.array(origin))
            merged.setdefault(kind, []).append(geom)
    merged_union = {k: shapely.union_all(v) for k, v in merged.items()}
    for tid, (tile, meshes) in tiles.items():
        cell = shapely.box(*tile["bounds_local"])
        for kind in ("road", "path"):
            if kind in merged_union and shapely.intersection(merged_union[kind], cell).area > 1e-3:
                _require(f"{kind}-col" in meshes.nodes, f"criterion 10: {tid} has no {kind}-col")
        if len(surfaces.get(tid, {})) == 2:
            overlap = _overlap_area(*surfaces[tid].values())
            _require(overlap < OVERLAP_M2, f"criterion 10: {tid} road/path overlap {overlap} m²")
    _check_tile_edges(tiles)

    # 11: bridge decks.
    chains = bridge_chains(graph)
    cfg = roads.bridge
    t, rh, rw = cfg.deck_thickness_m, cfg.railing_height_m, cfg.railing_width_m
    rise = [np.inf, -np.inf]
    deck_tops: dict[str, list[np.ndarray]] = {}
    for chain in chains:
        label = f"bridge span {sorted(chain.ways)}"
        z = chain.points[:, 2]
        _require(
            np.abs(z - chain.line_z(chain.s)).max() <= FLOAT32_SLACK, f"11: {label} not straight"
        )
        ends = chain.points[[0, -1]]
        surface = field.apply(ends[:, :2], _terrain_at(ends[:, :2], tiles, samplers) + offset)
        _require(
            np.abs(ends[:, 2] - surface).max() <= MM,
            f"11: {label}: ends {ends[:, 2]} vs road surface {surface}",
        )
        names = [n for n in deck_nodes if int(BRIDGE_NODE.match(n).group(1)) in chain.ways]
        _require(
            {int(BRIDGE_NODE.match(n).group(1)) for n in names} == chain.ways,
            f"11: {label}: deck nodes {names}",
        )
        top_s: list[np.ndarray] = []
        for name in names:
            v, _ = tiles[deck_nodes[name]][1].nodes[name]
            s, d = chain.project(v[:, :2])
            diff = v[:, 2] - chain.line_z(s)
            top = (diff > -t / 2) & (diff < rh / 2)
            bottom = (diff >= -t - FLOAT32_SLACK) & (diff <= -t + DECK_RISE)
            rail = (diff >= rh - FLOAT32_SLACK) & (diff <= rh + DECK_RISE)
            _require(np.all(top | bottom | rail), f"11: {name}: stray vertices")
            _require(bottom.any(), f"11: {name}: no deck underside {t} m below the surface")
            lo, hi = float(diff[top].min()), float(diff[top].max())
            _require(lo >= -FLOAT32_SLACK, f"criterion 11: {name} dips {lo} m below the line")
            _require(hi <= DECK_RISE, f"criterion 11: {name} rises {hi} m above the line")
            rise = [min(rise[0], lo), max(rise[1], hi)]
            top_s.append(s[top])
            deck_tops.setdefault(chain.end_nodes[0], []).append(v[top])
            deck_tops.setdefault(chain.end_nodes[1], []).append(v[top])
            # A box railing along each long edge of this way's deck. Where the span bends
            # (a deck extended onto the next way), corners project onto the other segment,
            # so the length is checked within half the deck width there.
            way = int(BRIDGE_NODE.match(name).group(1))
            own = max(e["width_m"] for e in chain.edges if e["osm_way"] == way) / 2
            centre = shapely.MultiLineString(
                [local_points(e["points"])[:, :2] for e in chain.edges if e["osm_way"] == way]
            )
            off = shapely.distance(centre, shapely.points(v[:, :2]))  # from this way's line
            for side in (1.0, -1.0):
                outer = rail & (side * d > 0) & (np.abs(off - own) <= MM)
                inner = rail & (side * d > 0) & (np.abs(off - (own - min(rw, own))) <= MM)
                _require(outer.any() and inner.any(), f"11: {name}: no railing on side {side}")
                along = rail & (side * d > 0)
                _require(
                    s[along].min() <= s[top].min() + own and s[along].max() >= s[top].max() - own,
                    f"11: {name}: railing shorter than the deck",
                )
        covered = np.concatenate(top_s)
        _require(
            covered.min() <= MM and covered.max() >= chain.length - MM,
            f"11: {label}: the deck doesn't reach both ends",
        )

    # Ramps: no step between a deck end edge and the ramp surface beyond it.
    road_samplers = {
        (tid, name): TriangleSampler(*meshes.nodes[name])
        for tid, (_, meshes) in tiles.items()
        for name in ROAD_SURFACES
        if name in meshes.nodes
    }
    footprints = [
        (
            set(chain.end_nodes),
            LineString(chain.points[:, :2]).buffer(
                max(e["width_m"] for e in chain.edges) / 2, cap_style="flat"
            ),
        )
        for chain in chains
    ]
    max_step = 0.0
    for ramp in deck_edges:
        corners = ramp.corners
        count = max(int(np.ceil(2 * ramp.half_width / STEP_SPACING_M)), 1) + 1
        f = np.linspace(0.0, 1.0, count)
        edge = corners[0] + np.outer(f, corners[1] - corners[0])
        # The deck: its driving-surface vertices at the end edge (corners) give its height.
        tops = np.concatenate(deck_tops.get(ramp.node, [np.zeros((0, 3))]))
        _require(len(tops), f"ramp at {ramp.node}: no deck there")
        deck = []
        for c in corners:
            k = int(np.argmin(np.linalg.norm(tops[:, :2] - c, axis=1)))
            _require(np.linalg.norm(tops[k, :2] - c) <= MM, f"ramp at {ramp.node}: no deck corner")
            deck.append(tops[k, 2])
        deck_z = deck[0] + f * (deck[1] - deck[0])
        probe = edge + np.array(ramp.u) * MM  # just onto the ramp
        # Where another deck covers the ramp zone (decks overlapping in plan, e.g. the two
        # carriageways of a dual carriageway), the end edge meets that deck, not a road.
        others = [fp for keys, fp in footprints if ramp.node not in keys]
        free = (
            ~shapely.contains_xy(shapely.union_all(others), probe[:, 0], probe[:, 1])
            if others
            else np.ones(len(probe), dtype=bool)
        )
        h = np.full(len(probe), np.nan)
        for sampler in road_samplers.values():
            h = np.where(np.isnan(h), sampler.height(probe, distance=0.1 * MM), h)
        _require(not np.isnan(h[free]).any(), f"ramp at {ramp.node}: no surface along the deck end")
        if not free.any():
            continue
        step = float(np.abs(h[free] - deck_z[free]).max())
        _require(step <= MAX_STEP_M, f"ramp at {ramp.node}: {step} m step at the deck end")
        max_step = max(max_step, step)

    # Graded ramps: never steeper than `max_ramp_grade` along the path where they are above
    # the terrain (sampled on the meshes along the path's centre line).
    max_grade = 0.0
    # The longest terrain triangle edge (DEM cell diagonal): the mesh is linear within it.
    triangle_m = max(
        (
            float(np.linalg.norm(np.diff(sm.triangles[:, [0, 1, 2, 0], :2], axis=1), axis=2).max())
            for sm in samplers.values()
            if len(sm.triangles)
        ),
        default=0.0,
    )
    for ramp in field.graded:
        line = LineString(ramp.path)
        length = min(ramp.zone_length, line.length)
        us = np.arange(0.0, length + 1e-9, GRADE_SPACING_M)
        xy = np.array([np.asarray(line.interpolate(u).coords)[0] for u in us])
        h = np.full(len(xy), np.nan)
        for sampler in road_samplers.values():
            h = np.where(np.isnan(h), sampler.height(xy, distance=0.1 * MM), h)
        _require(not np.isnan(h).any(), f"graded ramp at {ramp.edge.node}: no surface on the path")
        base = _terrain_at(xy, tiles, samplers) + offset
        # On the descent: where it is above the terrain. Within one terrain triangle of
        # where it meets the terrain, the mesh interpolates between the descent and the
        # terrain (the terrain's slope, not the ramp's), so those samples are left out.
        descent = ramp.height(xy) > base + MM
        meets = us[~descent]
        if len(meets):
            near = np.min(np.abs(us[:, None] - meets[None, :]), axis=1) <= triangle_m
            descent &= ~near
        on = descent & (h > base + MM)
        pair = on[1:] & on[:-1]
        if pair.any():
            grade = np.abs(np.diff(h))[pair] / np.diff(us)[pair]
            worst = float(grade.max())
            _require(
                worst <= ramp.grade + GRADE_TOL,
                f"graded ramp at {ramp.edge.node}: grade {worst:.4f} over {ramp.grade}",
            )
            max_grade = max(max_grade, worst)

    # 4: every span_end node is a deck end where the deck line meets the road surface (the
    # highest terrain + offset across the deck end, within 1 mm), or a reported ramped end.
    ends_by_node = {e["node"]: e for span in report.get("bridges", []) for e in span["ends"]}
    for node in graph["nodes"]:
        if node["kind"] != "span_end":
            continue
        end = ends_by_node.get(node["id"])
        _require(end is not None, f"4: {node['id']} isn't a deck end in the report")
        if end["reason"] == "no_meeting":
            continue  # listed: the deck stops at the search limit and its ramp takes it up
        ramp = next((r for r in deck_edges if r.node == node["id"]), None)
        _require(ramp is not None, f"4: {node['id']} has no ramp (no road meets the deck end)")
        corners = ramp.corners
        count = max(int(np.ceil(np.linalg.norm(corners[1] - corners[0]) / 0.005)), 1) + 1
        edge = corners[0] + np.outer(np.linspace(0, 1, count), corners[1] - corners[0])
        road = float(np.nanmax(_terrain_at(edge, tiles, samplers))) + offset
        (z,) = local_points(node["position"])[:, 2]
        _require(
            abs(z - road) <= MM,
            f"4: {node['id']} at {z:.4f} m, the road surface across its deck end at {road:.4f} m",
        )

    # 13b: the terrain under every deck is the DEM-only terrain.
    dem = _DemTerrain(work_dir, origin)
    for chain in chains:
        width = max(e["width_m"] for e in chain.edges)
        footprint = LineString(chain.points[:, :2]).buffer(width / 2, cap_style="flat")
        for tid, (tile, meshes) in tiles.items():
            if not shapely.intersects(footprint, shapely.box(*tile["bounds_local"])):
                continue
            v = meshes.nodes[TERRAIN_NODE][0]
            inside = shapely.contains_xy(footprint, v[:, 0], v[:, 1])
            if not inside.any():
                continue
            dem_only = dem.tile_vertices(tile)
            _require(dem_only.shape == v.shape, f"criterion 13b: {tid} terrain grid differs")
            _require(
                np.array_equal(v[inside], dem_only[inside]),
                f"criterion 13b: terrain under {sorted(chain.ways)} in {tid} isn't the DEM's",
            )
    return RoadSummary(
        len(graph["edges"]),
        len(graph["nodes"]),
        max_surface,
        max_graph,
        (float(rise[0]), float(rise[1])) if chains else (0.0, 0.0),
        chains,
        deck_nodes,
        max_step,
        len(deck_edges),
        max_grade,
    )
