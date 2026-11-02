"""Roadblocks where roads leave the playable area (docs/specs/world-edge-pipeline.md).

Every `area_edge` node of the road graph belongs to exactly one roadblock. A node's span
is the stretch of the ring covered by the road or path surface (stage 4's merged
surfaces, bridge decks included) that contains it, plus `clearance_m` on each side. Spans
whose fence gap is under `merge_distance_m` are merged (repeatedly), so a merged
roadblock also covers the boundary between them. When no surface reaches the ring at a
node, its span is the node ± half its edge's width (listed in the report).

A surface stretch on the ring that holds no `area_edge` node is a road touching the
boundary without crossing it: it gets no roadblock, and the report lists it.
"""

from collections.abc import Mapping, Sequence
from dataclasses import dataclass, field
from typing import Any, Literal

from shapely.geometry.base import BaseGeometry

from mapgen.build.world_edge.ring import Interval, Ring, interval_gap, merge_two

Role = Literal["spawn_despawn", "turn_around"]
NODE_TOLERANCE_M = 1e-3  # a node this close to a surface stretch lies on it


class RoadblockError(Exception):
    """The road graph's edge nodes can't be turned into roadblocks."""


@dataclass(frozen=True)
class EdgeNode:
    """An `area_edge` node of the road graph with its one edge (local x, n)."""

    node: str
    edge: str
    x: float
    n: float
    y: float  # the graph's height at the node
    drivable: bool
    walkable: bool
    road_class: str
    width_m: float
    width_source: str
    bridge: bool = False  # its edge is a bridge deck edge (the node is at deck height)

    @property
    def role(self) -> Role:
        return "spawn_despawn" if self.drivable else "turn_around"


@dataclass
class RoadblockSpan:
    """A roadblock's stretch of the ring (`a`, `b` circular) and its nodes."""

    a: float
    b: float
    nodes: list[EdgeNode]
    parts: int = 1  # separate spans merged into this one
    from_width: list[str] = field(default_factory=list)  # nodes with no surface on the ring

    @property
    def interval(self) -> Interval:
        return (self.a, self.b)

    @property
    def id(self) -> str:
        return f"roadblock:{min(n.node for n in self.nodes)}"


def edge_nodes(graph: Mapping[str, Any]) -> list[EdgeNode]:
    """The `area_edge` nodes of a `road_graph.json` document with their edges, by node id.

    Raises `RoadblockError` when one of them doesn't have exactly one edge.
    """
    nodes = {n["id"]: n for n in graph["nodes"] if n["kind"] == "area_edge"}
    edges_of: dict[str, list[dict[str, Any]]] = {k: [] for k in nodes}
    for edge in graph["edges"]:
        for end in (edge["from"], edge["to"]):
            if end in edges_of:
                edges_of[end].append(edge)
    result: list[EdgeNode] = []
    for node_id in sorted(nodes):
        edges = edges_of[node_id]
        if len(edges) != 1:
            raise RoadblockError(f"area_edge node {node_id} has {len(edges)} edges, not 1")
        edge = edges[0]
        x, y, z = nodes[node_id]["position"]
        result.append(
            EdgeNode(
                node=node_id,
                edge=edge["id"],
                x=float(x),
                n=-float(z),
                y=float(y),
                drivable=bool(edge["drivable"]),
                walkable=bool(edge["walkable"]),
                road_class=str(edge["class"]),
                width_m=float(edge["width_m"]),
                width_source=str(edge["width_source"]),
                bridge=bool(edge.get("bridge", False)),
            )
        )
    return result


def surface_intervals(ring: Ring, surface: BaseGeometry | None) -> list[Interval]:
    """The stretches of the ring covered by the road and path surfaces."""
    if surface is None or surface.is_empty:
        return []
    return ring.intervals(surface)


def node_spans(
    ring: Ring, nodes: Sequence[EdgeNode], stretches: Sequence[Interval], clearance_m: float
) -> list[RoadblockSpan]:
    """One span per node: the surface stretch containing it (or ± half the width) plus
    `clearance_m` on each side."""
    spans: list[RoadblockSpan] = []
    for node in nodes:
        s = ring.project(node.x, node.n)
        hits = [iv for iv in stretches if ring.contains_s(iv, s, NODE_TOLERANCE_M)]
        if hits:
            a, b = max(hits, key=lambda iv: iv[1] - iv[0])
            from_width: list[str] = []
        else:
            a, b = s - node.width_m / 2, s + node.width_m / 2
            from_width = [node.node]
        length = min(b - a + 2 * clearance_m, ring.length)
        start = ring.wrap(a - clearance_m)
        spans.append(RoadblockSpan(start, start + length, [node], 1, from_width))
    return spans


def merge_spans(
    ring: Ring, spans: Sequence[RoadblockSpan], merge_distance_m: float
) -> list[RoadblockSpan]:
    """Spans whose fence gap is under `merge_distance_m` (or that overlap) merged into one,
    repeatedly, sorted by start. Raises `RoadblockError` if they cover the whole ring."""
    current = sorted(
        (RoadblockSpan(s.a, s.b, list(s.nodes), s.parts, list(s.from_width)) for s in spans),
        key=lambda s: s.a,
    )
    while len(current) > 1:
        merged = False
        for i in range(len(current)):
            first, second = current[i], current[(i + 1) % len(current)]
            if interval_gap(first.interval, second.interval, ring.length) < merge_distance_m:
                a, b = merge_two(first.interval, second.interval, ring.length)
                if b - a >= ring.length:
                    raise RoadblockError("roadblocks would cover the whole boundary")
                joined = RoadblockSpan(
                    ring.wrap(a),
                    ring.wrap(a) + (b - a),
                    sorted([*first.nodes, *second.nodes], key=lambda n: n.node),
                    first.parts + second.parts,
                    sorted([*first.from_width, *second.from_width]),
                )
                rest = [s for k, s in enumerate(current) if k not in (i, (i + 1) % len(current))]
                current = sorted([*rest, joined], key=lambda s: s.a)
                merged = True
                break
        if not merged:
            break
    return current


def roadblock_spans(
    ring: Ring,
    nodes: Sequence[EdgeNode],
    surface: BaseGeometry | None,
    clearance_m: float,
    merge_distance_m: float,
) -> list[RoadblockSpan]:
    """The roadblocks' spans for the `area_edge` nodes, merged, sorted by start."""
    stretches = surface_intervals(ring, surface)
    return merge_spans(ring, node_spans(ring, nodes, stretches, clearance_m), merge_distance_m)


def touched_stretches(
    ring: Ring, nodes: Sequence[EdgeNode], stretches: Sequence[Interval]
) -> list[Interval]:
    """Surface stretches on the ring that hold no `area_edge` node: roads touching the
    boundary without crossing it."""
    positions = [ring.project(n.x, n.n) for n in nodes]
    return [
        iv
        for iv in stretches
        if not any(ring.contains_s(iv, s, NODE_TOLERANCE_M) for s in positions)
    ]


def widest(nodes: Sequence[EdgeNode]) -> EdgeNode:
    """The node whose edge is widest (ties: drivable first, then the lowest edge id)."""
    return min(nodes, key=lambda n: (-n.width_m, not n.drivable, n.edge))
