"""The shared adapter interface: download and load contexts, and the loaded data types."""

import hashlib
import time
from abc import ABC, abstractmethod
from collections.abc import Callable, Mapping
from dataclasses import dataclass, field
from datetime import UTC, datetime
from pathlib import Path
from typing import Any, ClassVar, Literal

import numpy as np
from pydantic import BaseModel, ConfigDict
from rasterio.transform import Affine
from shapely.geometry import MultiPolygon, Polygon

from mapgen.config import MapConfig, SourceConfig
from mapgen.geo import BBox
from mapgen.http import HttpClient

FileOrigin = Literal["downloaded", "manual"]

# What a source can load. The build picks enabled sources by capability, never by name.
OSM_VECTOR = "osm_vector"  # OpenStreetMap-style vector features (building footprints for now)
ELEVATION = "elevation"  # a terrain elevation raster
BUILDINGS_3D = "buildings_3d"  # building footprints with measured heights
POINT_CLOUD = "point_cloud"  # classified lidar points (building heights)
IMAGERY = "imagery"  # north-up RGB aerial imagery (debug orthophoto textures)


def utc_now() -> datetime:
    return datetime.now(UTC)


def isoformat_utc(moment: datetime) -> str:
    return moment.astimezone(UTC).isoformat(timespec="seconds").replace("+00:00", "Z")


def parse_isoformat(text: str) -> datetime:
    return datetime.fromisoformat(text.replace("Z", "+00:00"))


def sha256_of(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as handle:
        while chunk := handle.read(1024 * 1024):
            digest.update(chunk)
    return digest.hexdigest()


class SourceError(Exception):
    """A source could not be downloaded. The message is shown to the user as is."""


class AdapterOptions(BaseModel):
    """Base for each adapter's `options` model."""

    model_config = ConfigDict(extra="forbid", frozen=True)


@dataclass(frozen=True)
class DownloadedFile:
    """One raw file and where it came from. `request_params` never hold secrets."""

    path: Path
    source: str
    dataset: str
    request_params: dict[str, Any]
    retrieved_at: datetime
    size: int
    sha256: str
    licence: str
    attribution: str
    origin: FileOrigin

    def to_manifest_entry(self, raw_dir: Path) -> dict[str, Any]:
        return {
            "path": self.path.relative_to(raw_dir).as_posix(),
            "source": self.source,
            "dataset": self.dataset,
            "request": self.request_params,
            "retrieved_at": isoformat_utc(self.retrieved_at),
            "size": self.size,
            "sha256": self.sha256,
            "licence": self.licence,
            "attribution": self.attribution,
            "origin": self.origin,
        }


@dataclass
class DownloadContext:
    """Everything an adapter needs. Tests replace `http`, `env`, `sleep` and the clocks."""

    map_config: MapConfig
    source_name: str
    source_config: SourceConfig
    options: AdapterOptions
    raw_dir: Path
    repo_root: Path
    force: bool
    http: HttpClient
    env: Mapping[str, str]
    previous_entries: Mapping[str, Mapping[str, Any]] = field(default_factory=dict)
    retries: int = 0  # how often an adapter may retry a busy server (`download --retries`)
    log: Callable[[str], None] = print
    now: Callable[[], datetime] = utc_now
    sleep: Callable[[float], None] = time.sleep
    monotonic: Callable[[], float] = time.monotonic

    def display_path(self, path: Path) -> str:
        """`path` relative to the repository root when possible."""
        try:
            return path.relative_to(self.repo_root).as_posix()
        except ValueError:
            return str(path)

    def record(
        self,
        path: Path,
        *,
        dataset: str,
        request_params: dict[str, Any],
        retrieved_at: datetime,
        licence: str,
        attribution: str,
        origin: FileOrigin,
    ) -> DownloadedFile:
        return DownloadedFile(
            path=path,
            source=self.source_name,
            dataset=dataset,
            request_params=request_params,
            retrieved_at=retrieved_at,
            size=path.stat().st_size,
            sha256=sha256_of(path),
            licence=licence,
            attribution=attribution,
            origin=origin,
        )

    def existing_file(self, path: Path, adapter: "SourceAdapter", dataset: str) -> DownloadedFile:
        """Register a file that is already on disk.

        If the previous manifest has an entry for it with the same checksum, that entry is
        kept. Otherwise the file is treated as a manual download dated by its mtime.
        """
        checksum = sha256_of(path)
        rel = path.relative_to(self.raw_dir).as_posix()
        previous = self.previous_entries.get(rel)
        if previous is not None and previous.get("sha256") == checksum:
            retrieved_at = parse_isoformat(str(previous["retrieved_at"]))
            origin: FileOrigin = "manual" if previous.get("origin") == "manual" else "downloaded"
            return DownloadedFile(
                path=path,
                source=self.source_name,
                dataset=str(previous.get("dataset", dataset)),
                request_params=dict(previous.get("request") or {}),
                retrieved_at=retrieved_at,
                size=path.stat().st_size,
                sha256=checksum,
                licence=adapter.licence,
                attribution=adapter.attribution(retrieved_at),
                origin=origin,
            )
        retrieved_at = datetime.fromtimestamp(path.stat().st_mtime, UTC)
        return DownloadedFile(
            path=path,
            source=self.source_name,
            dataset=dataset,
            request_params={},
            retrieved_at=retrieved_at,
            size=path.stat().st_size,
            sha256=checksum,
            licence=adapter.licence,
            attribution=adapter.attribution(retrieved_at),
            origin="manual",
        )


class SourceLoadError(Exception):
    """A source's raw files are missing or unreadable. The message is shown to the user."""


@dataclass(frozen=True)
class Footprint:
    """A building footprint in `crs` coordinates.

    `id` is "<source>:<type>/<number>", e.g. "osm:way/123". `measured_height_m` is set only
    by `buildings_3d` sources.
    """

    id: str
    geometry: Polygon | MultiPolygon
    crs: str
    tags: Mapping[str, str] = field(default_factory=dict)
    measured_height_m: float | None = None


@dataclass(frozen=True)
class SkippedFeature:
    """A feature that was not loaded, and why."""

    id: str
    reason: str


@dataclass(frozen=True)
class ElevationRaster:
    """A north-up elevation grid. `data` rows run north to south; nodata cells are NaN."""

    data: np.ndarray
    transform: Affine
    crs: str
    warped_from: tuple[str, ...] = ()  # CRSs of source files that had to be warped

    @property
    def resolution(self) -> tuple[float, float]:
        return (abs(self.transform.a), abs(self.transform.e))


@dataclass(frozen=True)
class PointCloud:
    """Lidar points as parallel arrays of equal length, in `crs` coordinates.

    `x`, `y` and `z` are float64 metres, `classification` the ASPRS class (uint8),
    `return_number` and `number_of_returns` the pulse return counts (uint8).
    """

    x: np.ndarray
    y: np.ndarray
    z: np.ndarray
    classification: np.ndarray
    return_number: np.ndarray
    number_of_returns: np.ndarray
    crs: str

    def __post_init__(self) -> None:
        n = len(self.x)
        arrays = (
            self.y,
            self.z,
            self.classification,
            self.return_number,
            self.number_of_returns,
        )
        if any(a.ndim != 1 or len(a) != n for a in (self.x, *arrays)):
            raise ValueError("point cloud arrays must be 1-D and of equal length")

    def __len__(self) -> int:
        return len(self.x)

    @classmethod
    def empty(cls, crs: str) -> "PointCloud":
        f, u = np.zeros(0, dtype=np.float64), np.zeros(0, dtype=np.uint8)
        return cls(f, f.copy(), f.copy(), u, u.copy(), u.copy(), crs)

    def select(self, mask: np.ndarray) -> "PointCloud":
        """The points where `mask` (a boolean array or an index array) selects."""
        return PointCloud(
            self.x[mask],
            self.y[mask],
            self.z[mask],
            self.classification[mask],
            self.return_number[mask],
            self.number_of_returns[mask],
            self.crs,
        )

    def offset(self, dx: float, dy: float, crs: str | None = None) -> "PointCloud":
        """The points shifted by (dx, dy), e.g. to local coordinates."""
        return PointCloud(
            self.x + dx,
            self.y + dy,
            self.z,
            self.classification,
            self.return_number,
            self.number_of_returns,
            self.crs if crs is None else crs,
        )

    @classmethod
    def concatenate(cls, clouds: "list[PointCloud]", crs: str) -> "PointCloud":
        if not clouds:
            return cls.empty(crs)
        return cls(
            np.concatenate([c.x for c in clouds]),
            np.concatenate([c.y for c in clouds]),
            np.concatenate([c.z for c in clouds]),
            np.concatenate([c.classification for c in clouds]),
            np.concatenate([c.return_number for c in clouds]),
            np.concatenate([c.number_of_returns for c in clouds]),
            crs,
        )


@dataclass(frozen=True)
class Imagery:
    """North-up RGB raster files, read per tile by the build (not loaded into memory).

    `files` are the files that overlap the requested area. `resolution_m` is the requested
    output resolution in metres per pixel.
    """

    files: tuple[Path, ...]
    resolution_m: float


@dataclass
class SourceData:
    """What one source loaded, plus the raw files it read (used for the pack credits)."""

    files: list[Path] = field(default_factory=list)
    footprints: list[Footprint] = field(default_factory=list)
    elevation: ElevationRaster | None = None
    points: PointCloud | None = None
    imagery: Imagery | None = None
    skipped: list[SkippedFeature] = field(default_factory=list)
    partial: list[SkippedFeature] = field(default_factory=list)  # built, but incomplete


@dataclass
class LoadContext:
    """Everything an adapter needs to read its raw files for a build."""

    map_config: MapConfig
    source_name: str
    options: AdapterOptions
    raw_dir: Path
    target_crs: str
    bounds: BBox  # the area to read, in `target_crs` world coordinates
    log: Callable[[str], None] = print


class SourceAdapter(ABC):
    """One data source. Subclasses are registered in `mapgen.sources.registry`."""

    name: ClassVar[str]
    licence: ClassVar[str]
    Options: ClassVar[type[AdapterOptions]] = AdapterOptions
    provides: ClassVar[frozenset[str]] = frozenset()
    # Environment variables needed to download automatically (e.g. an API key). Without
    # them an adapter may fall back to manual steps; `download --strict` fails instead.
    required_env: ClassVar[tuple[str, ...]] = ()

    @abstractmethod
    def attribution(self, retrieved_at: datetime) -> str:
        """The attribution text required by the data licence."""

    @abstractmethod
    def download(self, ctx: DownloadContext) -> list[DownloadedFile]:
        """Download (or register already present) files and describe each one."""

    def load(self, ctx: LoadContext) -> SourceData:
        """Read the downloaded raw files. Only adapters with a capability in `provides` load."""
        raise NotImplementedError(f"adapter {self.name!r} cannot load data for a build")
