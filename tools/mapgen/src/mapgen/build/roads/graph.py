"""The road graph: ways clipped to the build area, nodes at OSM nodes, edges between them.

Nodes exist only at OSM nodes (every end of an included way piece, every OSM node used
more than once by the included ways, and the ends of inferred bridge spans) and at the
cuts where a way leaves the build area. Both cuts and OSM nodes on the area edge where a
way goes on outside are of kind `area_edge`; the latter keep their `osm:node/<id>` id.
Where a bridge deck meets the road away from an OSM node, a `span_end` node
(`span:way/<id>:<n>`, no OSM node) is inserted into the way.
A way crossing another without a shared OSM node is never connected to it. Ids are
stable: nodes `osm:node/<id>` or `cut:way/<id>:<n>`, edges `osm:way/<id>#<n>` (the
edge's index along the way).
"""

from collections import Counter, defaultdict
from collections.abc import Iterable, Sequence
from dataclasses import dataclass, field
from typing import Any, Literal

import numpy as np

from mapgen.build.roads.tags import WayAttributes
from mapgen.build.stage1_load import LocalRoadWay
from mapgen.geo import BBox

NodeKind = Literal["junction", "joint", "dead_end", "area_edge", "span_end"]
BridgeSource = Literal["osm", "inferred", "inferred_underpass"]
COORD_DECIMALS = 6  # 1 µm in road_graph.json (well under float32 rounding at ~100 m)


def osm_node_key(node_id: int) -> str:
    return f"osm:node/{node_id}"


def cut_key(way_id: int, n: int) -> str:
    return f"cut:way/{way_id}:{n}"


def span_key(way_id: int, n: int) -> str:
    return f"span:way/{way_id}:{n}"


def arc_lengths(xy: np.ndarray) -> np.ndarray:
    """Cumulative distance along a polyline, starting at 0."""
    steps = np.linalg.norm(np.diff(np.asarray(xy, dtype=float), axis=0), axis=1)
    return np.concatenate([[0.0], np.cumsum(steps)])


SNAP_M = 1e-6  # a position this close to an existing vertex uses that vertex


def insert_vertices(piece: "Piece", positions: list[float], keys: list[str]) -> list[int]:
    """Insert vertices at distances `positions` along the piece (not at its existing
    vertices) with the given node keys; returns the vertex index of each position.

    A position within `SNAP_M` of an existing vertex uses that vertex (and its key).
    Inserted vertices have no OSM node. `edge_nodes` and `inferred` are re-indexed.
    """
    arc = arc_lengths(piece.xy)
    new_points: list[tuple[float, int, np.ndarray, str]] = []  # (pos, order, xy, key)
    for order, (pos, key) in enumerate(zip(positions, keys, strict=True)):
        near = int(np.argmin(np.abs(arc - pos)))
        if abs(arc[near] - pos) <= SNAP_M:
            continue
        k = int(np.clip(np.searchsorted(arc, pos, side="right") - 1, 0, len(arc) - 2))
        t = (pos - arc[k]) / (arc[k + 1] - arc[k])
        xy = piece.xy[k] + t * (piece.xy[k + 1] - piece.xy[k])
        new_points.append((pos, order, xy, key))
    new_points.sort(key=lambda p: p[0])
    old_to_new: list[int] = []
    xy_out: list[np.ndarray] = []
    keys_out: list[str] = []
    ids_out: list[int | None] = []
    inserted: dict[int, int] = {}
    j = 0
    for i in range(len(piece.xy)):
        while j < len(new_points) and new_points[j][0] < arc[i]:
            inserted[new_points[j][1]] = len(xy_out)
            xy_out.append(new_points[j][2])
            keys_out.append(new_points[j][3])
            ids_out.append(None)
            j += 1
        old_to_new.append(len(xy_out))
        xy_out.append(piece.xy[i])
        keys_out.append(piece.keys[i])
        ids_out.append(piece.node_ids[i])
    piece.xy = np.array(xy_out)
    piece.keys, piece.node_ids = keys_out, ids_out
    piece.edge_nodes = frozenset(old_to_new[i] for i in piece.edge_nodes)
    piece.inferred = [(old_to_new[a], old_to_new[b]) for a, b in piece.inferred]
    piece.decks = [(old_to_new[a], old_to_new[b], src) for a, b, src in piece.decks]
    result = []
    new_arc = arc_lengths(piece.xy)
    for order, pos in enumerate(positions):
        if order in inserted:
            result.append(inserted[order])
        else:
            result.append(int(np.argmin(np.abs(new_arc - pos))))
    return result


def clip_segment(
    p: np.ndarray, q: np.ndarray, b: BBox
) -> tuple[float, float, int | None, int | None] | None:
    """The part of segment p -> q inside the closed box, as (t0, t1, edge0, edge1).

    Liang-Barsky. `edge0` / `edge1` name the box edge (0 min_x, 1 max_x, 2 min_y, 3 max_y)
    that limits the part at t0 / t1, or None where the part reaches p / q. None when the
    segment misses the box.
    """
    dx, dy = float(q[0] - p[0]), float(q[1] - p[1])
    t0, t1 = 0.0, 1.0
    e0: int | None = None
    e1: int | None = None
    checks = (
        (-dx, float(p[0]) - b.min_x),
        (dx, b.max_x - float(p[0])),
        (-dy, float(p[1]) - b.min_y),
        (dy, b.max_y - float(p[1])),
    )
    for edge, (pv, qv) in enumerate(checks):
        if pv == 0:
            if qv < 0:
                return None
            continue
        r = qv / pv
        if pv < 0:
            if r > t0:
                t0, e0 = r, edge
        elif r < t1:
            t1, e1 = r, edge
    if t0 > t1:
        return None
    return t0, t1, e0, e1


def _point_on_edge(p: np.ndarray, q: np.ndarray, t: float, edge: int, b: BBox) -> np.ndarray:
    """p + t (q - p), with the coordinate of the box edge it lies on set exactly."""
    point = p + t * (q - p)
    point = np.array([min(max(point[0], b.min_x), b.max_x), min(max(point[1], b.min_y), b.max_y)])
    point[edge // 2] = (b.min_x, b.max_x, b.min_y, b.max_y)[edge]
    return point


@dataclass
class Piece:
    """A part of an included way inside the build area, in way order.

    `keys[i]` is the graph node key of vertex i (an OSM node or a cut); `node_ids[i]` its
    OSM node id (None for a cut). `z` (road surface heights) is filled by the build.
    """

    way: LocalRoadWay
    attrs: WayAttributes
    index: int  # the piece's index along the way
    xy: np.ndarray  # (n, 2)
    keys: list[str]
    node_ids: list[int | None]
    z: np.ndarray = field(default_factory=lambda: np.zeros(0))
    # Water-inferred bridge spans as inclusive vertex ranges (start < end), found by
    # `bridges.infer_spans` before the deck extents are known.
    inferred: list[tuple[int, int]] = field(default_factory=list)
    # The final bridge decks on this piece: inclusive vertex ranges and their source.
    decks: list[tuple[int, int, BridgeSource]] = field(default_factory=list)
    # A tunnel piece that passes under a deck: draped like a ground way (tunnel stays true).
    under_deck: bool = False
    # End vertices that are OSM nodes on the area edge where the way continues outside.
    edge_nodes: frozenset[int] = frozenset()

    @property
    def way_id(self) -> int:
        return self.way.osm_id

    def is_cut(self, i: int) -> bool:
        return self.keys[i].startswith("cut:")

    @property
    def tunnel_span(self) -> bool:
        """A tunnel piece in the graph only (no mesh): not under a deck."""
        return self.attrs.tunnel and not self.under_deck

    def deck_source(self, a: int, b: int) -> "BridgeSource | None":
        """The source of the deck that covers vertices a..b, or None (ground)."""
        for start, end, source in self.decks:
            if start <= a and b <= end:
                return source
        return None

    def at_area_edge(self, i: int) -> bool:
        """A cut, or an OSM node on the area edge where the way leaves the area."""
        return self.is_cut(i) or i in self.edge_nodes


@dataclass(frozen=True)
class ClippedWay:
    pieces: list[tuple[np.ndarray, list[str], list[int | None]]]
    nodes_inside: int
    touches_area: bool
    # Per piece: its end vertices that are OSM nodes where the way continues outside.
    edge_nodes: list[frozenset[int]] = field(default_factory=list)


def clip_way(way: LocalRoadWay, bounds: BBox) -> ClippedWay:
    """The way's parts inside the closed box, with `area_edge` cut points where it leaves.

    A way that leaves and re-enters becomes several parts. Segments that only touch the
    box in a point are dropped. A part that ends on an OSM node on the box edge, where the
    way goes on outside, lists that end in `edge_nodes`.
    """
    xy = way.xy
    inside = (
        (xy[:, 0] >= bounds.min_x)
        & (xy[:, 0] <= bounds.max_x)
        & (xy[:, 1] >= bounds.min_y)
        & (xy[:, 1] <= bounds.max_y)
    )
    pieces: list[tuple[np.ndarray, list[str], list[int | None]]] = []
    edge_nodes: list[frozenset[int]] = []
    # (point, key, OSM node id, index in the way; -1 for a cut)
    current: list[tuple[np.ndarray, str, int | None, int]] | None = None
    last = len(xy) - 1
    cuts = 0
    touches = bool(inside.any())

    def close() -> None:
        nonlocal current
        if current is not None and len(current) >= 2:
            pieces.append(
                (
                    np.array([c[0] for c in current]),
                    [c[1] for c in current],
                    [c[2] for c in current],
                )
            )
            # A piece starts or ends on a node, not at a cut, only where the way does, or
            # where it continues outside the box from a node on the box edge.
            ends = set()
            if current[0][3] > 0:
                ends.add(0)
            if 0 <= current[-1][3] < last:
                ends.add(len(current) - 1)
            edge_nodes.append(frozenset(ends))
        current = None

    def node(i: int) -> tuple[np.ndarray, str, int | None, int]:
        return (xy[i].astype(float), osm_node_key(way.node_ids[i]), way.node_ids[i], i)

    for i in range(len(xy) - 1):
        p, q = xy[i].astype(float), xy[i + 1].astype(float)
        clipped = clip_segment(p, q, bounds)
        if clipped is None:
            close()
            continue
        touches = True
        t0, t1, e0, e1 = clipped
        if t0 >= t1 and not (p == q).all():
            close()  # touches the box in one point only
            continue
        if e0 is not None:
            close()
            current = [(_point_on_edge(p, q, t0, e0, bounds), cut_key(way.osm_id, cuts), None, -1)]
            cuts += 1
        elif current is None:
            current = [node(i)]
        if e1 is not None:
            cut = _point_on_edge(p, q, t1, e1, bounds)
            current.append((cut, cut_key(way.osm_id, cuts), None, -1))
            cuts += 1
            close()
        else:
            current.append(node(i + 1))
    close()
    return ClippedWay(pieces, int(inside.sum()), touches, edge_nodes)


@dataclass
class GraphNode:
    key: str
    position: tuple[float, float, float]  # local x east, y north, z up
    osm_node: int | None
    kind: NodeKind = "dead_end"


@dataclass
class GraphEdge:
    id: str
    from_key: str
    to_key: str
    piece: Piece
    start: int  # vertex range in the piece, inclusive, in way order
    end: int
    bridge_source: BridgeSource | None
    points: np.ndarray  # (n, 3) local x, y, z in the allowed direction

    @property
    def attrs(self) -> WayAttributes:
        return self.piece.attrs


@dataclass
class RoadGraph:
    nodes: dict[str, GraphNode]
    edges: list[GraphEdge]


def build_graph(pieces: Sequence[Piece]) -> RoadGraph:
    """Nodes and edges of the included pieces (their `z` and `decks` must be set)."""
    uses: Counter[int] = Counter()
    for piece in pieces:
        uses.update(n for n in piece.node_ids if n is not None)

    nodes: dict[str, GraphNode] = {}
    edges: list[GraphEdge] = []
    edge_index: defaultdict[int, int] = defaultdict(int)
    area_edge = {p.keys[i] for p in pieces for i in p.edge_nodes}
    for piece in sorted(pieces, key=lambda p: (p.way_id, p.index)):
        n = len(piece.xy)
        splits = {0, n - 1}
        splits |= {
            i for i, node in enumerate(piece.node_ids) if node is not None and uses[node] > 1
        }
        for start, end, _ in piece.decks:
            splits |= {start, end}
        cut_points = sorted(splits)
        for i in cut_points:
            key = piece.keys[i]
            if key not in nodes:
                x, y = piece.xy[i]
                nodes[key] = GraphNode(
                    key, (float(x), float(y), float(piece.z[i])), piece.node_ids[i]
                )
        for a, b in zip(cut_points[:-1], cut_points[1:], strict=True):
            source = piece.deck_source(a, b)
            points = np.column_stack([piece.xy[a : b + 1], piece.z[a : b + 1]])
            from_key, to_key = piece.keys[a], piece.keys[b]
            if piece.attrs.oneway < 0:
                points = points[::-1]
                from_key, to_key = to_key, from_key
            index = edge_index[piece.way_id]
            edge_index[piece.way_id] += 1
            edges.append(
                GraphEdge(
                    f"osm:way/{piece.way_id}#{index}",
                    from_key,
                    to_key,
                    piece,
                    a,
                    b,
                    source,
                    points,
                )
            )

    ends: Counter[str] = Counter()
    for edge in edges:
        ends[edge.from_key] += 1
        ends[edge.to_key] += 1
    for key, node in nodes.items():
        if key.startswith("span:"):
            node.kind = "span_end"
        elif key.startswith("cut:") or key in area_edge:
            node.kind = "area_edge"
        elif ends[key] >= 3:
            node.kind = "junction"
        elif ends[key] == 2:
            node.kind = "joint"
        else:
            node.kind = "dead_end"
    return RoadGraph(nodes, edges)


# Output.


def _r(value: float) -> float:
    return round(float(value), COORD_DECIMALS) + 0.0  # + 0.0: no "-0.0"


def godot_point(x: float, y: float, z: float) -> list[float]:
    """Local (east, north, up) to Godot [x, y, z] = [east, up, -north], rounded."""
    return [_r(x), _r(z), _r(-y)]


def polyline_length(points: np.ndarray) -> float:
    if len(points) < 2:
        return 0.0
    return float(np.linalg.norm(np.diff(points, axis=0), axis=1).sum())


def graph_document(graph: RoadGraph, map_id: str, area: str) -> dict[str, Any]:
    """The `road_graph.json` document (schema version 1), sorted for stable output."""
    nodes = [
        {
            "id": node.key,
            "kind": node.kind,
            "position": godot_point(*node.position),
            "osm_node": node.osm_node,
        }
        for node in sorted(graph.nodes.values(), key=lambda n: n.key)
    ]
    edges = []
    for edge in graph.edges:  # already in (way, index) order
        a = edge.attrs
        points = [godot_point(*p) for p in edge.points]
        edges.append(
            {
                "id": edge.id,
                "from": edge.from_key,
                "to": edge.to_key,
                "class": a.road_class,
                "drivable": a.drivable,
                "drivable_source": a.drivable_source,
                "walkable": a.walkable,
                "walkable_source": a.walkable_source,
                "lanes": a.lanes,
                "lanes_source": a.lanes_source,
                "speed_kmh": a.speed_kmh,
                "speed_source": a.speed_source,
                "width_m": a.width_m,
                "width_source": a.width_source,
                "oneway": a.oneway != 0,
                "bridge": edge.bridge_source is not None,
                "bridge_source": edge.bridge_source,
                "tunnel": a.tunnel,
                "layer": a.layer,
                "length_m": round(polyline_length(np.array(points)), COORD_DECIMALS),
                "osm_way": edge.piece.way_id,
                **a.names,
                "points": points,
            }
        )
    return {"schema_version": 1, "map_id": map_id, "area": area, "nodes": nodes, "edges": edges}


# Connectivity.


def weak_components(edges: Iterable[tuple[str, str, str]]) -> list[list[str]]:
    """Edge ids grouped by weakly connected component, largest first (ties: first id)."""
    parent: dict[str, str] = {}

    def find(x: str) -> str:
        root = x
        while parent.setdefault(root, root) != root:
            root = parent[root]
        while parent[x] != root:
            parent[x], x = root, parent[x]
        return root

    edge_list = list(edges)
    for _, a, b in edge_list:
        ra, rb = find(a), find(b)
        if ra != rb:
            parent[max(ra, rb)] = min(ra, rb)
    groups: defaultdict[str, list[str]] = defaultdict(list)
    for edge_id, a, _ in edge_list:
        groups[find(a)].append(edge_id)
    return sorted((sorted(g) for g in groups.values()), key=lambda g: (-len(g), g[0]))


def strong_components(nodes: Iterable[str], arcs: Iterable[tuple[str, str]]) -> list[list[str]]:
    """Node keys grouped by strongly connected component (iterative Tarjan), largest first."""
    succ: defaultdict[str, list[str]] = defaultdict(list)
    for a, b in arcs:
        succ[a].append(b)
    index: dict[str, int] = {}
    low: dict[str, int] = {}
    on_stack: set[str] = set()
    stack: list[str] = []
    result: list[list[str]] = []
    counter = 0
    for root in sorted(set(nodes)):
        if root in index:
            continue
        work: list[tuple[str, int]] = [(root, 0)]
        while work:
            v, i = work.pop()
            if i == 0:
                index[v] = low[v] = counter
                counter += 1
                stack.append(v)
                on_stack.add(v)
            children = sorted(succ[v])
            if i < len(children):
                work.append((v, i + 1))
                w = children[i]
                if w not in index:
                    work.append((w, 0))
                elif w in on_stack:
                    low[v] = min(low[v], index[w])
                continue
            if low[v] == index[v]:
                component = []
                while True:
                    w = stack.pop()
                    on_stack.discard(w)
                    component.append(w)
                    if w == v:
                        break
                result.append(sorted(component))
            if work:
                parent = work[-1][0]
                low[parent] = min(low[parent], low[v])
    return sorted(result, key=lambda c: (-len(c), c[0]))


def connectivity_report(graph: RoadGraph) -> dict[str, Any]:
    """Weak and strong components of the drivable edges, and per-class counts."""
    drivable = [e for e in graph.edges if e.attrs.drivable]
    weak = weak_components((e.id, e.from_key, e.to_key) for e in drivable)
    total = len(drivable)
    largest = len(weak[0]) if weak else 0
    arcs: list[tuple[str, str]] = []
    for e in drivable:
        arcs.append((e.from_key, e.to_key))
        if not e.attrs.oneway:
            arcs.append((e.to_key, e.from_key))
    drive_nodes = {k for e in drivable for k in (e.from_key, e.to_key)}
    strong = strong_components(drive_nodes, arcs)
    component_of = {node: i for i, comp in enumerate(strong) for node in comp}
    strong_edges: Counter[int] = Counter(
        component_of[e.from_key]
        for e in drivable
        if component_of[e.from_key] == component_of[e.to_key]
    )
    per_class: dict[str, dict[str, Any]] = {}
    for e in graph.edges:
        entry = per_class.setdefault(e.attrs.road_class, {"edges": 0, "length_m": 0.0})
        entry["edges"] += 1
        entry["length_m"] += polyline_length(e.points)
    return {
        "nodes": len(graph.nodes),
        "edges": len(graph.edges),
        "node_kinds": dict(sorted(Counter(n.kind for n in graph.nodes.values()).items())),
        "drivable_edges": total,
        "walkable_edges": sum(1 for e in graph.edges if e.attrs.walkable),
        "weak_components": {
            "count": len(weak),
            "edge_counts": [len(c) for c in weak],
            "largest_share": round(largest / total, 4) if total else None,
            "other_components": weak[1:],
        },
        "strong_components": {
            "count": len(strong),
            "node_counts": [len(c) for c in strong],
            "edge_counts": [strong_edges.get(i, 0) for i in range(len(strong))],
            "largest_edge_share": (
                round(max(strong_edges.values(), default=0) / total, 4) if total else None
            ),
        },
        "per_class": {
            k: {"edges": v["edges"], "length_m": round(v["length_m"], 1)}
            for k, v in sorted(per_class.items())
        },
    }
