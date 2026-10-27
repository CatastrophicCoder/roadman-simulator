"""Checks shared by the fixture build and the raw-data build tests."""

import json
from dataclasses import dataclass
from pathlib import Path

import jsonschema
import numpy as np
import shapely
from shapely.geometry import shape
from shapely.geometry.base import BaseGeometry

from conftest import mesh_height_at, read_tile
from mapgen.build.export_glb import TERRAIN_NODE
from mapgen.build.pack_manifest import committed_schema

TOLERANCE = 1e-6


def footprint_samples(geometry: BaseGeometry, spacing: float = 1.0) -> np.ndarray:
    """Footprint vertices plus interior points on a `spacing` grid (world coordinates)."""
    polygons = [geometry] if geometry.geom_type == "Polygon" else list(geometry.geoms)
    points = [np.asarray(p.exterior.coords)[:, :2] for p in polygons]
    min_x, min_y, max_x, max_y = geometry.bounds
    gx, gy = np.meshgrid(np.arange(min_x, max_x, spacing), np.arange(min_y, max_y, spacing))
    grid = np.column_stack([gx.ravel(), gy.ravel()])
    points.append(grid[shapely.contains_xy(geometry, grid[:, 0], grid[:, 1])])
    return np.vstack(points)


@dataclass
class PackSummary:
    tile_count: int
    building_count: int


def check_pack(pack_dir: Path, work_dir: Path) -> PackSummary:
    manifest = json.loads((pack_dir / "manifest.json").read_text(encoding="utf-8"))
    jsonschema.validate(manifest, committed_schema())
    buildings = json.loads((pack_dir / "buildings.json").read_text(encoding="utf-8"))["buildings"]
    load_report = json.loads((work_dir / "01-load" / "report.json").read_text(encoding="utf-8"))
    features = json.loads(
        (work_dir / "03-buildings" / "buildings.geojson").read_text(encoding="utf-8")
    )["features"]
    origin = np.array([manifest["origin"]["x"], manifest["origin"]["y"]])
    geometries = {f["properties"]["id"]: shape(f["geometry"]) for f in features}
    by_node = {b["node"]: b for b in buildings}

    assert {p.name for p in (pack_dir / "tiles").iterdir()} == {
        Path(t["file"]).name for t in manifest["tiles"]
    }
    glb_buildings = 0
    for tile in manifest["tiles"]:
        meshes = read_tile(pack_dir / tile["file"])
        min_x, min_y, max_x, max_y = tile["bounds_local"]
        for name, (vertices, _) in meshes.nodes.items():
            assert np.all(np.isfinite(vertices)), f"{tile['id']}/{name} has NaN or inf"
        terrain_v, terrain_f = meshes.nodes[TERRAIN_NODE]
        assert terrain_v[:, 0].min() >= min_x - TOLERANCE
        assert terrain_v[:, 0].max() <= max_x + TOLERANCE
        assert terrain_v[:, 1].min() >= min_y - TOLERANCE
        assert terrain_v[:, 1].max() <= max_y + TOLERANCE
        names = meshes.buildings()
        colonly = {n for n in meshes.nodes if n.endswith("-colonly")}
        expected = {c for n in names for c in by_node[n]["collision_nodes"]}
        assert colonly == expected, f"{tile['id']}: collision nodes don't match buildings.json"
        assert len(names) == tile["building_count"]
        glb_buildings += len(names)
        for name in names:
            building = by_node[name]
            collisions = building["collision_nodes"]
            assert collisions, f"{name} has no -colonly partner"
            for collision in collisions:
                assert collision.startswith(name) and collision.endswith("-colonly")
                assert collision in meshes.nodes, f"{collision} missing from {tile['id']}"
            assert building["tile"] == tile["id"]
            world = geometries[building["id"]]
            cx, cy = world.centroid.x - origin[0], world.centroid.y - origin[1]
            assert min_x <= cx <= max_x and min_y <= cy <= max_y, f"{name} centroid off tile"
            # The base never floats: terrain under the footprint is at or above base_z.
            pts = footprint_samples(world) - origin
            inside = (
                (pts[:, 0] >= min_x)
                & (pts[:, 0] <= max_x)
                & (pts[:, 1] >= min_y)
                & (pts[:, 1] <= max_y)
            )
            z = mesh_height_at(terrain_v, terrain_f, pts[inside])
            z = z[~np.isnan(z)]
            assert np.all(z >= building["base_z"] - 1e-3), f"{name} floats above the terrain"
    assert load_report["footprints"]["in_area"] == glb_buildings == len(buildings)
    return PackSummary(len(manifest["tiles"]), glb_buildings)
