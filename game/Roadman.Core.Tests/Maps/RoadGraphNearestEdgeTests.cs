using Roadman.Core.Maps;

namespace Roadman.Core.Tests.Maps;

public class RoadGraphNearestEdgeTests
{
    // A hand-made graph at ground height 100:
    // "b-ground" runs along x from (0, 100, 0) to (100, 100, 0), with a bend point at x = 50.
    // "a-parallel" runs along x at z = 10, so (any x, 100, 5) is exactly between the two.
    // "c-deck" is a bridge 6 m above "b-ground" over x = 40..60 (z = 0), walkable only.
    private static readonly RoadGraph Graph = new(
        1,
        "somero",
        "test",
        [
            Node("n1", 0, 100, 0),
            Node("n2", 100, 100, 0),
            Node("n3", 0, 100, 10),
            Node("n4", 100, 100, 10),
            Node("n5", 40, 106, 0),
            Node("n6", 60, 106, 0),
        ],
        [
            Edge("b-ground", "n1", "n2", drivable: true, (0, 100, 0), (50, 100, 0), (100, 100, 0)),
            Edge("a-parallel", "n3", "n4", drivable: true, (0, 100, 10), (100, 100, 10)),
            Edge("c-deck", "n5", "n6", drivable: false, (40, 106, 0), (60, 106, 0)),
        ]);

    [Fact]
    public void NearestEdge_ReturnsEdgeDistanceAndPoint()
    {
        var hit = Graph.NearestEdge(new LocalPoint(20, 100, -3), 40);

        Assert.NotNull(hit);
        Assert.Equal("b-ground", hit.Value.Edge.Id);
        Assert.Equal(3, hit.Value.Distance, 9);
        Assert.Equal(new LocalPoint(20, 100, 0), hit.Value.Point);
    }

    [Fact]
    public void NearestEdge_ClampsToTheEndOfThePolyline()
    {
        var hit = Graph.NearestEdge(new LocalPoint(-3, 100, -4), 40);

        Assert.Equal("b-ground", hit!.Value.Edge.Id);
        Assert.Equal(5, hit.Value.Distance, 9);
        Assert.Equal(new LocalPoint(0, 100, 0), hit.Value.Point);
    }

    [Fact]
    public void NearestEdge_FortyMetreLimit()
    {
        // 40 m south of b-ground counts; just over it doesn't.
        var atLimit = Graph.NearestEdge(new LocalPoint(20, 100, -40), 40);
        Assert.Equal("b-ground", atLimit!.Value.Edge.Id);
        Assert.Equal(40, atLimit.Value.Distance, 9);

        Assert.Null(Graph.NearestEdge(new LocalPoint(20, 100, -40.01), 40));
    }

    [Fact]
    public void NearestEdge_TieGoesToTheLowerEdgeId()
    {
        // Exactly 5 m from b-ground (z = 0) and a-parallel (z = 10).
        var hit = Graph.NearestEdge(new LocalPoint(20, 100, 5), 40);

        Assert.Equal("a-parallel", hit!.Value.Edge.Id);
        Assert.Equal(5, hit.Value.Distance, 9);
    }

    [Fact]
    public void NearestEdge_UsesHeight_LowerEdgeUnderHigherOne()
    {
        // Just above the ground road under the deck: the ground road wins.
        var under = Graph.NearestEdge(new LocalPoint(50, 101.5, 0), 40);
        Assert.Equal("b-ground", under!.Value.Edge.Id);
        Assert.Equal(1.5, under.Value.Distance, 9);
        Assert.Equal(new LocalPoint(50, 100, 0), under.Value.Point);

        // On the deck, the deck wins, although it is walkable only.
        var onDeck = Graph.NearestEdge(new LocalPoint(50, 106.5, 0), 40);
        Assert.Equal("c-deck", onDeck!.Value.Edge.Id);
        Assert.Equal(0.5, onDeck.Value.Distance, 9);
    }

    [Fact]
    public void NearestEdge_EmptyGraphAndBadLimit()
    {
        var empty = new RoadGraph(1, "somero", "full", [], []);

        Assert.Null(empty.NearestEdge(default, 40));
        Assert.Throws<ArgumentOutOfRangeException>(() => Graph.NearestEdge(default, -1));
        Assert.Throws<ArgumentOutOfRangeException>(() => Graph.NearestEdge(default, double.NaN));
    }

    private static RoadNode Node(string id, double x, double y, double z) =>
        new(id, RoadNodeKind.DeadEnd, new LocalPoint(x, y, z), null);

    private static RoadEdge Edge(string id, string from, string to, bool drivable, params (double X, double Y, double Z)[] points) =>
        new(
            id, from, to, "residential",
            drivable, RoadValueSource.Default, Walkable: true, RoadValueSource.Default,
            1, RoadValueSource.Default, drivable ? 30 : null, drivable ? RoadValueSource.Default : null,
            5, RoadValueSource.Default,
            Oneway: false, Bridge: !drivable, drivable ? null : RoadBridgeSource.Osm, Tunnel: false, Layer: 0,
            LengthM: 0, OsmWay: 1, FeatureNames.None,
            points.Select(p => new LocalPoint(p.X, p.Y, p.Z)).ToList());
}
