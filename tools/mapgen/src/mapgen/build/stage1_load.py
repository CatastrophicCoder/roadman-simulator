"""Stage 1: load the enabled sources, reproject to the map CRS and subtract the origin.

Sources are picked by capability (`osm_vector`, `elevation`, `buildings_3d`,
`point_cloud`), never by name. Footprints are kept when their centroid is inside the build
area. Lidar points are read only around those footprints (their combined bounds plus
`POINT_MARGIN_M`), so the test area stays fast. Output in `01-load/`: `footprints.geojson`
(world coordinates), `dem.tif` (cropped) and `report.json`.
"""

from collections import Counter
from collections.abc import Callable, Mapping
from dataclasses import dataclass, field
from pathlib import Path
from typing import Any

import numpy as np
from shapely.geometry import MultiPolygon, Polygon

from mapgen.build.area import BuildArea, to_world
from mapgen.build.heightfield import Heightfield, heightfield_from_raster
from mapgen.build.lidar import LidarParams
from mapgen.build.stage_io import write_geojson, write_geotiff, write_json
from mapgen.config import MapConfig
from mapgen.geo import BBox, offset_geometry, reproject_geometry
from mapgen.sources.base import (
    BUILDINGS_3D,
    ELEVATION,
    OSM_VECTOR,
    POINT_CLOUD,
    ElevationRaster,
    Footprint,
    LoadContext,
    PointCloud,
    SkippedFeature,
    SourceData,
    SourceLoadError,
)
from mapgen.sources.registry import SelectedSource, enabled_sources_providing

DEM_MARGIN_M = 10.0  # elevation read around the area, so edge samples have neighbours
POINT_MARGIN_M = 2.0  # lidar points read around the in-area footprints' combined bounds


class BuildError(Exception):
    """The build cannot continue. The message is shown to the user."""


@dataclass(frozen=True)
class LocalFootprint:
    """A footprint in local coordinates (map CRS minus the origin)."""

    id: str
    geometry: Polygon | MultiPolygon
    tags: Mapping[str, str] = field(default_factory=dict)
    measured_height_m: float | None = None


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

    def load(source: SelectedSource) -> SourceData:
        return load_bounded(source, read_bounds)

    def load_bounded(source: SelectedSource, bounds: BBox) -> SourceData:
        ctx = LoadContext(
            map_config=config,
            source_name=source.name,
            options=source.options,
            raw_dir=raw_dir,
            target_crs=config.crs,
            bounds=bounds,
            log=log,
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
    if vector_source is not None:
        vector = load(vector_source)
        files += vector.files
        skipped += vector.skipped
        partial += vector.partial
        all_footprints = [to_local(f, config) for f in vector.footprints]
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

    points: PointCloud | None = None
    lidar_params: LidarParams | None = None
    point_bounds: BBox | None = None
    if point_source is not None:
        lidar_params = LidarParams.from_options(point_source.options)
        points = PointCloud.empty(config.crs)
        point_bounds = points_read_bounds(in_area, config)
        if point_bounds is not None:
            data_points = load_bounded(point_source, point_bounds)
            files += data_points.files
            if data_points.points is None:
                raise BuildError(f"source {point_source.name!r} returned no point cloud")
            points = data_points.points.offset(-config.origin.x, -config.origin.y)
        log(f"[{point_source.name}] {len(points)} lidar points around {len(in_area)} footprints")
    reasons = Counter(s.reason.split(":")[0] for s in skipped)
    report: dict[str, Any] = {
        "area": area.name,
        "bounds_local": area.bounds.as_list(),
        "sources": {
            "elevation": elevation_source.name,
            "osm_vector": vector_source.name if vector_source else None,
            "buildings_3d": buildings_3d_source.name if buildings_3d_source else None,
            "point_cloud": point_source.name if point_source else None,
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
        "point_cloud": {
            "source": point_source.name if point_source else None,
            "points": len(points) if points is not None else 0,
            "bounds_world": point_bounds.as_list() if point_bounds else None,
            "params": lidar_params.as_dict() if lidar_params else None,
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
    )


def points_read_bounds(
    footprints: list[LocalFootprint], config: MapConfig, margin_m: float = POINT_MARGIN_M
) -> BBox | None:
    """World bounds to read lidar points for `footprints`: their combined bounds plus a margin.

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
