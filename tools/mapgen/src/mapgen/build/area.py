"""The build area and its tile grid, in local coordinates (metres from the map origin).

Tiles are aligned to the origin: tile (i, j) covers [i*T, (i+1)*T) x [j*T, (j+1)*T),
clipped to the area, and is named "tile_x{i}_y{j}".
"""

import math
from dataclasses import dataclass
from typing import Literal

from mapgen.config import MapConfig
from mapgen.geo import BBox

AreaName = Literal["full", "test"]
AREAS: tuple[AreaName, ...] = ("full", "test")


def tile_id(i: int, j: int) -> str:
    return f"tile_x{i}_y{j}"


@dataclass(frozen=True)
class Tile:
    i: int
    j: int
    bounds: BBox  # local coordinates, already clipped to the area

    @property
    def id(self) -> str:
        return tile_id(self.i, self.j)


@dataclass(frozen=True)
class BuildArea:
    """The part of the map to build, in local coordinates."""

    name: AreaName
    bounds: BBox
    tile_size: float

    def __post_init__(self) -> None:
        if self.tile_size <= 0:
            raise ValueError("tile_size must be positive")
        if self.bounds.width <= 0 or self.bounds.height <= 0:
            raise ValueError("the build area must have a positive size")

    def _index_range(self, lo: float, hi: float) -> range:
        return range(math.floor(lo / self.tile_size), math.ceil(hi / self.tile_size))

    def tiles(self) -> list[Tile]:
        """Every tile that overlaps the area with a positive size, ordered by (j, i)."""
        b, t = self.bounds, self.tile_size
        result: list[Tile] = []
        for j in self._index_range(b.min_y, b.max_y):
            for i in self._index_range(b.min_x, b.max_x):
                clipped = BBox(
                    max(b.min_x, i * t),
                    max(b.min_y, j * t),
                    min(b.max_x, (i + 1) * t),
                    min(b.max_y, (j + 1) * t),
                )
                if clipped.width > 0 and clipped.height > 0:
                    result.append(Tile(i, j, clipped))
        return result

    def contains(self, x: float, y: float) -> bool:
        return self.bounds.contains_point(x, y)

    def tile_index(self, x: float, y: float) -> tuple[int, int] | None:
        """The tile containing a point inside the area (the area's max edges count as inside)."""
        if not self.contains(x, y):
            return None
        xs = self._index_range(self.bounds.min_x, self.bounds.max_x)
        ys = self._index_range(self.bounds.min_y, self.bounds.max_y)
        i = min(max(math.floor(x / self.tile_size), xs.start), xs.stop - 1)
        j = min(max(math.floor(y / self.tile_size), ys.start), ys.stop - 1)
        return (i, j)


def build_area(config: MapConfig, name: AreaName) -> BuildArea:
    """The full map bbox or the test area, shifted to local coordinates.

    The local bounds are rounded outward to whole metres, so area and tile edges are
    exact in the 32-bit floats of the exported meshes.
    """
    world = config.projected_bbox if name == "full" else config.test_area_bbox
    ox, oy = config.origin.x, config.origin.y
    local = BBox(world.min_x - ox, world.min_y - oy, world.max_x - ox, world.max_y - oy)
    return BuildArea(name=name, bounds=local.rounded_outward(), tile_size=float(config.tile_size_m))


def to_world(bounds: BBox, config: MapConfig) -> BBox:
    ox, oy = config.origin.x, config.origin.y
    return BBox(bounds.min_x + ox, bounds.min_y + oy, bounds.max_x + ox, bounds.max_y + oy)
