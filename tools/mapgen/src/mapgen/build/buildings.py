"""Stage 3: buildings. Resolve heights, set bases on the terrain and extrude footprints.

Height priority: lidar (`mml_lidar`, see `mapgen.build.lidar`), MML 3D measured height
(`mml_3d`, a stub that never has a value yet), OSM `height`, OSM `building:levels` x 3 m,
then a default of 2 floors x 3 m. A manual override goes first once it exists. A lidar
height is the median roof elevation minus the base, so the flat roof sits at the measured
median elevation. The base is `Heightfield.min_under(footprint)`. Roofs are flat for now.
Each building belongs to the tile containing its centroid and is never split.
"""

import math
import re
from collections import Counter
from collections.abc import Iterable, Mapping
from dataclasses import dataclass, field
from typing import Any, Literal

import numpy as np
import shapely
from shapely.geometry import MultiPolygon, Polygon
from shapely.geometry.polygon import orient
from trimesh.creation import triangulate_polygon

from mapgen.build.area import BuildArea, tile_id
from mapgen.build.heightfield import Heightfield
from mapgen.build.lidar import REJECT_REASONS, LidarParams, LidarResult, PointIndex, lidar_height
from mapgen.build.stage1_load import LocalFootprint
from mapgen.sources.base import PointCloud

HeightSource = Literal["mml_lidar", "mml_3d", "osm_height", "osm_levels", "default"]
HEIGHT_SOURCES: tuple[HeightSource, ...] = (
    "mml_lidar",
    "mml_3d",
    "osm_height",
    "osm_levels",
    "default",
)
LEVEL_HEIGHT_M = 3.0
DEFAULT_LEVELS = 2
MIN_MATCH_OVERLAP = 0.5  # share of the footprint a 3D building must cover to lend its height

_HEIGHT_RE = re.compile(r"^\s*(\d+(?:\.\d+)?)\s*(?:m)?\s*$")
_LEVELS_RE = re.compile(r"^\s*(\d+(?:\.\d+)?)\s*$")


def _positive(pattern: re.Pattern[str], value: str | None) -> float | None:
    if value is None:
        return None
    match = pattern.match(value)
    if match is None:
        return None
    number = float(match.group(1))
    return number if number > 0 and math.isfinite(number) else None


def parse_height(value: str | None) -> float | None:
    """An OSM `height` in metres: "12", "12 m", "12.5". None if missing, invalid or <= 0."""
    return _positive(_HEIGHT_RE, value)


def parse_levels(value: str | None) -> float | None:
    """An OSM `building:levels` count. None if missing, invalid or <= 0."""
    return _positive(_LEVELS_RE, value)


@dataclass(frozen=True)
class ResolvedHeight:
    height_m: float
    source: HeightSource
    problems: tuple[str, ...] = ()  # invalid tags that were ignored


def _valid(value: float | None) -> bool:
    return value is not None and math.isfinite(value) and value > 0


def resolve_height(
    tags: Mapping[str, str],
    measured_m: float | None = None,
    lidar_m: float | None = None,
) -> ResolvedHeight:
    """The building height and where it came from, in priority order.

    `lidar_m` is an accepted lidar height (`LidarResult.height_m`), `measured_m` an MML 3D
    height. Missing, NaN, zero or negative values fall through to the next source.
    """
    problems: list[str] = []
    for key in ("height", "building:levels"):
        parse = parse_height if key == "height" else parse_levels
        if key in tags and parse(tags[key]) is None:
            problems.append(f"invalid {key}={tags[key]!r}")
    if lidar_m is not None and _valid(lidar_m):
        return ResolvedHeight(lidar_m, "mml_lidar", tuple(problems))
    if measured_m is not None and math.isfinite(measured_m) and measured_m > 0:
        return ResolvedHeight(measured_m, "mml_3d", tuple(problems))
    height = parse_height(tags.get("height"))
    if height is not None:
        return ResolvedHeight(height, "osm_height", tuple(problems))
    levels = parse_levels(tags.get("building:levels"))
    if levels is not None:
        return ResolvedHeight(levels * LEVEL_HEIGHT_M, "osm_levels", tuple(problems))
    return ResolvedHeight(DEFAULT_LEVELS * LEVEL_HEIGHT_M, "default", tuple(problems))


def match_measured_heights(
    footprints: Iterable[LocalFootprint], measured: Iterable[LocalFootprint]
) -> dict[str, float]:
    """Measured heights for footprints, from the 3D building that covers most of each.

    A 3D building lends its height when it covers at least `MIN_MATCH_OVERLAP` of the
    footprint's area.
    """
    candidates = [m for m in measured if m.measured_height_m is not None]
    if not candidates:
        return {}
    tree = shapely.STRtree([m.geometry for m in candidates])
    result: dict[str, float] = {}
    for footprint in footprints:
        best, best_area = None, 0.0
        for index in tree.query(footprint.geometry, predicate="intersects"):
            area = footprint.geometry.intersection(candidates[int(index)].geometry).area
            if area > best_area:
                best, best_area = candidates[int(index)], area
        if best is not None and best_area >= MIN_MATCH_OVERLAP * footprint.geometry.area:
            assert best.measured_height_m is not None
            result[footprint.id] = best.measured_height_m
    return result


def node_name(building_id: str) -> str:
    """The glTF node name of a building: "osm:way/123" -> "bld_osm_way_123"."""
    return "bld_" + re.sub(r"[^A-Za-z0-9]+", "_", building_id).strip("_")


def collision_names(node: str, parts: int) -> tuple[str, ...]:
    """Collision node names, one per footprint part.

    Godot imports nodes ending in `-colonly` as collision shapes only and strips the suffix
    from the body's name. Every building, also a one-part one, gets "<node>_c0-colonly",
    "<node>_c1-colonly"..., so no body is named like the visual node "<node>" (Godot would
    rename one of them).
    """
    if parts < 1:
        raise ValueError("a building has at least one part")
    return tuple(f"{node}_c{k}-colonly" for k in range(parts))


def _polygons(geometry: Polygon | MultiPolygon) -> list[Polygon]:
    return [geometry] if isinstance(geometry, Polygon) else list(geometry.geoms)


def _walls(ring: np.ndarray, z0: float, z1: float) -> tuple[np.ndarray, np.ndarray]:
    """Outward-facing wall quads for a ring (counter-clockwise for outer rings)."""
    p = ring[:-1] if np.allclose(ring[0], ring[-1]) else ring
    q = np.roll(p, -1, axis=0)
    keep = np.linalg.norm(q - p, axis=1) > 1e-9
    p, q = p[keep], q[keep]
    n = len(p)
    if n == 0:
        return np.zeros((0, 3)), np.zeros((0, 3), dtype=np.int64)
    low, high = np.full((n, 1), z0), np.full((n, 1), z1)
    quad = np.stack(
        [np.hstack([p, low]), np.hstack([q, low]), np.hstack([q, high]), np.hstack([p, high])],
        axis=1,
    ).reshape(-1, 3)
    base = (np.arange(n) * 4)[:, None]
    faces = np.concatenate([base + [0, 1, 2], base + [0, 2, 3]]).astype(np.int64)
    return quad, faces


def extrude(
    geometry: Polygon | MultiPolygon, z0: float, z1: float
) -> tuple[np.ndarray, np.ndarray]:
    """A closed prism from z0 to z1: flat top, bottom and walls; holes become courtyards.

    Returns (vertices (n, 3), faces (m, 3)) with outward-facing counter-clockwise faces.
    """
    if not z1 > z0:
        raise ValueError("z1 must be above z0")
    vertices: list[np.ndarray] = []
    faces: list[np.ndarray] = []
    offset = 0

    def add(v: np.ndarray, f: np.ndarray) -> None:
        nonlocal offset
        vertices.append(v)
        faces.append(f + offset)
        offset += len(v)

    for polygon in _polygons(geometry):
        polygon = orient(polygon, sign=1.0)
        flat, tris = triangulate_polygon(polygon, engine="earcut")
        flat = np.asarray(flat, dtype=float)
        tris = np.asarray(tris, dtype=np.int64)
        if len(tris):
            a, b, c = flat[tris[:, 0]], flat[tris[:, 1]], flat[tris[:, 2]]
            cross = (b[:, 0] - a[:, 0]) * (c[:, 1] - a[:, 1]) - (b[:, 1] - a[:, 1]) * (
                c[:, 0] - a[:, 0]
            )
            tris = np.where((cross < 0)[:, None], tris[:, ::-1], tris)
            ones = np.ones((len(flat), 1))
            add(np.hstack([flat, ones * z1]), tris)
            add(np.hstack([flat, ones * z0]), tris[:, ::-1])
        for ring in [polygon.exterior, *polygon.interiors]:
            add(*_walls(np.asarray(ring.coords, dtype=float)[:, :2], z0, z1))
    if not vertices:
        return np.zeros((0, 3)), np.zeros((0, 3), dtype=np.int64)
    return np.concatenate(vertices), np.concatenate(faces)


def collision_box(polygon: Polygon) -> Polygon:
    """A polygon's minimum rotated rectangle. Holes and concave corners are filled."""
    # GEOS's oriented envelope warns on axis-aligned edges but returns the right box.
    with np.errstate(divide="ignore", invalid="ignore"):
        box = shapely.minimum_rotated_rectangle(polygon)
    if not isinstance(box, Polygon) or not box.area > 0 or not math.isfinite(box.area):
        raise ValueError("footprint has no area")
    return box


def collision_boxes(geometry: Polygon | MultiPolygon) -> list[Polygon]:
    """One box per footprint part, so separate parts don't get a box spanning the gap."""
    return [collision_box(part) for part in _polygons(geometry)]


@dataclass(frozen=True)
class Building:
    id: str
    tile: str
    node: str
    collision_nodes: tuple[str, ...]  # one per footprint part, same order
    footprint: Polygon | MultiPolygon  # local coordinates
    height_m: float
    height_source: HeightSource
    base_z: float
    tags: Mapping[str, str] = field(default_factory=dict)
    problems: tuple[str, ...] = ()
    lidar: LidarResult | None = None  # None when the build has no point cloud source
    # Place fields (category, names, address, POI links, MML match), from `build.places`.
    place: Mapping[str, Any] = field(default_factory=dict)

    def __post_init__(self) -> None:
        if not (math.isfinite(self.height_m) and self.height_m > 0):
            raise ValueError(f"{self.id}: height must be finite and positive, not {self.height_m}")
        if not math.isfinite(self.base_z):
            raise ValueError(f"{self.id}: base_z must be finite")

    @property
    def top_z(self) -> float:
        return self.base_z + self.height_m

    def visual_mesh(self) -> tuple[np.ndarray, np.ndarray]:
        return extrude(self.footprint, self.base_z, self.top_z)

    def collision_meshes(self) -> list[tuple[str, np.ndarray, np.ndarray]]:
        boxes = collision_boxes(self.footprint)
        if len(boxes) != len(self.collision_nodes):
            raise ValueError(f"{self.id}: {len(boxes)} collision boxes for the node names")
        return [
            (name, *extrude(box, self.base_z, self.top_z))
            for name, box in zip(self.collision_nodes, boxes, strict=True)
        ]

    def lidar_provenance(self) -> dict[str, Any]:
        """Lidar details: the selection for a lidar height, or why lidar was rejected."""
        lidar = self.lidar
        if lidar is None:
            return {}
        if lidar.rejected is not None:
            return {"lidar_rejected": lidar.rejected}
        if self.height_source != "mml_lidar":
            return {}
        assert lidar.roof_z_median is not None and lidar.roof_z_p90 is not None
        return {
            "lidar_points": lidar.points,
            "lidar_buffer_m": lidar.buffer_m,
            "lidar_returns": lidar.returns,
            "roof_z_median": round(lidar.roof_z_median, 3),
            "roof_z_p90": round(lidar.roof_z_p90, 3),
        }

    def provenance(self) -> dict[str, Any]:
        """The entry for the pack's `buildings.json` (and the stage 3 GeoJSON)."""
        return {
            "id": self.id,
            "tile": self.tile,
            "node": self.node,
            "collision_nodes": list(self.collision_nodes),
            "height_m": round(self.height_m, 3),
            "height_source": self.height_source,
            "base_z": round(self.base_z, 3),
            "base_source": "dem",
            "roof_shape": "flat",
            "roof_shape_source": "default",
            **self.lidar_provenance(),
            **self.place,
        }


@dataclass
class BuildingsResult:
    buildings: list[Building]

    def by_tile(self) -> dict[str, list[Building]]:
        tiles: dict[str, list[Building]] = {}
        for building in self.buildings:
            tiles.setdefault(building.tile, []).append(building)
        return tiles

    def report(self) -> dict[str, Any]:
        counts = Counter(b.height_source for b in self.buildings)
        lidar = [b for b in self.buildings if b.lidar is not None]
        used = [b.lidar for b in lidar if b.height_source == "mml_lidar" and b.lidar is not None]
        rejected = Counter(b.lidar.rejected for b in lidar if b.lidar and b.lidar.rejected)
        buffers = Counter(str(float(r.buffer_m)) for r in used if r.buffer_m is not None)
        return {
            "building_count": len(self.buildings),
            "height_source_counts": {source: counts.get(source, 0) for source in HEIGHT_SOURCES},
            "lidar": {
                "buildings_checked": len(lidar),
                "rejected_counts": {reason: rejected.get(reason, 0) for reason in REJECT_REASONS},
                "buffer_counts": dict(sorted(buffers.items(), key=lambda kv: -float(kv[0]))),
                "single_returns": sum(1 for r in used if r.returns == "single"),
            },
            "ignored_tags": [
                {"id": b.id, "problem": problem} for b in self.buildings for problem in b.problems
            ],
        }


def _unique_names(name: str, parts: int, used: set[str]) -> tuple[str, tuple[str, ...]]:
    """A visual node name and its collision names, none of them used yet."""
    candidate, n = name, 2
    while True:
        collisions = collision_names(candidate, parts)
        if candidate not in used and not used.intersection(collisions):
            used.add(candidate)
            used.update(collisions)
            return candidate, collisions
        candidate = f"{name}_{n}"
        n += 1


def resolve_buildings(
    footprints: Iterable[LocalFootprint],
    heightfield: Heightfield,
    area: BuildArea,
    measured_heights: Mapping[str, float] | None = None,
    points: PointCloud | None = None,
    lidar_params: LidarParams | None = None,
) -> BuildingsResult:
    """Resolve height, base and tile for every footprint whose centroid is in the area.

    With `points` (non-ground lidar points in local coordinates), each footprint first
    tries a lidar height with `lidar_params` (defaults when None).
    """
    measured = measured_heights or {}
    point_index = PointIndex(points) if points is not None else None
    used: set[str] = set()
    buildings: list[Building] = []
    for footprint in sorted(footprints, key=lambda f: f.id):
        centroid = footprint.geometry.centroid
        index = area.tile_index(centroid.x, centroid.y)
        if index is None:
            raise ValueError(f"{footprint.id}: centroid is outside the build area")
        base_z = heightfield.min_under(footprint.geometry)
        lidar = (
            lidar_height(footprint.geometry, point_index, base_z, lidar_params)
            if point_index is not None
            else None
        )
        height = resolve_height(
            footprint.tags, measured.get(footprint.id), lidar.height_m if lidar else None
        )
        parts = len(_polygons(footprint.geometry))
        node, collisions = _unique_names(node_name(footprint.id), parts, used)
        buildings.append(
            Building(
                id=footprint.id,
                tile=tile_id(*index),
                node=node,
                collision_nodes=collisions,
                footprint=footprint.geometry,
                height_m=height.height_m,
                height_source=height.source,
                base_z=base_z,
                tags=footprint.tags,
                problems=height.problems,
                lidar=lidar,
            )
        )
    return BuildingsResult(buildings)
