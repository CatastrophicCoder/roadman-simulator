"""Checks on a built map pack, run on the staged pack before every swap (and by the tests).

Any failed check raises `PackCheckError`; the build then fails and the previous pack stays
untouched. The checks read the staged files back (tiles, manifest, road graph) and the
stage output in the work folder, so they test what the game will load.

- Tiles and buildings: tile files match the manifest; vertices are finite; terrain stays
  in its tile; every `bld_` node belongs to a building in `buildings.json`; collision
  nodes match `buildings.json` and are named `<node>_c<k>-colonly` (k = 0..n-1); no two
  node names of a tile are equal once Godot strips its collision suffixes (`-col`,
  `-colonly`, `-convcolonly`), so the import renames nothing; every
  building's centroid is in its tile; no building base floats above the terrain mesh.
- Roads (docs/specs/roads-and-road-graph.md, criterion numbers in messages): the graph
  matches its schema and its report; nodes and points are finite and in the area;
  `area_edge` nodes lie on the boundary ring of `boundary.json` (1 µm); `length_m` is the
  polyline length; every edge starts and ends exactly at its nodes.
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
- Criterion 13b: terrain vertices inside deck footprints are the DEM-only terrain (cut
  along the custom boundary polygon when `boundary.json` has one).
- Places (docs/specs/place-information.md, `check_places`): `buildings.json` (schema
  version 2) and `pois.json` match their committed schemas; categories and sources are
  valid values; each `category_source` has its raw value; POI and building links exist and
  point back; POI positions and outline points are finite and inside the area; outline
  rings are closed with at least 4 points.
- MML-only buildings (docs/specs/mml-only-buildings.md, `check_mml_only`): every building
  with an `mml:` id has `mml_building` equal to its id and `mml_overlap` 1.0, and no
  `mml:` footprint overlaps an `osm:` footprint by `OVERLAP_M2` or more (footprints from
  `03-buildings/buildings.geojson`).
- World edge (docs/specs/world-edge-pipeline.md, `check_world_edge`): `boundary.json`
  matches its committed schema, map id and area; every `area_edge` node is in exactly one
  roadblock with the role of its edge; roadblocks are at least the merge distance apart;
  roadblocks and fences close the ring (ends within 1 mm, lengths add up within 1 mm); no
  fence touches a road graph edge (1 mm); no fence runs over the ring stretch of a road,
  path or deck surface (`04-roads/roads.geojson`) by more than 1 mm, except the stretches
  listed as touched without a crossing; fences over water (more than 1 mm) are water
  fences and water fences lie in water; fence ends are at `Heightfield.sample` (1 mm),
  no longer than `fence.max_segment_m` and land fences within the terrain deviation; no
  building in `buildings.json` has any part of its footprint (`03-buildings`) outside the
  `boundary.json` ring or within `building_clearance_m` of it (any boundary source);
  10,000 sampled points of the void band lie in a prism and no prism reaches over the
  playable area; the spawn area is inside the test area and its open ground passes the
  spawn check.
- Custom boundary polygon (`check_custom_boundary`, only when `boundary.json` has
  `source` "custom"): every terrain triangle, road graph node and edge polyline, POI
  position and outline, and kept building centroid lies inside the polygon grown by 1 mm.

Heights are sampled on the actual triangles (the tiles' terrain meshes), not a grid. In a
pack with a custom boundary polygon only, a surface or graph point that no terrain
triangle is within 1 µm of (on a terrain edge cut along a slanted boundary edge, after
float32 rounding) takes the triangle within `cut_edge_slack` of the points checked, a few
float32 steps at their largest coordinate. Other packs keep the 1 µm.
"""

import json
import re
import struct
from collections import Counter
from collections.abc import Sequence
from dataclasses import dataclass, field
from pathlib import Path
from typing import Any

import jsonschema
import numpy as np
import shapely
from shapely.geometry import LineString, shape
from shapely.geometry.base import BaseGeometry

from mapgen.build.export_glb import TERRAIN_NODE, from_gltf
from mapgen.build.pack_manifest import committed_schema
from mapgen.build.places.categories import CATEGORIES, CATEGORY_SOURCES, NAME_SOURCES
from mapgen.build.places.model import BUILDINGS_SCHEMA, POIS_NAME, POIS_SCHEMA, PlacesError
from mapgen.config import RoadsConfig, WorldEdgeConfig

TOLERANCE = 1e-6
MM = 1e-3  # criteria 8, 9, 10, 11: 1 mm
UM = 1e-6  # road_graph.json coordinates are rounded to 1 µm
FLOAT32_SLACK = 1e-5  # criterion 11: 0.01 mm, float32 rounding at ~100 m is under 0.008 mm
DECK_RISE = 0.05  # criterion 11: the driving surface is at most 0.05 m above the line
OVERLAP_M2 = 0.01  # criterion 10: road and path intersection area per tile; MML-only buildings
MML_ID_PREFIX = "mml:"  # buildings made from building-use records no OSM building matched
OSM_ID_PREFIX = "osm:"
MAX_STEP_M = 0.02  # ramps: vertical gap between a deck end edge and the ramp surface
GRADE_SPACING_M = 0.25  # graded ramps: samples along the path
# Graded ramps: along a straight path the descent is linear, so the meshes reproduce it
# exactly; what is left is float32 rounding of heights near 100 m (under 8 µm per value,
# about 6e-5 over the 0.25 m samples) and the slight curvature of the descent where a path
# bends. 0.001 allows both with margin.
GRADE_TOL = 0.001
STEP_SPACING_M = 0.1  # ramps: samples along each deck end edge
# Surface and graph points on a custom boundary polygon's edge, where the terrain is cut
# along a slanted line: the tiles store float32, so a road vertex on the cut edge and the
# cut terrain edge itself can each be rounded by half a float32 step per coordinate, and so
# lie up to about two steps apart (9.8e-5 m measured on Somero, where a step at 1 km is
# 6.1e-5 m). Such a point takes its terrain height from the triangle within this many
# float32 steps at the largest coordinate of the points checked (`cut_edge_slack`); the
# height error is that times the slope, far under 1 mm. Axis-aligned edges are exact in
# float32, and packs without a custom polygon use no slack.
CUT_EDGE_SLACK_STEPS = 4
# What a custom boundary polygon clips (terrain, road graph, POIs, kept building centroids)
# lies inside it grown by this (`check_custom_boundary`).
CUSTOM_BOUNDARY_TOL_M = MM
ROAD_SURFACES = ("road-col", "path-col")
# Godot's glTF import suffixes for collision; the imported node's name drops them.
COLLISION_SUFFIXES = ("-convcolonly", "-colonly", "-col")
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
    names: list[str] = field(default_factory=list)  # every glTF node name, duplicates kept

    def buildings(self) -> list[str]:
        return [n for n in self.nodes if n.startswith("bld_") and not n.endswith("-colonly")]


def import_name(name: str) -> str:
    """A glTF node's name after Godot's import strips a collision suffix.

    Strips only the suffixes the pipeline writes, `-convcolonly`, `-colonly` and `-col`
    (matched case-insensitively). Godot's importer also recognises other forms, such as
    `_col`, `-convcol` and names with trailing digits; the pipeline doesn't use them, so
    they aren't stripped here.
    """
    for suffix in COLLISION_SUFFIXES:
        if name.lower().endswith(suffix):
            return name[: -len(suffix)]
    return name


def check_import_names(tile_id: str, names: Sequence[str]) -> None:
    """No two node names of a tile are equal once Godot strips the collision suffixes;
    otherwise the import renames one of them and the pack's names no longer find it.
    Nodes with an empty name are skipped rather than reported as a clash."""
    seen: dict[str, str] = {}
    for name in names:
        if not name:
            continue
        key = import_name(name)
        _require(
            key not in seen,
            f"{tile_id}: nodes {seen.get(key)!r} and {name!r} both import as {key!r}",
        )
        seen[key] = name


def check_collision_names(node: str, collisions: Sequence[str]) -> None:
    """A building's collision nodes are "<node>_c0-colonly" ... "<node>_c<n-1>-colonly"."""
    _require(collisions, f"{node} has no -colonly partner")
    expected = [f"{node}_c{k}-colonly" for k in range(len(collisions))]
    _require(
        list(collisions) == expected,
        f"{node}: collision nodes {list(collisions)} aren't {expected}",
    )


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
    names = [str(node.get("name", "")) for node in gltf.get("nodes", [])]
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
    return TileMeshes(nodes, names)


class TriangleSampler:
    """Heights of a z-up triangle mesh at 2D points: the triangles themselves, not a grid."""

    def __init__(self, vertices: np.ndarray, faces: np.ndarray) -> None:
        self.triangles = np.asarray(vertices, dtype=float)[np.asarray(faces)]
        self.polygons = shapely.polygons(self.triangles[:, :, :2])
        self.tree = shapely.STRtree(self.polygons)

    def height(
        self, xy: np.ndarray, distance: float = 1e-6, slack: float | None = None
    ) -> np.ndarray:
        """Heights at (n, 2) points; NaN where no triangle is within `distance`. With
        `slack`, a point no triangle is within `distance` of takes the first triangle
        within `slack` instead (in that triangle's plane)."""
        result = self._height(xy, distance)
        if slack is not None and np.isnan(result).any():
            missing = np.isnan(result)
            pts = np.asarray(xy, dtype=float).reshape(-1, 2)
            result[missing] = self._height(pts[missing], slack)
        return result

    def _height(self, xy: np.ndarray, distance: float) -> np.ndarray:
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
    poi_count: int = 0
    roadblocks: int = 0


def check_pack(
    pack_dir: Path,
    work_dir: Path,
    roads: RoadsConfig | None = None,
    *,
    world_edge: WorldEdgeConfig | None = None,
) -> PackSummary:
    """Every check. A map with a `roads` block also gets the road checks (`check_roads`);
    without one, `road_graph.json` must be empty and the tiles have no road meshes.
    `world_edge` is the map's world edge config; None takes the values the build recorded
    in `10-world-edge/report.json`."""
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
    known_nodes = {*by_node, *(c for b in buildings for c in b["collision_nodes"])}

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
        check_import_names(tid, meshes.names)
        # Every bld_ node (mesh or collision) belongs to a building in buildings.json, so a
        # building left out (crossing the boundary, centroid outside) has neither.
        stray = sorted(n for n in meshes.nodes if n.startswith("bld_") and n not in known_nodes)
        _require(not stray, f"{tid}: bld_ node(s) of no building in buildings.json: {stray[:5]}")
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
            check_collision_names(name, collisions)
            for collision in collisions:
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
    mml_only = sum(1 for b in buildings if b["id"].startswith(MML_ID_PREFIX))
    edge_report = _json(work_dir / WORLD_EDGE_DIR / "report.json")
    dropped_osm = sum(
        1 for d in edge_report["dropped_buildings"] if d["id"].startswith(OSM_ID_PREFIX)
    )
    _require(
        load_report["footprints"]["in_area"] - dropped_osm + mml_only
        == glb_buildings
        == len(buildings),
        "building counts differ between the load report (minus the OSM buildings crossing "
        "the boundary, plus the MML-only buildings), the tiles and buildings.json",
    )
    check_mml_only(buildings, geometries)
    bounds = (
        min(t["bounds_local"][0] for t in manifest["tiles"]),
        min(t["bounds_local"][1] for t in manifest["tiles"]),
        max(t["bounds_local"][2] for t in manifest["tiles"]),
        max(t["bounds_local"][3] for t in manifest["tiles"]),
    )
    poi_count = check_places(pack_dir, bounds)
    road_summary = None
    if roads is not None:
        road_summary = check_roads(pack_dir, work_dir, roads, tiles=tiles, samplers=samplers)
    else:
        graph = _json(pack_dir / "road_graph.json")
        _require(not graph["nodes"] and not graph["edges"], "no roads block: graph not empty")
        for tid, (_, meshes) in tiles.items():
            extra = [n for n in meshes.nodes if n in ROAD_SURFACES or BRIDGE_NODE.match(n)]
            _require(not extra, f"no roads block, but {tid} has {extra}")
    roadblocks = check_world_edge(pack_dir, work_dir, manifest, world_edge)
    check_custom_boundary(pack_dir, work_dir, manifest, tiles)
    return PackSummary(len(manifest["tiles"]), glb_buildings, road_summary, poi_count, roadblocks)


def check_mml_only(buildings: Sequence[dict[str, Any]], geometries: dict[str, BaseGeometry]) -> int:
    """The MML-only building checks; returns the number of `mml:` buildings.

    `geometries` are the footprints by building id (any common coordinates, e.g. the map
    CRS of `03-buildings/buildings.geojson`).
    """
    mml = [b for b in buildings if b["id"].startswith(MML_ID_PREFIX)]
    for building in mml:
        bid = building["id"]
        _require(
            building["mml_building"] == bid,
            f"{bid}: mml_building {building['mml_building']} isn't the building's own id",
        )
        _require(building["mml_overlap"] == 1.0, f"{bid}: mml_overlap isn't 1.0")
    osm_ids = sorted(b["id"] for b in buildings if b["id"].startswith(OSM_ID_PREFIX))
    osm = [geometries[i] for i in osm_ids if i in geometries]
    osm_ids = [i for i in osm_ids if i in geometries]
    if not mml or not osm:
        return len(mml)
    tree = shapely.STRtree(osm)
    for building in mml:
        bid = building["id"]
        _require(bid in geometries, f"{bid}: no footprint in 03-buildings/buildings.geojson")
        footprint = geometries[bid]
        for index in sorted(int(i) for i in tree.query(footprint, predicate="intersects")):
            shared = footprint.intersection(osm[index]).area
            _require(
                shared < OVERLAP_M2,
                f"{bid} overlaps {osm_ids[index]} by {shared:.3f} m² (limit {OVERLAP_M2} m²)",
            )
    return len(mml)


# Places.

# The raw value each category source needs (`category_raw` key, and whether a list).
_SOURCE_RAW = {
    "override": "override",
    "osm_building": "osm_building",
    "osm_poi": "osm_poi",
    "osm_poi_area": "osm_poi_area",
    "mml_use_class": "mml_feature_class",
    "osm_landuse": "osm_landuse",
}


def _finite_in(point: Any, bounds: tuple[float, float, float, float]) -> bool:
    """A Godot `[x, z]` or `[x, y, z]` point: finite, and its x / -z inside the area."""
    values = [float(v) for v in point]
    if not all(np.isfinite(values)):
        return False
    x, z = values[0], values[-1]
    min_x, min_y, max_x, max_y = bounds
    return (
        min_x - TOLERANCE <= x <= max_x + TOLERANCE and min_y - TOLERANCE <= -z <= max_y + TOLERANCE
    )


def check_places(pack_dir: Path, bounds: tuple[float, float, float, float]) -> int:
    """The place checks on `buildings.json` and `pois.json`; returns the POI count.

    `bounds` is the area in local metres (min_x, min_y, max_x, max_y).
    """
    try:
        BUILDINGS_SCHEMA.validate_file(pack_dir / "buildings.json")
        POIS_SCHEMA.validate_file(pack_dir / POIS_NAME)
    except FileNotFoundError as exc:
        raise PackCheckError(f"missing {Path(str(exc.filename)).name}") from exc
    except PlacesError as exc:
        raise PackCheckError(str(exc)) from exc
    buildings = _json(pack_dir / "buildings.json")["buildings"]
    pois = _json(pack_dir / POIS_NAME)["pois"]
    by_id = {b["id"]: b for b in buildings}
    poi_by_id = {p["id"]: p for p in pois}
    _require(len(by_id) == len(buildings), "buildings.json has duplicate ids")
    _require(len(poi_by_id) == len(pois), "pois.json has duplicate ids")
    _require([p["id"] for p in pois] == sorted(poi_by_id), "pois.json isn't sorted by id")

    for building in buildings:
        bid = building["id"]
        _require(building["category"] in CATEGORIES, f"{bid}: invalid category")
        source = building["category_source"]
        _require(source is None or source in CATEGORY_SOURCES, f"{bid}: invalid category source")
        _require(
            building["name_source"] is None or building["name_source"] in NAME_SOURCES,
            f"{bid}: invalid name source",
        )
        _require(building["address_source"] in (None, "osm"), f"{bid}: invalid address source")
        raw = building["category_raw"]
        if source is None:
            _require(building["category"] == "unknown", f"{bid}: no category source")
        else:
            value = raw[_SOURCE_RAW[source]]
            _require(
                value is not None and value != [],
                f"{bid}: category_source {source} without its raw value",
            )
        if source == "override":
            _require(raw["override"] == building["category"], f"{bid}: override category")
        _require(
            (building["mml_building"] is None) == (building["mml_overlap"] is None),
            f"{bid}: mml_building and mml_overlap not null together",
        )
        set_fields = {f for o in building["overrides"] for f in o["fields"]}
        _require(
            (source == "override") == ("category" in set_fields),
            f"{bid}: category_source override without an override setting category, or back",
        )
        _require(
            (building["name_source"] == "override") == ("name" in set_fields),
            f"{bid}: name_source override without an override setting the name, or back",
        )
        override_ids = [o["id"] for o in building["overrides"]]
        _require(override_ids == sorted(set(override_ids)), f"{bid}: overrides not sorted")
        for key in ("poi_ids", "area_poi_ids"):
            ids = building[key]
            _require(ids == sorted(set(ids)), f"{bid}: {key} not sorted or not unique")
        for pid in building["poi_ids"]:
            poi = poi_by_id.get(pid)
            _require(poi is not None, f"{bid}: poi_ids names {pid}, which isn't in pois.json")
            _require(poi["building_id"] == bid, f"{bid}: {pid} doesn't link back")
        for pid in building["area_poi_ids"]:
            poi = poi_by_id.get(pid)
            _require(poi is not None, f"{bid}: area_poi_ids names {pid}, not in pois.json")
            _require(bid in poi["contains_building_ids"], f"{bid}: {pid} doesn't link back")

    for poi in pois:
        pid = poi["id"]
        target = poi["building_id"]
        if target is not None:
            building = by_id.get(target)
            _require(building is not None, f"{pid}: building_id {target} isn't a building")
            _require(pid in building["poi_ids"], f"{pid}: {target} doesn't link back")
        contained = poi["contains_building_ids"]
        _require(contained == sorted(set(contained)), f"{pid}: contains_building_ids order")
        for bid in contained:
            building = by_id.get(bid)
            _require(building is not None, f"{pid}: contains {bid}, which isn't a building")
            _require(pid in building["area_poi_ids"], f"{pid}: {bid} doesn't link back")
        _require(_finite_in(poi["position"], bounds), f"{pid}: position outside the area")
        if poi["address_source"] == "building":
            _require(
                target is not None and poi["address"] == by_id[target]["address"],
                f"{pid}: address_source building, but not its building's address",
            )
        outline = poi["outline"]
        if poi["geometry"] == "point":
            _require(outline is None, f"{pid}: a point with an outline")
            _require(not contained, f"{pid}: a point containing buildings")
        else:
            _require(outline, f"{pid}: an area without an outline")
            for polygon in outline:
                _require(polygon, f"{pid}: an outline polygon without rings")
                for ring in polygon:
                    _require(
                        len(ring) >= 4 and ring[0] == ring[-1],
                        f"{pid}: outline ring open or under 4 points",
                    )
                    for point in ring:
                        _require(_finite_in(point, bounds), f"{pid}: outline point outside")
    return len(pois)


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


def cut_edge_slack(xy: np.ndarray) -> float:
    """The cut-edge slack for local points `xy`: `CUT_EDGE_SLACK_STEPS` float32 steps at
    their largest absolute coordinate."""
    pts = np.asarray(xy, dtype=float)
    largest = float(np.abs(pts).max()) if pts.size else 0.0
    return CUT_EDGE_SLACK_STEPS * float(np.spacing(np.float32(largest)))


def _terrain_at(
    xy: np.ndarray,
    tiles: dict[str, Any],
    samplers: dict[str, TriangleSampler],
    cut: bool = False,
) -> np.ndarray:
    """Terrain mesh heights at local points, from the first tile whose bounds hold each
    point. `cut`: the terrain is cut along a custom boundary polygon, so a point off every
    triangle by float32 rounding takes the triangle within `cut_edge_slack`."""
    result = np.full(len(xy), np.nan)
    for tid, (tile, _) in tiles.items():
        min_x, min_y, max_x, max_y = tile["bounds_local"]
        todo = np.isnan(result) & (xy[:, 0] >= min_x) & (xy[:, 0] <= max_x)
        todo &= (xy[:, 1] >= min_y) & (xy[:, 1] <= max_y)
        if todo.any():
            slack = cut_edge_slack(xy[todo]) if cut else None
            result[todo] = samplers[tid].height(xy[todo], slack=slack)
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


def work_heightfield(work_dir: Path, origin: tuple[float, float]) -> Any:
    """The build's `Heightfield`, rebuilt from the stage-1 DEM (`01-load/dem.tif`)."""
    import rasterio

    from mapgen.build.heightfield import heightfield_from_raster
    from mapgen.sources.base import ElevationRaster

    with rasterio.open(work_dir / "01-load" / "dem.tif") as src:
        raster = ElevationRaster(src.read(1), src.transform, str(src.crs))
    heightfield, _ = heightfield_from_raster(raster, *origin)
    return heightfield


def _boundary_ring(pack_dir: Path) -> list[tuple[float, float]] | None:
    """The boundary ring of the pack's boundary.json in local (x, n), or None without one."""
    path = pack_dir / "boundary.json"
    if not path.is_file():
        return None
    return [(float(p[0]), -float(p[1])) for p in _json(path)["boundary"]["ring"]]


def _custom_boundary(pack_dir: Path) -> Any:
    """The custom boundary polygon the build cut the terrain along (exactly: boundary.json
    holds its points, already rounded to 1 µm by the config), or None."""
    path = pack_dir / "boundary.json"
    if not path.is_file():
        return None
    boundary = _json(path)["boundary"]
    if boundary["source"] != "custom":
        return None
    from mapgen.build.world_edge.ring import Ring

    return Ring.from_coords([(float(p[0]), -float(p[1])) for p in boundary["ring"]]).polygon()


class _DemTerrain:
    """Criterion 13b: tile terrain vertices built from the stage-1 DEM alone (float32),
    cut along the custom boundary polygon when the pack has one."""

    def __init__(self, work_dir: Path, origin: tuple[float, float], clip: Any = None) -> None:
        self.work_dir, self.origin, self.clip = work_dir, origin, clip
        self._heightfield: Any = None

    def tile_vertices(self, tile: dict[str, Any]) -> np.ndarray:
        from mapgen.build.area import Tile
        from mapgen.build.terrain import MeshTerrainBuilder
        from mapgen.geo import BBox

        if self._heightfield is None:
            self._heightfield = work_heightfield(self.work_dir, self.origin)
        i, j = (int(v) for v in re.findall(r"-?\d+", tile["id"]))
        chunk = MeshTerrainBuilder().build_tile(
            self._heightfield, Tile(i, j, BBox(*tile["bounds_local"]))
        )
        if self.clip is not None:
            from mapgen.build.terrain.clip import clip_chunk

            chunk, _ = clip_chunk(chunk, self.clip, self._heightfield)
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
    # `area_edge` nodes lie on the boundary: the ring in boundary.json (the build area
    # rectangle, the test-area square or a custom polygon), else the area's rectangle.
    boundary = _boundary_ring(pack_dir)
    custom = _custom_boundary(pack_dir)
    cut = custom is not None  # the terrain is cut along a custom polygon
    edge_line = (
        LineString([*boundary, boundary[0]])
        if boundary is not None
        else shapely.box(*area).exterior
    )

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
            on_edge = float(edge_line.distance(shapely.Point(p[0], p[1])))
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
            slack = cut_edge_slack(v[:, :2]) if cut else None
            h = samplers[tid].height(v[:, :2], slack=slack)
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
            expected = field.apply(p[:, :2], _terrain_at(p[:, :2], tiles, samplers, cut) + offset)
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
        surface = field.apply(ends[:, :2], _terrain_at(ends[:, :2], tiles, samplers, cut) + offset)
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
        base = _terrain_at(xy, tiles, samplers, cut) + offset
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
        road = float(np.nanmax(_terrain_at(edge, tiles, samplers, cut))) + offset
        (z,) = local_points(node["position"])[:, 2]
        _require(
            abs(z - road) <= MM,
            f"4: {node['id']} at {z:.4f} m, the road surface across its deck end at {road:.4f} m",
        )

    # 13b: the terrain under every deck is the DEM-only terrain.
    dem = _DemTerrain(work_dir, origin, custom)
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


# World edge (docs/specs/world-edge-pipeline.md).

WORLD_EDGE_DIR = "10-world-edge"
BAND_SAMPLES = 10_000  # criterion 15: sampled points of the void band
AREA_TOLERANCE_M2 = 0.01  # spawn check: open ground area and obstacle overlap
RADIUS_TOLERANCE_M = 0.02  # spawn check: the largest clear disc (its own precision 0.01 m)


def _local_xz(point: Sequence[float]) -> tuple[float, float]:
    """A Godot [x, z] or [x, y, z] point as local (x, n)."""
    return (float(point[0]), -float(point[-1]))


def _work_features(path: Path, origin: np.ndarray) -> list[tuple[dict[str, Any], BaseGeometry]]:
    """The features of a stage GeoJSON (world coordinates) moved to local coordinates."""
    if not path.is_file():
        return []
    result = []
    for feature in _json(path)["features"]:
        geometry = shapely.transform(shape(feature["geometry"]), lambda c: c - origin)
        result.append((feature["properties"], geometry))
    return result


def _ring_overlap(length: float, a0: float, b0: float, a1: float, b1: float) -> float:
    """The length two circular stretches of a ring `length` long share (0 when apart)."""
    return max(0.0, *(min(b0, b1 + k * length) - max(a0, a1 + k * length) for k in (-1, 0, 1)))


def check_world_edge(
    pack_dir: Path,
    work_dir: Path,
    manifest: dict[str, Any],
    config: WorldEdgeConfig | None = None,
) -> int:
    """The world edge checks on a staged pack; returns the number of roadblocks."""
    from mapgen.build.world_edge.fences import max_deviation
    from mapgen.build.world_edge.model import BOUNDARY_NAME, BOUNDARY_SCHEMA, BoundaryError
    from mapgen.build.world_edge.ring import Ring, interval_gap
    from mapgen.build.world_edge.spawn import clear_radius, crossing_buildings
    from mapgen.build.world_edge.void import (
        band_samples,
        footprint_problems,
        point_in_quads,
        quads_overlapping,
    )

    path = pack_dir / BOUNDARY_NAME
    _require(path.is_file(), f"{BOUNDARY_NAME} is missing")
    document = _json(path)
    try:
        BOUNDARY_SCHEMA.validate(document, BOUNDARY_NAME)
    except BoundaryError as exc:
        raise PackCheckError(str(exc)) from exc
    _require(
        (document["map_id"], document["area"]) == (manifest["map_id"], manifest["area"]),
        f"{BOUNDARY_NAME}: map_id/area differ from the manifest",
    )
    report = _json(work_dir / WORLD_EDGE_DIR / "report.json")
    cfg = config or WorldEdgeConfig.model_validate(report["config"])
    origin = np.array([manifest["origin"]["x"], manifest["origin"]["y"]])

    points = [_local_xz(p) for p in document["boundary"]["ring"]]
    ring = Ring.from_coords(points)
    _require(
        np.allclose(ring.xy, np.array(points), atol=UM),
        "boundary ring isn't counter-clockwise from its lowest-x, lowest-n vertex",
    )
    _require(abs(ring.length - document["boundary"]["length_m"]) <= MM, "boundary length_m is off")
    polygon = ring.polygon()

    # Roadblocks and roles (criteria 4-6).
    graph = _json(pack_dir / "road_graph.json")
    edge_nodes = {n["id"] for n in graph["nodes"] if n["kind"] == "area_edge"}
    edges = {e["id"]: e for e in graph["edges"]}
    roadblocks = document["roadblocks"]
    seen = Counter(n["node"] for rb in roadblocks for n in rb["nodes"])
    twice = sorted(k for k, v in seen.items() if v > 1)
    _require(not twice, f"area_edge nodes in more than one roadblock: {twice[:5]}")
    missing = sorted(edge_nodes - set(seen))
    _require(not missing, f"area_edge nodes in no roadblock: {missing[:5]}")
    extra = sorted(set(seen) - edge_nodes)
    _require(not extra, f"roadblock nodes that aren't area_edge nodes: {extra[:5]}")
    for rb in roadblocks:
        for item in rb["nodes"]:
            edge = edges.get(item["edge"])
            _require(
                edge is not None and item["node"] in (edge["from"], edge["to"]),
                f"{rb['id']}: {item['edge']} isn't the edge of {item['node']}",
            )
            role = "spawn_despawn" if edge["drivable"] else "turn_around"
            _require(item["role"] == role, f"{rb['id']}: {item['node']} has role {item['role']}")
    spans = sorted((rb["s_start"], rb["s_end"]) for rb in roadblocks)
    if len(spans) > 1:
        for k, first in enumerate(spans):
            second = spans[(k + 1) % len(spans)]
            gap = interval_gap(first, second, ring.length)
            _require(
                gap >= cfg.roadblock.merge_distance_m - MM,
                f"roadblocks at s {first} and {second} are {gap:.3f} m apart",
            )

    # Closed loop (criterion 7).
    items = sorted(
        [(rb["s_start"], rb["s_end"], rb["start"], rb["end"], rb["id"]) for rb in roadblocks]
        + [(f["s_start"], f["s_end"], f["start"], f["end"], f["id"]) for f in document["fences"]]
    )
    _require(items, "no roadblocks or fences")
    for item in items:
        _require(
            0.0 <= item[0] < ring.length and item[1] > item[0],
            f"{item[4]}: s_start {item[0]} isn't in [0, {ring.length}) or s_end isn't after it",
        )
    for k, item in enumerate(items):
        following = items[(k + 1) % len(items)]
        end, start = np.array(_local_xz(item[3])), np.array(_local_xz(following[2]))
        _require(
            float(np.hypot(*(end - start))) <= MM,
            f"{item[4]} doesn't end where {following[4]} starts",
        )
    total = sum(item[1] - item[0] for item in items)
    _require(abs(total - ring.length) <= MM, f"items cover {total:.6f} m of {ring.length:.6f}")

    # Fences: road graph edges, water, heights and lengths (criteria 8, 9, 12).
    fences = document["fences"]
    lines = [LineString([_local_xz(f["start"]), _local_xz(f["end"])]) for f in fences]
    edge_lines = [LineString([_local_xz(p) for p in e["points"]]) for e in graph["edges"]]
    if edge_lines and lines:
        tree = shapely.STRtree(edge_lines)
        for fence, line in zip(fences, lines, strict=True):
            hits = tree.query(line, predicate="dwithin", distance=MM)
            _require(
                len(hits) == 0,
                f"{fence['id']} crosses road graph edge {graph['edges'][int(hits[0])]['id']}"
                if len(hits)
                else "",
            )
    # Fences off the road and path surfaces on the ring (criterion 8): a fence overlaps
    # the ring stretches of stage 4's merged surfaces (bridge decks included) by at most
    # 1 mm, except over the stretches the report lists as touched without a crossing,
    # which the fence runs over.
    surfaces = [
        g
        for props, g in _work_features(work_dir / "04-roads" / "roads.geojson", origin)
        if props.get("surface") in ("road", "path", "bridge")
    ]
    touched = [(t["s_start"], t["s_end"]) for t in report.get("touched_without_crossing", [])]
    stretches = ring.intervals(shapely.union_all(surfaces)) if surfaces else []
    for a, b in stretches:
        if any(_ring_overlap(ring.length, a, b, *t) >= (b - a) - MM for t in touched):
            continue
        for fence in fences:
            overlap = _ring_overlap(ring.length, a, b, fence["s_start"], fence["s_end"])
            _require(
                overlap <= MM,
                f"{fence['id']} runs {overlap:.4f} m over road or path surface at s "
                f"{a:.3f}..{b:.3f}",
            )

    features = _work_features(work_dir / WORLD_EDGE_DIR / "boundary.geojson", origin)
    water_geoms = [g for props, g in features if props.get("kind") == "water_area"]
    water = shapely.union_all(water_geoms) if water_geoms else None
    for fence, line in zip(fences, lines, strict=True):
        if water is not None:
            overlap = line.intersection(water).length
            _require(
                overlap <= MM or fence["type"] == "water",
                f"{fence['id']} is over water for {overlap:.3f} m but is {fence['type']}",
            )
        if fence["type"] == "water":
            outside = line.length if water is None else line.difference(water).length
            _require(outside <= MM, f"{fence['id']} is water but {outside:.3f} m is on land")
    if water is not None:
        for rb in roadblocks:
            stretch = ring.path_line(rb["s_start"], rb["s_end"])
            _require(
                stretch.intersection(water).length <= MM or rb["over_water"],
                f"{rb['id']} spans water but over_water is false",
            )
    heightfield = work_heightfield(work_dir, (float(origin[0]), float(origin[1])))
    for fence, line in zip(fences, lines, strict=True):
        for point in (fence["start"], fence["end"]):
            x, n = _local_xz(point)
            z = float(heightfield.sample(x, n))
            _require(abs(point[1] - z) <= MM, f"{fence['id']} end at {point[1]} != terrain {z}")
        _require(
            line.length <= cfg.fence.max_segment_m + MM,
            f"{fence['id']} is {line.length:.3f} m long",
        )
        if fence["type"] == "land":
            deviation, _ = max_deviation(heightfield, line.coords[0], line.coords[-1])
            _require(
                deviation <= cfg.fence.max_terrain_deviation_m + MM,
                f"{fence['id']}: terrain {deviation:.3f} m from its line",
            )

    # Buildings (criterion 16).
    buildings = _json(pack_dir / "buildings.json")["buildings"]
    footprints = {
        props["id"]: geometry
        for props, geometry in _work_features(
            work_dir / "03-buildings" / "buildings.geojson", origin
        )
    }
    missing = [b["id"] for b in buildings if b["id"] not in footprints]
    _require(not missing, f"buildings without a footprint in 03-buildings: {missing[:5]}")
    crossing = crossing_buildings(
        polygon,
        [(b["id"], footprints[b["id"]]) for b in buildings],
        max(cfg.building_clearance_m - UM, 0.0),
    )
    _require(
        not crossing,
        "buildings crossing the boundary: "
        + ", ".join(f"{c['id']} ({c['distance_m']:.3f} m)" for c in crossing[:5]),
    )

    # Void trigger (criterion 15).
    void = document["void_trigger"]
    quads = [[_local_xz(p) for p in prism["footprint"]] for prism in void["prisms"]]
    _require(
        abs(void["bottom_y"] - (document["cliff"]["bottom_y"] - cfg.void.width_m)) <= MM,
        "void bottom_y isn't the cliff bottom minus void.width_m",
    )
    bad_footprints = footprint_problems(quads)
    _require(
        not bad_footprints,
        f"{len(bad_footprints)} void footprints aren't convex quads: {bad_footprints[:3]}",
    )
    samples = band_samples(ring, cfg.void.inner_offset_m, cfg.void.width_m, BAND_SAMPLES)
    inside = point_in_quads(quads, samples)
    _require(
        inside.all(),
        f"{int((~inside).sum())} void band samples lie in no prism, e.g. "
        f"{samples[~inside][:1].tolist()}",
    )
    overlapping = quads_overlapping(polygon, quads)
    _require(not overlapping, f"void prisms {overlapping[:5]} reach over the playable area")

    # Spawn area (criterion 19).
    spawn_doc = document["spawn_area"]
    spawn = shapely.Polygon([_local_xz(p) for p in spawn_doc["polygon"]])
    test_area = shapely.box(*report["test_area_local"])
    _require(test_area.covers(spawn), "the spawn area isn't inside the test area")
    ground = shapely.union_all(
        [
            shapely.Polygon(
                [_local_xz(p) for p in rings[0]], [[_local_xz(p) for p in r] for r in rings[1:]]
            )
            for rings in spawn_doc["open_ground"]
        ]
    )
    _require(
        abs(ground.area - spawn_doc["open_area_m2"]) <= AREA_TOLERANCE_M2,
        "spawn open_area_m2 isn't the open ground's area",
    )
    _require(
        ground.area >= cfg.spawn.min_open_area_m2,
        f"spawn open ground {ground.area:.1f} m² is under {cfg.spawn.min_open_area_m2:g}",
    )
    radius = clear_radius(ground)
    _require(
        radius >= cfg.spawn.min_clear_radius_m - RADIUS_TOLERANCE_M,
        f"spawn open ground fits only a disc of r {radius:.2f} m",
    )
    _require(
        ground.difference(spawn).area <= AREA_TOLERANCE_M2, "open ground outside the spawn area"
    )
    inner = polygon.buffer(-cfg.spawn.min_boundary_distance_m)
    _require(
        ground.difference(inner).area <= AREA_TOLERANCE_M2,
        "open ground closer to the boundary than spawn.min_boundary_distance_m",
    )
    kept = {b["id"] for b in buildings}
    obstacles = [g for fid, g in footprints.items() if fid in kept]
    obstacles += water_geoms
    obstacles += [
        g
        for props, g in _work_features(work_dir / "04-roads" / "roads.geojson", origin)
        if props.get("surface") in ("road", "path", "bridge")
    ]
    if obstacles:
        grown = shapely.union_all([g.buffer(cfg.spawn.obstacle_buffer_m - MM) for g in obstacles])
        overlap = ground.intersection(grown).area
        _require(
            overlap <= AREA_TOLERANCE_M2, f"open ground overlaps obstacles by {overlap:.3f} m²"
        )
    return len(roadblocks)


def check_custom_boundary(
    pack_dir: Path,
    work_dir: Path,
    manifest: dict[str, Any],
    tiles: dict[str, Any] | None = None,
) -> bool:
    """With a custom boundary polygon in `boundary.json` (`boundary.source` "custom"):
    everything the build clips by it lies inside it, grown by `CUSTOM_BOUNDARY_TOL_M`.
    Checks every terrain triangle of every tile, every road graph node and edge polyline,
    every POI position and outline, and the centroid of every kept building (footprints
    from `03-buildings/buildings.geojson`). Returns whether the pack has a custom polygon
    (False: nothing to check)."""
    polygon = _custom_boundary(pack_dir)
    if polygon is None:
        return False
    grown = polygon.buffer(CUSTOM_BOUNDARY_TOL_M)
    shapely.prepare(grown)
    origin = np.array([manifest["origin"]["x"], manifest["origin"]["y"]])

    if tiles is None:
        tiles = {t["id"]: (t, read_glb_meshes(pack_dir / t["file"])) for t in manifest["tiles"]}
    for tid, (_, meshes) in tiles.items():
        vertices, faces = meshes.nodes[TERRAIN_NODE]
        triangles = shapely.polygons(np.asarray(vertices)[np.asarray(faces)][:, :, :2])
        outside = np.flatnonzero(~shapely.covers(grown, triangles))
        _require(
            len(outside) == 0,
            f"{tid}: {len(outside)} terrain triangle(s) outside the custom boundary, first "
            f"{np.asarray(triangles[outside[0]].exterior.coords)[:3].round(3).tolist()}"
            if len(outside)
            else "",
        )

    graph = _json(pack_dir / "road_graph.json")
    for node in graph["nodes"]:
        x, n = _local_xz(node["position"])
        _require(
            grown.covers(shapely.Point(x, n)),
            f"road graph node {node['id']} at ({x:.3f}, {n:.3f}) is outside the custom boundary",
        )
    for edge in graph["edges"]:
        points = [_local_xz(p) for p in edge["points"]]
        line = LineString(points) if len(points) > 1 else shapely.Point(points[0])
        _require(grown.covers(line), f"road graph edge {edge['id']} leaves the custom boundary")

    for poi in _json(pack_dir / POIS_NAME)["pois"]:
        x, n = _local_xz(poi["position"])
        _require(
            grown.covers(shapely.Point(x, n)),
            f"POI {poi['id']} at ({x:.3f}, {n:.3f}) is outside the custom boundary",
        )
        for rings in poi["outline"] or []:
            outline = shapely.Polygon([_local_xz(p) for p in rings[0]])
            _require(grown.covers(outline), f"POI {poi['id']}: outline leaves the custom boundary")

    footprints = {
        props["id"]: geometry
        for props, geometry in _work_features(
            work_dir / "03-buildings" / "buildings.geojson", origin
        )
    }
    for building in _json(pack_dir / "buildings.json")["buildings"]:
        bid = building["id"]
        _require(bid in footprints, f"{bid}: no footprint in 03-buildings/buildings.geojson")
        centroid = footprints[bid].centroid
        _require(
            grown.covers(centroid),
            f"building {bid}: centroid ({centroid.x:.3f}, {centroid.y:.3f}) is outside the "
            "custom boundary",
        )
    return True
