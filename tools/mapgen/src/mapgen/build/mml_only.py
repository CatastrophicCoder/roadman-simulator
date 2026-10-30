"""Buildings from building-use records that no OSM building matched (spec
docs/specs/mml-only-buildings.md).

Each record of the `building_use` source (e.g. MML Topographic Database `rakennus`) is
looked at in record number order (`record_number`):

1. `matched`: an OSM building matched it (ADR 0013 matching). Not added.
2. `outside`: its centroid (or the centroid of what is kept of it) is outside the build
   area. Not added.
3. `too_small`: its footprint is under `MIN_AREA_M2`. Not added.
4. Cut: the footprint minus the union of the OSM footprints it shares area with splits
   into pieces (the polygons of the difference). A piece under `SLIVER_AREA_M2`, or one
   of which less than `SLIVER_MIN_SURVIVING_SHARE` of the area survives a morphological
   opening by `SLIVER_SHRINK_M` (`buffer(-r).buffer(r)`: removes every part narrower than
   1.5 m, straight or bent, and only rounds the corners of a wider piece), is a sliver.
   A record without OSM overlap is one whole piece and isn't tested. A record with only
   slivers left is `slivers`.
5. `lidar_contradicts`: at least `CONTRADICTION_MIN_POINTS` lidar points (ground
   included, noise excluded) inside the kept footprint, and at least
   `CONTRADICTION_SHARE` of them within `CONTRADICTION_HEIGHT_M` of the ground
   (|z - `Heightfield.sample`|). Not added.
6. Otherwise one building: id = the record id, the kept pieces as one footprint.

The kept footprints become `LocalFootprint`s with empty tags, so heights, bases, nodes and
collision boxes follow the OSM building rules. `mml_only_report` builds the `mml_only`
block of `03-buildings/report.json`.
"""

import math
from collections import Counter
from collections.abc import Iterable, Mapping, Sequence
from dataclasses import dataclass
from typing import Any, Literal

import numpy as np
import shapely
from shapely.geometry import MultiPolygon, Polygon
from shapely.geometry.base import BaseGeometry

from mapgen.build.area import BuildArea
from mapgen.build.buildings import HEIGHT_SOURCES, Building
from mapgen.build.heightfield import Heightfield
from mapgen.build.lidar import REJECT_REASONS, PointIndex
from mapgen.build.places.categories import CATEGORIES, CATEGORY_SOURCES
from mapgen.build.places.matching import UseMatch, record_number
from mapgen.build.stage1_load import LocalBuildingUse, LocalFootprint
from mapgen.sources.base import PointCloud

__all__ = [
    "CONTRADICTION_HEIGHT_M",
    "CONTRADICTION_MIN_POINTS",
    "CONTRADICTION_SHARE",
    "MIN_AREA_M2",
    "NOT_ADDED_REASONS",
    "SLIVER_AREA_M2",
    "SLIVER_MIN_SURVIVING_SHARE",
    "SLIVER_SHRINK_M",
    "GroundCheck",
    "MmlOnlyRecord",
    "MmlOnlySelection",
    "compactness",
    "cut_record",
    "ground_check",
    "is_sliver",
    "mml_only_report",
    "opened_share",
    "road_overlaps",
    "select_mml_only",
]

MIN_AREA_M2 = 1.0  # a record under this is `too_small`
SLIVER_AREA_M2 = 4.0  # a cut piece under this is a sliver
SLIVER_SHRINK_M = 0.75  # the opening radius of the sliver test (parts under 1.5 m go)
SLIVER_MIN_SURVIVING_SHARE = 0.5  # a cut piece keeping less of its area in the opening
CONTRADICTION_MIN_POINTS = 10  # lidar rule: at least this many points inside
CONTRADICTION_SHARE = 0.9  # ... and at least this share of them
CONTRADICTION_HEIGHT_M = 1.5  # ... within this of the ground
DECIMALS = 3

Outcome = Literal["matched", "outside", "too_small", "slivers", "lidar_contradicts", "added"]
NotAddedReason = Literal["outside", "too_small", "slivers", "lidar_contradicts"]
NOT_ADDED_REASONS: tuple[NotAddedReason, ...] = (
    "outside",
    "too_small",
    "slivers",
    "lidar_contradicts",
)


def _r(value: float) -> float:
    return round(float(value), DECIMALS) + 0.0


@dataclass(frozen=True)
class GroundCheck:
    """The lidar points inside a footprint compared with the ground (`Heightfield`)."""

    points: int  # points inside (ground included, noise excluded)
    within: int  # of them, within `CONTRADICTION_HEIGHT_M` of the ground
    max_above_m: float | None  # highest point above the ground (z - ground); None: no points

    @property
    def share(self) -> float | None:
        return self.within / self.points if self.points else None

    @property
    def contradicts(self) -> bool:
        """At least `CONTRADICTION_MIN_POINTS` points, and `CONTRADICTION_SHARE` within."""
        return (
            self.points >= CONTRADICTION_MIN_POINTS
            and self.within >= CONTRADICTION_SHARE * self.points
        )

    def as_dict(self) -> dict[str, Any]:
        share = self.share
        return {
            "points_inside": self.points,
            "share_near_ground": _r(share) if share is not None else None,
            "max_above_ground_m": _r(self.max_above_m) if self.max_above_m is not None else None,
        }


def ground_check(
    footprint: Polygon | MultiPolygon, points: PointIndex, heightfield: Heightfield
) -> GroundCheck:
    """Count the points strictly inside `footprint` and those within
    `CONTRADICTION_HEIGHT_M` of `heightfield` (absolute difference)."""
    candidates = points.in_bounds(*footprint.bounds)
    if len(candidates) == 0:
        return GroundCheck(0, 0, None)
    cloud = points.cloud
    x, y, z = cloud.x[candidates], cloud.y[candidates], cloud.z[candidates]
    inside = np.asarray(shapely.contains_xy(footprint, x, y), dtype=bool)
    if not inside.any():
        return GroundCheck(0, 0, None)
    above = z[inside] - heightfield.sample(x[inside], y[inside])
    within = int(np.count_nonzero(np.abs(above) <= CONTRADICTION_HEIGHT_M))
    return GroundCheck(int(inside.sum()), within, float(above.max()))


def opened_share(piece: Polygon) -> float:
    """The share of `piece`'s area left by a morphological opening by `SLIVER_SHRINK_M`
    (shrink inward, then grow outward by the same distance); 0 for a piece without area."""
    if not piece.area > 0:
        return 0.0
    opened = piece.buffer(-SLIVER_SHRINK_M).buffer(SLIVER_SHRINK_M)
    return float(opened.area / piece.area)


def is_sliver(piece: Polygon) -> bool:
    """A cut piece under `SLIVER_AREA_M2`, or one of which less than
    `SLIVER_MIN_SURVIVING_SHARE` of the area survives the opening (`opened_share`)."""
    if not piece.area >= SLIVER_AREA_M2:
        return True
    return opened_share(piece) < SLIVER_MIN_SURVIVING_SHARE


def compactness(polygon: Polygon) -> float:
    """4·pi·area / perimeter² (1 for a circle), the perimeter including holes."""
    length = polygon.length
    return 4 * math.pi * polygon.area / (length * length) if length > 0 else 0.0


def _polygons_of(geometry: BaseGeometry) -> list[Polygon]:
    """The polygons with area in a geometry (collections flattened); lines and points go."""
    if geometry.is_empty:
        return []
    if isinstance(geometry, Polygon):
        return [geometry] if geometry.area > 0 else []
    result: list[Polygon] = []
    for part in getattr(geometry, "geoms", []):
        result += _polygons_of(part)
    return result


def _piece_order(piece: Polygon) -> tuple[float, ...]:
    return (-piece.area, *piece.bounds)


def _as_footprint(pieces: Sequence[Polygon]) -> Polygon | MultiPolygon:
    ordered = sorted(pieces, key=_piece_order)
    return ordered[0] if len(ordered) == 1 else MultiPolygon(ordered)


@dataclass(frozen=True)
class CutResult:
    kept: list[Polygon]  # largest first
    discarded: int  # slivers (and parts without area)
    cut_by: tuple[str, ...]  # ids of the OSM footprints sharing area with the record


def cut_record(
    geometry: Polygon | MultiPolygon, overlapping: Sequence[tuple[str, BaseGeometry]]
) -> CutResult:
    """Step 4 for one record: `overlapping` are (id, footprint) of the OSM buildings that
    may overlap it; those sharing area (> 0) are cut away."""
    shared = [(fid, g) for fid, g in overlapping if geometry.intersection(g).area > 0]
    if not shared:
        return CutResult(sorted(_polygons_of(geometry), key=_piece_order), 0, ())
    rest = geometry.difference(shapely.union_all([g for _, g in shared]))
    pieces = _polygons_of(rest)
    kept = sorted((p for p in pieces if not is_sliver(p)), key=_piece_order)
    discarded = len(pieces) - len(kept)
    if not kept and not pieces:
        discarded = 0
    return CutResult(kept, discarded, tuple(sorted(fid for fid, _ in shared)))


@dataclass(frozen=True)
class MmlOnlyRecord:
    """What happened to one unmatched record."""

    record: LocalBuildingUse
    outcome: Outcome
    record_area_m2: float
    kept: Polygon | MultiPolygon | None = None  # the footprint (also for lidar_contradicts)
    pieces: tuple[Polygon, ...] = ()  # kept pieces, largest first
    pieces_discarded: int = 0
    cut_by: tuple[str, ...] = ()  # OSM ids cut away; empty: whole
    ground: GroundCheck | None = None  # None without a point cloud

    @property
    def id(self) -> str:
        return self.record.id

    @property
    def is_cut(self) -> bool:
        return bool(self.cut_by)


@dataclass
class MmlOnlySelection:
    source: str | None  # the building-use source; None when there is none
    records_in_area: int
    records_matched: int
    records: list[MmlOnlyRecord]  # the unmatched records, in record number order

    @property
    def added(self) -> list[MmlOnlyRecord]:
        return [r for r in self.records if r.outcome == "added"]

    def footprints(self) -> list[LocalFootprint]:
        """The added buildings' footprints (id = record id, no tags)."""
        result = []
        for r in self.added:
            assert r.kept is not None
            result.append(LocalFootprint(r.id, r.kept))
        return result

    def records_by_id(self) -> dict[str, LocalBuildingUse]:
        """The record of each added building, by building id (= record id)."""
        return {r.id: r.record for r in self.added}


def _empty_selection(source: str | None) -> MmlOnlySelection:
    return MmlOnlySelection(source, 0, 0, [])


def select_mml_only(
    records: Sequence[LocalBuildingUse] | None,
    matches: Mapping[str, UseMatch],
    footprints: Sequence[LocalFootprint],
    area: BuildArea,
    heightfield: Heightfield,
    points: PointCloud | None = None,
    ground_points: PointCloud | None = None,
    source: str | None = None,
) -> MmlOnlySelection:
    """Steps 1-6 for every record (`records`: those intersecting the area; None without a
    building-use source). `matches` are the OSM footprints' matches by footprint id.

    `points` (non-ground) and `ground_points` (class 2) are lidar points in local
    coordinates; with neither, the lidar contradiction step is skipped.
    """
    if records is None:
        return _empty_selection(None)
    matched = {m.record.id for m in matches.values()}
    clouds = [c for c in (points, ground_points) if c is not None and len(c)]
    index: PointIndex | None = None
    if points is not None or ground_points is not None:
        # Not `points or ground_points`: an empty PointCloud is falsy (it has __len__).
        crs = points.crs if points is not None else ground_points.crs  # type: ignore[union-attr]
        index = PointIndex(PointCloud.concatenate(clouds, crs) if clouds else PointCloud.empty(crs))
    osm = [(f.id, f.geometry) for f in footprints]
    tree = shapely.STRtree([g for _, g in osm]) if osm else None

    result: list[MmlOnlyRecord] = []
    for record in sorted(records, key=lambda r: (record_number(r.id), r.id)):
        if record.id in matched:
            continue
        geometry = record.geometry
        record_area = float(geometry.area)
        centroid = geometry.centroid
        if not area.contains(centroid.x, centroid.y):
            result.append(MmlOnlyRecord(record, "outside", record_area))
            continue
        if not record_area >= MIN_AREA_M2:
            result.append(MmlOnlyRecord(record, "too_small", record_area))
            continue
        hits = (
            sorted(int(i) for i in tree.query(geometry, predicate="intersects"))
            if tree is not None
            else []
        )
        cut = cut_record(geometry, [osm[i] for i in hits])
        if not cut.kept:
            result.append(
                MmlOnlyRecord(
                    record,
                    "slivers",
                    record_area,
                    pieces_discarded=cut.discarded,
                    cut_by=cut.cut_by,
                )
            )
            continue
        kept = _as_footprint(cut.kept) if cut.cut_by else geometry
        details = {
            "kept": kept,
            "pieces": tuple(cut.kept),
            "pieces_discarded": cut.discarded,
            "cut_by": cut.cut_by,
        }
        kept_centroid = kept.centroid
        if not area.contains(kept_centroid.x, kept_centroid.y):
            # The building would belong to no tile; left out like an OSM footprint.
            result.append(MmlOnlyRecord(record, "outside", record_area, **details))
            continue
        ground = ground_check(kept, index, heightfield) if index is not None else None
        outcome: Outcome = "lidar_contradicts" if ground and ground.contradicts else "added"
        result.append(MmlOnlyRecord(record, outcome, record_area, ground=ground, **details))
    return MmlOnlySelection(source, len(records), len(matched & {r.id for r in records}), result)


def road_overlaps(
    buildings: Iterable[tuple[str, BaseGeometry]], surfaces: Sequence[BaseGeometry]
) -> list[str]:
    """Ids (sorted) of the buildings sharing area (> 0) with any road or path surface."""
    parts = [p for s in surfaces for p in _polygons_of(s)]
    if not parts:
        return []
    tree = shapely.STRtree(parts)
    found = []
    for bid, geometry in buildings:
        hits = tree.query(geometry, predicate="intersects")
        if any(geometry.intersection(parts[int(i)]).area > 0 for i in hits):
            found.append(bid)
    return sorted(found)


def _ordered_counts(values: Iterable[Any], keys: Sequence[Any]) -> dict[str, int]:
    counter = Counter(values)
    result = {str(k): counter.get(k, 0) for k in keys}
    extra = sorted(str(k) for k in counter if k not in keys)
    for k in extra:
        result[k] = sum(v for key, v in counter.items() if str(key) == k)
    return result


def mml_only_report(
    selection: MmlOnlySelection,
    buildings: Sequence[Building],
    overlaps: Sequence[str] = (),
) -> dict[str, Any]:
    """The `mml_only` block of `03-buildings/report.json`.

    `buildings` are the resolved buildings (with their place fields); only the added ones
    are looked at. `overlaps` are the added building ids that overlap a road or path.
    """
    added = selection.added
    by_id = {b.id: b for b in buildings}
    added_buildings = [by_id[r.id] for r in added]
    reasons = Counter(r.outcome for r in selection.records)

    cut_entries = sorted(
        (r for r in added if r.is_cut),
        key=lambda r: (-(r.kept.area if r.kept is not None else 0.0), record_number(r.id), r.id),
    )
    no_lidar = [b for b in added_buildings if b.height_source != "mml_lidar"]
    records_by_id = {r.id: r for r in added}

    def ground_fields(record: MmlOnlyRecord) -> dict[str, Any]:
        if record.ground is None:
            return {"points_inside": None, "share_near_ground": None, "max_above_ground_m": None}
        return record.ground.as_dict()

    return {
        "source": selection.source,
        "limits": {
            "min_area_m2": MIN_AREA_M2,
            "sliver_area_m2": SLIVER_AREA_M2,
            "sliver_opening_radius_m": SLIVER_SHRINK_M,
            "sliver_min_surviving_share": SLIVER_MIN_SURVIVING_SHARE,
            "contradiction_min_points": CONTRADICTION_MIN_POINTS,
            "contradiction_share": CONTRADICTION_SHARE,
            "contradiction_height_m": CONTRADICTION_HEIGHT_M,
        },
        "records_in_area": selection.records_in_area,
        "records_matched": selection.records_matched,
        "records_considered": len(selection.records),
        "added": len(added),
        "added_whole": sum(1 for r in added if not r.is_cut),
        "added_cut": sum(1 for r in added if r.is_cut),
        "not_added": {reason: reasons.get(reason, 0) for reason in NOT_ADDED_REASONS},
        "not_added_ids": {
            reason: [r.id for r in selection.records if r.outcome == reason]
            for reason in NOT_ADDED_REASONS
        },
        "cut": [
            {
                "id": r.id,
                "record_area_m2": _r(r.record_area_m2),
                "kept_area_m2": _r(r.kept.area if r.kept is not None else 0.0),
                "pieces_kept": len(r.pieces),
                "pieces_discarded": r.pieces_discarded,
                "osm_buildings": list(r.cut_by),
                "pieces": [
                    {
                        "area_m2": _r(p.area),
                        "compactness": _r(compactness(p)),
                        "opened_share": _r(opened_share(p)),
                    }
                    for p in r.pieces
                ],
            }
            for r in cut_entries
        ],
        "lidar_contradicts": [
            {
                "id": r.id,
                "area_m2": _r(r.kept.area if r.kept is not None else 0.0),
                **ground_fields(r),
            }
            for r in selection.records
            if r.outcome == "lidar_contradicts"
        ],
        "added_by_feature_class": dict(
            sorted(Counter(str(r.record.feature_class) for r in added).items())
        ),
        "added_by_height_source": _ordered_counts(
            (b.height_source for b in added_buildings), HEIGHT_SOURCES
        ),
        "added_lidar_rejected": _ordered_counts(
            (b.lidar.rejected for b in added_buildings if b.lidar and b.lidar.rejected),
            REJECT_REASONS,
        ),
        "added_by_category": _ordered_counts(
            (b.place.get("category") for b in added_buildings), CATEGORIES
        ),
        "added_by_category_source": {
            ("none" if k == "None" else k): v
            for k, v in _ordered_counts(
                (b.place.get("category_source") for b in added_buildings),
                [*CATEGORY_SOURCES, None],
            ).items()
        },
        "with_lidar_height": len(added_buildings) - len(no_lidar),
        "without_lidar_height": len(no_lidar),
        "without_lidar_height_buildings": [
            {
                "id": b.id,
                "kept_area_m2": _r(b.footprint.area),
                **ground_fields(records_by_id[b.id]),
                "lidar_rejected": b.lidar.rejected if b.lidar is not None else None,
            }
            for b in no_lidar
        ],
        "road_overlaps": sorted(overlaps),
    }
