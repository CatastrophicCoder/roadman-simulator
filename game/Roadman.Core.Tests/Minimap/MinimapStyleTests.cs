using Roadman.Core.Maps;
using Roadman.Core.Minimap;

namespace Roadman.Core.Tests.Minimap;

/// <summary><see cref="MinimapStyle"/>: <c>docs/specs/minimap.md</c>, acceptance criterion 7.</summary>
public class MinimapStyleTests
{
    /// <summary>Every road class in the full Somero pack (generated 2026-11-04), copied from the spec's "Context".</summary>
    public static readonly TheoryData<string, RoadGroup> FullPackRoadClasses = new()
    {
        { "residential", RoadGroup.Road },
        { "cycleway", RoadGroup.Path },
        { "service", RoadGroup.MinorDrivable },
        { "tertiary", RoadGroup.Road },
        { "driveway", RoadGroup.MinorDrivable },
        { "primary", RoadGroup.MainRoad },
        { "footway", RoadGroup.Path },
        { "parking_aisle", RoadGroup.MinorDrivable },
        { "unclassified", RoadGroup.Road },
        { "living_street", RoadGroup.Road },
        { "track", RoadGroup.MinorDrivable },
        { "pedestrian", RoadGroup.Path },
        { "path", RoadGroup.Path },
    };

    /// <summary>Every point-of-interest type and amenity subtype in the full Somero pack, copied from the spec's "Context".</summary>
    public static readonly TheoryData<PoiType, string, PoiGroup> FullPackPois = new()
    {
        { PoiType.Amenity, "parking", PoiGroup.Parking },
        { PoiType.Amenity, "fuel", PoiGroup.Other },
        { PoiType.Amenity, "school", PoiGroup.PublicServices },
        { PoiType.Amenity, "grave_yard", PoiGroup.PublicServices },
        { PoiType.Amenity, "cinema", PoiGroup.Other },
        { PoiType.Amenity, "cafe", PoiGroup.FoodAndDrink },
        { PoiType.Amenity, "pharmacy", PoiGroup.PublicServices },
        { PoiType.Amenity, "marketplace", PoiGroup.Shop },
        { PoiType.Amenity, "place_of_worship", PoiGroup.PublicServices },
        { PoiType.Amenity, "restaurant", PoiGroup.FoodAndDrink },
        { PoiType.Amenity, "library", PoiGroup.PublicServices },
        { PoiType.Amenity, "fire_station", PoiGroup.PublicServices },
        { PoiType.Shop, "supermarket", PoiGroup.Shop },
        { PoiType.Shop, "kiosk", PoiGroup.Shop },
        { PoiType.Leisure, "park", PoiGroup.Other },
        { PoiType.Leisure, "pitch", PoiGroup.Other },
        { PoiType.Tourism, "information", PoiGroup.Other },
    };

    /// <summary>The spec's table, row by row, beyond the full pack.</summary>
    public static readonly TheoryData<PoiType, string, PoiGroup> SpecTable = new()
    {
        { PoiType.Amenity, "fast_food", PoiGroup.FoodAndDrink },
        { PoiType.Amenity, "bar", PoiGroup.FoodAndDrink },
        { PoiType.Amenity, "pub", PoiGroup.FoodAndDrink },
        { PoiType.Amenity, "ice_cream", PoiGroup.FoodAndDrink },
        { PoiType.Amenity, "food_court", PoiGroup.FoodAndDrink },
        { PoiType.Amenity, "biergarten", PoiGroup.FoodAndDrink },
        { PoiType.Amenity, "kindergarten", PoiGroup.PublicServices },
        { PoiType.Amenity, "college", PoiGroup.PublicServices },
        { PoiType.Amenity, "university", PoiGroup.PublicServices },
        { PoiType.Amenity, "hospital", PoiGroup.PublicServices },
        { PoiType.Amenity, "clinic", PoiGroup.PublicServices },
        { PoiType.Amenity, "doctors", PoiGroup.PublicServices },
        { PoiType.Amenity, "dentist", PoiGroup.PublicServices },
        { PoiType.Amenity, "police", PoiGroup.PublicServices },
        { PoiType.Amenity, "townhall", PoiGroup.PublicServices },
        { PoiType.Amenity, "post_office", PoiGroup.PublicServices },
        { PoiType.Amenity, "community_centre", PoiGroup.PublicServices },
        { PoiType.Amenity, "parking_space", PoiGroup.Parking },
        { PoiType.Amenity, "motorcycle_parking", PoiGroup.Parking },
        { PoiType.Amenity, "bank", PoiGroup.Other },
        { PoiType.Office, "company", PoiGroup.Other },
        { PoiType.Office, "parking", PoiGroup.Other }, // the subtype counts only for amenities
        { PoiType.Leisure, "cafe", PoiGroup.Other },
    };

    [Theory]
    [MemberData(nameof(FullPackRoadClasses))]
    public void Ac7_EveryFullPackRoadClass_MapsToItsTableGroup(string roadClass, RoadGroup expected)
    {
        // The table's group, whatever the edge's drivable flag says.
        Assert.Equal(expected, MinimapStyle.RoadGroupOf(roadClass, drivable: true));
        Assert.Equal(expected, MinimapStyle.RoadGroupOf(roadClass, drivable: false));
        Assert.Equal(expected, MinimapStyle.RoadGroupOf(MinimapFixtures.Edge("e", roadClass, [(0, 0), (1, 0)])));
    }

    [Fact]
    public void Ac7_Secondary_IsAMainRoad()
    {
        Assert.Equal(RoadGroup.MainRoad, MinimapStyle.RoadGroupOf("secondary", drivable: true));
    }

    [Theory]
    [InlineData("motorway")]
    [InlineData("bus_guideway")]
    [InlineData("")]
    public void Ac7_UnknownDrivableClass_IsMinorDrivable(string roadClass)
    {
        Assert.Equal(RoadGroup.MinorDrivable, MinimapStyle.RoadGroupOf(roadClass, drivable: true));
    }

    [Theory]
    [InlineData("bridleway")]
    [InlineData("steps")]
    public void Ac7_UnknownNonDrivableClass_IsPath(string roadClass)
    {
        Assert.Equal(RoadGroup.Path, MinimapStyle.RoadGroupOf(roadClass, drivable: false));
        Assert.Equal(RoadGroup.Path, MinimapStyle.RoadGroupOf(MinimapFixtures.Edge("e", roadClass, [(0, 0), (1, 0)], drivable: false)));
    }

    [Theory]
    [MemberData(nameof(FullPackPois))]
    [MemberData(nameof(SpecTable))]
    public void Ac7_EveryPointOfInterest_MapsToItsTableGroup(PoiType type, string subtype, PoiGroup expected)
    {
        Assert.Equal(expected, MinimapStyle.PoiGroupOf(type, subtype));
        Assert.Equal(expected, MinimapStyle.PoiGroupOf(MinimapFixtures.Poi("osm:node/1", type, subtype, 0, 0)));
    }

    [Fact]
    public void Ac7_TheCriterionsNamedPois_MapAsWorded()
    {
        Assert.Equal(PoiGroup.Other, MinimapStyle.PoiGroupOf(PoiType.Amenity, "fuel"));
        Assert.Equal(PoiGroup.Other, MinimapStyle.PoiGroupOf(PoiType.Amenity, "cinema"));
        Assert.Equal(PoiGroup.Shop, MinimapStyle.PoiGroupOf(PoiType.Amenity, "marketplace"));
        Assert.Equal(PoiGroup.PublicServices, MinimapStyle.PoiGroupOf(PoiType.Amenity, "grave_yard"));
        Assert.Equal(PoiGroup.Parking, MinimapStyle.PoiGroupOf(PoiType.Amenity, "parking"));
    }

    [Fact]
    public void Ac7_Paths_AreThinnerAndFainterThanEveryDrivableGroup()
    {
        var path = MinimapStyle.StrokeOf(RoadGroup.Path);
        foreach (var group in new[] { RoadGroup.MainRoad, RoadGroup.Road, RoadGroup.MinorDrivable })
        {
            var drivable = MinimapStyle.StrokeOf(group);
            Assert.True(path.Width < drivable.Width, $"path width {path.Width} must be below {group}'s {drivable.Width}");
            Assert.True(path.Opacity < drivable.Opacity, $"path opacity {path.Opacity} must be below {group}'s {drivable.Opacity}");
        }
    }

    [Fact]
    public void Strokes_AreValidAndHaveDistinctDrawOrders()
    {
        var strokes = Enum.GetValues<RoadGroup>().Select(MinimapStyle.StrokeOf).ToList();

        Assert.All(strokes, stroke =>
        {
            Assert.True(stroke.Width > 0);
            Assert.InRange(stroke.Opacity, 0, 1);
        });
        Assert.Equal(strokes.Count, strokes.Select(stroke => stroke.DrawOrder).Distinct().Count());

        // Main roads lie on top of the others where they cross.
        Assert.Equal(strokes.Max(stroke => stroke.DrawOrder), MinimapStyle.StrokeOf(RoadGroup.MainRoad).DrawOrder);
    }

    [Fact]
    public void EveryPoiGroup_HasItsOwnSymbol()
    {
        var symbols = Enum.GetValues<PoiGroup>().Select(MinimapStyle.SymbolOf).ToList();

        Assert.Equal(symbols.Count, symbols.Distinct().Count());
        Assert.Equal(PoiSymbol.Square, MinimapStyle.SymbolOf(PoiGroup.Shop));
        Assert.Equal(PoiSymbol.Circle, MinimapStyle.SymbolOf(PoiGroup.FoodAndDrink));
        Assert.Equal(PoiSymbol.Diamond, MinimapStyle.SymbolOf(PoiGroup.PublicServices));
        Assert.Equal(PoiSymbol.ParkingSign, MinimapStyle.SymbolOf(PoiGroup.Parking));
        Assert.Equal(PoiSymbol.Dot, MinimapStyle.SymbolOf(PoiGroup.Other));
    }

    [Fact]
    public void LabelPriority_IsMainRoadsFirst()
    {
        Assert.Equal(
            [RoadGroup.MainRoad, RoadGroup.Road, RoadGroup.MinorDrivable, RoadGroup.Path],
            Enum.GetValues<RoadGroup>().Order());
    }
}
