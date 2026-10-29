"""Building categories and the OSM tables that map tags to them.

The tables are OSM's, not map-specific, so they live in the pipeline, as the road class
mapping does (ADR 0012). Building-use codes (e.g. MML feature classes) are mapped inside
their adapter instead (ADR 0002), which hands the build a `UseClassRule` per record.
Spec: docs/specs/place-information.md, "Building category".
"""

from collections.abc import Mapping
from typing import Literal

from mapgen.sources.osm import POI_KEYS

Category = Literal[
    "residential",
    "commercial",
    "retail",
    "industrial",
    "public",
    "religious",
    "education",
    "agricultural",
    "garage_or_shed",
    "other",
    "unknown",
]
CATEGORIES: tuple[Category, ...] = (
    "residential",
    "commercial",
    "retail",
    "industrial",
    "public",
    "religious",
    "education",
    "agricultural",
    "garage_or_shed",
    "other",
    "unknown",
)
# The precedence steps, in order. `null` (no step had a value) means `unknown`.
CategorySource = Literal[
    "override", "osm_building", "osm_poi", "osm_poi_area", "mml_use_class", "osm_landuse"
]
CATEGORY_SOURCES: tuple[CategorySource, ...] = (
    "override",
    "osm_building",
    "osm_poi",
    "osm_poi_area",
    "mml_use_class",
    "osm_landuse",
)
NameSource = Literal["override", "osm", "osm_poi"]
NAME_SOURCES: tuple[NameSource, ...] = ("override", "osm", "osm_poi")

# `building` values that say nothing about the use (step 2 skips them).
GENERIC_BUILDING_VALUES = frozenset({"yes", "roof"})


def _table(rows: Mapping[Category, tuple[str, ...]]) -> dict[str, Category]:
    return {value: category for category, values in rows.items() for value in values}


# Step 2: `building` values.
BUILDING_VALUES: dict[str, Category] = _table(
    {
        "residential": (
            "residential",
            "house",
            "detached",
            "apartments",
            "semidetached_house",
            "terrace",
            "bungalow",
            "dormitory",
            "cabin",
        ),
        "commercial": ("commercial", "office", "hotel"),
        "retail": ("retail", "supermarket", "kiosk"),
        "industrial": ("industrial", "warehouse", "factory", "manufacture"),
        "public": (
            "public",
            "civic",
            "government",
            "fire_station",
            "hospital",
            "train_station",
            "transportation",
        ),
        "religious": (
            "church",
            "chapel",
            "cathedral",
            "religious",
            "mosque",
            "temple",
            "synagogue",
        ),
        "education": ("school", "kindergarten", "university", "college"),
        "agricultural": (
            "farm",
            "farm_auxiliary",
            "barn",
            "cowshed",
            "stable",
            "greenhouse",
            "sty",
        ),
        "garage_or_shed": ("garage", "garages", "carport", "shed"),
    }
)

# Steps 3 and 4: POI tags. Keys whose every value maps to one category, the exceptions,
# and the listed `key=value` pairs. A `None` category means "no value" (ignored).
POI_ANY_VALUE: dict[str, Category] = {"shop": "retail", "office": "commercial"}
POI_VALUES: dict[tuple[str, str], Category | None] = {
    ("office", "government"): "public",
    ("amenity", "marketplace"): "retail",
    **{
        ("amenity", v): "commercial"
        for v in (
            "restaurant",
            "cafe",
            "fast_food",
            "bar",
            "pub",
            "bank",
            "pharmacy",
            "fuel",
            "cinema",
        )
    },
    ("leisure", "fitness_centre"): "commercial",
    **{("tourism", v): "commercial" for v in ("hotel", "motel", "hostel", "guest_house")},
    **{
        ("amenity", v): "public"
        for v in (
            "townhall",
            "library",
            "fire_station",
            "police",
            "post_office",
            "hospital",
            "clinic",
            "community_centre",
            "courthouse",
        )
    },
    **{
        ("leisure", v): "public"
        for v in ("sports_centre", "stadium", "sports_hall", "swimming_pool", "ice_rink")
    },
    **{("tourism", v): "public" for v in ("museum", "gallery", "information")},
    **{("amenity", v): "religious" for v in ("place_of_worship", "grave_yard")},
    **{("amenity", v): "education" for v in ("school", "kindergarten", "college", "university")},
    ("amenity", "parking"): None,
    **{("leisure", v): None for v in ("park", "playground", "pitch", "track", "garden")},
    **{("tourism", v): None for v in ("artwork", "viewpoint", "picnic_site")},
}

# Step 6: `landuse` values; other values give no value.
LANDUSE_VALUES: dict[str, Category] = {
    "residential": "residential",
    "commercial": "commercial",
    "retail": "retail",
    "industrial": "industrial",
    "farmland": "agricultural",
    "farmyard": "agricultural",
    "religious": "religious",
    "cemetery": "religious",
    "education": "education",
}

PoiTagStatus = Literal["mapped", "no_value", "unmapped"]


def building_value_category(value: str | None) -> tuple[Category | None, bool]:
    """(category, unmapped) for a `building` value in step 2.

    `yes`, `roof` and a missing tag give no value; a value not in the table gives `other`
    and is flagged as unmapped.
    """
    if value is None or value in GENERIC_BUILDING_VALUES:
        return None, False
    category = BUILDING_VALUES.get(value)
    return (category, False) if category is not None else ("other", True)


def poi_tag_category(key: str, value: str) -> tuple[Category | None, PoiTagStatus]:
    """The category of one POI tag, and whether the table maps it, ignores it or lacks it."""
    if (key, value) in POI_VALUES:
        category = POI_VALUES[(key, value)]
        return category, "mapped" if category is not None else "no_value"
    if key in POI_ANY_VALUE:
        return POI_ANY_VALUE[key], "mapped"
    return None, "unmapped"


def poi_tags(tags: Mapping[str, str]) -> list[str]:
    """The feature's POI tags as `key=value` strings, in `POI_KEYS` order."""
    return [f"{key}={tags[key]}" for key in POI_KEYS if key in tags]


def feature_poi_category(
    tags: Mapping[str, str], unmapped_as_other: bool
) -> tuple[Category | None, list[str]]:
    """One feature's category from its POI tags, and its unmapped `key=value` strings.

    The keys are tried in `POI_KEYS` order (`shop`, `amenity`, `office`, `tourism`,
    `leisure`); the first tag with a value wins. Tags mapped to "no value" are ignored. An
    unmapped tag gives `other` in step 3 (`unmapped_as_other`) and nothing in step 4.
    """
    found: Category | None = None
    unmapped: list[str] = []
    for key in POI_KEYS:
        if key not in tags:
            continue
        category, status = poi_tag_category(key, tags[key])
        if status == "unmapped":
            unmapped.append(f"{key}={tags[key]}")
            if unmapped_as_other and found is None:
                found = "other"
        elif category is not None and found is None:
            found = category
    return found, unmapped


def landuse_category(value: str | None) -> Category | None:
    return LANDUSE_VALUES.get(value) if value is not None else None
