"""OpenStreetMap through the Overpass API: one request for the whole map bbox.

`load` reads the saved `.osm` XML with the standard library and returns building
footprints in WGS84 (lon, lat). Ids look like "osm:way/123" and "osm:relation/45".
"""

import email.utils
import re
import urllib.parse
import xml.etree.ElementTree as ET
from dataclasses import dataclass, field
from datetime import UTC, datetime
from pathlib import Path

from pydantic import Field
from shapely import make_valid
from shapely.errors import GEOSException
from shapely.geometry import MultiPolygon, Polygon
from shapely.geometry.base import BaseGeometry
from shapely.ops import unary_union

from mapgen.geo import WGS84
from mapgen.http import HttpError
from mapgen.sources.base import (
    OSM_VECTOR,
    AdapterOptions,
    DownloadContext,
    DownloadedFile,
    Footprint,
    LoadContext,
    SkippedFeature,
    SourceAdapter,
    SourceData,
    SourceError,
    SourceLoadError,
)

DEFAULT_OVERPASS_URL = "https://overpass-api.de/api/interpreter"
SERVER_TIMEOUT_S = 180
CLIENT_TIMEOUT_S = 240.0
BUSY_STATUSES = frozenset({429, 502, 503, 504})
# Waits before retries 1, 2, 3, ...: 30, 60, 120 s, doubling. A Retry-After header replaces
# the wait, but one longer than MAX_RETRY_AFTER_S ends the retries instead.
RETRY_BASE_S = 30.0
MAX_RETRY_AFTER_S = 600.0


class OverpassBusyError(SourceError):
    """Overpass refused or timed out the request; the user should try again later."""


class OverpassOptions(AdapterOptions):
    url: str = DEFAULT_OVERPASS_URL
    server_timeout_s: int = Field(default=SERVER_TIMEOUT_S, gt=0)
    client_timeout_s: float = Field(default=CLIENT_TIMEOUT_S, gt=0)


def _num(value: float) -> str:
    return repr(float(value))


def retry_after_s(value: str | None, now: datetime) -> float | None:
    """Seconds to wait from a Retry-After header (delay-seconds or HTTP-date), or None."""
    if value is None:
        return None
    value = value.strip()
    if value.isdigit():
        return float(value)
    try:
        when = email.utils.parsedate_to_datetime(value)
    except (TypeError, ValueError):
        return None
    if when.tzinfo is None:
        when = when.replace(tzinfo=UTC)
    return max(0.0, (when - now).total_seconds())


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


def osm_path(raw_dir: Path, map_id: str) -> Path:
    return raw_dir / "osm" / f"{map_id}.osm"


# Reading the XML.

Coord = tuple[float, float]


@dataclass
class _Way:
    refs: list[int]
    tags: dict[str, str]


@dataclass
class _Relation:
    members: list[tuple[str, int, str]]  # (type, ref, role)
    tags: dict[str, str]


@dataclass
class OsmBuildings:
    """Building footprints read from an `.osm` file, in WGS84 (lon, lat)."""

    footprints: list[Footprint] = field(default_factory=list)
    skipped: list[SkippedFeature] = field(default_factory=list)
    partial: list[SkippedFeature] = field(default_factory=list)  # built, but incomplete


def is_building(tags: dict[str, str]) -> bool:
    value = tags.get("building")
    return value is not None and value != "no"


def _tags(element: ET.Element) -> dict[str, str]:
    return {t.attrib["k"]: t.attrib["v"] for t in element.iter("tag") if "k" in t.attrib}


def _parse(path: Path) -> tuple[dict[int, Coord], dict[int, _Way], dict[int, _Relation]]:
    nodes: dict[int, Coord] = {}
    ways: dict[int, _Way] = {}
    relations: dict[int, _Relation] = {}
    for _, element in ET.iterparse(path, events=("end",)):
        tag = element.tag
        if tag == "node":
            nodes[int(element.attrib["id"])] = (
                float(element.attrib["lon"]),
                float(element.attrib["lat"]),
            )
        elif tag == "way":
            refs = [int(nd.attrib["ref"]) for nd in element.iter("nd")]
            ways[int(element.attrib["id"])] = _Way(refs, _tags(element))
        elif tag == "relation":
            members = [
                (m.attrib["type"], int(m.attrib["ref"]), m.attrib.get("role", ""))
                for m in element.iter("member")
            ]
            relations[int(element.attrib["id"])] = _Relation(members, _tags(element))
        else:
            continue
        element.clear()
    return nodes, ways, relations


def _coords(refs: list[int], nodes: dict[int, Coord]) -> list[Coord] | None:
    try:
        return [nodes[ref] for ref in refs]
    except KeyError:
        return None


def _valid_area(geom: BaseGeometry) -> Polygon | MultiPolygon | None:
    """`geom` repaired, keeping only its polygonal parts; None if nothing with area is left."""
    if not geom.is_valid:
        geom = make_valid(geom)
    if isinstance(geom, Polygon | MultiPolygon):
        polygons = [geom] if isinstance(geom, Polygon) else list(geom.geoms)
    else:
        polygons = [g for g in getattr(geom, "geoms", []) if isinstance(g, Polygon)]
    polygons = [p for p in polygons if not p.is_empty and p.area > 0]
    if not polygons:
        return None
    return polygons[0] if len(polygons) == 1 else MultiPolygon(polygons)


def join_rings(segments: list[list[int]]) -> tuple[list[list[int]], int]:
    """Join way node lists into closed rings by shared end nodes.

    Returns the closed rings and the number of open chains that could not be closed.
    """
    pending = [list(seg) for seg in segments if len(seg) >= 2]
    rings: list[list[int]] = []
    unclosed = 0
    while pending:
        ring = pending.pop(0)
        while ring[0] != ring[-1]:
            for index, seg in enumerate(pending):
                if seg[0] == ring[-1]:
                    ring.extend(seg[1:])
                elif seg[-1] == ring[-1]:
                    ring.extend(reversed(seg[:-1]))
                elif seg[-1] == ring[0]:
                    ring[:0] = seg[:-1]
                elif seg[0] == ring[0]:
                    ring[:0] = list(reversed(seg[1:]))
                else:
                    continue
                pending.pop(index)
                break
            else:
                break
        if ring[0] == ring[-1] and len(ring) >= 4:
            rings.append(ring)
        else:
            unclosed += 1
    return rings, unclosed


@dataclass(frozen=True)
class _RelationResult:
    geometry: Polygon | MultiPolygon | None
    reason: str = ""  # why it was skipped (geometry is None)
    partial: tuple[str, ...] = ()  # what was missing although it was built


def _ring_areas(rings: list[list[int]], nodes: dict[int, Coord]) -> list[Polygon | MultiPolygon]:
    """Each closed ring as a repaired area (rings that collapse to nothing are dropped)."""
    areas = [_valid_area(Polygon([nodes[ref] for ref in ring])) for ring in rings]
    return [a for a in areas if a is not None]


def _relation_geometry(
    relation: _Relation, ways: dict[int, _Way], nodes: dict[int, Coord]
) -> _RelationResult:
    """The relation's area from its closed rings; outer and inner rings are both repaired."""
    by_role: dict[str, list[list[int]]] = {"outer": [], "inner": []}
    missing = 0
    for kind, ref, role in relation.members:
        if kind != "way":
            continue
        way = ways.get(ref)
        if way is None or _coords(way.refs, nodes) is None:
            missing += 1
            continue
        by_role["inner" if role == "inner" else "outer"].append(way.refs)
    outer_rings, open_outer = join_rings(by_role["outer"])
    inner_rings, open_inner = join_rings(by_role["inner"])
    problems: list[str] = []
    if missing:
        problems.append(f"{missing} member way(s) missing")
    if open_outer:
        problems.append(f"{open_outer} outer ring(s) not closed")
    if open_inner:
        problems.append(f"{open_inner} inner ring(s) not closed")
    if not outer_rings:
        detail = ", ".join(problems) or "no member ways"
        return _RelationResult(None, f"incomplete multipolygon: no closed outer ring ({detail})")
    outers = _ring_areas(outer_rings, nodes)
    inners = _ring_areas(inner_rings, nodes)
    if not outers:
        return _RelationResult(None, "multipolygon has no area after repair")
    outer = unary_union(outers)
    geom = outer.difference(unary_union(inners)) if inners else outer
    area = _valid_area(geom)
    if area is None:
        return _RelationResult(None, "multipolygon has no area after repair")
    return _RelationResult(area, partial=tuple(problems))


def read_osm_buildings(path: Path) -> OsmBuildings:
    """Building footprints from closed ways and `type=multipolygon` building relations.

    Relations can arrive incomplete (ADR 0005): unclosed rings are dropped, and a relation
    with no closed outer ring is skipped. A relation built despite missing member ways or
    unclosed rings is listed in `partial`. Geometry errors skip the feature with a reason.
    Ways that are the outer ring of a building relation that was built are skipped as
    duplicates.
    """
    nodes, ways, relations = _parse(path)
    result = OsmBuildings()
    used_outer_ways: dict[int, int] = {}

    for rel_id, relation in sorted(relations.items()):
        if not is_building(relation.tags):
            continue
        fid = f"osm:relation/{rel_id}"
        if relation.tags.get("type") != "multipolygon":
            kind = relation.tags.get("type", "")
            result.skipped.append(SkippedFeature(fid, f"unsupported relation type {kind!r}"))
            continue
        try:
            built = _relation_geometry(relation, ways, nodes)
        except (GEOSException, ValueError) as exc:
            result.skipped.append(SkippedFeature(fid, f"geometry error: {exc}"))
            continue
        if built.geometry is None:
            result.skipped.append(SkippedFeature(fid, built.reason))
            continue
        if built.partial:
            reason = "partial multipolygon: " + ", ".join(built.partial)
            result.partial.append(SkippedFeature(fid, reason))
        result.footprints.append(Footprint(fid, built.geometry, WGS84, dict(relation.tags)))
        for kind, ref, role in relation.members:
            if kind == "way" and role in ("outer", ""):
                used_outer_ways[ref] = rel_id

    for way_id, way in sorted(ways.items()):
        if not is_building(way.tags):
            continue
        fid = f"osm:way/{way_id}"
        if way_id in used_outer_ways:
            reason = f"duplicate: outer ring of osm:relation/{used_outer_ways[way_id]}"
            result.skipped.append(SkippedFeature(fid, reason))
            continue
        if len(way.refs) < 4 or way.refs[0] != way.refs[-1]:
            result.skipped.append(SkippedFeature(fid, "way is not closed"))
            continue
        coords = _coords(way.refs, nodes)
        if coords is None:
            result.skipped.append(SkippedFeature(fid, "way has nodes missing from the data"))
            continue
        try:
            area = _valid_area(Polygon(coords))
        except (GEOSException, ValueError) as exc:
            result.skipped.append(SkippedFeature(fid, f"geometry error: {exc}"))
            continue
        if area is None:
            result.skipped.append(SkippedFeature(fid, "footprint has no area after repair"))
            continue
        result.footprints.append(Footprint(fid, area, WGS84, dict(way.tags)))

    result.footprints.sort(key=lambda f: f.id)
    return result


class OverpassAdapter(SourceAdapter):
    name = "osm_overpass"
    licence = "ODbL 1.0"
    Options = OverpassOptions
    provides = frozenset({OSM_VECTOR})

    def load(self, ctx: LoadContext) -> SourceData:
        path = osm_path(ctx.raw_dir, ctx.map_config.id)
        if not path.is_file():
            raise SourceLoadError(
                f"[{ctx.source_name}] {path} not found; run `mapgen download "
                f"{ctx.map_config.id} --only {ctx.source_name}`"
            )
        try:
            buildings = read_osm_buildings(path)
        except (ET.ParseError, KeyError, ValueError) as exc:
            raise SourceLoadError(f"[{ctx.source_name}] cannot read {path}: {exc}") from exc
        return SourceData(
            files=[path],
            footprints=buildings.footprints,
            skipped=buildings.skipped,
            partial=buildings.partial,
        )

    def attribution(self, retrieved_at: datetime) -> str:
        return "© OpenStreetMap contributors"

    @staticmethod
    def _retry_wait(
        ctx: DownloadContext, exc: HttpError, attempt: int
    ) -> tuple[float, bool] | None:
        """(seconds to wait, whether Retry-After set it), or None to stop retrying."""
        if attempt > ctx.retries:
            return None
        server_wait = retry_after_s(exc.retry_after, ctx.now())
        if server_wait is not None:
            return (server_wait, True) if server_wait <= MAX_RETRY_AFTER_S else None
        return RETRY_BASE_S * 2 ** (attempt - 1), False

    @staticmethod
    def _busy_message(ctx: DownloadContext, exc: HttpError, attempt: int) -> str:
        if ctx.retries == 0:
            return (
                f"Overpass busy (HTTP {exc.status}), try again later. Not retrying "
                "(use --retries N)."
            )
        if attempt <= ctx.retries:
            return (
                f"Overpass busy (HTTP {exc.status}) and asks to wait {exc.retry_after!r} "
                f"(Retry-After, over {MAX_RETRY_AFTER_S:.0f} s), try again later."
            )
        return f"Overpass busy (HTTP {exc.status}) after {attempt} attempts, try again later."

    def download(self, ctx: DownloadContext) -> list[DownloadedFile]:
        options = ctx.options
        assert isinstance(options, OverpassOptions)
        cfg = ctx.map_config
        dest = osm_path(ctx.raw_dir, cfg.id)
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
            attempt = 0
            while True:
                attempt += 1
                try:
                    ctx.http.download(
                        options.url,
                        tmp,
                        data=body,
                        headers={"Content-Type": "application/x-www-form-urlencoded"},
                        timeout=options.client_timeout_s,
                    )
                    break
                except HttpError as exc:
                    if exc.status not in BUSY_STATUSES:
                        raise SourceError(f"Overpass request failed: {exc}") from None
                    retry = self._retry_wait(ctx, exc, attempt)
                    if retry is None:
                        raise OverpassBusyError(self._busy_message(ctx, exc, attempt)) from None
                    wait, from_header = retry
                    # Status and wait only: never the request body (the query) or the response.
                    ctx.log(
                        f"[{ctx.source_name}] Overpass busy (HTTP {exc.status}), retry "
                        f"{attempt} of {ctx.retries} in {wait:.0f} s"
                        + (" (Retry-After)" if from_header else "")
                    )
                    ctx.sleep(wait)
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
