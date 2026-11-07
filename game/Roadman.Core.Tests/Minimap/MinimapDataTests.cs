using Roadman.Core.Maps;
using Roadman.Core.Minimap;
using static Roadman.Core.Tests.Minimap.MinimapFixtures;

namespace Roadman.Core.Tests.Minimap;

/// <summary>
/// <see cref="MinimapData"/>: <c>docs/specs/minimap.md</c>, "Data and map loads" and acceptance criterion 15 (tunnels
/// dropped, bridges drawn after every other road).
/// </summary>
public class MinimapDataTests
{
    [Fact]
    public void Ac15_TunnelEdges_AreNotDrawn()
    {
        var graph = Graph(
            MinimapFixtures.Edge("osm:way/1#0", "primary", [(0, 0), (100, 0)], name: "Härkätie"),
            MinimapFixtures.Edge("osm:way/2#0", "primary", [(100, 0), (200, 0)], name: "Härkätie", tunnel: true),
            MinimapFixtures.Edge("osm:way/3#0", "footway", [(0, 10), (50, 10)], drivable: false, tunnel: true));

        var data = MinimapData.Build(graph, (PlaceIndex?)null, null);

        Assert.Equal(["osm:way/1#0"], data.Roads.Select(road => road.EdgeId));
        Assert.Empty(data.QueryRoads(150, 0, 10));
    }

    [Fact]
    public void Ac15_BridgeEdges_AreDrawnAfterEveryNonBridgeEdge()
    {
        var graph = Graph(
            MinimapFixtures.Edge("osm:way/1#0", "footway", [(0, 0), (10, 0)], drivable: false, bridge: true),
            MinimapFixtures.Edge("osm:way/2#0", "primary", [(0, 5), (10, 5)]),
            MinimapFixtures.Edge("osm:way/3#0", "residential", [(0, 10), (10, 10)], bridge: true),
            MinimapFixtures.Edge("osm:way/4#0", "cycleway", [(0, 15), (10, 15)], drivable: false),
            MinimapFixtures.Edge("osm:way/5#0", "service", [(0, 20), (10, 20)]),
            MinimapFixtures.Edge("osm:way/6#0", "primary", [(0, 25), (10, 25)], bridge: true));

        var data = MinimapData.Build(graph, (PlaceIndex?)null, null);
        var order = data.Roads.Select(road => road.EdgeId).ToList();

        // Non-bridges first (paths, minor, roads, main roads), then the bridges in the same group order.
        Assert.Equal(
            ["osm:way/4#0", "osm:way/5#0", "osm:way/2#0", "osm:way/1#0", "osm:way/3#0", "osm:way/6#0"],
            order);
        var lastNonBridge = data.Roads.Where(road => !road.Bridge).Max(road => road.DrawIndex);
        Assert.All(data.Roads.Where(road => road.Bridge), road => Assert.True(road.DrawIndex > lastNonBridge));
        Assert.Equal(Enumerable.Range(0, data.Roads.Count), data.Roads.Select(road => road.DrawIndex));

        // A query keeps the draw order.
        Assert.Equal(order, data.QueryRoads(5, 12, 20).Select(road => road.EdgeId));
    }

    [Fact]
    public void Roads_KeepTheirGroupLabelAndPolyline()
    {
        var graph = Graph(
            MinimapFixtures.Edge("osm:way/1#0", "primary", [(0, 0), (50, -20), (100, 0)], name: "Härkätie", nameSv: "Oxvägen", reference: "52"),
            MinimapFixtures.Edge("osm:way/2#0", "service", [(0, 0), (0, 10)], nameSv: "Vägen"));

        var data = MinimapData.Build(graph, (PlaceIndex?)null, null);

        var main = data.Roads.Single(road => road.EdgeId == "osm:way/1#0");
        Assert.Equal(RoadGroup.MainRoad, main.Group);
        Assert.Equal("Härkätie (52)", main.LabelText);
        Assert.Equal([new PlanarPoint(0, 0), new PlanarPoint(50, -20), new PlanarPoint(100, 0)], main.Points);
        Assert.Equal((0.0, -20.0, 100.0, 0.0), main.Bounds);
        Assert.Null(data.Roads.Single(road => road.EdgeId == "osm:way/2#0").LabelText);
        Assert.Equal(["Härkätie (52)"], data.LabelTexts);
    }

    [Fact]
    public void QueryRoads_ReturnsOnlyRoadsNearTheCentre()
    {
        var graph = Graph(
            MinimapFixtures.Edge("osm:way/1#0", "residential", [(0, 0), (10, 0)]),
            MinimapFixtures.Edge("osm:way/2#0", "residential", [(500, 500), (510, 500)]),
            MinimapFixtures.Edge("osm:way/3#0", "residential", [(-1000, 40), (1000, 40)])); // long: in many cells

        var data = MinimapData.Build(graph, (PlaceIndex?)null, null, cellSize: 32);

        Assert.Equal(["osm:way/1#0", "osm:way/3#0"], data.QueryRoads(0, 0, 50).Select(road => road.EdgeId));
        Assert.Equal(["osm:way/2#0"], data.QueryRoads(505, 505, 20).Select(road => road.EdgeId));
        Assert.Equal(["osm:way/3#0"], data.QueryRoads(-900, 40, 5).Select(road => road.EdgeId));
        Assert.Empty(data.QueryRoads(5000, 5000, 100));

        // Repeated queries give the same answer (the per-query stamps reset correctly).
        var into = new List<MinimapRoad>();
        for (var i = 0; i < 3; i++)
        {
            data.QueryRoads(0, 0, 50, into);
            Assert.Equal(2, into.Count);
        }

        // A huge radius returns every road once.
        Assert.Equal(3, data.QueryRoads(0, 0, 1e12).Count);
    }

    [Fact]
    public void QueryRoads_MatchesABruteForceScan()
    {
        var graph = MinimapFixtures.CrossingStreets();
        var data = MinimapData.Build(graph, (PlaceIndex?)null, null, cellSize: 50);
        var random = new Random(15);

        for (var i = 0; i < 300; i++)
        {
            var x = -400 + (random.NextDouble() * 800);
            var z = -400 + (random.NextDouble() * 800);
            var r = random.NextDouble() * 200;
            var expected = data.Roads
                .Where(road => road.Bounds.MaxX >= x - r && road.Bounds.MinX <= x + r && road.Bounds.MaxZ >= z - r && road.Bounds.MinZ <= z + r)
                .Select(road => road.EdgeId);

            Assert.Equal(expected, data.QueryRoads(x, z, r).Select(road => road.EdgeId));
        }
    }

    [Fact]
    public void Pois_BecomeSymbolsWithoutNames()
    {
        Poi[] pois =
        [
            Poi("osm:node/1", PoiType.Amenity, "parking", 10, 20, name: "Torin pysäköinti"),
            Poi("osm:way/2", PoiType.Shop, "supermarket", -5, 7, name: "Kauppa"),
            Poi("osm:node/3", PoiType.Amenity, "fuel", 1, 2),
        ];

        var data = MinimapData.Build(null, pois, null);

        Assert.Equal(
            [
                new MinimapPoi("osm:node/1", 10, 20, PoiGroup.Parking),
                new MinimapPoi("osm:way/2", -5, 7, PoiGroup.Shop),
                new MinimapPoi("osm:node/3", 1, 2, PoiGroup.Other),
            ],
            data.Pois);
    }

    [Fact]
    public void Boundary_GivesAClosedRingAndRoadblockBars()
    {
        var boundary = BoundaryReader.Parse(
            Roadman.Core.Tests.Maps.BoundaryReaderTests.ValidBoundary,
            Roadman.Core.Tests.Maps.BoundaryReaderTests.Manifest("somero", "test"));

        var data = MinimapData.Build(null, (PlaceIndex?)null, boundary);

        Assert.True(data.HasBoundary);
        Assert.Equal(
            [new PlanarPoint(0, 0), new PlanarPoint(100, 0), new PlanarPoint(100, 100), new PlanarPoint(0, 100), new PlanarPoint(0, 0)],
            data.Ring!);
        var bar = Assert.Single(data.Roadblocks);
        Assert.Equal("roadblock:cut:way/10:0", bar.Id);
        Assert.Equal(new PlanarPoint(40, 0), bar.Start);
        Assert.Equal(new PlanarPoint(47.5, 0), bar.End);
    }

    [Fact]
    public void NullInputs_GiveEmptyLayers()
    {
        var data = MinimapData.Build(null, (PlaceIndex?)null, null);

        Assert.Empty(data.Roads);
        Assert.Empty(data.LabelTexts);
        Assert.Empty(data.Pois);
        Assert.Null(data.Ring);
        Assert.False(data.HasBoundary);
        Assert.Empty(data.Roadblocks);
        Assert.Empty(data.QueryRoads(0, 0, 150));
        Assert.Empty(MinimapData.Empty.Roads);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void Build_RejectsABadCellSize(double cellSize)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => MinimapData.Build(null, (PlaceIndex?)null, null, cellSize));
    }
}
