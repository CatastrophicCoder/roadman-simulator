"""Stage 10, the world edge (docs/specs/world-edge-pipeline.md).

Builds the closed edge of the playable area from the boundary ring: roadblocks where the
road graph's `area_edge` nodes are, land and water fences along the rest, the cliff's
bottom height, the void trigger prisms and the spawn area check, and returns the
`boundary.json` document. Writes `10-world-edge/report.json` and `boundary.geojson`.
The cliff meshes are built afterwards from that document and the written tiles' terrain
(`cliff.py`, called by `run_build`), which adds the `cliff` block to the report.

Every part is a function of a `Ring` and plain inputs (surfaces, graph, heightfield,
water), so it works for any simple ring. `boundary_for` picks the ring: the test-area
square for test builds, the custom polygon (`world_edge.boundary_wgs84`) for full builds
that have one, the build area rectangle otherwise.
"""

import math
from collections import Counter
from collections.abc import Callable, Mapping, Sequence
from dataclasses import dataclass, field
from pathlib import Path
from typing import Any, Literal

import numpy as np
import shapely
from shapely.geometry import LineString, Polygon
from shapely.geometry.base import BaseGeometry

from mapgen.build.area import BuildArea
from mapgen.build.heightfield import Heightfield
from mapgen.build.stage1_load import LoadResult
from mapgen.build.stage_io import write_geojson, write_json
from mapgen.build.world_edge.fences import FenceSegment, fence_segments
from mapgen.build.world_edge.model import (
    BOUNDARY_SCHEMA,
    BOUNDARY_SCHEMA_VERSION,
)
from mapgen.build.world_edge.narrow import NarrowFeature, narrow_features
from mapgen.build.world_edge.ring import Ring
from mapgen.build.world_edge.roadblocks import (
    EdgeNode,
    RoadblockError,
    RoadblockSpan,
    edge_nodes,
    merge_spans,
    node_spans,
    surface_intervals,
    touched_stretches,
    widest,
)
from mapgen.build.world_edge.spawn import Obstacle, SpawnResult, spawn_check
from mapgen.build.world_edge.void import ARC_EXCESS_M, Quad, ring_distances, void_quads
from mapgen.build.world_edge.water import (
    WaterCrossing,
    WaterFeature,
    disagreements,
    ids_on,
    line_crossings,
    water_crossings,
)
from mapgen.config import MapConfig, WorldEdgeConfig
from mapgen.geo import map_to_wgs84

__all__ = [
    "Boundary",
    "WorldEdgeError",
    "WorldEdgeResult",
    "boundary_for",
    "build_world_edge",
]

STAGE_DIR = "10-world-edge"
DIGITS = 6  # boundary.json values are rounded to 1 µm (and 1e-6 degrees, m², ...)
HEIGHT_SPACING_M = 0.5  # the ring's terrain is sampled this often for its lowest/highest
BoundarySource = Literal["area", "custom", "test_area"]


class WorldEdgeError(Exception):
    """The world edge can't be built. The message is shown to the user."""


@dataclass(frozen=True)
class Boundary:
    ring: Ring
    source: BoundarySource

    def polygon(self) -> Polygon:
        return self.ring.polygon()


def boundary_for(config: MapConfig, area: BuildArea) -> Boundary:
    """The playable boundary: the test-area square in a test build (whatever
    `boundary_wgs84` says), the custom polygon (`world_edge.boundary_wgs84`, in local
    metres as `MapConfig.boundary_local` gives it) in a full build that has one, else the
    build area rectangle. Raises `WorldEdgeError` when the boundary, whatever its source,
    has a spike or notch narrower than `world_edge.min_feature_width_m` (`narrow.py`)."""
    if area.name == "test":
        boundary = Boundary(Ring.rectangle(*area.bounds.as_list()), "test_area")
    elif (coords := config.boundary_local()) is None:
        boundary = Boundary(Ring.rectangle(*area.bounds.as_list()), "area")
    else:
        try:
            boundary = Boundary(Ring.from_coords(coords), "custom")
        except ValueError as exc:
            raise WorldEdgeError(f"world_edge.boundary_wgs84: {exc}") from exc
    width = config.world_edge.min_feature_width_m
    features = narrow_features(boundary.ring, width)
    if features:
        raise WorldEdgeError(narrow_message(config, boundary, features, width))
    return boundary


def narrow_message(
    config: MapConfig, boundary: Boundary, features: Sequence[NarrowFeature], width: float
) -> str:
    """The build error for narrow spikes and notches: where each one is (Godot local x/z,
    WGS84), how narrow, and its stretch of the ring."""
    lines = [
        f"the boundary ({boundary.source}) has {len(features)} spike(s) or notch(es) "
        f"narrower than world_edge.min_feature_width_m ({width:g} m); widen or straighten "
        "the boundary there (rule: tools/mapgen/src/mapgen/build/world_edge/narrow.py):"
    ]
    listed = config.boundary_local() if boundary.source == "custom" else None
    for f in features:
        x, n = f.point
        lon, lat = map_to_wgs84(x + config.origin.x, n + config.origin.y, config.crs)
        if f.angle_deg is not None:
            what = f"a {f.angle_deg:.1f}° corner (width 0 at its tip)"
        else:
            what = f"{f.width_m:.3f} m wide"
        line = (
            f"  {f.kind} at x {x:.2f}, z {-n:.2f} (Godot local), lat {lat:.7f}, lon "
            f"{lon:.7f}: {what}, ring s {f.s_start:.2f}..{f.s_end:.2f} "
            f"(of {boundary.ring.length:.2f})"
        )
        if listed:
            ends = [_nearest_entry(listed, f.a), _nearest_entry(listed, f.b)]
            line += ", nearest boundary_wgs84 entries (0-based) " + (
                str(ends[0]) if ends[0] == ends[1] else f"{ends[0]} and {ends[1]}"
            )
        lines.append(line)
    return "\n".join(lines)


def _nearest_entry(points: Sequence[tuple[float, float]], at: tuple[float, float]) -> int:
    """The index of the `boundary_wgs84` entry (in local metres) nearest to `at`."""
    return min(range(len(points)), key=lambda k: math.dist(points[k], at))


@dataclass
class WorldEdgeResult:
    document: dict[str, Any]  # boundary.json
    report: dict[str, Any]  # 10-world-edge/report.json
    spans: list[RoadblockSpan] = field(default_factory=list)
    fences: list[FenceSegment] = field(default_factory=list)
    crossings: list[WaterCrossing] = field(default_factory=list)
    quads: list[Quad] = field(default_factory=list)
    spawn: SpawnResult | None = None


def r(value: float) -> float:
    """Rounded to `DIGITS`, without -0.0."""
    return round(float(value), DIGITS) + 0.0


def godot_xz(x: float, n: float) -> list[float]:
    return [r(x), r(-n)]


def godot_xyz(x: float, y: float, n: float) -> list[float]:
    return [r(x), r(y), r(-n)]


def boundary_heights(
    ring: Ring, heightfield: Heightfield, spacing_m: float = HEIGHT_SPACING_M
) -> tuple[float, float]:
    """The lowest and highest terrain along the ring (sampled every `spacing_m` and at
    every vertex)."""
    count = max(int(math.ceil(ring.length / spacing_m)), ring.n)
    s = np.unique(np.concatenate([np.linspace(0, ring.length, count + 1), ring.vertex_s]))
    xy = ring.points_at(s)
    z = heightfield.sample(xy[:, 0], xy[:, 1])
    return float(z.min()), float(z.max())


def ring_stretch(ring: Ring, a: float, b: float) -> tuple[float, float]:
    """`s_start` and `s_end` of the stretch (a, b) as written to `boundary.json`: rounded,
    then moved back by one ring length when `s_start` rounds to the ring's length (a value
    within 5e-7 m of it), so `s_start` stays in [0, length) and the length is kept."""
    limit = min(ring.length, r(ring.length))
    if r(a) >= limit:
        a, b = a - ring.length, b - ring.length
    return max(r(a), 0.0), r(b)


def yaw_degrees(tangent: Sequence[float]) -> float:
    """The angle of `tangent` (local x, n) in degrees, rounded to `DIGITS`, in (-180, 180]:
    rounded first, so a value just above -180 that rounds to -180 becomes 180."""
    yaw = r(math.degrees(math.atan2(float(tangent[1]), float(tangent[0]))))
    if yaw <= -180.0:
        yaw = r(yaw + 360.0)
    return yaw


def roadblock_record(
    ring: Ring,
    span: RoadblockSpan,
    heightfield: Heightfield,
    surface_offset_m: float,
    crossings: Sequence[WaterCrossing],
) -> dict[str, Any]:
    """A `boundary.json` roadblock for a span. Ends and position sit on the ring at
    terrain + `surface_offset_m` (the road surface), or, when one of its nodes is the end
    of a bridge deck edge, at the highest of those nodes' graph heights (the deck).
    `over_water` is true when any part of the span lies in a water crossing."""
    over_water = any(_overlap(ring, span.a, span.b, c.s_start, c.s_end) > 0 for c in crossings)
    deck_nodes = [n.y for n in span.nodes if n.bridge]
    deck = max(deck_nodes) if deck_nodes else None

    def point(s: float) -> list[float]:
        x, n = ring.point_at(s)
        y = deck if deck is not None else float(heightfield.sample(x, n)) + surface_offset_m
        return godot_xyz(x, y, n)

    middle = ring.wrap((span.a + span.b) / 2)
    tangent = ring.tangent_at(middle)
    outward = ring.outward_at(middle)
    main = widest(span.nodes)
    s_start, s_end = ring_stretch(ring, span.a, span.b)
    return {
        "id": span.id,
        "nodes": [{"node": n.node, "edge": n.edge, "role": n.role} for n in span.nodes],
        "class": main.road_class,
        "drivable": any(n.drivable for n in span.nodes),
        "walkable": any(n.walkable for n in span.nodes),
        "width_m": r(main.width_m),
        "width_source": main.width_source,
        "start": point(span.a),
        "end": point(span.b),
        "position": point(middle),
        "yaw_deg": yaw_degrees(tangent),
        "outward": [r(outward[0]), 0.0, r(-outward[1])],
        "s_start": s_start,
        "s_end": s_end,
        "over_water": over_water,
    }


def _overlap(ring: Ring, a0: float, b0: float, a1: float, b1: float) -> float:
    """The length shared by two circular stretches."""
    best = 0.0
    for k in (-1, 0, 1):
        lo = max(a0, a1 + k * ring.length)
        hi = min(b0, b1 + k * ring.length)
        best = max(best, hi - lo)
    return best


def fence_record(
    ring: Ring, k: int, fence: FenceSegment, heightfield: Heightfield
) -> dict[str, Any]:
    """A `boundary.json` fence segment; its ends sit on the terrain."""

    def point(s: float) -> list[float]:
        x, n = ring.point_at(s)
        return godot_xyz(x, float(heightfield.sample(x, n)), n)

    outward = ring.outward_at((fence.s_start + fence.s_end) / 2)
    s_start, s_end = ring_stretch(ring, fence.s_start, fence.s_end)
    record: dict[str, Any] = {
        "id": f"fence:{k}",
        "type": fence.type,
        "start": point(fence.s_start),
        "end": point(fence.s_end),
        "s_start": s_start,
        "s_end": s_end,
        "outward": [r(outward[0]), 0.0, r(-outward[1])],
    }
    if fence.type == "water":
        record["water_ids"] = list(fence.water_ids)
    return record


def _rings(geometry: BaseGeometry) -> list[list[list[list[float]]]]:
    parts = [geometry] if isinstance(geometry, Polygon) else list(getattr(geometry, "geoms", []))
    return [
        [[godot_xz(x, n) for x, n in ring.coords] for ring in (part.exterior, *part.interiors)]
        for part in parts
        if isinstance(part, Polygon) and not part.is_empty
    ]


def _check_left_out(
    loaded: LoadResult, ring: Ring, reach_m: float
) -> tuple[list[dict[str, Any]], list[dict[str, Any]]]:
    """OSM water relations the load stage left out: (near the boundary, not located)."""
    line = ring.line()
    near: list[dict[str, Any]] = []
    unlocated: list[dict[str, Any]] = []
    for item in loaded.water_left_out:
        if item.geometry is None or item.geometry.is_empty:
            unlocated.append({"id": item.id, "reason": item.reason})
            continue
        distance = float(item.geometry.distance(line))
        if distance <= reach_m:
            near.append({"id": item.id, "reason": item.reason, "distance_m": round(distance, 3)})
    return near, unlocated


def water_setup(
    loaded: LoadResult, ring: Ring, cfg: WorldEdgeConfig
) -> tuple[str | None, list[WaterFeature], dict[str, Any]]:
    """The water source, its areas and the water part of the report (crossings filled in
    later). Raises `WorldEdgeError` for an OSM water relation left out near the boundary
    when the OSM areas are used."""
    report: dict[str, Any] = {"mode": loaded.edge_water_mode, "source": loaded.edge_water_source}
    areas = [WaterFeature(w.id, w.geometry) for w in loaded.edge_water_areas]
    if loaded.edge_water_mode == "osm":
        reach = cfg.cliff.ledge_m + cfg.void.width_m
        near, unlocated = _check_left_out(loaded, ring, reach)
        report["left_out_relations_near_boundary"] = near
        report["left_out_relations_unlocated"] = unlocated
        if near:
            names = ", ".join(f"{n['id']} ({n['reason']}, {n['distance_m']} m)" for n in near)
            raise WorldEdgeError(
                f"OSM water relation(s) left out by the load stage lie within {reach:g} m of "
                f"the boundary, so its water fences can't be placed: {names}"
            )
    report["records_skipped"] = [
        {"id": s.id, "reason": s.reason} for s in loaded.edge_water_skipped
    ]
    return loaded.edge_water_source, areas, report


def _endpoint(ring: Ring, s: float, config: MapConfig) -> dict[str, Any]:
    x, n = ring.point_at(s)
    wx, wy = x + config.origin.x, n + config.origin.y
    lon, lat = map_to_wgs84(wx, wy, config.crs)
    return {
        "local": [round(float(x), 3), round(float(n), 3)],
        "map_crs": [round(wx, 3), round(wy, 3)],
        "wgs84": {"lat": round(lat, 7), "lon": round(lon, 7)},
    }


def build_world_edge(
    config: MapConfig,
    area: BuildArea,
    boundary: Boundary,
    graph: Mapping[str, Any],
    surfaces: Sequence[tuple[str, BaseGeometry]],
    loaded: LoadResult,
    buildings: Sequence[tuple[str, BaseGeometry]],
    spawn: Polygon,
    work_dir: Path,
    *,
    dropped_buildings: Sequence[dict[str, Any]] = (),
    log: Callable[[str], None] = print,
) -> WorldEdgeResult:
    """Run stage 10 and return `boundary.json` and the report (also written to
    `work_dir/10-world-edge/`). `surfaces` are the road, path and bridge deck footprints
    as (kind, geometry), `buildings` the kept footprints as (id, geometry)."""
    cfg = config.world_edge
    ring = boundary.ring
    heightfield = loaded.heightfield
    offset = config.roads.surface_offset_m if config.roads is not None else 0.0

    # Water.
    source, areas, water_report = water_setup(loaded, ring, cfg)
    crossings = water_crossings(ring, areas, source or "none")
    lines = line_crossings(
        ring, [WaterFeature(w.id, w.geometry) for w in loaded.edge_water_lines], source or ""
    )
    if loaded.edge_water_mode == "water_areas":
        osm_areas = [WaterFeature(w.id, w.geometry) for w in loaded.water_areas]
        osm = water_crossings(ring, osm_areas, "osm")
        lines += line_crossings(
            ring, [WaterFeature(w.id, w.geometry) for w in loaded.water_lines], "osm"
        )
        lines.sort(key=lambda item: (item["s"], item["id"]))
        water_report["disagreements"] = disagreements(ring, crossings, osm)
    else:
        osm_areas = []
    crossing_entries = []
    for crossing in crossings:
        entry = {
            "s_start": round(crossing.s_start, 3),
            "s_end": round(crossing.s_end, 3),
            "length_m": round(crossing.length, 3),
            "start": _endpoint(ring, crossing.s_start, config),
            "end": _endpoint(ring, crossing.s_end, config),
            "water_ids": list(crossing.water_ids),
            "source": crossing.source,
        }
        if osm_areas:
            entry["osm_areas_on_stretch"] = ids_on(ring, crossing.interval, osm_areas)
        crossing_entries.append(entry)
        a, b = entry["start"]["wgs84"], entry["end"]["wgs84"]
        log(
            f"[world-edge] water crossing: {crossing.length:.1f} m at "
            f"({a['lat']}, {a['lon']})–({b['lat']}, {b['lon']}) ({', '.join(crossing.water_ids)})"
        )
    total = sum(c.length for c in crossings)
    log(f"[world-edge] water crossings: {len(crossings)}, total {total:.1f} m")
    water_report["crossings"] = crossing_entries
    water_report["total_m"] = round(total, 3)
    water_report["lines"] = [
        {
            "id": item["id"],
            "source": item["source"],
            "s": round(item["s"], 3),
            **_endpoint(ring, item["s"], config),
            "in_water_crossing": any(ring.contains_s(c.interval, item["s"]) for c in crossings),
        }
        for item in lines
    ]

    # Roadblocks.
    try:
        nodes: list[EdgeNode] = edge_nodes(graph)
        surface = shapely.union_all([g for _, g in surfaces]) if surfaces else None
        stretches = surface_intervals(ring, surface)
        spans = merge_spans(
            ring,
            node_spans(ring, nodes, stretches, cfg.roadblock.clearance_m),
            cfg.roadblock.merge_distance_m,
        )
    except RoadblockError as exc:
        raise WorldEdgeError(str(exc)) from exc
    touched = touched_stretches(ring, nodes, stretches)
    roadblocks = [roadblock_record(ring, span, heightfield, offset, crossings) for span in spans]

    # Fences.
    fences = fence_segments(
        ring,
        [span.interval for span in spans],
        crossings,
        heightfield,
        cfg.fence.max_segment_m,
        cfg.fence.max_terrain_deviation_m,
    )
    fence_records = [fence_record(ring, k, f, heightfield) for k, f in enumerate(fences)]

    # Cliff and void.
    low, high = boundary_heights(ring, heightfield)
    heights = (
        [low, high] + [rb["start"][1] for rb in roadblocks] + [rb["end"][1] for rb in roadblocks]
    )
    low, high = min(heights), max(heights)
    cliff_bottom = low - cfg.cliff.depth_m
    void_bottom = cliff_bottom - cfg.void.width_m
    void_top = high + cfg.void.top_margin_m
    quads = void_quads(ring, cfg.void.inner_offset_m, cfg.void.width_m)

    # Spawn area.
    obstacles = [Obstacle("building", fid, g) for fid, g in buildings]
    obstacles += [Obstacle("water", a.id, a.geometry) for a in areas]
    kinds: Counter[str] = Counter()
    for kind, geometry in surfaces:
        obstacles.append(Obstacle(kind, f"{kind}:{kinds[kind]}", geometry))
        kinds[kind] += 1
    spawn_result = spawn_check(spawn, boundary.polygon(), obstacles, cfg.spawn)
    log(
        f"[world-edge] spawn area: open ground {spawn_result.open_area_m2:.0f} m², largest "
        f"clear disc r {spawn_result.clear_radius_m:.1f} m"
    )

    document = {
        "schema_version": BOUNDARY_SCHEMA_VERSION,
        "map_id": config.id,
        "area": area.name,
        "boundary": {
            "ring": [godot_xz(x, n) for x, n in ring.xy],
            "length_m": r(ring.length),
            "source": boundary.source,
        },
        "roadblocks": roadblocks,
        "fences": fence_records,
        "cliff": {"ledge_m": r(cfg.cliff.ledge_m), "bottom_y": r(cliff_bottom)},
        "void_trigger": {
            "bottom_y": r(void_bottom),
            "top_y": r(void_top),
            "prisms": [{"footprint": [godot_xz(x, n) for x, n in q]} for q in quads],
        },
        "spawn_area": {
            "polygon": [godot_xz(x, n) for x, n in list(spawn.exterior.coords)[:-1]],
            "open_ground": _rings(spawn_result.open_ground),
            "open_area_m2": r(spawn_result.open_area_m2),
        },
        "water_crossings": [
            {
                "s_start": s_start,
                "s_end": s_end,
                "source": c.source,
                "water_ids": list(c.water_ids),
            }
            for c in crossings
            for s_start, s_end in [ring_stretch(ring, c.s_start, c.s_end)]
        ],
    }

    report = _report(
        config,
        ring,
        boundary,
        spans,
        roadblocks,
        fences,
        touched,
        water_report,
        spawn_result,
        dropped_buildings,
        (low, high, cliff_bottom, void_bottom, void_top),
        quads,
        area,
    )
    folder = work_dir / STAGE_DIR
    write_json(folder / "report.json", report)
    _write_geojson(folder, config, ring, spans, fences, crossings, quads, spawn_result, areas)
    if not spawn_result.passed:
        raise WorldEdgeError(spawn_result.failure(cfg.spawn))
    BOUNDARY_SCHEMA.validate(document, "boundary.json")
    return WorldEdgeResult(document, report, spans, fences, crossings, quads, spawn_result)


def _report(
    config: MapConfig,
    ring: Ring,
    boundary: Boundary,
    spans: Sequence[RoadblockSpan],
    roadblocks: Sequence[dict[str, Any]],
    fences: Sequence[FenceSegment],
    touched: Sequence[tuple[float, float]],
    water: dict[str, Any],
    spawn: SpawnResult,
    dropped: Sequence[dict[str, Any]],
    heights: tuple[float, float, float, float, float],
    quads: Sequence[Quad],
    area: BuildArea,
) -> dict[str, Any]:
    low, high, cliff_bottom, void_bottom, void_top = heights
    fence_types = Counter(f.type for f in fences)
    by_class = Counter(rb["class"] for rb in roadblocks)
    covered = {rb["id"]: (rb["s_start"], rb["s_end"]) for rb in roadblocks}

    def covered_by(a: float, b: float) -> str | None:
        for rid, (s0, s1) in covered.items():
            if _overlap(ring, s0, s1, a, b) >= (b - a) - 1e-6:
                return rid
        return None

    test_area = config.test_area_bbox.offset(-config.origin.x, -config.origin.y)
    return {
        "boundary": {
            "source": boundary.source,
            "length_m": round(ring.length, 3),
            "vertices": ring.n,
        },
        "config": config.world_edge.model_dump(mode="json"),
        "test_area_local": test_area.as_list(),
        "area_bounds_local": area.bounds.as_list(),
        "counts": {
            "roadblocks": len(roadblocks),
            "roadblocks_drivable": sum(1 for rb in roadblocks if rb["drivable"]),
            "roadblocks_walk_only": sum(1 for rb in roadblocks if not rb["drivable"]),
            "area_edge_nodes": sum(len(s.nodes) for s in spans),
            "fences": len(fences),
            "fences_land": fence_types.get("land", 0),
            "fences_water": fence_types.get("water", 0),
            "water_crossings": len(water["crossings"]),
            "water_lines": len(water["lines"]),
            "void_prisms": len(quads),
            "touched_without_crossing": len(touched),
            "dropped_buildings": len(dropped),
        },
        "roadblocks_per_class": dict(sorted(by_class.items())),
        "merged_roadblocks": [
            {"id": s.id, "parts": s.parts, "nodes": [n.node for n in s.nodes]}
            for s in spans
            if s.parts > 1
        ],
        "spans_from_width": [node for s in spans for node in s.from_width],
        "roles": {n.node: n.role for s in spans for n in s.nodes},
        "touched_without_crossing": [
            {
                "s_start": round(a, 3),
                "s_end": round(b, 3),
                "length_m": round(b - a, 3),
                "covered_by_roadblock": covered_by(a, b),
            }
            for a, b in touched
        ],
        "water": water,
        "dropped_buildings": list(dropped),
        "heights": {
            "boundary_min": round(low, 3),
            "boundary_max": round(high, 3),
            "cliff_bottom_y": round(cliff_bottom, 3),
            "void_bottom_y": round(void_bottom, 3),
            "void_top_y": round(void_top, 3),
        },
        "void": {
            "prisms": len(quads),
            "min_ring_distance_m": (
                round(float(ring_distances(ring, quads).min()), 6) if quads else None
            ),
            "arc_excess_m": ARC_EXCESS_M,
        },
        "spawn": spawn.report(),
    }


def _write_geojson(
    folder: Path,
    config: MapConfig,
    ring: Ring,
    spans: Sequence[RoadblockSpan],
    fences: Sequence[FenceSegment],
    crossings: Sequence[WaterCrossing],
    quads: Sequence[Quad],
    spawn: SpawnResult,
    areas: Sequence[WaterFeature],
) -> None:
    features: list[tuple[BaseGeometry, dict[str, Any]]] = [
        (ring.line(), {"kind": "boundary", "length_m": round(ring.length, 3)})
    ]
    for span in spans:
        features.append(
            (
                ring.path_line(span.a, span.b),
                {"kind": "roadblock", "id": span.id, "nodes": [n.node for n in span.nodes]},
            )
        )
    for k, fence in enumerate(fences):
        features.append(
            (
                LineString([ring.point_at(fence.s_start), ring.point_at(fence.s_end)]),
                {"kind": f"fence_{fence.type}", "id": f"fence:{k}"},
            )
        )
    for crossing in crossings:
        features.append(
            (
                ring.path_line(crossing.s_start, crossing.s_end),
                {"kind": "water_crossing", "water_ids": list(crossing.water_ids)},
            )
        )
    for quad in quads:
        features.append((Polygon(quad), {"kind": "void_prism"}))
    features.append((spawn.polygon, {"kind": "spawn_area"}))
    if not spawn.open_ground.is_empty:
        features.append((spawn.open_ground, {"kind": "open_ground"}))
    for water in areas:
        features.append((water.geometry, {"kind": "water_area", "id": water.id}))
    write_geojson(
        folder / "boundary.geojson",
        features,
        config.crs,
        (config.origin.x, config.origin.y),
    )
