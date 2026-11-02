"""The spawn area check (docs/specs/world-edge-pipeline.md, "Spawn area").

Open ground = the spawn polygon minus the union of the obstacles (building footprints,
water areas, road and path surfaces including bridge decks), each grown by
`obstacle_buffer_m`, and minus everything within `min_boundary_distance_m` of the
boundary. The check passes when the open ground has at least `min_open_area_m2` and a
disc of radius `min_clear_radius_m` fits in it.
"""

from collections.abc import Sequence
from dataclasses import dataclass, field
from typing import Any

import shapely
from shapely.geometry import MultiPolygon, Polygon
from shapely.geometry.base import BaseGeometry

from mapgen.config import SpawnConfig

CLEAR_RADIUS_TOLERANCE_M = 0.01  # precision of the largest clear disc
REPORTED_OVERLAPS = 5


@dataclass(frozen=True)
class Obstacle:
    kind: str  # "building", "water", "road", "path" or "bridge"
    id: str
    geometry: BaseGeometry


@dataclass
class SpawnResult:
    polygon: Polygon
    open_ground: Polygon | MultiPolygon
    open_area_m2: float
    clear_radius_m: float
    boundary_distance_m: float  # from the spawn polygon to the boundary
    passed: bool
    overlaps: list[dict[str, Any]] = field(default_factory=list)  # largest first

    def report(self) -> dict[str, Any]:
        return {
            "passed": self.passed,
            "spawn_area_m2": round(self.polygon.area, 3),
            "open_area_m2": round(self.open_area_m2, 3),
            "clear_radius_m": round(self.clear_radius_m, 3),
            "boundary_distance_m": round(self.boundary_distance_m, 3),
            "largest_overlaps": self.overlaps,
        }

    def failure(self, config: SpawnConfig) -> str:
        overlaps = ", ".join(
            f"{o['kind']} {o['id']} {o['overlap_m2']:.1f} m²" for o in self.overlaps
        )
        return (
            f"spawn area check failed: open ground {self.open_area_m2:.1f} m² (minimum "
            f"{config.min_open_area_m2:g}), largest clear disc r {self.clear_radius_m:.2f} m "
            f"(minimum {config.min_clear_radius_m:g}), spawn area {self.polygon.area:.1f} m², "
            f"{self.boundary_distance_m:.2f} m from the boundary; largest obstacle overlaps "
            f"(grown by {config.obstacle_buffer_m:g} m): {overlaps or 'none'}"
        )


def polygonal(geometry: BaseGeometry) -> Polygon | MultiPolygon:
    """The polygons of `geometry` with an area (an empty MultiPolygon when none)."""
    if isinstance(geometry, Polygon):
        return geometry if geometry.area > 0 else MultiPolygon()
    parts: list[Polygon] = []
    for part in getattr(geometry, "geoms", []):
        sub = polygonal(part)
        parts += [sub] if isinstance(sub, Polygon) else list(sub.geoms)
    parts = [p for p in parts if p.area > 0]
    if len(parts) == 1:
        return parts[0]
    return MultiPolygon(parts)


def clear_radius(geometry: BaseGeometry) -> float:
    """The radius of the largest disc that fits in `geometry` (0 when it is empty)."""
    best = 0.0
    shape = polygonal(geometry)
    parts = [shape] if isinstance(shape, Polygon) else list(shape.geoms)
    for part in parts:
        circle = shapely.maximum_inscribed_circle(part, CLEAR_RADIUS_TOLERANCE_M)
        best = max(best, float(circle.length))
    return best


def open_ground(
    spawn: Polygon,
    boundary: Polygon,
    obstacles: Sequence[Obstacle],
    config: SpawnConfig,
) -> Polygon | MultiPolygon:
    """The spawn polygon minus the grown obstacles and the strip along the boundary."""
    grown = [o.geometry.buffer(config.obstacle_buffer_m) for o in obstacles]
    ground = spawn.intersection(boundary.buffer(-config.min_boundary_distance_m))
    if grown:
        ground = ground.difference(shapely.union_all(grown))
    return polygonal(ground)


def spawn_check(
    spawn: Polygon,
    boundary: Polygon,
    obstacles: Sequence[Obstacle],
    config: SpawnConfig,
) -> SpawnResult:
    """Measure the open ground of `spawn` (see the module docstring)."""
    ground = open_ground(spawn, boundary, obstacles, config)
    radius = clear_radius(ground)
    overlaps = sorted(
        (
            {"kind": o.kind, "id": o.id, "overlap_m2": round(area, 3)}
            for o in obstacles
            if (area := o.geometry.buffer(config.obstacle_buffer_m).intersection(spawn).area) > 0
        ),
        key=lambda o: (-o["overlap_m2"], o["kind"], o["id"]),
    )[:REPORTED_OVERLAPS]
    passed = ground.area >= config.min_open_area_m2 and radius >= config.min_clear_radius_m
    return SpawnResult(
        polygon=spawn,
        open_ground=ground,
        open_area_m2=float(ground.area),
        clear_radius_m=radius,
        boundary_distance_m=float(spawn.distance(boundary.exterior)),
        passed=passed,
        overlaps=overlaps,
    )


def crossing_buildings(
    boundary: Polygon, footprints: Sequence[tuple[str, BaseGeometry]], clearance_m: float
) -> list[dict[str, Any]]:
    """The footprints that cross the boundary: partly outside it, or closer to it than
    `clearance_m`. Each: `id`, `area_outside_m2` and `distance_m` (to the boundary line,
    0 when it crosses the line), in input order."""
    line = boundary.exterior
    shapely.prepare(boundary)
    result: list[dict[str, Any]] = []
    for fid, geometry in footprints:
        distance = float(geometry.distance(line))
        if boundary.contains(geometry) and distance >= clearance_m:
            continue
        result.append(
            {
                "id": fid,
                "area_outside_m2": float(geometry.difference(boundary).area),
                "distance_m": distance,
            }
        )
    return result
