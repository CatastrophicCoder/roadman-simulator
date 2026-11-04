"""The building collision checks (`check_collision_extent`, `check_collision_mesh`, ADR 0006
amendment 2026-11-04) on small hand-made meshes."""

import numpy as np
import pytest
import shapely
from shapely.geometry import MultiPolygon, Point, Polygon, box

from mapgen.build.buildings import collision_mesh, collision_parts, extrude
from mapgen.build.pack_checks import (
    COLLISION_EXTENT_M,
    PackCheckError,
    check_collision_extent,
    mesh_footprint,
)

BASE, TOP = 90.0, 96.0
L_SHAPE = Polygon([(20, 20), (40, 20), (40, 30), (30, 30), (30, 40), (20, 40)])
COURTYARD = Polygon([(0, 0), (10, 0), (10, 10), (0, 10)], [[(3, 3), (3, 6), (6, 6), (6, 3)]])
ROUND = Point(0, 0).buffer(8.0, quad_segs=16)  # 64 walls


def _collisions(
    geometry: Polygon | MultiPolygon, z0: float = BASE, z1: float = TOP
) -> list[tuple[str, np.ndarray, np.ndarray]]:
    return [
        (f"bld_test_c{k}-colonly", *collision_mesh(part, z0, z1))
        for k, part in enumerate(collision_parts(geometry))
    ]


def _check(
    footprint: Polygon | MultiPolygon, collisions: list[tuple[str, np.ndarray, np.ndarray]]
) -> None:
    check_collision_extent("bld_test", extrude(footprint, BASE, TOP), collisions)


def _walls(vertices: np.ndarray, faces: np.ndarray) -> np.ndarray:
    """Indices of the vertical (wall) triangles."""
    t = vertices[faces]
    normal = np.cross(t[:, 1] - t[:, 0], t[:, 2] - t[:, 0])
    return np.flatnonzero(np.abs(normal[:, 2]) < 1e-9)


# Passing meshes


@pytest.mark.parametrize("footprint", [L_SHAPE, COURTYARD, ROUND, box(0, 0, 4, 4)])
def test_extruded_footprint_passes(footprint: Polygon) -> None:
    _check(footprint, _collisions(footprint))


def test_multi_part_building_passes() -> None:
    two = MultiPolygon([box(0, 0, 4, 4), box(20, 0, 26, 3)])
    _check(two, _collisions(two))


def test_touching_parts_pass() -> None:
    """Two parts sharing a wall: each wall faces out of its own part."""
    two = MultiPolygon([box(0, 0, 4, 4), box(4, 0, 8, 4)])
    _check(two, _collisions(two))


def test_mesh_footprint_is_the_roof_outline_with_holes() -> None:
    footprint = mesh_footprint(*extrude(COURTYARD, BASE, TOP))
    assert footprint.equals(COURTYARD)


# Extent beyond the footprint


def test_old_minimum_rotated_rectangle_fails() -> None:
    """The box the pipeline used to write fills the L's empty corner: its corner (40, 40)
    is 10 m from the footprint."""
    with np.errstate(divide="ignore", invalid="ignore"):  # GEOS warns on axis-aligned edges
        rectangle = shapely.minimum_rotated_rectangle(L_SHAPE)
    with pytest.raises(PackCheckError, match=r"beyond the footprint of bld_test.*10\.000 m away"):
        _check(L_SHAPE, _collisions(rectangle))


def test_filled_courtyard_fails_with_the_true_distance() -> None:
    """Every vertex of the filled mesh is on the footprint; the courtyard's centre is
    1.5 m from it."""
    filled = Polygon(COURTYARD.exterior)
    with pytest.raises(PackCheckError, match=r"9\.000 m² outside, up to 1\.500 m away"):
        _check(COURTYARD, _collisions(filled))


@pytest.mark.parametrize(
    ("reach", "passes"),
    [(COLLISION_EXTENT_M - 0.01, True), (COLLISION_EXTENT_M + 0.01, False)],
)
def test_extent_tolerance_is_five_centimetres(reach: float, passes: bool) -> None:
    """A 1 cm wide spike (too little area to fail the outline comparison) reaching `reach`
    out of the footprint."""
    footprint = box(0, 0, 10, 6)
    spiked = footprint.union(box(4.995, 6, 5.005, 6 + reach))
    if passes:
        _check(footprint, _collisions(spiked))
    else:
        with pytest.raises(PackCheckError, match="more than 0.05 m beyond"):
            _check(footprint, _collisions(spiked))


# Outline and height


def test_shrunken_outline_fails() -> None:
    shrunk = L_SHAPE.buffer(-0.1, join_style="mitre")
    with pytest.raises(PackCheckError, match="collision outline differs from the footprint"):
        _check(L_SHAPE, _collisions(shrunk))


def test_missing_part_fails() -> None:
    two = MultiPolygon([box(0, 0, 4, 4), box(20, 0, 26, 3)])
    with pytest.raises(PackCheckError, match=r"differs from the footprint by 18\.000 m²"):
        _check(two, _collisions(two)[:1])


@pytest.mark.parametrize(("z0", "z1"), [(BASE, TOP - 0.5), (BASE, TOP + 0.5), (BASE + 0.5, TOP)])
def test_too_short_or_too_tall_fails(z0: float, z1: float) -> None:
    with pytest.raises(PackCheckError, match="collision spans z"):
        _check(L_SHAPE, _collisions(L_SHAPE, z0, z1))


# Orientation and closure


def test_one_flipped_wall_triangle_fails() -> None:
    vertices, faces = collision_mesh(ROUND, BASE, TOP)
    walls = _walls(vertices, faces)
    assert len(walls) == 128
    faces = faces.copy()
    faces[walls[37]] = faces[walls[37]][::-1]
    with pytest.raises(PackCheckError, match=r"1 wall triangle\(s\) facing into the building"):
        _check(ROUND, [("bld_test_c0-colonly", vertices, faces)])


def test_one_flipped_roof_triangle_fails() -> None:
    vertices, faces = collision_mesh(ROUND, BASE, TOP)
    faces = faces.copy()
    faces[0] = faces[0][::-1]  # the first triangles are the roof
    with pytest.raises(PackCheckError, match=r"1 horizontal triangle\(s\) not facing out"):
        _check(ROUND, [("bld_test_c0-colonly", vertices, faces)])


def test_all_faces_inward_fails() -> None:
    vertices, faces = collision_mesh(box(0, 0, 4, 4), BASE, TOP)
    with pytest.raises(PackCheckError, match="not facing out of the roof or the floor"):
        _check(box(0, 0, 4, 4), [("bld_test_c0-colonly", vertices, faces[:, ::-1])])


@pytest.mark.parametrize("count", [1, 2])
def test_missing_wall_fails(count: int) -> None:
    """One wall triangle, or a whole wall (both triangles of its quad), left out."""
    vertices, faces = collision_mesh(L_SHAPE, BASE, TOP)
    walls = _walls(vertices, faces)
    quad = walls[[0, len(walls) // 2]]  # `_walls` in buildings.py: first halves, then seconds
    keep = np.ones(len(faces), dtype=bool)
    keep[quad[:count]] = False
    with pytest.raises(PackCheckError, match="a missing or extra wall"):
        _check(L_SHAPE, [("bld_test_c0-colonly", vertices, faces[keep])])


def test_missing_floor_fails() -> None:
    vertices, faces = collision_mesh(L_SHAPE, BASE, TOP)
    t = vertices[faces]
    floor = np.isclose(t[:, :, 2], BASE).all(axis=1)
    with pytest.raises(PackCheckError, match="roof and floor outlines differ"):
        _check(L_SHAPE, [("bld_test_c0-colonly", vertices, faces[~floor])])


def test_stray_wall_outside_the_footprint_fails() -> None:
    wall = np.array([[20.0, 0, BASE], [21.0, 0, BASE], [21.0, 0, TOP], [20.0, 0, TOP]])
    roof_v, roof_f = collision_mesh(box(0, 0, 4, 4), BASE, TOP)
    vertices = np.vstack([roof_v, wall])
    faces = np.vstack([roof_f, np.array([[0, 1, 2], [0, 2, 3]]) + len(roof_v)])
    with pytest.raises(PackCheckError, match="a missing or extra wall"):
        _check(box(0, 0, 4, 4), [("bld_test_c0-colonly", vertices, faces)])


def test_degenerate_triangle_fails() -> None:
    vertices, faces = collision_mesh(box(0, 0, 4, 4), BASE, TOP)
    vertices = np.vstack([vertices, [[0.0, 0, BASE], [1.0, 0, BASE], [2.0, 0, BASE]]])
    faces = np.vstack([faces, [[len(vertices) - 3, len(vertices) - 2, len(vertices) - 1]]])
    with pytest.raises(PackCheckError, match="1 degenerate triangle"):
        _check(box(0, 0, 4, 4), [("bld_test_c0-colonly", vertices, faces)])
