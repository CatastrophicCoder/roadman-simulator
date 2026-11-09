"""`mapgen build`: run the pipeline stages in order and write the map pack.

Stage output goes to the work folder (`tools/mapgen/build/<map-id>/<area>/`), the pack
to `game/maps/<map-id>/` (or `--out`). Both areas write the same pack folder; the
manifest's `area` says which one it holds. The pack is written to a staging folder in the
work folder, outside the Godot project, and moved in with one rename on the same volume
only when complete and valid (`pack_io`). An optional
`maps/<map-id>/camera_presets.json` is validated first and copied unchanged into the pack.
With an imagery source and its files, the pack also gets `debug/orthophoto/<tile>.jpg`
per tile (plus `debug/.gdignore`); the terrain's UVs map each tile onto its texture.
A release build (`release=True`) skips the sources marked `debug_only` in map.yaml, so
the pack neither reads nor credits their files (no `debug/orthophoto/` for Somero).
An optional `maps/<map-id>/overrides/buildings.json` (category and name overrides) is
validated before the load stage. The places stage resolves each building's category,
names, address and POI links (`buildings.json` schema version 2) and writes `pois.json`.
With a `building_use` source, the buildings stage also adds the records no OSM building
matched, with the parts overlapping OSM buildings cut away (`mml_only`,
docs/specs/mml-only-buildings.md). `03-buildings/report.json` is written after the places
stage (with an empty `mml_only.road_overlaps`), so a failing roads stage still leaves it,
and rewritten after the roads stage with the added buildings standing on a road.
With a `roads` block in map.yaml, the roads stage (stages 4 and 7) adds `road-col`,
`path-col` and bridge deck nodes to the tiles and fills `road_graph.json`; without it,
the map has no road meshes and an empty (valid) `road_graph.json`.
`maps/<map-id>/markers.json` (the spawn area) is required and validated before the load
stage. The world edge (stage 10, docs/specs/world-edge-pipeline.md) runs after the roads
stage and writes `boundary.json`; buildings crossing the boundary (the build area
rectangle, or the test-area square) are dropped before the buildings stage and listed in
the reports. A full build of a map with a custom boundary polygon
(`world_edge.boundary_wgs84`) clips everything the build area clips by that polygon
instead: building and POI centroids, road ways, surfaces and the terrain mesh, which is
cut along it (`terrain/clip.py`). The tile grid stays the build area's, but a tile with no
terrain inside the polygon isn't written. A building the polygon crosses fails the build.
The cliff (`world_edge/cliff.py`) is built from `boundary.json`'s ring, roadblocks, fences
and cliff values and the written tiles' terrain edge: every written tile the boundary
passes through gets an `edge_cliff-col` node (ledge, face, seams and skirt) and every
written tile an `edge_bottom` cap, both flat-shaded with the material `edge_cliff`.
The minimap stage (`minimap.py`, docs/specs/minimap.md) runs after the world edge and
the cliff: it paints the background image over the written tiles from the land use, the
world edge's water and the buildings that go into `buildings.json`, writes
`11-minimap/report.json`, and the export writes `minimap.png`, `minimap.png.import` and
`minimap.json` into the pack.
"""

import shutil
import time
from collections.abc import Callable, Iterator, Sequence
from contextlib import ExitStack, contextmanager
from dataclasses import dataclass, field, replace
from datetime import datetime
from pathlib import Path
from typing import Any

import shapely
from shapely.geometry import Polygon

from mapgen import __version__
from mapgen.build.area import AreaName, BuildArea, Tile, build_area
from mapgen.build.buildings import BuildingsResult, match_measured_heights, resolve_buildings
from mapgen.build.camera_presets import (
    PRESETS_NAME,
    CameraPresetsError,
    read_camera_presets,
)
from mapgen.build.export_glb import AXES, TERRAIN_NODE, ExportError, MeshNode, export_glb
from mapgen.build.markers import MARKERS_NAME, MarkersError, read_markers
from mapgen.build.minimap import STAGE_DIR as MINIMAP_DIR
from mapgen.build.minimap import MinimapError, build_minimap, minimap_inputs, write_minimap
from mapgen.build.mml_only import (
    MmlOnlySelection,
    mml_only_report,
    road_overlaps,
    select_mml_only,
)
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
from mapgen.build.pack_io import (
    PackFolderError,
    check_pack_target,
    check_same_volume,
    keep_failed,
    staging_dir,
    swap_in,
)
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
from mapgen.build.places.matching import UseMatch, match_building_use
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
from mapgen.build.roads.surface import OffTerrainError
from mapgen.build.stage1_load import BuildError, LocalFootprint, run_load
from mapgen.build.stage_io import write_geojson, write_json
from mapgen.build.terrain import MeshTerrainBuilder, TerrainBuilder, TerrainChunk
from mapgen.build.terrain.clip import CutStats, clip_chunk
from mapgen.build.world_edge import (
    STAGE_DIR as WORLD_EDGE_DIR,
)
from mapgen.build.world_edge import (
    WorldEdgeError,
    boundary_for,
    build_world_edge,
)
from mapgen.build.world_edge.cliff import (
    Cliff,
    CliffError,
    Tiling,
    build_cliff,
    ring_items,
    ring_profile,
)
from mapgen.build.world_edge.model import BOUNDARY_NAME, BOUNDARY_SCHEMA, BoundaryError
from mapgen.build.world_edge.ring import Ring
from mapgen.build.world_edge.spawn import crossing_buildings
from mapgen.config import MapConfig
from mapgen.geo import BBox
from mapgen.manifest import load_entries
from mapgen.sources.base import isoformat_utc, utc_now

__all__ = ["BuildError", "BuildReport", "run_build"]

STAGE_DIRS = (
    "01-load",
    "02-terrain",
    "03-buildings",
    "04-roads",
    "07-road-graph",
    "08-places",
    WORLD_EDGE_DIR,
    MINIMAP_DIR,
)


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
    mml_only_added: int = 0  # buildings from building-use records no OSM building matched
    dropped_buildings: int = 0  # buildings crossing the boundary, left out
    roadblocks: int = 0
    fences: int = 0
    water_crossings: int = 0
    minimap_size: tuple[int, int] | None = None  # minimap.png width, height in pixels


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
    stages too. Its `markers.json` (the spawn area) is required, so a build without
    `map_dir` fails. With `release`, the `debug_only` sources are treated as disabled,
    even when their raw files exist.
    """
    if release:
        for name in config.debug_only_sources():
            log(f"[{name}] skipped (debug only)")
        config = config.for_release()
    builder = terrain_builder or MeshTerrainBuilder()
    try:
        check_pack_target(out_dir)  # fail before the slow stages, and again before the swap
        check_same_volume(work_dir, out_dir)  # the swap must be a rename
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
    try:
        boundary = boundary_for(config, area)
    except WorldEdgeError as exc:
        raise BuildError(str(exc)) from exc
    if boundary.source == "custom":
        # Everything the area keeps is clipped by the polygon from here on.
        area = replace(area, boundary=boundary.polygon())
    if map_dir is None:
        raise BuildError(f"no map folder: the build needs maps/{config.id}/{MARKERS_NAME}")
    try:
        _, spawn = read_markers(map_dir / MARKERS_NAME, config)
    except MarkersError as exc:
        raise BuildError(str(exc)) from exc
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
    report.timings_s.update(loaded.timings_s)

    with stage("terrain"):
        chunks: dict[str, TerrainChunk] = {}
        stats = []
        written: list[Tile] = []
        cuts: list[tuple[str, CutStats]] = []
        for tile in tiles:
            chunk = builder.build_tile(loaded.heightfield, tile)
            if area.boundary is not None:
                chunk, cut = clip_chunk(chunk, area.boundary, loaded.heightfield)
                cuts.append((tile.id, cut))
                if len(chunk.faces) == 0:
                    continue  # no terrain inside the polygon: the tile isn't written
            written.append(tile)
            chunks[tile.id] = chunk
            stats.append(_terrain_stats(tile.id, chunk))
            export_glb(
                work_dir / "02-terrain" / f"{tile.id}.glb", [_terrain_node(chunk, tile.bounds)]
            )
        terrain_stats: dict[str, Any] = {"tiles": stats}
        if area.boundary is not None:
            terrain_stats["boundary_cut"] = _cut_report(cuts, {t.id for t in written})
            left_out = terrain_stats["boundary_cut"]["tiles_not_written"]
            totals = terrain_stats["boundary_cut"]["totals"]
            log(
                f"[terrain] cut along the boundary polygon: {totals['removed']} grid "
                f"triangles removed, {totals['cut']} cut into {totals['pieces']}; "
                f"{len(written)} of {len(tiles)} tiles written"
                + (f" ({', '.join(left_out)} outside)" if left_out else "")
            )
        write_json(work_dir / "02-terrain" / "stats.json", terrain_stats)
        tiles = written
        report.tile_count = len(tiles)

    with stage("buildings"):
        measured = match_measured_heights(loaded.footprints, loaded.buildings_3d)
        matches: dict[str, UseMatch] = (
            match_building_use(loaded.footprints, loaded.building_use)
            if loaded.building_use is not None
            else {}
        )
        selection = select_mml_only(
            loaded.building_use,
            matches,
            loaded.footprints,
            area,
            loaded.heightfield,
            points=loaded.points,
            ground_points=loaded.ground_points,
            source=loaded.building_use_source,
        )
        if loaded.building_use is None:
            log(
                "[buildings] no enabled source provides building_use: no MML-only "
                "buildings are added"
            )
        else:
            log(
                f"[buildings] {len(selection.added)} MML-only buildings added from "
                f"{len(selection.records)} unmatched building-use records"
            )
        added_footprints = selection.footprints()
        report.mml_only_added = len(added_footprints)
        # No building may cross the boundary: with the build area or test-area square as
        # the boundary, the crossing ones are left out; a custom polygon must be routed
        # around buildings, so a crossing fails the build (docs/specs/world-edge-pipeline.md).
        crossing = crossing_buildings(
            boundary.polygon(),
            [(f.id, f.geometry) for f in [*loaded.footprints, *added_footprints]],
            config.world_edge.building_clearance_m,
        )
        if boundary.source == "custom":
            crossing += _cut_by_polygon(boundary.polygon(), loaded.footprints_outside, selection)
            if crossing:
                raise BuildError(_crossing_message(crossing, config))
        dropped = [
            {
                "id": c["id"],
                "source": c["id"].split(":", 1)[0],
                "area_outside_m2": round(c["area_outside_m2"], 3),
                "distance_m": round(c["distance_m"], 3),
            }
            for c in crossing
        ]
        dropped_ids = {d["id"] for d in dropped}
        kept_osm = [f for f in loaded.footprints if f.id not in dropped_ids]
        added_footprints = [f for f in added_footprints if f.id not in dropped_ids]
        report.dropped_buildings = len(dropped)
        log(
            f"[buildings] {len(dropped)} buildings crossing the boundary left out "
            f"(clearance {config.world_edge.building_clearance_m:g} m)"
        )
        buildings = resolve_buildings(
            [*kept_osm, *added_footprints],
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
            [*kept_osm, *added_footprints],
            loaded,
            loaded.heightfield,
            overrides,
            _display(overrides_file, map_dir) if overrides is not None else None,
            matches=matches,
            mml_only={
                bid: record
                for bid, record in selection.records_by_id().items()
                if bid not in dropped_ids
            },
        )
        buildings = BuildingsResult(
            [replace(b, place=places.building_fields[b.id]) for b in buildings.buildings]
        )
        _write_buildings_geojson(work_dir / "03-buildings", buildings, config.crs, origin)
        _write_buildings_report(
            work_dir / "03-buildings", buildings, places, selection, [], dropped
        )
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
                    boundary=boundary.polygon(),
                )
            except (RoadGraphError, OffTerrainError) as exc:
                raise BuildError(str(exc)) from exc
        road_graph = roads.document
        report.roads = True
        report.road_edges = roads.edge_count
        report.road_nodes = roads.node_count
        report.suspected_missing_bridges = len(roads.report["suspected_missing_bridges"])
        report.clearance_warnings = len(roads.report["clearance_warnings"])
        report.drivable_share = roads.graph_report["weak_components"]["largest_share"]

    if roads is not None:
        added_ids = set(selection.records_by_id())
        overlaps = road_overlaps(
            ((b.id, b.footprint) for b in buildings.buildings if b.id in added_ids),
            roads.surface_footprints,
        )
        _write_buildings_report(
            work_dir / "03-buildings", buildings, places, selection, overlaps, dropped
        )

    with stage("world_edge"):
        try:
            world = build_world_edge(
                config,
                area,
                boundary,
                road_graph,
                roads.labelled_surfaces() if roads is not None else [],
                loaded,
                [(b.id, b.footprint) for b in buildings.buildings],
                spawn,
                work_dir,
                dropped_buildings=dropped,
                log=log,
            )
        except (WorldEdgeError, BoundaryError) as exc:
            raise BuildError(str(exc)) from exc
    report.roadblocks = len(world.document["roadblocks"])
    report.fences = len(world.document["fences"])
    report.water_crossings = len(world.document["water_crossings"])

    with stage("cliff"):
        cliff = _build_cliff(config, area, boundary.ring, world.document, tiles, chunks)
        world.report["cliff"] = cliff.report
        write_json(work_dir / WORLD_EDGE_DIR / "report.json", world.report)
        triangles = cliff.report["triangles"]
        log(
            f"[world-edge] cliff: {triangles['edge_cliff']} triangles in edge_cliff-col "
            f"(ledge {triangles['ledge']}, face {triangles['face']}, seam "
            f"{triangles['seam']}, skirt {triangles['skirt']}), {triangles['edge_bottom']} "
            f"in edge_bottom, {len(cliff.nodes)} tiles"
        )

    with stage("minimap"):
        minimap = build_minimap(
            config.minimap,
            [t.bounds for t in tiles],
            minimap_inputs(
                loaded, boundary.polygon(), [(b.id, b.footprint) for b in buildings.buildings]
            ),
            work_dir,
            log=log,
        )
    report.minimap_size = (minimap.grid.width, minimap.grid.height)

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

        staging = staging_dir(out_dir, work_dir)
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
                nodes += cliff.nodes.get(tile.id, [])
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
            BOUNDARY_SCHEMA.validate_file(write_json(staging / BOUNDARY_NAME, world.document))
            write_minimap(staging, minimap, config.id, area_name)
            if presets_bytes is not None:
                (staging / PRESETS_NAME).write_bytes(presets_bytes)  # unchanged
            validate_manifest_file(write_json(staging / MANIFEST_NAME, document))
            # Every check on the staged pack; a failure leaves the previous pack untouched.
            start = time.perf_counter()
            check_pack(staging, work_dir, config.roads, world_edge=config.world_edge)
            report.timings_s["pack_checks"] = round(time.perf_counter() - start, 3)
            log(f"[build] pack checks: {report.timings_s['pack_checks']:.2f} s")
            swap_in(staging, out_dir)
        except PackCheckError as exc:
            kept = _keep_failed_staging(staging, now, log)
            raise BuildError(f"pack check failed: {exc}{kept}") from exc
        except ExportError as exc:
            kept = _keep_failed_staging(staging, now, log)
            raise ExportError(f"{exc}{kept}") from exc
        except (
            ManifestError,
            PackFolderError,
            OrthophotoError,
            RoadGraphError,
            PlacesError,
            BoundaryError,
            MinimapError,
        ) as exc:
            raise BuildError(str(exc)) from exc
        finally:
            shutil.rmtree(staging, ignore_errors=True)

    write_json(work_dir / "timings.json", report.timings_s)
    return report


def _keep_failed_staging(
    staging: Path, now: Callable[[], datetime], log: Callable[[str], None]
) -> str:
    """Keep a staged pack that failed a check or the read-back (`keep_failed`); returns
    the text for the error message ("; staged pack kept in <path>"), or a note that it
    couldn't be kept (the staging folder is then deleted as usual)."""
    try:
        kept = keep_failed(staging, now())
    except OSError as exc:
        return f"; the staged pack couldn't be kept: {exc}"
    log(f"[build] failed staged pack kept in {kept}")
    return f"; staged pack kept in {kept}"


def _build_cliff(
    config: MapConfig,
    area: BuildArea,
    ring: Ring,
    document: dict[str, Any],
    tiles: Sequence[Tile],
    chunks: dict[str, TerrainChunk],
) -> Cliff:
    """The cliff ledge, face and bottom cap of every written tile, from `boundary.json`'s
    ring, roadblocks, fences and `cliff` values and the written tiles' terrain edge."""
    tiling = Tiling(
        float(config.tile_size_m),
        {t.id: t.bounds for t in area.tiles()},
        frozenset(t.id for t in tiles),
    )
    try:
        return build_cliff(
            ring,
            ring_items(document),
            document["cliff"]["ledge_m"],
            document["cliff"]["bottom_y"],
            ring_profile(ring, [chunks[t.id] for t in tiles]),
            tiling,
        )
    except CliffError as exc:
        raise BuildError(f"world edge cliff: {exc}") from exc


def _cut_report(cuts: Sequence[tuple[str, CutStats]], written: set[str]) -> dict[str, Any]:
    """`02-terrain/stats.json` `boundary_cut`: what cutting the terrain along the custom
    boundary polygon did, per tile and in total, and the tiles left out."""
    totals = {
        key: sum(c.as_dict()[key] for _, c in cuts) for key in CutStats(0, 0, 0, 0, 0).as_dict()
    }
    return {
        "totals": totals,
        "tiles_not_written": [tid for tid, _ in cuts if tid not in written],
        "tiles": [{"id": tid, **cut.as_dict()} for tid, cut in cuts],
    }


def _cut_by_polygon(
    polygon: Polygon,
    outside: Sequence[LocalFootprint],
    selection: MmlOnlySelection,
) -> list[dict[str, Any]]:
    """Buildings whose centroid lies outside a custom boundary polygon but whose
    footprint reaches inside it (intersection area > 0), which the polygon so cuts
    (docs/specs/world-edge-pipeline.md, amendments of 2026-11-02). A building wholly
    outside isn't in the pack at all, however close to the polygon, so
    `building_clearance_m` doesn't apply to it. Same fields as `crossing_buildings`:
    `distance_m` is the footprint's distance to the boundary line (0 when it crosses it)."""
    candidates = [(f.id, f.geometry) for f in outside]
    candidates += [
        (r.id, r.kept if r.kept is not None else r.record.geometry)
        for r in selection.records
        if r.outcome == "outside"
    ]
    line = polygon.exterior
    shapely.prepare(polygon)
    result = []
    for fid, geometry in candidates:
        if not (polygon.intersects(geometry) and float(geometry.intersection(polygon).area) > 0):
            continue
        distance = float(geometry.distance(line))
        result.append(
            {
                "id": fid,
                "area_outside_m2": float(geometry.difference(polygon).area),
                "distance_m": distance,
            }
        )
    return result


def _crossing_message(crossing: Sequence[dict[str, Any]], config: MapConfig) -> str:
    clearance = config.world_edge.building_clearance_m
    names = ", ".join(
        f"{c['id']} ({c['distance_m']:.3f} m from the boundary, "
        f"{c['area_outside_m2']:.1f} m² outside)"
        for c in crossing
    )
    return (
        f"world_edge.boundary_wgs84 crosses {len(crossing)} building(s): a building "
        f"with its centroid inside has part of its footprint outside the polygon or "
        f"within building_clearance_m ({clearance:g} m) of it, or one with its centroid "
        f"outside reaches inside it; route the polygon around them: {names}"
    )


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


def _write_buildings_report(
    folder: Path,
    buildings: BuildingsResult,
    places: PlacesResult,
    selection: MmlOnlySelection,
    overlaps: Sequence[str],
    dropped: Sequence[dict[str, Any]],
) -> None:
    """`03-buildings/report.json`: heights, place statistics, the `mml_only` block and the
    buildings left out for crossing the boundary (`world_edge.dropped_buildings`)."""
    write_json(
        folder / "report.json",
        {
            **buildings.report(),
            **places.building_report,
            "mml_only": mml_only_report(selection, buildings.buildings, overlaps),
            "world_edge": {"dropped_buildings": list(dropped)},
        },
    )


def _write_buildings_geojson(
    folder: Path,
    buildings: BuildingsResult,
    crs: str,
    origin: tuple[float, float],
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
