from pathlib import Path

import numpy as np
import pytest

from conftest import glb_primitive, read_tile
from mapgen.build.export_glb import (
    ExportError,
    MeshNode,
    export_glb,
    from_gltf,
    read_glb_node_names,
    to_gltf,
)

QUAD = np.array([[0.0, 0.0, 5.0], [10.0, 0.0, 5.0], [10.0, 20.0, 6.0], [0.0, 20.0, 6.0]])
QUAD_FACES = np.array([[0, 1, 2], [0, 2, 3]])


def test_axes_east_up_minus_north() -> None:
    assert to_gltf(np.array([[1.0, 2.0, 3.0]])).tolist() == [[1.0, 3.0, -2.0]]
    assert np.allclose(from_gltf(to_gltf(QUAD)), QUAD)


def test_names_survive_and_geometry_round_trips(tmp_path: Path) -> None:
    path = tmp_path / "t.glb"
    nodes = [
        MeshNode("terrain-col", QUAD, QUAD_FACES),
        MeshNode("bld_osm_way_1", QUAD + [0, 0, 1], QUAD_FACES),
        MeshNode("bld_osm_way_1-colonly", QUAD + [0, 0, 2], QUAD_FACES),
    ]
    export_glb(path, nodes)
    assert sorted(read_glb_node_names(path)) == sorted(n.name for n in nodes)
    meshes = read_tile(path)
    vertices, _ = meshes.nodes["terrain-col"]
    assert np.allclose(vertices, QUAD, atol=1e-5)
    assert meshes.buildings() == ["bld_osm_way_1"]


def test_normals_are_written_and_upward_faces_point_up(tmp_path: Path) -> None:
    path = export_glb(tmp_path / "t.glb", [MeshNode("terrain-col", QUAD, QUAD_FACES)])
    stored = glb_primitive(path, "terrain-col")
    normals, positions, triangles = stored["NORMAL"], stored["POSITION"], stored["indices"]
    assert np.allclose(np.linalg.norm(normals, axis=1), 1.0, atol=1e-6)
    assert np.all(normals[:, 1] > 0)  # stored normals point +Y (up)
    a, b, c = (positions[triangles[:, k]] for k in range(3))
    winding = np.cross(b - a, c - a)
    assert np.all(winding[:, 1] > 0)  # counter-clockwise seen from +Y


def test_given_normals_are_stored_in_gltf_axes(tmp_path: Path) -> None:
    tilted = np.tile([[0.6, 0.0, 0.8]], (4, 1))  # local: leaning east
    path = export_glb(tmp_path / "t.glb", [MeshNode("terrain-col", QUAD, QUAD_FACES, tilted)])
    assert np.allclose(glb_primitive(path, "terrain-col")["NORMAL"], [[0.6, 0.8, 0.0]] * 4)


def test_duplicate_names_are_rejected(tmp_path: Path) -> None:
    with pytest.raises(ExportError, match="duplicate"):
        export_glb(tmp_path / "t.glb", [MeshNode("a", QUAD, QUAD_FACES)] * 2)


def test_nan_vertices_are_rejected(tmp_path: Path) -> None:
    bad = QUAD.copy()
    bad[0, 2] = np.nan
    with pytest.raises(ExportError, match="NaN"):
        export_glb(tmp_path / "t.glb", [MeshNode("a", bad, QUAD_FACES)])
