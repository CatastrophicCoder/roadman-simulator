import pytest

from mapgen import geo
from mapgen.config import MapConfig

# Reference values computed once with pyproj 3.8 (EPSG:4326 -> EPSG:3067, always_xy).
# The central meridian point is also checked by definition: TM35FIN has a false
# easting of 500 000 m on 27 E.
REFERENCES = [
    ((23.52, 60.63), (309656.758, 6726616.032)),  # Somero bbox centre
    ((24.9384, 60.1699), (385611.317, 6672118.380)),  # Helsinki centre
    ((27.0, 60.0), (500000.0, 6651411.190)),  # central meridian
]


@pytest.mark.parametrize(("lonlat", "expected"), REFERENCES)
def test_wgs84_to_tm35fin(lonlat: tuple[float, float], expected: tuple[float, float]) -> None:
    x, y = geo.wgs84_to_map(*lonlat, "EPSG:3067")
    assert x == pytest.approx(expected[0], abs=0.01)
    assert y == pytest.approx(expected[1], abs=0.01)


def test_map_to_wgs84_round_trip() -> None:
    lon, lat = geo.map_to_wgs84(*geo.wgs84_to_map(23.52, 60.63, "EPSG:3067"), "EPSG:3067")
    assert (lon, lat) == (pytest.approx(23.52, abs=1e-9), pytest.approx(60.63, abs=1e-9))


def test_projected_bbox_envelope() -> None:
    box = geo.projected_bbox(23.50, 60.62, 23.54, 60.64, "EPSG:3067")
    assert box.min_x == pytest.approx(308504.23, abs=0.01)  # south-west corner easting
    assert box.min_y == pytest.approx(6725445.53, abs=0.01)  # south-east corner northing
    assert box.max_x == pytest.approx(310808.63, abs=0.01)  # north-east corner easting
    assert box.max_y == pytest.approx(6727786.84, abs=0.01)  # north-west corner northing
    assert box.rounded_outward() == geo.BBox(308504, 6725445, 310809, 6727787)


def test_projected_bbox_contains_every_densified_edge_point() -> None:
    box = geo.projected_bbox(23.50, 60.62, 23.54, 60.64, "EPSG:3067", densify=8)
    for i in range(41):
        lon = 23.50 + 0.001 * i
        for lat in (60.62, 60.64):
            assert box.contains_point(*geo.wgs84_to_map(lon, lat, "EPSG:3067"))


def test_local_round_trip(fixture_config: MapConfig) -> None:
    origin = fixture_config.origin
    world = (309700.25, 6726500.75)
    local = geo.world_to_local(*world, origin.x, origin.y)
    assert local == pytest.approx((43.25, -115.25))
    assert geo.local_to_world(*local, origin.x, origin.y) == pytest.approx(world)


def test_origin_is_local_zero(fixture_config: MapConfig) -> None:
    origin = fixture_config.origin
    assert geo.world_to_local(origin.x, origin.y, origin.x, origin.y) == (0.0, 0.0)


def test_test_area_is_300m_square_around_origin(fixture_config: MapConfig) -> None:
    area = fixture_config.test_area_bbox
    assert area.width == 300
    assert area.height == 300
    assert area.centre == (fixture_config.origin.x, fixture_config.origin.y)
    assert fixture_config.projected_bbox.contains(area)


def test_testtown_stays_within_about_1_5_km_of_origin(fixture_config: MapConfig) -> None:
    box = fixture_config.projected_bbox
    origin = fixture_config.origin
    for x, y in ((box.min_x, box.min_y), (box.max_x, box.max_y)):
        lx, ly = geo.world_to_local(x, y, origin.x, origin.y)
        assert abs(lx) < 1500 and abs(ly) < 1500


def test_invalid_bbox_raises() -> None:
    with pytest.raises(ValueError):
        geo.BBox(1, 0, 0, 1)
    with pytest.raises(ValueError):
        geo.square_around(0, 0, 0)
