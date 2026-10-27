import pytest

from mapgen.build.area import BuildArea, build_area, tile_id
from mapgen.config import MapConfig
from mapgen.geo import BBox


def test_test_area_has_four_tiles(fixture_config: MapConfig) -> None:
    area = build_area(fixture_config, "test")
    assert area.bounds.as_list() == [-150.0, -150.0, 150.0, 150.0]
    tiles = area.tiles()
    assert [t.id for t in tiles] == ["tile_x-1_y-1", "tile_x0_y-1", "tile_x-1_y0", "tile_x0_y0"]
    assert tiles[0].bounds.as_list() == [-150.0, -150.0, 0.0, 0.0]
    assert tiles[3].bounds.as_list() == [0.0, 0.0, 150.0, 150.0]


def test_full_area_is_about_ten_by_ten_tiles(fixture_config: MapConfig) -> None:
    area = build_area(fixture_config, "full")
    tiles = area.tiles()
    columns = {t.i for t in tiles}
    rows = {t.j for t in tiles}
    assert len(tiles) == len(columns) * len(rows)
    assert 9 <= len(columns) <= 10 and 9 <= len(rows) <= 11
    for tile in tiles:  # clipped to the area, inside the tile's grid cell
        assert area.bounds.contains(tile.bounds)
        assert tile.bounds.min_x >= tile.i * 256 and tile.bounds.max_x <= (tile.i + 1) * 256


def test_tiles_are_half_open() -> None:
    area = BuildArea("test", BBox(-10, -10, 512, 300), 256.0)
    assert area.tile_index(0.0, 0.0) == (0, 0)
    assert area.tile_index(-0.001, 0.0) == (-1, 0)
    assert area.tile_index(256.0, 255.999) == (1, 0)
    assert area.tile_index(512.0, 300.0) == (1, 1)  # the area's far edges stay in the last tile
    assert area.tile_index(513.0, 0.0) is None
    assert tile_id(-1, 3) == "tile_x-1_y3"


def test_area_must_have_size() -> None:
    with pytest.raises(ValueError):
        BuildArea("test", BBox(0, 0, 0, 10), 256.0)


def test_area_bounds_are_whole_metres(fixture_config: MapConfig) -> None:
    bounds = build_area(fixture_config, "full").bounds
    assert all(float(v).is_integer() for v in bounds.as_list())
    world = fixture_config.projected_bbox
    assert bounds.min_x <= world.min_x - fixture_config.origin.x
    assert bounds.max_y >= world.max_y - fixture_config.origin.y
