import re
from pathlib import Path

import numpy as np
import pytest
from shapely.geometry import MultiPolygon, Polygon

from conftest import OSM_FIXTURE, PLANE, TmpRepo, write_plane_dem
from mapgen.config import MapConfig
from mapgen.geo import BBox
from mapgen.sources.base import (
    BUILDINGS_3D,
    ELEVATION,
    OSM_VECTOR,
    LoadContext,
    SourceAdapter,
    SourceLoadError,
)
from mapgen.sources.mml import MmlBuildings3dAdapter, MmlDem2mAdapter, read_dem
from mapgen.sources.osm import OverpassAdapter, join_rings, read_osm_buildings
from mapgen.sources.registry import ADAPTERS, enabled_sources_providing

ORIGIN = (309657.0, 6726616.0)


def test_osm_reader_finds_buildings_and_reports_skips() -> None:
    result = read_osm_buildings(OSM_FIXTURE)
    ids = {f.id for f in result.footprints}
    assert ids == {
        *(f"osm:way/{n}" for n in (101, 102, 103, 104, 105, 106, 109, 110)),
        *(f"osm:relation/{n}" for n in (201, 203, 205, 206, 207)),
    }
    skipped = {s.id: s.reason for s in result.skipped}
    assert set(skipped) == {"osm:way/108", "osm:way/301", "osm:relation/202"}
    assert skipped["osm:way/108"] == "way is not closed"
    assert skipped["osm:way/301"] == "duplicate: outer ring of osm:relation/201"
    assert skipped["osm:relation/202"].startswith("incomplete multipolygon")
    partial = {p.id: p.reason for p in result.partial}
    assert partial == {
        "osm:relation/205": "partial multipolygon: 1 member way(s) missing",
        "osm:relation/206": "partial multipolygon: 1 inner ring(s) not closed",
    }
    assert "osm:way/107" not in ids and "osm:way/111" not in ids  # highway, building=no


def test_osm_multipolygon_keeps_its_hole_and_tags() -> None:
    by_id = {f.id: f for f in read_osm_buildings(OSM_FIXTURE).footprints}
    courtyard = by_id["osm:relation/201"]
    assert isinstance(courtyard.geometry, Polygon)
    assert len(courtyard.geometry.interiors) == 1
    assert courtyard.tags["height"] == "10"
    assert courtyard.crs == "EPSG:4326"
    joined = by_id["osm:relation/203"]
    assert isinstance(joined.geometry, Polygon) and joined.geometry.is_valid
    two_parts = by_id["osm:relation/205"].geometry
    assert isinstance(two_parts, MultiPolygon) and len(two_parts.geoms) == 2
    unclosed_inner = by_id["osm:relation/206"].geometry
    assert isinstance(unclosed_inner, Polygon) and not unclosed_inner.interiors


def test_osm_self_intersecting_inner_ring_is_repaired() -> None:
    bow_tie = {f.id: f for f in read_osm_buildings(OSM_FIXTURE).footprints}["osm:relation/207"]
    geometry = bow_tie.geometry
    assert geometry.is_valid and isinstance(geometry, Polygon)
    assert len(geometry.interiors) == 2  # the bow-tie hole becomes two triangles


def test_osm_geometry_errors_skip_the_relation(monkeypatch: pytest.MonkeyPatch) -> None:
    from shapely.errors import GEOSException

    from mapgen.sources import osm

    def broken(*args: object, **kwargs: object) -> None:
        raise GEOSException("TopologyException: test")

    monkeypatch.setattr(osm, "unary_union", broken)
    result = read_osm_buildings(OSM_FIXTURE)
    reasons = {s.id: s.reason for s in result.skipped}
    assert reasons["osm:relation/201"] == "geometry error: TopologyException: test"
    assert "osm:way/101" in {f.id for f in result.footprints}  # ways are unaffected


def test_join_rings_reverses_and_reports_open_chains() -> None:
    rings, unclosed = join_rings([[1, 2, 3], [1, 4, 3], [7, 8]])
    assert unclosed == 1
    assert len(rings) == 1 and rings[0][0] == rings[0][-1] and set(rings[0]) == {1, 2, 3, 4}


def test_adapters_declare_capabilities() -> None:
    assert OSM_VECTOR in OverpassAdapter.provides
    assert ELEVATION in MmlDem2mAdapter.provides
    assert BUILDINGS_3D in MmlBuildings3dAdapter.provides
    assert ADAPTERS["mml_orthophoto"].provides == frozenset()


def test_sources_are_selected_by_capability_not_name(tmp_repo: TmpRepo, fixture_data: dict) -> None:
    fixture_data["sources"] = {
        "heights": {"adapter": "mml_dem_2m", "options": {}},
        "streets": {"adapter": "osm_overpass", "options": {}},
        "photo": {"adapter": "mml_orthophoto", "options": {}},
    }
    config = tmp_repo.load(fixture_data)
    assert [s.name for s in enabled_sources_providing(config, ELEVATION)] == ["heights"]
    assert [s.name for s in enabled_sources_providing(config, OSM_VECTOR)] == ["streets"]
    assert enabled_sources_providing(config, BUILDINGS_3D) == []


def _ctx(config: MapConfig, raw: Path, adapter: type[SourceAdapter]) -> LoadContext:
    return LoadContext(
        map_config=config,
        source_name="src",
        options=adapter.Options(),
        raw_dir=raw,
        target_crs=config.crs,
        bounds=BBox(ORIGIN[0] - 50, ORIGIN[1] - 50, ORIGIN[0] + 50, ORIGIN[1] + 50),
        log=lambda _: None,
    )


def test_dem_load_crops_to_whole_pixels(fixture_config: MapConfig, tmp_path: Path) -> None:
    write_plane_dem(tmp_path / "mml" / "dem_2m" / "a.tif")
    data = MmlDem2mAdapter().load(_ctx(fixture_config, tmp_path, MmlDem2mAdapter))
    raster = data.elevation
    assert raster is not None and data.files == [tmp_path / "mml" / "dem_2m" / "a.tif"]
    assert raster.warped_from == ()
    assert raster.data.shape == (50, 50)
    assert raster.transform.c == ORIGIN[0] - 50 and raster.transform.f == ORIGIN[1] + 50
    # top-left pixel centre is at local (-49, 49)
    assert raster.data[0, 0] == pytest.approx(PLANE[0] + PLANE[1] * -49 + PLANE[2] * 49)


def test_dem_mosaic_respects_nodata(tmp_path: Path) -> None:
    west = write_plane_dem(tmp_path / "w.tif", origin=(ORIGIN[0] - 40, ORIGIN[1]), half_size=40)
    east = write_plane_dem(
        tmp_path / "e.tif",
        origin=(ORIGIN[0] + 40, ORIGIN[1]),
        half_size=40,
        nodata_cells=(slice(0, 5), slice(0, 5)),
    )
    bounds = BBox(ORIGIN[0] - 80, ORIGIN[1] - 40, ORIGIN[0] + 80, ORIGIN[1] + 40)
    raster = read_dem([west, east], "EPSG:3067", bounds)
    assert raster.data.shape == (40, 80)
    assert np.isnan(raster.data[0:5, 40:45]).all()
    assert np.isfinite(raster.data[0:5, 0:40]).all()
    assert np.isfinite(raster.data[10:, :]).all()


def test_dem_in_another_crs_is_warped(tmp_path: Path) -> None:
    from pyproj import Transformer

    to_3067 = Transformer.from_crs("EPSG:3067", "EPSG:3857", always_xy=True)
    centre = to_3067.transform(*ORIGIN)
    path = write_plane_dem(tmp_path / "m.tif", origin=centre, half_size=400, crs="EPSG:3857")
    bounds = BBox(ORIGIN[0] - 40, ORIGIN[1] - 40, ORIGIN[0] + 40, ORIGIN[1] + 40)
    raster = read_dem([path], "EPSG:3067", bounds)
    assert raster.crs == "EPSG:3067"
    assert raster.warped_from == ("EPSG:3857",)
    assert np.isfinite(raster.data).all()


def test_dem_load_without_files_says_how_to_download(
    fixture_config: MapConfig, tmp_path: Path
) -> None:
    with pytest.raises(SourceLoadError, match="mapgen download testtown"):
        MmlDem2mAdapter().load(_ctx(fixture_config, tmp_path, MmlDem2mAdapter))


def test_osm_load_reads_the_map_file(fixture_config: MapConfig, tmp_path: Path) -> None:
    (tmp_path / "osm").mkdir()
    (tmp_path / "osm" / "testtown.osm").write_bytes(OSM_FIXTURE.read_bytes())
    data = OverpassAdapter().load(_ctx(fixture_config, tmp_path, OverpassAdapter))
    assert data.files == [tmp_path / "osm" / "testtown.osm"]
    assert len(data.footprints) == 13 and len(data.skipped) == 3 and len(data.partial) == 2


def test_buildings_3d_stub_reads_nothing(fixture_config: MapConfig, tmp_path: Path) -> None:
    data = MmlBuildings3dAdapter().load(_ctx(fixture_config, tmp_path, MmlBuildings3dAdapter))
    assert data.footprints == [] and data.files == []


def test_adapters_without_capability_cannot_load(fixture_config: MapConfig, tmp_path: Path) -> None:
    adapter = ADAPTERS["mml_orthophoto"]
    with pytest.raises(NotImplementedError):
        adapter().load(_ctx(fixture_config, tmp_path, adapter))


def test_raster_crs_equivalence_ignores_naming() -> None:
    from rasterio.crs import CRS

    from mapgen.sources.mml import same_raster_crs

    # A GeoTIFF-style definition: other name, no authority codes, same projection.
    wkt = re.sub(r',AUTHORITY\["EPSG","\d+"\]', "", CRS.from_epsg(3067).to_wkt())
    renamed = CRS.from_wkt(wkt.replace("EUREF-FIN / TM35FIN(E,N)", "local name"))
    assert renamed.to_wkt() != CRS.from_epsg(3067).to_wkt()
    assert same_raster_crs(renamed, "EPSG:3067")
    assert same_raster_crs(CRS.from_epsg(3067), "EPSG:3067")
    assert not same_raster_crs(CRS.from_epsg(3857), "EPSG:3067")
