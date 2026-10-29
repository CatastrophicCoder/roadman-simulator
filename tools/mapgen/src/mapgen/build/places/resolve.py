"""Building category, name and address resolution (docs/specs/place-information.md).

Category: the first step with a value wins, and `category_source` names it:

1. `override`: the override file.
2. `osm_building`: the `building` tag, unless `yes` or `roof`; unmapped values: `other`.
3. `osm_poi`: POI tags on the building (first), else on the POIs on or in it; POIs that
   map to different categories give no value (reported). Unmapped values: `other`.
4. `osm_poi_area`: the POI areas the building is inside, when they agree (else reported).
5. `mml_use_class`: the matched building-use record's rule (see `UseClassRule`).
6. `osm_landuse`: the smallest land use area covering the centroid that maps.
7. The record's fallback category (`category_source` `mml_use_class`).
8. `unknown`, `category_source` null.

Every source's raw value is kept in `category_raw`, whichever step wins.
"""

from collections.abc import Mapping, Sequence
from dataclasses import dataclass, field
from typing import Any

from mapgen.build.names import address_fields, has_any, name_fields
from mapgen.build.places.categories import (
    Category,
    CategorySource,
    NameSource,
    building_value_category,
    feature_poi_category,
    landuse_category,
    poi_tags,
)
from mapgen.build.places.matching import UseMatch


@dataclass(frozen=True)
class CategoryResult:
    category: Category
    source: CategorySource | None
    raw: dict[str, Any]
    # Unmapped raw values per step ("osm_building", "osm_poi", ...), for the report.
    unmapped: dict[str, list[str]] = field(default_factory=dict)
    poi_conflict: tuple[Category, ...] = ()  # step 3: the POIs on or in it disagree
    poi_area_conflict: tuple[Category, ...] = ()  # step 4: the POI areas disagree


def _agreeing(categories: Sequence[Category | None]) -> tuple[Category | None, tuple[str, ...]]:
    """(the one category, ()) when the values agree; (None, the sorted values) if not."""
    distinct = sorted({c for c in categories if c is not None})
    if len(distinct) == 1:
        return distinct[0], ()  # type: ignore[return-value]
    return None, tuple(distinct)


def resolve_category(
    *,
    override: Category | None,
    building_tags: Mapping[str, str],
    inside_poi_tags: Sequence[Mapping[str, str]],
    area_poi_tags: Sequence[Mapping[str, str]],
    use: UseMatch | None,
    footprint_area_m2: float,
    landuse: Sequence[str],
) -> CategoryResult:
    """The category of one building.

    `inside_poi_tags` are the tags of the POIs on or in it other than itself;
    `area_poi_tags` those of the POI areas it is inside; `landuse` the `landuse` values of
    the areas covering its centroid, smallest area first.
    """
    unmapped: dict[str, list[str]] = {}
    found: list[tuple[Category, CategorySource]] = []

    if override is not None:
        found.append((override, "override"))

    building_value = building_tags.get("building")
    category, is_unmapped = building_value_category(building_value)
    if is_unmapped and building_value is not None:
        unmapped["osm_building"] = [building_value]
    if category is not None:
        found.append((category, "osm_building"))

    own, own_unmapped = feature_poi_category(building_tags, unmapped_as_other=True)
    poi_unmapped = list(own_unmapped)
    considered = set(poi_tags(building_tags))
    inside: list[Category | None] = []
    for tags in inside_poi_tags:
        value, bad = feature_poi_category(tags, unmapped_as_other=True)
        inside.append(value)
        poi_unmapped += bad
        considered |= set(poi_tags(tags))
    poi_conflict: tuple[str, ...] = ()
    if own is not None:
        found.append((own, "osm_poi"))
    else:
        agreed, poi_conflict = _agreeing(inside)
        if agreed is not None:
            found.append((agreed, "osm_poi"))
    if poi_unmapped:
        unmapped["osm_poi"] = sorted(set(poi_unmapped))

    area_values: list[Category | None] = []
    area_unmapped: list[str] = []
    area_considered: set[str] = set()
    for tags in area_poi_tags:
        value, bad = feature_poi_category(tags, unmapped_as_other=False)
        area_values.append(value)
        area_unmapped += bad
        area_considered |= set(poi_tags(tags))
    area_agreed, area_conflict = _agreeing(area_values)
    if area_agreed is not None:
        found.append((area_agreed, "osm_poi_area"))
    if area_unmapped:
        unmapped["osm_poi_area"] = sorted(set(area_unmapped))

    rule = use.record.rule if use is not None else None
    if use is not None and rule is not None:
        if not rule.mapped:
            code = use.record.feature_class
            unmapped["mml_feature_class"] = [str(code) if code is not None else "null"]
        if rule.category is not None:
            found.append((rule.category, "mml_use_class"))  # type: ignore[arg-type]
        elif (
            rule.small_category is not None
            and rule.small_area_m2 is not None
            and footprint_area_m2 < rule.small_area_m2
        ):
            found.append((rule.small_category, "mml_use_class"))  # type: ignore[arg-type]

    landuse_raw: str | None = None
    for value in landuse:
        mapped = landuse_category(value)
        if mapped is not None:
            landuse_raw = value
            found.append((mapped, "osm_landuse"))
            break
    else:
        if landuse:
            landuse_raw = landuse[0]
    bad_landuse = [v for v in landuse if landuse_category(v) is None]
    if bad_landuse:
        unmapped["osm_landuse"] = sorted(set(bad_landuse))

    if rule is not None and rule.fallback_category is not None:
        found.append((rule.fallback_category, "mml_use_class"))  # type: ignore[arg-type]

    raw = {
        "osm_building": building_value,
        "osm_poi": sorted(considered),
        "osm_poi_area": sorted(area_considered),
        "mml_feature_class": use.record.feature_class if use is not None else None,
        "mml_use_class": use.record.use_class if use is not None else None,
        "osm_landuse": landuse_raw,
        "override": override,
    }
    final, source = found[0] if found else ("unknown", None)
    return CategoryResult(
        category=final,
        source=source,
        raw=raw,
        unmapped=unmapped,
        poi_conflict=poi_conflict,  # type: ignore[arg-type]
        poi_area_conflict=area_conflict,  # type: ignore[arg-type]
    )


@dataclass(frozen=True)
class NamesResult:
    names: dict[str, str | None]
    source: NameSource | None


def resolve_names(
    *,
    override: Mapping[str, str | None] | None,
    building_tags: Mapping[str, str],
    inside_poi_names: Sequence[Mapping[str, str | None]],
) -> NamesResult:
    """The building's name fields: override, its own tags, or its one named POI.

    `inside_poi_names` are the name fields of the POIs on or in it (POI areas it is
    inside don't count). With two or more named POIs the names stay null.
    """
    if override is not None:
        return NamesResult(dict(override), "override")
    own = name_fields(building_tags)
    if has_any(own):
        return NamesResult(own, "osm")
    named = [n for n in inside_poi_names if has_any(n)]
    if len(named) == 1:
        return NamesResult(dict(named[0]), "osm_poi")
    return NamesResult({"name": None, "name_sv": None, "ref": None}, None)


def resolve_address(tags: Mapping[str, str]) -> tuple[dict[str, str | None], str | None]:
    """(address, address_source): the feature's own `addr:*` tags, `osm` when any is set."""
    address = address_fields(tags)
    return address, "osm" if has_any(address) else None
