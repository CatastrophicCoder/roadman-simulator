"""One `.glb` per tile, written with trimesh.

Axes (glTF is Y-up): local east -> +X, up -> +Y, north -> -Z. Node names carry Godot's
import suffixes: "terrain-col" (visual plus collision), "bld_<id>" (visual),
"bld_<id>_c<k>-colonly" (collision only, k = 0.. per footprint part), and "road-col", "path-col" and
"bridge_osm_way_<id>-col" (road surfaces and bridge decks, visual plus collision). After
writing, the node names are read back from the file, because exporters may rename
duplicates. A node can carry TEXCOORD_0 (the terrain does; buildings don't) and a plain
named material (road surfaces and decks).
"""

import json
import struct
from collections.abc import Sequence
from dataclasses import dataclass
from pathlib import Path

import numpy as np
import trimesh
from trimesh.visual import TextureVisuals
from trimesh.visual.material import PBRMaterial

AXES = {"x": "east", "y": "up", "z": "-north"}
TERRAIN_NODE = "terrain-col"

GLB_MAGIC = b"glTF"
JSON_CHUNK = 0x4E4F534A


class ExportError(Exception):
    """A tile could not be exported as expected."""


@dataclass(frozen=True)
class MeshNode:
    """A named mesh in local coordinates (x east, y north, z up)."""

    name: str
    vertices: np.ndarray
    faces: np.ndarray
    normals: np.ndarray | None = None  # local unit normals; None: computed from the faces
    # TEXCOORD_0 in glTF convention (origin top-left, v down); None: no UVs written.
    uv: np.ndarray | None = None
    # Name of a plain glTF material (the game replaces it); None: no material unless UVs.
    material: str | None = None


def to_gltf(vertices: np.ndarray) -> np.ndarray:
    """Local (east, north, up) to glTF (X, Y, Z) = (east, up, -north). A rotation, so faces
    keep their winding."""
    v = np.asarray(vertices, dtype=float)
    return np.column_stack([v[:, 0], v[:, 2], -v[:, 1]])


def from_gltf(vertices: np.ndarray) -> np.ndarray:
    """glTF (X, Y, Z) back to local (east, north, up)."""
    v = np.asarray(vertices, dtype=float)
    return np.column_stack([v[:, 0], -v[:, 2], v[:, 1]])


def vertex_normals(vertices: np.ndarray, faces: np.ndarray) -> np.ndarray:
    """Area-weighted unit vertex normals, computed here so trimesh doesn't need scipy."""
    v = np.asarray(vertices, dtype=float)
    f = np.asarray(faces, dtype=np.int64)
    face_normals = np.cross(v[f[:, 1]] - v[f[:, 0]], v[f[:, 2]] - v[f[:, 0]])
    normals = np.zeros_like(v)
    for corner in range(3):
        np.add.at(normals, f[:, corner], face_normals)
    length = np.linalg.norm(normals, axis=1, keepdims=True)
    up = np.zeros_like(v)
    up[:, 1] = 1.0  # unused or degenerate vertices point up
    return np.where(length > 0, normals / np.where(length > 0, length, 1.0), up)


def read_glb_json(path: Path) -> dict:
    data = path.read_bytes()
    if len(data) < 20 or data[:4] != GLB_MAGIC:
        raise ExportError(f"{path} is not a GLB file")
    length, kind = struct.unpack_from("<II", data, 12)
    if kind != JSON_CHUNK:
        raise ExportError(f"{path}: first chunk is not JSON")
    return json.loads(data[20 : 20 + length])


def read_glb_node_names(path: Path) -> list[str]:
    return [str(node.get("name", "")) for node in read_glb_json(path).get("nodes", [])]


def export_glb(path: Path, nodes: Sequence[MeshNode]) -> Path:
    """Write the meshes as one GLB scene and check that every node name survived."""
    names = [node.name for node in nodes]
    if len(set(names)) != len(names):
        duplicates = sorted({n for n in names if names.count(n) > 1})
        raise ExportError(f"{path.name}: duplicate node names {duplicates[:5]}")
    scene = trimesh.Scene()
    for node in nodes:
        vertices = to_gltf(node.vertices)
        if not np.all(np.isfinite(vertices)):
            raise ExportError(f"{path.name}: node {node.name} has NaN or infinite vertices")
        normals = (
            vertex_normals(vertices, node.faces) if node.normals is None else to_gltf(node.normals)
        )
        visual = None
        if node.uv is not None:
            uv = np.asarray(node.uv, dtype=float)
            if uv.shape != (len(vertices), 2) or not np.all(np.isfinite(uv)):
                raise ExportError(f"{path.name}: node {node.name} has invalid UVs")
            # trimesh stores UVs with the origin bottom-left and flips v on glTF export.
            visual = TextureVisuals(
                uv=np.column_stack([uv[:, 0], 1.0 - uv[:, 1]]),
                material=PBRMaterial(name=node.name, baseColorFactor=[255, 255, 255, 255]),
            )
        elif node.material is not None:
            visual = TextureVisuals(
                material=PBRMaterial(name=node.material, baseColorFactor=[255, 255, 255, 255])
            )
        mesh = trimesh.Trimesh(
            vertices=vertices,
            faces=node.faces,
            vertex_normals=normals,
            visual=visual,
            process=False,
        )
        scene.add_geometry(mesh, node_name=node.name, geom_name=node.name)
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_bytes(scene.export(file_type="glb", include_normals=True))
    written = read_glb_node_names(path)
    if sorted(written) != sorted(names):
        missing = sorted(set(names) - set(written))[:5]
        extra = sorted(set(written) - set(names))[:5]
        raise ExportError(f"{path.name}: node names changed on export: {missing} -> {extra}")
    check_written(path, nodes)
    return path


_COMPONENTS = {5123: "<u2", 5125: "<u4", 5126: "<f4"}
_WIDTHS = {"SCALAR": 1, "VEC3": 3}


def check_written(path: Path, nodes: Sequence[MeshNode]) -> None:
    """Read each mesh's positions and indices back from the file and compare them with
    what was written (positions as float32 in glTF axes, indices exactly)."""
    data = path.read_bytes()
    json_len = struct.unpack_from("<I", data, 12)[0]
    gltf = json.loads(data[20 : 20 + json_len])
    bin_start = 20 + json_len + 8

    def accessor(index: int) -> np.ndarray:
        acc = gltf["accessors"][index]
        view = gltf["bufferViews"][acc["bufferView"]]
        width = _WIDTHS[acc["type"]]
        start = bin_start + view.get("byteOffset", 0) + acc.get("byteOffset", 0)
        array = np.frombuffer(
            data, dtype=_COMPONENTS[acc["componentType"]], count=acc["count"] * width, offset=start
        )
        return array.reshape(acc["count"], width) if width > 1 else array

    by_name = {n["name"]: n for n in gltf["nodes"] if "mesh" in n}
    for node in nodes:
        written = by_name[node.name]
        if any(k in written for k in ("matrix", "translation", "rotation", "scale")):
            raise ExportError(f"{path.name}: node {node.name} has a transform")
        primitive = gltf["meshes"][written["mesh"]]["primitives"][0]
        positions = accessor(primitive["attributes"]["POSITION"])
        indices = accessor(primitive["indices"]).reshape(-1, 3)
        expected = to_gltf(node.vertices).astype(np.float32)
        if positions.shape != expected.shape or not np.array_equal(positions, expected):
            raise ExportError(f"{path.name}: node {node.name}: positions differ when read back")
        faces = np.asarray(node.faces)
        if indices.shape != faces.shape or not np.array_equal(indices, faces):
            raise ExportError(f"{path.name}: node {node.name}: indices differ when read back")
