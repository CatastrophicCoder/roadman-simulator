"""OpenStreetMap through the Overpass API: one request for the whole map bbox."""

import re
import urllib.parse
from datetime import datetime
from pathlib import Path

from pydantic import Field

from mapgen.http import HttpError
from mapgen.sources.base import (
    AdapterOptions,
    DownloadContext,
    DownloadedFile,
    SourceAdapter,
    SourceError,
)

DEFAULT_OVERPASS_URL = "https://overpass-api.de/api/interpreter"
SERVER_TIMEOUT_S = 180
CLIENT_TIMEOUT_S = 240.0
BUSY_STATUSES = frozenset({429, 504})


class OverpassBusyError(SourceError):
    """Overpass refused or timed out the request; the user should try again later."""


class OverpassOptions(AdapterOptions):
    url: str = DEFAULT_OVERPASS_URL
    server_timeout_s: int = Field(default=SERVER_TIMEOUT_S, gt=0)
    client_timeout_s: float = Field(default=CLIENT_TIMEOUT_S, gt=0)


def _num(value: float) -> str:
    return repr(float(value))


def build_query(south: float, west: float, north: float, east: float, timeout_s: int) -> str:
    """Nodes and ways in the bbox, all nodes of those ways, and the relations that use them.

    Relation members outside the bbox are not fetched, so large relations such as
    administrative boundaries don't pull in their whole geometry.
    """
    bbox = ",".join(_num(v) for v in (south, west, north, east))
    return (
        f"[out:xml][timeout:{timeout_s}];"
        f"(node({bbox});way({bbox}););(._;>;);(._;rel(bn);rel(bw););out meta;"
    )


def overpass_runtime_error(path: Path) -> str | None:
    """Overpass reports late failures (e.g. timeouts) as a `<remark>` at the end of HTTP 200."""
    with path.open("rb") as handle:
        handle.seek(max(0, path.stat().st_size - 8192))
        tail = handle.read().decode("utf-8", errors="replace")
    match = re.search(r"<remark>(.*?)</remark>", tail, re.DOTALL)
    if match and "error" in match.group(1).lower():
        return match.group(1).strip()
    return None


class OverpassAdapter(SourceAdapter):
    name = "osm_overpass"
    licence = "ODbL 1.0"
    Options = OverpassOptions

    def attribution(self, retrieved_at: datetime) -> str:
        return "© OpenStreetMap contributors"

    def download(self, ctx: DownloadContext) -> list[DownloadedFile]:
        options = ctx.options
        assert isinstance(options, OverpassOptions)
        cfg = ctx.map_config
        dest = ctx.raw_dir / "osm" / f"{cfg.id}.osm"
        dataset = "OpenStreetMap"

        if dest.exists() and not ctx.force:
            ctx.log(f"[{ctx.source_name}] {ctx.display_path(dest)} exists, skipping (use --force)")
            return [ctx.existing_file(dest, self, dataset)]

        box = cfg.bbox_wgs84
        query = build_query(
            box.lat_min, box.lon_min, box.lat_max, box.lon_max, options.server_timeout_s
        )
        ctx.log(f"[{ctx.source_name}] requesting {options.url} (this can take a few minutes)")
        body = urllib.parse.urlencode({"data": query}).encode("utf-8")
        # Download next to the target and replace it only once the response is known good,
        # so a failed --force run keeps the previous file.
        tmp = dest.with_name(dest.name + ".download")
        try:
            try:
                ctx.http.download(
                    options.url,
                    tmp,
                    data=body,
                    headers={"Content-Type": "application/x-www-form-urlencoded"},
                    timeout=options.client_timeout_s,
                )
            except HttpError as exc:
                if exc.status in BUSY_STATUSES:
                    raise OverpassBusyError(
                        f"Overpass busy (HTTP {exc.status}), try again later. Not retrying."
                    ) from None
                raise SourceError(f"Overpass request failed: {exc}") from None
            error = overpass_runtime_error(tmp)
            if error:
                raise SourceError(f"Overpass returned an error instead of data: {error}")
            tmp.replace(dest)
        finally:
            tmp.unlink(missing_ok=True)

        retrieved_at = ctx.now()
        ctx.log(f"[{ctx.source_name}] wrote {ctx.display_path(dest)}")
        return [
            ctx.record(
                dest,
                dataset=dataset,
                request_params={"url": options.url, "query": query},
                retrieved_at=retrieved_at,
                licence=self.licence,
                attribution=self.attribution(retrieved_at),
                origin="downloaded",
            )
        ]
