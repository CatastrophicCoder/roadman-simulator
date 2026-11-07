"""The minimap background image (docs/specs/minimap.md, "Pipeline: background image").

A top-down RGBA image of the build area in flat placeholder colours, which the game turns
under the roads, labels and symbols it draws live from the pack. Layers, painted in this
order (later ones cover earlier ones), every one clipped to the boundary ring:

1. outside the ring: fully transparent;
2. inside the ring: the ground colour;
3. `landuse` areas by group (`LANDUSE_GROUPS`; green land first, then built land, so a
   built area inside farmland shows). Values not in the table keep the ground colour and
   are counted by value in the log and the report;
4. water areas: the world edge's own (`world_edge.water.edge_water_areas`), so the two
   never disagree;
5. narrow streams: the water-area source's lines (MML `virtavesikapea`) in mode
   "water_areas" only, buffered by `minimap.stream_width_m / 2`, in the water colour;
6. building footprints: every building in `buildings.json` (OSM and MML-only).

The grid is the union of the written tiles' bounds, `minimap.m_per_px` metres per pixel,
anchored at its north-west corner (minimum x, minimum Godot z): the top row is north.
Pixels are burnt by the pixel-centre rule (`rasterio.features.rasterize`, `all_touched`
False) and written with GDAL's PNG driver without any metadata or sidecar file, so the
same input gives the same bytes.

Pack files: `minimap.png`, `minimap.json` (`minimap-v1.schema.json`) and
`minimap.png.import`, which tells Godot to keep the PNG as a plain file (it is exported
as is, and the game reads its bytes at runtime). Stage output: `11-minimap/report.json`.
After a model change, regenerate the committed schema with:

    uv run python -c "from mapgen.build.minimap import MINIMAP_SCHEMA; MINIMAP_SCHEMA.write()"
"""

import math
import warnings
from collections import Counter
from collections.abc import Callable, Sequence
from dataclasses import dataclass
from pathlib import Path
from typing import Any, Literal

import numpy as np
import rasterio
import shapely
from pydantic import BaseModel, ConfigDict, Field
from rasterio.errors import NotGeoreferencedWarning
from rasterio.features import rasterize
from rasterio.transform import Affine
from shapely.geometry import Polygon
from shapely.geometry.base import BaseGeometry

from mapgen.build.places.model import CommittedSchema
from mapgen.build.stage1_load import LoadResult
from mapgen.build.stage_io import write_json
from mapgen.build.world_edge.water import WaterFeature, edge_stream_lines, edge_water_areas
from mapgen.config import MinimapConfig
from mapgen.geo import BBox

STAGE_DIR = "11-minimap"
MINIMAP_PNG = "minimap.png"
MINIMAP_JSON = "minimap.json"
MINIMAP_IMPORT = "minimap.png.import"
MINIMAP_SCHEMA_VERSION = 1
# Godot keeps the PNG unimported and exports it unchanged; the game reads its bytes.
IMPORT_CONTENT = '[remap]\n\nimporter="keep"\n'

Rgba = tuple[int, int, int, int]

# Flat placeholder colours, in palette index order (index 0 is the transparent outside).
# The art-direction palette replaces them later through the style preset.
PALETTE: dict[str, Rgba] = {
    "outside": (0, 0, 0, 0),
    "ground": (218, 214, 196, 255),
    "built_land": (204, 196, 190, 255),
    "green_land": (190, 214, 160, 255),
    "water": (138, 182, 222, 255),
    "building": (156, 146, 140, 255),
}
_INDEX = {name: i for i, name in enumerate(PALETTE)}

# `landuse` values per colour group. Any other value keeps the ground colour.
LANDUSE_GROUPS: dict[str, str] = {
    # Built land
    "residential": "built_land",
    "retail": "built_land",
    "commercial": "built_land",
    "industrial": "built_land",
    "construction": "built_land",
    "garages": "built_land",
    "railway": "built_land",
    "education": "built_land",
    "institutional": "built_land",
    "religious": "built_land",
    "military": "built_land",
    "depot": "built_land",
    "port": "built_land",
    "farmyard": "built_land",
    "brownfield": "built_land",
    # Farmland and green land
    "farmland": "green_land",
    "meadow": "green_land",
    "orchard": "green_land",
    "grass": "green_land",
    "vineyard": "green_land",
    "allotments": "green_land",
    "plant_nursery": "green_land",
    "village_green": "green_land",
    "recreation_ground": "green_land",
    "greenfield": "green_land",
    "flowerbed": "green_land",
}
LANDUSE_ORDER = ("green_land", "built_land")  # painted in this order


class MinimapError(Exception):
    """A minimap document doesn't match its model or committed schema."""


# --- minimap.json ---------------------------------------------------------------------


class _StrictJsonModel(BaseModel):
    model_config = ConfigDict(extra="forbid", frozen=True, strict=True, allow_inf_nan=False)


class BoundsLocal(_StrictJsonModel):
    min_x: float
    max_x: float
    min_z: float = Field(description="north edge (Godot z = -north)")
    max_z: float = Field(description="south edge")


class MinimapDocument(_StrictJsonModel):
    schema_version: Literal[1]
    map_id: str
    area: Literal["full", "test"]
    image: Literal["minimap.png"]
    m_per_px: float = Field(gt=0, description="metres per pixel")
    width: int = Field(ge=1, description="pixels, west to east")
    height: int = Field(ge=1, description="pixels, north to south")
    bounds_local: BoundsLocal = Field(
        description="the image's outer pixel edges in Godot local x/z: "
        "max_x - min_x = width * m_per_px, max_z - min_z = height * m_per_px"
    )


MINIMAP_SCHEMA = CommittedSchema(MinimapDocument, "minimap-v1.schema.json", MinimapError)


# --- grid -----------------------------------------------------------------------------


@dataclass(frozen=True)
class MinimapGrid:
    """The image grid in local (x, n) metres: the top-left corner is (min_x, max_n)."""

    min_x: float
    max_n: float
    m_per_px: float
    width: int
    height: int

    @classmethod
    def covering(cls, bounds: Sequence[BBox], m_per_px: float) -> "MinimapGrid":
        """The grid over the union of `bounds` (the written tiles), from its north-west
        corner; the far edges are rounded up to whole pixels."""
        if not bounds:
            raise ValueError("no tiles: the minimap needs at least one")
        min_x = min(b.min_x for b in bounds)
        max_x = max(b.max_x for b in bounds)
        min_n = min(b.min_y for b in bounds)
        max_n = max(b.max_y for b in bounds)
        return cls(
            float(min_x),
            float(max_n),
            float(m_per_px),
            _pixels(max_x - min_x, m_per_px),
            _pixels(max_n - min_n, m_per_px),
        )

    @property
    def transform(self) -> Affine:
        """Pixel (col, row) to local (x, n)."""
        return Affine(self.m_per_px, 0.0, self.min_x, 0.0, -self.m_per_px, self.max_n)

    def bounds_local(self) -> dict[str, float]:
        """The outer pixel edges in Godot local x/z (z = -n; the top row is min_z)."""
        min_z = -self.max_n + 0.0
        return {
            "min_x": self.min_x,
            "max_x": self.min_x + self.width * self.m_per_px,
            "min_z": min_z,
            "max_z": min_z + self.height * self.m_per_px,
        }

    def pixel_centres(self) -> tuple[np.ndarray, np.ndarray]:
        """Local x and n of every pixel centre, each of shape (height, width)."""
        xs = self.min_x + (np.arange(self.width) + 0.5) * self.m_per_px
        ns = self.max_n - (np.arange(self.height) + 0.5) * self.m_per_px
        return np.meshgrid(xs, ns)


def _pixels(extent: float, m_per_px: float) -> int:
    """Whole pixels covering `extent` metres (a ratio within 1e-9 of an integer counts as
    that integer, so float noise never adds a column)."""
    ratio = extent / m_per_px
    return max(1, math.ceil(ratio - 1e-9))


# --- rendering ------------------------------------------------------------------------


@dataclass(frozen=True)
class LandUse:
    id: str
    value: str
    geometry: BaseGeometry


@dataclass(frozen=True)
class MinimapInputs:
    """Everything the image is painted from, in local (x, n) metres."""

    ring: Polygon
    landuse: Sequence[LandUse]
    water_areas: Sequence[WaterFeature]
    stream_lines: Sequence[WaterFeature]
    buildings: Sequence[tuple[str, BaseGeometry]]
    water_mode: str = "none"
    water_source: str | None = None


@dataclass(frozen=True)
class MinimapImage:
    grid: MinimapGrid
    classes: np.ndarray  # (height, width) palette indices
    report: dict[str, Any]

    def rgba(self) -> np.ndarray:
        """The image as (4, height, width) uint8 bands."""
        colours = np.array(list(PALETTE.values()), dtype=np.uint8)  # (n, 4)
        return np.moveaxis(colours[self.classes], -1, 0)


def minimap_inputs(
    loaded: LoadResult, ring: Polygon, buildings: Sequence[tuple[str, BaseGeometry]]
) -> MinimapInputs:
    """The inputs from the load stage: land use, the world edge's water areas and stream
    lines (the same feature objects the world-edge stage uses), and `buildings`, the
    footprints that go into `buildings.json`."""
    return MinimapInputs(
        ring=ring,
        landuse=[LandUse(a.id, a.tags.get("landuse", ""), a.geometry) for a in loaded.landuse],
        water_areas=edge_water_areas(loaded),
        stream_lines=edge_stream_lines(loaded),
        buildings=list(buildings),
        water_mode=loaded.edge_water_mode,
        water_source=loaded.edge_water_source,
    )


def _valid(geometry: BaseGeometry) -> BaseGeometry:
    return geometry if geometry.is_valid else shapely.make_valid(geometry)


def _clipped(geometry: BaseGeometry, ring: Polygon) -> BaseGeometry | None:
    """The polygonal part of `geometry` inside `ring`, or None when nothing is left."""
    geometry = _valid(geometry)
    if geometry.is_empty or not geometry.intersects(ring):
        return None
    parts = [
        p
        for p in shapely.get_parts(geometry.intersection(ring))
        if p.geom_type in ("Polygon", "MultiPolygon") and not p.is_empty and p.area > 0
    ]
    if not parts:
        return None
    return parts[0] if len(parts) == 1 else shapely.union_all(parts)


def _burn(
    out: np.ndarray, grid: MinimapGrid, geometries: Sequence[BaseGeometry], name: str
) -> None:
    """Paint `geometries` into `out` in colour `name` by the pixel-centre rule."""
    if not geometries:
        return
    rasterize(
        [(g, _INDEX[name]) for g in geometries],
        out=out,
        transform=grid.transform,
        all_touched=False,
        dtype="uint8",
    )


def render_minimap(grid: MinimapGrid, inputs: MinimapInputs, stream_width_m: float) -> MinimapImage:
    """Paint the layers (module docstring) into palette indices, with counts per layer."""
    ring = inputs.ring
    shapely.prepare(ring)
    classes = np.zeros((grid.height, grid.width), dtype=np.uint8)
    _burn(classes, grid, [ring], "ground")
    inside = classes == _INDEX["ground"]

    groups: dict[str, list[BaseGeometry]] = {g: [] for g in LANDUSE_ORDER}
    unlisted: Counter[str] = Counter()
    for area in inputs.landuse:
        clipped = _clipped(area.geometry, ring)
        if clipped is None:
            continue
        group = LANDUSE_GROUPS.get(area.value)
        if group is None:
            unlisted[area.value] += 1
        else:
            groups[group].append(clipped)
    for group in LANDUSE_ORDER:
        _burn(classes, grid, groups[group], group)

    water = [c for a in inputs.water_areas if (c := _clipped(a.geometry, ring)) is not None]
    _burn(classes, grid, water, "water")
    streams = [
        c
        for line in inputs.stream_lines
        if (c := _clipped(line.geometry.buffer(stream_width_m / 2), ring)) is not None
    ]
    _burn(classes, grid, streams, "water")
    buildings = [c for _, g in inputs.buildings if (c := _clipped(g, ring)) is not None]
    _burn(classes, grid, buildings, "building")
    classes[~inside] = _INDEX["outside"]  # nothing outside the ring, whatever the rounding

    counts = np.bincount(classes.ravel(), minlength=len(PALETTE))
    report: dict[str, Any] = {
        "grid": {
            "m_per_px": grid.m_per_px,
            "width": grid.width,
            "height": grid.height,
            "bounds_local": grid.bounds_local(),
        },
        "water": {
            "mode": inputs.water_mode,
            "source": inputs.water_source,
            "areas": len(water),
            "stream_lines": len(streams),
            "stream_width_m": stream_width_m,
        },
        "features": {
            "landuse": {g: len(groups[g]) for g in LANDUSE_ORDER},
            "landuse_unlisted": sum(unlisted.values()),
            "water_areas": len(water),
            "stream_lines": len(streams),
            "buildings": len(buildings),
        },
        "landuse_unlisted": dict(sorted(unlisted.items())),
        "pixels": {name: int(counts[i]) for i, name in enumerate(PALETTE)},
        "palette": {name: list(rgba) for name, rgba in PALETTE.items()},
        "landuse_groups": dict(LANDUSE_GROUPS),
    }
    return MinimapImage(grid, classes, report)


# --- files ----------------------------------------------------------------------------


def minimap_document(image: MinimapImage, map_id: str, area: str) -> dict[str, Any]:
    grid = image.grid
    return {
        "schema_version": MINIMAP_SCHEMA_VERSION,
        "map_id": map_id,
        "area": area,
        "image": MINIMAP_PNG,
        "m_per_px": grid.m_per_px,
        "width": grid.width,
        "height": grid.height,
        "bounds_local": grid.bounds_local(),
    }


def write_png(path: Path, rgba: np.ndarray) -> Path:
    """An 8-bit RGBA PNG through GDAL's PNG driver, with no georeferencing, metadata or
    `.aux.xml` sidecar, so the bytes depend only on the pixels."""
    _, height, width = rgba.shape
    path.parent.mkdir(parents=True, exist_ok=True)
    with warnings.catch_warnings():
        warnings.simplefilter("ignore", NotGeoreferencedWarning)
        with (
            rasterio.Env(GDAL_PAM_ENABLED="NO"),
            rasterio.open(
                path,
                "w",
                driver="PNG",
                width=width,
                height=height,
                count=4,
                dtype="uint8",
                ZLEVEL=6,
                WORLDFILE="NO",
            ) as dst,
        ):
            dst.write(rgba)
    for sidecar in (path.with_name(path.name + ".aux.xml"), path.with_suffix(".wld")):
        sidecar.unlink(missing_ok=True)
    return path


def write_minimap(pack_dir: Path, image: MinimapImage, map_id: str, area: str) -> None:
    """`minimap.png`, `minimap.png.import` and `minimap.json` (validated) in `pack_dir`."""
    write_png(pack_dir / MINIMAP_PNG, image.rgba())
    (pack_dir / MINIMAP_IMPORT).write_bytes(IMPORT_CONTENT.encode("utf-8"))
    MINIMAP_SCHEMA.validate_file(
        write_json(pack_dir / MINIMAP_JSON, minimap_document(image, map_id, area))
    )


def build_minimap(
    config: MinimapConfig,
    tiles: Sequence[BBox],
    inputs: MinimapInputs,
    work_dir: Path,
    *,
    log: Callable[[str], None] = print,
) -> MinimapImage:
    """Render the image over `tiles` (the written tiles' bounds), write
    `11-minimap/report.json` and log the summary."""
    grid = MinimapGrid.covering(tiles, config.m_per_px)
    image = render_minimap(grid, inputs, config.stream_width_m)
    write_json(work_dir / STAGE_DIR / "report.json", image.report)
    water = image.report["water"]
    log(
        f"[minimap] {grid.width} x {grid.height} px at {grid.m_per_px:g} m/px; water "
        f"(mode {water['mode']}, source {water['source']}): {water['areas']} areas, "
        f"{water['stream_lines']} stream lines"
    )
    unlisted = image.report["landuse_unlisted"]
    if unlisted:
        values = ", ".join(f"{value or '(empty)'} {n}" for value, n in unlisted.items())
        log(f"[minimap] landuse values not in the table (ground colour): {values}")
    return image
