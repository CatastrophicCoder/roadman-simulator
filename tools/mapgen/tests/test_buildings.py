import numpy as np
import pytest
from shapely.geometry import MultiPolygon, Polygon, box

from mapgen.build.area import BuildArea
from mapgen.build.buildings import (
    collision_box,
    collision_boxes,
    extrude,
    match_measured_heights,
    node_name,
    parse_height,
    parse_levels,
    resolve_buildings,
    resolve_height,
)
from mapgen.build.heightfield import Heightfield
from mapgen.build.stage1_load import LocalFootprint
from mapgen.geo import BBox


@pytest.mark.parametrize(
    ("value", "expected"),
    [
        ("12", 12.0),
        ("12 m", 12.0),
        ("12.5", 12.5),
        (" 7.5m ", 7.5),
        ("0", None),
        ("-3", None),
        ("tall", None),
        ("12,5", None),
        ("12 ft", None),
        ("", None),
        (None, None),
    ],
)
def test_parse_height(value: str | None, expected: float | None) -> None:
    assert parse_height(value) == expected


def test_parse_levels() -> None:
    assert parse_levels("3") == 3.0
    assert parse_levels("0") is None
    assert parse_levels("three") is None


def test_height_priority_order() -> None:
    tags = {"height": "9", "building:levels": "5"}
    assert resolve_height(tags, measured_m=14.0).source == "mml_3d"
    assert resolve_height(tags).height_m == 9.0
    assert resolve_height(tags).source == "osm_height"
    levels = resolve_height({"building:levels": "5"})
    assert (levels.height_m, levels.source) == (15.0, "osm_levels")
    default = resolve_height({})
    assert (default.height_m, default.source) == (6.0, "default")


def test_invalid_tags_fall_through_and_are_reported() -> None:
    resolved = resolve_height({"height": "tall", "building:levels": "3"})
    assert (resolved.height_m, resolved.source) == (9.0, "osm_levels")
    assert resolved.problems == ("invalid height='tall'",)
    assert resolve_height({"height": "-1"}).source == "default"
    assert resolve_height({}, measured_m=float("nan")).source == "default"


def test_measured_heights_need_enough_overlap() -> None:
    footprints = [
        LocalFootprint("osm:way/1", box(0, 0, 10, 10)),
        LocalFootprint("osm:way/2", box(20, 0, 30, 10)),
    ]
    measured = [
        LocalFootprint("mml:building/a", box(1, 0, 11, 10), measured_height_m=13.0),
        LocalFootprint("mml:building/b", box(28, 0, 40, 10), measured_height_m=4.0),
    ]
    assert match_measured_heights(footprints, measured) == {"osm:way/1": 13.0}


def test_node_names() -> None:
    assert node_name("osm:way/123") == "bld_osm_way_123"
    assert node_name("osm:relation/45") == "bld_osm_relation_45"


def _signed_volume(vertices: np.ndarray, faces: np.ndarray) -> float:
    a, b, c = vertices[faces[:, 0]], vertices[faces[:, 1]], vertices[faces[:, 2]]
    return float(np.einsum("ij,ij->i", a, np.cross(b, c)).sum() / 6)


def test_extrude_is_closed_and_outward_with_holes() -> None:
    footprint = Polygon([(0, 0), (10, 0), (10, 10), (0, 10)], [[(3, 3), (3, 6), (6, 6), (6, 3)]])
    vertices, faces = extrude(footprint, 2.0, 8.0)
    assert vertices[:, 2].min() == 2.0 and vertices[:, 2].max() == 8.0
    # outward faces give a positive volume equal to area x height
    assert _signed_volume(vertices, faces) == pytest.approx(footprint.area * 6.0)


def test_extrude_clockwise_and_multipolygon_input() -> None:
    clockwise = Polygon([(0, 0), (0, 4), (4, 4), (4, 0)])
    assert _signed_volume(*extrude(clockwise, 0, 1)) == pytest.approx(16.0)
    two = MultiPolygon([box(0, 0, 1, 1), box(5, 5, 7, 7)])
    assert _signed_volume(*extrude(two, 0, 3)) == pytest.approx(15.0)


def test_multi_part_buildings_get_one_box_per_part() -> None:
    parts = MultiPolygon([box(0, 0, 4, 4), box(20, 0, 26, 3)])
    boxes = collision_boxes(parts)
    assert len(boxes) == 2
    assert [round(b.area, 6) for b in boxes] == [16.0, 18.0]  # no box spans the 16 m gap
    assert not any(b.intersects(box(5, 0, 19, 4)) for b in boxes)


def test_multi_part_collision_names_and_meshes() -> None:
    xs = np.arange(-10, 11) * 4.0
    gx, gy = np.meshgrid(xs, xs)
    hf = Heightfield(z=0 * gx, x0=-40.0, y0=-40.0, dx=4.0, dy=4.0)
    area = BuildArea("test", BBox(-40, -40, 40, 40), 32.0)
    two = LocalFootprint("osm:relation/5", MultiPolygon([box(0, 0, 4, 4), box(10, 0, 14, 4)]))
    one = LocalFootprint("osm:way/6", box(-20, -20, -10, -10))
    result = resolve_buildings([two, one], hf, area)
    by_id = {b.id: b for b in result.buildings}
    assert by_id["osm:relation/5"].collision_nodes == (
        "bld_osm_relation_5_c0-colonly",
        "bld_osm_relation_5_c1-colonly",
    )
    assert by_id["osm:way/6"].collision_nodes == ("bld_osm_way_6_c0-colonly",)
    meshes = by_id["osm:relation/5"].collision_meshes()
    assert [name for name, _, _ in meshes] == list(by_id["osm:relation/5"].collision_nodes)
    assert by_id["osm:relation/5"].provenance()["collision_nodes"] == [
        "bld_osm_relation_5_c0-colonly",
        "bld_osm_relation_5_c1-colonly",
    ]


def test_node_names_stay_unique() -> None:
    xs = np.arange(-10, 11) * 4.0
    gx, gy = np.meshgrid(xs, xs)
    hf = Heightfield(z=0 * gx, x0=-40.0, y0=-40.0, dx=4.0, dy=4.0)
    area = BuildArea("test", BBox(-40, -40, 40, 40), 32.0)
    clash = [
        LocalFootprint("osm:way/7", box(0, 0, 2, 2)),
        LocalFootprint("osm way 7", box(5, 5, 7, 7)),
    ]
    names = [b.node for b in resolve_buildings(clash, hf, area).buildings]
    assert sorted(names) == ["bld_osm_way_7", "bld_osm_way_7_2"]


def test_courtyards_and_concave_footprints_get_filled_boxes() -> None:
    courtyard = Polygon([(0, 0), (10, 0), (10, 10), (0, 10)], [[(3, 3), (3, 6), (6, 6), (6, 3)]])
    (rectangle,) = collision_boxes(courtyard)
    assert rectangle.area == pytest.approx(100.0)  # the courtyard is inside the box


def test_collision_box_covers_the_footprint() -> None:
    footprint = Polygon([(20, 20), (40, 20), (40, 30), (30, 30), (30, 40), (20, 40)])
    rectangle = collision_box(footprint)
    assert rectangle.covers(footprint.buffer(-1e-9))
    assert len(rectangle.exterior.coords) == 5


def test_resolve_buildings_assigns_tile_by_centroid_and_base_below_ground() -> None:
    xs = np.arange(-10, 11) * 4.0
    gx, gy = np.meshgrid(xs, xs)
    hf = Heightfield(z=50 + 0.1 * gx + 0.2 * gy, x0=-40.0, y0=-40.0, dx=4.0, dy=4.0)
    area = BuildArea("test", BBox(-40, -40, 40, 40), 32.0)
    straddling = LocalFootprint("osm:way/9", box(-2, 1, 10, 5), {"building:levels": "3"})
    result = resolve_buildings([straddling], hf, area)
    (building,) = result.buildings
    assert building.tile == "tile_x0_y0"
    assert building.node == "bld_osm_way_9"
    assert building.collision_nodes == ("bld_osm_way_9_c0-colonly",)
    corners = np.asarray(straddling.geometry.exterior.coords)
    assert building.base_z <= hf.sample(corners[:, 0], corners[:, 1]).min()
    assert building.provenance()["height_source"] == "osm_levels"
    assert result.report()["height_source_counts"] == {
        "mml_lidar": 0,
        "mml_3d": 0,
        "osm_height": 0,
        "osm_levels": 1,
        "default": 0,
    }
