"""Stage 1: load the enabled sources, reproject to the map CRS and subtract the origin.

Sources are picked by capability (`osm_vector`, `elevation`, `buildings_3d`,
`point_cloud`, `imagery`, `building_use`), never by name. Footprints are kept when their
centroid is inside the build area. Points of interest, land use areas and building-use
records (e.g. MML Topographic Database buildings) are kept when they intersect it. Road
ways and water are moved to local coordinates whole (the roads stage clips them); road
ways keep their OSM node ids. Lidar points are read only around those footprints and
the in-area building-use records (their combined bounds plus `POINT_MARGIN_M`), so the
test area stays fast. With a building-use source, the ground points (class 2) around the
in-area records are read too, for the MML-only buildings' lidar contradiction check
(docs/specs/mml-only-buildings.md). Imagery files are only listed here (those overlapping
the area); the export reads them per tile for the debug textures. The world edge's water
(docs/specs/world-edge-pipeline.md) comes from a `water_areas` source when the map has
one (areas and lines meeting the area), else from the OSM water areas clipped to the
area and the OSM waterway lines; the OSM water relations left out are kept with what is
known of their location. Output in `01-load/`:
`footprints.geojson` (world coordinates), `dem.tif` (cropped) and `report.json`.
"""

import time
from collections import Counter
from collections.abc import Callable, Mapping, Sequence
from dataclasses import dataclass, field
from pathlib import Path
from typing import Any

import numpy as np
from shapely.geometry import LineString, MultiPolygon, Point, Polygon
from shapely.geometry.base import BaseGeometry

from mapgen.build.area import BuildArea, to_world
from mapgen.build.heightfield import Heightfield, heightfield_from_raster
from mapgen.build.lidar import LidarParams
from mapgen.build.stage_io import write_geojson, write_geotiff, write_json
from mapgen.config import MapConfig
from mapgen.geo import BBox, offset_geometry, reproject_geometry, reproject_xy
from mapgen.sources.base import (
    BUILDING_USE,
    BUILDINGS_3D,
    ELEVATION,
    IMAGERY,
    OSM_VECTOR,
    POINT_CLOUD,
    WATER_AREAS,
    ElevationRaster,
    Footprint,
    Imagery,
    LandUseArea,
    LoadContext,
    PoiFeature,
    PointCloud,
    RoadWay,
    SkippedFeature,
    SkippedWater,
    SourceData,
    SourceLoadError,
    UseClassRule,
    WaterArea,
    WaterLine,
)
from mapgen.sources.registry import SelectedSource, enabled_sources_providing

DEM_MARGIN_M = 10.0  # elevation read around the area, so edge samples have neighbours
POINT_MARGIN_M = 2.0  # lidar points read around the in-area footprints' combined bounds
GROUND_CLASS = 2  # ASPRS ground: read apart for the MML-only buildings' ground check


class BuildError(Exception):
    """The build cannot continue. The message is shown to the user."""


@dataclass(frozen=True)
class LocalFootprint:
    """A footprint in local coordinates (map CRS minus the origin)."""

    id: str
    geometry: Polygon | MultiPolygon
    tags: Mapping[str, str] = field(default_factory=dict)
    measured_height_m: float | None = None


@dataclass(frozen=True)
class LocalRoadWay:
    """A `highway` way in local coordinates: `xy[i]` (shape (n, 2)) is node `node_ids[i]`."""

    osm_id: int
    node_ids: tuple[int, ...]
    xy: np.ndarray
    tags: Mapping[str, str] = field(default_factory=dict)
    node_tags: Mapping[int, Mapping[str, str]] = field(default_factory=dict)


@dataclass(frozen=True)
class LocalWater:
    """A water area or waterway line in local coordinates. `id` is e.g. "osm:way/12"."""

    id: str
    geometry: Polygon | MultiPolygon | LineString
    tags: Mapping[str, str] = field(default_factory=dict)


@dataclass(frozen=True)
class LocalLeftOut:
    """A water feature the load stage left out, with its location in local coordinates
    when anything is known (the member ways that could be read), else None."""

    id: str
    reason: str
    geometry: BaseGeometry | None


@dataclass(frozen=True)
class LocalPoi:
    """A point of interest in local coordinates (a point or an area)."""

    id: str
    geometry: Point | Polygon | MultiPolygon
    tags: Mapping[str, str] = field(default_factory=dict)


@dataclass(frozen=True)
class LocalLandUse:
    """A land use area in local coordinates."""

    id: str
    geometry: Polygon | MultiPolygon
    tags: Mapping[str, str] = field(default_factory=dict)


@dataclass(frozen=True)
class LocalBuildingUse:
    """A building-use record (e.g. "mml:rakennus/123") in local coordinates."""

    id: str
    geometry: Polygon | MultiPolygon
    feature_class: int | None
    use_class: int | None
    rule: UseClassRule


@dataclass
class LoadResult:
    footprints: list[LocalFootprint]  # centroid inside the area
    buildings_3d: list[LocalFootprint]  # intersecting the area
    heightfield: Heightfield
    raster: ElevationRaster  # cropped, world coordinates
    files: list[Path]  # raw files read, for the credits
    report: dict[str, Any]
    # Non-ground lidar points around the footprints, local coordinates (z unchanged), and
    # the height thresholds from the point cloud source's options. None without a source.
    points: PointCloud | None = None
    lidar_source: str | None = None
    lidar_params: LidarParams | None = None
    # Imagery files overlapping the area (debug orthophoto textures). None without a source
    # or without files; the build then writes no `debug/` folder.
    imagery: Imagery | None = None
    # Ground-class lidar points over the area (local coordinates), for bridge deck heights.
    # None without a point cloud source or a `roads` block.
    deck_points: PointCloud | None = None
    # Road ways, water and the water relations left out, from the `osm_vector` source.
    roads: list[LocalRoadWay] = field(default_factory=list)
    roads_skipped: list[SkippedFeature] = field(default_factory=list)
    water_areas: list[LocalWater] = field(default_factory=list)
    water_lines: list[LocalWater] = field(default_factory=list)
    water_skipped: list[SkippedFeature] = field(default_factory=list)
    # Points of interest and land use intersecting the area, from the `osm_vector` source,
    # and the features that couldn't be built (with reasons).
    pois: list[LocalPoi] = field(default_factory=list)
    pois_skipped: list[SkippedFeature] = field(default_factory=list)
    landuse: list[LocalLandUse] = field(default_factory=list)
    landuse_skipped: list[SkippedFeature] = field(default_factory=list)
    # Building-use records intersecting the area; None without a `building_use` source.
    building_use: list[LocalBuildingUse] | None = None
    # Skipped building-use records whose geometry or envelope intersects the area, and
    # the skipped ones with no location at all (counted apart, never dropped).
    building_use_skipped: list[SkippedFeature] = field(default_factory=list)
    building_use_skipped_unlocated: list[SkippedFeature] = field(default_factory=list)
    building_use_source: str | None = None
    # Ground (class 2) lidar points around the in-area building-use records, local
    # coordinates. None without both a point cloud and a building-use source.
    ground_points: PointCloud | None = None
    # Seconds spent in parts of the load (e.g. "load_ground_points"); never in a pack file.
    timings_s: dict[str, float] = field(default_factory=dict)
    # The world edge's water: from the `water_areas` source (mode "water_areas"), else the
    # OSM water (mode "osm", areas clipped to the area), else none. Areas and lines meet
    # the area. `edge_water_source` is the map.yaml source name.
    edge_water_mode: str = "none"
    edge_water_source: str | None = None
    edge_water_areas: list[LocalWater] = field(default_factory=list)
    edge_water_lines: list[LocalWater] = field(default_factory=list)
    edge_water_skipped: list[SkippedFeature] = field(default_factory=list)  # unreadable
    # OSM water relations left out by the load stage (all of them, wherever they lie).
    water_left_out: list[LocalLeftOut] = field(default_factory=list)


def _geometry_to_local(geometry: BaseGeometry, crs: str, config: MapConfig) -> Any:
    world = reproject_geometry(geometry, crs, config.crs)
    return offset_geometry(world, -config.origin.x, -config.origin.y)


def poi_to_local(poi: PoiFeature, config: MapConfig) -> LocalPoi:
    return LocalPoi(poi.id, _geometry_to_local(poi.geometry, poi.crs, config), dict(poi.tags))


def landuse_to_local(area: LandUseArea, config: MapConfig) -> LocalLandUse:
    geometry = _geometry_to_local(area.geometry, area.crs, config)
    return LocalLandUse(area.id, geometry, dict(area.tags))


def road_to_local(way: RoadWay, config: MapConfig) -> LocalRoadWay:
    xy = reproject_xy(np.asarray(way.coords, dtype=float), way.crs, config.crs)
    xy -= np.array([config.origin.x, config.origin.y])
    return LocalRoadWay(way.osm_id, way.node_ids, xy, dict(way.tags), dict(way.node_tags))


def water_to_local(feature: WaterArea | WaterLine, config: MapConfig) -> LocalWater:
    world = reproject_geometry(feature.geometry, feature.crs, config.crs)
    local = offset_geometry(world, -config.origin.x, -config.origin.y)
    return LocalWater(feature.id, local, dict(feature.tags))


def _one(config: MapConfig, capability: str, required: bool) -> SelectedSource | None:
    sources = enabled_sources_providing(config, capability)
    if len(sources) > 1:
        names = ", ".join(s.name for s in sources)
        raise BuildError(f"more than one enabled source provides {capability}: {names}")
    if not sources and required:
        raise BuildError(f"no enabled source in map.yaml provides {capability}")
    return sources[0] if sources else None


def to_local(footprint: Footprint, config: MapConfig) -> LocalFootprint:
    world = reproject_geometry(footprint.geometry, footprint.crs, config.crs)
    local = offset_geometry(world, -config.origin.x, -config.origin.y)
    return LocalFootprint(footprint.id, local, dict(footprint.tags), footprint.measured_height_m)


def _footprint_properties(footprint: LocalFootprint) -> dict[str, Any]:
    tags = footprint.tags
    return {
        "id": footprint.id,
        "building": tags.get("building"),
        "height": tags.get("height"),
        "building:levels": tags.get("building:levels"),
        "measured_height_m": footprint.measured_height_m,
        "tags": dict(tags),
    }


def run_load(
    config: MapConfig,
    area: BuildArea,
    raw_dir: Path,
    out_dir: Path | None = None,
    log: Callable[[str], None] = print,
) -> LoadResult:
    """Load, reproject and crop everything the later stages need.

    Writes the stage output to `out_dir` when given.
    """
    world_area = to_world(area.bounds, config)
    m = DEM_MARGIN_M
    read_bounds = BBox(
        world_area.min_x - m, world_area.min_y - m, world_area.max_x + m, world_area.max_y + m
    )

    cache: dict[str, SourceData] = {}

    def load(source: SelectedSource) -> SourceData:
        # A source providing several capabilities (e.g. building_use and water_areas) is
        # read once.
        if source.name not in cache:
            cache[source.name] = load_bounded(source, read_bounds)
        return cache[source.name]

    def load_bounded(
        source: SelectedSource, bounds: BBox, classes: tuple[int, ...] | None = None
    ) -> SourceData:
        ctx = LoadContext(
            map_config=config,
            source_name=source.name,
            options=source.options,
            raw_dir=raw_dir,
            target_crs=config.crs,
            bounds=bounds,
            log=log,
            point_classes=classes,
        )
        try:
            return source.adapter.load(ctx)
        except SourceLoadError as exc:
            raise BuildError(str(exc)) from exc

    elevation_source = _one(config, ELEVATION, required=True)
    assert elevation_source is not None
    vector_source = _one(config, OSM_VECTOR, required=False)
    buildings_3d_source = _one(config, BUILDINGS_3D, required=False)
    point_source = _one(config, POINT_CLOUD, required=False)
    imagery_source = _one(config, IMAGERY, required=False)
    building_use_source = _one(config, BUILDING_USE, required=False)
    water_source = _one(config, WATER_AREAS, required=False)

    files: list[Path] = []
    skipped: list[SkippedFeature] = []
    partial: list[SkippedFeature] = []

    elevation = load(elevation_source)
    files += elevation.files
    if elevation.elevation is None:
        raise BuildError(f"source {elevation_source.name!r} returned no elevation data")
    raster = elevation.elevation
    try:
        heightfield, filled = heightfield_from_raster(raster, config.origin.x, config.origin.y)
    except ValueError as exc:
        raise BuildError(f"elevation from {elevation_source.name!r}: {exc}") from exc

    all_footprints: list[LocalFootprint] = []
    roads: list[LocalRoadWay] = []
    roads_skipped: list[SkippedFeature] = []
    water_areas: list[LocalWater] = []
    water_lines: list[LocalWater] = []
    water_skipped: list[SkippedFeature] = []
    pois: list[LocalPoi] = []
    pois_skipped: list[SkippedFeature] = []
    landuse: list[LocalLandUse] = []
    landuse_skipped: list[SkippedFeature] = []
    area_box = _area_polygon(area)
    if vector_source is not None:
        vector = load(vector_source)
        files += vector.files
        skipped += vector.skipped
        partial += vector.partial
        all_footprints = [to_local(f, config) for f in vector.footprints]
        roads = [road_to_local(r, config) for r in vector.roads]
        roads_skipped = list(vector.roads_skipped)
        water_areas = [water_to_local(w, config) for w in vector.water_areas]
        water_lines = [water_to_local(w, config) for w in vector.water_lines]
        water_skipped = list(vector.water_skipped)
        pois = [
            local
            for local in (poi_to_local(p, config) for p in vector.pois)
            if local.geometry.intersects(area_box)
        ]
        pois_skipped = list(vector.pois_skipped)
        landuse = [
            local
            for local in (landuse_to_local(a, config) for a in vector.landuse)
            if local.geometry.intersects(area_box)
        ]
        landuse_skipped = list(vector.landuse_skipped)
    else:
        log("no enabled source provides osm_vector; building no OSM footprints")

    buildings_3d: list[LocalFootprint] = []
    if buildings_3d_source is not None:
        data_3d = load(buildings_3d_source)
        files += data_3d.files
        skipped += data_3d.skipped
        partial += data_3d.partial
        buildings_3d = [
            local
            for local in (to_local(f, config) for f in data_3d.footprints)
            if local.geometry.intersects(_area_polygon(area))
        ]

    in_area = [f for f in all_footprints if _centroid_in(f, area)]

    building_use: list[LocalBuildingUse] | None = None
    building_use_skipped: list[SkippedFeature] = []
    building_use_unlocated: list[SkippedFeature] = []
    if building_use_source is not None:
        data_use = load(building_use_source)
        files += data_use.files
        building_use = []
        for record in data_use.building_use:
            geometry = _geometry_to_local(record.geometry, record.crs, config)
            if geometry.intersects(area_box):
                building_use.append(
                    LocalBuildingUse(
                        record.id, geometry, record.feature_class, record.use_class, record.rule
                    )
                )
        for skip in data_use.building_use_skipped:
            if skip.geometry is None or skip.crs is None:
                building_use_unlocated.append(skip)
            elif _geometry_to_local(skip.geometry, skip.crs, config).intersects(area_box):
                building_use_skipped.append(skip)
        log(
            f"[{building_use_source.name}] {len(building_use)} building-use records in the "
            f"area ({len(building_use_skipped)} skipped, {len(building_use_unlocated)} "
            "skipped without a location)"
        )
    else:
        log(
            "no enabled source provides building_use; building categories get no use class "
            "(mml_building and mml_overlap are null)"
        )

    edge_mode = "none"
    edge_source: str | None = None
    edge_areas: list[LocalWater] = []
    edge_lines: list[LocalWater] = []
    edge_skipped: list[SkippedFeature] = []
    if water_source is not None:
        data_water = load(water_source)
        files += [f for f in data_water.files if f not in files]
        edge_mode, edge_source = "water_areas", water_source.name
        edge_areas = [
            local
            for local in (water_to_local(w, config) for w in data_water.water_areas)
            if local.geometry.intersects(area_box)
        ]
        edge_lines = [
            local
            for local in (water_to_local(w, config) for w in data_water.water_lines)
            if local.geometry.intersects(area_box)
        ]
        edge_skipped = list(data_water.water_skipped)
    elif vector_source is not None:
        edge_mode, edge_source = "osm", vector_source.name
        edge_areas = [
            LocalWater(w.id, _polygonal(w.geometry.intersection(area_box)), dict(w.tags))
            for w in water_areas
            if w.geometry.intersects(area_box)
            and not w.geometry.intersection(area_box).is_empty
            and w.geometry.intersection(area_box).area > 0
        ]
        edge_lines = [w for w in water_lines if w.geometry.intersects(area_box)]
    left_out = [
        LocalLeftOut(
            skip.id,
            skip.reason,
            _geometry_to_local(skip.geometry, skip.crs, config)
            if isinstance(skip, SkippedWater) and skip.geometry is not None and skip.crs
            else None,
        )
        for skip in water_skipped
    ]

    points: PointCloud | None = None
    lidar_params: LidarParams | None = None
    # Imagery files overlapping the area (debug orthophoto textures). None without a source
    # or without files; the build then writes no `debug/` folder.
    imagery: Imagery | None = None
    point_bounds: BBox | None = None
    deck_points: PointCloud | None = None
    deck_window: BBox | None = None
    ground_points: PointCloud | None = None
    ground_bounds: BBox | None = None
    timings: dict[str, float] = {}
    if point_source is not None:
        lidar_params = LidarParams.from_options(point_source.options)
        points = PointCloud.empty(config.crs)
        point_bounds = points_read_bounds([*in_area, *(building_use or [])], config)
        if point_bounds is not None:
            data_points = load_bounded(point_source, point_bounds)
            files += data_points.files
            if data_points.points is None:
                raise BuildError(f"source {point_source.name!r} returned no point cloud")
            points = data_points.points.offset(-config.origin.x, -config.origin.y)
        log(f"[{point_source.name}] {len(points)} lidar points around {len(in_area)} footprints")
        if building_use is not None:
            # Ground points for the MML-only buildings' lidar contradiction check.
            start = time.perf_counter()
            ground_points = PointCloud.empty(config.crs)
            ground_bounds = points_read_bounds(building_use, config)
            if ground_bounds is not None:
                data_ground_use = load_bounded(point_source, ground_bounds, (GROUND_CLASS,))
                files += [f for f in data_ground_use.files if f not in files]
                if data_ground_use.points is not None:
                    ground_points = data_ground_use.points.offset(
                        -config.origin.x, -config.origin.y
                    )
            seconds = time.perf_counter() - start
            timings["load_ground_points"] = round(seconds, 3)
            log(
                f"[{point_source.name}] {len(ground_points)} ground points around "
                f"{len(building_use)} building-use records ({seconds:.2f} s)"
            )
        deck_window = deck_points_bounds(roads, water_areas + water_lines, config, read_bounds)
        if config.roads is not None and deck_window is not None:
            # Deck surfaces are ground points in the lidar (the DEM has them removed): the
            # roads stage fits bridge deck heights to them. Read around every way that can
            # have or cross a deck (bridge, tunnel or layer tags, water), plus the search.
            classes = tuple(config.roads.bridge.lidar_fit.classes)
            data_ground = load_bounded(point_source, deck_window, classes)
            files += [f for f in data_ground.files if f not in files]
            if data_ground.points is not None:
                deck_points = data_ground.points.offset(-config.origin.x, -config.origin.y)
            count = len(deck_points) if deck_points is not None else 0
            log(f"[{point_source.name}] {count} points of classes {list(classes)} for decks")
    imagery: Imagery | None = None
    if imagery_source is not None:
        data_imagery = load_bounded(imagery_source, world_area)
        files += data_imagery.files
        imagery = data_imagery.imagery
        if imagery is not None:
            log(f"[{imagery_source.name}] {len(imagery.files)} orthophoto file(s) for the area")
    else:
        log("no enabled source provides imagery; building without debug orthophoto textures")

    reasons = Counter(s.reason.split(":")[0] for s in skipped)
    report: dict[str, Any] = {
        "area": area.name,
        "bounds_local": area.bounds.as_list(),
        "sources": {
            "elevation": elevation_source.name,
            "osm_vector": vector_source.name if vector_source else None,
            "buildings_3d": buildings_3d_source.name if buildings_3d_source else None,
            "point_cloud": point_source.name if point_source else None,
            "imagery": imagery_source.name if imagery_source else None,
            "building_use": building_use_source.name if building_use_source else None,
        },
        "files_read": sorted(_rel(p, raw_dir) for p in files),
        "footprints": {
            "loaded": len(all_footprints),
            "in_area": len(in_area),
            "outside_area": len(all_footprints) - len(in_area),
            "skipped": len(skipped),
            "partial": len(partial),
        },
        "buildings_3d": {"in_area": len(buildings_3d)},
        "roads": {"highway_ways": len(roads), "unreadable": len(roads_skipped)},
        "water": {
            "areas": len(water_areas),
            "lines": len(water_lines),
            "relations_left_out": len(water_skipped),
        },
        "edge_water": {
            "mode": edge_mode,
            "source": edge_source,
            "areas": len(edge_areas),
            "lines": len(edge_lines),
            "skipped": [{"id": s.id, "reason": s.reason} for s in edge_skipped],
        },
        "pois": {"in_area": len(pois), "skipped": len(pois_skipped)},
        "landuse": {"in_area": len(landuse), "left_out": len(landuse_skipped)},
        "building_use": {
            "source": building_use_source.name if building_use_source else None,
            "in_area": len(building_use) if building_use is not None else 0,
            "skipped": len(building_use_skipped),
            "skipped_records": [{"id": s.id, "reason": s.reason} for s in building_use_skipped],
            "skipped_without_location": len(building_use_unlocated),
            "skipped_without_location_records": [
                {"id": s.id, "reason": s.reason} for s in building_use_unlocated
            ],
        },
        "point_cloud": {
            "source": point_source.name if point_source else None,
            "points": len(points) if points is not None else 0,
            "deck_points": len(deck_points) if deck_points is not None else 0,
            "ground_points": len(ground_points) if ground_points is not None else 0,
            "ground_points_bounds_world": ground_bounds.as_list() if ground_bounds else None,
            "deck_points_bounds_world": deck_window.as_list() if deck_window else None,
            "bounds_world": point_bounds.as_list() if point_bounds else None,
            "params": lidar_params.as_dict() if lidar_params else None,
        },
        "imagery": {
            "source": imagery_source.name if imagery_source else None,
            "files": sorted(_rel(p, raw_dir) for p in imagery.files) if imagery else [],
            "m_per_px": imagery.resolution_m if imagery else None,
        },
        "skipped_reasons": dict(sorted(reasons.items())),
        "skipped": [{"id": s.id, "reason": s.reason} for s in skipped],
        "partial": [{"id": p.id, "reason": p.reason} for p in partial],
        "dem": {
            "shape": list(raster.data.shape),
            "resolution_m": list(raster.resolution),
            "warped_from": list(raster.warped_from),
            "nodata_filled_cells": filled,
            "nodata_cells_in_area": _nodata_in_area(raster, world_area),
            "z_min": float(heightfield.z.min()),
            "z_max": float(heightfield.z.max()),
        },
    }

    if out_dir is not None:
        origin = (config.origin.x, config.origin.y)
        write_geojson(
            out_dir / "footprints.geojson",
            ((f.geometry, _footprint_properties(f)) for f in in_area),
            config.crs,
            origin,
        )
        write_geotiff(out_dir / "dem.tif", raster)
        write_json(out_dir / "report.json", report)

    return LoadResult(
        footprints=in_area,
        buildings_3d=buildings_3d,
        heightfield=heightfield,
        raster=raster,
        files=files,
        report=report,
        points=points,
        lidar_source=point_source.name if point_source else None,
        lidar_params=lidar_params,
        imagery=imagery,
        roads=roads,
        roads_skipped=roads_skipped,
        deck_points=deck_points,
        water_areas=water_areas,
        water_lines=water_lines,
        water_skipped=water_skipped,
        pois=pois,
        pois_skipped=pois_skipped,
        landuse=landuse,
        landuse_skipped=landuse_skipped,
        building_use=building_use,
        building_use_skipped=building_use_skipped,
        building_use_skipped_unlocated=building_use_unlocated,
        building_use_source=building_use_source.name if building_use_source else None,
        ground_points=ground_points,
        timings_s=timings,
        edge_water_mode=edge_mode,
        edge_water_source=edge_source,
        edge_water_areas=edge_areas,
        edge_water_lines=edge_lines,
        edge_water_skipped=edge_skipped,
        water_left_out=left_out,
    )


def deck_points_bounds(
    roads: list[LocalRoadWay], water: list[LocalWater], config: MapConfig, limit: BBox
) -> BBox | None:
    """World bounds of the lidar points for bridge decks: around the highway ways with a
    `bridge`, `tunnel` or `layer` tag and the water, plus `rim_search_m` and the widest
    road class. None when there is nothing that can carry or create a deck."""
    if config.roads is None:
        return None
    boxes = [
        r.xy
        for r in roads
        if any(r.tags.get(k) not in (None, "no") for k in ("bridge", "tunnel", "layer"))
    ]
    if water and roads:
        boxes += [np.asarray(w.geometry.bounds).reshape(2, 2) for w in water]
    if not boxes:
        return None
    pts = np.vstack(boxes)
    margin = (
        config.roads.bridge.rim_search_m + max(c.width_m for c in config.roads.classes.values())
        if config.roads.classes
        else config.roads.bridge.rim_search_m
    )
    local = BBox(
        float(pts[:, 0].min()) - margin,
        float(pts[:, 1].min()) - margin,
        float(pts[:, 0].max()) + margin,
        float(pts[:, 1].max()) + margin,
    )
    world = to_world(local, config)
    if not world.intersects(limit):
        return None
    return BBox(
        max(world.min_x, limit.min_x),
        max(world.min_y, limit.min_y),
        min(world.max_x, limit.max_x),
        min(world.max_y, limit.max_y),
    )


def points_read_bounds(
    footprints: Sequence[LocalFootprint | LocalBuildingUse],
    config: MapConfig,
    margin_m: float = POINT_MARGIN_M,
) -> BBox | None:
    """World bounds to read lidar points for `footprints` (footprints or building-use
    records): their combined bounds plus a margin.

    None when there are no footprints (then no point files are read).
    """
    if not footprints:
        return None
    bounds = np.array([f.geometry.bounds for f in footprints])
    local = BBox(
        float(bounds[:, 0].min()) - margin_m,
        float(bounds[:, 1].min()) - margin_m,
        float(bounds[:, 2].max()) + margin_m,
        float(bounds[:, 3].max()) + margin_m,
    )
    return to_world(local, config)


def _polygonal(geometry: BaseGeometry) -> Polygon | MultiPolygon:
    """The polygonal parts of a clipped area (a clip can leave lines and points)."""
    if isinstance(geometry, Polygon | MultiPolygon):
        return geometry
    parts: list[Polygon] = []
    for part in getattr(geometry, "geoms", []):
        if isinstance(part, Polygon) and part.area > 0:
            parts.append(part)
        elif isinstance(part, MultiPolygon):
            parts += [g for g in part.geoms if g.area > 0]
    return parts[0] if len(parts) == 1 else MultiPolygon(parts)


def _area_polygon(area: BuildArea) -> Polygon:
    b = area.bounds
    return Polygon([(b.min_x, b.min_y), (b.max_x, b.min_y), (b.max_x, b.max_y), (b.min_x, b.max_y)])


def _centroid_in(footprint: LocalFootprint, area: BuildArea) -> bool:
    centroid = footprint.geometry.centroid
    return area.contains(centroid.x, centroid.y)


def _rel(path: Path, root: Path) -> str:
    try:
        return path.relative_to(root).as_posix()
    except ValueError:
        return str(path)


def _nodata_in_area(raster: ElevationRaster, world_area: BBox) -> int:
    """NaN cells whose centre is inside the area (the rest are in the read margin)."""
    rows, cols = raster.data.shape
    t = raster.transform
    xs = t.c + (np.arange(cols) + 0.5) * t.a
    ys = t.f + (np.arange(rows) + 0.5) * t.e
    in_x = (xs >= world_area.min_x) & (xs <= world_area.max_x)
    in_y = (ys >= world_area.min_y) & (ys <= world_area.max_y)
    return int(np.isnan(raster.data[np.ix_(in_y, in_x)]).sum())
