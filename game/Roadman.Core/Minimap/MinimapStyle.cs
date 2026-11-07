using Roadman.Core.Maps;

namespace Roadman.Core.Minimap;

/// <summary>
/// A road's class group on the minimap (<c>docs/specs/minimap.md</c>, "What the player sees", layer 4). The enum order
/// is the label priority: main roads first.
/// </summary>
public enum RoadGroup
{
    /// <summary><c>primary</c>, <c>secondary</c>.</summary>
    MainRoad,

    /// <summary><c>tertiary</c>, <c>unclassified</c>, <c>residential</c>, <c>living_street</c>.</summary>
    Road,

    /// <summary><c>service</c>, <c>driveway</c>, <c>parking_aisle</c>, <c>track</c>, and unknown drivable classes.</summary>
    MinorDrivable,

    /// <summary><c>cycleway</c>, <c>footway</c>, <c>path</c>, <c>pedestrian</c>, and unknown non-drivable classes.</summary>
    Path,
}

/// <summary>A point of interest's symbol group on the minimap (<c>docs/specs/minimap.md</c>, "Points of interest").</summary>
public enum PoiGroup
{
    /// <summary>Type <c>shop</c>; amenity <c>marketplace</c>. Drawn as a square.</summary>
    Shop,

    /// <summary>Cafés, restaurants and the like. Drawn as a circle.</summary>
    FoodAndDrink,

    /// <summary>Schools, libraries, health care, emergency services, worship and the like. Drawn as a diamond.</summary>
    PublicServices,

    /// <summary>Parking. Drawn as a rounded square with "P".</summary>
    Parking,

    /// <summary>Everything else. Drawn as a small dot.</summary>
    Other,
}

/// <summary>The generic shape of a <see cref="PoiGroup"/>.</summary>
public enum PoiSymbol
{
    Square,
    Circle,
    Diamond,
    ParkingSign,
    Dot,
}

/// <summary>
/// Default stroke values of a road group. Widths are in panel base units; colours live in Godot's <c>UiTheme</c>.
/// </summary>
/// <param name="Width">Stroke width in base units, above 0.</param>
/// <param name="Opacity">Stroke opacity, 0 to 1.</param>
/// <param name="DrawOrder">Lower is drawn first (further back); bridges are drawn after every non-bridge road.</param>
public readonly record struct RoadStroke(double Width, double Opacity, int DrawOrder);

/// <summary>
/// The minimap's style tables (<c>docs/specs/minimap.md</c>): road class groups, point-of-interest symbol groups and
/// the default stroke widths, opacities and draw order. Engine-free; the Godot HUD takes its colours from
/// <c>UiTheme</c> and may override the widths with its exports.
/// </summary>
public static class MinimapStyle
{
    /// <summary>The text drawn in the parking symbol.</summary>
    public const string ParkingSymbolText = "P";

    private static readonly Dictionary<string, RoadGroup> RoadClasses = new(StringComparer.Ordinal)
    {
        ["primary"] = RoadGroup.MainRoad,
        ["secondary"] = RoadGroup.MainRoad,
        ["tertiary"] = RoadGroup.Road,
        ["unclassified"] = RoadGroup.Road,
        ["residential"] = RoadGroup.Road,
        ["living_street"] = RoadGroup.Road,
        ["service"] = RoadGroup.MinorDrivable,
        ["driveway"] = RoadGroup.MinorDrivable,
        ["parking_aisle"] = RoadGroup.MinorDrivable,
        ["track"] = RoadGroup.MinorDrivable,
        ["cycleway"] = RoadGroup.Path,
        ["footway"] = RoadGroup.Path,
        ["path"] = RoadGroup.Path,
        ["pedestrian"] = RoadGroup.Path,
    };

    private static readonly Dictionary<string, PoiGroup> AmenityGroups = new(StringComparer.Ordinal)
    {
        ["marketplace"] = PoiGroup.Shop,

        ["cafe"] = PoiGroup.FoodAndDrink,
        ["restaurant"] = PoiGroup.FoodAndDrink,
        ["fast_food"] = PoiGroup.FoodAndDrink,
        ["bar"] = PoiGroup.FoodAndDrink,
        ["pub"] = PoiGroup.FoodAndDrink,
        ["ice_cream"] = PoiGroup.FoodAndDrink,
        ["food_court"] = PoiGroup.FoodAndDrink,
        ["biergarten"] = PoiGroup.FoodAndDrink,

        ["school"] = PoiGroup.PublicServices,
        ["kindergarten"] = PoiGroup.PublicServices,
        ["college"] = PoiGroup.PublicServices,
        ["university"] = PoiGroup.PublicServices,
        ["library"] = PoiGroup.PublicServices,
        ["pharmacy"] = PoiGroup.PublicServices,
        ["hospital"] = PoiGroup.PublicServices,
        ["clinic"] = PoiGroup.PublicServices,
        ["doctors"] = PoiGroup.PublicServices,
        ["dentist"] = PoiGroup.PublicServices,
        ["fire_station"] = PoiGroup.PublicServices,
        ["police"] = PoiGroup.PublicServices,
        ["townhall"] = PoiGroup.PublicServices,
        ["post_office"] = PoiGroup.PublicServices,
        ["community_centre"] = PoiGroup.PublicServices,
        ["place_of_worship"] = PoiGroup.PublicServices,
        ["grave_yard"] = PoiGroup.PublicServices,

        ["parking"] = PoiGroup.Parking,
        ["parking_space"] = PoiGroup.Parking,
        ["motorcycle_parking"] = PoiGroup.Parking,
    };

    /// <summary>Every road class the table names, with its group (unknown classes go by <see cref="RoadGroupOf(string, bool)"/>).</summary>
    public static IReadOnlyDictionary<string, RoadGroup> KnownRoadClasses => RoadClasses;

    /// <summary>Every amenity subtype the table names, with its group; other amenities are <see cref="PoiGroup.Other"/>.</summary>
    public static IReadOnlyDictionary<string, PoiGroup> KnownAmenities => AmenityGroups;

    /// <summary>
    /// The class group of a road: the table's group for a listed class (whatever <paramref name="drivable"/> says),
    /// else <see cref="RoadGroup.MinorDrivable"/> for a drivable edge and <see cref="RoadGroup.Path"/> for the others.
    /// </summary>
    public static RoadGroup RoadGroupOf(string roadClass, bool drivable)
    {
        ArgumentNullException.ThrowIfNull(roadClass);
        return RoadClasses.TryGetValue(roadClass, out var group)
            ? group
            : drivable ? RoadGroup.MinorDrivable : RoadGroup.Path;
    }

    /// <inheritdoc cref="RoadGroupOf(string, bool)"/>
    public static RoadGroup RoadGroupOf(RoadEdge edge)
    {
        ArgumentNullException.ThrowIfNull(edge);
        return RoadGroupOf(edge.Class, edge.Drivable);
    }

    /// <summary>
    /// The symbol group of a point of interest: type <c>shop</c> is <see cref="PoiGroup.Shop"/>; an amenity goes by the
    /// table (<see cref="KnownAmenities"/>), else <see cref="PoiGroup.Other"/>; every other type is
    /// <see cref="PoiGroup.Other"/>.
    /// </summary>
    public static PoiGroup PoiGroupOf(PoiType type, string subtype)
    {
        ArgumentNullException.ThrowIfNull(subtype);
        return type switch
        {
            PoiType.Shop => PoiGroup.Shop,
            PoiType.Amenity => AmenityGroups.TryGetValue(subtype, out var group) ? group : PoiGroup.Other,
            _ => PoiGroup.Other,
        };
    }

    /// <inheritdoc cref="PoiGroupOf(PoiType, string)"/>
    public static PoiGroup PoiGroupOf(Poi poi)
    {
        ArgumentNullException.ThrowIfNull(poi);
        return PoiGroupOf(poi.Type, poi.Subtype);
    }

    /// <summary>The generic shape of a symbol group.</summary>
    public static PoiSymbol SymbolOf(PoiGroup group) =>
        group switch
        {
            PoiGroup.Shop => PoiSymbol.Square,
            PoiGroup.FoodAndDrink => PoiSymbol.Circle,
            PoiGroup.PublicServices => PoiSymbol.Diamond,
            PoiGroup.Parking => PoiSymbol.ParkingSign,
            PoiGroup.Other => PoiSymbol.Dot,
            _ => throw new ArgumentOutOfRangeException(nameof(group), group, "not a point-of-interest group"),
        };

    /// <summary>
    /// The default stroke of a road group. Paths are thinner and fainter than every drivable group; the more important
    /// groups are drawn later, so they lie on top where roads cross.
    /// </summary>
    public static RoadStroke StrokeOf(RoadGroup group) =>
        group switch
        {
            RoadGroup.MainRoad => new RoadStroke(4.0, 1.0, 3),
            RoadGroup.Road => new RoadStroke(3.0, 1.0, 2),
            RoadGroup.MinorDrivable => new RoadStroke(2.0, 0.9, 1),
            RoadGroup.Path => new RoadStroke(1.25, 0.55, 0),
            _ => throw new ArgumentOutOfRangeException(nameof(group), group, "not a road group"),
        };
}
