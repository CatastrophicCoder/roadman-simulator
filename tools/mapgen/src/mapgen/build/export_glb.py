"""One `.glb` per tile, written with trimesh.

Axes (glTF is Y-up): local east -> +X, up -> +Y, north -> -Z. Node names carry Godot's
import suffixes: "terrain-col" (visual plus collision), "bld_<id>" (visual) and
"bld_<id>-colonly" (collision only). After writing, the node names are read back from
the file, because exporters may rename duplicates.
"""

import json
import struct
from collections.abc import Sequence
from dataclasses import dataclass
from pathlib import Path

import numpy as np
import trimesh

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
        mesh = trimesh.Trimesh(
            vertices=vertices, faces=node.faces, vertex_normals=normals, process=False
        )
        scene.add_geometry(mesh, node_name=node.name, geom_name=node.name)
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_bytes(scene.export(file_type="glb", include_normals=True))
    written = read_glb_node_names(path)
    if sorted(written) != sorted(names):
        missing = sorted(set(names) - set(written))[:5]
        extra = sorted(set(written) - set(names))[:5]
        raise ExportError(f"{path.name}: node names changed on export: {missing} -> {extra}")
    return path
