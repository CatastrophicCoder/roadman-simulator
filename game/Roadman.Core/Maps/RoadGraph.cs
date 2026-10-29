namespace Roadman.Core.Maps;

/// <summary>What a road graph node is (<c>kind</c> in <c>road_graph.json</c>).</summary>
public enum RoadNodeKind
{
    /// <summary>Three or more edge ends meet (<c>junction</c>).</summary>
    Junction,

    /// <summary>Exactly two edge ends meet (<c>joint</c>).</summary>
    Joint,

    /// <summary>One edge end (<c>dead_end</c>).</summary>
    DeadEnd,

    /// <summary>Where a way was cut at the build area's edge (<c>area_edge</c>).</summary>
    AreaEdge,

    /// <summary>Where a bridge deck meets the road (<c>span_end</c>); not an OSM node.</summary>
    SpanEnd,
}

/// <summary>Where an edge attribute came from (<c>*_source</c> in <c>road_graph.json</c>).</summary>
public enum RoadValueSource
{
    /// <summary>An OSM tag (<c>osm</c>).</summary>
    Osm,

    /// <summary>The map's class defaults (<c>default</c>).</summary>
    Default,
}

/// <summary>Why an edge is a bridge (<c>bridge_source</c>).</summary>
public enum RoadBridgeSource
{
    /// <summary>Tagged <c>bridge</c> in OSM (<c>osm</c>).</summary>
    Osm,

    /// <summary>Crosses water without a bridge tag; treated as a bridge by the pipeline (<c>inferred</c>).</summary>
    Inferred,

    /// <summary>
    /// Crosses over a tunnel or a lower-<c>layer</c> way without a bridge tag; the pipeline spans the dip with a deck
    /// (<c>inferred_underpass</c>).
    /// </summary>
    InferredUnderpass,
}

/// <summary>Which nodes a lookup such as <see cref="RoadGraph.NearestNode"/> considers.</summary>
public enum RoadNodeFilter
{
    /// <summary>Every node.</summary>
    Any,

    /// <summary>Nodes with at least one drivable edge.</summary>
    Drivable,

    /// <summary>Nodes with at least one walkable edge.</summary>
    Walkable,
}

/// <summary>
/// The shared name fields of a map feature (<c>name</c>, <c>name_sv</c>, <c>ref</c>), from the OSM tags
/// <c>name</c>, <c>name:sv</c> and <c>ref</c>. Each is null when the tag is missing or empty. The pipeline trims and
/// NFC-normalises them and keeps <c>;</c>-separated multiple values as one string.
/// </summary>
public sealed record FeatureNames(string? Name, string? NameSv, string? Ref)
{
    public static FeatureNames None { get; } = new(null, null, null);
}

/// <summary>A node of the road graph.</summary>
/// <param name="Id">
/// Stable id: <c>osm:node/&lt;id&gt;</c>, <c>cut:way/&lt;id&gt;:&lt;n&gt;</c> for an area-edge cut, or
/// <c>span:way/&lt;id&gt;:&lt;n&gt;</c> for a span end.
/// </param>
/// <param name="Kind">Junction, joint, dead end, area edge or span end.</param>
/// <param name="Position">Godot local metres, at the road surface.</param>
/// <param name="OsmNode">The OSM node id; null for an area-edge cut or a span end.</param>
public sealed record RoadNode(string Id, RoadNodeKind Kind, LocalPoint Position, long? OsmNode);

/// <summary>
/// An edge of the road graph: one OSM way between two consecutive graph nodes. <see cref="From"/> to
/// <see cref="To"/> is the allowed driving direction when <see cref="Oneway"/> is true; walking is allowed both ways.
/// </summary>
/// <param name="Id">Stable id: <c>osm:way/&lt;id&gt;#&lt;n&gt;</c>.</param>
/// <param name="From">Start node id.</param>
/// <param name="To">End node id.</param>
/// <param name="Class">Road class from the map's config (for example <c>primary</c>, <c>cycleway</c>); any string.</param>
/// <param name="Drivable">Cars may use it.</param>
/// <param name="DrivableSource">Where <paramref name="Drivable"/> came from.</param>
/// <param name="Walkable">Pedestrians may use it.</param>
/// <param name="WalkableSource">Where <paramref name="Walkable"/> came from.</param>
/// <param name="Lanes">Number of lanes, at least 1.</param>
/// <param name="LanesSource">Where <paramref name="Lanes"/> came from.</param>
/// <param name="SpeedKmh">Speed limit; always set on drivable edges, null on the others.</param>
/// <param name="SpeedSource">Where <paramref name="SpeedKmh"/> came from; null exactly when it is null.</param>
/// <param name="WidthM">Surface width in metres, greater than 0.</param>
/// <param name="WidthSource">Where <paramref name="WidthM"/> came from.</param>
/// <param name="Oneway">Driving only from <paramref name="From"/> to <paramref name="To"/>.</param>
/// <param name="Bridge">On a bridge deck.</param>
/// <param name="BridgeSource">Why it is a bridge; null exactly when <paramref name="Bridge"/> is false.</param>
/// <param name="Tunnel">In a tunnel (no mesh, no collision).</param>
/// <param name="Layer">OSM layer, 0 by default.</param>
/// <param name="LengthM">3D length of <paramref name="Points"/> in metres.</param>
/// <param name="OsmWay">The OSM way id.</param>
/// <param name="Names">Name fields of the way.</param>
/// <param name="Points">Polyline in Godot local metres, at least 2 points, from <paramref name="From"/> to <paramref name="To"/>.</param>
public sealed record RoadEdge(
    string Id,
    string From,
    string To,
    string Class,
    bool Drivable,
    RoadValueSource DrivableSource,
    bool Walkable,
    RoadValueSource WalkableSource,
    int Lanes,
    RoadValueSource LanesSource,
    int? SpeedKmh,
    RoadValueSource? SpeedSource,
    double WidthM,
    RoadValueSource WidthSource,
    bool Oneway,
    bool Bridge,
    RoadBridgeSource? BridgeSource,
    bool Tunnel,
    int Layer,
    double LengthM,
    long OsmWay,
    FeatureNames Names,
    IReadOnlyList<LocalPoint> Points)
{
    /// <summary>True when cars may drive it from <paramref name="nodeId"/> towards its other end.</summary>
    public bool CanDriveFrom(string nodeId) => Drivable && (nodeId == From || (!Oneway && nodeId == To));

    /// <summary>The other end of the edge, seen from <paramref name="nodeId"/> (one of its ends).</summary>
    public string OtherEnd(string nodeId) => nodeId == From ? To : From;
}

/// <summary>
/// An edge as taken from one of its nodes: <see cref="Forward"/> is true when it is walked or driven from
/// <see cref="RoadEdge.From"/> to <see cref="RoadEdge.To"/> (in <see cref="RoadEdge.Points"/> order).
/// </summary>
public readonly record struct RoadEdgeStep(RoadEdge Edge, bool Forward)
{
    /// <summary>The node the step leaves.</summary>
    public string StartNode => Forward ? Edge.From : Edge.To;

    /// <summary>The node the step reaches.</summary>
    public string EndNode => Forward ? Edge.To : Edge.From;
}

/// <summary>The result of <see cref="RoadGraph.NearestEdge"/>.</summary>
/// <param name="Edge">The nearest edge.</param>
/// <param name="Distance">3D distance in metres from the query position to <paramref name="Point"/>.</param>
/// <param name="Point">The nearest point on the edge's polyline.</param>
public readonly record struct RoadEdgeHit(RoadEdge Edge, double Distance, LocalPoint Point);

/// <summary>
/// The pack's <c>road_graph.json</c>: every road and path of the map as nodes and edges. Read it with
/// <see cref="RoadGraphReader"/>, which guarantees that ids are unique and every edge's ends exist.
/// </summary>
public sealed class RoadGraph
{
    private readonly Dictionary<string, RoadNode> _nodes;
    private readonly Dictionary<string, RoadEdge> _edges;
    private readonly Dictionary<string, List<RoadEdge>> _edgesAt;

    /// <exception cref="ArgumentException">A duplicate id, or an edge whose end isn't in <paramref name="nodes"/>.</exception>
    public RoadGraph(int schemaVersion, string mapId, string area, IReadOnlyList<RoadNode> nodes, IReadOnlyList<RoadEdge> edges)
    {
        ArgumentNullException.ThrowIfNull(mapId);
        ArgumentNullException.ThrowIfNull(area);
        ArgumentNullException.ThrowIfNull(nodes);
        ArgumentNullException.ThrowIfNull(edges);
        SchemaVersion = schemaVersion;
        MapId = mapId;
        Area = area;
        Nodes = nodes;
        Edges = edges;

        _nodes = new Dictionary<string, RoadNode>(nodes.Count, StringComparer.Ordinal);
        _edgesAt = new Dictionary<string, List<RoadEdge>>(nodes.Count, StringComparer.Ordinal);
        foreach (var node in nodes)
        {
            if (!_nodes.TryAdd(node.Id, node))
            {
                throw new ArgumentException($"node id '{node.Id}' appears more than once", nameof(nodes));
            }

            _edgesAt.Add(node.Id, []);
        }

        _edges = new Dictionary<string, RoadEdge>(edges.Count, StringComparer.Ordinal);
        foreach (var edge in edges)
        {
            if (!_edges.TryAdd(edge.Id, edge))
            {
                throw new ArgumentException($"edge id '{edge.Id}' appears more than once", nameof(edges));
            }

            if (!_edgesAt.TryGetValue(edge.From, out var fromList) || !_edgesAt.TryGetValue(edge.To, out var toList))
            {
                throw new ArgumentException($"edge '{edge.Id}' references an unknown node", nameof(edges));
            }

            fromList.Add(edge);
            if (edge.To != edge.From)
            {
                toList.Add(edge);
            }
        }
    }

    public int SchemaVersion { get; }

    public string MapId { get; }

    /// <summary><c>full</c> or <c>test</c>.</summary>
    public string Area { get; }

    /// <summary>Every node, in file order.</summary>
    public IReadOnlyList<RoadNode> Nodes { get; }

    /// <summary>Every edge, in file order.</summary>
    public IReadOnlyList<RoadEdge> Edges { get; }

    /// <exception cref="KeyNotFoundException">No node has <paramref name="id"/>.</exception>
    public RoadNode Node(string id) =>
        _nodes.TryGetValue(id, out var node) ? node : throw new KeyNotFoundException($"no road graph node '{id}'");

    public bool TryGetNode(string id, out RoadNode node) => _nodes.TryGetValue(id, out node!);

    /// <exception cref="KeyNotFoundException">No edge has <paramref name="id"/>.</exception>
    public RoadEdge Edge(string id) =>
        _edges.TryGetValue(id, out var edge) ? edge : throw new KeyNotFoundException($"no road graph edge '{id}'");

    public bool TryGetEdge(string id, out RoadEdge edge) => _edges.TryGetValue(id, out edge!);

    /// <summary>Every edge with an end at <paramref name="nodeId"/>, in file order.</summary>
    /// <exception cref="KeyNotFoundException">No node has <paramref name="nodeId"/>.</exception>
    public IReadOnlyList<RoadEdge> EdgesAt(string nodeId) => EdgeList(nodeId);

    /// <summary>
    /// The drivable edges a car may take from <paramref name="nodeId"/>: one-way edges that start there, and two-way
    /// edges with either end there. A two-way edge that starts and ends at the node gives a step in each direction.
    /// </summary>
    /// <exception cref="KeyNotFoundException">No node has <paramref name="nodeId"/>.</exception>
    public IReadOnlyList<RoadEdgeStep> DrivableEdgesFrom(string nodeId)
    {
        var result = new List<RoadEdgeStep>();
        foreach (var edge in EdgeList(nodeId))
        {
            if (!edge.Drivable)
            {
                continue;
            }

            if (edge.From == nodeId)
            {
                result.Add(new RoadEdgeStep(edge, Forward: true));
            }

            if (edge.To == nodeId && !edge.Oneway)
            {
                result.Add(new RoadEdgeStep(edge, Forward: false));
            }
        }

        return result;
    }

    /// <summary>
    /// The walkable edges at <paramref name="nodeId"/>, each as a step away from the node. One-way doesn't apply to
    /// walking. An edge that starts and ends at the node gives a step in each direction.
    /// </summary>
    /// <exception cref="KeyNotFoundException">No node has <paramref name="nodeId"/>.</exception>
    public IReadOnlyList<RoadEdgeStep> WalkableEdgesAt(string nodeId)
    {
        var result = new List<RoadEdgeStep>();
        foreach (var edge in EdgeList(nodeId))
        {
            if (!edge.Walkable)
            {
                continue;
            }

            if (edge.From == nodeId)
            {
                result.Add(new RoadEdgeStep(edge, Forward: true));
            }

            if (edge.To == nodeId)
            {
                result.Add(new RoadEdgeStep(edge, Forward: false));
            }
        }

        return result;
    }

    /// <summary>True when <paramref name="nodeId"/> passes <paramref name="filter"/>.</summary>
    /// <exception cref="KeyNotFoundException">No node has <paramref name="nodeId"/>.</exception>
    public bool Matches(string nodeId, RoadNodeFilter filter) =>
        filter switch
        {
            RoadNodeFilter.Drivable => EdgeList(nodeId).Any(edge => edge.Drivable),
            RoadNodeFilter.Walkable => EdgeList(nodeId).Any(edge => edge.Walkable),
            _ => EdgeList(nodeId) is not null,
        };

    /// <summary>
    /// The node closest to <paramref name="position"/> (3D distance, so a bridge deck and the road under it are told
    /// apart) among the nodes that pass <paramref name="filter"/>. On a tie, the earlier node in file order wins.
    /// Null when no node passes the filter.
    /// </summary>
    public RoadNode? NearestNode(LocalPoint position, RoadNodeFilter filter = RoadNodeFilter.Any)
    {
        RoadNode? best = null;
        var bestDistance = double.PositiveInfinity;
        foreach (var node in Nodes)
        {
            if (filter != RoadNodeFilter.Any && !Matches(node.Id, filter))
            {
                continue;
            }

            var dx = node.Position.X - position.X;
            var dy = node.Position.Y - position.Y;
            var dz = node.Position.Z - position.Z;
            var distance = (dx * dx) + (dy * dy) + (dz * dz);
            if (distance < bestDistance)
            {
                best = node;
                bestDistance = distance;
            }
        }

        return best;
    }

    /// <summary>
    /// The edge (drivable or walkable, any class) whose polyline is nearest to <paramref name="position"/> in 3D, so
    /// under a bridge the way below wins over the deck. Only edges at most <paramref name="maxDistance"/> metres away
    /// count; on a tie the lower edge id (ordinal) wins, so a point exactly between two edges always gives the same
    /// one. Null when no edge is that close.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="maxDistance"/> is negative or NaN.</exception>
    public RoadEdgeHit? NearestEdge(LocalPoint position, double maxDistance)
    {
        if (double.IsNaN(maxDistance) || maxDistance < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maxDistance), maxDistance, "must be 0 or more");
        }

        RoadEdge? best = null;
        var bestPoint = default(LocalPoint);
        var bestDistance = maxDistance * maxDistance;
        foreach (var edge in Edges)
        {
            var (point, distance) = NearestOnPolyline(edge.Points, position);
            if (distance < bestDistance
                || (distance == bestDistance && (best is null || string.CompareOrdinal(edge.Id, best.Id) < 0)))
            {
                best = edge;
                bestPoint = point;
                bestDistance = distance;
            }
        }

        return best is null ? null : new RoadEdgeHit(best, Math.Sqrt(bestDistance), bestPoint);
    }

    /// <summary>The point of <paramref name="points"/> nearest to <paramref name="position"/>, and its squared distance.</summary>
    private static (LocalPoint Point, double SquaredDistance) NearestOnPolyline(IReadOnlyList<LocalPoint> points, LocalPoint position)
    {
        var bestPoint = points[0];
        var bestDistance = double.PositiveInfinity;
        for (var i = 0; i + 1 < points.Count; i++)
        {
            var a = points[i];
            var b = points[i + 1];
            var abx = b.X - a.X;
            var aby = b.Y - a.Y;
            var abz = b.Z - a.Z;
            var lengthSquared = (abx * abx) + (aby * aby) + (abz * abz);
            var t = lengthSquared > 0
                ? Math.Clamp((((position.X - a.X) * abx) + ((position.Y - a.Y) * aby) + ((position.Z - a.Z) * abz)) / lengthSquared, 0, 1)
                : 0;
            var point = new LocalPoint(a.X + (t * abx), a.Y + (t * aby), a.Z + (t * abz));
            var dx = point.X - position.X;
            var dy = point.Y - position.Y;
            var dz = point.Z - position.Z;
            var distance = (dx * dx) + (dy * dy) + (dz * dz);
            if (distance < bestDistance)
            {
                bestPoint = point;
                bestDistance = distance;
            }
        }

        return (bestPoint, bestDistance);
    }

    private List<RoadEdge> EdgeList(string nodeId) =>
        _edgesAt.TryGetValue(nodeId, out var list) ? list : throw new KeyNotFoundException($"no road graph node '{nodeId}'");
}
