"""Writing intermediate stage output: JSON, GeoJSON for QGIS, and GeoTIFF."""

import json
from collections.abc import Iterable, Mapping
from pathlib import Path
from typing import Any

import rasterio
from shapely.geometry import mapping
from shapely.geometry.base import BaseGeometry

from mapgen.geo import crs_urn, offset_geometry
from mapgen.sources.base import ElevationRaster


def write_json(path: Path, data: Any) -> Path:
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(json.dumps(data, indent=2, ensure_ascii=False) + "\n", encoding="utf-8")
    return path


def write_geojson(
    path: Path,
    features: Iterable[tuple[BaseGeometry, Mapping[str, Any]]],
    crs: str,
    origin: tuple[float, float],
) -> Path:
    """Local geometries written in world coordinates (origin added back).

    Includes the legacy `crs` member, so QGIS places the layer in the map CRS.
    """
    ox, oy = origin
    document = {
        "type": "FeatureCollection",
        "crs": {"type": "name", "properties": {"name": crs_urn(crs)}},
        "features": [
            {
                "type": "Feature",
                "geometry": mapping(offset_geometry(geometry, ox, oy)),
                "properties": dict(properties),
            }
            for geometry, properties in features
        ],
    }
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(json.dumps(document, ensure_ascii=False) + "\n", encoding="utf-8")
    return path


def write_geotiff(path: Path, raster: ElevationRaster) -> Path:
    path.parent.mkdir(parents=True, exist_ok=True)
    rows, cols = raster.data.shape
    with rasterio.open(
        path,
        "w",
        driver="GTiff",
        width=cols,
        height=rows,
        count=1,
        dtype="float32",
        crs=raster.crs,
        transform=raster.transform,
        nodata=float("nan"),
        compress="deflate",
    ) as dst:
        dst.write(raster.data.astype("float32"), 1)
    return path
