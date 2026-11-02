from pathlib import Path

import numpy as np
import pytest

from conftest import glb_primitive, read_tile
from mapgen.build.export_glb import (
    ExportError,
    MeshNode,
    export_glb,
    flat_shaded,
    from_gltf,
    read_glb_json,
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
        MeshNode("bld_osm_way_1_c0-colonly", QUAD + [0, 0, 2], QUAD_FACES),
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


# Flat-shaded nodes (the world edge cliff): two triangles of one vertical quad and one
# of a sloped ledge, each with its own face normal.
FLAT = np.array(
    [
        [[0.0, 0.0, 0.0], [10.0, 0.0, 0.0], [10.0, 0.0, 5.0]],
        [[0.0, 0.0, 0.0], [10.0, 0.0, 5.0], [0.0, 0.0, 5.0]],
        [[0.0, 0.0, 5.0], [10.0, 0.0, 5.0], [10.0, 2.0, 5.5]],
    ]
)


def _face_normals(triangles: np.ndarray) -> np.ndarray:
    cross = np.cross(triangles[:, 1] - triangles[:, 0], triangles[:, 2] - triangles[:, 0])
    return cross / np.linalg.norm(cross, axis=1, keepdims=True)


def test_flat_shaded_nodes_share_no_vertices_and_keep_their_face_normals(tmp_path: Path) -> None:
    normals = _face_normals(FLAT)
    node = flat_shaded("edge_cliff-col", FLAT, normals, "edge_cliff")
    path = export_glb(tmp_path / "t.glb", [MeshNode("terrain-col", QUAD, QUAD_FACES), node])
    stored = glb_primitive(path, "edge_cliff-col")
    assert stored["indices"].ravel().tolist() == list(range(9))
    assert np.allclose(from_gltf(stored["POSITION"]), FLAT.reshape(-1, 3), atol=1e-6)
    assert np.array_equal(
        stored["NORMAL"], to_gltf(np.repeat(normals, 3, axis=0)).astype(np.float32)
    )
    assert "TEXCOORD_0" not in stored
    gltf = read_glb_json(path)
    (mesh_node,) = [n for n in gltf["nodes"] if n.get("name") == "edge_cliff-col"]
    primitive = gltf["meshes"][mesh_node["mesh"]]["primitives"][0]
    assert gltf["materials"][primitive["material"]]["name"] == "edge_cliff"


def test_flat_shaded_normals_are_written_as_given_not_recomputed(tmp_path: Path) -> None:
    """A normal that isn't the face's own (here: every face pointing east) is stored as
    it is: the exporter never recomputes given normals."""
    east = np.tile([1.0, 0.0, 0.0], (len(FLAT), 1))
    path = export_glb(tmp_path / "t.glb", [flat_shaded("edge_cliff-col", FLAT, east)])
    assert np.allclose(glb_primitive(path, "edge_cliff-col")["NORMAL"], [[1.0, 0.0, 0.0]] * 9)


def test_flat_shaded_needs_one_normal_per_triangle() -> None:
    with pytest.raises(ExportError, match="3 triangles but 2 normals"):
        flat_shaded("edge_cliff-col", FLAT, _face_normals(FLAT)[:2])


def test_invalid_given_normals_are_rejected(tmp_path: Path) -> None:
    bad = np.full((4, 3), np.nan)
    with pytest.raises(ExportError, match="invalid normals"):
        export_glb(tmp_path / "t.glb", [MeshNode("terrain-col", QUAD, QUAD_FACES, bad)])
    with pytest.raises(ExportError, match="invalid normals"):
        export_glb(tmp_path / "t.glb", [MeshNode("terrain-col", QUAD, QUAD_FACES, bad[:2])])
