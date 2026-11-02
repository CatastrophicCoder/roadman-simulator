"""Water on the boundary: crossings of water areas, water lines, and OSM/MML differences.

A water crossing is a stretch of the ring inside the union of the water areas; touching
areas (a lake and the river it drains into) give one crossing listing both ids. Stretches
shorter than `MIN_CROSSING_M` are ignored, so a fence that overlaps water by less than
that is never a water fence (the pack check uses the same limit).
"""

from collections.abc import Sequence
from dataclasses import dataclass
from typing import Any

import shapely
from shapely.geometry import LineString, MultiPoint, Point
from shapely.geometry.base import BaseGeometry

from mapgen.build.world_edge.ring import Interval, Ring

MIN_CROSSING_M = 1e-3  # a stretch of water shorter than 1 mm isn't a crossing
DISAGREEMENT_M = 1.0  # crossing ends further apart than this between sources: reported


@dataclass(frozen=True)
class WaterFeature:
    """A water area or line in local coordinates, with the source it came from."""

    id: str
    geometry: BaseGeometry


@dataclass(frozen=True)
class WaterCrossing:
    s_start: float
    s_end: float  # may exceed the ring length for a crossing over the ring's start
    water_ids: tuple[str, ...]
    source: str

    @property
    def length(self) -> float:
        return self.s_end - self.s_start

    @property
    def interval(self) -> Interval:
        return (self.s_start, self.s_end)


def water_crossings(ring: Ring, areas: Sequence[WaterFeature], source: str) -> list[WaterCrossing]:
    """The stretches of `ring` inside the water areas, sorted by `s_start`."""
    if not areas:
        return []
    union = shapely.union_all([a.geometry for a in areas])
    result: list[WaterCrossing] = []
    for a, b in ring.intervals(union, min_length=MIN_CROSSING_M):
        stretch = ring.path_line(a, b)
        ids = sorted(
            {
                area.id
                for area in areas
                if stretch.intersection(area.geometry).length > MIN_CROSSING_M
            }
        )
        result.append(WaterCrossing(a, b, tuple(ids), source))
    return sorted(result, key=lambda c: c.s_start)


def line_crossings(ring: Ring, lines: Sequence[WaterFeature], source: str) -> list[dict[str, Any]]:
    """Every point where a water line crosses or touches the ring: `s`, local position,
    line id and source, sorted by `s` then id."""
    boundary = ring.line()
    result: list[dict[str, Any]] = []
    for line in lines:
        hit = boundary.intersection(line.geometry)
        for point in _points(hit):
            result.append(
                {
                    "id": line.id,
                    "source": source,
                    "s": ring.project(point.x, point.y),
                    "local": (point.x, point.y),
                }
            )
    return sorted(result, key=lambda r: (r["s"], r["id"]))


def _points(geometry: BaseGeometry) -> list[Point]:
    """The points of an intersection; a shared stretch counts by its first point."""
    if geometry.is_empty:
        return []
    if isinstance(geometry, Point):
        return [geometry]
    if isinstance(geometry, LineString):
        return [Point(geometry.coords[0])]
    if isinstance(geometry, MultiPoint):
        return list(geometry.geoms)
    return [p for g in getattr(geometry, "geoms", []) for p in _points(g)]


def ids_on(ring: Ring, interval: Interval, areas: Sequence[WaterFeature]) -> list[str]:
    """The ids of the areas that overlap the stretch by more than `MIN_CROSSING_M`."""
    stretch = ring.path_line(*interval)
    return sorted(a.id for a in areas if stretch.intersection(a.geometry).length > MIN_CROSSING_M)


def ring_offset(ring: Ring, s: float, other: float) -> float:
    """The signed distance along the ring from `s` to `other`, the shorter way round:
    in (-length / 2, length / 2]."""
    d = ring.wrap(other - s)
    return d - ring.length if d > ring.length / 2 else d


def disagreements(
    ring: Ring, used: Sequence[WaterCrossing], other: Sequence[WaterCrossing]
) -> list[dict[str, Any]]:
    """Where two sources' crossings differ: water in one and not the other, or ends more
    than `DISAGREEMENT_M` apart along the ring."""
    result: list[dict[str, Any]] = []

    def overlaps(a: WaterCrossing, b: WaterCrossing) -> bool:
        return ring.contains_s(a.interval, b.s_start) or ring.contains_s(b.interval, a.s_start)

    for crossing in used:
        matches = [o for o in other if overlaps(crossing, o)]
        if not matches:
            result.append(
                {
                    "kind": "only_in_used_source",
                    "s_start": crossing.s_start,
                    "s_end": crossing.s_end,
                    "water_ids": list(crossing.water_ids),
                }
            )
            continue
        start = min(matches, key=lambda o: abs(ring_offset(ring, crossing.s_start, o.s_start)))
        end = min(matches, key=lambda o: abs(ring_offset(ring, crossing.s_end, o.s_end)))
        start_s, end_s = start.s_start, end.s_end
        d_start = ring_offset(ring, crossing.s_start, start_s)
        d_end = ring_offset(ring, crossing.s_end, end_s)
        if abs(d_start) > DISAGREEMENT_M or abs(d_end) > DISAGREEMENT_M:
            result.append(
                {
                    "kind": "ends_differ",
                    "s_start": crossing.s_start,
                    "s_end": crossing.s_end,
                    "other_s_start": start_s,
                    "other_s_end": end_s,
                    "start_difference_m": d_start,
                    "end_difference_m": d_end,
                    "water_ids": list(crossing.water_ids),
                    "other_water_ids": sorted({i for o in matches for i in o.water_ids}),
                }
            )
    for crossing in other:
        if not any(overlaps(crossing, u) for u in used):
            result.append(
                {
                    "kind": "only_in_other_source",
                    "s_start": crossing.s_start,
                    "s_end": crossing.s_end,
                    "water_ids": list(crossing.water_ids),
                }
            )
    return sorted(result, key=lambda r: (r["s_start"], r["kind"]))
