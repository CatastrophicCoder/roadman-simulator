using Roadman.Core.Maps;

namespace Roadman.Core.Tests.Maps;

public class StreetLabelTests
{
    [Fact]
    public void Format_NameAndRef()
    {
        Assert.Equal("Jokioistentie (52)", StreetLabel.Format(Edge("primary", "Jokioistentie", "52")));
    }

    [Fact]
    public void Format_NameOnly()
    {
        Assert.Equal("Härkälänkatu", StreetLabel.Format(Edge("residential", "Härkälänkatu", null)));
    }

    [Fact]
    public void Format_RefOnly()
    {
        Assert.Equal("2810", StreetLabel.Format(Edge("tertiary", null, "2810")));
    }

    [Fact]
    public void Format_NeverShowsTheSwedishName()
    {
        var edge = Edge("primary", null, null) with { Names = new FeatureNames(null, "Åbovägen", null) };

        Assert.Equal("Main road", StreetLabel.Format(edge));
    }

    [Theory]
    [InlineData("primary", "Main road")]
    [InlineData("secondary", "Main road")]
    [InlineData("tertiary", "Road")]
    [InlineData("unclassified", "Road")]
    [InlineData("residential", "Residential street")]
    [InlineData("living_street", "Living street")]
    [InlineData("service", "Service road")]
    [InlineData("driveway", "Driveway")]
    [InlineData("parking_aisle", "Parking aisle")]
    [InlineData("track", "Track")]
    [InlineData("cycleway", "Cycle path")]
    [InlineData("footway", "Footpath")]
    [InlineData("path", "Path")]
    [InlineData("pedestrian", "Pedestrian street")]
    public void Format_ListedClass(string roadClass, string expected)
    {
        Assert.Equal(expected, StreetLabel.Format(Edge(roadClass, null, null)));
    }

    [Theory]
    [InlineData("trunk", "Trunk")]
    [InlineData("motorway_link", "Motorway link")]
    [InlineData("bus_guide_way", "Bus guide way")]
    [InlineData("x", "X")]
    public void Format_UnlistedClass(string roadClass, string expected)
    {
        Assert.Equal(expected, StreetLabel.Format(Edge(roadClass, null, null)));
    }

    private static RoadEdge Edge(string roadClass, string? name, string? reference) =>
        new(
            "osm:way/1#0", "a", "b", roadClass,
            Drivable: true, RoadValueSource.Default, Walkable: true, RoadValueSource.Default,
            1, RoadValueSource.Default, 30, RoadValueSource.Default, 5, RoadValueSource.Default,
            Oneway: false, Bridge: false, null, Tunnel: false, Layer: 0, LengthM: 1, OsmWay: 1,
            new FeatureNames(name, null, reference),
            [new LocalPoint(0, 0, 0), new LocalPoint(1, 0, 0)]);
}
