"""Roads (stage 4) and the road graph (stage 7), from the OSM `highway` ways.

Everything about roads comes from the map's `roads` block in map.yaml (spec:
`docs/specs/roads-and-road-graph.md`). `build_roads` returns the per-tile meshes
(`road-col`, `path-col`, `bridge_osm_way_<id>[_p<n>]-col`), the `road_graph.json`
document and the stage reports, and writes `04-roads/` and `07-road-graph/`.
"""

from collections import Counter
from collections.abc import Callable, Sequence
from dataclasses import dataclass, field
from pathlib import Path
from typing import Any

import numpy as np
import shapely
from shapely.geometry import LineString, Point

from mapgen.build.area import BuildArea, Tile, tile_id
from mapgen.build.export_glb import MeshNode
from mapgen.build.heightfield import Heightfield
from mapgen.build.roads.bridges import (
    Span,
    WaterIndex,
    build_decks,
    build_ramps,
    clearance_check,
    deck_footprint,
    deck_node_names,
    infer_spans,
    interior_heights_keys,
    set_centre_line,
    settle_span_heights,
    tagged_spans,
    terrain_above_deck,
)
from mapgen.build.roads.decks import Plan, plan_decks, suspected_dips
from mapgen.build.roads.graph import (
    Piece,
    RoadGraph,
    build_graph,
    clip_way,
    connectivity_report,
    godot_point,
    graph_document,
)
from mapgen.build.roads.model import validate_document
from mapgen.build.roads.ramps import GradedRamp, Ramp, RampField
from mapgen.build.roads.surface import (
    TerrainSurface,
    drape_tile,
    ground_runs,
    insert_tile_crossings,
    merged_surfaces,
    surface_normals,
)
from mapgen.build.roads.tags import is_bridge, is_tunnel, resolve_attributes, road_class
from mapgen.build.stage1_load import LoadResult
from mapgen.build.stage_io import write_geojson, write_json
from mapgen.build.terrain import TerrainChunk
from mapgen.config import MapConfig, RoadsConfig
from mapgen.geo import map_to_wgs84

ROAD_NODE = "road-col"
PATH_NODE = "path-col"
CONNECTIVITY_THRESHOLD = 0.95  # spec: checked on the full-area build only


@dataclass
class RoadsResult:
    document: dict[str, Any]  # road_graph.json
    tile_nodes: dict[str, list[MeshNode]] = field(default_factory=dict)
    report: dict[str, Any] = field(default_factory=dict)  # 04-roads/report.json
    graph_report: dict[str, Any] = field(default_factory=dict)  # 07-road-graph/report.json

    @property
    def edge_count(self) -> int:
        return len(self.document["edges"])

    @property
    def node_count(self) -> int:
        return len(self.document["nodes"])


def _skip(way_id: int, cls: str | None, reason: str, highway: str | None) -> dict[str, Any]:
    return {"osm_way": f"osm:way/{way_id}", "highway": highway, "class": cls, "reason": reason}


def prepare_pieces(
    loaded: LoadResult, area: BuildArea, roads: RoadsConfig
) -> tuple[list[Piece], dict[str, Any]]:
    """The included way pieces, and the way counts, skips and defaults for the report."""
    pieces: list[Piece] = []
    skipped: list[dict[str, Any]] = []
    defaults: Counter[str] = Counter()
    invalid: Counter[str] = Counter()
    included = not_in_area = both_tags = 0
    for way in sorted(loaded.roads, key=lambda w: w.osm_id):
        clipped = clip_way(way, area.bounds)
        if not clipped.touches_area:
            not_in_area += 1
            continue
        highway = way.tags.get("highway")
        cls = road_class(way.tags)
        if cls is None or cls not in roads.classes:
            skipped.append(_skip(way.osm_id, cls, "class_not_configured", highway))
            continue
        if way.tags.get("area") == "yes":
            skipped.append(_skip(way.osm_id, cls, "area", highway))
            continue
        attrs = resolve_attributes(way.tags, cls, roads)
        if not attrs.drivable and not attrs.walkable:
            skipped.append(_skip(way.osm_id, cls, "no_access", highway))
            continue
        if not clipped.pieces:  # the line only touches the area in a point
            skipped.append(_skip(way.osm_id, cls, "outside_area", highway))
            continue
        included += 1
        if is_bridge(way.tags) and is_tunnel(way.tags):
            both_tags += 1
        for name in ("lanes", "width"):
            if getattr(attrs, f"{name}_source") == "default":
                defaults[name] += 1
        if attrs.drivable and attrs.speed_source == "default":
            defaults["speed_kmh"] += 1
        invalid.update(attrs.invalid)
        for index, (xy, keys, node_ids) in enumerate(clipped.pieces):
            piece = Piece(way, attrs, index, xy, keys, node_ids)
            piece.edge_nodes = clipped.edge_nodes[index]
            pieces.append(piece)
    skipped.sort(key=lambda s: (s["reason"], s["osm_way"]))
    # Ways the OSM reader couldn't read (nodes missing from the file, fewer than 2 nodes)
    # have no known position, so they aren't counted as in the area; listed separately.
    unreadable = sorted(
        ({"osm_way": f.id, "reason": f.reason} for f in loaded.roads_skipped),
        key=lambda u: (u["reason"], u["osm_way"]),
    )
    counts = {
        "highway_ways_in_area": included + len(skipped),
        "included": included,
        "skipped": len(skipped),
        "not_in_area": not_in_area,
        "unreadable": len(unreadable),
        "pieces": len(pieces),
        "bridge_and_tunnel_tagged": both_tags,
    }
    info = {
        "ways": counts,
        "skipped_reasons": dict(sorted(Counter(s["reason"] for s in skipped).items())),
        "skipped": skipped,
        "unreadable": unreadable,
        "defaults_used": dict(sorted(defaults.items())),
        "invalid_tags": dict(sorted(invalid.items())),
    }
    return pieces, info


def build_roads(
    config: MapConfig,
    roads: RoadsConfig,
    area: BuildArea,
    tiles: Sequence[Tile],
    chunks: dict[str, TerrainChunk],
    heightfield: Heightfield,
    loaded: LoadResult,
    work_dir: Path,
    log: Callable[[str], None] = print,
) -> RoadsResult:
    """Run stages 4 and 7. Writes `work_dir/04-roads/` and `work_dir/07-road-graph/`."""
    offset = roads.surface_offset_m
    terrain = TerrainSurface(area, tiles, chunks)
    pieces, info = prepare_pieces(loaded, area, roads)
    water = WaterIndex.build(
        loaded.water_areas, loaded.water_lines, roads.bridge.inferred_waterways
    )
    inferred, crossings = infer_spans(pieces, water)
    tagged = tagged_spans(pieces, "bridge")
    plans, underpasses_left_out = plan_decks(
        pieces, tagged, inferred, terrain, roads.bridge, offset, loaded.deck_points
    )
    bridges: list[Span] = [p.span for p in plans if p.span is not None]
    spans: list[Span] = bridges + tagged_spans(pieces, "tunnel")
    for span in spans:
        set_centre_line(span)
    water_spans = [p.span for p in plans if p.source == "inferred" and p.span is not None]

    # Ground runs meeting a deck (at its ends, or joining it in the middle) end flat, and
    # lose the part under the deck.
    interior = {key for span in spans for key in interior_heights_keys(span)}
    bridge_ends = {key for span in bridges for key in span.end_keys()}
    bridge_ends |= {key for span in bridges for key in interior_heights_keys(span)}
    runs = ground_runs(pieces, bridge_ends)
    decks: dict[str, Any] = {}
    for span in bridges:
        footprint = deck_footprint(span)
        for key in (*span.end_keys(), *interior_heights_keys(span)):
            decks[key] = shapely.union(decks[key], footprint) if key in decks else footprint

    # Ramps where a ground way meets a deck end (not at area-edge cuts or inside spans).
    run_ends = {
        r.piece.keys[i] for r in runs for i in (r.start, r.end) if not r.piece.at_area_edge(i)
    }
    ramp_ends = {
        k for span in bridges for k in span.end_keys() if k in run_ends and k not in interior
    }
    all_ramps = build_ramps(bridges, ramp_ends, terrain, offset, roads.bridge.ramp_length_m)
    # Walk-only deck ends that meet no path get a gradient-limited ramp along the path
    # instead of the standard one.
    graded_plans = {
        e.key: e.graded for p in plans for e in p.ends if e.graded and e.graded.get("fits")
    }
    ramps = [r for r in all_ramps if r.node not in graded_plans]
    graded = [
        GradedRamp(
            r,
            graded_plans[r.node]["grade"],
            graded_plans[r.node]["path"],
            graded_plans[r.node]["path_half_width_m"],
            graded_plans[r.node]["zone_length_m"],
        )
        for r in all_ramps
        if r.node in graded_plans
    ]
    field = RampField(ramps, graded=graded)
    for piece in pieces:
        piece.z = field.apply(piece.xy, terrain.height(piece.xy) + offset)
    settle_span_heights(spans, pieces, terrain, offset, field)

    graph = build_graph(pieces)
    document = graph_document(graph, config.id, area.name)
    validate_document(document)

    drivable_end = {
        key: part.piece.attrs.drivable
        for span in bridges
        for key, part in zip(span.end_keys(), (span.parts[0], span.parts[-1]), strict=True)
    }
    zones_road = [r.zone() for r in ramps if drivable_end[r.node]]
    zones_road += [g.zone() for g in graded if drivable_end[g.edge.node]]
    zones_path = [r.zone() for r in ramps if not drivable_end[r.node]]
    zones_path += [g.zone() for g in graded if not drivable_end[g.edge.node]]
    road, path = merged_surfaces(runs, decks, zones_road, zones_path)
    # Cut every surface at the ramped deck ends: no surface reaches under a deck end, so
    # the only surface meeting the end edge is the ramp (exactly at the deck's height).
    footprints = {k: deck_footprint(span) for span in bridges for k in span.end_keys()}
    under = [r.under(footprints[r.node]) for r in [*ramps, *(g.edge for g in graded)]]
    if under:
        cut = shapely.union_all(under)
        road, path = road.difference(cut), path.difference(cut)
    road_t, path_t = insert_tile_crossings(road, area), insert_tile_crossings(path, area)

    tile_nodes: dict[str, list[MeshNode]] = {tile.id: [] for tile in tiles}
    surface_stats = []
    for tile in tiles:
        stats: dict[str, Any] = {"id": tile.id}
        for name, geom, material in ((ROAD_NODE, road_t, "road"), (PATH_NODE, path_t, "path")):
            mesh = drape_tile(geom, tile, terrain, offset, field)
            if mesh is None:
                continue
            tile_nodes[tile.id].append(
                MeshNode(
                    name,
                    mesh.vertices,
                    mesh.faces,
                    surface_normals(mesh, heightfield),
                    material=material,
                )
            )
            stats[material] = {
                "vertices": len(mesh.vertices),
                "faces": len(mesh.faces),
                "area_m2": round(mesh.area, 1),
            }
        surface_stats.append(stats)

    decks = build_decks(bridges, area, roads.bridge)
    names = deck_node_names(decks)
    deck_entries = []
    for part in decks:
        name = names[(part.way_id, part.tile)]
        tid = tile_id(*part.tile)
        tile_nodes[tid].append(
            MeshNode(name, part.mesh.vertices, part.mesh.faces, material="bridge")
        )
        deck_entries.append({"node": name, "tile": tid, "osm_way": f"osm:way/{part.way_id}"})

    ramp_nodes = {r.node for r in ramps}
    above = [terrain_above_deck(s, terrain) for s in bridges]
    limit = roads.bridge.ground_above_deck_warning_m
    deck_warnings = [
        {
            "warning": "ground_above_deck",
            "osm_ways": a["osm_ways"],
            "max_rise_m": a["max_rise_m"],
            "area_m2": a["area_m2"],
            "from_end_m": a["from_end_m"],
        }
        for a in above
        if a["max_rise_m"] is not None and a["max_rise_m"] > limit
    ]
    for w in deck_warnings:
        log(
            f"[roads] warning: ground {w['max_rise_m']} m above the deck of "
            f"{', '.join(w['osm_ways'])} ({w['area_m2']} m², {w['from_end_m']} m from an end)"
        )
    dips = [
        _dip_entry(d, config, terrain, offset)
        for d in suspected_dips(pieces, terrain, offset, roads.bridge)
    ]
    spacing = min(heightfield.dx, heightfield.dy) / 2
    warnings = [w for s in bridges if (w := clearance_check(s, terrain, roads.bridge, spacing))]
    suspected = [_suspected(span, config) for span in water_spans]
    excluded = [
        {
            "osm_way": f"osm:way/{c.way_id}",
            "water": c.water,
            "reason": c.reason,
            "position_local": [round(c.position[0], 2), round(c.position[1], 2)],
        }
        for c in crossings
        if c.reason is not None
    ]

    report: dict[str, Any] = {
        **info,
        "surface_offset_m": offset,
        "surfaces": {
            "road_area_m2": round(float(road.area), 1),
            "path_area_m2": round(float(path.area), 1),
            "tiles": surface_stats,
        },
        "bridges": [_plan_entry(p, terrain, offset, ramp_nodes) for p in plans],
        "underpasses_left_out": underpasses_left_out,
        "tunnels": [_span_entry(s) for s in spans if s.kind == "tunnel"],
        # Tunnel ways under a deck: draped road/path surfaces with collision (tunnel: true).
        "tunnels_under_decks": sorted({f"osm:way/{p.way_id}" for p in pieces if p.under_deck}),
        "decks": deck_entries,
        "ramps": [_ramp_entry(r, terrain, offset) for r in ramps],
        "graded_ramps": [
            {
                **g.as_dict(),
                **{k: graded_plans[g.edge.node][k] for k in _GRADED_KEYS},
            }
            for g in graded
        ],
        "terrain_above_deck": above,
        "warnings": deck_warnings,
        "suspected_dips": dips,
        "clearance_warnings": warnings,
        "suspected_missing_bridges": suspected,
        "excluded_crossings": excluded,
        "excluded_crossing_counts": dict(sorted(Counter(e["reason"] for e in excluded).items())),
        "water": {
            "areas": len(loaded.water_areas),
            "lines": len(loaded.water_lines),
            "inferred_waterways": list(roads.bridge.inferred_waterways),
            "relations_left_out": [{"id": s.id, "reason": s.reason} for s in loaded.water_skipped],
        },
    }
    graph_report = connectivity_report(graph)
    share = graph_report["weak_components"]["largest_share"]
    full = area.name == "full"
    graph_report["threshold"] = CONNECTIVITY_THRESHOLD if full else None
    graph_report["meets_threshold"] = (
        (share is not None and share >= CONNECTIVITY_THRESHOLD) if full else None
    )

    _write_stage(work_dir, config, graph, runs, road, path, bridges, report, graph_report)
    if not graph.edges:
        log("[roads] no included road ways in the area: road_graph.json has no nodes or edges")
    share_text = f"{share:.1%}" if share is not None else "n/a"
    log(
        f"[roads] {len(graph.edges)} edges, {len(graph.nodes)} nodes, "
        f"{len(bridges)} bridge spans, {len(suspected)} suspected missing bridges, "
        f"{len(warnings)} clearance warnings; largest drivable component {share_text}"
    )
    if full and graph_report["meets_threshold"] is False:
        log(
            f"[roads] warning: the largest drivable component is under {CONNECTIVITY_THRESHOLD:.0%}"
        )
    return RoadsResult(document, tile_nodes, report, graph_report)


_GRADED_KEYS = ("length_m", "nominal_length_m", "height_difference_m")


def _graded_entry(graded: dict[str, Any] | None) -> dict[str, Any] | None:
    """A walk-only end's graded ramp for the report (without its geometry)."""
    if graded is None:
        return None
    return {k: v for k, v in graded.items() if k not in ("edge", "path")}


def _plan_entry(
    plan: Plan, terrain: TerrainSurface, offset: float, ramp_nodes: set[str]
) -> dict[str, Any]:
    """A bridge span in the report: how its line was found and where its deck ends."""
    span = plan.span
    assert span is not None
    first = span.parts[0].piece
    ends = []
    for end, z in zip(plan.ends, (span.z0, span.z1), strict=True):
        xy = span.xy[0] if end is plan.ends[0] else span.xy[-1]
        ground = float(terrain.height(xy[None])[0]) + offset
        ends.append(
            {
                "node": end.key,
                "kind": end.kind,
                "reason": end.reason,
                "extension_m": round(end.extension_m, 3),
                "raised_m": end.raised_m,
                "searched_m": end.searched_m,
                "search_stop": end.search_stop,
                "graded_ramp": _graded_entry(end.graded),
                "ramp": end.key in ramp_nodes,
                "height": round(z, 3),
                "height_above_road_m": round(z - ground, 3),
            }
        )
    return {
        "osm_ways": [f"osm:way/{w}" for w in span.way_ids],
        **first.attrs.names,
        "source": plan.source,
        "method": plan.method,
        "fallback_reason": plan.fallback_reason,
        "lidar": plan.lidar,
        "lidar_end_heights": plan.lidar_ends,
        "geometric_end_heights": plan.geometric_ends,
        "end_heights": [round(span.z0, 3), round(span.z1, 3)],
        "length_m": round(span.length, 2),
        "ends": ends,
        "lower_ways": [f"osm:way/{w}" for w in plan.lower_ways],
        "water": plan.water,
    }


def _ramp_entry(ramp: Ramp, terrain: TerrainSurface, offset: float) -> dict[str, Any]:
    """A ramp: its geometry, and the height difference it takes up over its length."""
    base = float(terrain.height(np.array([ramp.end]))[0]) + offset
    return {**ramp.as_dict(), "height_difference_m": round(ramp.z_end - base, 3)}


def _dip_entry(
    dip: dict[str, Any], config: MapConfig, terrain: TerrainSurface, offset: float
) -> dict[str, Any]:
    way, other = dip["way"], dip["other"]
    x, y = dip["point"]
    z = float(terrain.height(np.array([[x, y]]))[0]) + offset
    wx, wy = x + config.origin.x, y + config.origin.y
    lon, lat = map_to_wgs84(wx, wy, config.crs)
    return {
        "osm_way": f"osm:way/{way.way_id}",
        "class": way.attrs.road_class,
        **way.attrs.names,
        "crossing_way": f"osm:way/{other.way_id}",
        "crossing_class": other.attrs.road_class,
        "crossing_name": other.attrs.names["name"],
        "dip_m": dip["depth_m"],
        "position_local": godot_point(x, y, z),
        "position_world": [round(wx, 3), round(wy, 3), round(z, 3)],
        "crs": config.crs,
        "position_wgs84": {"lat": round(lat, 7), "lon": round(lon, 7)},
    }


def _span_entry(span: Span) -> dict[str, Any]:
    start, end = span.end_keys()
    return {
        "osm_ways": [f"osm:way/{w}" for w in span.way_ids],
        "source": span.source,
        "from": start,
        "to": end,
        "length_m": round(span.length, 2),
        "end_heights": [round(span.z0, 3), round(span.z1, 3)],
    }


def _suspected(span: Span, config: MapConfig) -> dict[str, Any]:
    piece = span.parts[0].piece
    assert span.crossing is not None
    x, y = span.crossing
    s = LineString(span.xy).project(Point(x, y))
    z = float(span.line_z(s))
    wx, wy = x + config.origin.x, y + config.origin.y
    lon, lat = map_to_wgs84(wx, wy, config.crs)
    return {
        "osm_way": f"osm:way/{piece.way_id}",
        "class": piece.attrs.road_class,
        **piece.attrs.names,
        "water": span.water[0],
        "water_features": span.water,
        "position_local": godot_point(x, y, z),
        "position_world": [round(wx, 3), round(wy, 3), round(z, 3)],
        "crs": config.crs,
        "position_wgs84": {"lat": round(lat, 7), "lon": round(lon, 7)},
    }


def _write_stage(
    work_dir: Path,
    config: MapConfig,
    graph: RoadGraph,
    runs: Sequence[Any],
    road: Any,
    path: Any,
    bridges: Sequence[Span],
    report: dict[str, Any],
    graph_report: dict[str, Any],
) -> None:
    origin = (config.origin.x, config.origin.y)
    features: list[tuple[Any, dict[str, Any]]] = []
    lines = [LineString(r.xy) for r in runs]
    tree = shapely.STRtree(lines) if lines else None
    for surface, geom in (("road", road), ("path", path)):
        parts = list(getattr(geom, "geoms", [geom])) if not geom.is_empty else []
        for polygon in parts:
            hits = sorted(tree.query(polygon, predicate="intersects")) if tree else []
            ways = sorted({runs[i].piece.way_id for i in hits})
            classes = sorted({runs[i].piece.attrs.road_class for i in hits})
            layers = sorted({runs[i].piece.attrs.layer for i in hits})
            features.append(
                (
                    polygon,
                    {
                        "surface": surface,
                        "classes": classes,
                        "layers": layers,
                        "osm_ways": [f"osm:way/{w}" for w in ways],
                    },
                )
            )
    for span in bridges:
        width = max(p.piece.attrs.width_m for p in span.parts)
        outline = LineString(span.xy).buffer(width / 2, cap_style="flat")
        first = span.parts[0].piece.attrs
        features.append(
            (
                outline,
                {
                    "surface": "bridge",
                    "classes": sorted({p.piece.attrs.road_class for p in span.parts}),
                    "layers": [first.layer],
                    "osm_ways": [f"osm:way/{w}" for w in span.way_ids],
                    "source": span.source,
                },
            )
        )
    write_geojson(work_dir / "04-roads" / "roads.geojson", features, config.crs, origin)
    write_json(work_dir / "04-roads" / "report.json", report)

    graph_features: list[tuple[Any, dict[str, Any]]] = []
    for node in sorted(graph.nodes.values(), key=lambda n: n.key):
        x, y, z = node.position
        graph_features.append(
            (
                Point(x, y),
                {"id": node.key, "kind": node.kind, "osm_node": node.osm_node, "z": round(z, 3)},
            )
        )
    for edge in graph.edges:
        a = edge.attrs
        graph_features.append(
            (
                LineString(edge.points[:, :2]),
                {
                    "id": edge.id,
                    "from": edge.from_key,
                    "to": edge.to_key,
                    "class": a.road_class,
                    "drivable": a.drivable,
                    "walkable": a.walkable,
                    "oneway": a.oneway != 0,
                    "speed_kmh": a.speed_kmh,
                    "lanes": a.lanes,
                    "bridge_source": edge.bridge_source,
                    "tunnel": a.tunnel,
                    "layer": a.layer,
                    **a.names,
                    "z": [round(float(v), 3) for v in edge.points[:, 2]],
                },
            )
        )
    write_geojson(work_dir / "07-road-graph" / "graph.geojson", graph_features, config.crs, origin)
    write_json(work_dir / "07-road-graph" / "report.json", graph_report)


__all__ = ["PATH_NODE", "ROAD_NODE", "RoadsResult", "build_roads"]
