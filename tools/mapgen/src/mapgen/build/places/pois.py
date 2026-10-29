"""Points of interest for `pois.json`: positions, outlines and their types.

Positions and outlines are Godot local metres: x east, y up (the terrain height from
`Heightfield.sample`), z = -north. An area's position is its representative point
(shapely `point_on_surface`, deterministic) and its outline is clipped to the build area:
a list of polygons, each a list of closed rings (exterior first, then holes) of `[x, z]`.
A POI is kept when its position is inside the build area.
"""

import math
from collections.abc import Mapping
from dataclasses import dataclass

import shapely
from shapely.geometry import MultiPolygon, Point, Polygon
from shapely.geometry.polygon import orient

from mapgen.build.area import BuildArea
from mapgen.build.stage1_load import LocalPoi
from mapgen.sources.osm import POI_KEYS, is_building

DECIMALS = 3  # millimetres, as in buildings.json


@dataclass(frozen=True)
class PlacePoi:
    """A POI kept for the pack: position (local x, y), clipped outline, and type."""

    poi: LocalPoi
    position: Point
    outline: list[list[list[tuple[float, float]]]] | None  # Godot [x, z] rings
    clipped: Polygon | MultiPolygon | None  # local, for the GeoJSON

    @property
    def id(self) -> str:
        return self.poi.id

    @property
    def geometry(self) -> Point | Polygon | MultiPolygon:
        return self.poi.geometry

    @property
    def is_building(self) -> bool:
        return is_building(dict(self.poi.tags))


def poi_type(tags: Mapping[str, str]) -> tuple[str, str, list[str]]:
    """(type, subtype, other_tags): the first POI key the feature has, its value, and the
    further POI keys as sorted `key=value` strings."""
    present = [key for key in POI_KEYS if key in tags]
    if not present:
        raise ValueError("not a point of interest")
    first = present[0]
    return first, tags[first], sorted(f"{k}={tags[k]}" for k in present[1:])


def _round(value: float) -> float:
    return round(float(value), DECIMALS) + 0.0  # no -0.0


def godot_ring(
    coords: list[tuple[float, float]] | list[tuple[float, ...]],
) -> list[tuple[float, float]]:
    """A local ring as closed Godot `[x, z]` points (z = -north), rounded, without repeats."""
    ring: list[tuple[float, float]] = []
    for x, y, *_ in coords:
        point = (_round(x), _round(-y))
        if not ring or ring[-1] != point:
            ring.append(point)
    if ring and ring[0] != ring[-1]:
        ring.append(ring[0])
    return ring


def godot_outline(geometry: Polygon | MultiPolygon) -> list[list[list[tuple[float, float]]]]:
    """Polygons of closed Godot rings; exteriors counter-clockwise in local x/y.

    Rings that collapse below 4 points after rounding are dropped, and so is a polygon
    whose exterior collapses.
    """
    polygons = [geometry] if isinstance(geometry, Polygon) else list(geometry.geoms)
    result: list[list[list[tuple[float, float]]]] = []
    for polygon in polygons:
        polygon = orient(polygon, sign=1.0)
        exterior = godot_ring(list(polygon.exterior.coords))
        if len(exterior) < 4:
            continue
        holes = [godot_ring(list(r.coords)) for r in polygon.interiors]
        result.append([exterior, *[h for h in holes if len(h) >= 4]])
    return result


def _polygonal(geometry: shapely.Geometry) -> Polygon | MultiPolygon | None:
    parts: list[Polygon] = []
    for part in shapely.get_parts(geometry):
        if isinstance(part, Polygon) and part.area > 0:
            parts.append(part)
        elif isinstance(part, MultiPolygon):
            parts.extend(p for p in part.geoms if p.area > 0)
    if not parts:
        return None
    return parts[0] if len(parts) == 1 else MultiPolygon(parts)


def place_poi(poi: LocalPoi, area: BuildArea) -> PlacePoi | str | None:
    """The POI ready for the pack; None when its position is outside the area, or the
    reason it can't be kept (an outline that can't be built)."""
    geometry = poi.geometry
    position = geometry if isinstance(geometry, Point) else geometry.point_on_surface()
    if position.is_empty or not all(math.isfinite(v) for v in (position.x, position.y)):
        return "no representative point"
    if not area.contains(position.x, position.y):
        return None
    if isinstance(geometry, Point):
        return PlacePoi(poi, position, None, None)
    box = shapely.box(*area.bounds.as_list())
    clipped = _polygonal(geometry.intersection(box))
    if clipped is None:
        return "outline has no area inside the build area"
    outline = godot_outline(clipped)
    if not outline:
        return "outline collapsed after rounding"
    return PlacePoi(poi, position, outline, clipped)
