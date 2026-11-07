using Roadman.Core.Maps;

namespace Roadman.Core.Minimap;

/// <summary>One road edge as the minimap draws it: a ground polyline (x/z) with its group.</summary>
/// <param name="EdgeId">The road graph edge id.</param>
/// <param name="Group">Its class group (<see cref="MinimapStyle.RoadGroupOf(RoadEdge)"/>).</param>
/// <param name="Bridge">On a bridge deck: drawn after every non-bridge road.</param>
/// <param name="LabelText">
/// The street label text (<see cref="StreetLabel.Format"/>) when the edge has a <c>name</c> or a <c>ref</c>; null
/// otherwise (class texts are never shown on the minimap, and <c>name_sv</c> never is).
/// </param>
/// <param name="Points">The polyline in Godot local x/z, at least 2 points.</param>
/// <param name="DrawIndex">Its position in <see cref="MinimapData.Roads"/>, the draw order.</param>
public sealed record MinimapRoad(
    string EdgeId,
    RoadGroup Group,
    bool Bridge,
    string? LabelText,
    IReadOnlyList<PlanarPoint> Points,
    int DrawIndex)
{
    /// <summary>The polyline in Godot local x/z; setting it (a <c>with</c> copy) recomputes <see cref="Bounds"/>.</summary>
    public IReadOnlyList<PlanarPoint> Points
    {
        get;
        init
        {
            field = value;
            Bounds = BoundsOf(value);
        }
    } = Points;

    /// <summary>The polyline's bounding box in x/z, kept in step with <see cref="Points"/>.</summary>
    public (double MinX, double MinZ, double MaxX, double MaxZ) Bounds { get; private set; } = BoundsOf(Points);

    private static (double, double, double, double) BoundsOf(IReadOnlyList<PlanarPoint> points)
    {
        var minX = double.PositiveInfinity;
        var minZ = double.PositiveInfinity;
        var maxX = double.NegativeInfinity;
        var maxZ = double.NegativeInfinity;
        foreach (var point in points)
        {
            minX = Math.Min(minX, point.X);
            minZ = Math.Min(minZ, point.Z);
            maxX = Math.Max(maxX, point.X);
            maxZ = Math.Max(maxZ, point.Z);
        }

        return (minX, minZ, maxX, maxZ);
    }
}

/// <summary>A point-of-interest symbol: its id, position (<c>Poi.Position</c>'s x/z) and group. No names.</summary>
public readonly record struct MinimapPoi(string Id, double X, double Z, PoiGroup Group);

/// <summary>A roadblock bar across the road, from the roadblock's span <c>start</c> to <c>end</c> (x/z).</summary>
public readonly record struct MinimapRoadblockBar(string Id, PlanarPoint Start, PlanarPoint End);

/// <summary>
/// The minimap's prepared map data, built once per map load (<c>docs/specs/minimap.md</c>, "Data and map loads"):
/// road polylines with their groups in draw order, bucketed per square spatial cell so a frame projects only the
/// roads near the player; street label texts; point-of-interest symbols; the boundary ring and roadblock bars. Each
/// input may be null, which leaves its layers empty.
/// </summary>
public sealed class MinimapData
{
    /// <summary>The default spatial cell edge in metres.</summary>
    public const double DefaultCellSize = 64;

    private readonly Dictionary<(int X, int Z), int[]> _cells;
    private readonly int[] _stamps;
    private readonly (int MinX, int MinZ, int MaxX, int MaxZ) _cellRange;
    private int _stamp;

    private MinimapData(
        IReadOnlyList<MinimapRoad> roads,
        IReadOnlyList<MinimapPoi> pois,
        IReadOnlyList<PlanarPoint>? ring,
        IReadOnlyList<MinimapRoadblockBar> roadblocks,
        double cellSize)
    {
        Roads = roads;
        Pois = pois;
        Ring = ring;
        Roadblocks = roadblocks;
        CellSize = cellSize;
        _stamps = new int[roads.Count];

        var cells = new Dictionary<(int X, int Z), List<int>>();
        foreach (var road in roads)
        {
            var (minX, minZ, maxX, maxZ) = road.Bounds;
            for (var cx = Cell(minX); cx <= Cell(maxX); cx++)
            {
                for (var cz = Cell(minZ); cz <= Cell(maxZ); cz++)
                {
                    if (!cells.TryGetValue((cx, cz), out var list))
                    {
                        list = [];
                        cells.Add((cx, cz), list);
                    }

                    list.Add(road.DrawIndex);
                }
            }
        }

        _cells = cells.ToDictionary(pair => pair.Key, pair => pair.Value.ToArray());
        _cellRange = cells.Count == 0
            ? (0, 0, -1, -1)
            : (cells.Keys.Min(key => key.X), cells.Keys.Min(key => key.Z), cells.Keys.Max(key => key.X), cells.Keys.Max(key => key.Z));
        LabelTexts = roads
            .Select(road => road.LabelText)
            .OfType<string>()
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>No roads, symbols, ring or roadblocks.</summary>
    public static MinimapData Empty { get; } = new([], [], null, [], DefaultCellSize);

    /// <summary>
    /// Every non-tunnel road in draw order: first the non-bridge roads, then the bridges; within each, by
    /// <see cref="RoadStroke.DrawOrder"/> of the group (paths first), then in road graph file order.
    /// <see cref="MinimapRoad.DrawIndex"/> is the position in this list.
    /// </summary>
    public IReadOnlyList<MinimapRoad> Roads { get; }

    /// <summary>Every distinct street label text, ordinal order.</summary>
    public IReadOnlyList<string> LabelTexts { get; }

    /// <summary>Every point of interest, in <c>pois.json</c> order.</summary>
    public IReadOnlyList<MinimapPoi> Pois { get; }

    /// <summary>
    /// The boundary ring as a closed polyline (the first point repeated at the end), or null without a boundary.
    /// </summary>
    public IReadOnlyList<PlanarPoint>? Ring { get; }

    /// <summary>True when the pack had a boundary: the minimap draws the void outside it.</summary>
    public bool HasBoundary => Ring is not null;

    /// <summary>Every roadblock bar, in <c>boundary.json</c> order.</summary>
    public IReadOnlyList<MinimapRoadblockBar> Roadblocks { get; }

    /// <summary>The spatial cell edge in metres.</summary>
    public double CellSize { get; }

    /// <summary>Builds the data from a map load's road graph, places and boundary.</summary>
    public static MinimapData Build(RoadGraph? graph, PlaceIndex? places, Boundary? boundary, double cellSize = DefaultCellSize) =>
        Build(graph, places?.Pois.Items, boundary, cellSize);

    /// <summary>Builds the data from a road graph, a list of points of interest and a boundary; each may be null.</summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="cellSize"/> isn't a finite number above 0.</exception>
    public static MinimapData Build(
        RoadGraph? graph,
        IReadOnlyList<Poi>? pois,
        Boundary? boundary,
        double cellSize = DefaultCellSize)
    {
        if (!double.IsFinite(cellSize) || cellSize <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(cellSize), cellSize, "must be a finite number above 0");
        }

        var roads = new List<MinimapRoad>();
        if (graph is not null)
        {
            var ordered = graph.Edges
                .Select((edge, index) => (Edge: edge, Index: index))
                .Where(item => !item.Edge.Tunnel && item.Edge.Points.Count >= 2)
                .Select(item => (item.Edge, item.Index, Group: MinimapStyle.RoadGroupOf(item.Edge)))
                .OrderBy(item => item.Edge.Bridge)
                .ThenBy(item => MinimapStyle.StrokeOf(item.Group).DrawOrder)
                .ThenBy(item => item.Index);
            foreach (var (edge, _, group) in ordered)
            {
                roads.Add(new MinimapRoad(
                    edge.Id,
                    group,
                    edge.Bridge,
                    LabelTextOf(edge),
                    edge.Points.Select(point => new PlanarPoint(point.X, point.Z)).ToList(),
                    roads.Count));
            }
        }

        var symbols = pois?
            .Select(poi => new MinimapPoi(poi.Id, poi.Position.X, poi.Position.Z, MinimapStyle.PoiGroupOf(poi)))
            .ToList() ?? [];

        IReadOnlyList<PlanarPoint>? ring = null;
        var bars = new List<MinimapRoadblockBar>();
        if (boundary is not null)
        {
            var points = boundary.Ring.Points;
            ring = [.. points, points[0]];
            bars.AddRange(boundary.Roadblocks.Select(roadblock => new MinimapRoadblockBar(
                roadblock.Id,
                new PlanarPoint(roadblock.Start.X, roadblock.Start.Z),
                new PlanarPoint(roadblock.End.X, roadblock.End.Z))));
        }

        return new MinimapData(roads, symbols, ring, bars, cellSize);
    }

    /// <summary>
    /// The minimap label text of an edge: <see cref="StreetLabel.Format"/> when it has a <c>name</c> or a <c>ref</c>,
    /// else null (never a class text, never <c>name_sv</c>).
    /// </summary>
    public static string? LabelTextOf(RoadEdge edge)
    {
        ArgumentNullException.ThrowIfNull(edge);
        return edge.Names.Name is null && edge.Names.Ref is null ? null : StreetLabel.Format(edge);
    }

    /// <summary>
    /// The roads whose bounding box meets the square of half-side <paramref name="radius"/> around
    /// (<paramref name="x"/>, <paramref name="z"/>), in draw order, appended to <paramref name="into"/> (which is
    /// cleared first). Only the spatial cells under that square are looked at. Not thread-safe.
    /// </summary>
    public void QueryRoads(double x, double z, double radius, List<MinimapRoad> into)
    {
        ArgumentNullException.ThrowIfNull(into);
        into.Clear();
        if (Roads.Count == 0 || !(radius >= 0) || !double.IsFinite(x) || !double.IsFinite(z))
        {
            return;
        }

        var minX = x - radius;
        var maxX = x + radius;
        var minZ = z - radius;
        var maxZ = z + radius;
        if (++_stamp == int.MaxValue)
        {
            Array.Clear(_stamps);
            _stamp = 1;
        }

        var lastX = Math.Min(Cell(maxX), _cellRange.MaxX);
        var lastZ = Math.Min(Cell(maxZ), _cellRange.MaxZ);
        for (var cx = Math.Max(Cell(minX), _cellRange.MinX); cx <= lastX; cx++)
        {
            for (var cz = Math.Max(Cell(minZ), _cellRange.MinZ); cz <= lastZ; cz++)
            {
                if (!_cells.TryGetValue((cx, cz), out var indices))
                {
                    continue;
                }

                foreach (var index in indices)
                {
                    if (_stamps[index] == _stamp)
                    {
                        continue;
                    }

                    _stamps[index] = _stamp;
                    var bounds = Roads[index].Bounds;
                    if (bounds.MaxX >= minX && bounds.MinX <= maxX && bounds.MaxZ >= minZ && bounds.MinZ <= maxZ)
                    {
                        into.Add(Roads[index]);
                    }
                }
            }
        }

        into.Sort(static (a, b) => a.DrawIndex.CompareTo(b.DrawIndex));
    }

    /// <summary>Like <see cref="QueryRoads(double, double, double, List{MinimapRoad})"/>, into a new list.</summary>
    public List<MinimapRoad> QueryRoads(double x, double z, double radius)
    {
        var result = new List<MinimapRoad>();
        QueryRoads(x, z, radius, result);
        return result;
    }

    private int Cell(double value) => (int)Math.Floor(Math.Clamp(value / CellSize, int.MinValue / 2.0, int.MaxValue / 2.0));
}
