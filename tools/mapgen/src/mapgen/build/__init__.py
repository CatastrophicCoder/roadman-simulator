"""`mapgen build`: run the pipeline stages in order and write the map pack.

Stage output goes to the work folder (`tools/mapgen/build/<map-id>/<area>/`), the pack
to `game/maps/<map-id>/` (or `--out`). Both areas write the same pack folder; the
manifest's `area` says which one it holds. The pack is written to a temporary sibling
folder and swapped in only when complete and valid (`pack_io`). An optional
`maps/<map-id>/camera_presets.json` is validated first and copied unchanged into the pack.
With an imagery source and its files, the pack also gets `debug/orthophoto/<tile>.jpg`
per tile (plus `debug/.gdignore`); the terrain's UVs map each tile onto its texture.
A release build (`release=True`) skips the sources marked `debug_only` in map.yaml, so
the pack neither reads nor credits their files (no `debug/orthophoto/` for Somero).
An optional `maps/<map-id>/overrides/buildings.json` (category and name overrides) is
validated before the load stage. The places stage resolves each building's category,
names, address and POI links (`buildings.json` schema version 2) and writes `pois.json`.
With a `roads` block in map.yaml, the roads stage (stages 4 and 7) adds `road-col`,
`path-col` and bridge deck nodes to the tiles and fills `road_graph.json`; without it,
the map has no road meshes and an empty (valid) `road_graph.json`.
"""

import shutil
import time
from collections.abc import Callable, Iterator
from contextlib import ExitStack, contextmanager
from dataclasses import dataclass, field, replace
from datetime import datetime
from pathlib import Path
from typing import Any

from mapgen import __version__
from mapgen.build.area import AreaName, build_area
from mapgen.build.buildings import BuildingsResult, match_measured_heights, resolve_buildings
from mapgen.build.camera_presets import (
    PRESETS_NAME,
    CameraPresetsError,
    read_camera_presets,
)
from mapgen.build.export_glb import AXES, TERRAIN_NODE, MeshNode, export_glb
from mapgen.build.orthophoto import (
    ORTHOPHOTO_DIR,
    OrthophotoError,
    TileTexture,
    check_coverage,
    open_mosaic,
    plan_textures,
    terrain_uv,
    write_debug_textures,
)
from mapgen.build.pack_checks import PackCheckError, check_pack
from mapgen.build.pack_io import PackFolderError, check_pack_target, staging_dir, swap_in
from mapgen.build.pack_manifest import (
    BUILDINGS_NAME,
    HEIGHT_DATUM,
    MANIFEST_NAME,
    TILES_DIR,
    ManifestError,
    PackManifest,
    credits_for,
    validate_manifest_document,
    validate_manifest_file,
)
from mapgen.build.pack_manifest import SCHEMA_VERSION as MANIFEST_SCHEMA_VERSION
from mapgen.build.places import PlacesResult, build_places
from mapgen.build.places.model import (
    BUILDINGS_SCHEMA,
    BUILDINGS_SCHEMA_VERSION,
    POIS_NAME,
    POIS_SCHEMA,
    PlacesError,
)
from mapgen.build.places.overrides import (
    BuildingOverridesError,
    overrides_path,
    read_building_overrides,
)
from mapgen.build.roads import RoadsResult, build_roads
from mapgen.build.roads.model import ROAD_GRAPH_NAME, RoadGraphError, empty_graph_document
from mapgen.build.roads.model import validate_file as validate_road_graph_file
from mapgen.build.stage1_load import BuildError, run_load
from mapgen.build.stage_io import write_geojson, write_json
from mapgen.build.terrain import MeshTerrainBuilder, TerrainBuilder, TerrainChunk
from mapgen.config import MapConfig
from mapgen.geo import BBox
from mapgen.manifest import load_entries
from mapgen.sources.base import isoformat_utc, utc_now

__all__ = ["BuildError", "BuildReport", "run_build"]

STAGE_DIRS = ("01-load", "02-terrain", "03-buildings", "04-roads", "07-road-graph", "08-places")


@dataclass
class BuildReport:
    map_id: str
    area: AreaName
    pack_dir: Path
    work_dir: Path
    tile_count: int = 0
    building_count: int = 0
    height_source_counts: dict[str, int] = field(default_factory=dict)
    skipped: list[dict[str, str]] = field(default_factory=list)
    timings_s: dict[str, float] = field(default_factory=dict)
    debug_textures: int = 0  # debug orthophoto JPEGs written (0: no `debug/` folder)
    roads: bool = False  # the map has a `roads` block (else no road_graph.json)
    road_edges: int = 0
    road_nodes: int = 0
    suspected_missing_bridges: int = 0
    clearance_warnings: int = 0
    drivable_share: float | None = None  # largest weakly connected drivable component
    category_counts: dict[str, int] = field(default_factory=dict)
    category_source_counts: dict[str, int] = field(default_factory=dict)
    poi_count: int = 0


def _clear(folder: Path) -> None:
    if folder.is_dir():
        shutil.rmtree(folder)
    folder.mkdir(parents=True)


def _terrain_stats(tile_id: str, chunk: TerrainChunk) -> dict[str, Any]:
    z = chunk.vertices[:, 2]
    return {
        "id": tile_id,
        "vertices": int(len(chunk.vertices)),
        "faces": int(len(chunk.faces)),
        "z_min": float(z.min()),
        "z_max": float(z.max()),
    }


def run_build(
    config: MapConfig,
    area_name: AreaName,
    raw_dir: Path,
    out_dir: Path,
    work_dir: Path,
    *,
    map_dir: Path | None = None,
    terrain_builder: TerrainBuilder | None = None,
    now: Callable[[], datetime] = utc_now,
    log: Callable[[str], None] = print,
    release: bool = False,
) -> BuildReport:
    """Build the map pack for `config` from the raw files in `raw_dir`.

    `map_dir` is the map's config folder (`maps/<map-id>/`). Its optional
    `camera_presets.json` is validated before the slow stages and copied unchanged into
    the pack, and its optional `overrides/buildings.json` is validated before the slow
    stages too. Without `map_dir`, the pack has no camera presets and no overrides. With
    `release`, the `debug_only` sources are treated as disabled, even when their raw files
    exist.
    """
    if release:
        for name in config.debug_only_sources():
            log(f"[{name}] skipped (debug only)")
        config = config.for_release()
    builder = terrain_builder or MeshTerrainBuilder()
    try:
        check_pack_target(out_dir)  # fail before the slow stages, and again before the swap
    except PackFolderError as exc:
        raise BuildError(str(exc)) from exc
    try:
        presets_bytes = read_camera_presets(map_dir / PRESETS_NAME) if map_dir else None
    except CameraPresetsError as exc:
        raise BuildError(str(exc)) from exc
    overrides_file = overrides_path(map_dir) if map_dir else None
    try:
        overrides = read_building_overrides(overrides_file) if overrides_file else None
    except BuildingOverridesError as exc:
        raise BuildError(str(exc)) from exc
    area = build_area(config, area_name)
    tiles = area.tiles()
    report = BuildReport(config.id, area_name, out_dir, work_dir, tile_count=len(tiles))
    origin = (config.origin.x, config.origin.y)

    @contextmanager
    def stage(name: str) -> Iterator[None]:
        start = time.perf_counter()
        yield
        report.timings_s[name] = round(time.perf_counter() - start, 3)
        log(f"[build] {name}: {report.timings_s[name]:.2f} s")

    for name in STAGE_DIRS:
        _clear(work_dir / name)

    with stage("load"):
        loaded = run_load(config, area, raw_dir, work_dir / "01-load", log=log)
    report.skipped = list(loaded.report["skipped"])

    with stage("terrain"):
        chunks: dict[str, TerrainChunk] = {}
        stats = []
        for tile in tiles:
            chunk = builder.build_tile(loaded.heightfield, tile)
            chunks[tile.id] = chunk
            stats.append(_terrain_stats(tile.id, chunk))
            export_glb(
                work_dir / "02-terrain" / f"{tile.id}.glb", [_terrain_node(chunk, tile.bounds)]
            )
        write_json(work_dir / "02-terrain" / "stats.json", {"tiles": stats})

    with stage("buildings"):
        measured = match_measured_heights(loaded.footprints, loaded.buildings_3d)
        buildings = resolve_buildings(
            loaded.footprints,
            loaded.heightfield,
            area,
            measured,
            points=loaded.points,
            lidar_params=loaded.lidar_params,
        )

    with stage("places"):
        places = build_places(
            config.id,
            area,
            loaded.footprints,
            loaded,
            loaded.heightfield,
            overrides,
            _display(overrides_file, map_dir) if overrides is not None else None,
        )
        buildings = BuildingsResult(
            [replace(b, place=places.building_fields[b.id]) for b in buildings.buildings]
        )
        _write_buildings_stage(work_dir / "03-buildings", buildings, config.crs, origin, places)
        _write_places_stage(work_dir / "08-places", places, config.crs, origin)
    building_report = buildings.report()
    report.building_count = building_report["building_count"]
    report.height_source_counts = building_report["height_source_counts"]
    report.category_counts = places.building_report["category_counts"]
    report.category_source_counts = places.building_report["category_source_counts"]
    report.poi_count = len(places.pois_document["pois"])

    roads: RoadsResult | None = None
    road_graph = empty_graph_document(config.id, area_name)
    if config.roads is None:
        log("[build] map.yaml has no roads block: no road meshes, road_graph.json is empty")
    else:
        with stage("roads"):
            try:
                roads = build_roads(
                    config,
                    config.roads,
                    area,
                    tiles,
                    chunks,
                    loaded.heightfield,
                    loaded,
                    work_dir,
                    log=log,
                )
            except RoadGraphError as exc:
                raise BuildError(str(exc)) from exc
        road_graph = roads.document
        report.roads = True
        report.road_edges = roads.edge_count
        report.road_nodes = roads.node_count
        report.suspected_missing_bridges = len(roads.report["suspected_missing_bridges"])
        report.clearance_warnings = len(roads.report["clearance_warnings"])
        report.drivable_share = roads.graph_report["weak_components"]["largest_share"]

    with stage("export"), ExitStack() as resources:
        by_tile = buildings.by_tile()
        unknown = set(by_tile) - {t.id for t in tiles}
        if unknown:
            raise BuildError(f"buildings assigned to tiles outside the area: {sorted(unknown)}")
        tile_entries = [
            {
                "id": tile.id,
                "file": f"{TILES_DIR}/{tile.id}.glb",
                "bounds_local": tile.bounds.as_list(),
                "building_count": len(by_tile.get(tile.id, [])),
            }
            for tile in tiles
        ]
        # Everything that can fail on bad input is checked before the pack folder is touched.
        try:
            credits = credits_for(
                loaded.files, raw_dir, load_entries(raw_dir), extra=places.credits
            )
            manifest = PackManifest.model_validate(
                {
                    "schema_version": MANIFEST_SCHEMA_VERSION,
                    "map_id": config.id,
                    "display_name": config.display_name,
                    "area": area_name,
                    "crs": config.crs,
                    "origin": {"x": config.origin.x, "y": config.origin.y},
                    "height_datum": HEIGHT_DATUM,
                    "tile_size_m": config.tile_size_m,
                    "axes": AXES,
                    "tiles": tile_entries,
                    "credits": [c.model_dump() for c in credits],
                    "generated_at": isoformat_utc(now()),
                    "tool": {"name": "mapgen", "version": __version__},
                }
            )
            document = manifest.model_dump(mode="json")
            validate_manifest_document(document)
        except ManifestError as exc:
            raise BuildError(str(exc)) from exc

        mosaic = None
        plans: list[TileTexture] = []
        if loaded.imagery is None:
            log(f"[build] no orthophoto; the pack gets no {ORTHOPHOTO_DIR}/ textures")
        else:
            try:
                mosaic = resources.enter_context(open_mosaic(loaded.imagery, config.crs))
                plans = plan_textures(tiles, origin, loaded.imagery.resolution_m)
                check_coverage(mosaic, plans)
            except OrthophotoError as exc:
                raise BuildError(str(exc)) from exc

        staging = staging_dir(out_dir)
        try:
            for tile in tiles:
                chunk = chunks[tile.id]
                nodes = [_terrain_node(chunk, tile.bounds)]
                for building in by_tile.get(tile.id, []):
                    nodes.append(MeshNode(building.node, *building.visual_mesh()))
                    for name, vertices, faces in building.collision_meshes():
                        nodes.append(MeshNode(name, vertices, faces))
                if roads is not None:
                    nodes += roads.tile_nodes.get(tile.id, [])
                export_glb(staging / TILES_DIR / f"{tile.id}.glb", nodes)
            BUILDINGS_SCHEMA.validate_file(
                write_json(
                    staging / BUILDINGS_NAME,
                    {
                        "schema_version": BUILDINGS_SCHEMA_VERSION,
                        "map_id": config.id,
                        "buildings": [b.provenance() for b in buildings.buildings],
                    },
                )
            )
            POIS_SCHEMA.validate_file(write_json(staging / POIS_NAME, places.pois_document))
            if mosaic is not None:
                written = write_debug_textures(mosaic, plans, staging)
                report.debug_textures = len(written)
                log(f"[build] {len(written)} debug orthophoto textures in {ORTHOPHOTO_DIR}/")
            validate_road_graph_file(write_json(staging / ROAD_GRAPH_NAME, road_graph))
            if presets_bytes is not None:
                (staging / PRESETS_NAME).write_bytes(presets_bytes)  # unchanged
            validate_manifest_file(write_json(staging / MANIFEST_NAME, document))
            # Every check on the staged pack; a failure leaves the previous pack untouched.
            start = time.perf_counter()
            check_pack(staging, work_dir, config.roads)
            report.timings_s["pack_checks"] = round(time.perf_counter() - start, 3)
            log(f"[build] pack checks: {report.timings_s['pack_checks']:.2f} s")
            swap_in(staging, out_dir)
        except PackCheckError as exc:
            raise BuildError(f"pack check failed: {exc}") from exc
        except (
            ManifestError,
            PackFolderError,
            OrthophotoError,
            RoadGraphError,
            PlacesError,
        ) as exc:
            raise BuildError(str(exc)) from exc
        finally:
            shutil.rmtree(staging, ignore_errors=True)

    write_json(work_dir / "timings.json", report.timings_s)
    return report


def _terrain_node(chunk: TerrainChunk, bounds: BBox) -> MeshNode:
    """The terrain mesh node, with UVs mapping the tile's bounds onto its debug texture."""
    uv = terrain_uv(chunk.vertices, bounds)
    return MeshNode(TERRAIN_NODE, chunk.vertices, chunk.faces, chunk.normals, uv)


def _display(path: Path | None, map_dir: Path | None) -> str | None:
    """`path` relative to the repository (the parent of `maps/`) when possible."""
    if path is None:
        return None
    if map_dir is not None:
        try:
            return path.relative_to(map_dir.parent.parent).as_posix()
        except ValueError:
            pass
    return str(path)


def _write_places_stage(
    folder: Path, places: PlacesResult, crs: str, origin: tuple[float, float]
) -> None:
    write_geojson(folder / "pois.geojson", places.poi_features, crs, origin)
    write_json(folder / "report.json", places.places_report)


def _write_buildings_stage(
    folder: Path,
    buildings: BuildingsResult,
    crs: str,
    origin: tuple[float, float],
    places: PlacesResult,
) -> None:
    write_geojson(
        folder / "buildings.geojson",
        (
            (
                b.footprint,
                {
                    **b.provenance(),
                    "top_z": round(b.top_z, 3),
                    "height_tag": b.tags.get("height"),
                    "levels_tag": b.tags.get("building:levels"),
                },
            )
            for b in buildings.buildings
        ),
        crs,
        origin,
    )
    write_json(folder / "report.json", {**buildings.report(), **places.building_report})
