"""The shared adapter interface and the download context."""

import hashlib
import time
from abc import ABC, abstractmethod
from collections.abc import Callable, Mapping
from dataclasses import dataclass, field
from datetime import UTC, datetime
from pathlib import Path
from typing import Any, ClassVar, Literal

from pydantic import BaseModel, ConfigDict

from mapgen.config import MapConfig, SourceConfig
from mapgen.http import HttpClient

FileOrigin = Literal["downloaded", "manual"]


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


class SourceAdapter(ABC):
    """One data source. Subclasses are registered in `mapgen.sources.registry`."""

    name: ClassVar[str]
    licence: ClassVar[str]
    Options: ClassVar[type[AdapterOptions]] = AdapterOptions

    @abstractmethod
    def attribution(self, retrieved_at: datetime) -> str:
        """The attribution text required by the data licence."""

    @abstractmethod
    def download(self, ctx: DownloadContext) -> list[DownloadedFile]:
        """Download (or register already present) files and describe each one."""
