"""Place information: building categories, names and addresses, and points of interest.

Spec: docs/specs/place-information.md. Runs after the buildings stage: matches buildings
to building-use records (`matching`), links POIs and buildings (`links`), resolves each
building's category, names and address (`resolve`), applies the override file
(`overrides`) and builds `pois.json` (`pois`). The fields it returns per building go into
`buildings.json` (schema version 2, `model`). Output in `08-places/`: `pois.geojson` (map
CRS) and `report.json`; its building statistics go into `03-buildings/report.json`.
"""

from collections import Counter
from collections.abc import Sequence
from dataclasses import dataclass, field
from typing import Any

import shapely
from shapely.geometry import Point
from shapely.geometry.base import BaseGeometry

from mapgen.build.area import BuildArea
from mapgen.build.heightfield import Heightfield
from mapgen.build.names import name_fields
from mapgen.build.pack_manifest import PackCredit
from mapgen.build.places.categories import CATEGORIES, CATEGORY_SOURCES, NAME_SOURCES
from mapgen.build.places.links import LinkPoi, PoiLinks, link_pois
from mapgen.build.places.matching import UseMatch, match_building_use
from mapgen.build.places.model import POIS_SCHEMA_VERSION, BuildingOverrides
from mapgen.build.places.overrides import override_credits, resolve_override
from mapgen.build.places.pois import PlacePoi, place_poi, poi_type
from mapgen.build.places.resolve import (
    resolve_address,
    resolve_category,
    resolve_names,
)
from mapgen.build.stage1_load import LoadResult, LocalFootprint, LocalLandUse

__all__ = ["PlacesResult", "build_places"]


@dataclass
class PlacesResult:
    building_fields: dict[str, dict[str, Any]]  # building id -> its place fields
    pois_document: dict[str, Any]
    building_report: dict[str, Any]  # merged into 03-buildings/report.json
    places_report: dict[str, Any]  # 08-places/report.json
    poi_features: list[tuple[BaseGeometry, dict[str, Any]]] = field(default_factory=list)
    # Manifest credits of the applied third-party overrides (`own work` adds none).
    credits: list[PackCredit] = field(default_factory=list)


def _landuse_covering(
    footprints: Sequence[LocalFootprint], landuse: Sequence[LocalLandUse]
) -> dict[str, list[LocalLandUse]]:
    """The land use areas covering each footprint's centroid, smallest first (then id)."""
    if not landuse:
        return {}
    tree = shapely.STRtree([a.geometry for a in landuse])
    result: dict[str, list[LocalLandUse]] = {}
    for footprint in footprints:
        centroid = footprint.geometry.centroid
        hits = [landuse[int(i)] for i in tree.query(centroid, predicate="intersects")]
        covering = [a for a in hits if a.geometry.covers(centroid)]
        if covering:
            result[footprint.id] = sorted(covering, key=lambda a: (a.geometry.area, a.id))
    return result


def _round3(value: float) -> float:
    return round(float(value), 3) + 0.0


def build_places(
    map_id: str,
    area: BuildArea,
    footprints: Sequence[LocalFootprint],
    loaded: LoadResult,
    heightfield: Heightfield,
    overrides: BuildingOverrides | None = None,
    overrides_file: str | None = None,
) -> PlacesResult:
    """Resolve the place fields of every footprint (the buildings) and the pack's POIs."""
    footprints = sorted(footprints, key=lambda f: f.id)
    by_id = {f.id: f for f in footprints}

    # POIs inside the area, with their positions and clipped outlines.
    kept: list[PlacePoi] = []
    skipped = [
        {"id": s.id, "reason": s.reason}
        for s in loaded.pois_skipped
        if not s.reason.startswith("partial")
    ]
    partial = [
        {"id": s.id, "reason": s.reason}
        for s in loaded.pois_skipped
        if s.reason.startswith("partial")
    ]
    outside = 0
    for poi in sorted(loaded.pois, key=lambda p: p.id):
        placed = place_poi(poi, area)
        if placed is None:
            outside += 1
        elif isinstance(placed, str):
            skipped.append({"id": poi.id, "reason": placed})
        else:
            kept.append(placed)
    pois_by_id = {p.id: p for p in kept}

    matches: dict[str, UseMatch] = (
        match_building_use(footprints, loaded.building_use)
        if loaded.building_use is not None
        else {}
    )
    links: PoiLinks = link_pois(
        footprints, [LinkPoi(p.id, p.geometry, p.position, p.is_building) for p in kept]
    )
    landuse = _landuse_covering(footprints, loaded.landuse)

    fields: dict[str, dict[str, Any]] = {}
    unmapped: dict[str, Counter[str]] = {
        k: Counter()
        for k in ("osm_building", "osm_poi", "osm_poi_area", "mml_feature_class", "osm_landuse")
    }
    poi_conflicts: list[dict[str, Any]] = []
    area_conflicts: list[dict[str, Any]] = []
    override_hits: dict[str, dict[str, Any]] = {}
    building_addresses: dict[str, tuple[dict[str, str | None], str | None]] = {}
    for footprint in footprints:
        bid = footprint.id
        match = matches.get(bid)
        use_id = match.record.id if match is not None else None
        override = resolve_override(overrides, bid, use_id)
        poi_ids = links.pois_of_building.get(bid, [])
        area_ids = links.areas_of_building.get(bid, [])
        inside = [pois_by_id[p] for p in poi_ids if p != bid]
        result = resolve_category(
            override=override.category,
            building_tags=footprint.tags,
            inside_poi_tags=[p.poi.tags for p in inside],
            area_poi_tags=[pois_by_id[a].poi.tags for a in area_ids],
            use=match,
            footprint_area_m2=footprint.geometry.area,
            landuse=[a.tags.get("landuse", "") for a in landuse.get(bid, [])],
        )
        for step, values in result.unmapped.items():
            unmapped[step].update(values)
        if result.poi_conflict:
            poi_conflicts.append(
                {"id": bid, "categories": list(result.poi_conflict), "pois": poi_ids}
            )
        if result.poi_area_conflict:
            area_conflicts.append(
                {"id": bid, "categories": list(result.poi_area_conflict), "pois": area_ids}
            )
        names = resolve_names(
            override=override.names,
            building_tags=footprint.tags,
            inside_poi_names=[name_fields(p.poi.tags) for p in inside],
        )
        address, address_source = resolve_address(footprint.tags)
        building_addresses[bid] = (address, address_source)
        set_by: dict[str, set[str]] = {}
        for key, used in (
            (override.category_key, "category" if result.source == "override" else None),
            (override.names_key, "name" if names.source == "override" else None),
        ):
            if key is not None and used is not None:
                set_by.setdefault(key, set()).add(used)
                hit = override_hits.setdefault(key, {"buildings": set(), "fields": set()})
                hit["buildings"].add(bid)
                hit["fields"].add(used)
        applied_here = []
        for key in sorted(set_by):
            assert overrides is not None
            entry = overrides.buildings[key]
            applied_here.append(
                {
                    "id": key,
                    "source": entry.source,
                    "licence": entry.licence,
                    "fields": sorted(set_by[key]),
                }
            )
        fields[bid] = {
            "category": result.category,
            "category_source": result.source,
            "category_raw": result.raw,
            **names.names,
            "name_source": names.source,
            "address": address,
            "address_source": address_source,
            "poi_ids": list(poi_ids),
            "area_poi_ids": list(area_ids),
            "mml_building": use_id,
            "mml_overlap": match.overlap if match is not None else None,
            "overrides": applied_here,
        }

    # pois.json
    entries: list[dict[str, Any]] = []
    features: list[tuple[BaseGeometry, dict[str, Any]]] = []
    for placed in kept:
        tags = placed.poi.tags
        kind, subtype, other = poi_type(tags)
        address, address_source = resolve_address(tags)
        building_id = links.building_of_poi.get(placed.id)
        if address_source is None and building_id is not None:
            building_address, building_source = building_addresses[building_id]
            if building_source is not None:
                address, address_source = dict(building_address), "building"
        x, y = placed.position.x, placed.position.y
        height = float(heightfield.sample(x, y))
        entry = {
            "id": placed.id,
            **name_fields(tags),
            "type": kind,
            "subtype": subtype,
            "other_tags": other,
            "geometry": "point" if placed.outline is None else "area",
            "position": [_round3(x), _round3(height), _round3(-y)],
            "outline": placed.outline,
            "address": address,
            "address_source": address_source,
            "building_id": building_id,
            "contains_building_ids": list(links.buildings_of_area.get(placed.id, []))
            if placed.outline is not None
            else [],
        }
        entries.append(entry)
        geometry = placed.clipped if placed.clipped is not None else Point(x, y)
        features.append((geometry, {k: v for k, v in entry.items() if k != "outline"}))
    pois_document = {
        "schema_version": POIS_SCHEMA_VERSION,
        "map_id": map_id,
        "area": area.name,
        "pois": entries,
    }

    building_report = _building_report(
        footprints=footprints,
        fields=fields,
        matches=matches,
        loaded=loaded,
        unmapped=unmapped,
        poi_conflicts=poi_conflicts,
        area_conflicts=area_conflicts,
        several=links.several_footprints,
        overrides=overrides,
        overrides_file=overrides_file,
        override_hits=override_hits,
        by_id=by_id,
    )
    places_report = _places_report(
        entries=entries,
        skipped=skipped,
        partial=partial,
        outside=outside,
        loaded=loaded,
        landuse=landuse,
    )
    credits = override_credits(overrides, set(override_hits))
    return PlacesResult(fields, pois_document, building_report, places_report, features, credits)


def _counts(values: Sequence[Any], keys: Sequence[Any]) -> dict[str, int]:
    counter = Counter(values)
    return {str(k): counter.get(k, 0) for k in keys}


def _building_report(
    *,
    footprints: Sequence[LocalFootprint],
    fields: dict[str, dict[str, Any]],
    matches: dict[str, UseMatch],
    loaded: LoadResult,
    unmapped: dict[str, Counter[str]],
    poi_conflicts: list[dict[str, Any]],
    area_conflicts: list[dict[str, Any]],
    several: list[dict[str, Any]],
    overrides: BuildingOverrides | None,
    overrides_file: str | None,
    override_hits: dict[str, dict[str, Any]],
    by_id: dict[str, LocalFootprint],
) -> dict[str, Any]:
    entries = list(fields.values())
    source_counts = _counts([e["category_source"] for e in entries], [*CATEGORY_SOURCES, None])
    source_counts["none"] = source_counts.pop("None")
    name_counts = _counts([e["name_source"] for e in entries], [*NAME_SOURCES, None])
    name_counts["none"] = name_counts.pop("None")

    records = loaded.building_use
    use_report: dict[str, Any] = {"source": loaded.building_use_source}
    small: dict[str, Any] = {}
    fallback: dict[str, Any] = {}
    if records is not None:
        matched_ids = Counter(m.record.id for m in matches.values())
        multi = sorted(i for i, n in matched_ids.items() if n > 1)
        use_report.update(
            {
                "records_in_area": len(records),
                "records_skipped": len(loaded.building_use_skipped),
                "matched_buildings": len(matches),
                "unmatched_buildings": len(footprints) - len(matches),
                "unmatched_records": sum(1 for r in records if r.id not in matched_ids),
                "records_matched_more_than_once": len(multi),
                "records_matched_more_than_once_ids": multi,
                "matched_by_feature_class": dict(
                    sorted(Counter(str(m.record.feature_class) for m in matches.values()).items())
                ),
            }
        )
        under: list[str | None] = []
        over: list[str | None] = []
        threshold: float | None = None
        decided: list[str | None] = []
        for bid, match in sorted(matches.items()):
            rule = match.record.rule
            source = fields[bid]["category_source"] or "none"
            if rule.small_category is not None and rule.small_area_m2 is not None:
                threshold = rule.small_area_m2
                (under if by_id[bid].geometry.area < rule.small_area_m2 else over).append(source)
            if rule.fallback_category is not None:
                decided.append("fallback" if source == "mml_use_class" else source)
        small = {
            "threshold_m2": threshold,
            "under": len(under),
            "at_or_over": len(over),
            "under_by_category_source": dict(sorted(Counter(map(str, under)).items())),
            "at_or_over_by_category_source": dict(sorted(Counter(map(str, over)).items())),
        }
        fallback = {
            "buildings": len(decided),
            "by_category_source": dict(sorted(Counter(map(str, decided)).items())),
        }
    applied = []
    unmatched_overrides = []
    if overrides is not None:
        for key, entry in sorted(overrides.buildings.items()):
            hit = override_hits.get(key)
            if hit is None:
                reached = key in by_id or any(m.record.id == key for m in matches.values())
                if not reached:
                    unmatched_overrides.append(
                        {
                            "id": key,
                            "source": entry.source,
                            "licence": entry.licence,
                            "warning": "matches no building in the build area",
                        }
                    )
                    continue
                hit = {"buildings": set(), "fields": set()}
            applied.append(
                {
                    "id": key,
                    "buildings": sorted(hit["buildings"]),
                    "fields": sorted(hit["fields"]),
                    "source": entry.source,
                    "licence": entry.licence,
                }
            )
    return {
        "category_counts": _counts([e["category"] for e in entries], CATEGORIES),
        "category_source_counts": source_counts,
        "name_source_counts": name_counts,
        "address_source_counts": {
            "osm": sum(1 for e in entries if e["address_source"] == "osm"),
            "none": sum(1 for e in entries if e["address_source"] is None),
        },
        "building_use": use_report,
        "use_class_small_rule": small,
        "use_class_fallback_rule": fallback,
        "unmapped_values": {
            step: dict(sorted(counter.items())) for step, counter in unmapped.items()
        },
        "poi_category_conflicts": poi_conflicts,
        "poi_area_category_conflicts": area_conflicts,
        "pois_in_several_footprints": several,
        "overrides": {
            "file": overrides_file,
            "applied": applied,
            "unmatched": unmatched_overrides,
        },
        "buildings_with_pois": sum(1 for e in entries if e["poi_ids"]),
        "buildings_in_poi_areas": sum(1 for e in entries if e["area_poi_ids"]),
    }


def _places_report(
    *,
    entries: list[dict[str, Any]],
    skipped: list[dict[str, Any]],
    partial: list[dict[str, Any]],
    outside: int,
    loaded: LoadResult,
    landuse: dict[str, list[LocalLandUse]],
) -> dict[str, Any]:
    by_type: dict[str, dict[str, int]] = {}
    for entry in entries:
        named = any(entry[k] is not None for k in ("name", "name_sv", "ref"))
        counts = by_type.setdefault(entry["type"], {"named": 0, "unnamed": 0})
        counts["named" if named else "unnamed"] += 1
    linked = sum(1 for e in entries if e["building_id"] is not None)
    used: Counter[str] = Counter()
    for areas in landuse.values():
        for area in areas:
            used[area.id] += 1
    return {
        "pois": {
            "total": len(entries),
            "points": sum(1 for e in entries if e["geometry"] == "point"),
            "areas": sum(1 for e in entries if e["geometry"] == "area"),
            "named": sum(1 for e in entries if e["name"] is not None),
            "unnamed": sum(1 for e in entries if e["name"] is None),
            "linked": linked,
            "unlinked": len(entries) - linked,
            "by_type": dict(sorted(by_type.items())),
            "by_type_and_subtype": dict(
                sorted(Counter(f"{e['type']}={e['subtype']}" for e in entries).items())
            ),
            "outside_area": outside,
        },
        "skipped": sorted(skipped, key=lambda s: s["id"]),
        "partial": sorted(partial, key=lambda s: s["id"]),
        "landuse": {
            "in_area": len(loaded.landuse),
            "by_value": dict(
                sorted(Counter(a.tags.get("landuse", "") for a in loaded.landuse).items())
            ),
            "used": [
                {"id": a.id, "landuse": a.tags.get("landuse"), "buildings_covered": used[a.id]}
                for a in sorted(loaded.landuse, key=lambda a: a.id)
                if used[a.id]
            ],
            "left_out": [
                {"id": s.id, "reason": s.reason}
                for s in sorted(loaded.landuse_skipped, key=lambda s: s.id)
            ],
        },
    }
