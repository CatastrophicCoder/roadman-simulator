"""Place information: the acceptance criteria on the Somero test area and its fixture.

Criterion numbers (`acN`) refer to docs/specs/place-information.md. The Somero test area
is built from the committed fixture the way CI builds it (`somero_area` in conftest.py).
Expected values come from the fixture files (read independently of mapgen where
possible) or the built pack, never from guessed ranges. Snippet builds are in
test_places_snippets.py, unit tests in test_places_units.py, the committed schemas in
test_places_schemas.py, and the full-map counts in test_build_rawdata.py.
"""

import json
import shutil
import sqlite3
import xml.etree.ElementTree as ET
from collections.abc import Callable
from pathlib import Path
from typing import Any

import jsonschema
import pytest
import shapely
from shapely.geometry import Point, Polygon

from conftest import REPO_ROOT, SOMERO_FIXTURE_RAW, SomeroArea, build_somero_fixture
from mapgen import paths
from mapgen.build import BuildError, run_build
from mapgen.build.area import build_area
from mapgen.build.pack_manifest import committed_schema
from mapgen.cli import EXIT_OK, main
from pack_checks import PackCheckError, check_pack

FIXTURE_OSM = SOMERO_FIXTURE_RAW / "osm" / "somero.osm"
FIXTURE_GPKG = SOMERO_FIXTURE_RAW / "mml" / "topographic" / "maastotietokanta_kaikki.gpkg"
SHOP = "osm:node/9523461057"  # spec "Test area": the one POI, a shop=convenience node
POI_KEYS = ("shop", "amenity", "office", "tourism", "leisure")
METADATA = ("user", "uid", "changeset", "timestamp", "version")


def _json(path: Path) -> Any:
    return json.loads(path.read_text(encoding="utf-8"))


def _osm() -> tuple[dict[int, tuple[float, float, dict]], dict[int, tuple[list[int], dict]]]:
    """Nodes (lon, lat, tags) and ways (refs, tags) of the fixture, read without mapgen."""
    root = ET.parse(FIXTURE_OSM).getroot()

    def tags(element: ET.Element) -> dict[str, str]:
        return {t.attrib["k"]: t.attrib["v"] for t in element.iter("tag")}

    nodes = {
        int(n.attrib["id"]): (float(n.attrib["lon"]), float(n.attrib["lat"]), tags(n))
        for n in root.iter("node")
    }
    ways = {
        int(w.attrib["id"]): ([int(nd.attrib["ref"]) for nd in w.iter("nd")], tags(w))
        for w in root.iter("way")
    }
    return nodes, ways


# AC2


def test_ac2_manifest_validates_and_credits_the_topographic_database(
    somero_area: SomeroArea, tmp_path: Path
) -> None:
    manifest = _json(somero_area.pack / "manifest.json")
    jsonschema.validate(manifest, committed_schema())
    assert manifest["schema_version"] == 1
    download = _json(SOMERO_FIXTURE_RAW / "download-manifest.json")
    (entry,) = [e for e in download["files"] if e["source"] == "mml_topographic"]
    (credit,) = [c for c in manifest["credits"] if c["source"] == "mml_topographic"]
    assert credit == {
        "source": "mml_topographic",
        "dataset": entry["dataset"],
        "licence": entry["licence"],
        "attribution": entry["attribution"],
        "retrieved_at": entry["retrieved_at"],
    }
    assert credit["dataset"] == "Topographic Database"
    out = tmp_path / "NOTICE.txt"
    root = str(somero_area.root)
    assert main(["--repo-root", root, "notice", "somero", "--out", str(out)]) == EXIT_OK
    assert entry["attribution"] in out.read_text(encoding="utf-8").splitlines()
    assert main(["--repo-root", root, "notice", "--check", str(out), "somero"]) == EXIT_OK


# AC8


def test_ac8_the_shop_node_links_both_ways_to_the_building_containing_it(
    somero_area: SomeroArea,
) -> None:
    nodes, ways = _osm()
    lon, lat, shop_tags = nodes[int(SHOP.split("/")[1])]
    assert shop_tags["shop"] == "convenience"
    # The containing building, found from the fixture alone.
    containing = [
        f"osm:way/{way_id}"
        for way_id, (refs, tags) in ways.items()
        if tags.get("building", "no") != "no"
        and len(refs) >= 4
        and refs[0] == refs[-1]
        and Polygon([nodes[r][:2] for r in refs]).covers(Point(lon, lat))
    ]
    assert len(containing) == 1
    (building_id,) = containing
    pois = {p["id"]: p for p in _json(somero_area.pack / "pois.json")["pois"]}
    buildings = {b["id"]: b for b in _json(somero_area.pack / "buildings.json")["buildings"]}
    assert pois[SHOP]["building_id"] == building_id
    building = buildings[building_id]
    assert building["poi_ids"] == [SHOP]
    assert (building["name"], building["name_sv"], building["ref"]) == (
        shop_tags["name"].strip(),
        None,
        None,
    )
    assert building["name_source"] == "osm_poi"
    assert (building["category"], building["category_source"]) == ("retail", "osm_poi")
    assert building["category_raw"]["osm_poi"] == ["shop=convenience"]
    others = [b for b in buildings.values() if b["id"] != building_id]
    assert all(not b["poi_ids"] and b["name_source"] is None for b in others)


def test_ac8_test_area_counts_from_the_fixture(somero_area: SomeroArea) -> None:
    """Spec "Test area": every footprint is a building; one POI; MML matches reported."""
    load = _json(somero_area.work / "01-load" / "report.json")
    buildings = _json(somero_area.pack / "buildings.json")["buildings"]
    assert len(buildings) == load["footprints"]["in_area"]
    pois = _json(somero_area.pack / "pois.json")["pois"]
    assert [p["id"] for p in pois] == [SHOP]
    report = _json(somero_area.work / "03-buildings" / "report.json")
    matched = sum(1 for b in buildings if b["mml_building"] is not None)
    assert report["building_use"]["matched_buildings"] == matched
    assert report["building_use"]["unmatched_buildings"] == len(buildings) - matched
    assert sum(report["category_counts"].values()) == len(buildings)


# AC10


def _pack_copy(somero_area: SomeroArea, tmp_path: Path) -> Path:
    pack = tmp_path / "pack"
    shutil.copytree(somero_area.pack, pack)
    return pack


def _edit(pack: Path, name: str, change: Callable[[dict[str, Any]], None]) -> None:
    path = pack / name
    document = _json(path)
    change(document)
    path.write_text(json.dumps(document, ensure_ascii=False), encoding="utf-8")


def _shop_building(document: dict[str, Any]) -> dict[str, Any]:
    (building,) = [b for b in document["buildings"] if b["poi_ids"]]
    return building


def _first_unlinked(document: dict[str, Any]) -> dict[str, Any]:
    return next(b for b in document["buildings"] if not b["poi_ids"])


def _set(target: Callable[[dict[str, Any]], dict[str, Any]], key: str, value: Any) -> Callable:
    def change(document: dict[str, Any]) -> None:
        target(document)[key] = value

    return change


def _shop(document: dict[str, Any]) -> dict[str, Any]:
    (poi,) = [p for p in document["pois"] if p["id"] == SHOP]
    return poi


def _as_area(outline: list) -> Callable:
    def change(document: dict[str, Any]) -> None:
        poi = _shop(document)
        poi["geometry"] = "area"
        poi["outline"] = outline
        poi["building_id"] = None

    return change


CORRUPTIONS: dict[str, tuple[str, Callable[[dict[str, Any]], None], str]] = {
    "dangling_poi_id": (
        "buildings.json",
        _set(_first_unlinked, "poi_ids", ["osm:node/1"]),
        "which isn't in pois.json",
    ),
    "dangling_area_poi_id": (
        "buildings.json",
        _set(_first_unlinked, "area_poi_ids", ["osm:way/1"]),
        "not in pois.json",
    ),
    "dangling_building_id": (
        "pois.json",
        _set(_shop, "building_id", "osm:way/1"),
        "doesn't link back|isn't a building",
    ),
    "one_way_poi_to_building": (
        "pois.json",
        _set(_shop, "building_id", None),
        "doesn't link back",
    ),
    "invalid_category": ("buildings.json", _set(_first_unlinked, "category", "castle"), "category"),
    "invalid_category_source": (
        "buildings.json",
        _set(_first_unlinked, "category_source", "guess"),
        "category_source",
    ),
    "invalid_name_source": (
        "buildings.json",
        _set(_first_unlinked, "name_source", "guess"),
        "name_source",
    ),
    "invalid_address_source": (
        "buildings.json",
        _set(_first_unlinked, "address_source", "mml"),
        "address_source",
    ),
    "category_source_without_raw_value": (
        "buildings.json",
        _set(_first_unlinked, "category_source", "osm_poi_area"),
        "without its raw value",
    ),
    "poi_position_outside_the_area": (
        "pois.json",
        _set(_shop, "position", [1000.0, 90.0, 0.0]),
        "position outside the area",
    ),
    "poi_position_not_finite": (
        "pois.json",
        _set(_shop, "position", [float("nan"), 90.0, 0.0]),
        "pois.json|position",
    ),
}


@pytest.mark.parametrize("case", sorted(CORRUPTIONS))
def test_ac10_pack_checks_fail_a_corrupted_pack(
    case: str, somero_area: SomeroArea, tmp_path: Path
) -> None:
    pack = _pack_copy(somero_area, tmp_path)
    name, change, message = CORRUPTIONS[case]
    _edit(pack, name, change)
    with pytest.raises(PackCheckError, match=message):
        check_pack(pack, somero_area.work, somero_area.config.roads)


def test_ac10_one_way_link_from_the_building_side_fails(
    somero_area: SomeroArea, tmp_path: Path
) -> None:
    pack = _pack_copy(somero_area, tmp_path)
    _edit(pack, "buildings.json", _set(_shop_building, "poi_ids", []))
    with pytest.raises(PackCheckError, match="doesn't link back"):
        check_pack(pack, somero_area.work, somero_area.config.roads)


def _square(x0: float, z0: float, size: float = 4.0) -> list[list[float]]:
    return [[x0, z0], [x0 + size, z0], [x0 + size, z0 + size], [x0, z0 + size], [x0, z0]]


def test_ac10_an_outline_point_outside_the_area_fails(
    somero_area: SomeroArea, tmp_path: Path
) -> None:
    pack = _pack_copy(somero_area, tmp_path)
    _edit(pack, "buildings.json", _set(_shop_building, "poi_ids", []))
    _edit(pack, "pois.json", _as_area([[_square(-12.0, -118.0)]]))
    check_pack(pack, somero_area.work, somero_area.config.roads)  # the edits are valid
    _edit(pack, "pois.json", _as_area([[_square(140.0, -118.0, 20.0)]]))
    with pytest.raises(PackCheckError, match="outline point outside"):
        check_pack(pack, somero_area.work, somero_area.config.roads)


@pytest.mark.parametrize(
    "outline",
    [
        [[_square(-12.0, -118.0)[:-1]]],  # open
        [[[[-12.0, -118.0], [-8.0, -118.0], [-12.0, -118.0]]]],  # closed, 3 points
        [[]],  # a polygon without rings
        None,  # an area without an outline
    ],
    ids=["open", "short", "no_rings", "none"],
)
def test_ac10_open_short_or_missing_outline_rings_fail(
    outline: Any, somero_area: SomeroArea, tmp_path: Path
) -> None:
    pack = _pack_copy(somero_area, tmp_path)
    _edit(pack, "buildings.json", _set(_shop_building, "poi_ids", []))
    _edit(pack, "pois.json", _as_area(outline))
    with pytest.raises(PackCheckError):
        check_pack(pack, somero_area.work, somero_area.config.roads)


def test_ac10_a_missing_pois_json_fails(somero_area: SomeroArea, tmp_path: Path) -> None:
    pack = _pack_copy(somero_area, tmp_path)
    (pack / "pois.json").unlink()
    with pytest.raises(PackCheckError, match="missing pois.json"):
        check_pack(pack, somero_area.work, somero_area.config.roads)


def _tree(folder: Path) -> dict[str, bytes]:
    return {
        p.relative_to(folder).as_posix(): p.read_bytes() for p in folder.rglob("*") if p.is_file()
    }


def test_ac10_a_failed_place_check_fails_the_build_and_leaves_the_previous_pack(
    tmp_path: Path, monkeypatch: pytest.MonkeyPatch
) -> None:
    import mapgen.build as build_module

    area = build_somero_fixture(tmp_path)  # the previous pack
    before = _tree(area.pack)
    real = build_module.build_places

    def dangling(*args: Any, **kwargs: Any) -> Any:
        result = real(*args, **kwargs)
        for poi in result.pois_document["pois"]:
            poi["building_id"] = "osm:way/1"  # passes the schema, fails the link check
        return result

    monkeypatch.setattr(build_module, "build_places", dangling)
    raw = paths.raw_dir(area.root, "somero")
    with pytest.raises(
        BuildError, match="pack check failed: .*(doesn't link back|isn't a building)"
    ):
        run_build(area.config, "test", raw, area.pack, area.work, log=lambda _: None)
    assert _tree(area.pack) == before
    leftovers = [p.name for p in area.pack.parent.iterdir() if p.name != area.pack.name]
    assert leftovers == []


# AC12


def test_ac12_two_builds_write_byte_identical_place_files(
    somero_area: SomeroArea, tmp_path: Path
) -> None:
    again = build_somero_fixture(tmp_path)
    for name in ("buildings.json", "pois.json"):
        assert (somero_area.pack / name).read_bytes() == (again.pack / name).read_bytes(), name
    for name in ("03-buildings/report.json", "08-places/report.json"):
        assert (somero_area.work / name).read_bytes() == (again.work / name).read_bytes(), name


def test_ac12_place_files_are_utf8_with_unescaped_names(somero_area: SomeroArea) -> None:
    text = (somero_area.pack / "buildings.json").read_text(encoding="utf-8")
    assert "\\u00" not in text  # e.g. "Hämeen härkätie" is written as is
    assert "ä" in text


# AC13


def test_ac13_fixture_gpkg_holds_rakennus_records_touching_the_test_area(
    somero_area: SomeroArea,
) -> None:
    from mapgen.sources.mml import read_rakennus

    con = sqlite3.connect(f"{FIXTURE_GPKG.resolve().as_uri()}?mode=ro", uri=True)
    try:
        columns = {row[1] for row in con.execute("PRAGMA table_info(rakennus)")}
        count = con.execute("SELECT COUNT(*) FROM rakennus").fetchone()[0]
    finally:
        con.close()
    assert {"mtk_id", "kohdeluokka", "kayttotarkoitus", "kerrosluku"} <= columns
    download = _json(SOMERO_FIXTURE_RAW / "download-manifest.json")
    (entry,) = [e for e in download["files"] if e["source"] == "mml_topographic"]
    assert entry["path"] == FIXTURE_GPKG.relative_to(SOMERO_FIXTURE_RAW).as_posix()
    assert entry["request"]["test_area_cut"]["records"] == count > 0
    records, skipped = read_rakennus(FIXTURE_GPKG)
    assert len(records) == count and skipped == []
    bounds = build_area(somero_area.config, "test").bounds
    origin = somero_area.config.origin
    world = shapely.box(
        bounds.min_x + origin.x,
        bounds.min_y + origin.y,
        bounds.max_x + origin.x,
        bounds.max_y + origin.y,
    )
    assert all(r.geometry.intersects(world) for r in records)
    report = _json(somero_area.work / "03-buildings" / "report.json")
    assert report["building_use"]["records_in_area"] == count


def test_ac13_fixture_holds_land_use_and_pois_with_all_their_nodes(
    somero_area: SomeroArea,
) -> None:
    nodes, ways = _osm()
    landuse = {w for w, (_, tags) in ways.items() if "landuse" in tags}
    poi_ways = {w for w, (_, tags) in ways.items() if any(k in tags for k in POI_KEYS)}
    poi_nodes = {n for n, (*_, tags) in nodes.items() if any(k in tags for k in POI_KEYS)}
    download = _json(SOMERO_FIXTURE_RAW / "download-manifest.json")
    (osm,) = [e for e in download["files"] if e["source"] == "osm"]
    cut = osm["request"]["test_area_cut"]
    assert cut["landuse_areas"] == len(landuse) > 0
    assert cut["points_of_interest"] == len(poi_ways) + len(poi_nodes) > 0
    for way_id in landuse | poi_ways:
        refs = ways[way_id][0]
        assert all(r in nodes for r in refs), f"way {way_id} lost nodes in the cut"
    # Every one of them touches the test area.
    config = somero_area.config
    area = build_area(config, "test").bounds
    box = shapely.box(
        area.min_x + config.origin.x,
        area.min_y + config.origin.y,
        area.max_x + config.origin.x,
        area.max_y + config.origin.y,
    )
    from mapgen.geo import wgs84_to_map

    def world(lonlat: tuple[float, float]) -> tuple[float, float]:
        return wgs84_to_map(lonlat[0], lonlat[1], config.crs)

    for way_id in landuse | poi_ways:
        polygon = Polygon([world(nodes[r][:2]) for r in ways[way_id][0]])
        assert polygon.intersects(box), way_id
    for node_id in poi_nodes:
        assert Point(world(nodes[node_id][:2])).intersects(box), node_id
    loaded = _json(somero_area.work / "08-places" / "report.json")
    assert loaded["landuse"]["in_area"] == len(landuse)


def test_ac13_fixture_has_no_contributor_metadata() -> None:
    root = ET.parse(FIXTURE_OSM).getroot()
    for element in root:
        for attribute in METADATA:
            assert attribute not in element.attrib, (element.tag, element.attrib.get("id"))
    con = sqlite3.connect(f"{FIXTURE_GPKG.resolve().as_uri()}?mode=ro", uri=True)
    try:
        tables = {
            row[0] for row in con.execute("SELECT name FROM sqlite_master WHERE type='table'")
        }
    finally:
        con.close()
    assert tables <= {
        "rakennus",
        "gpkg_spatial_ref_sys",
        "gpkg_contents",
        "gpkg_geometry_columns",
        "sqlite_sequence",
    }


def test_ac13_download_manifest_and_notice_name_the_topographic_database() -> None:
    download = _json(SOMERO_FIXTURE_RAW / "download-manifest.json")
    (entry,) = [e for e in download["files"] if e["source"] == "mml_topographic"]
    assert entry["dataset"] == "Topographic Database"
    assert entry["licence"] == "CC BY 4.0"
    assert (SOMERO_FIXTURE_RAW / entry["path"]).is_file()
    notice = (SOMERO_FIXTURE_RAW / "NOTICE.md").read_text(encoding="utf-8")
    assert f"- {entry['attribution']}. Licence:" in notice
    assert "Topographic Database" in notice and "10/2026" in entry["attribution"]


# AC13 against the local raw data (skipped without it, e.g. in CI)

RAW = paths.raw_dir(REPO_ROOT, "somero")
RAW_GPKG = RAW / "mml" / "topographic" / "maastotietokanta_kaikki.gpkg"
RAW_OSM = RAW / "osm" / "somero.osm"


@pytest.mark.rawdata
def test_ac13_fixture_has_every_rakennus_record_touching_the_test_area(
    somero_area: SomeroArea,
) -> None:
    if not RAW_GPKG.is_file():
        pytest.skip("local Topographic Database GPKG not downloaded")
    from mapgen.sources.mml import read_rakennus

    config = somero_area.config
    area = build_area(config, "test").bounds
    world = shapely.box(
        area.min_x + config.origin.x,
        area.min_y + config.origin.y,
        area.max_x + config.origin.x,
        area.max_y + config.origin.y,
    )
    raw_records, _ = read_rakennus(RAW_GPKG)
    expected = {r.id for r in raw_records if r.geometry.intersects(world)}
    fixture_records, _ = read_rakennus(FIXTURE_GPKG)
    assert {r.id for r in fixture_records} == expected


@pytest.mark.rawdata
def test_ac13_fixture_has_every_land_use_area_and_poi_touching_the_test_area(
    somero_area: SomeroArea,
) -> None:
    if not RAW_OSM.is_file():
        pytest.skip("local OSM file not downloaded")
    from mapgen.build.stage1_load import landuse_to_local, poi_to_local
    from mapgen.sources.osm import osm_landuse, osm_pois, parse_osm

    config = somero_area.config
    b = build_area(config, "test").bounds
    local_box = shapely.box(b.min_x, b.min_y, b.max_x, b.max_y)

    def ids(path: Path) -> tuple[set[str], set[str]]:
        data = parse_osm(path)
        pois = {
            p.id
            for p in osm_pois(data).pois
            if poi_to_local(p, config).geometry.intersects(local_box)
        }
        landuse = {
            a.id
            for a in osm_landuse(data).areas
            if landuse_to_local(a, config).geometry.intersects(local_box)
        }
        return pois, landuse

    assert ids(FIXTURE_OSM) == ids(RAW_OSM)
