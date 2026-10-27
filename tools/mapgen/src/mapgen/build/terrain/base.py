"""The terrain builder interface, so the terrain approach (ADR 0004) can change later."""

from dataclasses import dataclass
from typing import Protocol

import numpy as np

from mapgen.build.area import Tile
from mapgen.build.heightfield import Heightfield


@dataclass(frozen=True)
class TerrainChunk:
    """A tile's terrain mesh in local coordinates (x east, y north, z up).

    Faces are counter-clockwise seen from above.
    """

    vertices: np.ndarray  # (n, 3) float
    faces: np.ndarray  # (m, 3) int
    normals: np.ndarray | None = None  # (n, 3) unit vertex normals, or None to compute them


class TerrainBuilder(Protocol):
    def build_tile(self, heightfield: Heightfield, tile: Tile) -> TerrainChunk:
        """The terrain of one tile (its bounds are already clipped to the build area)."""
        ...
