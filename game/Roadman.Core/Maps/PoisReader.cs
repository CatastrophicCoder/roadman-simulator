using System.Text.Json;

namespace Roadman.Core.Maps;

/// <summary>
/// Parses and checks a pack's <c>pois.json</c> (schema version 1, <c>docs/specs/place-information.md</c>).
/// Any problem throws a <see cref="MapPackException"/> that names the file and the field, like
/// <see cref="RoadGraphReader"/>: another schema version, a missing, mistyped or unknown field (nullable keys must
/// still be present), an unknown <c>type</c>, <c>geometry</c> or <c>address_source</c>, a duplicate id, an outline on
/// a point or none (or an empty one) on an area, a polygon without rings, a ring that isn't closed or has fewer than
/// 4 points, <c>contains_building_ids</c> on a point, an <c>address_source</c> that doesn't agree with the address or
/// <c>building_id</c>, and non-finite numbers. Links to <c>buildings.json</c> are checked by <see cref="PlaceIndex.Create"/>.
/// </summary>
public static class PoisReader
{
    public const int SupportedSchemaVersion = 1;

    private static readonly string[] RootFields = ["schema_version", "map_id", "area", "pois"];

    private static readonly string[] PoiFields =
    [
        "id", "name", "name_sv", "ref", "type", "subtype", "other_tags", "geometry", "position", "outline",
        "address", "address_source", "building_id", "contains_building_ids",
    ];

    private static readonly Dictionary<string, PoiType> Types = new(StringComparer.Ordinal)
    {
        ["shop"] = PoiType.Shop,
        ["amenity"] = PoiType.Amenity,
        ["office"] = PoiType.Office,
        ["tourism"] = PoiType.Tourism,
        ["leisure"] = PoiType.Leisure,
    };

    private static readonly Dictionary<string, PoiGeometry> Geometries = new(StringComparer.Ordinal)
    {
        ["point"] = PoiGeometry.Point,
        ["area"] = PoiGeometry.Area,
    };

    private static readonly Dictionary<string, AddressSource> AddressSources = new(StringComparer.Ordinal)
    {
        ["osm"] = AddressSource.Osm,
        ["building"] = AddressSource.Building,
    };

    /// <param name="json">The file's contents.</param>
    /// <param name="source">Name used in error messages, usually the file path.</param>
    public static Pois Parse(string json, string source = MapPack.PoisFile)
    {
        using var document = PackJson.Parse(json, source);
        var root = PackJson.Root(document, source);
        PackJson.RejectUnknown(root, source, "", RootFields);

        var version = PackJson.Int(root, "schema_version", source, "");
        if (version != SupportedSchemaVersion)
        {
            throw new MapPackException(
                $"{source}: schema_version {version} is not supported; this game reads version {SupportedSchemaVersion}");
        }

        var mapId = PackJson.String(root, "map_id", source, "");
        if (!MapPack.IsValidMapId(mapId))
        {
            throw PackJson.Error(source, "map_id", $"'{mapId}' is not a lowercase slug");
        }

        var area = PackJson.String(root, "area", source, "");
        if (area is not ("full" or "test"))
        {
            throw PackJson.Error(source, "area", $"must be 'full' or 'test', not '{area}'");
        }

        var pois = new List<Poi>();
        var ids = new HashSet<string>(StringComparer.Ordinal);
        var index = 0;
        foreach (var element in PackJson.Array(root, "pois", source, "").EnumerateArray())
        {
            var path = $"pois[{index}]";
            PackJson.RequireObject(element, source, path);
            var poi = ParsePoi(element, source, path);
            if (!ids.Add(poi.Id))
            {
                throw PackJson.Error(source, $"{path}.id", $"POI id '{poi.Id}' appears more than once");
            }

            pois.Add(poi);
            index++;
        }

        return new Pois(version, mapId, area, pois);
    }

    private static Poi ParsePoi(JsonElement element, string source, string path)
    {
        PackJson.RejectUnknown(element, source, path, PoiFields);
        var id = PackJson.NonEmptyString(element, "id", source, path);
        var names = PackJson.Names(element, source, path);
        var type = PackJson.Choice(element, "type", Types, source, path);
        var subtype = PackJson.NonEmptyString(element, "subtype", source, path);
        var otherTags = PackJson.Strings(element, "other_tags", source, path);
        var geometry = PackJson.Choice(element, "geometry", Geometries, source, path);
        var position = PackJson.Numbers(element, "position", 3, source, path);

        IReadOnlyList<PoiPolygon>? outline = null;
        if (PackJson.RequiredNullable(element, "outline", source, path, out var outlineElement))
        {
            if (geometry == PoiGeometry.Point)
            {
                throw PackJson.Error(source, $"{path}.outline", $"must be null for a point (POI '{id}')");
            }

            outline = Outline(outlineElement, source, $"{path}.outline");
        }
        else if (geometry == PoiGeometry.Area)
        {
            throw PackJson.Error(source, $"{path}.outline", $"is null, but POI '{id}' is an area and needs one");
        }

        var address = PackJson.Address(element, source, path);
        var addressSource = PackJson.NullableChoice(element, "address_source", AddressSources, source, path);
        if (address.HasValue != (addressSource is not null))
        {
            throw PackJson.Error(source, $"{path}.address_source", "must be null exactly when the address has no value");
        }

        var buildingId = PackJson.NullableString(element, "building_id", source, path);
        if (buildingId is { Length: 0 })
        {
            throw PackJson.Error(source, $"{path}.building_id", "must not be empty");
        }

        if (addressSource == AddressSource.Building && buildingId is null)
        {
            throw PackJson.Error(source, $"{path}.address_source", $"is 'building', but POI '{id}' has no building_id");
        }

        var contains = PackJson.Strings(element, "contains_building_ids", source, path);
        if (geometry == PoiGeometry.Point && contains.Count > 0)
        {
            throw PackJson.Error(source, $"{path}.contains_building_ids", $"must be empty for a point (POI '{id}')");
        }

        return new Poi(
            id, names, type, subtype, otherTags, geometry,
            new LocalPoint(position[0], position[1], position[2]),
            outline, address, addressSource, buildingId, contains);
    }

    private static List<PoiPolygon> Outline(JsonElement outline, string source, string path)
    {
        if (outline.ValueKind != JsonValueKind.Array)
        {
            throw PackJson.Error(source, path, "must be a list of polygons or null");
        }

        if (outline.GetArrayLength() == 0)
        {
            throw PackJson.Error(source, path, "must have at least one polygon");
        }

        var polygons = new List<PoiPolygon>(outline.GetArrayLength());
        var i = 0;
        foreach (var polygon in outline.EnumerateArray())
        {
            var polygonPath = $"{path}[{i}]";
            if (polygon.ValueKind != JsonValueKind.Array)
            {
                throw PackJson.Error(source, polygonPath, "must be a list of rings");
            }

            if (polygon.GetArrayLength() == 0)
            {
                throw PackJson.Error(source, polygonPath, "must have at least an exterior ring");
            }

            var rings = new List<IReadOnlyList<PlanarPoint>>(polygon.GetArrayLength());
            var j = 0;
            foreach (var ring in polygon.EnumerateArray())
            {
                rings.Add(Ring(ring, source, $"{polygonPath}[{j}]"));
                j++;
            }

            polygons.Add(new PoiPolygon(rings[0], rings.Skip(1).ToList()));
            i++;
        }

        return polygons;
    }

    private static List<PlanarPoint> Ring(JsonElement ring, string source, string path)
    {
        if (ring.ValueKind != JsonValueKind.Array)
        {
            throw PackJson.Error(source, path, "must be a list of [x, z] points");
        }

        if (ring.GetArrayLength() < 4)
        {
            throw PackJson.Error(source, path, $"must have at least 4 points, has {ring.GetArrayLength()}");
        }

        var points = new List<PlanarPoint>(ring.GetArrayLength());
        var i = 0;
        foreach (var item in ring.EnumerateArray())
        {
            var pointPath = $"{path}[{i}]";
            if (item.ValueKind != JsonValueKind.Array || item.GetArrayLength() != 2)
            {
                throw PackJson.Error(source, pointPath, "must be [x, z]");
            }

            points.Add(new PlanarPoint(
                PackJson.Number(item[0], source, $"{pointPath}[0]"),
                PackJson.Number(item[1], source, $"{pointPath}[1]")));
            i++;
        }

        if (points[0] != points[^1])
        {
            throw PackJson.Error(source, path, "is not closed (the last point must repeat the first)");
        }

        return points;
    }
}
