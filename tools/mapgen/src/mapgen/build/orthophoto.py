"""Debug orthophoto textures: one JPEG per tile, resampled to exactly the tile's bounds.

Each tile's terrain has TEXCOORD_0 mapping its `bounds_local` onto [0, 1] x [0, 1] with
the image's top row at north (`terrain_uv`), so `debug/orthophoto/<tile_id>.jpg` covers
the tile exactly. The `debug/` folder holds an empty `.gdignore`, so Godot neither imports
nor exports it; the game loads the JPEGs from disk in debug runs. The manifest doesn't
list them (ADR 0006 amendment).
"""

import warnings
from collections.abc import Iterator, Sequence
from contextlib import ExitStack, contextmanager
from dataclasses import dataclass
from pathlib import Path

import numpy as np
import rasterio
from rasterio.enums import Resampling
from rasterio.errors import NotGeoreferencedWarning, RasterioError
from rasterio.io import DatasetReader, MemoryFile
from rasterio.merge import merge
from rasterio.vrt import WarpedVRT
from shapely.geometry import box
from shapely.ops import unary_union

from mapgen.build.area import Tile
from mapgen.geo import BBox
from mapgen.sources.base import Imagery
from mapgen.sources.mml import same_raster_crs

DEBUG_DIR = "debug"
ORTHOPHOTO_DIR = f"{DEBUG_DIR}/orthophoto"
GDIGNORE = ".gdignore"
JPEG_QUALITY = 85
RGB_BANDS = (1, 2, 3)


class OrthophotoError(Exception):
    """The orthophoto can't produce the debug textures. The message is shown to the user."""


def texture_path(tile_id: str) -> str:
    """A tile's texture path relative to the pack folder (the game's path convention)."""
    return f"{ORTHOPHOTO_DIR}/{tile_id}.jpg"


def texture_size(bounds: BBox, m_per_px: float) -> tuple[int, int]:
    """(width, height) in pixels: the tile's size divided by the resolution, rounded."""
    width = max(1, round(bounds.width / m_per_px))
    height = max(1, round(bounds.height / m_per_px))
    return width, height


def terrain_uv(vertices: np.ndarray, bounds: BBox) -> np.ndarray:
    """glTF TEXCOORD_0 (origin top-left) mapping `bounds` onto [0, 1] x [0, 1].

    u = (east - min_x) / width and v = (max_y - north) / height, so v = 0 is the north
    edge, the image's top row. `vertices` are local (east, north, up).
    """
    v = np.asarray(vertices, dtype=float)
    u_coord = (v[:, 0] - bounds.min_x) / bounds.width
    v_coord = (bounds.max_y - v[:, 1]) / bounds.height
    return np.column_stack([u_coord, v_coord])


@dataclass(frozen=True)
class TileTexture:
    tile_id: str
    world_bounds: BBox
    width: int
    height: int


class OrthophotoMosaic:
    """The imagery files opened once (warped when their CRS differs from the map CRS)."""

    def __init__(self, datasets: Sequence[DatasetReader | WarpedVRT], files: Sequence[Path]):
        self.datasets = list(datasets)
        self.files = list(files)
        self.coverage = unary_union([box(*d.bounds) for d in self.datasets])

    def covers(self, bounds: BBox) -> bool:
        return bool(self.coverage.covers(box(*bounds.as_list())))

    def read(self, bounds: BBox, width: int, height: int) -> np.ndarray:
        """RGB (3, height, width) uint8 covering exactly `bounds`, average resampling."""
        res = (bounds.width / width, bounds.height / height)
        data, _ = merge(
            self.datasets,
            bounds=tuple(bounds.as_list()),
            res=res,
            indexes=list(RGB_BANDS),
            resampling=Resampling.average,
        )
        if data.shape != (3, height, width):
            raise OrthophotoError(
                f"orthophoto read gave shape {data.shape}, expected {(3, height, width)}"
            )
        return data.astype(np.uint8, copy=False)


@contextmanager
def open_mosaic(imagery: Imagery, target_crs: str) -> Iterator[OrthophotoMosaic]:
    if not imagery.files:
        raise OrthophotoError("no orthophoto files overlap the build area")
    with ExitStack() as stack:
        datasets: list[DatasetReader | WarpedVRT] = []
        try:
            for path in imagery.files:
                src = stack.enter_context(rasterio.open(path))
                if src.crs is None:
                    raise OrthophotoError(f"{path} has no CRS")
                if src.count < len(RGB_BANDS):
                    raise OrthophotoError(f"{path} has {src.count} bands, expected RGB")
                t = src.transform
                if t.b != 0 or t.d != 0 or t.e >= 0:
                    raise OrthophotoError(f"{path}: only north-up rasters are supported")
                if same_raster_crs(src.crs, target_crs):
                    datasets.append(src)
                else:
                    vrt = WarpedVRT(src, crs=target_crs, resampling=Resampling.bilinear)
                    datasets.append(stack.enter_context(vrt))
        except RasterioError as exc:
            raise OrthophotoError(f"cannot read the orthophoto: {exc}") from exc
        yield OrthophotoMosaic(datasets, imagery.files)


def plan_textures(
    tiles: Sequence[Tile], origin: tuple[float, float], m_per_px: float
) -> list[TileTexture]:
    ox, oy = origin
    plans = []
    for tile in tiles:
        width, height = texture_size(tile.bounds, m_per_px)
        plans.append(TileTexture(tile.id, tile.bounds.offset(ox, oy), width, height))
    return plans


def check_coverage(mosaic: OrthophotoMosaic, plans: Sequence[TileTexture]) -> None:
    """Fail unless the files cover every tile completely."""
    uncovered = [p.tile_id for p in plans if not mosaic.covers(p.world_bounds)]
    if uncovered:
        raise OrthophotoError(
            f"the orthophoto files don't cover {len(uncovered)} tile(s) completely: "
            f"{', '.join(uncovered[:10])}; download the missing sheets or disable the source"
        )


def encode_jpeg(rgb: np.ndarray, quality: int = JPEG_QUALITY) -> bytes:
    """A (3, height, width) uint8 array as JPEG bytes (no georeferencing, no sidecars)."""
    _, height, width = rgb.shape
    with warnings.catch_warnings():
        warnings.simplefilter("ignore", NotGeoreferencedWarning)
        with MemoryFile() as memory:
            with memory.open(
                driver="JPEG",
                width=width,
                height=height,
                count=3,
                dtype="uint8",
                QUALITY=str(quality),
            ) as dst:
                dst.write(rgb)
            return bytes(memory.read())


def write_debug_textures(
    mosaic: OrthophotoMosaic, plans: Sequence[TileTexture], pack_dir: Path
) -> list[Path]:
    """Write `debug/.gdignore` and one JPEG per tile into `pack_dir`."""
    debug = pack_dir / DEBUG_DIR
    (pack_dir / ORTHOPHOTO_DIR).mkdir(parents=True, exist_ok=True)
    (debug / GDIGNORE).write_bytes(b"")
    written = []
    for plan in plans:
        try:
            rgb = mosaic.read(plan.world_bounds, plan.width, plan.height)
        except RasterioError as exc:
            raise OrthophotoError(f"{plan.tile_id}: cannot read the orthophoto: {exc}") from exc
        path = pack_dir / texture_path(plan.tile_id)
        path.write_bytes(encode_jpeg(rgb))
        written.append(path)
    return written
