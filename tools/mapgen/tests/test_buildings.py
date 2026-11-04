from collections import Counter

import numpy as np
import pytest
from shapely.geometry import MultiPolygon, Point, Polygon, box

from mapgen.build.area import BuildArea
from mapgen.build.buildings import (
    DEGENERATE_AREA_M2,
    collision_mesh,
    collision_parts,
    extrude,
    match_measured_heights,
    node_name,
    parse_height,
    parse_levels,
    resolve_buildings,
    resolve_height,
    triangle_areas,
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


def test_multi_part_buildings_get_one_collision_mesh_per_part() -> None:
    parts = collision_parts(MultiPolygon([box(0, 0, 4, 4), box(20, 0, 26, 3)]))
    assert len(parts) == 2
    meshes = [collision_mesh(part, 0.0, 2.0) for part in parts]
    assert [round(_signed_volume(*m), 6) for m in meshes] == [32.0, 36.0]
    for vertices, _ in meshes:  # no mesh spans the 16 m gap
        assert not ((vertices[:, 0] > 4) & (vertices[:, 0] < 20)).any()


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


def _edges_pair_up(vertices: np.ndarray, faces: np.ndarray) -> bool:
    """Closed and consistently wound: every directed edge (by position) has its reverse."""
    keys = [tuple(np.round(p, 9)) for p in vertices]
    edges = Counter((keys[f[i]], keys[f[(i + 1) % 3]]) for f in np.asarray(faces) for i in range(3))
    return all(edges[(b, a)] == n for (a, b), n in edges.items())


def _top_covers(vertices: np.ndarray, faces: np.ndarray, x: float, y: float) -> bool:
    t = vertices[faces]
    top = t[np.isclose(t[:, :, 2], vertices[:, 2].max()).all(axis=1)]
    return any(Polygon(tri[:, :2]).covers(Point(x, y)) for tri in top)


def test_courtyard_stays_open_in_the_collision_mesh() -> None:
    courtyard = Polygon([(0, 0), (10, 0), (10, 10), (0, 10)], [[(3, 3), (3, 6), (6, 6), (6, 3)]])
    vertices, faces = collision_mesh(courtyard, 2.0, 8.0)
    assert _signed_volume(vertices, faces) == pytest.approx(courtyard.area * 6.0)  # 91 m² x 6
    assert not _top_covers(vertices, faces, 4.5, 4.5)  # the courtyard is open
    assert _top_covers(vertices, faces, 1.0, 1.0)
    assert _edges_pair_up(vertices, faces)


def test_concave_footprint_collision_is_the_footprint_extruded() -> None:
    footprint = Polygon([(20, 20), (40, 20), (40, 30), (30, 30), (30, 40), (20, 40)])
    vertices, faces = collision_mesh(footprint, 0.0, 5.0)
    assert _signed_volume(vertices, faces) == pytest.approx(footprint.area * 5.0)
    assert not _top_covers(vertices, faces, 35.0, 35.0)  # the concave corner stays empty
    assert {tuple(p) for p in vertices[:, :2]} == set(footprint.exterior.coords)
    assert _edges_pair_up(vertices, faces)
    assert triangle_areas(vertices, faces).min() >= DEGENERATE_AREA_M2


def test_collision_mesh_is_the_visual_extrusion_of_one_part() -> None:
    part = Polygon([(0, 0), (8, 0), (8, 3), (5, 3), (5, 7), (0, 7)])
    visual_v, visual_f = extrude(part, 90.0, 96.5)
    vertices, faces = collision_mesh(part, 90.0, 96.5)
    assert np.array_equal(vertices, visual_v) and np.array_equal(faces, visual_f)


def test_collision_mesh_drops_degenerate_triangles(monkeypatch: pytest.MonkeyPatch) -> None:
    part = box(0, 0, 6, 4)
    vertices, faces = extrude(part, 0.0, 3.0)
    # A zero-area triangle (three points on the south wall's bottom edge) appended.
    sliver = np.array([[0.0, 0.0, 0.0], [2.0, 0.0, 0.0], [4.0, 0.0, 0.0]])
    padded = (np.vstack([vertices, sliver]), np.vstack([faces, [len(vertices) + np.arange(3)]]))
    monkeypatch.setattr("mapgen.build.buildings.extrude", lambda *_: padded)
    kept_v, kept_f = collision_mesh(part, 0.0, 3.0)
    assert len(kept_f) == len(faces)
    assert triangle_areas(kept_v, kept_f).min() >= DEGENERATE_AREA_M2
    assert _signed_volume(kept_v, kept_f) == pytest.approx(72.0)


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
