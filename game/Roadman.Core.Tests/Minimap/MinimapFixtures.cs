using Roadman.Core.Maps;

namespace Roadman.Core.Tests.Minimap;

/// <summary>Hand-built road graph edges, points of interest and graphs for the minimap tests.</summary>
internal static class MinimapFixtures
{
    /// <summary>An edge with the given polyline (x, z pairs), class and names; its ends are nodes "n:&lt;id&gt;:a/b".</summary>
    public static RoadEdge Edge(
        string id,
        string roadClass,
        (double X, double Z)[] points,
        string? name = null,
        string? nameSv = null,
        string? reference = null,
        bool drivable = true,
        bool bridge = false,
        bool tunnel = false) =>
        new(
            id, $"n:{id}:a", $"n:{id}:b", roadClass,
            drivable, RoadValueSource.Default, Walkable: true, RoadValueSource.Default,
            1, RoadValueSource.Default, drivable ? 30 : null, drivable ? RoadValueSource.Default : null,
            5, RoadValueSource.Default,
            Oneway: false, bridge, bridge ? RoadBridgeSource.Osm : null, tunnel, Layer: bridge ? 1 : tunnel ? -1 : 0,
            LengthM: 1, OsmWay: 1,
            new FeatureNames(name, nameSv, reference),
            points.Select(p => new LocalPoint(p.X, 100, p.Z)).ToList());

    /// <summary>A graph of <paramref name="edges"/>, each with its own two end nodes.</summary>
    public static RoadGraph Graph(params RoadEdge[] edges)
    {
        var nodes = edges
            .SelectMany(edge => new[]
            {
                new RoadNode(edge.From, RoadNodeKind.DeadEnd, edge.Points[0], null),
                new RoadNode(edge.To, RoadNodeKind.DeadEnd, edge.Points[^1], null),
            })
            .ToList();
        return new RoadGraph(1, "somero", "test", nodes, edges);
    }

    /// <summary>A point POI at (x, z).</summary>
    public static Poi Poi(string id, PoiType type, string subtype, double x, double z, string? name = null) =>
        new(
            id, new FeatureNames(name, null, null), type, subtype, [], PoiGeometry.Point,
            new LocalPoint(x, 100, z), null, new Address(null, null), null, null, []);

    /// <summary>
    /// A grid of crossing named streets around the origin: east-west streets every 60 m and north-south streets
    /// every 70 m from −300 to 300 m, each split into edges at every crossing (as the road graph splits ways at
    /// junctions), plus a bent street, an unnamed street, a street with only a ref and one with a Swedish name.
    /// </summary>
    public static RoadGraph CrossingStreets()
    {
        var edges = new List<RoadEdge>();
        var xs = Enumerable.Range(-4, 9).Select(i => i * 70.0).ToArray();
        var zs = Enumerable.Range(-5, 11).Select(i => i * 60.0).ToArray();
        for (var row = 0; row < zs.Length; row++)
        {
            var name = row == 5 ? "Härkätie" : $"Itäkatu {row}";
            var roadClass = row == 5 ? "primary" : row % 2 == 0 ? "residential" : "tertiary";
            var reference = row == 5 ? "52" : null;
            for (var k = 0; k + 1 < xs.Length; k++)
            {
                edges.Add(Edge(
                    $"osm:way/{1000 + row}#{k}", roadClass, [(xs[k], zs[row]), (xs[k + 1], zs[row])],
                    name, nameSv: row == 5 ? "Oxvägen" : null, reference: reference));
            }
        }

        for (var col = 0; col < xs.Length; col++)
        {
            var name = col == 4 ? "Torikatu" : $"Pohjoiskatu {col}";
            for (var k = 0; k + 1 < zs.Length; k++)
            {
                edges.Add(Edge(
                    $"osm:way/{2000 + col}#{k}", col == 4 ? "tertiary" : "residential",
                    [(xs[col], zs[k]), (xs[col], zs[k + 1])],
                    name, nameSv: col == 4 ? "Torggatan" : null));
            }
        }

        // A bent street (a curve sampled every 10 degrees), an unnamed one, a ref-only one, and a footway.
        var bend = Enumerable.Range(0, 10)
            .Select(i => (X: 35 + (40 * Math.Cos(i * Math.PI / 18)), Z: 30 + (40 * Math.Sin(i * Math.PI / 18))))
            .ToArray();
        edges.Add(Edge("osm:way/3000#0", "residential", bend, "Kaarikatu"));
        edges.Add(Edge("osm:way/3001#0", "service", [(-35, -30), (35, -30)]));
        edges.Add(Edge("osm:way/3002#0", "unclassified", [(-35, 30), (-35, 90)], reference: "2803"));
        edges.Add(Edge("osm:way/3003#0", "footway", [(-100, 10), (100, 10)], "Rantapolku", drivable: false));
        return Graph([.. edges]);
    }
}
