"""Matching buildings to building-use records (e.g. MML Topographic Database buildings).

Each building gets at most one record: the one with the largest overlap with its
footprint, if that covers at least half of the footprint (`MIN_MATCH_OVERLAP`, the rule
of docs/specs/building-heights.md). Ties go to the lower record number, so builds are
deterministic. Several buildings may match the same record.
"""

from collections.abc import Sequence
from dataclasses import dataclass
from typing import Protocol

import shapely
from shapely.geometry import MultiPolygon, Polygon

from mapgen.build.buildings import MIN_MATCH_OVERLAP
from mapgen.build.stage1_load import LocalBuildingUse

OVERLAP_DECIMALS = 3


class HasFootprint(Protocol):
    @property
    def id(self) -> str: ...

    @property
    def geometry(self) -> Polygon | MultiPolygon: ...


@dataclass(frozen=True)
class UseMatch:
    record: LocalBuildingUse
    overlap: float  # share of the footprint the record covers, 0-1, rounded to 3 decimals


def record_number(record_id: str) -> int:
    """The number at the end of an id ("mml:rakennus/123" -> 123); -1 if there is none."""
    tail = record_id.rsplit("/", 1)[-1]
    return int(tail) if tail.isdigit() else -1


def match_building_use(
    footprints: Sequence[HasFootprint], records: Sequence[LocalBuildingUse]
) -> dict[str, UseMatch]:
    """The matched record of each footprint that has one, by footprint id."""
    if not records:
        return {}
    tree = shapely.STRtree([r.geometry for r in records])
    result: dict[str, UseMatch] = {}
    for footprint in footprints:
        area = footprint.geometry.area
        if not area > 0:
            continue
        best: LocalBuildingUse | None = None
        best_area = 0.0
        for index in sorted(int(i) for i in tree.query(footprint.geometry, "intersects")):
            record = records[index]
            shared = footprint.geometry.intersection(record.geometry).area
            if shared > best_area or (
                shared == best_area
                and best is not None
                and shared > 0
                and (record_number(record.id), record.id) < (record_number(best.id), best.id)
            ):
                best, best_area = record, shared
        if best is not None and best_area >= MIN_MATCH_OVERLAP * area:
            overlap = min(1.0, round(best_area / area, OVERLAP_DECIMALS))
            result[footprint.id] = UseMatch(best, overlap)
    return result
