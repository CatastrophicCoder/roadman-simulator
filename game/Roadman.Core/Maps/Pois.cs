namespace Roadman.Core.Maps;

/// <summary>The POI's main key (<c>type</c> in <c>pois.json</c>): the first of these OSM keys the feature has.</summary>
public enum PoiType
{
    /// <summary><c>shop</c></summary>
    Shop,

    /// <summary><c>amenity</c></summary>
    Amenity,

    /// <summary><c>office</c></summary>
    Office,

    /// <summary><c>tourism</c></summary>
    Tourism,

    /// <summary><c>leisure</c></summary>
    Leisure,
}

/// <summary>A POI's geometry (<c>geometry</c>).</summary>
public enum PoiGeometry
{
    /// <summary>An OSM node (<c>point</c>); no outline.</summary>
    Point,

    /// <summary>An OSM way or relation (<c>area</c>); has an outline.</summary>
    Area,
}

/// <summary>A point on the ground plane, in Godot local metres (x east, z south).</summary>
public readonly record struct PlanarPoint(double X, double Z);

/// <summary>
/// One polygon of a POI outline: an exterior ring and its holes, each closed (first point repeated, at least 4 points).
/// </summary>
public sealed record PoiPolygon(IReadOnlyList<PlanarPoint> Exterior, IReadOnlyList<IReadOnlyList<PlanarPoint>> Holes)
{
    /// <summary>Area in square metres: the exterior's minus the holes'.</summary>
    public double Area => Math.Abs(SignedArea(Exterior)) - Holes.Sum(hole => Math.Abs(SignedArea(hole)));

    /// <summary>
    /// True when (<paramref name="x"/>, <paramref name="z"/>) is inside the exterior and outside every hole
    /// (even-odd rule; a point exactly on a ring may go either way).
    /// </summary>
    public bool Contains(double x, double z) =>
        RingContains(Exterior, x, z) && !Holes.Any(hole => RingContains(hole, x, z));

    private static double SignedArea(IReadOnlyList<PlanarPoint> ring)
    {
        var sum = 0.0;
        for (var i = 0; i < ring.Count - 1; i++)
        {
            sum += (ring[i].X * ring[i + 1].Z) - (ring[i + 1].X * ring[i].Z);
        }

        return sum / 2;
    }

    private static bool RingContains(IReadOnlyList<PlanarPoint> ring, double x, double z)
    {
        var inside = false;
        for (var i = 0; i < ring.Count - 1; i++)
        {
            var a = ring[i];
            var b = ring[i + 1];
            if ((a.Z > z) != (b.Z > z) && x < a.X + ((z - a.Z) * (b.X - a.X) / (b.Z - a.Z)))
            {
                inside = !inside;
            }
        }

        return inside;
    }
}

/// <summary>One entry of <c>pois.json</c>: a shop, amenity, office, tourism or leisure feature, named or not.</summary>
/// <param name="Id"><c>osm:node/&lt;id&gt;</c>, <c>osm:way/&lt;id&gt;</c> or <c>osm:relation/&lt;id&gt;</c>.</param>
/// <param name="Names">Name fields; all null for an unnamed POI.</param>
/// <param name="Type">The first of the POI keys the feature has.</param>
/// <param name="Subtype">That key's value, for example <c>parking</c>.</param>
/// <param name="OtherTags">Any further POI keys as sorted <c>key=value</c> strings.</param>
/// <param name="Geometry">Point (node) or area (way or relation).</param>
/// <param name="Position">Godot local metres: the node, or the area's representative point; y is the terrain height.</param>
/// <param name="Outline">Areas: the polygons, clipped to the build area. Null for points.</param>
/// <param name="Address">The address shown for the POI.</param>
/// <param name="AddressSource">The POI's own tags, its building's address, or null when it has none.</param>
/// <param name="BuildingId">The building it is on or in.</param>
/// <param name="ContainsBuildingIds">Areas: the buildings inside it. Empty for points.</param>
public sealed record Poi(
    string Id,
    FeatureNames Names,
    PoiType Type,
    string Subtype,
    IReadOnlyList<string> OtherTags,
    PoiGeometry Geometry,
    LocalPoint Position,
    IReadOnlyList<PoiPolygon>? Outline,
    Address Address,
    AddressSource? AddressSource,
    string? BuildingId,
    IReadOnlyList<string> ContainsBuildingIds)
{
    /// <summary>The outline's area in square metres; 0 for points.</summary>
    public double Area => Outline?.Sum(polygon => polygon.Area) ?? 0;

    /// <summary>True when the outline contains (<paramref name="x"/>, <paramref name="z"/>); always false for points.</summary>
    public bool Contains(double x, double z) => Outline?.Any(polygon => polygon.Contains(x, z)) ?? false;
}

/// <summary>
/// The pack's <c>pois.json</c>. Read it with <see cref="PoisReader"/>, which guarantees unique ids and well-formed
/// outlines; join it with the buildings through <see cref="PlaceIndex.Create"/>.
/// </summary>
/// <param name="Area"><c>full</c> or <c>test</c>.</param>
/// <param name="Items">Every POI, in file order.</param>
public sealed record Pois(int SchemaVersion, string MapId, string Area, IReadOnlyList<Poi> Items);
