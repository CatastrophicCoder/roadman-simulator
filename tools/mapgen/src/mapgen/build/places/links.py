"""Links between points of interest and buildings (docs/specs/place-information.md).

A POI is on or in a building when it is the building itself (POI tags on the building;
same id), when it is a node covered by the footprint (boundary included), or when it is
an area that isn't a building and whose representative point (`point_on_surface`) is
covered by the footprint. When several footprints cover the point, the smallest wins
(ties: the lower id) and the case is reported. A building is inside a POI area when an
area POI that isn't a building covers its footprint centroid. All lists are sorted by id.
"""

from collections.abc import Sequence
from dataclasses import dataclass, field
from typing import Any

import shapely
from shapely.geometry import MultiPolygon, Point, Polygon

from mapgen.build.places.matching import HasFootprint


@dataclass(frozen=True)
class LinkPoi:
    """A POI as the linker sees it: its local geometry and representative point."""

    id: str
    geometry: Point | Polygon | MultiPolygon
    position: Point  # the node, or the area's representative point (local x, y)
    is_building: bool  # carries a `building` tag (an area POI that is a building)


@dataclass
class PoiLinks:
    building_of_poi: dict[str, str] = field(default_factory=dict)
    pois_of_building: dict[str, list[str]] = field(default_factory=dict)
    areas_of_building: dict[str, list[str]] = field(default_factory=dict)
    buildings_of_area: dict[str, list[str]] = field(default_factory=dict)
    several_footprints: list[dict[str, Any]] = field(default_factory=list)


def link_pois(buildings: Sequence[HasFootprint], pois: Sequence[LinkPoi]) -> PoiLinks:
    links = PoiLinks()
    by_id = {b.id: b for b in buildings}
    order = sorted(by_id)
    geometries = [by_id[i].geometry for i in order]
    tree = shapely.STRtree(geometries) if geometries else None

    def covering(point: Point) -> list[str]:
        if tree is None:
            return []
        hits = tree.query(point, predicate="intersects")
        return sorted(order[int(i)] for i in hits if geometries[int(i)].covers(point))

    for poi in sorted(pois, key=lambda p: p.id):
        if poi.id in by_id:
            links.building_of_poi[poi.id] = poi.id
            continue
        if poi.is_building:
            continue  # a building POI that isn't one of the build's buildings
        candidates = covering(poi.position)
        if not candidates:
            continue
        chosen = min(candidates, key=lambda b: (by_id[b].geometry.area, b))
        if len(candidates) > 1:
            links.several_footprints.append(
                {"poi": poi.id, "buildings": candidates, "chosen": chosen}
            )
        links.building_of_poi[poi.id] = chosen

    for poi_id, building_id in links.building_of_poi.items():
        links.pois_of_building.setdefault(building_id, []).append(poi_id)

    areas = [p for p in pois if not isinstance(p.geometry, Point) and not p.is_building]
    for building_id in order:
        centroid = by_id[building_id].geometry.centroid
        for area in areas:
            if area.geometry.covers(centroid):
                links.areas_of_building.setdefault(building_id, []).append(area.id)
                links.buildings_of_area.setdefault(area.id, []).append(building_id)

    for mapping in (links.pois_of_building, links.areas_of_building, links.buildings_of_area):
        for key in mapping:
            mapping[key] = sorted(mapping[key])
    return links
