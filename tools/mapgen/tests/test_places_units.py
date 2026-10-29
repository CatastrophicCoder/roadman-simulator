"""Place information: unit tests on the tables, matching, links, the GPKG reader and the
override file.

Criterion numbers (`acN`) refer to docs/specs/place-information.md. The OSM tables are
compared with the tables in the spec itself (parsed from the Markdown), so the expected
values are the spec's, not a copy. The end-to-end snippet build is in
test_places_snippets.py; the Somero test area in test_places_acceptance.py.
"""

import json
import math
import re
import struct
import unicodedata
from pathlib import Path
from typing import Any

import jsonschema
import pytest
import shapely
from shapely.geometry import Point, Polygon, box

from conftest import REPO_ROOT, TmpRepo, gpkg_blob, write_rakennus_gpkg
from mapgen.build.names import address_fields, name_fields
from mapgen.build.places.categories import (
    BUILDING_VALUES,
    LANDUSE_VALUES,
    POI_ANY_VALUE,
    POI_VALUES,
    building_value_category,
    feature_poi_category,
    landuse_category,
    poi_tag_category,
)
from mapgen.build.places.links import LinkPoi, link_pois
from mapgen.build.places.matching import match_building_use
from mapgen.build.places.model import OVERRIDES_SCHEMA, OWN_WORK
from mapgen.build.places.overrides import (
    BuildingOverridesError,
    override_credits,
    parse_building_overrides,
    read_building_overrides,
    resolve_override,
)
from mapgen.build.places.resolve import resolve_address, resolve_names
from mapgen.build.stage1_load import LocalBuildingUse, LocalFootprint
from mapgen.config import MapConfig
from mapgen.geo import BBox
from mapgen.licences import LICENCE_URLS
from mapgen.sources.base import LoadContext, SourceLoadError
from mapgen.sources.mml import (
    GpkgError,
    gpkg_envelope,
    gpkg_wkb,
    rakennus_rule,
    read_rakennus,
)
from mapgen.sources.registry import ADAPTERS

SPEC = REPO_ROOT / "docs" / "specs" / "place-information.md"
ORIGIN = (309657.0, 6726616.0)


# Spec tables (parsed from the Markdown).


def _spec_section(heading: str) -> list[str]:
    """The table rows (`| ... |`) that follow `heading` up to the next blank paragraph."""
    lines = SPEC.read_text(encoding="utf-8").splitlines()
    start = lines.index(heading)
    rows: list[str] = []
    for line in lines[start + 1 :]:
        if line.startswith("|"):
            rows.append(line)
        elif rows:
            break
    return [r for r in rows[2:]]  # skip the header and the separator


def _cells(row: str) -> list[str]:
    return [c.strip() for c in row.strip().strip("|").split("|")]


def _ticks(text: str) -> list[str]:
    return re.findall(r"`([^`]+)`", text)


def spec_building_values() -> dict[str, str]:
    table: dict[str, str] = {}
    for row in _spec_section("`building` values (step 2):"):
        category, values = _cells(row)
        for value in _ticks(values):
            table[value] = _ticks(category)[0]
    return table


def spec_poi_tags() -> tuple[dict[tuple[str, str], str | None], dict[str, tuple[str, set[str]]]]:
    """(listed key=value pairs -> category or None, `key=*` keys -> (category, except))."""
    pairs: dict[tuple[str, str], str | None] = {}
    any_value: dict[str, tuple[str, set[str]]] = {}
    for row in _spec_section("POI tags (steps 3 and 4):"):
        category_cell, tags = _cells(row)
        category = _ticks(category_cell)[0] if _ticks(category_cell) else None
        for part in tags.split(";"):
            tokens = _ticks(part)
            if not tokens:
                continue
            if "=" in tokens[0]:
                key, value = tokens[0].split("=", 1)
                if value == "*":
                    assert category is not None
                    any_value[key] = (category, set(tokens[1:]))
                else:
                    pairs[(key, value)] = category
            else:
                for value in tokens[1:]:
                    pairs[(tokens[0], value)] = category
    return pairs, any_value


def spec_landuse_values() -> dict[str, str]:
    text = SPEC.read_text(encoding="utf-8")
    (sentence,) = re.findall(r"`landuse` values \(step 6\): (.*?) Other values", text)
    table: dict[str, str] = {}
    for part in sentence.rstrip(".").split(";"):
        if "→" in part:
            values, category = part.split("→")
            for value in _ticks(values):
                table[value] = _ticks(category)[0]
        else:
            assert "same name" in part
            for value in _ticks(part):
                table[value] = value
    return table


def test_spec_tables_parse_to_the_expected_sizes() -> None:
    """Guards the parser: the counts are those of the tables as written in the spec."""
    assert len(spec_building_values()) == 9 + 3 + 3 + 4 + 7 + 7 + 4 + 7 + 4
    pairs, any_value = spec_poi_tags()
    assert set(any_value) == {"shop", "office"}
    assert any_value["office"] == ("commercial", {"government"})
    assert len(spec_landuse_values()) == 9


# AC4


def test_ac4_every_building_value_in_the_spec_maps_as_listed() -> None:
    table = spec_building_values()
    assert BUILDING_VALUES == table
    for value, category in table.items():
        assert building_value_category(value) == (category, False), value


def test_ac4_generic_and_unmapped_building_values() -> None:
    assert building_value_category("yes") == (None, False)
    assert building_value_category("roof") == (None, False)
    assert building_value_category(None) == (None, False)
    assert building_value_category("spaceport") == ("other", True)


def test_ac4_every_poi_tag_in_the_spec_maps_as_listed() -> None:
    pairs, any_value = spec_poi_tags()
    assert dict(POI_ANY_VALUE) == {k: c for k, (c, _) in any_value.items()}
    assert set(POI_VALUES) == set(pairs)
    for (key, value), category in pairs.items():
        status = "mapped" if category is not None else "no_value"
        assert poi_tag_category(key, value) == (category, status), f"{key}={value}"
    for key, (category, exceptions) in any_value.items():
        assert poi_tag_category(key, "any_value_at_all") == (category, "mapped")
        for value in exceptions:
            mapped, status = poi_tag_category(key, value)
            assert status == "mapped" and mapped != category, f"{key}={value}"
    # The exception is listed elsewhere in the table.
    assert poi_tag_category("office", "government") == ("public", "mapped")


def test_ac4_unmapped_poi_tag_is_other_in_step_3_and_no_value_in_step_4() -> None:
    assert poi_tag_category("amenity", "foo_bar") == (None, "unmapped")
    assert feature_poi_category({"amenity": "foo_bar"}, unmapped_as_other=True) == (
        "other",
        ["amenity=foo_bar"],
    )
    assert feature_poi_category({"amenity": "foo_bar"}, unmapped_as_other=False) == (
        None,
        ["amenity=foo_bar"],
    )
    # "No value" tags are ignored, not unmapped, and don't become `other`.
    assert feature_poi_category({"amenity": "parking"}, unmapped_as_other=True) == (None, [])


def test_ac4_every_landuse_value_in_the_spec_maps_as_listed() -> None:
    table = spec_landuse_values()
    assert LANDUSE_VALUES == table
    for value, category in table.items():
        assert landuse_category(value) == category
    assert landuse_category("quarry") is None
    assert landuse_category(None) is None


# MML feature classes (step 5; the table lives in the adapter).


@pytest.mark.parametrize(
    ("codes", "category", "small", "fallback"),
    [
        ((42210, 42211, 42212), "residential", None, None),
        ((42230, 42231, 42232), "residential", None, None),
        ((42240, 42241, 42242), "industrial", None, None),
        ((42250, 42251, 42252), "religious", None, None),
        ((42270,), "religious", None, None),
        ((42260, 42261, 42262), None, "garage_or_shed", None),
        ((42220, 42221, 42222), None, None, "commercial"),
    ],
)
def test_ac3_mml_feature_classes_map_by_their_first_four_digits(
    codes: tuple[int, ...], category: str | None, small: str | None, fallback: str | None
) -> None:
    for code in codes:
        rule = rakennus_rule(code)
        assert rule.mapped, code
        assert (rule.category, rule.small_category, rule.fallback_category) == (
            category,
            small,
            fallback,
        ), code
        if small is not None:
            assert rule.small_area_m2 == 40.0


@pytest.mark.parametrize("code", [None, 42200, 42271, 42280, 42290, 12345])
def test_ac3_other_mml_feature_classes_are_unmapped(code: int | None) -> None:
    rule = rakennus_rule(code)
    assert not rule.mapped
    assert (rule.category, rule.small_category, rule.fallback_category) == (None, None, None)


# AC5: matching.


def _footprint(fid: str, geometry: Polygon) -> LocalFootprint:
    return LocalFootprint(fid, geometry, {"building": "yes"})


def _record(number: int, geometry: Polygon, feature_class: int = 42211) -> LocalBuildingUse:
    return LocalBuildingUse(
        f"mml:rakennus/{number}", geometry, feature_class, 1, rakennus_rule(feature_class)
    )


def test_ac5_a_60_percent_overlap_matches_and_a_40_percent_overlap_does_not() -> None:
    footprints = [
        _footprint("osm:way/1", box(0, 0, 10, 10)),
        _footprint("osm:way/2", box(50, 0, 60, 10)),
    ]
    records = [_record(1, box(4, -5, 20, 15)), _record(2, box(56, -5, 70, 15))]
    matches = match_building_use(footprints, records)
    assert set(matches) == {"osm:way/1"}
    assert matches["osm:way/1"].record.id == "mml:rakennus/1"
    assert matches["osm:way/1"].overlap == 0.6


def test_ac5_exactly_half_matches() -> None:
    matches = match_building_use(
        [_footprint("osm:way/1", box(0, 0, 10, 10))], [_record(1, box(5, -5, 20, 15))]
    )
    assert matches["osm:way/1"].overlap == 0.5


def test_ac5_two_osm_buildings_on_one_mml_building_both_match() -> None:
    footprints = [
        _footprint("osm:way/1", box(0, 0, 10, 10)),
        _footprint("osm:way/2", box(10, 0, 20, 10)),
    ]
    matches = match_building_use(footprints, [_record(7, box(0, 0, 20, 10))])
    assert {m.record.id for m in matches.values()} == {"mml:rakennus/7"}
    assert set(matches) == {"osm:way/1", "osm:way/2"}
    assert all(m.overlap == 1.0 for m in matches.values())


def test_ac5_equal_overlaps_go_to_the_lower_mtk_id() -> None:
    # 12 sorts before 7 as text; the rule is the lower number. Both orders of input.
    footprint = _footprint("osm:way/1", box(0, 0, 10, 10))
    left, right = box(-5, -5, 5, 15), box(5, -5, 15, 15)
    for records in ([_record(12, left), _record(7, right)], [_record(7, right), _record(12, left)]):
        match = match_building_use([footprint], records)["osm:way/1"]
        assert match.record.id == "mml:rakennus/7"
        assert match.overlap == 0.5


def test_ac5_the_largest_overlap_wins_over_a_lower_id() -> None:
    footprint = _footprint("osm:way/1", box(0, 0, 10, 10))
    records = [_record(1, box(-5, -5, 4, 15)), _record(2, box(4, -5, 15, 15))]
    match = match_building_use([footprint], records)["osm:way/1"]
    assert (match.record.id, match.overlap) == ("mml:rakennus/2", 0.6)


def test_ac5_overlap_is_rounded_to_three_decimals() -> None:
    footprint = _footprint("osm:way/1", box(0, 0, 3, 3))  # 9 m², 2/3 covered
    match = match_building_use([footprint], [_record(1, box(1, -1, 4, 4))])["osm:way/1"]
    assert match.overlap == 0.667


# POI links (edge cases from the spec).


def test_poi_node_on_a_shared_wall_goes_to_the_smaller_footprint_and_is_reported() -> None:
    small = _footprint("osm:way/2", box(0, 0, 10, 10))
    large = _footprint("osm:way/1", box(10, 0, 30, 10))
    node = LinkPoi("osm:node/5", Point(10, 5), Point(10, 5), False)
    links = link_pois([large, small], [node])
    assert links.building_of_poi == {"osm:node/5": "osm:way/2"}
    assert links.pois_of_building == {"osm:way/2": ["osm:node/5"]}
    assert links.several_footprints == [
        {"poi": "osm:node/5", "buildings": ["osm:way/1", "osm:way/2"], "chosen": "osm:way/2"}
    ]


def test_buildings_in_a_poi_area_hole_are_not_inside_it() -> None:
    ring = Polygon([(0, 0), (40, 0), (40, 40), (0, 40)], [[(10, 10), (30, 10), (30, 30), (10, 30)]])
    inside_hole = _footprint("osm:way/1", box(15, 15, 25, 25))
    on_ring = _footprint("osm:way/2", box(1, 1, 6, 6))
    area = LinkPoi("osm:relation/9", ring, ring.point_on_surface(), False)
    links = link_pois([inside_hole, on_ring], [area])
    assert links.areas_of_building == {"osm:way/2": ["osm:relation/9"]}
    assert links.buildings_of_area == {"osm:relation/9": ["osm:way/2"]}


# AC7: name and address values (the shared function, unit level).


def test_ac7_buildings_and_pois_use_the_road_name_function() -> None:
    from mapgen.build.places import resolve
    from mapgen.build.roads import tags as road_tags

    assert road_tags.name_fields is name_fields
    assert resolve.name_fields is name_fields
    assert resolve.address_fields is address_fields


@pytest.mark.parametrize(
    ("tags", "expected"),
    [
        ({}, (None, None, None)),
        ({"name": "", "name:sv": "", "ref": ""}, (None, None, None)),
        ({"name": "   ", "ref": "  "}, (None, None, None)),
        ({"name": "  Härkälä  ", "name:sv": " Åbo ", "ref": " 52 "}, ("Härkälä", "Åbo", "52")),
        ({"name": unicodedata.normalize("NFD", "Härkälä")}, ("Härkälä", None, None)),
        ({"name": "A;B", "ref": "52;2341"}, ("A;B", None, "52;2341")),
    ],
)
def test_ac7_building_name_values_follow_the_road_rules(
    tags: dict[str, str], expected: tuple[str | None, ...]
) -> None:
    result = resolve_names(
        override=None, building_tags={"building": "yes", **tags}, inside_poi_names=[]
    )
    assert (result.names["name"], result.names["name_sv"], result.names["ref"]) == expected
    assert result.source == (None if expected == (None, None, None) else "osm")
    for value in expected:
        assert value is None or unicodedata.is_normalized("NFC", value)


@pytest.mark.parametrize(
    ("tags", "expected", "source"),
    [
        ({}, (None, None), None),
        ({"addr:street": "", "addr:housenumber": " "}, (None, None), None),
        ({"addr:street": " Testikatu ", "addr:housenumber": "12a"}, ("Testikatu", "12a"), "osm"),
        ({"addr:street": "Testikatu", "addr:housenumber": "3-5"}, ("Testikatu", "3-5"), "osm"),
        ({"addr:housenumber": "7"}, (None, "7"), "osm"),
        ({"addr:street": unicodedata.normalize("NFD", "Härkätie")}, ("Härkätie", None), "osm"),
        ({"addr:street": "A;B", "addr:housenumber": "1;3"}, ("A;B", "1;3"), "osm"),
    ],
)
def test_ac7_address_values_follow_the_road_rules(
    tags: dict[str, str], expected: tuple[str | None, str | None], source: str | None
) -> None:
    address, address_source = resolve_address(tags)
    assert (address["street"], address["housenumber"]) == expected
    assert address_source == source


def test_ac7_names_from_one_named_poi_but_not_from_two() -> None:
    one = {"name": "Kahvila", "name_sv": "Kafé", "ref": None}
    unnamed = {"name": None, "name_sv": None, "ref": None}
    result = resolve_names(override=None, building_tags={}, inside_poi_names=[one, unnamed])
    assert (result.names, result.source) == (one, "osm_poi")
    two = resolve_names(
        override=None, building_tags={}, inside_poi_names=[one, {**one, "name": "B"}]
    )
    assert (two.names, two.source) == (unnamed, None)
    own = resolve_names(override=None, building_tags={"name": "Oma"}, inside_poi_names=[one])
    assert (own.names["name"], own.source) == ("Oma", "osm")


# The `rakennus` GeoPackage reader (synthetic GPKG written with sqlite3).


def _world(geometry: Polygon) -> Polygon:
    return shapely.affinity.translate(geometry, *ORIGIN)


def test_gpkg_wkb_skips_each_envelope_size_and_reads_both_byte_orders() -> None:
    square = box(1, 2, 3, 4)
    for envelope in (False, True):
        for little in (False, True):
            blob = gpkg_blob(square, envelope=envelope, little_endian=little)
            wkb = gpkg_wkb(blob)
            assert wkb is not None
            assert shapely.from_wkb(wkb).equals(square)
    # Envelope indicator 2 (xyz, 48 bytes), written by hand.
    header = b"GP" + bytes([0, 0b101]) + struct.pack("<i", 3067) + struct.pack("<6d", *range(6))
    assert shapely.from_wkb(gpkg_wkb(header + shapely.to_wkb(square))).equals(square)
    assert gpkg_wkb(gpkg_blob(square, empty=True)) is None
    with pytest.raises(GpkgError, match="no GP magic"):
        gpkg_wkb(b"XX" + bytes(30))
    with pytest.raises(GpkgError, match="envelope"):
        gpkg_wkb(b"GP" + bytes([0, 0b1011]) + bytes(4))  # indicator 5


def test_read_rakennus_reads_ids_classes_and_polygons(tmp_path: Path) -> None:
    bow_tie = Polygon([(0, 0), (10, 10), (10, 0), (0, 10)])
    path = write_rakennus_gpkg(
        tmp_path / "t.gpkg",
        [
            (12, _world(box(0, 0, 10, 10)), 42211, 1),
            (7, _world(box(20, 0, 30, 10)), 42261, 6),
            (30, _world(bow_tie), 42221, 2),  # invalid: repaired with make_valid
            (40, gpkg_blob(_world(box(0, 0, 1, 1)), empty=True), 42211, 1),
            (41, None, 42211, 1),
            (42, _world(Polygon([(0, 0), (5, 0), (10, 0)])), 42211, 1),  # no area
            (43, _world(box(50, 0, 60, 10)), None, None),
        ],
    )
    records, skipped = read_rakennus(path)
    assert [r.id for r in records] == [
        "mml:rakennus/7",
        "mml:rakennus/12",
        "mml:rakennus/30",
        "mml:rakennus/43",
    ]
    by_id = {r.id: r for r in records}
    assert by_id["mml:rakennus/12"].crs == "EPSG:3067"
    assert (by_id["mml:rakennus/12"].feature_class, by_id["mml:rakennus/12"].use_class) == (
        42211,
        1,
    )
    assert by_id["mml:rakennus/12"].geometry.equals(_world(box(0, 0, 10, 10)))
    assert by_id["mml:rakennus/12"].rule == rakennus_rule(42211)
    repaired = by_id["mml:rakennus/30"].geometry
    assert repaired.is_valid and repaired.area == pytest.approx(50.0)
    assert (by_id["mml:rakennus/43"].feature_class, by_id["mml:rakennus/43"].use_class) == (
        None,
        None,
    )
    assert not by_id["mml:rakennus/43"].rule.mapped
    reasons = {s.id: s.reason for s in skipped}
    assert reasons == {
        "mml:rakennus/40": "empty geometry",
        "mml:rakennus/41": "no geometry",
        "mml:rakennus/42": "polygon has no area after repair",
    }


def test_read_rakennus_skips_keep_the_raw_geometry_or_the_header_envelope(
    tmp_path: Path,
) -> None:
    flat = _world(Polygon([(0, 0), (5, 0), (10, 0)]))
    envelope = _world(box(20, 0, 30, 10))
    broken = gpkg_blob(envelope)[: 8 + 32] + b"\x01\x03\x00\x00"  # envelope, bad WKB
    no_envelope = gpkg_blob(envelope, envelope=False)[:8] + b"\x01\x03\x00\x00"
    path = write_rakennus_gpkg(
        tmp_path / "t.gpkg",
        [
            (1, flat, 42211, 1),
            (2, broken, 42211, 1),
            (3, no_envelope, 42211, 1),
            (4, gpkg_blob(envelope, empty=True), 42211, 1),
            (5, None, 42211, 1),
        ],
    )
    records, skipped = read_rakennus(path)
    assert records == []
    by_id = {s.id.rsplit("/", 1)[1]: s for s in skipped}
    assert by_id["1"].reason == "polygon has no area after repair"
    assert by_id["1"].geometry is not None and by_id["1"].geometry.equals(flat)
    assert by_id["1"].crs == "EPSG:3067"
    assert by_id["2"].reason.startswith("geometry error")
    assert by_id["2"].geometry is not None and by_id["2"].geometry.equals(envelope)
    assert by_id["3"].reason.startswith("geometry error") and by_id["3"].geometry is None
    assert by_id["4"].reason == "empty geometry"  # located by its (here finite) envelope
    assert by_id["4"].geometry is not None and by_id["4"].geometry.equals(envelope)
    assert (by_id["5"].reason, by_id["5"].geometry) == ("no geometry", None)


def test_gpkg_envelope_reads_the_header_and_ignores_missing_or_nan_envelopes() -> None:
    square = box(1, 2, 3, 4)
    for little in (False, True):
        found = gpkg_envelope(gpkg_blob(square, little_endian=little))
        assert found is not None and found.equals(square)
    point = gpkg_envelope(gpkg_blob(shapely.Point(5, 6)))
    assert point is not None and point.equals(shapely.Point(5, 6))
    assert gpkg_envelope(gpkg_blob(square, envelope=False)) is None
    nan = b"GP" + bytes([0, 0b11]) + struct.pack("<i", 3067) + struct.pack("<4d", *[math.nan] * 4)
    assert gpkg_envelope(nan) is None
    assert gpkg_envelope(b"XX" + bytes(40)) is None


def test_read_rakennus_takes_the_crs_from_the_geopackage(tmp_path: Path) -> None:
    path = write_rakennus_gpkg(
        tmp_path / "t.gpkg", [(1, box(23.5, 60.6, 23.51, 60.61), 42211, 1)], srs_id=4326
    )
    (record,), _ = read_rakennus(path)
    assert record.crs == "EPSG:4326"


def test_read_rakennus_without_the_table_fails(tmp_path: Path) -> None:
    path = write_rakennus_gpkg(
        tmp_path / "t.gpkg", [(1, _world(box(0, 0, 1, 1)), 42211, 1)], table="tieviiva"
    )
    with pytest.raises(GpkgError, match="no table 'rakennus'"):
        read_rakennus(path)


def _ctx(config: MapConfig, raw: Path, source_name: str = "mml_topographic") -> LoadContext:
    adapter = ADAPTERS["mml_topographic"]
    return LoadContext(
        map_config=config,
        source_name=source_name,
        options=adapter.Options(),
        raw_dir=raw,
        target_crs=config.crs,
        bounds=BBox(ORIGIN[0] - 50, ORIGIN[1] - 50, ORIGIN[0] + 50, ORIGIN[1] + 50),
        log=lambda _: None,
    )


def test_ac11_topographic_load_reads_every_gpkg_once(
    fixture_config: MapConfig, tmp_path: Path
) -> None:
    folder = tmp_path / "mml" / "topographic"
    write_rakennus_gpkg(folder / "a.gpkg", [(2, _world(box(0, 0, 10, 10)), 42211, 1)])
    write_rakennus_gpkg(
        folder / "b.gpkg",
        [(2, _world(box(5, 5, 9, 9)), 42261, 6), (1, _world(box(20, 0, 30, 10)), 42221, 2)],
    )
    (folder / "notes.txt").write_text("not a GeoPackage", encoding="utf-8")
    data = ADAPTERS["mml_topographic"]().load(_ctx(fixture_config, tmp_path))
    assert data.files == [folder / "a.gpkg", folder / "b.gpkg"]
    assert [(r.id, r.feature_class) for r in data.building_use] == [
        ("mml:rakennus/1", 42221),
        ("mml:rakennus/2", 42211),  # the first file (by name) wins
    ]


def test_ac11_topographic_load_counts_a_skipped_record_once_across_files(
    fixture_config: MapConfig, tmp_path: Path
) -> None:
    folder = tmp_path / "mml" / "topographic"
    flat = Polygon([(0, 0), (5, 0), (10, 0)])
    write_rakennus_gpkg(
        folder / "a.gpkg",
        [(1, None, 42211, 1), (2, _world(flat), 42211, 1), (3, _world(box(0, 0, 9, 9)), 42211, 1)],
    )
    write_rakennus_gpkg(
        folder / "b.gpkg",
        # 1 skipped in both (a's reason wins); 2 skipped in a but readable here; 3
        # readable in a but skipped here.
        [(1, _world(flat), 42211, 1), (2, _world(box(0, 0, 9, 9)), 42211, 1), (3, None, 1, 1)],
    )
    data = ADAPTERS["mml_topographic"]().load(_ctx(fixture_config, tmp_path))
    assert [r.id for r in data.building_use] == ["mml:rakennus/2", "mml:rakennus/3"]
    assert [(s.id, s.reason) for s in data.building_use_skipped] == [
        ("mml:rakennus/1", "no geometry")
    ]


def test_ac11_topographic_load_fails_on_a_gpkg_without_rakennus(
    fixture_config: MapConfig, tmp_path: Path
) -> None:
    write_rakennus_gpkg(
        tmp_path / "mml" / "topographic" / "x.gpkg",
        [(1, _world(box(0, 0, 1, 1)), 1, 1)],
        table="other",
    )
    with pytest.raises(SourceLoadError, match="no table 'rakennus'"):
        ADAPTERS["mml_topographic"]().load(_ctx(fixture_config, tmp_path))


# AC6: the override file.

THIRD_PARTY = {
    "source": "Street-level image 123456",
    "licence": "CC BY-SA 4.0",
    "attribution": "Example contributor, example imagery service",
    "retrieved_at": "2026-11-02",
}
OWN = {"source": "playtest 2026-11-02", "licence": OWN_WORK}


def _overrides(buildings: dict[str, Any], **top: Any) -> bytes:
    return json.dumps({"schema_version": 1, "buildings": buildings, **top}).encode()


def test_ac6_valid_override_file_parses_and_matches_the_committed_schema() -> None:
    document = {
        "schema_version": 1,
        "buildings": {
            "osm:way/123": {"category": "garage_or_shed", **OWN},
            "osm:relation/5": {"name": "Talo", **OWN},
            "mml:rakennus/456": {"name": "Example", "name_sv": None, "ref": None, **THIRD_PARTY},
        },
    }
    for licence in LICENCE_URLS:
        document["buildings"][f"osm:way/{len(licence)}0"] = {
            "category": "retail",
            **THIRD_PARTY,
            "licence": licence,
        }
    parsed = parse_building_overrides(json.dumps(document).encode(), "test")
    assert set(parsed.buildings) == set(document["buildings"])
    jsonschema.validate(document, OVERRIDES_SCHEMA.committed())


def test_ac6_spec_example_override_file_is_valid() -> None:
    text = SPEC.read_text(encoding="utf-8")
    (example,) = [
        b for b in re.findall(r"```json\n(.*?)```", text, flags=re.DOTALL) if "buildings" in b
    ]
    parsed = parse_building_overrides(example.encode(), "spec")
    assert set(parsed.buildings) == {"osm:way/123", "mml:rakennus/456"}
    jsonschema.validate(json.loads(example), OVERRIDES_SCHEMA.committed())


INVALID_OVERRIDES: dict[str, tuple[bytes, str]] = {
    "unknown_entry_key": (
        _overrides({"osm:way/1": {"category": "retail", "colour": "red", **OWN}}),
        "colour",
    ),
    "unknown_top_key": (_overrides({"osm:way/1": {"category": "retail", **OWN}}, extra=1), "extra"),
    "unknown_category": (_overrides({"osm:way/1": {"category": "castle", **OWN}}), "category"),
    "missing_source": (
        _overrides({"osm:way/1": {"category": "retail", "licence": OWN_WORK}}),
        "source",
    ),
    "empty_source": (
        _overrides({"osm:way/1": {"category": "retail", "source": "", "licence": OWN_WORK}}),
        "source",
    ),
    "missing_licence": (
        _overrides({"osm:way/1": {"category": "retail", "source": "playtest"}}),
        "licence",
    ),
    "neither_category_nor_name": (_overrides({"osm:way/1": dict(OWN)}), "category or a name"),
    "third_party_without_attribution": (
        _overrides(
            {
                "osm:way/1": {
                    "category": "retail",
                    **{k: v for k, v in THIRD_PARTY.items() if k != "attribution"},
                }
            }
        ),
        "attribution",
    ),
    "third_party_without_retrieved_at": (
        _overrides(
            {
                "osm:way/1": {
                    "category": "retail",
                    **{k: v for k, v in THIRD_PARTY.items() if k != "retrieved_at"},
                }
            }
        ),
        "retrieved_at",
    ),
    "licence_notice_does_not_know": (
        _overrides({"osm:way/1": {"category": "retail", **THIRD_PARTY, "licence": "CC0 1.0"}}),
        "licence",
    ),
    "attribution_on_own_work": (
        _overrides({"osm:way/1": {"category": "retail", **OWN, "attribution": "Me"}}),
        "attribution",
    ),
    "retrieved_at_on_own_work": (
        _overrides({"osm:way/1": {"category": "retail", **OWN, "retrieved_at": "2026-11-02"}}),
        "retrieved_at",
    ),
    "retrieved_at_not_a_date": (
        _overrides(
            {"osm:way/1": {"category": "retail", **THIRD_PARTY, "retrieved_at": "2.11.2026"}}
        ),
        "retrieved_at",
    ),
    "category_wrong_type": (_overrides({"osm:way/1": {"category": 5, **OWN}}), "category"),
    "name_wrong_type": (_overrides({"osm:way/1": {"name": 5, **OWN}}), "name"),
    "bad_key": (_overrides({"way/1": {"category": "retail", **OWN}}), "way/1"),
    "schema_version_2": (
        json.dumps({"schema_version": 2, "buildings": {}}).encode(),
        "schema_version",
    ),
    "schema_version_true": (
        json.dumps({"schema_version": True, "buildings": {}}).encode(),
        "schema_version",
    ),
}


@pytest.mark.parametrize("case", sorted(INVALID_OVERRIDES))
def test_ac6_invalid_override_file_is_rejected(case: str) -> None:
    data, needle = INVALID_OVERRIDES[case]
    with pytest.raises(BuildingOverridesError, match="invalid building overrides x.json") as exc:
        parse_building_overrides(data, "x.json")
    assert needle in str(exc.value)
    if case != "neither_category_nor_name":  # see the test below
        # The committed schema (for other readers) rejects it too.
        with pytest.raises(jsonschema.ValidationError):
            jsonschema.validate(json.loads(data), OVERRIDES_SCHEMA.committed())


def test_ac6_committed_override_schema_rejects_an_entry_without_category_or_name() -> None:
    """The spec requires at least one of `category` or a name field; the model enforces
    it, so the committed schema (the file's contract for other readers) should too."""
    data, _ = INVALID_OVERRIDES["neither_category_nor_name"]
    with pytest.raises(jsonschema.ValidationError):
        jsonschema.validate(json.loads(data), OVERRIDES_SCHEMA.committed())


def test_ac6_a_real_but_impossible_date_is_rejected() -> None:
    data = _overrides(
        {"osm:way/1": {"category": "retail", **THIRD_PARTY, "retrieved_at": "2026-13-45"}}
    )
    with pytest.raises(BuildingOverridesError, match="retrieved_at"):
        parse_building_overrides(data, "x.json")


def test_ac6_duplicate_keys_are_rejected() -> None:
    data = (
        b'{"schema_version": 1, "buildings": {"osm:way/1": {"category": "retail", '
        b'"source": "a", "licence": "own work"}, "osm:way/1": {"category": "public", '
        b'"source": "b", "licence": "own work"}}}'
    )
    with pytest.raises(BuildingOverridesError, match='duplicate key "osm:way/1"'):
        parse_building_overrides(data, "x.json")


def test_ac6_missing_override_file_is_allowed(tmp_path: Path) -> None:
    assert read_building_overrides(tmp_path / "overrides" / "buildings.json") is None


def test_ac6_osm_key_wins_field_by_field_and_a_name_override_sets_all_three() -> None:
    overrides = parse_building_overrides(
        _overrides(
            {
                "osm:way/1": {"name": "Osm", **OWN},
                "mml:rakennus/9": {"category": "public", "name": "Mml", "ref": "R", **OWN},
                "osm:way/2": {"category": "retail", **OWN},
            }
        ),
        "x",
    )
    first = resolve_override(overrides, "osm:way/1", "mml:rakennus/9")
    assert (first.category, first.category_key) == ("public", "mml:rakennus/9")
    assert (dict(first.names or {}), first.names_key) == (
        {"name": "Osm", "name_sv": None, "ref": None},
        "osm:way/1",
    )
    second = resolve_override(overrides, "osm:way/2", "mml:rakennus/9")
    assert (second.category, second.category_key) == ("retail", "osm:way/2")
    assert (dict(second.names or {}), second.names_key) == (
        {"name": "Mml", "name_sv": None, "ref": "R"},
        "mml:rakennus/9",
    )
    none = resolve_override(overrides, "osm:way/3", None)
    assert (none.category, none.names) == (None, None)


def test_ac6_override_credits_only_for_applied_third_party_entries() -> None:
    overrides = parse_building_overrides(
        _overrides(
            {
                "osm:way/1": {"category": "retail", **THIRD_PARTY},
                "osm:way/2": {"category": "retail", **OWN},
                "osm:way/3": {"category": "retail", **THIRD_PARTY, "licence": "CC BY 4.0"},
            }
        ),
        "x",
    )
    credits = override_credits(overrides, {"osm:way/1", "osm:way/2"})
    assert [c.model_dump() for c in credits] == [
        {
            "source": "overrides",
            "dataset": THIRD_PARTY["source"],
            "licence": "CC BY-SA 4.0",
            "attribution": THIRD_PARTY["attribution"],
            "retrieved_at": "2026-11-02",
        }
    ]
    assert override_credits(None, set()) == []


def test_ac6_notice_knows_cc_by_sa() -> None:
    from mapgen.notice import LICENCE_URLS as NOTICE_URLS

    assert NOTICE_URLS["CC BY-SA 4.0"] == "https://creativecommons.org/licenses/by-sa/4.0/"


def test_ac6_overrides_path_is_in_the_map_folder(tmp_repo: TmpRepo) -> None:
    from mapgen.build.places.overrides import overrides_path

    assert overrides_path(tmp_repo.root / "maps" / "x") == (
        tmp_repo.root / "maps" / "x" / "overrides" / "buildings.json"
    )
