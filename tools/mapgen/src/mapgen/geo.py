"""Coordinate helpers: WGS84 to the map CRS, projected bounding boxes, local coordinates.

All functions are pure. Coordinates are always (x, y) = (easting, northing) or
(lon, lat), because every transformer uses `always_xy=True`.
"""

import math
from dataclasses import dataclass
from functools import lru_cache

import numpy as np
import shapely
from pyproj import CRS, Transformer
from shapely.geometry.base import BaseGeometry

WGS84 = "EPSG:4326"


@dataclass(frozen=True)
class BBox:
    """An axis-aligned box in a projected CRS (metres)."""

    min_x: float
    min_y: float
    max_x: float
    max_y: float

    def __post_init__(self) -> None:
        if not (self.min_x <= self.max_x and self.min_y <= self.max_y):
            raise ValueError(f"invalid bbox: {self}")

    @property
    def width(self) -> float:
        return self.max_x - self.min_x

    @property
    def height(self) -> float:
        return self.max_y - self.min_y

    @property
    def centre(self) -> tuple[float, float]:
        return ((self.min_x + self.max_x) / 2, (self.min_y + self.max_y) / 2)

    def contains_point(self, x: float, y: float) -> bool:
        return self.min_x <= x <= self.max_x and self.min_y <= y <= self.max_y

    def contains(self, other: "BBox") -> bool:
        return self.contains_point(other.min_x, other.min_y) and self.contains_point(
            other.max_x, other.max_y
        )

    def rounded_outward(self) -> "BBox":
        """Round to whole units, never shrinking the box."""
        return BBox(
            math.floor(self.min_x),
            math.floor(self.min_y),
            math.ceil(self.max_x),
            math.ceil(self.max_y),
        )

    def as_list(self) -> list[float]:
        return [self.min_x, self.min_y, self.max_x, self.max_y]


@lru_cache(maxsize=32)
def _transformer(src: str, dst: str) -> Transformer:
    return Transformer.from_crs(src, dst, always_xy=True)


def wgs84_to_map(lon: float, lat: float, crs: str) -> tuple[float, float]:
    x, y = _transformer(WGS84, crs).transform(lon, lat)
    return (float(x), float(y))


def map_to_wgs84(x: float, y: float, crs: str) -> tuple[float, float]:
    lon, lat = _transformer(crs, WGS84).transform(x, y)
    return (float(lon), float(lat))


def projected_bbox(
    lon_min: float,
    lat_min: float,
    lon_max: float,
    lat_max: float,
    crs: str,
    densify: int = 64,
) -> BBox:
    """Envelope in `crs` of the WGS84 box edges, each sampled at `densify` + 1 points."""
    if densify < 1:
        raise ValueError("densify must be at least 1")
    xs: list[float] = []
    ys: list[float] = []
    for i in range(densify + 1):
        f = i / densify
        lon = lon_min + (lon_max - lon_min) * f
        lat = lat_min + (lat_max - lat_min) * f
        for p_lon, p_lat in ((lon, lat_min), (lon, lat_max), (lon_min, lat), (lon_max, lat)):
            x, y = wgs84_to_map(p_lon, p_lat, crs)
            xs.append(x)
            ys.append(y)
    return BBox(min(xs), min(ys), max(xs), max(ys))


def bbox_centre_in_map(
    lon_min: float, lat_min: float, lon_max: float, lat_max: float, crs: str
) -> tuple[float, float]:
    """The centre of the WGS84 box, projected to `crs` (used to derive a map origin)."""
    return wgs84_to_map((lon_min + lon_max) / 2, (lat_min + lat_max) / 2, crs)


def world_to_local(x: float, y: float, origin_x: float, origin_y: float) -> tuple[float, float]:
    return (x - origin_x, y - origin_y)


def local_to_world(x: float, y: float, origin_x: float, origin_y: float) -> tuple[float, float]:
    return (x + origin_x, y + origin_y)


def square_around(centre_x: float, centre_y: float, size: float) -> BBox:
    """A square of side `size` centred on the given point (the map's test area)."""
    if size <= 0:
        raise ValueError("size must be positive")
    half = size / 2
    return BBox(centre_x - half, centre_y - half, centre_x + half, centre_y + half)


def same_crs(a: str, b: str) -> bool:
    return a == b or CRS.from_user_input(a) == CRS.from_user_input(b)


def reproject_geometry[G: BaseGeometry](geometry: G, src_crs: str, dst_crs: str) -> G:
    """`geometry` with its (x, y) coordinates transformed from `src_crs` to `dst_crs`."""
    if same_crs(src_crs, dst_crs):
        return geometry
    transformer = _transformer(src_crs, dst_crs)

    def apply(coords: np.ndarray) -> np.ndarray:
        x, y = transformer.transform(coords[:, 0], coords[:, 1])
        return np.column_stack([x, y])

    return shapely.transform(geometry, apply)


def offset_geometry[G: BaseGeometry](geometry: G, dx: float, dy: float) -> G:
    """`geometry` moved by (dx, dy), e.g. minus the origin to get local coordinates."""
    shift = np.array([dx, dy])
    return shapely.transform(geometry, lambda coords: coords + shift)


def crs_urn(crs: str) -> str:
    """The OGC URN of a CRS, e.g. "urn:ogc:def:crs:EPSG::3067" (for GeoJSON's legacy `crs`)."""
    authority = CRS.from_user_input(crs).to_authority()
    if authority is None:
        raise ValueError(f"CRS {crs!r} has no authority code")
    return f"urn:ogc:def:crs:{authority[0]}::{authority[1]}"
