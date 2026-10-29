"""Place information on hand-written snippets, built end to end (OSM + GPKG -> pack).

Criterion numbers (`acN`) refer to docs/specs/place-information.md. One synthetic map on
the sloped plane DEM, with a hand-written OSM file (buildings, POI nodes and areas, land
use), a synthetic `rakennus` GeoPackage written with sqlite3 and an override file. Every
case sits at its own place in the test area (300 m square around the origin):

- y = 120: building tags and POIs (steps 1-3); y = 95: MML feature classes (step 5);
- y = 60: land use (step 6); y = 15..45: POI areas (step 4, outlines);
- y = -20: names, addresses, POI tags on a building way, MML overlaps;
- y = -60: overrides by OSM and MML key.

Expected values come from the snippet layout and the spec. Nothing touches the network.
"""

import copy
import json
import shutil
import unicodedata
from dataclasses import dataclass
from pathlib import Path
from typing import Any

import jsonschema
import pytest
import shapely
import yaml
from shapely.geometry import Polygon, box

from conftest import (
    FIXED_NOW,
    FIXTURE_ORIGIN,
    FIXTURE_YAML,
    FixtureBuild,
    TmpRepo,
    plane_z,
    write_fixture_raw,
    write_rakennus_gpkg,
)
from mapgen import paths
from mapgen.build import BuildError, BuildReport, run_build
from mapgen.build.places.model import BUILDINGS_SCHEMA, OWN_WORK, POIS_SCHEMA
from mapgen.cli import EXIT_FAILED, EXIT_OK, main
from mapgen.config import MapConfig
from pack_checks import check_pack
from test_places_units import INVALID_OVERRIDES
from test_roads_snippets import Osm

MAP_ID = "placeville"
TOPO_FILE = "mml/topographic/rakennus.gpkg"
TOPO_ATTRIBUTION = "Contains data from the National Land Survey of Finland Topographic Database"

OWN = {"source": "playtest 2026-11-02", "licence": OWN_WORK}
STREET_IMAGES = {
    "source": "Street-level images 2026-11",
    "licence": "CC BY-SA 4.0",
    "attribution": "Example contributor, example imagery service",
    "retrieved_at": "2026-11-02",
}
LOST = {  # reaches a building, but every field it sets loses to the OSM key: no credit
    "source": "Other images",
    "licence": "CC BY 4.0",
    "attribution": "Other contributor",
    "retrieved_at": "2026-11-03",
}
UNMATCHED = {
    "source": "Unmatched source",
    "licence": "CC BY 4.0",
    "attribution": "Unmatched contributor",
    "retrieved_at": "2026-11-04",
}
OVERRIDES: dict[str, dict[str, Any]] = {
    "osm:way/1001": {"category": "industrial", **OWN},
    "mml:rakennus/1030": {"category": "public", **OWN},
    "mml:rakennus/1031": {"category": "industrial", "name": "Mml nimi", **STREET_IMAGES},
    "osm:way/1031": {"category": "education", **OWN},
    "mml:rakennus/1032": {"category": "agricultural", **OWN},
    "osm:way/1032": {"name": "Osm nimi", **STREET_IMAGES},
    "mml:rakennus/1033": {"category": "retail", **LOST},
    "osm:way/1033": {"category": "garage_or_shed", **OWN},
    "mml:rakennus/1034": {"name": "Pari", **OWN},
    "osm:way/999999": {"category": "retail", **UNMATCHED},
    "mml:rakennus/99999": {"category": "retail", **OWN},
}

YES = {"building": "yes"}
NFD_HARKALA = unicodedata.normalize("NFD", "Härkälä")
NFD_STREET = unicodedata.normalize("NFD", "Härkätie")


@dataclass(frozen=True)
class Case:
    """A building: centre, size, tags, its MML record (feature class, use class) or None."""

    x: float
    y: float
    tags: dict[str, str]
    mml: tuple[int, int] | None = None
    size: tuple[float, float] = (10.0, 10.0)
    mml_box: tuple[float, float, float, float] | None = None  # local, else footprint + 0.5 m


BUILDINGS: dict[int, Case] = {
    # y = 120: override, building values, POIs on and in the building.
    1001: Case(-120, 120, {"building": "house"}),
    1002: Case(-95, 120, {"building": "school"}),
    1003: Case(-70, 120, {"building": "roof", "amenity": "fuel"}),
    1004: Case(-45, 120, {"building": "yes", "amenity": "fuel"}),
    1005: Case(-20, 120, {**YES, "addr:street": "Rakennustie", "addr:housenumber": "1"}),
    1006: Case(5, 120, YES, (42211, 1)),
    1009: Case(30, 120, {"building": "spaceport"}),
    1021: Case(55, 120, {"building": "yes", "amenity": "foo_bar"}),
    # y = 95: MML feature classes.
    1010: Case(-120, 95, YES, (42211, 1)),
    1011: Case(-95, 95, YES, (42231, 3)),
    1012: Case(-70, 95, YES, (42251, 5)),
    1013: Case(-45, 95, YES, (42270, 8)),
    1014: Case(-20, 95, YES, (42261, 6), size=(6.5, 6.0)),  # 39 m²
    1015: Case(5, 95, YES, (42261, 6), size=(8.2, 5.0)),  # 41 m²
    1017: Case(30, 95, YES, (42221, 2)),
    1020: Case(55, 95, YES, (42241, 4)),
    1022: Case(80, 95, YES, (42290, 9)),  # not in the table
    1019: Case(105, 95, YES),
    # y = 60: land use.
    1016: Case(-120, 60, YES, (42221, 2)),
    1018: Case(-95, 60, YES),
    1023: Case(-70, 60, YES),
    1024: Case(-45, 60, YES),
    1025: Case(-20, 60, YES),
    1026: Case(5, 60, YES),
    # y = 15..45: POI areas.
    1007: Case(-122, 22, YES),
    1008: Case(-72, 22, YES),
    1027: Case(-22, 22, YES),
    1028: Case(32, 22, YES),
    1050: Case(85, 30, YES),  # in the parking area's hole
    # y = -20: names, addresses, POI tags on the way, overlaps.
    1042: Case(-120, -20, {**YES, "addr:housenumber": "7"}),
    1043: Case(
        -95,
        -20,
        {
            **YES,
            "name": "  Kauppa  ",
            "name:sv": NFD_HARKALA,
            "addr:street": f" {NFD_STREET} ",
            "addr:housenumber": "12a",
        },
    ),
    1044: Case(-70, -20, {**YES, "name": "", "ref": "A;B"}),
    1029: Case(-45, -20, {"building": "yes", "amenity": "pharmacy"}),
    1040: Case(
        -20,
        -20,
        {**YES, "name": "Oma nimi", "addr:street": "Testikatu", "addr:housenumber": "12a"},
    ),
    1041: Case(5, -20, {**YES, "addr:street": "Testikatu", "addr:housenumber": "3-5"}),
    1036: Case(60, -20, YES, (42211, 1), mml_box=(59, -27, 75, -13)),  # 60 % of it
    1037: Case(90, -20, YES, (42211, 1), mml_box=(91, -27, 105, -13)),  # 40 % of it
    # y = -60: overrides.
    1030: Case(-120, -60, YES, (42211, 1)),
    1031: Case(-95, -60, YES, (42211, 1)),
    1032: Case(-70, -60, {**YES, "name:sv": "Svenska", "ref": "R1"}, (42211, 1)),
    1033: Case(-45, -60, YES, (42211, 1)),
    1034: Case(-20, -60, YES, (42261, 6), mml_box=(-25.5, -65.5, -4.5, -54.5)),
    1035: Case(-10, -60, YES),  # on MML 1034 too
}

# POI nodes: (x, y, tags).
POI_NODES: dict[str, tuple[float, float, dict[str, str]]] = {
    "bakery": (
        -18,
        122,
        {"shop": "bakery", "name": "Leipomo", "name:sv": "Bageri", "addr:street": "Kauppatie"},
    ),
    "cafe": (3, 118, {"amenity": "cafe", "name": "Kahvila"}),
    "library": (7, 122, {"amenity": "library", "name": "Kirjasto"}),
    "kiosk": (-43, -18, {"shop": "kiosk"}),
    "named_cafe": (-18, -18, {"amenity": "cafe", "name": "Poi nimi"}),
    "museum": (30, -20, {"tourism": "museum", "leisure": "park", "name": "Museo"}),
    "outside": (200, 0, {"shop": "gift"}),
}
# Areas (closed ways): way id -> (min_x, min_y, max_x, max_y, tags).
LANDUSE = {
    3001: (-130, 50, -110, 70, {"landuse": "retail"}),
    3002: (-105, 50, -85, 70, {"landuse": "industrial"}),
    3003: (-80, 50, -60, 70, {"landuse": "quarry"}),
    3004: (-55, 48, 15, 72, {"landuse": "residential"}),
    3005: (-52, 52, -38, 68, {"landuse": "commercial"}),
    3006: (-2, 52, 12, 68, {"landuse": "quarry"}),
}
POI_AREAS = {
    4001: (-130, 15, -90, 45, {"amenity": "school", "name": "Koulu"}),
    4002: (-30, 15, 0, 45, {"amenity": "place_of_worship"}),
    4003: (-28, 17, -2, 43, {"leisure": "sports_centre"}),
    4004: (25, 15, 55, 45, {"amenity": "weird_area"}),
    4005: (120, 15, 170, 45, {"leisure": "pitch"}),  # crosses the area edge at x = 150
}
PARK = 5002  # two outer rings
PARK_OUTERS = [(-80, 15, -60, 45), (-55, 15, -40, 45)]
PARKING = 5001  # outer ring with a hole
PARKING_OUTER, PARKING_INNER = (65, 10, 105, 50), (75, 20, 95, 40)
OUTSIDE_RECORD = 9000  # an MML building far outside the area

RAW_DEFAULT: dict[str, Any] = {
    "osm_building": "yes",
    "osm_poi": [],
    "osm_poi_area": [],
    "mml_feature_class": None,
    "mml_use_class": None,
    "osm_landuse": None,
    "override": None,
}


def _raw(way: int, **values: Any) -> dict[str, Any]:
    case = BUILDINGS[way]
    raw = {**RAW_DEFAULT, "osm_building": case.tags.get("building")}
    if case.mml is not None and way != 1037:
        raw["mml_feature_class"], raw["mml_use_class"] = case.mml
    return {**raw, **values}


# AC3: way -> (category, category_source, category_raw).
EXPECTED_CATEGORY: dict[int, tuple[str, str | None, dict[str, Any]]] = {
    1001: ("industrial", "override", _raw(1001, override="industrial")),
    1002: ("education", "osm_building", _raw(1002)),
    1003: ("commercial", "osm_poi", _raw(1003, osm_poi=["amenity=fuel"])),
    1004: ("commercial", "osm_poi", _raw(1004, osm_poi=["amenity=fuel"])),
    1005: ("retail", "osm_poi", _raw(1005, osm_poi=["shop=bakery"])),
    1006: ("residential", "mml_use_class", _raw(1006, osm_poi=["amenity=cafe", "amenity=library"])),
    1009: ("other", "osm_building", _raw(1009)),
    1021: ("other", "osm_poi", _raw(1021, osm_poi=["amenity=foo_bar"])),
    1010: ("residential", "mml_use_class", _raw(1010)),
    1011: ("residential", "mml_use_class", _raw(1011)),
    1012: ("religious", "mml_use_class", _raw(1012)),
    1013: ("religious", "mml_use_class", _raw(1013)),
    1014: ("garage_or_shed", "mml_use_class", _raw(1014)),
    1015: ("unknown", None, _raw(1015)),
    1017: ("commercial", "mml_use_class", _raw(1017)),
    1020: ("industrial", "mml_use_class", _raw(1020)),
    1022: ("unknown", None, _raw(1022)),
    1019: ("unknown", None, _raw(1019)),
    1016: ("retail", "osm_landuse", _raw(1016, osm_landuse="retail")),
    1018: ("industrial", "osm_landuse", _raw(1018, osm_landuse="industrial")),
    1023: ("unknown", None, _raw(1023, osm_landuse="quarry")),
    1024: ("commercial", "osm_landuse", _raw(1024, osm_landuse="commercial")),
    1025: ("residential", "osm_landuse", _raw(1025, osm_landuse="residential")),
    1026: ("residential", "osm_landuse", _raw(1026, osm_landuse="residential")),
    1007: ("education", "osm_poi_area", _raw(1007, osm_poi_area=["amenity=school"])),
    1008: ("unknown", None, _raw(1008, osm_poi_area=["leisure=park"])),
    1027: (
        "unknown",
        None,
        _raw(1027, osm_poi_area=["amenity=place_of_worship", "leisure=sports_centre"]),
    ),
    1028: ("unknown", None, _raw(1028, osm_poi_area=["amenity=weird_area"])),
    1050: ("unknown", None, _raw(1050)),
    1029: ("commercial", "osm_poi", _raw(1029, osm_poi=["amenity=pharmacy", "shop=kiosk"])),
    1040: ("commercial", "osm_poi", _raw(1040, osm_poi=["amenity=cafe"])),
    1036: ("residential", "mml_use_class", _raw(1036)),
    1037: ("unknown", None, _raw(1037)),
    1030: ("public", "override", _raw(1030, override="public")),
    1031: ("education", "override", _raw(1031, override="education")),
    1032: ("agricultural", "override", _raw(1032, override="agricultural")),
    1033: ("garage_or_shed", "override", _raw(1033, override="garage_or_shed")),
    1034: ("unknown", None, _raw(1034)),
    1035: ("unknown", None, {**_raw(1035), "mml_feature_class": 42261, "mml_use_class": 6}),
}
for _way in (1041, 1042, 1043, 1044):
    EXPECTED_CATEGORY[_way] = ("unknown", None, _raw(_way))


# The snippet data.


def _rect(osm: Osm, way_id: int, bounds: tuple[float, float, float, float], tags: dict) -> None:
    min_x, min_y, max_x, max_y = bounds
    corners = [(min_x, min_y), (max_x, min_y), (max_x, max_y), (min_x, max_y)]
    refs = [osm.node(x, y) for x, y in corners]
    osm.ways[way_id] = ([*refs, refs[0]], tags)


def footprint(case: Case) -> tuple[float, float, float, float]:
    w, h = case.size
    return (case.x - w / 2, case.y - h / 2, case.x + w / 2, case.y + h / 2)


def snippet_osm() -> tuple[Osm, dict[str, int]]:
    """The OSM snippet, and the node id of each POI node."""
    osm = Osm()
    for way, case in sorted(BUILDINGS.items()):
        _rect(osm, way, footprint(case), case.tags)
    nodes = {name: osm.node(x, y, tags) for name, (x, y, tags) in POI_NODES.items()}
    for way, (*bounds, tags) in {**LANDUSE, **POI_AREAS}.items():
        _rect(osm, way, tuple(bounds), tags)  # type: ignore[arg-type]
    for k, bounds in enumerate(PARK_OUTERS):
        _rect(osm, 6001 + k, bounds, {})
    osm.relations[PARK] = (
        [(6001, "outer"), (6002, "outer")],
        {"type": "multipolygon", "leisure": "park", "name": "Puisto"},
    )
    _rect(osm, 6011, PARKING_OUTER, {})
    _rect(osm, 6012, PARKING_INNER, {})
    osm.relations[PARKING] = (
        [(6011, "outer"), (6012, "inner")],
        {"type": "multipolygon", "amenity": "parking"},
    )
    return osm, nodes


def mml_rows() -> list[tuple[int, Any, int | None, int | None]]:
    ox, oy = FIXTURE_ORIGIN
    rows = []
    for way, case in sorted(BUILDINGS.items()):
        if case.mml is None:
            continue
        if case.mml_box is not None:
            local = box(*case.mml_box)
        else:
            min_x, min_y, max_x, max_y = footprint(case)
            local = box(min_x - 0.5, min_y - 0.5, max_x + 0.5, max_y + 0.5)
        rows.append((way, shapely.affinity.translate(local, ox, oy), *case.mml))
    rows.append((OUTSIDE_RECORD, box(ox + 1000, oy + 1000, ox + 1010, oy + 1010), 42211, 1))
    return rows


MML_IN_AREA = sum(1 for c in BUILDINGS.values() if c.mml is not None)


@dataclass
class PlaceBuild:
    root: Path
    config: MapConfig
    pack: Path
    work: Path
    report: BuildReport
    logs: list[str]
    nodes: dict[str, int]

    def json(self, rel: str) -> Any:
        base = self.pack if not rel.startswith(("0", "timings")) else self.work
        return json.loads((base / rel).read_text(encoding="utf-8"))

    def buildings(self) -> dict[int, dict[str, Any]]:
        return {int(b["id"].rsplit("/", 1)[1]): b for b in self.json("buildings.json")["buildings"]}

    def pois(self) -> dict[str, dict[str, Any]]:
        return {p["id"]: p for p in self.json("pois.json")["pois"]}

    def node_poi(self, name: str) -> dict[str, Any]:
        return self.pois()[f"osm:node/{self.nodes[name]}"]


def setup_snippets(
    root: Path, *, topographic: bool, gpkg: bool, overrides: dict[str, Any] | None
) -> tuple[MapConfig, Path, Path, dict[str, int]]:
    """The repo for a snippet build: config, raw folder, map folder, POI node ids."""
    data = yaml.safe_load(FIXTURE_YAML.read_text(encoding="utf-8"))
    data["id"] = MAP_ID
    data.pop("roads")  # places only: no road stage
    data["sources"]["mml_topographic"]["enabled"] = topographic
    repo = TmpRepo(root)
    (root / "tools" / "mapgen").mkdir(parents=True, exist_ok=True)
    config = repo.load(copy.deepcopy(data))
    raw = write_fixture_raw(repo.raw_dir(MAP_ID), MAP_ID)
    osm, nodes = snippet_osm()
    osm.write(raw / "osm" / f"{MAP_ID}.osm")
    if gpkg:
        write_rakennus_gpkg(raw / TOPO_FILE, mml_rows())
        manifest = json.loads((raw / "download-manifest.json").read_text(encoding="utf-8"))
        manifest["files"].append(
            {
                "path": TOPO_FILE,
                "source": "mml_topographic",
                "dataset": "Topographic Database",
                "request": {},
                "retrieved_at": "2026-10-29T00:00:00Z",
                "size": 0,
                "sha256": "",
                "licence": "CC BY 4.0",
                "attribution": TOPO_ATTRIBUTION,
                "origin": "manual",
            }
        )
        (raw / "download-manifest.json").write_text(json.dumps(manifest), encoding="utf-8")
    map_dir = paths.map_dir(root, MAP_ID)
    if overrides is not None:
        path = map_dir / "overrides" / "buildings.json"
        path.parent.mkdir(parents=True)
        path.write_text(
            json.dumps({"schema_version": 1, "buildings": overrides}, ensure_ascii=False),
            encoding="utf-8",
        )
    return config, raw, map_dir, nodes


def build(
    root: Path, *, topographic: bool = True, gpkg: bool = True, overrides: Any = OVERRIDES
) -> PlaceBuild:
    config, raw, map_dir, nodes = setup_snippets(
        root, topographic=topographic, gpkg=gpkg, overrides=overrides
    )
    logs: list[str] = []
    pack, work = paths.pack_dir(root, MAP_ID), paths.work_dir(root, MAP_ID, "test")
    report = run_build(
        config, "test", raw, pack, work, map_dir=map_dir, now=lambda: FIXED_NOW, log=logs.append
    )
    return PlaceBuild(root, config, pack, work, report, logs, nodes)


@pytest.fixture(scope="module")
def places(tmp_path_factory: pytest.TempPathFactory) -> PlaceBuild:
    """The snippet map with the GPKG and the override file (treat as read-only)."""
    return build(tmp_path_factory.mktemp("places"))


@pytest.fixture(scope="module")
def no_topographic(tmp_path_factory: pytest.TempPathFactory) -> PlaceBuild:
    """The same snippets with `mml_topographic` disabled and no override file."""
    return build(tmp_path_factory.mktemp("no-topo"), topographic=False, gpkg=False, overrides=None)


def test_snippet_pack_passes_the_pack_checks_and_its_schemas(places: PlaceBuild) -> None:
    summary = check_pack(places.pack, places.work, None)
    assert summary.building_count == len(BUILDINGS)
    BUILDINGS_SCHEMA.validate_file(places.pack / "buildings.json")
    POIS_SCHEMA.validate_file(places.pack / "pois.json")
    jsonschema.validate(places.json("buildings.json"), BUILDINGS_SCHEMA.committed())
    jsonschema.validate(places.json("pois.json"), POIS_SCHEMA.committed())


# AC3


@pytest.mark.parametrize("way", sorted(EXPECTED_CATEGORY))
def test_ac3_each_step_gives_the_expected_category_and_keeps_every_raw_value(
    places: PlaceBuild, way: int
) -> None:
    building = places.buildings()[way]
    category, source, raw = EXPECTED_CATEGORY[way]
    assert (building["category"], building["category_source"]) == (category, source)
    assert building["category_raw"] == raw


def test_ac3_every_building_is_a_case(places: PlaceBuild) -> None:
    assert set(places.buildings()) == set(EXPECTED_CATEGORY) == set(BUILDINGS)


def test_ac3_the_39_and_41_square_metre_footprints_straddle_the_rule(places: PlaceBuild) -> None:
    from shapely.geometry import shape

    features = places.json("03-buildings/buildings.geojson")["features"]
    area = {f["properties"]["id"]: shape(f["geometry"]).area for f in features}
    assert area["osm:way/1014"] == pytest.approx(39.0, abs=0.05)
    assert area["osm:way/1015"] == pytest.approx(41.0, abs=0.05)
    report = places.json("03-buildings/report.json")
    small = report["use_class_small_rule"]
    assert (small["threshold_m2"], small["under"], small["at_or_over"]) == (40.0, 1, 3)
    assert small["under_by_category_source"] == {"mml_use_class": 1}
    assert small["at_or_over_by_category_source"] == {"none": 3}  # 1015, 1034, 1035
    fallback = report["use_class_fallback_rule"]
    assert fallback == {"buildings": 2, "by_category_source": {"fallback": 1, "osm_landuse": 1}}


def test_ac3_conflicting_pois_go_on_to_the_next_step_and_are_reported(places: PlaceBuild) -> None:
    report = places.json("03-buildings/report.json")
    (conflict,) = report["poi_category_conflicts"]
    assert conflict["id"] == "osm:way/1006"
    assert conflict["categories"] == ["commercial", "public"]
    assert conflict["pois"] == sorted(f"osm:node/{places.nodes[n]}" for n in ("cafe", "library"))
    (area_conflict,) = report["poi_area_category_conflicts"]
    assert area_conflict["id"] == "osm:way/1027"
    assert area_conflict["categories"] == ["public", "religious"]
    assert area_conflict["pois"] == ["osm:way/4002", "osm:way/4003"]


def test_ac3_counts_per_category_and_source_match_the_buildings(places: PlaceBuild) -> None:
    from collections import Counter

    report = places.json("03-buildings/report.json")
    buildings = places.buildings().values()
    categories = Counter(b["category"] for b in buildings)
    assert {k: v for k, v in report["category_counts"].items() if v} == dict(categories)
    sources = Counter(b["category_source"] or "none" for b in buildings)
    assert {k: v for k, v in report["category_source_counts"].items() if v} == dict(sources)
    assert places.report.category_counts == report["category_counts"]


# AC4


def test_ac4_unmapped_values_are_counted_per_step(places: PlaceBuild) -> None:
    unmapped = places.json("03-buildings/report.json")["unmapped_values"]
    assert unmapped == {
        "osm_building": {"spaceport": 1},
        "osm_poi": {"amenity=foo_bar": 1},
        "osm_poi_area": {"amenity=weird_area": 1},
        "mml_feature_class": {"42290": 1},
        "osm_landuse": {"quarry": 2},  # 1023, and 1026 inside residential
    }


# AC5 (in the build; the rule itself is unit-tested in test_places_units.py)


def test_ac5_matching_in_the_build(places: PlaceBuild) -> None:
    buildings = places.buildings()
    assert buildings[1036]["mml_building"] == "mml:rakennus/1036"
    assert buildings[1036]["mml_overlap"] == pytest.approx(0.6, abs=0.001)
    assert (buildings[1037]["mml_building"], buildings[1037]["mml_overlap"]) == (None, None)
    for way in (1034, 1035):
        assert buildings[way]["mml_building"] == "mml:rakennus/1034"
        assert buildings[way]["mml_overlap"] == 1.0
    use = places.json("03-buildings/report.json")["building_use"]
    assert use["source"] == "mml_topographic"
    assert use["records_in_area"] == MML_IN_AREA  # the record far outside is dropped
    matched = [w for w, b in buildings.items() if b["mml_building"] is not None]
    assert use["matched_buildings"] == len(matched) == MML_IN_AREA  # 1035 in, 1037 out
    assert use["unmatched_records"] == 1  # 1037
    assert use["records_matched_more_than_once_ids"] == ["mml:rakennus/1034"]


# AC6


def _applied(key: str, *fields: str) -> dict[str, Any]:
    entry = OVERRIDES[key]
    return {"id": key, "source": entry["source"], "licence": entry["licence"], "fields": [*fields]}


EXPECTED_OVERRIDES: dict[int, list[dict[str, Any]]] = {
    1001: [_applied("osm:way/1001", "category")],
    1030: [_applied("mml:rakennus/1030", "category")],
    1031: [_applied("mml:rakennus/1031", "name"), _applied("osm:way/1031", "category")],
    1032: [_applied("mml:rakennus/1032", "category"), _applied("osm:way/1032", "name")],
    1033: [_applied("osm:way/1033", "category")],
    1034: [_applied("mml:rakennus/1034", "name")],
    1035: [_applied("mml:rakennus/1034", "name")],
}


def test_ac6_each_buildings_overrides_list_exactly_the_entries_that_set_something(
    places: PlaceBuild,
) -> None:
    for way, building in places.buildings().items():
        assert building["overrides"] == EXPECTED_OVERRIDES.get(way, []), way


def test_ac6_overrides_apply_by_osm_and_mml_id_and_the_osm_key_wins(places: PlaceBuild) -> None:
    b = places.buildings()
    names = {w: (b[w]["name"], b[w]["name_sv"], b[w]["ref"], b[w]["name_source"]) for w in b}
    # By OSM id over the building tag; by MML id over the MML class.
    assert (b[1001]["category"], b[1030]["category"]) == ("industrial", "public")
    # 1031: OSM key's category wins; the MML key still sets the name.
    assert b[1031]["category"] == "education"
    assert names[1031] == ("Mml nimi", None, None, "override")
    # 1032: MML key sets the category, OSM key the name, which sets all three fields
    # (the building's own name:sv and ref are not mixed in).
    assert b[1032]["category"] == "agricultural"
    assert names[1032] == ("Osm nimi", None, None, "override")
    # 1033: the OSM key wins the only field the MML entry sets.
    assert b[1033]["category"] == "garage_or_shed"
    # An MML key applies to every building matched to that record.
    assert names[1034] == names[1035] == ("Pari", None, None, "override")


def test_ac6_unmatched_override_ids_are_warnings_in_the_report(places: PlaceBuild) -> None:
    overrides = places.json("03-buildings/report.json")["overrides"]
    assert overrides["file"] == f"maps/{MAP_ID}/overrides/buildings.json"
    unmatched = {u["id"]: u for u in overrides["unmatched"]}
    assert set(unmatched) == {"osm:way/999999", "mml:rakennus/99999"}
    assert all("matches no building" in u["warning"] for u in unmatched.values())
    assert unmatched["osm:way/999999"]["licence"] == "CC BY 4.0"
    applied = {a["id"]: a for a in overrides["applied"]}
    for key in EXPECTED_OVERRIDES[1031] + EXPECTED_OVERRIDES[1032]:
        assert applied[key["id"]]["source"] == key["source"]
        assert applied[key["id"]]["licence"] == key["licence"]
    assert applied["mml:rakennus/1034"]["buildings"] == ["osm:way/1034", "osm:way/1035"]


def _override_credits(pack: Path) -> list[dict[str, Any]]:
    manifest = json.loads((pack / "manifest.json").read_text(encoding="utf-8"))
    return [c for c in manifest["credits"] if c["source"] == "overrides"]


def test_ac6_applied_third_party_overrides_add_one_folded_credit(places: PlaceBuild) -> None:
    # Two applied CC BY-SA entries with the same credit fold into one; `own work` adds
    # none; the entry whose fields all lost and the unmatched entry add none.
    assert _override_credits(places.pack) == [
        {
            "source": "overrides",
            "dataset": STREET_IMAGES["source"],
            "licence": "CC BY-SA 4.0",
            "attribution": STREET_IMAGES["attribution"],
            "retrieved_at": "2026-11-02",
        }
    ]
    from mapgen.build.pack_manifest import committed_schema

    jsonschema.validate(places.json("manifest.json"), committed_schema())


def test_ac6_notice_check_passes_with_the_override_credit(
    places: PlaceBuild, tmp_path: Path, capsys: pytest.CaptureFixture[str]
) -> None:
    out = tmp_path / "NOTICE.txt"
    root = str(places.root)
    assert main(["--repo-root", root, "notice", MAP_ID, "--out", str(out)]) == EXIT_OK
    lines = out.read_text(encoding="utf-8").splitlines()
    attribution = STREET_IMAGES["attribution"]
    assert lines[lines.index(attribution) + 1] == (
        "Licence: CC BY-SA 4.0, https://creativecommons.org/licenses/by-sa/4.0/"
    )
    assert main(["--repo-root", root, "notice", "--check", str(out), MAP_ID]) == EXIT_OK
    out.write_text("\n".join(line for line in lines if line != attribution), encoding="utf-8")
    capsys.readouterr()
    assert main(["--repo-root", root, "notice", "--check", str(out), MAP_ID]) == EXIT_FAILED
    assert attribution in capsys.readouterr().err


def test_ac6_own_work_only_overrides_add_no_credit(tmp_path: Path) -> None:
    own_only = {k: v for k, v in OVERRIDES.items() if v["licence"] == OWN_WORK}
    result = build(tmp_path, overrides=own_only)
    assert _override_credits(result.pack) == []
    assert result.buildings()[1001]["category_source"] == "override"


def _tree(folder: Path) -> dict[str, bytes]:
    return {
        p.relative_to(folder).as_posix(): p.read_bytes() for p in folder.rglob("*") if p.is_file()
    }


@pytest.mark.parametrize("case", sorted(INVALID_OVERRIDES))
def test_ac6_invalid_override_file_fails_the_build_before_the_pack_is_touched(
    case: str, tmp_path: Path, fixture_build: FixtureBuild
) -> None:
    config, raw, map_dir, _ = setup_snippets(tmp_path, topographic=True, gpkg=True, overrides={})
    (map_dir / "overrides" / "buildings.json").write_bytes(INVALID_OVERRIDES[case][0])
    pack, work = paths.pack_dir(tmp_path, MAP_ID), paths.work_dir(tmp_path, MAP_ID, "test")
    shutil.copytree(fixture_build.pack_dir, pack)  # a previous pack
    before = _tree(pack)
    siblings = sorted(p.name for p in pack.parent.iterdir())
    with pytest.raises(BuildError, match="invalid building overrides"):
        run_build(config, "test", raw, pack, work, map_dir=map_dir, log=lambda _: None)
    assert _tree(pack) == before
    assert sorted(p.name for p in pack.parent.iterdir()) == siblings  # no staging folder
    assert not work.exists()  # failed before the load stage


# AC7


def test_ac7_building_names_come_from_its_tags_or_its_one_named_poi(places: PlaceBuild) -> None:
    b = places.buildings()

    def names(way: int) -> tuple[Any, ...]:
        return (b[way]["name"], b[way]["name_sv"], b[way]["ref"], b[way]["name_source"])

    assert names(1040) == ("Oma nimi", None, None, "osm")  # own name over its named POI
    assert names(1005) == ("Leipomo", "Bageri", None, "osm_poi")
    assert names(1006) == (None, None, None, None)  # two named POIs
    assert names(1007) == (None, None, None, None)  # a named area it is inside doesn't count
    assert names(1043) == ("Kauppa", "Härkälä", None, "osm")  # trimmed, NFC
    assert unicodedata.is_normalized("NFC", b[1043]["name_sv"])
    assert names(1044) == (None, None, "A;B", "osm")  # empty name is null, `;` kept


def test_ac7_building_addresses_follow_the_name_rules(places: PlaceBuild) -> None:
    b = places.buildings()

    def address(way: int) -> tuple[Any, ...]:
        return (
            b[way]["address"]["street"],
            b[way]["address"]["housenumber"],
            b[way]["address_source"],
        )

    assert address(1040) == ("Testikatu", "12a", "osm")
    assert address(1041) == ("Testikatu", "3-5", "osm")
    assert address(1042) == (None, "7", "osm")
    assert address(1043) == ("Härkätie", "12a", "osm")
    assert unicodedata.is_normalized("NFC", b[1043]["address"]["street"])
    assert address(1019) == (None, None, None)


def test_ac7_poi_addresses_are_their_own_or_their_buildings(places: PlaceBuild) -> None:
    bakery = places.node_poi("bakery")  # own street only: the building's isn't mixed in
    assert (bakery["address"], bakery["address_source"]) == (
        {"street": "Kauppatie", "housenumber": None},
        "osm",
    )
    cafe = places.node_poi("named_cafe")  # none of its own: its building's
    assert cafe["building_id"] == "osm:way/1040"
    assert (cafe["address"], cafe["address_source"]) == (
        {"street": "Testikatu", "housenumber": "12a"},
        "building",
    )
    kiosk = places.node_poi("kiosk")  # neither has one
    assert (kiosk["address"], kiosk["address_source"]) == (
        {"street": None, "housenumber": None},
        None,
    )


# Links (part of AC8 and the spec's link rules, on the snippets)


def test_poi_links_agree_in_both_directions(places: PlaceBuild) -> None:
    b = places.buildings()
    pois = places.pois()
    node = lambda name: f"osm:node/{places.nodes[name]}"  # noqa: E731
    assert b[1005]["poi_ids"] == [node("bakery")]
    assert b[1006]["poi_ids"] == sorted([node("cafe"), node("library")])
    assert b[1029]["poi_ids"] == [node("kiosk"), "osm:way/1029"]  # the way itself and a node
    assert pois["osm:way/1029"]["building_id"] == "osm:way/1029"
    assert b[1007]["area_poi_ids"] == ["osm:way/4001"]
    assert pois["osm:way/4001"]["contains_building_ids"] == ["osm:way/1007"]
    assert b[1008]["area_poi_ids"] == [f"osm:relation/{PARK}"]
    assert b[1027]["area_poi_ids"] == ["osm:way/4002", "osm:way/4003"]
    assert b[1050]["area_poi_ids"] == []  # in the parking area's hole
    assert pois[f"osm:relation/{PARKING}"]["contains_building_ids"] == []
    for area in (4001, 4002, 4003, 4004, 4005):  # representative points outside buildings
        assert pois[f"osm:way/{area}"]["building_id"] is None


# AC9


def _points(outline: list) -> list[tuple[float, float]]:
    return [tuple(p) for polygon in outline for ring in polygon for p in ring]


def _ring_bounds(ring: list) -> tuple[float, float, float, float]:
    xs, zs = [p[0] for p in ring], [p[1] for p in ring]
    return (min(xs), min(zs), max(xs), max(zs))


def _godot_bounds(local: tuple[float, float, float, float]) -> tuple[float, ...]:
    min_x, min_y, max_x, max_y = local
    return (min_x, -max_y, max_x, -min_y)  # z = -north


def test_ac9_area_with_a_hole_gives_closed_rings_exterior_first(places: PlaceBuild) -> None:
    parking = places.pois()[f"osm:relation/{PARKING}"]
    assert (parking["type"], parking["subtype"], parking["geometry"]) == (
        "amenity",
        "parking",
        "area",
    )
    assert (parking["name"], parking["name_sv"], parking["ref"]) == (None, None, None)  # unnamed
    (polygon,) = parking["outline"]
    exterior, hole = polygon
    for ring in polygon:
        assert len(ring) >= 4 and ring[0] == ring[-1]
    assert _ring_bounds(exterior) == pytest.approx(_godot_bounds(PARKING_OUTER), abs=0.01)
    assert _ring_bounds(hole) == pytest.approx(_godot_bounds(PARKING_INNER), abs=0.01)
    assert Polygon(exterior).area > Polygon(hole).area


def test_ac9_multipolygon_gives_one_polygon_per_outer_ring(places: PlaceBuild) -> None:
    park = places.pois()[f"osm:relation/{PARK}"]
    assert len(park["outline"]) == 2
    bounds = sorted(_ring_bounds(polygon[0]) for polygon in park["outline"])
    assert bounds == [pytest.approx(_godot_bounds(b), abs=0.01) for b in sorted(PARK_OUTERS)]
    for polygon in park["outline"]:
        assert len(polygon) == 1 and polygon[0][0] == polygon[0][-1]


def test_ac9_area_crossing_the_build_area_edge_is_clipped(places: PlaceBuild) -> None:
    pitch = places.pois()["osm:way/4005"]
    min_x, min_z, max_x, max_z = _ring_bounds(pitch["outline"][0][0])
    half = places.config.test_area.size_m / 2
    assert max_x == pytest.approx(half, abs=1e-3)
    assert min_x == pytest.approx(120.0, abs=0.01)
    assert -half <= pitch["position"][0] <= half


def test_ac9_points_have_no_outline_and_positions_are_on_the_terrain(places: PlaceBuild) -> None:
    museum = places.node_poi("museum")
    assert (museum["geometry"], museum["outline"], museum["building_id"]) == ("point", None, None)
    assert museum["contains_building_ids"] == []
    x, y = POI_NODES["museum"][:2]
    assert museum["position"] == pytest.approx([x, plane_z(x, y), -y], abs=2e-3)
    assert (museum["type"], museum["subtype"], museum["other_tags"]) == (
        "tourism",
        "museum",
        ["leisure=park"],
    )


def test_ac9_pois_json_holds_the_pois_inside_the_area(places: PlaceBuild) -> None:
    pois = places.pois()
    expected = {f"osm:node/{n}" for name, n in places.nodes.items() if name != "outside"}
    expected |= {f"osm:way/{w}" for w in POI_AREAS}
    expected |= {f"osm:way/{w}" for w in (1003, 1004, 1021, 1029)}  # POI tags on the way
    expected |= {f"osm:relation/{PARK}", f"osm:relation/{PARKING}"}
    assert set(pois) == expected
    assert list(pois) == sorted(pois)
    report = places.json("08-places/report.json")["pois"]
    assert report["total"] == len(expected)
    assert report["by_type_and_subtype"]["amenity=parking"] == 1
    for poi in pois.values():
        if poi["geometry"] == "area":
            for x, z in _points(poi["outline"]):
                assert abs(x) <= 150 + 1e-6 and abs(z) <= 150 + 1e-6


# AC11


def test_ac11_topographic_enabled_without_its_gpkg_fails_with_the_download_hint(
    tmp_path: Path,
) -> None:
    with pytest.raises(BuildError, match=f"mapgen download {MAP_ID} --only mml_topographic"):
        build(tmp_path, gpkg=False, overrides=None)


def test_ac11_topographic_disabled_builds_with_step_5_empty(no_topographic: PlaceBuild) -> None:
    buildings = no_topographic.buildings().values()
    assert buildings
    for building in buildings:
        assert (building["mml_building"], building["mml_overlap"]) == (None, None)
        assert building["category_source"] != "mml_use_class"
        raw = building["category_raw"]
        assert (raw["mml_feature_class"], raw["mml_use_class"]) == (None, None)
    b = no_topographic.buildings()
    assert (b[1010]["category"], b[1010]["category_source"]) == ("unknown", None)
    assert (b[1016]["category"], b[1016]["category_source"]) == ("retail", "osm_landuse")
    assert (b[1017]["category"], b[1017]["category_source"]) == ("unknown", None)  # no fallback
    assert any("no enabled source provides building_use" in line for line in no_topographic.logs)
    use = no_topographic.json("03-buildings/report.json")["building_use"]
    assert use == {"source": None}
    credits = {c["source"] for c in no_topographic.json("manifest.json")["credits"]}
    assert "mml_topographic" not in credits
    check_pack(no_topographic.pack, no_topographic.work, None)
